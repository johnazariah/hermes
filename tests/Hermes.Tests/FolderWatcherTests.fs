module Hermes.Tests.FolderWatcherTests

open System
open System.IO
open System.Collections.Concurrent
open System.Threading.Tasks
open Xunit
open Hermes.Core

// ─── Test helpers ────────────────────────────────────────────────────

let private inMemoryFileSystem () =
    let files = ConcurrentDictionary<string, string>()
    let fileBytes = ConcurrentDictionary<string, byte array>()
    let dirs = ConcurrentDictionary<string, bool>()

    let fs: Algebra.FileSystem =
        { readAllText =
            fun path ->
                task {
                    match files.TryGetValue(path) with
                    | true, content -> return content
                    | _ -> return failwith $"File not found: {path}"
                }
          writeAllText = fun path content -> task { files.[path] <- content }
          writeAllBytes =
            fun path bytes ->
                task {
                    fileBytes.[path] <- bytes
                    files.[path] <- Text.Encoding.UTF8.GetString(bytes)
                }
          readAllBytes =
            fun path ->
                task {
                    match fileBytes.TryGetValue(path) with
                    | true, bytes -> return bytes
                    | _ ->
                        match files.TryGetValue(path) with
                        | true, content -> return Text.Encoding.UTF8.GetBytes(content)
                        | _ -> return failwith $"File not found: {path}"
                }
          fileExists = fun path -> files.ContainsKey(path) || fileBytes.ContainsKey(path)
          directoryExists = fun path -> dirs.ContainsKey(path)
          createDirectory = fun path -> dirs.[path] <- true
          deleteFile =
            fun path ->
                files.TryRemove(path) |> ignore
                fileBytes.TryRemove(path) |> ignore
          moveFile =
            fun src dst ->
                match files.TryRemove(src) with
                | true, content -> files.[dst] <- content
                | _ -> ()

                match fileBytes.TryRemove(src) with
                | true, bytes -> fileBytes.[dst] <- bytes
                | _ -> ()
          getFiles =
            fun dir _pattern ->
                let prefix =
                    if dir.EndsWith("/") || dir.EndsWith("\\") then
                        dir
                    else
                        dir + "/"

                files.Keys
                |> Seq.append fileBytes.Keys
                |> Seq.distinct
                |> Seq.filter (fun k ->
                    k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && not (k.Substring(prefix.Length).Contains("/"))
                    && not (k.Substring(prefix.Length).Contains("\\")))
                |> Seq.toArray
          getFileSize =
            fun path ->
                match fileBytes.TryGetValue(path) with
                | true, bytes -> int64 bytes.Length
                | _ ->
                    match files.TryGetValue(path) with
                    | true, content -> int64 (Text.Encoding.UTF8.GetByteCount(content))
                    | _ -> 0L }

    fs, files, fileBytes, dirs

let private testClock () : Algebra.Clock =
    { utcNow = fun () -> DateTimeOffset(2025, 3, 15, 10, 30, 0, TimeSpan.Zero) }

let private testDb () =
    let conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:")
    conn.Open()

    use pragma = conn.CreateCommand()
    pragma.CommandText <- "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;"
    pragma.ExecuteNonQuery() |> ignore

    let db = Database.fromConnection conn
    db.initSchema () |> Async.AwaitTask |> Async.RunSynchronously |> ignore
    db

let private testConfig archiveDir watchFolders : Domain.HermesConfig =
    { ArchiveDir = archiveDir
      Credentials = ""
      Accounts = []
      SyncIntervalMinutes = 15
      MinAttachmentSize = 20480
      WatchFolders = watchFolders
      Ollama =
        { Enabled = false
          BaseUrl = ""
          EmbeddingModel = ""
          VisionModel = ""
          InstructModel = "" }
      Fallback = { Embedding = ""; Ocr = "" }
      Azure = { DocumentIntelligenceEndpoint = ""; DocumentIntelligenceKey = "" } }

