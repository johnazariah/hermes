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

            let! hasLearnedEvidence =
                db.tableExists "learned_pattern_evidence"
            Assert.True(hasLearnedEvidence)

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
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_InitSchema_FromV11_AddsStagePublicationsIdempotently`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            // A database recorded at the previous version.
            let! _ =
                db.execNonQuery
                    "CREATE TABLE schema_version (version INTEGER PRIMARY KEY, applied_at TEXT DEFAULT (datetime('now')))"
                    []
            let! _ = db.execNonQuery "INSERT INTO schema_version(version) VALUES (11)" []
            match! db.initSchema () with
            | Error error -> failwith error
            | Ok () -> ()
            let! exists = db.tableExists "stage_publications"
            Assert.True(exists, "Migration must create stage_publications")
            let! version = db.schemaVersion ()
            Assert.Equal(Database.CurrentSchemaVersion, version)
            // Re-running the migration is a no-op.
            match! db.initSchema () with
            | Error error -> failwith error
            | Ok () -> ()
            let! again = db.schemaVersion ()
            Assert.Equal(Database.CurrentSchemaVersion, again)
        finally db.dispose ()
    }

let ``Database_SchemaVersion_BeforeInit_ReturnsZero_Original`` () =
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

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_InitSchema_MigratesFromV8_CreatesReflowSchema`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            let! _ =
                db.execNonQuery
                    "CREATE TABLE schema_version (version INTEGER PRIMARY KEY, applied_at TEXT DEFAULT (datetime('now')))"
                    []
            let! _ = db.execNonQuery "INSERT INTO schema_version(version) VALUES (8)" []
            let! first = db.initSchema ()
            let! second = db.initSchema ()
            Assert.True(Result.isOk first)
            Assert.True(Result.isOk second)
            let! version = db.schemaVersion ()
            Assert.Equal(Database.CurrentSchemaVersion, version)
            let! operations = db.tableExists "reflow_operations"
            let! stages = db.tableExists "reflow_operation_stages"
            let! generations = db.tableExists "document_generations"
            Assert.True(operations)
            Assert.True(stages)
            Assert.True(generations)
            let! index =
                db.execScalar
                    "SELECT count(*) FROM sqlite_master WHERE type='index' AND name='idx_reflow_ops_active_apply'"
                    []
            Assert.Equal(1L, index :?> int64)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_InTransaction_Error_RollsBack`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! result =
                db.inTransaction (fun tx ->
                    task {
                        let! _ =
                            tx.execNonQuery
                                """INSERT INTO documents (source_type,saved_path,category,sha256)
                                   VALUES ('manual_drop','rollback.pdf','unsorted','rollback')"""
                                []
                        return Error "forced rollback"
                    })
            Assert.True(Result.isError result)
            let! count = db.execScalar "SELECT count(*) FROM documents WHERE sha256='rollback'" []
            Assert.Equal(0L, count :?> int64)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_NormalCommand_WaitsForActiveTransactionOnSameConnection`` () =
    task {
        let db = TestHelpers.createDb ()
        let entered =
            TaskCompletionSource<unit>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        let release =
            TaskCompletionSource<unit>(
                TaskCreationOptions.RunContinuationsAsynchronously)

        try
            let transaction =
                db.inTransaction (fun _ ->
                    task {
                        entered.TrySetResult() |> ignore
                        do! release.Task
                        return Ok ()
                    })

            do! entered.Task
            let normalCommand = db.execScalar "SELECT 42" []
            do! Task.Delay(100)
            Assert.False(normalCommand.IsCompleted)

            release.TrySetResult() |> ignore
            let! transactionResult = transaction
            let! value = normalCommand
            Assert.True(Result.isOk transactionResult)
            Assert.Equal(42L, value :?> int64)
        finally
            release.TrySetResult() |> ignore
            db.dispose ()
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

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_InitSchema_DeduplicatesActiveDeadLettersBeforeUniqueIndex`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ =
                db.execNonQuery
                    "DROP INDEX IF EXISTS idx_dead_letters_active"
                    []
            let! _ =
                db.execNonQuery
                    """INSERT INTO dead_letters
                         (doc_id, stage, error, retryable, failed_at)
                       VALUES (42, 'embed', 'old', 1, datetime('now')),
                              (42, 'embed', 'new', 1, datetime('now'))"""
                    []
            let! result = db.initSchema ()
            Assert.True(Result.isOk result)
            let! active =
                db.execScalar
                    """SELECT count(*) FROM dead_letters
                       WHERE doc_id = 42 AND stage = 'embed' AND dismissed = 0"""
                    []
            let! dismissed =
                db.execScalar
                    """SELECT count(*) FROM dead_letters
                       WHERE doc_id = 42 AND stage = 'embed' AND dismissed = 1"""
                    []
            let! activeError =
                db.execScalar
                    """SELECT error FROM dead_letters
                       WHERE doc_id = 42 AND stage = 'embed' AND dismissed = 0"""
                    []
            Assert.Equal(1L, active :?> int64)
            Assert.Equal(1L, dismissed :?> int64)
            match activeError with
            | :? string as error -> Assert.Equal("new", error)
            | _ -> Assert.Fail("Expected active error to be a string")
        finally
            db.dispose ()
    }
