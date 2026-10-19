module Hermes.Tests.ComprehensionProvenanceSchemaTests

open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture
open Hermes.Tests.RetrievalHttpFixtures
open Hermes.Tests.RetrievalRaceFixtures
open Hermes.Tests.ReembedReadFixtures

let private history (server: Server) =
    server.Db.execReader
        """SELECT d.*,p.stage_name,p.generation,p.response_json,p.published_at
           FROM documents d JOIN stage_publications p ON p.document_id=d.id
           ORDER BY d.id,p.stage_name,p.generation"""
        []

let private initialize (server: Server) =
    task {
        let! result = server.Db.initSchema ()
        Assert.True(Result.isOk result, sprintf "%A" result)
    }

let private installSchema16 server =
    executeSql server
        """DROP TABLE comprehension_provenance;
           DELETE FROM schema_version WHERE version>16;
           INSERT OR IGNORE INTO schema_version(version) VALUES (16);"""
        []

let private upgrade server =
    task {
        let! expected = seedAt server 7L "upgrade" canonical
        let! before = history server
        let! files = FileKeywordFixtures.fileHashes server
        do! installSchema16 server
        do! initialize server
        let! first = checkpoint server
        do! initialize server
        let! second = checkpoint server
        let! after = history server
        let! version = server.Db.schemaVersion ()
        Assert.Equal(17, version)
        Assert.Equal<Map<string, obj> list>(before, after)
        Assert.Equal<Checkpoint>(first, second)
        Assert.Equal<Map<string, string>>(files, second.FileHashes)
        do! verifyBoth server expected
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Schema17 upgrades schema16 atomically and idempotently without rewriting sources or history`` () =
    withServer None upgrade

let private rejectCarry server =
    executeSql server
        """CREATE TRIGGER reject_comprehension_carry
           BEFORE INSERT ON comprehension_provenance
           BEGIN SELECT RAISE(ABORT,'synthetic provenance failure'); END;"""
        []

let private rollback server =
    task {
        let! expected = seedAt server 7L "rollback" canonical
        do! rejectCarry server
        let! before = checkpoint server
        let! result =
            Reflow.request server.Db TestHelpers.silentLogger (TestHelpers.standardV5Dag ())
                expected.DocumentId Reflow.Reembed Reflow.Apply
        Assert.True(Result.isError result)
        let! after = checkpoint server
        Assert.Equal<Checkpoint>(before, after)
        do! verifyBoth server expected
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Carry failure rolls back generation invalidation operation audit and provenance together`` () =
    withServer None rollback
