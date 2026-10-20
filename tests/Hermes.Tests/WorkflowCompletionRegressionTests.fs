module Hermes.Tests.WorkflowCompletionRegressionTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open Hermes.Core

let private deadline = TimeSpan.FromSeconds 10.0
let private stageName = "completion-regression"
let private extractedAt = TestHelpers.defaultClock.utcNow().ToString("o")

type private Fixture =
    { Db: Algebra.Database
      Input: Channel<Document.T>
      Output: Channel<Document.T>
      Events: ConcurrentQueue<string>
      Processed: ConcurrentQueue<int64>
      Resource: SemaphoreSlim option }
    interface IDisposable with
        member this.Dispose() =
            this.Resource |> Option.iter (fun resource -> resource.Dispose())
            this.Db.dispose ()

let private createFixture (useResource: bool) (initialPermits: int) : Fixture =
    { Db = TestHelpers.createDb ()
      Input = Channel.CreateUnbounded<Document.T>()
      Output = Channel.CreateUnbounded<Document.T>()
      Events = ConcurrentQueue<string>()
      Processed = ConcurrentQueue<int64>()
      Resource = if useResource then Some (new SemaphoreSlim(initialPermits, 1)) else None }

let private logger (fixture: Fixture) : Algebra.Logger =
    let record level message = fixture.Events.Enqueue $"{level} {message}"
    { info = record "INFO"; debug = record "DEBUG"
      warn = record "WARN"; error = record "ERROR" }

let private enrich (document: Document.T) : Document.T =
    document
    |> Document.encode "stage" ("extracted" :> obj)
    |> Document.encode "extracted_at" (extractedAt :> obj)
    |> Document.encode "extracted_vendor" ("completion-vendor" :> obj)

let private definition (fixture: Fixture) : Workflow.StageDefinition =
    { Name = stageName
      OutputKey = "extracted_at"
      RequiredKeys = [ "saved_path" ]
      Process = fun document ->
          fixture.Processed.Enqueue(Document.id document)
          document |> enrich |> Task.FromResult
      ResourceLock = fixture.Resource
      MaxHoldTime = TimeSpan.FromTicks(-1L) }

let private readDocuments (fixture: Fixture) : Task<Document.T list> =
    fixture.Db.execReader "SELECT * FROM documents ORDER BY id" []

let private seedDocuments (fixture: Fixture) : Task<Document.T list> =
    task {
        let! _ =
            fixture.Db.execNonQuery
                """INSERT INTO documents
                     (id,stage,source_type,original_name,saved_path,category,sha256,starred)
                   VALUES
                     (1,'received','manual_drop','one.txt','local/one.txt','original','completion-one',1),
                     (2,'received','manual_drop','two.txt','local/two.txt','original','completion-two',0)"""
                []
        return! readDocuments fixture
    }

let private startWorker (fixture: Fixture) (forward: bool) (ct: CancellationToken) : Task =
    let output = if forward then Some fixture.Output.Writer else None
    Task.Run(Func<Task>(fun () ->
        Workflow.runStage (definition fixture) fixture.Db (logger fixture)
            fixture.Input.Reader output ct :> Task))

/// A timeout fails the test; cancellation is only cleanup for that failed test.
let private finishWithoutCancellation (cancellation: CancellationTokenSource) (worker: Task) : Task<unit> =
    task {
        try
            do! worker.WaitAsync(deadline)
            Assert.False(cancellation.IsCancellationRequested, "Completed input must stop without cancellation")
        with :? TimeoutException as expired ->
            cancellation.Cancel()
            do! worker.WaitAsync(deadline)
            return raise expired
    }

let private assertLifecycle (fixture: Fixture) (expectedPermits: int) =
    let infos =
        fixture.Events
        |> Seq.filter (fun entry -> entry.StartsWith("INFO ", StringComparison.Ordinal))
        |> Seq.toList
    Assert.Equal<string list>(
        [ $"INFO Stage '{stageName}' consumer started"; $"INFO Stage '{stageName}' consumer stopped" ],
        infos)
    let failed =
        fixture.Events
        |> Seq.exists (fun entry -> entry.StartsWith("WARN ", StringComparison.Ordinal)
                                    || entry.StartsWith("ERROR ", StringComparison.Ordinal))
    Assert.False(failed, String.concat Environment.NewLine fixture.Events)
    fixture.Resource |> Option.iter (fun resource -> Assert.Equal(expectedPermits, resource.CurrentCount))

