namespace Hermes.Core

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks

/// Bounded detection and metadata-only repair of stale legacy saved paths.
[<RequireQualifiedAccess>]
module LegacyReclassification =

    type ScanBounds =
        private
            { MaxDocuments: int
              MaxFiles: int }

    type RepairRunId = RepairRunId of string

    type SnapshotEpoch = SnapshotEpoch of int64

    type RunPhase =
        | ArchiveScan
        | DocumentScan

    type ArchiveContinuation =
        | ArchiveNotStarted
        | AfterArchiveFile of sortKey: string
        | ArchiveCompleted

    type DocumentContinuation =
        | BeforeFirstDocument
        | AfterDocument of documentId: int64

    type CandidatePath =
        { OwnershipKey: string
          SavedPath: string }

    type TargetCandidates =
        { Sha256: string
          Paths: CandidatePath list }

    type RunCursor =
        { RunId: RepairRunId
          ArchiveRootKey: string
          MaxDocuments: int
          MaxFiles: int
          Epoch: SnapshotEpoch
          Phase: RunPhase
          Archive: ArchiveContinuation
          Documents: DocumentContinuation
          Candidates: TargetCandidates list }

    type RunStability =
        | InProgress
        | StablePassCompleted
        | SnapshotChanged of restart: RunCursor

    type RunMode =
        | DryRun
        | Apply

    type PageProgress =
        { DocumentsScanned: int
          FilesHashed: int
          ArchiveComplete: bool }

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

    type PathOwnershipConflict =
        { CandidateSavedPath: string
          OwnerDocumentIds: int64 list }

    type RepairFailure =
        | CandidateDisappeared of path: string
        | CandidateChanged of actualSha256: string
        | DocumentChanged
        | DatabaseFailure of message: string

    type RepairDisposition =
        | Repaired of savedPath: string
        | Unchanged of savedPath: string
        | Skipped of evidence: Evidence
        | Conflict of conflict: PathOwnershipConflict
        | Failed of failure: RepairFailure

    type RepairOutcome =
        { DocumentId: int64
          Disposition: RepairDisposition }

    type RunPageResult =
        { Cursor: RunCursor option
          Progress: PageProgress
          Stability: RunStability
          Findings: Finding list
          Outcomes: RepairOutcome list }

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

    let private archiveRootKey archiveDir =
        Database.canonicalArchivePath archiveDir "."
        |> Result.map (fun canonical -> canonical.OwnershipKey)

    let private sortedDistinct (values: string list) : string list =
        values
        |> List.distinct
        |> List.sortWith (fun left right ->
            StringComparer.Ordinal.Compare(left, right))

    let private compareCandidatePaths left right =
        let keyComparison =
            StringComparer.Ordinal.Compare(
                left.OwnershipKey,
                right.OwnershipKey)

        if keyComparison <> 0 then keyComparison
        else StringComparer.Ordinal.Compare(left.SavedPath, right.SavedPath)

    let private normalizeCandidatePaths paths =
        paths
        |> List.groupBy (fun path -> path.OwnershipKey)
        |> List.map (fun (_, aliases) ->
            aliases
            |> List.sortWith compareCandidatePaths
            |> List.head)
        |> List.sortWith compareCandidatePaths
        |> List.truncate 2

    let private upsertCandidate
        (sha256: string)
        (path: CandidatePath)
        (candidates: TargetCandidates list)
        : TargetCandidates list =
        let existing =
            candidates
            |> List.tryFind (fun item -> item.Sha256 = sha256)
            |> Option.map (fun item -> item.Paths)
            |> Option.defaultValue []

        let updated =
            path :: existing
            |> normalizeCandidatePaths

        { Sha256 = sha256; Paths = updated }
        :: (candidates |> List.filter (fun item -> item.Sha256 <> sha256))
        |> List.sortBy (fun item -> item.Sha256)

    let addCandidate
        archiveDir
        sha256
        savedPath
        (candidates: TargetCandidates list)
        : Result<TargetCandidates list, string> =
        if String.IsNullOrWhiteSpace sha256 then
            Error "Candidate SHA-256 must not be empty"
        else
            Database.canonicalArchivePath archiveDir savedPath
            |> Result.map (fun canonical ->
                let path =
                    { OwnershipKey = canonical.OwnershipKey
                      SavedPath = savedPath }

                upsertCandidate
                    (sha256.ToLowerInvariant())
                    path
                    candidates)

    let candidatePaths
        (sha256: string)
        (candidates: TargetCandidates list)
        : string list =
        candidates
        |> List.tryFind (fun item ->
            String.Equals(
                item.Sha256,
                sha256,
                StringComparison.OrdinalIgnoreCase))
        |> Option.map (fun item ->
            item.Paths |> List.map (fun path -> path.SavedPath))
        |> Option.defaultValue []

    let createRunCursor
        (archiveDir: string)
        (bounds: ScanBounds)
        (epoch: int64)
        : Result<RunCursor, string> =
        if epoch < 0L then
            Error "Snapshot epoch must not be negative"
        else
            archiveRootKey archiveDir
            |> Result.map (fun rootKey ->
                { RunId = RepairRunId(Guid.NewGuid().ToString("N"))
                  ArchiveRootKey = rootKey
                  MaxDocuments = bounds.MaxDocuments
                  MaxFiles = bounds.MaxFiles
                  Epoch = SnapshotEpoch epoch
                  Phase = ArchiveScan
                  Archive = ArchiveNotStarted
                  Documents = BeforeFirstDocument
                  Candidates = [] })

    let private validateRunId (RepairRunId value) =
        match Guid.TryParseExact(value, "N") with
        | true, _ -> Ok()
        | false, _ -> Error "Repair run ID must be a non-empty N-format GUID"

    let private validateEpoch (SnapshotEpoch epoch) =
        if epoch < 0L then Error "Snapshot epoch must not be negative"
        else Ok()

    let private validateDocumentCursor = function
        | BeforeFirstDocument -> Ok()
        | AfterDocument documentId when documentId >= 0L -> Ok()
        | AfterDocument _ -> Error "Document cursor must not be negative"

    let private validateArchiveCursor = function
        | AfterArchiveFile key when String.IsNullOrWhiteSpace key ->
            Error "Archive cursor sort key must not be empty"
        | _ -> Ok()

    let private validatePhase cursor =
        match cursor.Phase, cursor.Archive with
        | ArchiveScan, ArchiveCompleted ->
            Error "Archive-scan phase cannot have a completed archive cursor"
        | DocumentScan, ArchiveCompleted -> Ok()
        | DocumentScan, _ ->
            Error "Document-scan phase requires a completed archive cursor"
        | ArchiveScan, _ -> Ok()

    let private validateCandidatePathOwnership
        (archiveDir: string)
        (path: CandidatePath)
        : Result<unit, string> =
        Database.canonicalArchivePath archiveDir path.SavedPath
        |> Result.mapError (fun error -> $"Candidate saved_path is invalid: {error}")
        |> Result.bind (fun canonical ->
            if canonical.OwnershipKey = path.OwnershipKey then Ok()
            else Error "Candidate ownership key does not match its saved_path")

    let private validateCandidateShape
        (item: TargetCandidates)
        : Result<unit, string> =
        let deterministic =
            item.Paths |> normalizeCandidatePaths

        if String.IsNullOrWhiteSpace item.Sha256 then
            Error "Candidate SHA-256 must not be empty"
        elif item.Paths.Length > 2 then
            Error "Candidate evidence must retain at most two paths per SHA-256"
        elif
            item.Paths
            |> List.exists (fun path ->
                String.IsNullOrWhiteSpace path.OwnershipKey
                || String.IsNullOrWhiteSpace path.SavedPath)
        then
            Error "Candidate canonical paths must not be empty"
        elif deterministic <> item.Paths then
            Error "Candidate canonical paths must be sorted and distinct"
        else
            Ok()

    let private validateCandidateOwnership
        (archiveDir: string)
        (item: TargetCandidates)
        : Result<unit, string> =
        item.Paths
        |> List.tryPick (fun path ->
            match validateCandidatePathOwnership archiveDir path with
            | Error error -> Some error
            | Ok() -> None)
        |> Option.map Error
        |> Option.defaultValue (Ok())

    let private validateCandidate
        (archiveDir: string)
        (item: TargetCandidates)
        : Result<unit, string> =
        validateCandidateShape item
        |> Result.bind (fun () -> validateCandidateOwnership archiveDir item)

    let private validateCandidates
        (archiveDir: string)
        (bounds: ScanBounds)
        (candidates: TargetCandidates list)
        : Result<unit, string> =
        let hashes = candidates |> List.map (fun item -> item.Sha256)

        if candidates.Length > bounds.MaxDocuments then
            Error "Candidate targets exceed the document-page bound"
        elif hashes.Length <> (hashes |> List.distinct).Length then
            Error "Candidate evidence contains duplicate SHA-256 targets"
        else
            candidates
            |> List.tryPick (fun item ->
                match validateCandidate archiveDir item with
                | Error error -> Some error
                | Ok () -> None)
            |> Option.map Error
            |> Option.defaultValue (Ok ())

    let private validateBounds
        (bounds: ScanBounds)
        (cursor: RunCursor)
        : Result<unit, string> =
        if cursor.MaxDocuments <> bounds.MaxDocuments then
            Error "Cursor maxDocuments does not match the requested bounds"
        elif cursor.MaxFiles <> bounds.MaxFiles then
            Error "Cursor maxFiles does not match the requested bounds"
        else
            Ok()

    let private validateArchiveRoot
        (archiveDir: string)
        (cursor: RunCursor)
        : Result<unit, string> =
        archiveRootKey archiveDir
        |> Result.bind (fun expected ->
            if expected = cursor.ArchiveRootKey then Ok()
            else Error "Cursor archive root does not match this archive")

    let validateRunCursor
        (archiveDir: string)
        (bounds: ScanBounds)
        (cursor: RunCursor)
        : Result<unit, string> =
        [ validateRunId cursor.RunId
          validateEpoch cursor.Epoch
          validateDocumentCursor cursor.Documents
          validateArchiveCursor cursor.Archive
          validatePhase cursor
          validateBounds bounds cursor
          validateCandidates archiveDir bounds cursor.Candidates
          validateArchiveRoot archiveDir cursor ]
        |> List.tryPick (function
            | Error error -> Some error
            | Ok() -> None)
        |> Option.map Error
        |> Option.defaultValue (Ok())

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

    type private ArchiveFile =
        { SortKey: string
          FileSystemPath: string
          SavedPath: string }

    let private encodeSegment (value: string) =
        value.Normalize(NormalizationForm.FormC)
        |> Encoding.UTF8.GetBytes
        |> Convert.ToHexString

    let private orderedOrdinal (values: string array) : string list =
        values
        |> Array.sortWith (fun left right ->
            StringComparer.Ordinal.Compare(left, right))
        |> Array.toList

    let private archiveFile
        (archiveDir: string)
        (prefix: string)
        (path: string)
        : ArchiveFile =
        let fileName =
            Path.GetFileName path
            |> Option.ofObj
            |> Option.defaultValue ""

        { SortKey = prefix + "F" + encodeSegment fileName
          FileSystemPath = path
          SavedPath = Path.GetRelativePath(archiveDir, path) }

    let private collectArchivePage
        (fs: Algebra.FileSystem)
        (archiveDir: string)
        (afterKey: string option)
        (limit: int)
        : ArchiveFile list =
        let isAfter (key: string) =
            afterKey
            |> Option.forall (fun previous ->
                StringComparer.Ordinal.Compare(key, previous) > 0)

        let rec addFiles
            (prefix: string)
            (collected: ArchiveFile list)
            (files: string list)
            : ArchiveFile list =
            match files with
            | _ when collected.Length >= limit -> collected
            | [] -> collected
            | path :: tail ->
                let item = archiveFile archiveDir prefix path
                let next =
                    if isAfter item.SortKey then item :: collected
                    else collected

                addFiles prefix next tail

        and walkDirectories
            (prefix: string)
            (collected: ArchiveFile list)
            (directories: string list)
            : ArchiveFile list =
            match directories with
            | _ when collected.Length >= limit -> collected
            | [] -> collected
            | directory :: tail ->
                let segment =
                    Path.GetFileName directory
                    |> Option.ofObj
                    |> Option.defaultValue ""
                    |> encodeSegment

                let nestedPrefix = prefix + "D" + segment + "/"
                let next = walk nestedPrefix directory collected
                walkDirectories prefix next tail

        and walk
            (prefix: string)
            (directory: string)
            (collected: ArchiveFile list)
            : ArchiveFile list =
            if collected.Length >= limit then
                collected
            else
                let directories =
                    fs.getDirectories directory
                    |> orderedOrdinal

                let afterDirectories =
                    walkDirectories prefix collected directories

                if afterDirectories.Length >= limit then
                    afterDirectories
                else
                    fs.getFiles directory "*"
                    |> Array.filter (isGeneratedArtifact >> not)
                    |> orderedOrdinal
                    |> addFiles prefix afterDirectories

        walk "" archiveDir []
        |> List.rev

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
        let step hashed path =
            task {
                let! hash = sha256 fs path
                return (path, hash) :: hashed
            }

        task {
            let! reversed = files |> Prelude.foldTask step []
            return List.rev reversed
        }

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

    type private PageDocument =
        { DocumentId: int64
          SavedPath: string
          Sha256: string }

    let private documentCursorValue = function
        | BeforeFirstDocument -> 0L
        | AfterDocument documentId -> documentId

    let private pageDocument row =
        let reader = Prelude.RowReader row

        { DocumentId = reader.Int64 "id" 0L
          SavedPath = reader.String "saved_path" ""
          Sha256 =
            reader.String "sha256" ""
            |> fun value -> value.ToLowerInvariant() }

    let private loadRunDocuments
        (db: Algebra.Database)
        (bounds: ScanBounds)
        continuation =
        db.execReader
            """SELECT id, saved_path, sha256
               FROM documents
               WHERE id > @afterId
               ORDER BY id
               LIMIT @limit"""
            [ "@afterId",
              continuation
              |> documentCursorValue
              |> Database.boxVal
              "@limit", Database.boxVal (int64 bounds.MaxDocuments) ]

    let private loadEpoch (db: Algebra.Database) : Task<Result<int64, string>> =
        task {
            let! (value: obj | null) =
                db.execScalar
                    """SELECT epoch FROM documents_change_epoch
                       WHERE singleton = 1"""
                    []

            return
                match value with
                | :? int64 as epoch when epoch >= 0L -> Ok epoch
                | :? int64 -> Error "Documents change epoch is negative"
                | _ -> Error "Documents change epoch is unavailable"
        }

    let private targetCandidates
        (documents: PageDocument list)
        : TargetCandidates list =
        documents
        |> List.map (fun document -> document.Sha256)
        |> List.distinct
        |> List.sortWith (fun left right ->
            StringComparer.Ordinal.Compare(left, right))
        |> List.map (fun sha256 ->
            { Sha256 = sha256
              Paths = [] })

    let private candidateTargets
        (candidates: TargetCandidates list)
        : string list =
        candidates
        |> List.map (fun candidate -> candidate.Sha256)
        |> List.sortWith (fun left right ->
            StringComparer.Ordinal.Compare(left, right))

    let private cursorTargetsMatch
        (documents: PageDocument list)
        (cursor: RunCursor)
        : bool =
        candidateTargets cursor.Candidates =
            candidateTargets (targetCandidates documents)

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

    let private repairRequest
        archiveDir
        (finding: Finding)
        candidate
        : Algebra.SavedPathRepairRequest =
        { ArchiveDirectory = archiveDir
          DocumentId = finding.DocumentId
          CurrentSavedPath = finding.SavedPath
          ExpectedSha256 = finding.Sha256
          CandidateSavedPath = candidate }

    let private mapRepairDecision candidate = function
        | Error message -> Failed(DatabaseFailure message)
        | Ok Algebra.SavedPathUpdated -> Repaired candidate
        | Ok(Algebra.SavedPathAlreadyOwnedByDocument savedPath) ->
            Unchanged savedPath
        | Ok(Algebra.SavedPathOwnedByOtherDocuments owners) ->
            Conflict
                { CandidateSavedPath = candidate
                  OwnerDocumentIds = owners }
        | Ok Algebra.SavedPathDocumentChanged -> Failed DocumentChanged

    let private updateSavedPath
        (db: Algebra.Database)
        archiveDir
        finding
        candidate =
        task {
            let request = repairRequest archiveDir finding candidate
            let! decision = db.tryRepairSavedPath request
            return mapRepairDecision candidate decision
        }

    let private repairUnique
        (db: Algebra.Database)
        (fs: Algebra.FileSystem)
        (archiveDir: string)
        (finding: Finding)
        (candidate: string) =
        task {
            match Database.canonicalArchivePath archiveDir candidate with
            | Error message ->
                return Failed(DatabaseFailure $"Invalid candidate path: {message}")
            | Ok _ ->
                let fileSystemPath = fullPath archiveDir candidate

                if not (fs.fileExists fileSystemPath) then
                    return Failed(CandidateDisappeared fileSystemPath)
                else
                    let! actual = sha256 fs fileSystemPath

                    if actual <> finding.Sha256 then
                        return Failed(CandidateChanged actual)
                    else
                        return! updateSavedPath db archiveDir finding candidate
        }

    let private repairFinding db fs archiveDir finding =
        match finding.Evidence with
        | UniqueShaMatch candidate ->
            repairUnique db fs archiveDir finding candidate
        | evidence ->
            Task.FromResult(Skipped evidence)

    let private repairAll db fs archiveDir findings =
        let step outcomes finding =
            task {
                let! disposition =
                    repairFinding db fs archiveDir finding
                return
                    { DocumentId = finding.DocumentId
                      Disposition = disposition }
                    :: outcomes
            }

        task {
            let! reversed = findings |> Prelude.foldTask step []
            return List.rev reversed
        }

    let private snapshotEpochValue (SnapshotEpoch epoch) = epoch

    let private restartPage
        (archiveDir: string)
        (bounds: ScanBounds)
        (epoch: int64)
        (findings: Finding list)
        (outcomes: RepairOutcome list)
        : Result<RunPageResult, string> =
        createRunCursor archiveDir bounds epoch
        |> Result.map (fun restart ->
            { Cursor = Some restart
              Progress =
                { DocumentsScanned = 0
                  FilesHashed = 0
                  ArchiveComplete = false }
              Stability = SnapshotChanged restart
              Findings = findings
              Outcomes = outcomes })

    let private inProgress
        (cursor: RunCursor)
        (documentsScanned: int)
        (filesHashed: int)
        : RunPageResult =
        { Cursor = Some cursor
          Progress =
            { DocumentsScanned = documentsScanned
              FilesHashed = filesHashed
              ArchiveComplete = false }
          Stability = InProgress
          Findings = []
          Outcomes = [] }

    let private nextDocumentCursor
        (cursor: RunCursor)
        (epoch: int64)
        (lastDocumentId: int64)
        : RunCursor =
        { cursor with
            Epoch = SnapshotEpoch epoch
            Phase = ArchiveScan
            Archive = ArchiveNotStarted
            Documents = AfterDocument lastDocumentId
            Candidates = [] }

    let private addMatchingCandidate
        (archiveDir: string)
        (targetHashes: Set<string>)
        (candidates: TargetCandidates list)
        (file: ArchiveFile)
        (hash: string)
        : Result<TargetCandidates list, string> =
        if Set.contains hash targetHashes then
            addCandidate archiveDir hash file.SavedPath candidates
        else
            Ok candidates

    let private hashArchivePage
        (fs: Algebra.FileSystem)
        (archiveDir: string)
        (targetHashes: Set<string>)
        (initial: TargetCandidates list)
        (files: ArchiveFile list)
        : Task<Result<TargetCandidates list, string>> =
        let step candidates file =
            task {
                let! hash = sha256 fs file.FileSystemPath
                return
                    addMatchingCandidate
                        archiveDir
                        targetHashes
                        candidates
                        file
                        hash
            }

        files |> Prelude.foldTaskResult step initial

    let private findingForDocument
        (fs: Algebra.FileSystem)
        (archiveDir: string)
        (candidates: TargetCandidates list)
        (document: PageDocument) =
        task {
            let currentPath = fullPath archiveDir document.SavedPath

            if fs.fileExists currentPath then
                let! actual = sha256 fs currentPath

                if actual = document.Sha256 then
                    return None
                else
                    return
                        Some
                            { DocumentId = document.DocumentId
                              SavedPath = document.SavedPath
                              Sha256 = document.Sha256
                              Evidence = ShaMismatch actual }
            else
                let paths =
                    candidatePaths document.Sha256 candidates

                let proof =
                    match paths with
                    | [] -> MissingShaMatch
                    | [ path ] -> UniqueShaMatch path
                    | values -> AmbiguousShaMatches values

                return
                    Some
                        { DocumentId = document.DocumentId
                          SavedPath = document.SavedPath
                          Sha256 = document.Sha256
                          Evidence = proof }
        }

    let private evaluateDocuments
        (fs: Algebra.FileSystem)
        (archiveDir: string)
        (candidates: TargetCandidates list)
        (documents: PageDocument list)
        : Task<Finding list> =
        let step findings document =
            task {
                let! finding =
                    findingForDocument
                        fs archiveDir candidates document
                return
                    finding
                    |> Option.map (fun value -> value :: findings)
                    |> Option.defaultValue findings
            }

        task {
            let! reversed =
                documents |> Prelude.foldTask step []
            return List.rev reversed
        }

    let private countRepairs (outcomes: RepairOutcome list) =
        outcomes
        |> List.filter (fun outcome ->
            match outcome.Disposition with
            | Repaired _ -> true
            | _ -> false)
        |> List.length
        |> int64

    let private hasDocumentsAfter
        (db: Algebra.Database)
        (documentId: int64)
        : Task<bool> =
        task {
            let! (value: obj | null) =
                db.execScalar
                    """SELECT EXISTS(
                           SELECT 1 FROM documents WHERE id > @id)"""
                    [ "@id", Database.boxVal documentId ]

            return
                match value with
                | :? int64 as exists -> exists <> 0L
                | _ -> false
        }

    let private finishDocumentPage
        (db: Algebra.Database)
        (fs: Algebra.FileSystem)
        (archiveDir: string)
        (bounds: ScanBounds)
        (mode: RunMode)
        (cursor: RunCursor)
        (filesHashed: int)
        (documents: PageDocument list)
        : Task<Result<RunPageResult, string>> =
        task {
            let! findings =
                evaluateDocuments
                    fs archiveDir cursor.Candidates documents

            let! outcomes =
                match mode with
                | DryRun -> Task.FromResult []
                | Apply ->
                    repairAll db fs archiveDir findings

            let startedAt = snapshotEpochValue cursor.Epoch
            let expectedEpoch = startedAt + countRepairs outcomes
            let! observed = loadEpoch db

            match observed with
            | Error error -> return Error error
            | Ok epoch when epoch <> expectedEpoch ->
                return!
                    restartPage
                        archiveDir bounds epoch findings outcomes
                    |> Task.FromResult
            | Ok _ ->
                let lastId =
                    documents
                    |> List.last
                    |> fun document -> document.DocumentId

                let! hasMore = hasDocumentsAfter db lastId
                let! finalEpoch = loadEpoch db

                match finalEpoch with
                | Error error -> return Error error
                | Ok epoch when epoch <> expectedEpoch ->
                    return!
                        restartPage
                            archiveDir bounds epoch findings outcomes
                        |> Task.FromResult
                | Ok epoch when hasMore ->
                    let next =
                        nextDocumentCursor cursor epoch lastId

                    return
                        Ok
                            { Cursor = Some next
                              Progress =
                                { DocumentsScanned = documents.Length
                                  FilesHashed = filesHashed
                                  ArchiveComplete = true }
                              Stability = InProgress
                              Findings = findings
                              Outcomes = outcomes }
                | Ok _ ->
                    return
                        Ok
                            { Cursor = None
                              Progress =
                                { DocumentsScanned = documents.Length
                                  FilesHashed = filesHashed
                                  ArchiveComplete = true }
                              Stability = StablePassCompleted
                              Findings = findings
                              Outcomes = outcomes }
        }

    let private completeEmptyPass
        (db: Algebra.Database)
        (archiveDir: string)
        (bounds: ScanBounds)
        (cursor: RunCursor)
        : Task<Result<RunPageResult, string>> =
        task {
            let expected = snapshotEpochValue cursor.Epoch
            let! observed = loadEpoch db

            match observed with
            | Error error -> return Error error
            | Ok epoch when epoch <> expected ->
                return restartPage archiveDir bounds epoch [] []
            | Ok _ ->
                return
                    Ok
                        { Cursor = None
                          Progress =
                            { DocumentsScanned = 0
                              FilesHashed = 0
                              ArchiveComplete = true }
                          Stability = StablePassCompleted
                          Findings = []
                          Outcomes = [] }
        }

    let private scanArchivePage
        (db: Algebra.Database)
        (fs: Algebra.FileSystem)
        (archiveDir: string)
        (bounds: ScanBounds)
        (mode: RunMode)
        (cursor: RunCursor)
        (documents: PageDocument list)
        : Task<Result<RunPageResult, string>> =
        task {
            let candidates =
                match cursor.Archive with
                | ArchiveNotStarted -> targetCandidates documents
                | _ -> cursor.Candidates

            let afterKey =
                match cursor.Archive with
                | AfterArchiveFile key -> Some key
                | _ -> None

            let discovered =
                collectArchivePage
                    fs archiveDir afterKey (bounds.MaxFiles + 1)

            let selected =
                discovered |> List.truncate bounds.MaxFiles

            let hasMore = discovered.Length > bounds.MaxFiles
            let targetHashes =
                candidates
                |> List.map (fun candidate -> candidate.Sha256)
                |> Set.ofList

            let! accumulated =
                hashArchivePage
                    fs archiveDir targetHashes candidates selected

            match accumulated with
            | Error error -> return Error error
            | Ok updated when hasMore ->
                let lastKey =
                    selected |> List.last |> fun file -> file.SortKey

                let next =
                    { cursor with
                        Archive = AfterArchiveFile lastKey
                        Candidates = updated }

                return
                    Ok(
                        inProgress
                            next documents.Length selected.Length)
            | Ok updated ->
                let completed =
                    { cursor with
                        Phase = DocumentScan
                        Archive = ArchiveCompleted
                        Candidates = updated }

                let! observed = loadEpoch db
                let expected = snapshotEpochValue cursor.Epoch

                match observed with
                | Error error -> return Error error
                | Ok epoch when epoch <> expected ->
                    return restartPage archiveDir bounds epoch [] []
                | Ok _ ->
                    return!
                        finishDocumentPage
                            db fs archiveDir bounds mode completed
                            selected.Length documents
        }

    let private executeRunPage
        (db: Algebra.Database)
        (fs: Algebra.FileSystem)
        (archiveDir: string)
        (bounds: ScanBounds)
        (mode: RunMode)
        (cursor: RunCursor)
        : Task<Result<RunPageResult, string>> =
        task {
            let! rows =
                loadRunDocuments db bounds cursor.Documents

            let documents = rows |> List.map pageDocument

            if documents.IsEmpty then
                return! completeEmptyPass db archiveDir bounds cursor
            elif
                cursor.Archive <> ArchiveNotStarted
                && not (cursorTargetsMatch documents cursor)
            then
                let! observed = loadEpoch db

                match observed with
                | Error error -> return Error error
                | Ok epoch ->
                    let expected = snapshotEpochValue cursor.Epoch

                    if epoch <> expected then
                        return restartPage archiveDir bounds epoch [] []
                    else
                        return Error "Cursor target documents do not match this page"
            else
                match cursor.Phase with
                | ArchiveScan ->
                    return!
                        scanArchivePage
                            db fs archiveDir bounds mode cursor documents
                | DocumentScan ->
                    return!
                        finishDocumentPage
                            db fs archiveDir bounds mode cursor 0 documents
        }

    let runPage
        (db: Algebra.Database)
        (fs: Algebra.FileSystem)
        (archiveDir: string)
        (bounds: ScanBounds)
        (mode: RunMode)
        (continuation: RunCursor option)
        : Task<Result<RunPageResult, string>> =
        task {
            let! observed = loadEpoch db

            match observed with
            | Error error -> return Error error
            | Ok epoch ->
                let prepared =
                    match continuation with
                    | None -> createRunCursor archiveDir bounds epoch
                    | Some cursor ->
                        validateRunCursor archiveDir bounds cursor
                        |> Result.map (fun () -> cursor)

                match prepared with
                | Error error -> return Error error
                | Ok cursor ->
                    let expected = snapshotEpochValue cursor.Epoch

                    if epoch <> expected then
                        return restartPage archiveDir bounds epoch [] []
                    else
                        return!
                            executeRunPage
                                db fs archiveDir bounds mode cursor
        }

    let repair db fs archiveDir bounds : Task<RepairReport> =
        task {
            let! scan = detect db fs archiveDir bounds
            let! outcomes =
                repairAll db fs archiveDir scan.Findings
            return { Scan = scan; Outcomes = outcomes }
        }
