module Hermes.Tests.GenerationValidationRegressionTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.CoreRegressionSupport

let private originalArtifact = """{"owner":"original"}"""
let private canonicalArtifact = """{"owner":"canonical"}"""
let private preparationError = "current artifact could not be merged"
let private folderPath = Path.Combine(archiveDir, "local")
let private artifactPath = Path.Combine(folderPath, "thread.comprehension.json")

let private folder () : PublicationFence.ArtifactFolder =
    PublicationFence.ArtifactFolder.tryFromMetadata archiveDir (savedPath 1L) None
    |> Option.defaultWith (fun () -> failwith "Fixture artifact folder was not resolved")

let private bumpIn (scope: Algebra.TransactionScope) : Task<Result<unit, string>> =
    task {
        let! _ = Generation.bump scope 1L
        return Ok ()
    }

let private advanceGeneration (db: Algebra.Database) : Task<unit> =
    task {
        let! result = db.inTransaction bumpIn
        Assert.Equal<Result<unit, string>>(Ok (), result)
    }

let private markCurrent (fixture: Fixture) : Task<int> =
    fixture.Db.execNonQuery
        "INSERT INTO stage_completions(document_id,stage_name) VALUES (1,'extract')" []

let private outputCurrent (scope: Algebra.TransactionScope) : Task<bool> =
    task {
        let! value =
            scope.execScalar
                "SELECT COUNT(*) FROM stage_completions WHERE document_id = 1 AND stage_name = 'extract'" []
        return scalarInt64 value = 1L
    }

let private readThenAdvance (fixture: Fixture) (changed: bool) () : Task<string option> =
    task {
        let! content = fixture.Memory.Fs.readAllText artifactPath
        if changed then do! advanceGeneration fixture.Db
        return Some content
    }

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("Category", "Integration")>]
let ``Generation stable reads reject bytes observed across a generation change`` changed =
    task {
        use fixture = fixture ()
        do! seed fixture 1L "received"
        fixture.Memory.Put artifactPath originalArtifact
        let! result =
            Generation.readStable fixture.Db 1L (readThenAdvance fixture changed)
            |> fun pending -> pending.WaitAsync(timeout)
        let expected = if changed then None else Some originalArtifact
        Assert.Equal<string option>(expected, result)
        let! current = Generation.current fixture.Db 1L
        Assert.Equal((if changed then 1L else 0L), current.Value)
        Assert.Equal<string option>(Some originalArtifact, fixture.Memory.Get artifactPath)
        let! document = row fixture.Db 1L
        assertOriginal fixture document
    }

let private prepare (events: ConcurrentQueue<string>) () : Task<Result<string, string>> =
    events.Enqueue "prepare"
    Task.FromResult(Ok canonicalArtifact)

let private republish (fixture: Fixture) (token: Generation.Token) (artifact: ArtifactRevision.Token)
    (prepareContent: unit -> Task<Result<string, string>>) : Task<Result<Generation.Publication<string>, string>> =
    Generation.republishArtifact fixture.Db token (folder ()) artifact outputCurrent prepareContent
        (ArchiveWriter.writeComprehension
            (SourceFiles.metadataFileSystem fixture.Db fixture.Memory.Fs) folderPath)
    |> fun pending -> pending.WaitAsync(timeout)

let private prepareRejectedState (fixture: Fixture) (staleGeneration: bool) : Task<unit> =
    task {
        do! seed fixture 1L "received"
        fixture.Memory.Put artifactPath originalArtifact
        if staleGeneration then
            let! _ = markCurrent fixture
            do! advanceGeneration fixture.Db
    }

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("Category", "Integration")>]
let ``Generation republication checks both the document generation and current output before preparing bytes`` staleGeneration =
    task {
        use fixture = fixture ()
        do! prepareRejectedState fixture staleGeneration
        let token : Generation.Token = { DocumentId = 1L; Value = 0L }
        let! artifact = ArtifactRevision.current fixture.Db (folder ())
        let events = ConcurrentQueue<string>()
        let! result = republish fixture token artifact (prepare events)
        Assert.Equal<Result<Generation.Publication<string>, string>>(Ok Generation.Superseded, result)
        Assert.Empty(events)
        Assert.Equal<string option>(Some originalArtifact, fixture.Memory.Get artifactPath)
        let! unchanged = ArtifactRevision.current fixture.Db (folder ())
        Assert.Equal(artifact.Value, unchanged.Value)
        let! document = row fixture.Db 1L
        assertOriginal fixture document
    }

let private retryAndRejectOldRevision (fixture: Fixture) (token: Generation.Token)
    (artifact: ArtifactRevision.Token) : Task<unit> =
    task {
        let events = ConcurrentQueue<string>()
        let! retried = republish fixture token artifact (prepare events)
        Assert.Equal<Result<Generation.Publication<string>, string>>(Ok (Generation.Published canonicalArtifact), retried)
        Assert.Equal("prepare", Assert.Single<string>(events))
        let! advanced = ArtifactRevision.current fixture.Db (folder ())
        Assert.Equal(artifact.Value + 1L, advanced.Value)
        let! stale =
            republish fixture token artifact
                (fun () -> Task.FromResult<Result<string, string>>(Error preparationError))
        Assert.Equal<Result<Generation.Publication<string>, string>>(Ok Generation.Superseded, stale)
        Assert.Equal<string option>(Some canonicalArtifact, fixture.Memory.Get artifactPath)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Generation preparation errors preserve bytes and revision while allowing a later fenced retry`` () =
    task {
        use fixture = fixture ()
        do! seed fixture 1L "received"
        fixture.Memory.Put artifactPath originalArtifact
        let! _ = markCurrent fixture
        let! token = Generation.current fixture.Db 1L
        let! artifact = ArtifactRevision.current fixture.Db (folder ())
        let! result =
            republish fixture token artifact
                (fun () -> Task.FromResult<Result<string, string>>(Error preparationError))
        Assert.Equal<Result<Generation.Publication<string>, string>>(Error preparationError, result)
        Assert.Equal<string option>(Some originalArtifact, fixture.Memory.Get artifactPath)
        let! unchanged = ArtifactRevision.current fixture.Db (folder ())
        Assert.Equal(artifact.Value, unchanged.Value)
        do! retryAndRejectOldRevision fixture token artifact
        let! document = row fixture.Db 1L
        assertOriginal fixture document
    }
