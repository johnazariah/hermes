module Hermes.Tests.ClassifierTests

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

let private testRulesEngine () : Algebra.RulesEngine =
    let rulesYaml =
        """
rules:
  - name: plumber-domain
    match:
      sender_domain: plumbing.com.au
    category: trades

  - name: invoices-by-filename
    match:
      filename: "(?i)invoice"
    category: invoices

  - name: tax-by-subject
    match:
      subject: "(?i)tax|ato"
    category: tax

default_category: unsorted
"""

    match Rules.parseRulesYaml rulesYaml with
    | Ok(rules, defaultCat) ->
        { classify = fun sidecar filename -> Rules.classifyWithRules rules defaultCat sidecar filename
          reload = fun () -> task { return Ok() } }
    | Error e -> failwith $"Failed to load test rules: {e}"

// ─── Sidecar parsing tests ───────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_ParseSidecar_ValidJson_ReturnsSidecar`` () =
    let json =
        """{
  "source_type": "email_attachment",
  "account": "john-personal",
  "gmail_id": "18e4f2a3b5c6d7e8",
  "thread_id": "thread123",
  "sender": "bob@plumbing.com.au",
  "subject": "Invoice for March work",
  "date": "2025-03-15T10:30:00+11:00",
  "original_name": "Invoice-2025-001.pdf",
  "saved_as": "Invoice-2025-001.pdf",
  "sha256": "abc123",
  "downloaded_at": "2025-03-15T00:00:00Z"
}"""

    match Classifier.parseSidecar json with
    | Ok meta ->
        Assert.Equal("email_attachment", meta.SourceType)
        Assert.Equal("john-personal", meta.Account)
        Assert.Equal("18e4f2a3b5c6d7e8", meta.GmailId)
        Assert.Equal(Some "bob@plumbing.com.au", meta.Sender)
        Assert.Equal(Some "Invoice for March work", meta.Subject)
        Assert.Equal("Invoice-2025-001.pdf", meta.OriginalName)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_ParseSidecar_MissingOptionalFields_ReturnsNone`` () =
    let json =
        """{
  "source_type": "manual_drop",
  "account": "test",
  "gmail_id": "",
  "original_name": "test.pdf",
  "sha256": "def456"
}"""

    match Classifier.parseSidecar json with
    | Ok meta ->
        Assert.Equal(None, meta.Sender)
        Assert.Equal(None, meta.Subject)
        Assert.Equal(None, meta.EmailDate)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_ParseSidecar_InvalidJson_ReturnsError`` () =
    match Classifier.parseSidecar "not valid json" with
    | Error _ -> ()
    | Ok _ -> failwith "Expected Error for invalid JSON"

