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

/// Read a scalar as int64, treating "no rows" as zero.
let private scalarInt64
    (db: Algebra.Database)
    (sql: string)
    (ps: (string * obj) list)
    : Task<int64> =
    task {
        let! value = db.execScalar sql ps
        return match value with null -> 0L | v -> v :?> int64
    }

/// Read a scalar that must be text.
let private scalarText (db: Algebra.Database) (sql: string) : Task<string> =
    task {
        let! value = db.execScalar sql []
        return
            match value with
            | :? string as text -> text
            | _ -> failwith $"Expected text from: {sql}"
    }

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

// ─── v8 to current upgrade ────────────────────────────────────────────
// v8 is the last version any archive actually ran - 9, 10 and 11 were never
// merged - so v8 to Database.CurrentSchemaVersion is the only upgrade a real
// database performs.

/// Tables that exist only from v9 onward; a v8 archive must gain them all.
let private addedTables =
    [ "reflow_operations"
      "reflow_operation_stages"
      "document_generations"
      "learned_pattern_evidence"
      "stage_publications"
      "artifact_folder_revisions"
      "pipeline_stage_attempts" ]

/// Indexes the upgrade adds, including the partial unique index that the
/// dead-letter collapse has to make satisfiable.
let private addedIndexes =
    [ "idx_dead_letters_active"
      "idx_reflow_ops_active_apply"
      "idx_learned_evidence_pattern"
      "idx_stage_publications_stage"
      "idx_pipeline_stage_attempts_stage" ]

/// Representative preserved columns of the document v8 finished processing.
let private documentFingerprintSql =
    """SELECT category || '|' || sha256 || '|' || saved_path || '|' ||
              extracted_vendor || '|' || starred
       FROM documents WHERE id = 1"""

/// Document 1 completed 'embed' under v8; its ledger row must survive.
let private embedCompletionSql =
    "SELECT count(*) FROM stage_completions WHERE document_id = 1 AND stage_name = 'embed'"

/// Active (not dismissed) dead letters: six at v8, three once they collapse.
let private activeDeadLettersSql =
    "SELECT count(*) FROM dead_letters WHERE dismissed = 0"

/// documents_fts is external-content: a clobbered index would silently lose
/// rows the documents table still holds.
let private ftsProbeSql =
    "SELECT count(*) FROM documents_fts WHERE documents_fts MATCH 'vendor'"

/// Startup order used by Hermes.Service: core schema first, then the pipeline
/// framework tables (stage_completions, pipeline_stage_attempts, outputs).
let private runStartupSchema (db: Algebra.Database) : Task<unit> =
    task {
        match! db.initSchema () with
        | Error error -> failwith $"initSchema failed: {error}"
        | Ok () -> ()
        do! TestHelpers.initV5 db
    }

let private readV8Counts (db: Algebra.Database) : Task<TestHelpers.V8Counts> =
    task {
        let! documents = scalarInt64 db "SELECT count(*) FROM documents" []
        let! completions =
            scalarInt64 db "SELECT count(*) FROM stage_completions" []
        let! deadLetters =
            scalarInt64 db "SELECT count(*) FROM dead_letters" []
        let! active = scalarInt64 db activeDeadLettersSql []
        let counts: TestHelpers.V8Counts =
            { Documents = documents
              StageCompletions = completions
              DeadLetters = deadLetters
              ActiveDeadLetters = active }

        return counts
    }

let private assertTablesExist
    (db: Algebra.Database)
    (names: string list)
    : Task<unit> =
    task {
        for name in names do
            let! exists = db.tableExists name
            Assert.True(exists, $"Upgrade must create table {name}")
    }

let private assertIndexesExist
    (db: Algebra.Database)
    (names: string list)
    : Task<unit> =
    task {
        for name in names do
            let! count =
                scalarInt64 db
                    "SELECT count(*) FROM sqlite_master WHERE type='index' AND name=@n"
                    [ ("@n", Database.boxVal name) ]
            Assert.True(count > 0L, $"Upgrade must create index {name}")
    }

/// Everything a v8 archive must still hold after any schema initialisation.
let private assertV8DataIntact
    (fixture: TestHelpers.V8Fixture)
    : Task<unit> =
    task {
        let! counts = readV8Counts fixture.Db
        Assert.Equal<TestHelpers.V8Counts>(fixture.Counts, counts)
        let! fingerprint = scalarText fixture.Db documentFingerprintSql
        Assert.Equal(fixture.DocumentFingerprint, fingerprint)
        let! completed = scalarInt64 fixture.Db embedCompletionSql []
        Assert.Equal(1L, completed)
        let! indexed = scalarInt64 fixture.Db ftsProbeSql []
        Assert.Equal(1L, indexed)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_InitSchema_FromPopulatedV8_UpgradesAndPreservesData`` () =
    task {
        let fixture = TestHelpers.createV8Db ()
        try
            do! runStartupSchema fixture.Db
            let! version = fixture.Db.schemaVersion ()
            Assert.Equal(Database.CurrentSchemaVersion, version)
            do! assertTablesExist fixture.Db addedTables
            do! assertIndexesExist fixture.Db addedIndexes
            do! assertV8DataIntact fixture
        finally fixture.Db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_InitSchema_FromPopulatedV8_SecondRunChangesNothing`` () =
    task {
        let fixture = TestHelpers.createV8Db ()
        try
            do! runStartupSchema fixture.Db
            do! runStartupSchema fixture.Db
            do! assertV8DataIntact fixture
            let! version = fixture.Db.schemaVersion ()
            Assert.Equal(Database.CurrentSchemaVersion, version)
            // v8 history kept, the current version stamped exactly once.
            let! stamps =
                scalarInt64 fixture.Db "SELECT count(*) FROM schema_version" []
            Assert.Equal(2L, stamps)
        finally fixture.Db.dispose ()
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

/// Largest number of active dead letters left in any (doc_id, stage) group.
let private worstActiveGroupSql =
    """SELECT COALESCE(MAX(active), 0) FROM
         (SELECT count(*) AS active FROM dead_letters
          WHERE dismissed = 0 GROUP BY doc_id, stage)"""

/// The surviving active row of the (doc 3, 'extract') duplicate group.
let private survivingExtractErrorSql =
    """SELECT error FROM dead_letters
       WHERE doc_id = 3 AND stage = 'extract' AND dismissed = 0"""

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_InitSchema_DeduplicatesActiveDeadLettersBeforeUniqueIndex`` () =
    task {
        // v8 has no unique index over active dead letters, so a real archive
        // reaches the upgrade holding duplicates for a (doc_id, stage).
        let fixture = TestHelpers.createV8Db ()
        try
            let! before =
                scalarInt64 fixture.Db activeDeadLettersSql []
            Assert.Equal(fixture.ActiveDeadLettersAtV8, before)
            // Fails outright if the collapse does not run before the index.
            do! runStartupSchema fixture.Db
            let! worstGroup =
                scalarInt64 fixture.Db worstActiveGroupSql []
            Assert.Equal(1L, worstGroup)
            // The survivor is deterministically the newest failure.
            let! survivor =
                scalarText fixture.Db survivingExtractErrorSql
            Assert.Equal(fixture.SurvivingExtractError, survivor)
            // Losers are dismissed, never deleted: the evidence survives.
            do! assertV8DataIntact fixture
        finally
            fixture.Db.dispose ()
    }
