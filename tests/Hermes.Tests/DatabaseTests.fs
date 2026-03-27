module Hermes.Tests.DatabaseTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open Hermes.Core

// ─── Helpers ─────────────────────────────────────────────────────────

/// Create a temporary in-memory SQLite database algebra.
let createTestDb () =
    let conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:")
    conn.Open()

    use pragma = conn.CreateCommand()
    pragma.CommandText <- "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;"
    pragma.ExecuteNonQuery() |> ignore

    Database.fromConnection conn

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
[<Trait("Category", "Unit")>]
let ``Database_InitSchema_CreatesAllTables`` () =
    task {
        let db = createTestDb ()

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
[<Trait("Category", "Unit")>]
let ``Database_InitSchema_SetsSchemaVersion`` () =
    task {
        let db = createTestDb ()

        try
            let! _ = db.initSchema ()
            let! version = db.schemaVersion ()
            Assert.Equal(Database.CurrentSchemaVersion, version)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Database_InitSchema_IsIdempotent`` () =
    task {
        let db = createTestDb ()

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
[<Trait("Category", "Unit")>]
let ``Database_SchemaVersion_BeforeInit_ReturnsZero`` () =
    task {
        let db = createTestDb ()

        try
            let! version = db.schemaVersion ()
            Assert.Equal(0, version)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Database_TableExists_NonexistentTable_ReturnsFalse`` () =
    task {
        let db = createTestDb ()

        try
            let! exists = db.tableExists "nonexistent_table"
            Assert.False(exists)
        finally
            db.dispose ()
    }

// ─── FTS5 tests ──────────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Database_FTS5_InsertTrigger_PopulatesFtsOnInsert`` () =
    task {
        let db = createTestDb ()

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
[<Trait("Category", "Unit")>]
let ``Database_FTS5_SearchByVendor_FindsDocument`` () =
    task {
        let db = createTestDb ()

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
[<Trait("Category", "Unit")>]
let ``Database_InitSchema_CreatesAllIndexes`` () =
    task {
        let db = createTestDb ()

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
