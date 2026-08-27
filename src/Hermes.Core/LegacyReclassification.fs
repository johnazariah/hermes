namespace Hermes.Core

open System
open System.IO
open System.Security.Cryptography
open System.Threading.Tasks

/// Bounded detection and metadata-only repair of stale legacy saved paths.
[<RequireQualifiedAccess>]
module LegacyReclassification =

    type ScanBounds =
        private
            { MaxDocuments: int
              MaxFiles: int }

    type Evidence =
        | UniqueShaMatch of savedPath: string
        | AmbiguousShaMatches of savedPaths: string list
        | MissingShaMatch
        | ShaMismatch of actualSha256: string
        | InconclusiveScan of possibleMatches: string list

    type Finding =
        { DocumentId: int64
          SavedPath: string
          Sha256: string
          Evidence: Evidence }

    type ScanReport =
        { DocumentsScanned: int
          FilesHashed: int
          DocumentsTruncated: bool
          FilesTruncated: bool
          Findings: Finding list }

    type RepairFailure =
        | CandidateDisappeared of path: string
        | CandidateChanged of actualSha256: string
        | DocumentChanged

    type RepairDisposition =
        | Repaired of savedPath: string
        | Skipped of evidence: Evidence
        | Failed of failure: RepairFailure

    type RepairOutcome =
        { DocumentId: int64
          Disposition: RepairDisposition }

    type RepairReport =
        { Scan: ScanReport
          Outcomes: RepairOutcome list }

    type private ArchiveIndex =
        { ByPath: Map<string, string>
          ByHash: Map<string, string list>
          FilesHashed: int
          Truncated: bool }

    let createBounds maxDocuments maxFiles =
        if maxDocuments < 1 || maxDocuments > 1000 then
            Error "maxDocuments must be between 1 and 1000"
        elif maxFiles < 1 || maxFiles > 10000 then
            Error "maxFiles must be between 1 and 10000"
        else
            Ok
                { MaxDocuments = maxDocuments
                  MaxFiles = maxFiles }

    let private fullPath (archiveDir: string) (savedPath: string) =
        if Path.IsPathRooted(savedPath) then savedPath
        else Path.Combine(archiveDir, savedPath)

    let private canonicalPath (path: string) =
        Path.GetFullPath(path).ToUpperInvariant()

    let private generatedArtifactNames =
        [ ".hermes.json"
          "thread.comprehension.json"
          "db.sqlite"
          "db.sqlite-wal"
          "db.sqlite-shm"
          "db.sqlite-journal" ]

    let private isGeneratedMarkdownSidecar (fileName: string) =
        let sourceName = Path.GetFileNameWithoutExtension(fileName)
        fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        && Path.HasExtension(sourceName)

    let private isGeneratedArtifact (path: string) =
        let fileName = Path.GetFileName(path) |> Option.ofObj |> Option.defaultValue ""
        let exactMatch =
            generatedArtifactNames
            |> List.exists (fun candidate ->
                String.Equals(fileName, candidate, StringComparison.OrdinalIgnoreCase))

        exactMatch
        || isGeneratedMarkdownSidecar fileName
        || fileName.EndsWith(".extracted.md", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)

    let private orderedFiles
        (fs: Algebra.FileSystem)
        (directory: string) =
        fs.getFiles directory "*"
        |> Array.filter (isGeneratedArtifact >> not)
        |> Array.sort
        |> Array.toList

    let private orderedDirectories
        (fs: Algebra.FileSystem)
        (directory: string) =
        fs.getDirectories directory
        |> Array.sort
        |> Array.toList

    let private collectFiles
        (fs: Algebra.FileSystem)
        (archiveDir: string)
        (maxFiles: int) =
        let rec walk pending collected =
            match pending with
            | [] -> List.rev collected, false
            | directory :: rest ->
                let discovered = orderedFiles fs directory
                let available = maxFiles - List.length collected
                let selected = discovered |> List.truncate available
                let updated = (List.rev selected) @ collected

                if discovered.Length > available then
                    List.rev updated, true
                else
                    let directories = orderedDirectories fs directory
                    let pending = rest @ directories
                    if List.length updated = maxFiles && not pending.IsEmpty then
                        List.rev updated, true
                    else
                        walk pending updated

        walk [ archiveDir ] []

    let private sha256
        (fs: Algebra.FileSystem)
        (path: string)
        : Task<string> =
        task {
            let! bytes = fs.readAllBytes path
            let hash: byte array = SHA256.HashData(bytes)
            return Convert.ToHexString(hash).ToLowerInvariant()
        }

    let private hashFiles fs files =
        let rec loop hashed = function
            | [] -> Task.FromResult(List.rev hashed)
            | path :: tail ->
                task {
                    let! hash = sha256 fs path
                    return! loop ((path, hash) :: hashed) tail
                }

        loop [] files

    let private addHash hash path index =
        let paths =
            index
            |> Map.tryFind hash
            |> Option.defaultValue []
        index |> Map.add hash (path :: paths)

    let private toArchiveIndex truncated hashes =
        let byPath, byHash =
            hashes
            |> List.fold (fun (paths, hashesByValue) (path, hash) ->
                paths |> Map.add (canonicalPath path) hash,
                hashesByValue |> addHash hash path) (Map.empty, Map.empty)

        { ByPath = byPath
          ByHash = byHash
          FilesHashed = hashes.Length
          Truncated = truncated }

    let private indexArchive fs archiveDir maxFiles =
        task {
            let files, truncated = collectFiles fs archiveDir maxFiles
            let! hashes = hashFiles fs files
            return toArchiveIndex truncated hashes
        }

    let private loadDocuments
        (db: Algebra.Database)
        (bounds: ScanBounds) =
        db.execReader
            """SELECT id, saved_path, sha256
               FROM documents
               ORDER BY id
               LIMIT @limit"""
            [ "@limit", Database.boxVal (int64 (bounds.MaxDocuments + 1)) ]

    let private relativeCandidates
        (archiveDir: string)
        (expected: string)
        (index: ArchiveIndex) =
        index.ByHash
        |> Map.tryFind expected
        |> Option.defaultValue []
        |> List.map (fun path -> Path.GetRelativePath(archiveDir, path))
        |> List.sort

    let private missingPathEvidence truncated candidates =
        match candidates, truncated with
        | _, true -> InconclusiveScan candidates
        | [ candidate ], false -> UniqueShaMatch candidate
        | [], false -> MissingShaMatch
        | values, false -> AmbiguousShaMatches values

    let private evidence
        (fs: Algebra.FileSystem)
        (archiveDir: string)
        (savedPath: string)
        (expected: string)
        (index: ArchiveIndex) =
        let current = fullPath archiveDir savedPath
        let currentHash =
            index.ByPath |> Map.tryFind (canonicalPath current)
        let candidates = relativeCandidates archiveDir expected index

        match currentHash with
        | Some actual when actual = expected -> None
        | Some actual -> Some (ShaMismatch actual)
        | None when fs.fileExists current ->
            Some (InconclusiveScan candidates)
        | None ->
            Some (missingPathEvidence index.Truncated candidates)

    let private toFinding fs archiveDir index row =
        let reader = Prelude.RowReader(row)
        let documentId = reader.Int64 "id" 0L
        let savedPath = reader.String "saved_path" ""
        let expected =
            reader.String "sha256" ""
            |> fun value -> value.ToLowerInvariant()

        evidence fs archiveDir savedPath expected index
        |> Option.map (fun proof ->
            { DocumentId = documentId
              SavedPath = savedPath
              Sha256 = expected
              Evidence = proof })

    let detect db fs archiveDir bounds : Task<ScanReport> =
        task {
            let! rows = loadDocuments db bounds
            let sampled = rows |> List.truncate bounds.MaxDocuments
            let! index = indexArchive fs archiveDir bounds.MaxFiles
            let findings =
                sampled
                |> List.choose (toFinding fs archiveDir index)

            return
                { DocumentsScanned = sampled.Length
                  FilesHashed = index.FilesHashed
                  DocumentsTruncated = rows.Length > bounds.MaxDocuments
                  FilesTruncated = index.Truncated
                  Findings = findings }
        }

    let private updateSavedPath
        (db: Algebra.Database)
        (finding: Finding)
        (candidate: string) =
        task {
            let! affected =
                db.execNonQuery
                    """UPDATE documents
                       SET saved_path = @newPath
                       WHERE id = @id
                         AND saved_path = @oldPath
                         AND lower(sha256) = @sha256"""
                    [ "@newPath", Database.boxVal candidate
                      "@id", Database.boxVal finding.DocumentId
                      "@oldPath", Database.boxVal finding.SavedPath
                      "@sha256", Database.boxVal finding.Sha256 ]

            return
                if affected = 1 then Repaired candidate
                else Failed DocumentChanged
        }

    let private repairUnique
        (db: Algebra.Database)
        (fs: Algebra.FileSystem)
        (archiveDir: string)
        (finding: Finding)
        (candidate: string) =
        task {
            let path = fullPath archiveDir candidate
            if not (fs.fileExists path) then
                return Failed (CandidateDisappeared path)
            else
                let! actual = sha256 fs path
                if actual <> finding.Sha256 then
                    return Failed (CandidateChanged actual)
                else
                    return! updateSavedPath db finding candidate
        }

    let private repairFinding db fs archiveDir finding =
        match finding.Evidence with
        | UniqueShaMatch candidate ->
            repairUnique db fs archiveDir finding candidate
        | evidence ->
            Task.FromResult(Skipped evidence)

    let private repairAll db fs archiveDir findings =
        let rec loop outcomes = function
            | [] -> Task.FromResult(List.rev outcomes)
            | finding :: tail ->
                task {
                    let! disposition =
                        repairFinding db fs archiveDir finding
                    let outcome =
                        { DocumentId = finding.DocumentId
                          Disposition = disposition }
                    return! loop (outcome :: outcomes) tail
                }

        loop [] findings

    let repair db fs archiveDir bounds : Task<RepairReport> =
        task {
            let! scan = detect db fs archiveDir bounds
            let! outcomes =
                repairAll db fs archiveDir scan.Findings
            return { Scan = scan; Outcomes = outcomes }
        }
