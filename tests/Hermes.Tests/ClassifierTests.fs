module Hermes.Tests.ClassifierTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open Hermes.Core

// ─── Test helpers ────────────────────────────────────────────────────

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
        ({ classify = fun sidecar filename -> Rules.classifyWithRules rules defaultCat sidecar filename
           reload = fun () -> task { return Ok() } } : Algebra.RulesEngine)
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
        let db = TestHelpers.createDb ()

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
        let db = TestHelpers.createDb ()

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
        let db = TestHelpers.createDb ()

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
        let m = TestHelpers.memFs ()
        m.Files.["/test/file.pdf"] <- "hello world"

        let! hash1 = Classifier.computeSha256 m.Fs "/test/file.pdf"
        let! hash2 = Classifier.computeSha256 m.Fs "/test/file.pdf"

        Assert.Equal(hash1, hash2)
        Assert.Equal(64, hash1.Length) // SHA256 = 32 bytes = 64 hex chars
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_ComputeSha256_DifferentContent_DifferentHash`` () =
    task {
        let m = TestHelpers.memFs ()
        m.Files.["/test/file1.pdf"] <- "hello"
        m.Files.["/test/file2.pdf"] <- "world"

        let! hash1 = Classifier.computeSha256 m.Fs "/test/file1.pdf"
        let! hash2 = Classifier.computeSha256 m.Fs "/test/file2.pdf"

        Assert.True(hash1 <> hash2)
    }

