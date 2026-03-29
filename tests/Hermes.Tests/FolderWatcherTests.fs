module Hermes.Tests.FolderWatcherTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open Hermes.Core

// ─── Test helpers ────────────────────────────────────────────────────

let private watchTestConfig archiveDir watchFolders : Domain.HermesConfig =
    { TestHelpers.testConfig archiveDir with WatchFolders = watchFolders }

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
        let db = TestHelpers.createDb ()

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
        let db = TestHelpers.createDb ()

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
        let m = TestHelpers.memFs ()
        let db = TestHelpers.createDb ()
        let clock = TestHelpers.defaultClock
        let logger = Logging.silent

        let archiveDir = "/archive"
        m.Dirs.[archiveDir] <- true
        m.Dirs.["/watch/Downloads"] <- true

        let srcPath = "/watch/Downloads/invoice.pdf"
        m.Files.[srcPath] <- "PDF content here"

        let watchFolder : Domain.WatchFolderConfig =
            { Path = "/watch/Downloads"
              Patterns = [ "*.pdf" ] }

        try
            let! result =
                FolderWatcher.processFile m.Fs db logger clock archiveDir watchFolder srcPath

            match result with
            | FolderWatcher.Copied savedPath ->
                Assert.Contains("unclassified", savedPath)
                Assert.Contains("Downloads", savedPath)
                Assert.Contains("invoice.pdf", savedPath)
                // Source file should still exist (copy, not move)
                Assert.True(m.Files.ContainsKey(srcPath))
                // Sidecar should exist
                Assert.True(m.Files.ContainsKey(savedPath + ".meta.json"))
            | other -> failwith $"Expected Copied, got {other}"
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_ProcessFile_SkipsNonMatchingPattern`` () =
    task {
        let m = TestHelpers.memFs ()
        let db = TestHelpers.createDb ()
        let clock = TestHelpers.defaultClock
        let logger = Logging.silent

        m.Dirs.["/watch"] <- true
        m.Files.["/watch/readme.txt"] <- "text content"

        let watchFolder : Domain.WatchFolderConfig =
            { Path = "/watch"
              Patterns = [ "*.pdf" ] }

        try
            let! result =
                FolderWatcher.processFile m.Fs db logger clock "/archive" watchFolder "/watch/readme.txt"

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
        let m = TestHelpers.memFs ()
        let db = TestHelpers.createDb ()
        let clock = TestHelpers.defaultClock
        let logger = Logging.silent

        let archiveDir = "/archive"
        m.Dirs.[archiveDir] <- true
        m.Dirs.["/watch"] <- true

        let content = "duplicate content"
        m.Files.["/watch/dup.pdf"] <- content

        // Compute hash and pre-insert
        let! hash = FolderWatcher.computeSha256 m.Fs "/watch/dup.pdf"

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
                FolderWatcher.processFile m.Fs db logger clock archiveDir watchFolder "/watch/dup.pdf"

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
        let m = TestHelpers.memFs ()
        let db = TestHelpers.createDb ()
        let clock = TestHelpers.defaultClock
        let logger = Logging.silent

        let watchFolder : Domain.WatchFolderConfig =
            { Path = "/watch"
              Patterns = [ "*" ] }

        try
            let! result =
                FolderWatcher.processFile m.Fs db logger clock "/archive" watchFolder "/watch/nonexistent.pdf"

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
        let m = TestHelpers.memFs ()
        let db = TestHelpers.createDb ()
        let clock = TestHelpers.defaultClock
        let logger = Logging.silent

        let archiveDir = "/archive"
        m.Dirs.[archiveDir] <- true
        m.Dirs.["/watch"] <- true

        m.Files.["/watch/test.pdf"] <- "content"

        let watchFolder : Domain.WatchFolderConfig =
            { Path = "/watch"
              Patterns = [ "*.pdf" ] }

        try
            let! result =
                FolderWatcher.processFile m.Fs db logger clock archiveDir watchFolder "/watch/test.pdf"

            match result with
            | FolderWatcher.Copied savedPath ->
                // Temp file should NOT exist (renamed away)
                Assert.False(m.Files.ContainsKey(savedPath + ".hermes_copying"))
                // Final file should exist
                Assert.True(m.Files.ContainsKey(savedPath))
            | other -> failwith $"Expected Copied, got {other}"
        finally
            db.dispose ()
    }

