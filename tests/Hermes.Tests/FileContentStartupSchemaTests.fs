module Hermes.Tests.FileContentStartupSchemaTests

open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.FileContentStartupFixtures

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Fresh Database initialization installs the ledger before its content trigger without PipelineV5`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            do! initializeTwice db
            do! assertPrerequisites db
            let! stamps = scalar db "SELECT COUNT(*) FROM schema_version" []
            Assert.Equal(1L, stamps)
            let! version = db.schemaVersion ()
            Assert.Equal(Database.CurrentSchemaVersion, version)
            Assert.Equal<string>(
                StageCompletionSchema.statements,
                PipelineV5.coreSchema |> Array.take StageCompletionSchema.statements.Length)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database and pipeline initialization preserve the same ledger and functional invalidation trigger`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            do! initialize db
            do! seedTriggerRows db
            let tables = [ "documents"; "stage_completions"; "document_content_fts"; "schema_version" ]
            let! before = snapshot db tables
            do! PipelineV5.initSchema db []
            do! initializeTwice db
            let! after = snapshot db tables
            Assert.Equal<Snapshot>(before, after)
            do! execute db "DELETE FROM stage_completions WHERE stage_name='extract'" []
            let! generated = scalar db "SELECT COUNT(*) FROM document_content_fts WHERE rowid=2" []
            let! body = scalar db "SELECT COUNT(*) FROM document_content_fts WHERE rowid=1 AND kind='source'" []
            Assert.Equal(0L, generated)
            Assert.Equal(1L, body)
        finally db.dispose ()
    }

[<Theory>]
[<InlineData(8)>]
[<InlineData(12)>]
[<InlineData(14)>]
[<InlineData(15)>]
[<Trait("Category", "Integration")>]
let ``Version-stamped Database-only startup gains its missing ledger without losing metadata or history`` version =
    task {
        let db = TestHelpers.createRawDb ()
        try
            do! initialize db
            do! seedMetadata db
            do! removePipelinePrerequisite db version
            let! before = snapshot db preservedTables
            do! initializeTwice db
            do! assertPrerequisites db
            let! after = snapshot db preservedTables
            Assert.Equal<Snapshot>(before, after)
            Assert.NotEmpty(after.MetadataFts)
            do! assertHistory db version
        finally db.dispose ()
    }

let private assertV8DeadLetters (fixture: TestHelpers.V8Fixture) =
    task {
        let! total = scalar fixture.Db "SELECT COUNT(*) FROM dead_letters" []
        let! active = scalar fixture.Db "SELECT COUNT(*) FROM dead_letters WHERE dismissed=0" []
        Assert.Equal(fixture.Counts.DeadLetters, total)
        Assert.Equal(fixture.Counts.ActiveDeadLetters, active)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Populated v8 upgrades through Database alone preserving its existing ledger metadata FTS and stamps`` () =
    task {
        let fixture = TestHelpers.createV8Db ()
        try
            let tables = [ "documents"; "messages"; "stage_completions" ]
            let! before = snapshot fixture.Db tables
            do! initializeTwice fixture.Db
            do! assertPrerequisites fixture.Db
            let! after = snapshot fixture.Db tables
            Assert.Equal<Snapshot>(before, after)
            Assert.NotEmpty(after.MetadataFts)
            do! assertV8DeadLetters fixture
            let! stamps = scalar fixture.Db "SELECT COUNT(*) FROM schema_version" []
            Assert.Equal(2L, stamps)
        finally fixture.Db.dispose ()
    }