let private readOutput (reader: ChannelReader<Document.T>) (collected: Document.T list)
    (_: Document.T) : Task<Document.T list> =
    task {
        let! document = reader.ReadAsync().AsTask().WaitAsync(deadline)
        return document :: collected
    }

let private assertOutputOwnership (fixture: Fixture) (expected: Document.T list) : Task<unit> =
    task {
        Assert.False(fixture.Output.Reader.Completion.IsCompleted)
        let marker : Document.T =
            Map.ofList [ "id", -1L :> obj; "stage", "output-owner-marker" :> obj ]
        Assert.True(fixture.Output.Writer.TryWrite marker, "A consumer must not complete its output writer")
        fixture.Output.Writer.Complete()
        let allExpected = expected @ [ marker ]
        let! reversed = allExpected |> Prelude.foldTask (readOutput fixture.Output.Reader) []
        Assert.Equal<Document.T list>(allExpected, List.rev reversed)
        do! fixture.Output.Reader.Completion.WaitAsync(deadline)
        let! canRead = fixture.Output.Reader.WaitToReadAsync().AsTask().WaitAsync(deadline)
        Assert.False(canRead)
    }

[<Theory>]
[<InlineData(false, false)>]
[<InlineData(false, true)>]
[<InlineData(true, false)>]
[<InlineData(true, true)>]
[<Trait("Category", "Integration")>]
let ``Completed empty input terminates without cancellation or acquiring a resource`` useResource forward =
    task {
        use fixture = createFixture useResource 0
        use cancellation = new CancellationTokenSource()
        fixture.Input.Writer.Complete()
        let worker = startWorker fixture forward cancellation.Token
        do! finishWithoutCancellation cancellation worker
        do! fixture.Input.Reader.Completion.WaitAsync(deadline)
        let! persisted = readDocuments fixture
        Assert.Empty(persisted)
        Assert.Empty(fixture.Processed)
        assertLifecycle fixture 0
        do! assertOutputOwnership fixture []
    }

[<Theory>]
[<InlineData(false, false)>]
[<InlineData(false, true)>]
[<InlineData(true, false)>]
[<InlineData(true, true)>]
[<Trait("Category", "Integration")>]
let ``Completed populated input drains persists and releases its resource before terminating naturally`` useResource forward =
    task {
        use fixture = createFixture useResource 1
        use cancellation = new CancellationTokenSource()
        let! original = seedDocuments fixture
        original |> List.iter (fun document -> Assert.True(fixture.Input.Writer.TryWrite document))
        fixture.Input.Writer.Complete()
        let worker = startWorker fixture forward cancellation.Token
        do! finishWithoutCancellation cancellation worker
        do! fixture.Input.Reader.Completion.WaitAsync(deadline)
        let! persisted = readDocuments fixture
        let expected = original |> List.map enrich
        Assert.Equal<Document.T list>(expected, persisted)
        Assert.Equal<int64 list>([ 1L; 2L ], fixture.Processed |> Seq.toList)
        assertLifecycle fixture 1
        do! assertOutputOwnership fixture (if forward then expected else [])
    }

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("Category", "Integration")>]
let ``Large prebuffered completed input drains naturally with and without resource yielding`` useResource =
    task {
        use fixture = createFixture useResource 1
        use cancellation = new CancellationTokenSource()
        let expected: Document.T list =
            [ 1L .. 20000L ]
            |> List.map (fun id -> Map.ofList [ "id", id :> obj; "extracted_at", extractedAt :> obj ])
        expected |> List.iter (fun document -> Assert.True(fixture.Input.Writer.TryWrite document))
        fixture.Input.Writer.Complete()
        let worker = startWorker fixture true cancellation.Token
        do! finishWithoutCancellation cancellation worker
        do! fixture.Input.Reader.Completion.WaitAsync(deadline)
        Assert.Empty(fixture.Processed)
        assertLifecycle fixture 1
        do! assertOutputOwnership fixture expected
    }