// ─── Sidecar loading tests ──────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_TryLoadSidecar_WithMetaFile_ReturnsSome`` () =
    task {
        let m = TestHelpers.memFs ()
        let logger = Logging.silent

        m.Files.["/archive/unclassified/test.pdf.meta.json"] <-
            """{"source_type":"email_attachment","account":"test","gmail_id":"msg1","sender":"bob@example.com","subject":"Test","original_name":"test.pdf","sha256":"abc"}"""

        let! result = Classifier.tryLoadSidecar m.Fs logger "/archive/unclassified/test.pdf"

        Assert.True(result.IsSome)
        Assert.Equal(Some "bob@example.com", result.Value.Sender)
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_TryLoadSidecar_NoMetaFile_ReturnsNone`` () =
    task {
        let m = TestHelpers.memFs ()
        let logger = Logging.silent

        let! result = Classifier.tryLoadSidecar m.Fs logger "/archive/unclassified/test.pdf"
        Assert.True(result.IsNone)
    }

// ─── Full processFile tests ──────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_ProcessFile_ClassifiesAndMovesFile`` () =
    task {
        let m = TestHelpers.memFs ()
        let db = TestHelpers.createDb ()
        let logger = Logging.silent
        let clock = TestHelpers.defaultClock
        let rules = testRulesEngine ()

        let archiveDir = "/archive"
        m.Dirs.[archiveDir] <- true
        let srcPath = Path.Combine(archiveDir, "unclassified", "Invoice-March.pdf")
        m.Files.[srcPath] <- "PDF content here"

        try
            let! result =
                Classifier.processFile m.Fs db logger clock rules archiveDir srcPath

            Assert.True(Result.isOk result)

            // File should have been moved to invoices/
            Assert.False(m.Files.ContainsKey(srcPath))
            let destPath = Path.Combine(archiveDir, "invoices", "Invoice-March.pdf")
            let keysStr = String.Join("; ", m.Files.Keys)
            Assert.True(m.Files.ContainsKey(destPath), $"Expected file at {destPath}. Keys: {keysStr}")

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
        let m = TestHelpers.memFs ()
        let db = TestHelpers.createDb ()
        let logger = Logging.silent
        let clock = TestHelpers.defaultClock
        let rules = testRulesEngine ()

        let archiveDir = "/archive"
        m.Dirs.[archiveDir] <- true
        let srcPath = Path.Combine(archiveDir, "unclassified", "document.pdf")
        let metaPath = srcPath + ".meta.json"
        m.Files.[srcPath] <- "PDF content"

        m.Files.[metaPath] <-
            """{"source_type":"email_attachment","account":"john","gmail_id":"msg1","sender":"bob@plumbing.com.au","subject":"March invoice","original_name":"document.pdf","sha256":"abc"}"""

        try
            let! result =
                Classifier.processFile m.Fs db logger clock rules archiveDir srcPath

            Assert.True(Result.isOk result, $"Expected Ok but got: {result}")

            // Domain rule should have classified to trades (plumbing.com.au)
            let destPath = Path.Combine(archiveDir, "trades", "document.pdf")
            let keysStr = String.Join("; ", m.Files.Keys)
            Assert.True(m.Files.ContainsKey(destPath), $"Expected file at {destPath}. Keys: {keysStr}")

            // Sidecar should have been cleaned up
            Assert.False(m.Files.ContainsKey(metaPath))
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_ProcessFile_DuplicateHash_SkipsFile`` () =
    task {
        let m = TestHelpers.memFs ()
        let db = TestHelpers.createDb ()
        let logger = Logging.silent
        let clock = TestHelpers.defaultClock
        let rules = testRulesEngine ()

        let archiveDir = "/archive"
        m.Dirs.[archiveDir] <- true

        // Add a file with known content
        let content = "duplicate content"
        let srcPath = Path.Combine(archiveDir, "unclassified", "dup.pdf")
        m.Files.[srcPath] <- content

        // Pre-insert a document with the same hash
        let! hash = Classifier.computeSha256 m.Fs srcPath

        let! _ =
            db.execNonQuery
                """INSERT INTO documents (source_type, saved_path, category, sha256)
                   VALUES ('manual_drop', 'invoices/existing.pdf', 'invoices', @sha)"""
                [ ("@sha", Database.boxVal hash) ]

        try
            let! result =
                Classifier.processFile m.Fs db logger clock rules archiveDir srcPath

            Assert.True(Result.isOk result)

            // File should have been deleted (not moved)
            Assert.False(m.Files.ContainsKey(srcPath))

            // Should NOT have been moved to any category
            Assert.False(m.Files.ContainsKey(Path.Combine(archiveDir, "invoices", "dup.pdf")))
            Assert.False(m.Files.ContainsKey(Path.Combine(archiveDir, "unsorted", "dup.pdf")))
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_ProcessFile_MissingFile_ReturnsOk`` () =
    task {
        let m = TestHelpers.memFs ()
        let db = TestHelpers.createDb ()
        let logger = Logging.silent
        let clock = TestHelpers.defaultClock
        let rules = testRulesEngine ()

        try
            let! result =
                Classifier.processFile m.Fs db logger clock rules "/archive" "/archive/unclassified/nonexistent.pdf"

            Assert.True(Result.isOk result)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_ProcessFile_UnmatchedFile_GoesToUnsorted`` () =
    task {
        let m = TestHelpers.memFs ()
        let db = TestHelpers.createDb ()
        let logger = Logging.silent
        let clock = TestHelpers.defaultClock
        let rules = testRulesEngine ()

        let archiveDir = "/archive"
        m.Dirs.[archiveDir] <- true
        let srcPath = Path.Combine(archiveDir, "unclassified", "random-file.pdf")
        m.Files.[srcPath] <- "some content"

        try
            let! result =
                Classifier.processFile m.Fs db logger clock rules archiveDir srcPath

            Assert.True(Result.isOk result)
            let destPath = Path.Combine(archiveDir, "unsorted", "random-file.pdf")
            let keysStr = String.Join("; ", m.Files.Keys)
            Assert.True(m.Files.ContainsKey(destPath), $"Expected file at {destPath}. Keys: {keysStr}")
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_ProcessFile_InsertsDocumentRecord`` () =
    task {
        let m = TestHelpers.memFs ()
        let db = TestHelpers.createDb ()
        let logger = Logging.silent
        let clock = TestHelpers.defaultClock
        let rules = testRulesEngine ()

        let archiveDir = "/archive"
        m.Dirs.[archiveDir] <- true
        let srcPath = Path.Combine(archiveDir, "unclassified", "Invoice-Test.pdf")
        m.Files.[srcPath] <- "invoice content"

        try
            let! _ =
                Classifier.processFile m.Fs db logger clock rules archiveDir srcPath

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
