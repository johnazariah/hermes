namespace Hermes.Core

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks

/// Folder watching logic: watches configured directories for new files,
/// filters by glob patterns, deduplicates via SHA256, and copies to unclassified/.
/// Parameterised over algebras for testability.
[<RequireQualifiedAccess>]
module FolderWatcher =

    let private boxVal (x: 'a) : obj = x :> obj

    // ─── Glob pattern matching ───────────────────────────────────────

    /// Convert a simple glob pattern (*, ?) to a regex pattern.
    let globToRegex (glob: string) : Regex =
        let escaped = Regex.Escape(glob)
        let pattern =
            escaped
                .Replace(@"\*", ".*")
                .Replace(@"\?", ".")
        Regex($"^{pattern}$", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)

    /// Check if a filename matches any of the given glob patterns.
    let matchesAnyPattern (patterns: string list) (fileName: string) : bool =
        if patterns.IsEmpty then
            true
        else
            patterns |> List.exists (fun p -> (globToRegex p).IsMatch(fileName))

    // ─── Filename standardisation ────────────────────────────────────

    let private invalidChars =
        Path.GetInvalidFileNameChars() |> Set.ofArray

    /// Remove invalid filename characters and collapse underscores.
    let sanitiseFileName (name: string) =
        name.ToCharArray()
        |> Array.map (fun c -> if invalidChars.Contains c then '_' else c)
        |> String
        |> fun s -> Regex.Replace(s, @"_+", "_")
        |> fun s -> s.Trim('_', ' ')
        |> fun s -> if String.IsNullOrWhiteSpace(s) then "file" else s

    /// Build a standardised filename: {date_today}_{source_folder}_{original_name}.
    let buildStandardName (today: DateTimeOffset) (sourceFolder: string) (originalName: string) =
        let datePart = today.ToString("yyyy-MM-dd")
        let folderPart = sanitiseFileName sourceFolder
        let namePart = sanitiseFileName originalName
        $"{datePart}_{folderPart}_{namePart}"

    // ─── SHA256 hashing ──────────────────────────────────────────────

    /// Compute SHA256 hash of file content via the FileSystem algebra.
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
                    [ ("@sha", boxVal sha256) ]

            return
                match result with
                | null -> false
                | v -> (v :?> int64) > 0L
        }

    // ─── Sidecar metadata ────────────────────────────────────────────

    /// Metadata written as a .meta.json sidecar alongside each copied file.
    type WatchSidecarMetadata =
        { SourceType: string
          SourceFolder: string
          OriginalPath: string
          OriginalName: string
          SavedAs: string
          Sha256: string
          CopiedAt: string }

    let private jsonOptions =
        let opts = JsonSerializerOptions(WriteIndented = true)
        opts.PropertyNamingPolicy <- JsonNamingPolicy.SnakeCaseLower
        opts

    /// Serialise sidecar metadata to JSON.
    let serialiseSidecar (sidecar: WatchSidecarMetadata) : string =
        JsonSerializer.Serialize(sidecar, jsonOptions)

    /// Build sidecar metadata for a watched-folder file.
    let buildSidecar
        (sourceFolder: string)
        (originalPath: string)
        (originalName: string)
        (savedAs: string)
        (sha256: string)
        (now: DateTimeOffset)
        : WatchSidecarMetadata =
        { SourceType = "watched_folder"
          SourceFolder = sourceFolder
          OriginalPath = originalPath
          OriginalName = originalName
          SavedAs = savedAs
          Sha256 = sha256
          CopiedAt = now.ToString("o") }

    // ─── File intake ─────────────────────────────────────────────────

    /// Result of processing a single file from a watched folder.
    type IntakeResult =
        | Copied of savedPath: string
        | Duplicate of sha256: string
        | Skipped of reason: string
        | Failed of error: string

    /// Safe copy to unclassified/ with .hermes_copying temp file.
    let private safeCopyToUnclassified
        (fs: Algebra.FileSystem) (logger: Algebra.Logger)
        (unclassifiedDir: string) (standardName: string) (filePath: string)
        : Task<string> =
        task {
            let finalPath = Path.Combine(unclassifiedDir, standardName)
            let tempPath = finalPath + ".hermes_copying"
            let! bytes = fs.readAllBytes filePath
            do! fs.writeAllBytes tempPath bytes
            fs.moveFile tempPath finalPath
            let srcName = Path.GetFileName(filePath) |> Option.ofObj |> Option.defaultValue ""
            logger.info $"Copied '{srcName}' -> unclassified/{standardName}"
            return finalPath
        }

    /// Process a single file: hash, dedup, copy to unclassified/ with safe rename.
    let processFile
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (archiveDir: string) (watchFolder: Domain.WatchFolderConfig) (filePath: string)
        =
        task {
            let fileName = Path.GetFileName(filePath) |> Option.ofObj |> Option.defaultValue ""
            if String.IsNullOrEmpty(fileName) then return Skipped "empty filename"
            elif not (fs.fileExists filePath) then return Skipped "file no longer exists"
            elif not (matchesAnyPattern watchFolder.Patterns fileName) then return Skipped "pattern mismatch"
            else

            try
                let! sha256 = computeSha256 fs filePath
                let! isDup = isDuplicate db sha256
                if isDup then return Duplicate sha256
                else

                let now = clock.utcNow ()
                let unclDir = Path.Combine(archiveDir, "unclassified")
                fs.createDirectory unclDir
                let folderName = Path.GetFileName(watchFolder.Path) |> Option.ofObj |> Option.defaultValue "watched"
                let standardName = buildStandardName now folderName fileName
                let! finalPath = safeCopyToUnclassified fs logger unclDir standardName filePath

                let sidecar = buildSidecar watchFolder.Path filePath fileName standardName sha256 now
                do! fs.writeAllText (finalPath + ".meta.json") (serialiseSidecar sidecar)
                return Copied finalPath
            with ex ->
                logger.error $"Failed to process {fileName}: {ex.Message}"
                return Failed ex.Message
        }

    // ─── Watch folder summary ────────────────────────────────────────

    /// Status of a configured watch folder.
    type WatchFolderStatus =
        { Path: string
          Patterns: string list
          Exists: bool }

    /// List configured watch folders with their status.
    let listWatchFolders (fs: Algebra.FileSystem) (config: Domain.HermesConfig) : WatchFolderStatus list =
        config.WatchFolders
        |> List.map (fun wf ->
            { Path = wf.Path
              Patterns = wf.Patterns
              Exists = fs.directoryExists wf.Path })

    // ─── Config manipulation ─────────────────────────────────────────

    /// Add a watch folder to the configuration (in memory).
    let addWatchFolder
        (env: Algebra.Environment)
        (config: Domain.HermesConfig)
        (path: string)
        (patterns: string list)
        : Result<Domain.HermesConfig, string> =
        let expandedPath = Config.expandHome env path

        let alreadyExists =
            config.WatchFolders
            |> List.exists (fun wf ->
                String.Equals(wf.Path, expandedPath, StringComparison.OrdinalIgnoreCase))

        if alreadyExists then
            Error $"Watch folder already configured: {expandedPath}"
        else
            let newFolder : Domain.WatchFolderConfig =
                { Path = expandedPath
                  Patterns = if patterns.IsEmpty then [ "*" ] else patterns }

            Ok { config with WatchFolders = config.WatchFolders @ [ newFolder ] }

    /// Remove a watch folder from the configuration (in memory).
    let removeWatchFolder
        (env: Algebra.Environment)
        (config: Domain.HermesConfig)
        (path: string)
        : Result<Domain.HermesConfig, string> =
        let expandedPath = Config.expandHome env path

        let exists =
            config.WatchFolders
            |> List.exists (fun wf ->
                String.Equals(wf.Path, expandedPath, StringComparison.OrdinalIgnoreCase))

        if not exists then
            Error $"Watch folder not found: {expandedPath}"
        else
            let updated =
                config.WatchFolders
                |> List.filter (fun wf ->
                    not (String.Equals(wf.Path, expandedPath, StringComparison.OrdinalIgnoreCase)))

            Ok { config with WatchFolders = updated }

    // ─── Batch scan ──────────────────────────────────────────────────

    /// Scan a single watch folder for all matching files (no watcher events needed).
    let scanFolder
        (fs: Algebra.FileSystem)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (clock: Algebra.Clock)
        (archiveDir: string)
        (watchFolder: Domain.WatchFolderConfig)
        =
        task {
            let results = ResizeArray<string * IntakeResult>()

            if not (fs.directoryExists watchFolder.Path) then
                logger.warn $"Watch folder does not exist: {watchFolder.Path}"
                return results |> Seq.toList
            else

            let allFiles = fs.getFiles watchFolder.Path "*"

            for filePath in allFiles do
                let! result = processFile fs db logger clock archiveDir watchFolder filePath
                results.Add(filePath, result)

            return results |> Seq.toList
        }

    /// Scan all configured watch folders.
    let scanAll
        (fs: Algebra.FileSystem)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (clock: Algebra.Clock)
        (config: Domain.HermesConfig)
        =
        task {
            let allResults = ResizeArray<string * IntakeResult>()

            for wf in config.WatchFolders do
                let! results = scanFolder fs db logger clock config.ArchiveDir wf
                allResults.AddRange(results)

            return allResults |> Seq.toList
        }