// ─── Config manipulation tests ───────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_AddWatchFolder_AddsToConfig`` () =
    let config = watchTestConfig "/archive" []

    match FolderWatcher.addWatchFolder config "/watch/new" [ "*.pdf" ] with
    | Ok updated ->
        Assert.Equal(1, updated.WatchFolders.Length)
        Assert.Equal("/watch/new", updated.WatchFolders.[0].Path)
        Assert.Equal<string list>([ "*.pdf" ], updated.WatchFolders.[0].Patterns)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_AddWatchFolder_EmptyPatterns_DefaultsToStar`` () =
    let config = watchTestConfig "/archive" []

    match FolderWatcher.addWatchFolder config "/watch/new" [] with
    | Ok updated ->
        Assert.Equal<string list>([ "*" ], updated.WatchFolders.[0].Patterns)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_AddWatchFolder_Duplicate_ReturnsError`` () =
    let existing : Domain.WatchFolderConfig =
        { Path = "/watch/existing"; Patterns = [ "*" ] }

    let config = watchTestConfig "/archive" [ existing ]

    match FolderWatcher.addWatchFolder config "/watch/existing" [ "*.pdf" ] with
    | Error e -> Assert.Contains("already configured", e)
    | Ok _ -> failwith "Expected Error for duplicate"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_RemoveWatchFolder_RemovesFromConfig`` () =
    let existing : Domain.WatchFolderConfig =
        { Path = "/watch/existing"; Patterns = [ "*" ] }

    let config = watchTestConfig "/archive" [ existing ]

    match FolderWatcher.removeWatchFolder config "/watch/existing" with
    | Ok updated -> Assert.Empty(updated.WatchFolders)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_RemoveWatchFolder_NotFound_ReturnsError`` () =
    let config = watchTestConfig "/archive" []

    match FolderWatcher.removeWatchFolder config "/watch/nonexistent" with
    | Error e -> Assert.Contains("not found", e)
    | Ok _ -> failwith "Expected Error for missing folder"

// ─── List watch folders test ─────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_ListWatchFolders_ReportsStatus`` () =
    let m = TestHelpers.memFs ()
    m.Dirs.["/watch/existing"] <- true

    let config =
        watchTestConfig
            "/archive"
            [ { Path = "/watch/existing"; Patterns = [ "*.pdf" ] }
              { Path = "/watch/missing"; Patterns = [ "*" ] } ]

    let statuses = FolderWatcher.listWatchFolders m.Fs config

    Assert.Equal(2, statuses.Length)
    Assert.True(statuses.[0].Exists)
    Assert.False(statuses.[1].Exists)

// ─── Batch scan test ─────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``FolderWatcher_ScanFolder_ProcessesAllMatchingFiles`` () =
    task {
        let m = TestHelpers.memFs ()
        let db = TestHelpers.createDb ()
        let clock = TestHelpers.defaultClock
        let logger = Logging.silent

        let archiveDir = "/archive"
        m.Dirs.[archiveDir] <- true
        m.Dirs.["/watch"] <- true

        m.Files.["/watch/invoice1.pdf"] <- "content1"
        m.Files.["/watch/invoice2.pdf"] <- "content2"
        m.Files.["/watch/readme.txt"] <- "text content"

        let watchFolder : Domain.WatchFolderConfig =
            { Path = "/watch"
              Patterns = [ "*.pdf" ] }

        try
            let! results =
                FolderWatcher.scanFolder m.Fs db logger clock archiveDir watchFolder

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
