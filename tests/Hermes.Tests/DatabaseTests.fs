module Hermes.Tests.DatabaseTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open Hermes.Core

// ─── Helpers ─────────────────────────────────────────────────────────

/// Create a temporary file-based SQLite database algebra.
let createTempFileDb () =
    let dir = Path.Combine(Path.GetTempPath(), $"hermes-test-{Guid.NewGuid():N}")
    Directory.CreateDirectory(dir) |> ignore
    let dbPath = Path.Combine(dir, "db.sqlite")
    let db = Database.fromPath dbPath
    db, dir

let cleanupDir dir =
    try
        Directory.Delete(dir, true)
    with
    | _ -> ()

// ─── Schema initialisation tests ─────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_InitSchema_CreatesAllTables`` () =
    task {
        let db = TestHelpers.createRawDb ()

        try
            let! result = db.initSchema ()
            Assert.True(Result.isOk result)

            let! hasMessages = db.tableExists "messages"
            Assert.True(hasMessages)

            let! hasDocuments = db.tableExists "documents"
            Assert.True(hasDocuments)

            let! hasSyncState = db.tableExists "sync_state"
            Assert.True(hasSyncState)

            let! hasSchemaVersion = db.tableExists "schema_version"
            Assert.True(hasSchemaVersion)

            let! hasFts = db.tableExists "documents_fts"
            Assert.True(hasFts)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_InitSchema_SetsSchemaVersion`` () =
    task {
        let db = TestHelpers.createRawDb ()

        try
            let! _ = db.initSchema ()
            let! version = db.schemaVersion ()
            Assert.Equal(Database.CurrentSchemaVersion, version)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_InitSchema_IsIdempotent`` () =
    task {
        let db = TestHelpers.createRawDb ()

        try
            let! r1 = db.initSchema ()
            Assert.True(Result.isOk r1)

            let! r2 = db.initSchema ()
            Assert.True(Result.isOk r2)

            let! version = db.schemaVersion ()
            Assert.Equal(Database.CurrentSchemaVersion, version)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_SchemaVersion_BeforeInit_ReturnsZero`` () =
    task {
        let db = TestHelpers.createRawDb ()

        try
            let! version = db.schemaVersion ()
            Assert.Equal(0, version)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_TableExists_NonexistentTable_ReturnsFalse`` () =
    task {
        let db = TestHelpers.createRawDb ()

        try
            let! exists = db.tableExists "nonexistent_table"
            Assert.False(exists)
        finally
            db.dispose ()
    }

// ─── FTS5 tests ──────────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_FTS5_InsertTrigger_PopulatesFtsOnInsert`` () =
    task {
        let db = TestHelpers.createRawDb ()

        try
            let! _ = db.initSchema ()

            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (source_type, saved_path, category, sha256, sender, subject, original_name)
                       VALUES ('manual_drop', 'invoices/test.pdf', 'invoices', 'abc123', 'bob@example.com', 'Invoice #42', 'test.pdf')"""
                    []

            let! result =
                db.execScalar "SELECT COUNT(*) FROM documents_fts WHERE documents_fts MATCH 'invoice'" []

            Assert.True((result :?> int64) > 0L)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_FTS5_SearchByVendor_FindsDocument`` () =
    task {
        let db = TestHelpers.createRawDb ()

        try
            let! _ = db.initSchema ()

            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (source_type, saved_path, category, sha256, extracted_vendor)
                       VALUES ('manual_drop', 'invoices/plumber.pdf', 'invoices', 'def456', 'Bob Plumbing')"""
                    []

            let! result =
                db.execScalar "SELECT COUNT(*) FROM documents_fts WHERE documents_fts MATCH 'plumbing'" []

            Assert.True((result :?> int64) > 0L)
        finally
            db.dispose ()
    }

// ─── Archive initialisation tests ────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_InitArchive_CreatesDirectoriesAndDatabase`` () =
    task {
        let dir = Path.Combine(Path.GetTempPath(), $"hermes-test-{Guid.NewGuid():N}")

        try
            let fs = Interpreters.realFileSystem
            let! result = Database.initArchive fs dir

            match result with
            | Ok db ->
                try
                    Assert.True(Directory.Exists(Path.Combine(dir, "unclassified")))
                    Assert.True(Directory.Exists(Path.Combine(dir, "invoices")))
                    Assert.True(Directory.Exists(Path.Combine(dir, "unsorted")))
                    Assert.True(File.Exists(Path.Combine(dir, "db.sqlite")))

                    let! version = db.schemaVersion ()
                    Assert.Equal(Database.CurrentSchemaVersion, version)
                finally
                    db.dispose ()
            | Error e ->
                failwith $"Expected Ok, got Error: {e}"
        finally
            cleanupDir dir
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Database_FromPath_CreatesParentDirectories`` () =
    let dir = Path.Combine(Path.GetTempPath(), $"hermes-test-{Guid.NewGuid():N}", "nested")

    try
        let db = Database.fromPath (Path.Combine(dir, "db.sqlite"))

        try
            Assert.True(Directory.Exists(dir))
        finally
            db.dispose ()
    finally
        cleanupDir (Path.GetDirectoryName(dir) |> Option.ofObj |> Option.defaultValue dir)

// ─── Indexes verification ────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_InitSchema_CreatesAllIndexes`` () =
    task {
        let db = TestHelpers.createRawDb ()

        try
            let! _ = db.initSchema ()

            let expectedIndexes =
                [ "idx_msg_date"
                  "idx_msg_sender"
                  "idx_msg_account"
                  "idx_doc_category"
                  "idx_doc_date"
                  "idx_doc_sender"
                  "idx_doc_sha256"
                  "idx_doc_account"
                  "idx_doc_source"
                  "idx_doc_extracted"
                  "idx_doc_embedded" ]

            for idxName in expectedIndexes do
                let! result =
                    db.execScalar
                        "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=@n"
                        [ ("@n", Database.boxVal idxName) ]

                let count = match result with null -> 0L | v -> v :?> int64
                Assert.True(count > 0L, $"Index {idxName} should exist")
        finally
            db.dispose ()
    }

// ─── Schema migration v3 tests ──────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_InitSchema_V3_CreatesRemindersTable`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            let! _ = db.initSchema ()
            let! exists = db.tableExists "reminders"
            Assert.True(exists, "reminders table should exist")
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_InitSchema_V3_SyncStateHasBackfillColumns`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            let! _ = db.initSchema ()
            let! _ =
                db.execNonQuery
                    "INSERT INTO sync_state (account, backfill_scanned, backfill_completed) VALUES (@a, 10, 0)"
                    ([ ("@a", Database.boxVal "test") ])
            let! result =
                db.execScalar
                    "SELECT backfill_scanned FROM sync_state WHERE account = @a"
                    ([ ("@a", Database.boxVal "test") ])
            let scanned = match result with null -> -1L | v -> v :?> int64
            Assert.Equal(10L, scanned)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_InitSchema_V3_SchemaVersionIs3`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            let! _ = db.initSchema ()
            let! v = db.schemaVersion ()
            Assert.Equal(Database.CurrentSchemaVersion, v)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_InitSchema_V3_IdempotentRunTwice`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            let! r1 = db.initSchema ()
            Assert.True(Result.isOk r1)
            let! r2 = db.initSchema ()
            Assert.True(Result.isOk r2)
            let! v = db.schemaVersion ()
            Assert.Equal(Database.CurrentSchemaVersion, v)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_InitSchema_V3_ReminderIndexesExist`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            let! _ = db.initSchema ()
            for idx in [ "idx_reminder_status"; "idx_reminder_due"; "idx_reminder_doc" ] do
                let! result =
                    db.execScalar
                        "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=@n"
                        ([ ("@n", Database.boxVal idx) ])
                let count = match result with null -> 0L | v -> v :?> int64
                Assert.True(count > 0L, $"Index {idx} should exist")
        finally db.dispose ()
    }

// ─── Schema tests ────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_SchemaVersion_FreshDb_ReturnsLatest`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            let! _ = db.initSchema ()
            let! v = db.schemaVersion ()
            Assert.Equal(Database.CurrentSchemaVersion, v)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_InitSchema_Idempotent_CanRunTwice`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            let! r1 = db.initSchema ()
            Assert.True(Result.isOk r1)
            let! r2 = db.initSchema ()
            Assert.True(Result.isOk r2)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_FreshSchema_HasAllTables`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            let! _ = db.initSchema ()
            for table in [ "messages"; "documents"; "sync_state"; "reminders"; "activity_log"; "dead_letters"; "tags" ] do
                let! exists = db.tableExists table
                Assert.True(exists, $"Table {table} should exist")
        finally db.dispose ()
    }

let private loadDocumentsEpoch (db: Algebra.Database) =
    task {
        let! value =
            db.execScalar
                """SELECT epoch FROM documents_change_epoch
                   WHERE singleton = 1"""
                []

        return Assert.IsType<int64>(value)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Documents change epoch advances on every mutation`` () =
    task {
        let db = TestHelpers.createDb ()

        try
            let! initial = loadDocumentsEpoch db

            let! _ =
                db.execNonQuery
                    """INSERT INTO documents
                       (source_type, saved_path, category, sha256)
                       VALUES ('manual_drop', 'epoch.pdf', 'unsorted', 'epoch')"""
                    []

            let! afterInsert = loadDocumentsEpoch db

            let! _ =
                db.execNonQuery
                    """UPDATE documents
                       SET category = 'receipts'
                       WHERE saved_path = 'epoch.pdf'"""
                    []

            let! afterUpdate = loadDocumentsEpoch db

            let! _ =
                db.execNonQuery
                    "DELETE FROM documents WHERE saved_path = 'epoch.pdf'"
                    []

            let! afterDelete = loadDocumentsEpoch db
            Assert.Equal(initial + 1L, afterInsert)
            Assert.Equal(afterInsert + 1L, afterUpdate)
            Assert.Equal(afterUpdate + 1L, afterDelete)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Schema reinitialization preserves epoch and trigger behavior`` () =
    task {
        let db = TestHelpers.createDb ()

        try
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents
                       (source_type, saved_path, category, sha256)
                       VALUES ('manual_drop', 'preserved.pdf', 'unsorted', 'p')"""
                    []

            let! beforeReinitialize = loadDocumentsEpoch db
            let! initialized = db.initSchema ()
            Assert.True(Result.isOk initialized)
            let! afterReinitialize = loadDocumentsEpoch db
            Assert.Equal(beforeReinitialize, afterReinitialize)

            let! _ =
                db.execNonQuery
                    """UPDATE documents
                       SET category = 'receipts'
                       WHERE saved_path = 'preserved.pdf'"""
                    []

            let! afterMutation = loadDocumentsEpoch db
            Assert.Equal(afterReinitialize + 1L, afterMutation)
            let! version = db.schemaVersion ()
            Assert.Equal(Database.CurrentSchemaVersion, version)
        finally
            db.dispose ()
    }

// ─── canonicalArchivePath containment (B1 hardening) ──────────────────

[<Theory>]
[<InlineData("../escape.pdf")>]
[<InlineData("../../escape.pdf")>]
[<InlineData("a/../../escape.pdf")>]
[<Trait("Category", "Unit")>]
let ``Database_CanonicalArchivePath_RejectsRelativeEscape`` (savedPath: string) =
    match Database.canonicalArchivePath "/archive" savedPath with
    | Error message -> Assert.Contains("escapes the archive directory", message)
    | Ok canonical -> failwith $"Expected escape to be rejected, got {canonical.FullPath}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Database_CanonicalArchivePath_RejectsRootedEscape`` () =
    let outside = Path.Combine(Path.GetTempPath(), "hermes-canonical-escape", "outside.pdf")

    match Database.canonicalArchivePath "/archive" outside with
    | Error message -> Assert.Contains("escapes the archive directory", message)
    | Ok canonical -> failwith $"Expected rooted escape to be rejected, got {canonical.FullPath}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Database_CanonicalArchivePath_AllowsContainedDotDotSegments`` () =
    match Database.canonicalArchivePath "/archive" "owned/../owned/source.pdf" with
    | Ok canonical ->
        Assert.EndsWith("owned/source.pdf", canonical.FullPath.Replace('\\', '/'))
    | Error message -> failwith $"Expected contained path to be accepted, got {message}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Database_CanonicalArchivePath_ArchiveRootIsStableAndSelfContained`` () =
    let first = Database.canonicalArchivePath "/archive" "." |> Result.defaultWith failwith
    let second = Database.canonicalArchivePath "/archive" "./" |> Result.defaultWith failwith

    Assert.Equal(first.OwnershipKey, second.OwnershipKey)
