module Hermes.Tests.LocalReindexCoreTests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Xunit
open Hermes.Core
open Hermes.Tests.LocalReindexFixtures

let private asSchema15 fixture =
    withDatabase fixture (fun db ->
        task {
            let! _ =
                db.execNonQuery
                    """DROP TABLE document_content_fts;
                       DELETE FROM schema_version WHERE version > 15;
                       INSERT OR IGNORE INTO schema_version(version) VALUES (15);""" []
            return ()
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Schema15 preview performs no migration while explicit apply creates the additive index`` () =
    withFixture (fun fixture ->
        task {
            do! asSchema15 fixture
            let! before = metadata fixture
            let preview = runHandler fixture [ fixture.Ids.Extracted ] []
            Assert.Equal(0, preview.ExitCode)
            Assert.Equal(15, preview.Json.GetProperty("schemaVersion").GetInt32())
            let! after = metadata fixture
            Assert.Equal<Map<string, Map<string, obj> list>>(before, after)
            let applied = runHandler fixture [ fixture.Ids.Extracted ] [ "--apply" ]
            Assert.Equal(0, applied.ExitCode)
            Assert.Equal(Database.CurrentSchemaVersion, applied.Json.GetProperty("schemaVersion").GetInt32())
            do! assertIndexed fixture "cliextractedsapphiremarker" fixture.Ids.Extracted
        })

let private openReadOnly (fixture: Fixture) =
    let settings =
        SqliteConnectionStringBuilder(
            DataSource = Path.Combine(fixture.Archive, "db.sqlite"),
            Mode = SqliteOpenMode.ReadOnly, Pooling = false)
    let connection = new SqliteConnection(settings.ConnectionString)
    connection.Open()
    Database.fromReadOnlyConnectionForArchive fixture.Archive connection

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Read-only archive algebra refuses schema initialization and SQLite writes`` () =
    withFixture (fun fixture ->
        task {
            let db = openReadOnly fixture
            try
                let! schema = db.initSchema ()
                Assert.True(Result.isError schema)
                let! _ =
                    Assert.ThrowsAsync<SqliteException>(fun () ->
                        db.execNonQuery "DELETE FROM documents" [] :> Task)
                let! result =
                    FileContentReindex.inspect db Interpreters.realFileSystem TestHelpers.silentLogger
                        fixture.Archive [ fixture.Ids.Body ]
                Assert.Equal<int64 list>([ fixture.Ids.Body ], (result |> Result.defaultWith failwith).Indexable)
            finally db.dispose ()
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Read-only factory rejects a writable connection`` () =
    withFixture (fun fixture ->
        task {
            use connection = new SqliteConnection("Data Source=:memory:")
            connection.Open()
            Assert.Throws<ArgumentException>(fun () ->
                Database.fromReadOnlyConnectionForArchive fixture.Archive connection |> ignore)
            |> ignore
        })

let private attemptDuringRead (db: Algebra.Database) documentId =
    let original = Interpreters.realFileSystem
    { original with
        readAllBytes = fun path ->
            task {
                let! _ =
                    db.execNonQuery
                        """INSERT INTO pipeline_stage_attempts(document_id,stage_name,attempt_token)
                           VALUES (@id,'extract','fedcba9876543210fedcba9876543210')"""
                        [ "@id", Database.boxVal documentId ]
                return! original.readAllBytes path
            } }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``A lease acquired during file reading prevents index publication`` () =
    withFixture (fun fixture ->
        withDatabase fixture (fun db ->
            task {
                let! result =
                    FileContentReindex.rebuild db (attemptDuringRead db fixture.Ids.Body)
                        TestHelpers.silentLogger fixture.Archive [ fixture.Ids.Body ]
                Assert.True(Result.isError result)
                let! value = db.execScalar "SELECT COUNT(*) FROM document_content_fts" []
                Assert.Equal(0L, Assert.IsType<int64>(value))
            }))

let private generationDuringRead (db: Algebra.Database) documentId =
    let original = Interpreters.realFileSystem
    { original with
        readAllBytes = fun path ->
            task {
                let! _ =
                    db.execNonQuery
                        """INSERT INTO document_generations(document_id,generation)
                           VALUES (@id,1)"""
                        [ "@id", Database.boxVal documentId ]
                return! original.readAllBytes path
            } }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Dry run rejects a generation change during its file read`` () =
    withFixture (fun fixture ->
        withDatabase fixture (fun db ->
            task {
                let! result =
                    FileContentReindex.inspect db (generationDuringRead db fixture.Ids.Body)
                        TestHelpers.silentLogger fixture.Archive [ fixture.Ids.Body ]
                Assert.True(Result.isError result)
                let! value = db.execScalar "SELECT COUNT(*) FROM document_content_fts" []
                Assert.Equal(0L, Assert.IsType<int64>(value))
            }))