// ─── Glob pattern matching tests ─────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_MatchesAnyPattern_StarDotPdf_MatchesPdf`` () =
    Assert.True(FolderWatcher.matchesAnyPattern [ "*.pdf" ] "invoice.pdf")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_MatchesAnyPattern_StarDotPdf_DoesNotMatchTxt`` () =
    Assert.False(FolderWatcher.matchesAnyPattern [ "*.pdf" ] "readme.txt")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_MatchesAnyPattern_WildcardStatement_MatchesContaining`` () =
    Assert.True(FolderWatcher.matchesAnyPattern [ "*statement*" ] "bank-statement-march.pdf")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_MatchesAnyPattern_MultiplePatterns_MatchesAny`` () =
    let patterns = [ "*.pdf"; "*.docx" ]
    Assert.True(FolderWatcher.matchesAnyPattern patterns "report.pdf")
    Assert.True(FolderWatcher.matchesAnyPattern patterns "letter.docx")
    Assert.False(FolderWatcher.matchesAnyPattern patterns "image.png")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_MatchesAnyPattern_EmptyPatterns_MatchesAll`` () =
    Assert.True(FolderWatcher.matchesAnyPattern [] "anything.txt")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_MatchesAnyPattern_CaseInsensitive`` () =
    Assert.True(FolderWatcher.matchesAnyPattern [ "*.PDF" ] "invoice.pdf")
    Assert.True(FolderWatcher.matchesAnyPattern [ "*.pdf" ] "REPORT.PDF")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_MatchesAnyPattern_QuestionMark_MatchesSingleChar`` () =
    Assert.True(FolderWatcher.matchesAnyPattern [ "file?.txt" ] "file1.txt")
    Assert.False(FolderWatcher.matchesAnyPattern [ "file?.txt" ] "file12.txt")

