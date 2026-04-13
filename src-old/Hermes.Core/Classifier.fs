namespace Hermes.Core

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json

/// Classifier pipeline: watches unclassified/, classifies, deduplicates, and archives documents.
/// Parameterised over algebras for testability.
[<RequireQualifiedAccess>]
module Classifier =

    // ─── Sidecar JSON parsing via JsonDocument ───────────────────────

    let private getStr (root: JsonElement) (prop: string) =
        match root.TryGetProperty(prop) with
        | true, v when v.ValueKind = JsonValueKind.String ->
            match v.GetString() with
            | null -> ""
            | s -> s
        | _ -> ""

    let private getOptStr (root: JsonElement) (prop: string) =
        match root.TryGetProperty(prop) with
        | true, v when v.ValueKind = JsonValueKind.String ->
            match v.GetString() with
            | null -> None
            | s when String.IsNullOrWhiteSpace(s) -> None
            | s -> Some s
        | _ -> None

    /// Parse a .meta.json sidecar file to SidecarMetadata.
    let parseSidecar (json: string) : Result<Domain.SidecarMetadata, string> =
        try
            use doc = JsonDocument.Parse(json)
            let r = doc.RootElement

            Ok(
                { SourceType = getStr r "source_type"
                  Account = getStr r "account"
                  GmailId = getStr r "gmail_id"
                  ThreadId = getStr r "thread_id"
                  Sender = getOptStr r "sender"
                  Subject = getOptStr r "subject"
                  EmailDate = getOptStr r "date"
                  OriginalName = getStr r "original_name"
                  SavedAs = getStr r "saved_as"
                  Sha256 = getStr r "sha256"
                  DownloadedAt = getStr r "downloaded_at" }
                : Domain.SidecarMetadata
            )
        with ex ->
            Error $"Failed to parse sidecar: {ex.Message}"

    // ─── SHA256 ──────────────────────────────────────────────────────

    /// Compute SHA256 hash of file content.
    let computeSha256 (fs: Algebra.FileSystem) (filePath: string) =
        task {
            let! bytes = fs.readAllBytes filePath
            let hash = SHA256.HashData(bytes)
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant()
        }

    // ─── Dedup check ─────────────────────────────────────────────────

    /// Check if a document with the given SHA256 already exists in the database.
    let isDuplicate (db: Algebra.Database) (sha256: string) =
        task {
            let! result =
                db.execScalar
                    "SELECT COUNT(*) FROM documents WHERE sha256 = @sha"
                    [ ("@sha", Database.boxVal sha256) ]

            return
                match result with
                | null -> false
                | v -> (v :?> int64) > 0L
        }

    // ─── Sidecar loading ─────────────────────────────────────────────

    /// Try to read and parse a .meta.json sidecar for the given file.
    let tryLoadSidecar (fs: Algebra.FileSystem) (logger: Algebra.Logger) (filePath: string) =
        task {
            let metaPath = filePath + ".meta.json"

            if fs.fileExists metaPath then
                try
                    let! json = fs.readAllText metaPath

                    match parseSidecar json with
                    | Ok meta -> return Some meta
                    | Error e ->
                        logger.warn $"Failed to parse sidecar for {filePath}: {e}"
                        return None
                with ex ->
                    logger.warn $"Failed to read sidecar for {filePath}: {ex.Message}"
                    return None
            else
                return None
        }

    // ─── File classification & archival ──────────────────────────────

    /// Process a single file: classify, dedup, move, and record.
    /// Resolve filename collision by appending timestamp.
    let private resolveDestPath (fs: Algebra.FileSystem) (clock: Algebra.Clock) (destPath: string) (fileName: string) =
        if not (fs.fileExists destPath) then destPath
        else
            let dir = Path.GetDirectoryName(destPath) |> Option.ofObj |> Option.defaultValue ""
            let stem = Path.GetFileNameWithoutExtension(fileName) |> Option.ofObj |> Option.defaultValue ""
            let ext = Path.GetExtension(fileName) |> Option.ofObj |> Option.defaultValue ""
            let ts = clock.utcNow().ToString("yyyyMMddHHmmss")
            Path.Combine(dir, $"{stem}_{ts}{ext}")

    /// Build a relative path from archive root.
    let private buildRelativePath (archiveDir: string) (fullPath: string) =
        let archiveFull = Path.GetFullPath(archiveDir)
        let fileFull = Path.GetFullPath(fullPath)
        if fileFull.StartsWith(archiveFull, StringComparison.OrdinalIgnoreCase) then
            fileFull.Substring(archiveFull.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        else fullPath

    /// Build SQL parameters for document insert from sidecar + classification result.
    let private buildDocParams
        (sidecar: Domain.SidecarMetadata option) (fileName: string) (relativePath: string)
        (category: string) (fileSize: int64) (sha256: string) (filePath: string) (now: string)
        : (string * obj) list =
        let optVal v = v |> Option.map Database.boxVal |> Option.defaultValue (Database.boxVal DBNull.Value)
        let sidecarStr f = sidecar |> Option.bind f |> optVal
        let sourceType = sidecar |> Option.map (fun s -> s.SourceType) |> Option.defaultValue "manual_drop"
        [ ("@source_type", Database.boxVal sourceType)
          ("@gmail_id", Database.boxVal DBNull.Value)
          ("@account", sidecar |> Option.map (fun s -> s.Account) |> Option.filter (String.IsNullOrEmpty >> not) |> optVal)
          ("@sender", sidecarStr (fun s -> s.Sender))
          ("@subject", sidecarStr (fun s -> s.Subject))
          ("@email_date", sidecarStr (fun s -> s.EmailDate))
          ("@original_name", Database.boxVal fileName)
          ("@saved_path", Database.boxVal relativePath)
          ("@category", Database.boxVal category)
          ("@size_bytes", Database.boxVal fileSize)
          ("@sha256", Database.boxVal sha256)
          ("@source_path", Database.boxVal filePath)
          ("@ingested_at", Database.boxVal now) ]

    let private insertDocSql =
        """INSERT INTO documents
           (source_type, gmail_id, account, sender, subject, email_date,
            original_name, saved_path, category, size_bytes, sha256, source_path, ingested_at)
           VALUES
           (@source_type, @gmail_id, @account, @sender, @subject, @email_date,
            @original_name, @saved_path, @category, @size_bytes, @sha256, @source_path, @ingested_at)
           RETURNING id"""

    /// Clean up sidecar .meta.json if it exists.
    let private cleanupSidecar (fs: Algebra.FileSystem) (filePath: string) =
        let metaPath = filePath + ".meta.json"
        if fs.fileExists metaPath then fs.deleteFile metaPath

    /// Process a single file: classify, dedup, move, and record.
    /// Returns Ok(Some docId) on insert, Ok(None) on duplicate/skip, Error on failure.
    let processFile
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (rules: Algebra.RulesEngine) (archiveDir: string) (filePath: string)
        =
        task {
            let fileName = Path.GetFileName(filePath) |> Option.ofObj |> Option.defaultValue ""
            if not (fs.fileExists filePath) then return Ok None
            else
            try
                let! sha256 = computeSha256 fs filePath
                let! isDup = isDuplicate db sha256
                if isDup then
                    logger.info $"Duplicate detected (sha256={sha256.[..7]}), skipping: {fileName}"
                    fs.deleteFile filePath
                    cleanupSidecar fs filePath
                    return Ok None
                else

                let! sidecar = tryLoadSidecar fs logger filePath
                let result = rules.classify sidecar fileName
                logger.info $"Classified '{fileName}' -> {result.Category}"

                let categoryDir = Path.Combine(archiveDir, result.Category)
                fs.createDirectory categoryDir
                let destPath = Path.Combine(categoryDir, fileName)
                let finalDest = resolveDestPath fs clock destPath fileName
                fs.moveFile filePath finalDest

                let relativePath = buildRelativePath archiveDir finalDest
                let now = clock.utcNow().ToString("o")
                let ps = buildDocParams sidecar fileName relativePath result.Category (fs.getFileSize finalDest) sha256 filePath now
                let! idObj = db.execScalar insertDocSql ps
                let docId = match idObj with :? int64 as i -> i | :? int as i -> int64 i | _ -> 0L
                cleanupSidecar fs filePath
                return Ok (Some docId)
            with ex ->
                logger.error $"Failed to process {fileName}: {ex.Message}"
                return Error ex.Message
        }

    // ─── Reconcile ───────────────────────────────────────────────────

    /// Result of a reconcile scan.
    type ReconcileAction =
        | MissingFromDisk of savedPath: string * docId: int64
        | NewOnDisk of filePath: string * category: string
        | MovedOnDisk of savedPath: string * docId: int64

    /// Walk the archive and compare against database records.
    let reconcile
        (fs: Algebra.FileSystem)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (archiveDir: string)
        (dryRun: bool)
        =
        task {
            logger.info $"Reconciling archive at {archiveDir} (dry-run={dryRun})..."

            let actions = ResizeArray<ReconcileAction>()

            for cat in Database.archiveCategories do
                if cat <> "unclassified" then
                    let catDir = Path.Combine(archiveDir, cat)

                    if fs.directoryExists catDir then
                        let files = fs.getFiles catDir "*.*"

                        for filePath in files do
                            let relPath =
                                let full = Path.GetFullPath(filePath)
                                let archFull = Path.GetFullPath(archiveDir)

                                if full.StartsWith(archFull, StringComparison.OrdinalIgnoreCase) then
                                    full.Substring(archFull.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                else
                                    filePath

                            let! countResult =
                                db.execScalar
                                    "SELECT COUNT(*) FROM documents WHERE saved_path = @p"
                                    [ ("@p", Database.boxVal relPath) ]

                            let count =
                                match countResult with
                                | null -> 0L
                                | v -> v :?> int64

                            if count = 0L then
                                actions.Add(NewOnDisk(filePath, cat))

                                if not dryRun then
                                    logger.info $"New file on disk (not in DB): {relPath}"

            return actions |> Seq.toList
        }

    // ─── Suggest rules ───────────────────────────────────────────────

    type RuleSuggestion =
        { SuggestedName: string
          MatchType: string
          Pattern: string
          Category: string
          ExampleFile: string }

    let suggestRules
        (fs: Algebra.FileSystem)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (archiveDir: string)
        =
        task {
            let suggestions = ResizeArray<RuleSuggestion>()

            for cat in Database.archiveCategories do
                if cat <> "unclassified" && cat <> "unsorted" then
                    let catDir = Path.Combine(archiveDir, cat)

                    if fs.directoryExists catDir then
                        let files = fs.getFiles catDir "*.*"

                        for filePath in files do
                            let fileName = Path.GetFileName(filePath) |> Option.ofObj |> Option.defaultValue ""

                            let! countResult =
                                db.execScalar
                                    "SELECT COUNT(*) FROM documents WHERE original_name = @n AND category = 'unsorted'"
                                    [ ("@n", Database.boxVal fileName) ]

                            let count =
                                match countResult with
                                | null -> 0L
                                | v -> v :?> int64

                            if count > 0L then
                                let rawStem =
                                    Path.GetFileNameWithoutExtension(fileName) |> Option.ofObj |> Option.defaultValue ""

                                let stem = rawStem.ToLowerInvariant()

                                suggestions.Add(
                                    { SuggestedName = $"auto-{cat}-{stem}"
                                      MatchType = "filename"
                                      Pattern = $"(?i){Text.RegularExpressions.Regex.Escape(stem)}"
                                      Category = cat
                                      ExampleFile = fileName }
                                )

            return suggestions |> Seq.toList
        }

    // ─── Bulk reclassification ───────────────────────────────────────

    /// Get unsorted documents that have extracted text (candidates for Tier 2).
    let getUnsortedWithText (db: Algebra.Database) (limit: int) =
        task {
            let! rows =
                db.execReader
                    """SELECT id, saved_path, extracted_text, extracted_amount
                       FROM documents
                       WHERE (category = 'unsorted' OR category = 'unclassified')
                         AND extracted_text IS NOT NULL
                       ORDER BY id ASC LIMIT @lim"""
                    [ ("@lim", Database.boxVal (int64 limit)) ]
            return rows |> List.choose (fun row ->
                let r = Prelude.RowReader(row)
                match r.OptInt64 "id", r.OptString "saved_path", r.OptString "extracted_text" with
                | Some id, Some path, Some text ->
                    Some (id, path, text, r.OptFloat "extracted_amount" |> Option.map decimal)
                | _ -> None)
        }

    /// Reclassify a batch of unsorted documents using Tier 2 content rules.
    let reclassifyUnsortedBatch
        (db: Algebra.Database) (fs: Algebra.FileSystem)
        (contentRules: Domain.ContentRule list)
        (archiveDir: string) (batchSize: int)
        : Threading.Tasks.Task<int * int> =
        task {
            let! candidates = getUnsortedWithText db batchSize
            let mutable reclassified = 0
            for (docId, _savedPath, text, amount) in candidates do
                match ContentClassifier.classify text [] amount contentRules with
                | Some (category, confidence) ->
                    let! result = DocumentManagement.reclassify db fs archiveDir docId category
                    match result with
                    | Ok () ->
                        let! _ =
                            db.execNonQuery
                                """UPDATE documents SET classification_tier = 'content',
                                   classification_confidence = @conf WHERE id = @id"""
                                [ ("@conf", Database.boxVal confidence)
                                  ("@id", Database.boxVal docId) ]
                        reclassified <- reclassified + 1
                    | Error _ -> ()
                | None -> ()
            let! remainingObj =
                db.execScalar
                    "SELECT COUNT(*) FROM documents WHERE category = 'unsorted' OR category = 'unclassified'"
                    []
            let remaining = match remainingObj with :? int64 as i -> int i | _ -> 0
            return (reclassified, remaining)
        }