// ─── Dedup tests ─────────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_IsDuplicate_NoExistingDoc_ReturnsFalse`` () =
    task {
        let db = testDb ()

        try
            let! isDup = Classifier.isDuplicate db "abc123"
            Assert.False(isDup)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_IsDuplicate_ExistingDoc_ReturnsTrue`` () =
    task {
        let db = testDb ()

        try
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (source_type, saved_path, category, sha256)
                       VALUES ('manual_drop', 'invoices/test.pdf', 'invoices', 'abc123')"""
                    []

            let! isDup = Classifier.isDuplicate db "abc123"
            Assert.True(isDup)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_IsDuplicate_DifferentHash_ReturnsFalse`` () =
    task {
        let db = testDb ()

        try
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (source_type, saved_path, category, sha256)
                       VALUES ('manual_drop', 'invoices/test.pdf', 'invoices', 'abc123')"""
                    []

            let! isDup = Classifier.isDuplicate db "different_hash"
            Assert.False(isDup)
        finally
            db.dispose ()
    }

// ─── SHA256 tests ────────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_ComputeSha256_ReturnsConsistentHash`` () =
    task {
        let fs, files, _, _ = inMemoryFileSystem ()
        files.["/test/file.pdf"] <- "hello world"

        let! hash1 = Classifier.computeSha256 fs "/test/file.pdf"
        let! hash2 = Classifier.computeSha256 fs "/test/file.pdf"

        Assert.Equal(hash1, hash2)
        Assert.Equal(64, hash1.Length) // SHA256 = 32 bytes = 64 hex chars
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_ComputeSha256_DifferentContent_DifferentHash`` () =
    task {
        let fs, files, _, _ = inMemoryFileSystem ()
        files.["/test/file1.pdf"] <- "hello"
        files.["/test/file2.pdf"] <- "world"

        let! hash1 = Classifier.computeSha256 fs "/test/file1.pdf"
        let! hash2 = Classifier.computeSha256 fs "/test/file2.pdf"

        Assert.True(hash1 <> hash2)
    }

// ─── Sidecar loading tests ──────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_TryLoadSidecar_WithMetaFile_ReturnsSome`` () =
    task {
        let fs, files, _, _ = inMemoryFileSystem ()
        let logger = Logging.silent

        files.["/archive/unclassified/test.pdf.meta.json"] <-
            """{"source_type":"email_attachment","account":"test","gmail_id":"msg1","sender":"bob@example.com","subject":"Test","original_name":"test.pdf","sha256":"abc"}"""

        let! result = Classifier.tryLoadSidecar fs logger "/archive/unclassified/test.pdf"

        Assert.True(result.IsSome)
        Assert.Equal(Some "bob@example.com", result.Value.Sender)
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_TryLoadSidecar_NoMetaFile_ReturnsNone`` () =
    task {
        let fs, _, _, _ = inMemoryFileSystem ()
        let logger = Logging.silent

        let! result = Classifier.tryLoadSidecar fs logger "/archive/unclassified/test.pdf"
        Assert.True(result.IsNone)
    }

// ─── Full processFile tests ──────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_ProcessFile_ClassifiesAndMovesFile`` () =
    task {
        let fs, files, _, dirs = inMemoryFileSystem ()
        let db = testDb ()
        let logger = Logging.silent
        let clock = testClock ()
        let rules = testRulesEngine ()

        let archiveDir = "/archive"
        dirs.[archiveDir] <- true
        let srcPath = Path.Combine(archiveDir, "unclassified", "Invoice-March.pdf")
        files.[srcPath] <- "PDF content here"

        try
            let! result =
                Classifier.processFile fs db logger clock rules archiveDir srcPath

            Assert.True(Result.isOk result)

            // File should have been moved to invoices/
            Assert.False(files.ContainsKey(srcPath))
            let destPath = Path.Combine(archiveDir, "invoices", "Invoice-March.pdf")
            let keysStr = String.Join("; ", files.Keys)
            Assert.True(files.ContainsKey(destPath), $"Expected file at {destPath}. Keys: {keysStr}")

            // Document should be in the database
            let! count =
                db.execScalar "SELECT COUNT(*) FROM documents WHERE category = 'invoices'" []

            Assert.True((count :?> int64) > 0L)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_ProcessFile_WithSidecar_UsesMetadataForClassification`` () =
    task {
        let fs, files, _, dirs = inMemoryFileSystem ()
        let db = testDb ()
        let logger = Logging.silent
        let clock = testClock ()
        let rules = testRulesEngine ()

        let archiveDir = "/archive"
        dirs.[archiveDir] <- true
        let srcPath = Path.Combine(archiveDir, "unclassified", "document.pdf")
        let metaPath = srcPath + ".meta.json"
        files.[srcPath] <- "PDF content"

        files.[metaPath] <-
            """{"source_type":"email_attachment","account":"john","gmail_id":"msg1","sender":"bob@plumbing.com.au","subject":"March invoice","original_name":"document.pdf","sha256":"abc"}"""

        try
            let! result =
                Classifier.processFile fs db logger clock rules archiveDir srcPath

            Assert.True(Result.isOk result, $"Expected Ok but got: {result}")

            // Domain rule should have classified to trades (plumbing.com.au)
            let destPath = Path.Combine(archiveDir, "trades", "document.pdf")
            let keysStr = String.Join("; ", files.Keys)
            Assert.True(files.ContainsKey(destPath), $"Expected file at {destPath}. Keys: {keysStr}")

            // Sidecar should have been cleaned up
            Assert.False(files.ContainsKey(metaPath))
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_ProcessFile_DuplicateHash_SkipsFile`` () =
    task {
        let fs, files, _, dirs = inMemoryFileSystem ()
        let db = testDb ()
        let logger = Logging.silent
        let clock = testClock ()
        let rules = testRulesEngine ()

        let archiveDir = "/archive"
        dirs.[archiveDir] <- true

        // Add a file with known content
        let content = "duplicate content"
        let srcPath = Path.Combine(archiveDir, "unclassified", "dup.pdf")
        files.[srcPath] <- content

        // Pre-insert a document with the same hash
        let! hash = Classifier.computeSha256 fs srcPath

        let! _ =
            db.execNonQuery
                """INSERT INTO documents (source_type, saved_path, category, sha256)
                   VALUES ('manual_drop', 'invoices/existing.pdf', 'invoices', @sha)"""
                [ ("@sha", Database.boxVal hash) ]

        try
            let! result =
                Classifier.processFile fs db logger clock rules archiveDir srcPath

            Assert.True(Result.isOk result)

            // File should have been deleted (not moved)
            Assert.False(files.ContainsKey(srcPath))

            // Should NOT have been moved to any category
            Assert.False(files.ContainsKey(Path.Combine(archiveDir, "invoices", "dup.pdf")))
            Assert.False(files.ContainsKey(Path.Combine(archiveDir, "unsorted", "dup.pdf")))
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_ProcessFile_MissingFile_ReturnsOk`` () =
    task {
        let fs, _, _, _ = inMemoryFileSystem ()
        let db = testDb ()
        let logger = Logging.silent
        let clock = testClock ()
        let rules = testRulesEngine ()

        try
            let! result =
                Classifier.processFile fs db logger clock rules "/archive" "/archive/unclassified/nonexistent.pdf"

            Assert.True(Result.isOk result)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_ProcessFile_UnmatchedFile_GoesToUnsorted`` () =
    task {
        let fs, files, _, dirs = inMemoryFileSystem ()
        let db = testDb ()
        let logger = Logging.silent
        let clock = testClock ()
        let rules = testRulesEngine ()

        let archiveDir = "/archive"
        dirs.[archiveDir] <- true
        let srcPath = Path.Combine(archiveDir, "unclassified", "random-file.pdf")
        files.[srcPath] <- "some content"

        try
            let! result =
                Classifier.processFile fs db logger clock rules archiveDir srcPath

            Assert.True(Result.isOk result)
            let destPath = Path.Combine(archiveDir, "unsorted", "random-file.pdf")
            let keysStr = String.Join("; ", files.Keys)
            Assert.True(files.ContainsKey(destPath), $"Expected file at {destPath}. Keys: {keysStr}")
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_ProcessFile_InsertsDocumentRecord`` () =
    task {
        let fs, files, _, dirs = inMemoryFileSystem ()
        let db = testDb ()
        let logger = Logging.silent
        let clock = testClock ()
        let rules = testRulesEngine ()

        let archiveDir = "/archive"
        dirs.[archiveDir] <- true
        let srcPath = Path.Combine(archiveDir, "unclassified", "Invoice-Test.pdf")
        files.[srcPath] <- "invoice content"

        try
            let! _ =
                Classifier.processFile fs db logger clock rules archiveDir srcPath

            let! countResult =
                db.execScalar "SELECT COUNT(*) FROM documents WHERE original_name = 'Invoice-Test.pdf'" []

            Assert.Equal(1L, countResult :?> int64)

            let! catResult =
                db.execScalar
                    "SELECT category FROM documents WHERE original_name = 'Invoice-Test.pdf'"
                    []

            match catResult with
            | null -> failwith "Expected a category value"
            | v -> Assert.Equal("invoices", v :?> string)
        finally
            db.dispose ()
    }