// ─── Filename standardisation tests ──────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_BuildStandardName_FormatsCorrectly`` () =
    let now = DateTimeOffset(2025, 3, 15, 10, 30, 0, TimeSpan.Zero)
    let result = FolderWatcher.buildStandardName now "Downloads" "invoice.pdf"
    Assert.Equal("2025-03-15_Downloads_invoice.pdf", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_BuildStandardName_SanitisesInvalidChars`` () =
    let now = DateTimeOffset(2025, 3, 15, 10, 30, 0, TimeSpan.Zero)
    let result = FolderWatcher.buildStandardName now "My Folder" "file<>name.pdf"
    Assert.Contains("2025-03-15", result)
    Assert.DoesNotContain("<", result)
    Assert.DoesNotContain(">", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_SanitiseFileName_CollapsesUnderscores`` () =
    let result = FolderWatcher.sanitiseFileName "file___name"
    Assert.Equal("file_name", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_SanitiseFileName_EmptyString_ReturnsFallback`` () =
    let result = FolderWatcher.sanitiseFileName ""
    Assert.Equal("file", result)

// ─── Dedup check tests ───────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_IsDuplicate_NoExistingDoc_ReturnsFalse`` () =
    task {
        let db = testDb ()

        try
            let! isDup = FolderWatcher.isDuplicate db "abc123"
            Assert.False(isDup)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_IsDuplicate_ExistingDoc_ReturnsTrue`` () =
    task {
        let db = testDb ()

        try
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (source_type, saved_path, category, sha256)
                       VALUES ('watched_folder', 'invoices/test.pdf', 'invoices', 'abc123')"""
                    []

            let! isDup = FolderWatcher.isDuplicate db "abc123"
            Assert.True(isDup)
        finally
            db.dispose ()
    }

// ─── Sidecar metadata tests ─────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_BuildSidecar_HasCorrectSourceType`` () =
    let now = DateTimeOffset(2025, 3, 15, 10, 30, 0, TimeSpan.Zero)

    let sidecar =
        FolderWatcher.buildSidecar "/watch/Downloads" "/watch/Downloads/invoice.pdf" "invoice.pdf" "2025-03-15_Downloads_invoice.pdf" "abc123" now

    Assert.Equal("watched_folder", sidecar.SourceType)
    Assert.Equal("/watch/Downloads", sidecar.SourceFolder)
    Assert.Equal("invoice.pdf", sidecar.OriginalName)
    Assert.Equal("2025-03-15_Downloads_invoice.pdf", sidecar.SavedAs)
    Assert.Equal("abc123", sidecar.Sha256)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_SerialiseSidecar_ProducesValidJson`` () =
    let now = DateTimeOffset(2025, 3, 15, 10, 30, 0, TimeSpan.Zero)

    let sidecar =
        FolderWatcher.buildSidecar "/watch" "/watch/test.pdf" "test.pdf" "saved.pdf" "hash123" now

    let json = FolderWatcher.serialiseSidecar sidecar
    Assert.Contains("\"source_type\"", json)
    Assert.Contains("\"watched_folder\"", json)
    Assert.Contains("\"source_folder\"", json)
    Assert.Contains("\"original_name\"", json)

// ─── File processing tests ───────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_ProcessFile_CopiesMatchingFile`` () =
    task {
        let fs, files, _, dirs = inMemoryFileSystem ()
        let db = testDb ()
        let clock = testClock ()
        let logger = Logging.silent

        let archiveDir = "/archive"
        dirs.[archiveDir] <- true
        dirs.["/watch/Downloads"] <- true

        let srcPath = "/watch/Downloads/invoice.pdf"
        files.[srcPath] <- "PDF content here"

        let watchFolder : Domain.WatchFolderConfig =
            { Path = "/watch/Downloads"
              Patterns = [ "*.pdf" ] }

        try
            let! result =
                FolderWatcher.processFile fs db logger clock archiveDir watchFolder srcPath

            match result with
            | FolderWatcher.Copied savedPath ->
                Assert.Contains("unclassified", savedPath)
                Assert.Contains("Downloads", savedPath)
                Assert.Contains("invoice.pdf", savedPath)
                // Source file should still exist (copy, not move)
                Assert.True(files.ContainsKey(srcPath))
                // Sidecar should exist
                Assert.True(files.ContainsKey(savedPath + ".meta.json"))
            | other -> failwith $"Expected Copied, got {other}"
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_ProcessFile_SkipsNonMatchingPattern`` () =
    task {
        let fs, files, _, dirs = inMemoryFileSystem ()
        let db = testDb ()
        let clock = testClock ()
        let logger = Logging.silent

        dirs.["/watch"] <- true
        files.["/watch/readme.txt"] <- "text content"

        let watchFolder : Domain.WatchFolderConfig =
            { Path = "/watch"
              Patterns = [ "*.pdf" ] }

        try
            let! result =
                FolderWatcher.processFile fs db logger clock "/archive" watchFolder "/watch/readme.txt"

            match result with
            | FolderWatcher.Skipped reason ->
                Assert.Contains("pattern", reason)
            | other -> failwith $"Expected Skipped, got {other}"
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_ProcessFile_DetectsDuplicate`` () =
    task {
        let fs, files, _, dirs = inMemoryFileSystem ()
        let db = testDb ()
        let clock = testClock ()
        let logger = Logging.silent

        let archiveDir = "/archive"
        dirs.[archiveDir] <- true
        dirs.["/watch"] <- true

        let content = "duplicate content"
        files.["/watch/dup.pdf"] <- content

        // Compute hash and pre-insert
        let! hash = FolderWatcher.computeSha256 fs "/watch/dup.pdf"

        let! _ =
            db.execNonQuery
                """INSERT INTO documents (source_type, saved_path, category, sha256)
                   VALUES ('watched_folder', 'invoices/existing.pdf', 'invoices', @sha)"""
                [ ("@sha", Database.boxVal hash) ]

        let watchFolder : Domain.WatchFolderConfig =
            { Path = "/watch"
              Patterns = [ "*.pdf" ] }

        try
            let! result =
                FolderWatcher.processFile fs db logger clock archiveDir watchFolder "/watch/dup.pdf"

            match result with
            | FolderWatcher.Duplicate _ -> ()
            | other -> failwith $"Expected Duplicate, got {other}"
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_ProcessFile_MissingFile_ReturnsSkipped`` () =
    task {
        let fs, _, _, _ = inMemoryFileSystem ()
        let db = testDb ()
        let clock = testClock ()
        let logger = Logging.silent

        let watchFolder : Domain.WatchFolderConfig =
            { Path = "/watch"
              Patterns = [ "*" ] }

        try
            let! result =
                FolderWatcher.processFile fs db logger clock "/archive" watchFolder "/watch/nonexistent.pdf"

            match result with
            | FolderWatcher.Skipped _ -> ()
            | other -> failwith $"Expected Skipped, got {other}"
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_ProcessFile_UsesSafeCopyRename`` () =
    task {
        let fs, files, _, dirs = inMemoryFileSystem ()
        let db = testDb ()
        let clock = testClock ()
        let logger = Logging.silent

        let archiveDir = "/archive"
        dirs.[archiveDir] <- true
        dirs.["/watch"] <- true

        files.["/watch/test.pdf"] <- "content"

        let watchFolder : Domain.WatchFolderConfig =
            { Path = "/watch"
              Patterns = [ "*.pdf" ] }

        try
            let! result =
                FolderWatcher.processFile fs db logger clock archiveDir watchFolder "/watch/test.pdf"

            match result with
            | FolderWatcher.Copied savedPath ->
                // Temp file should NOT exist (renamed away)
                Assert.False(files.ContainsKey(savedPath + ".hermes_copying"))
                // Final file should exist
                Assert.True(files.ContainsKey(savedPath))
            | other -> failwith $"Expected Copied, got {other}"
        finally
            db.dispose ()
    }

// ─── Config manipulation tests ───────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_AddWatchFolder_AddsToConfig`` () =
    let config = testConfig "/archive" []

    match FolderWatcher.addWatchFolder config "/watch/new" [ "*.pdf" ] with
    | Ok updated ->
        Assert.Equal(1, updated.WatchFolders.Length)
        Assert.Equal("/watch/new", updated.WatchFolders.[0].Path)
        Assert.Equal<string list>([ "*.pdf" ], updated.WatchFolders.[0].Patterns)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_AddWatchFolder_EmptyPatterns_DefaultsToStar`` () =
    let config = testConfig "/archive" []

    match FolderWatcher.addWatchFolder config "/watch/new" [] with
    | Ok updated ->
        Assert.Equal<string list>([ "*" ], updated.WatchFolders.[0].Patterns)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_AddWatchFolder_Duplicate_ReturnsError`` () =
    let existing : Domain.WatchFolderConfig =
        { Path = "/watch/existing"; Patterns = [ "*" ] }

    let config = testConfig "/archive" [ existing ]

    match FolderWatcher.addWatchFolder config "/watch/existing" [ "*.pdf" ] with
    | Error e -> Assert.Contains("already configured", e)
    | Ok _ -> failwith "Expected Error for duplicate"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_RemoveWatchFolder_RemovesFromConfig`` () =
    let existing : Domain.WatchFolderConfig =
        { Path = "/watch/existing"; Patterns = [ "*" ] }

    let config = testConfig "/archive" [ existing ]

    match FolderWatcher.removeWatchFolder config "/watch/existing" with
    | Ok updated -> Assert.Empty(updated.WatchFolders)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_RemoveWatchFolder_NotFound_ReturnsError`` () =
    let config = testConfig "/archive" []

    match FolderWatcher.removeWatchFolder config "/watch/nonexistent" with
    | Error e -> Assert.Contains("not found", e)
    | Ok _ -> failwith "Expected Error for missing folder"

// ─── List watch folders test ─────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_ListWatchFolders_ReportsStatus`` () =
    let fs, _, _, dirs = inMemoryFileSystem ()
    dirs.["/watch/existing"] <- true

    let config =
        testConfig
            "/archive"
            [ { Path = "/watch/existing"; Patterns = [ "*.pdf" ] }
              { Path = "/watch/missing"; Patterns = [ "*" ] } ]

    let statuses = FolderWatcher.listWatchFolders fs config

    Assert.Equal(2, statuses.Length)
    Assert.True(statuses.[0].Exists)
    Assert.False(statuses.[1].Exists)

// ─── Batch scan test ─────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_ScanFolder_ProcessesAllMatchingFiles`` () =
    task {
        let fs, files, _, dirs = inMemoryFileSystem ()
        let db = testDb ()
        let clock = testClock ()
        let logger = Logging.silent

        let archiveDir = "/archive"
        dirs.[archiveDir] <- true
        dirs.["/watch"] <- true

        files.["/watch/invoice1.pdf"] <- "content1"
        files.["/watch/invoice2.pdf"] <- "content2"
        files.["/watch/readme.txt"] <- "text content"

        let watchFolder : Domain.WatchFolderConfig =
            { Path = "/watch"
              Patterns = [ "*.pdf" ] }

        try
            let! results =
                FolderWatcher.scanFolder fs db logger clock archiveDir watchFolder

            let copied =
                results
                |> List.filter (fun (_, r) ->
                    match r with
                    | FolderWatcher.Copied _ -> true
                    | _ -> false)

            let skipped =
                results
                |> List.filter (fun (_, r) ->
                    match r with
                    | FolderWatcher.Skipped _ -> true
                    | _ -> false)

            Assert.Equal(2, copied.Length)
            Assert.Equal(1, skipped.Length)
        finally
            db.dispose ()
    }

// ─── Glob to regex tests ────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_GlobToRegex_StarDotPdf`` () =
    let regex = FolderWatcher.globToRegex "*.pdf"
    Assert.True(regex.IsMatch("invoice.pdf"))
    Assert.True(regex.IsMatch("REPORT.PDF"))
    Assert.False(regex.IsMatch("report.txt"))
    Assert.False(regex.IsMatch("report.pdf.bak"))

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_GlobToRegex_WildcardMiddle`` () =
    let regex = FolderWatcher.globToRegex "*statement*"
    Assert.True(regex.IsMatch("bank-statement-march.pdf"))
    Assert.True(regex.IsMatch("statement"))
    Assert.False(regex.IsMatch("invoice.pdf"))
