module Hermes.Tests.WorkflowFailureRegressionTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.CoreRegressionSupport

let private failureMessage = "deliberate write-aside failure"

let private failEnrichedWrite (db: Algebra.Database) =
    { db with
        execNonQuery = fun sql parameters ->
            let values = Prelude.RowReader(parameters |> Map.ofList)
            let enriched =
                sql = Document.updateSql
                && values.String "@stage" "" = "extracted"
                && values.Int64 "@id" 0L = 1L
            if enriched then Task.FromException<int>(IOException failureMessage)
            else db.execNonQuery sql parameters }

let private processor (persistFailure: bool) (document: Document.T) : Task<Document.T> =
    if not persistFailure && Document.id document = 1L then
        Task.FromException<Document.T>(IOException failureMessage)
    else
        document |> complete |> Task.FromResult

let private assertFailureThenSuccess (fixture: Fixture) : Task<unit> =
    task {
        let! failed = row fixture.Db 1L
        let! succeeded = row fixture.Db 2L
        Assert.Equal("failed", Document.stage failed)
        Assert.Equal<string option>(Some "archive-vendor", Document.decode<string> "extracted_vendor" failed)
        Assert.False(Document.hasKey "extracted_at" failed)
        assertExtracted succeeded
        assertOriginal fixture failed
        assertOriginal fixture succeeded
    }

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("Category", "Integration")>]
let ``Workflow failure persists the original row and continues with the next document`` persistFailure =
    task {
        use fixture = fixture ()
        do! seedMany fixture [ 1L, "received"; 2L, "received" ]
        let! input = documents fixture
        let observation = probe "processed doc 2"
        let db = if persistFailure then failEnrichedWrite fixture.Db else fixture.Db
        let stage = workflowStage (processor persistFailure)
        let! output = runWorkflow stage db observation input true
        let! forwarded = output.ReadAsync().AsTask().WaitAsync(timeout)
        Assert.Equal(2L, Document.id forwarded)
        assertExtracted forwarded
        Assert.Equal(0, output.Count)
        Assert.True(saw observation $"failed doc 1: {failureMessage}")
        do! assertFailureThenSuccess fixture
    }

let private rejectFirstWrites (db: Algebra.Database) (rejected: ConcurrentQueue<string>) =
    { db with
        execNonQuery = fun sql parameters ->
            let values = Prelude.RowReader(parameters |> Map.ofList)
            if sql = Document.updateSql && values.Int64 "@id" 0L = 1L then
                rejected.Enqueue(values.String "@stage" "")
                Task.FromException<int>(IOException failureMessage)
            else db.execNonQuery sql parameters }

let private assertRecoveryLease (resource: SemaphoreSlim) (observation: Probe) (rejected: ConcurrentQueue<string>) =
    let events = observation.Events |> Seq.toList
    let count (fragment: string) =
        events |> List.filter (fun entry -> entry.Contains(fragment, StringComparison.Ordinal)) |> List.length
    Assert.Equal<string list>([ "extracted"; "failed" ], rejected |> Seq.toList)
    Assert.Equal(1, resource.CurrentCount)
    Assert.Equal(1, count "acquired resource lock")
    Assert.Equal(1, count "released resource lock")
    Assert.True(saw observation $"loop error: {failureMessage}")
    Assert.Equal<string list>(
        [ $"ERROR Stage 'regression' loop error: {failureMessage}" ],
        events |> List.filter (fun entry -> entry.StartsWith("ERROR ", StringComparison.Ordinal)))
    Assert.True(saw observation "consumer stopped")

let private assertRejectedFirstRow (fixture: Fixture) (original: Document.T)
    (output: ChannelReader<Document.T>) : Task<unit> =
    task {
        let! unchanged = row fixture.Db 1L
        let! persisted = row fixture.Db 2L
        let! forwarded = output.ReadAsync().AsTask().WaitAsync(timeout)
        Assert.Equal<Document.T>(original, unchanged)
        Assert.Equal<Document.T>(persisted, forwarded)
        Assert.Equal(2L, Document.id forwarded)
        assertExtracted persisted
        Assert.Equal(0, output.Count)
        assertOriginal fixture unchanged
        assertOriginal fixture persisted
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Workflow continues after both writes for the first document are rejected and releases its resource`` () =
    task {
        use fixture = fixture ()
        use resource = new SemaphoreSlim(1, 1)
        do! seedMany fixture [ 1L, "received"; 2L, "received" ]
        let! input = documents fixture
        let rejected = ConcurrentQueue<string>()
        let db = rejectFirstWrites fixture.Db rejected
        let stage =
            { workflowStage (complete >> Task.FromResult) with
                ResourceLock = Some resource; MaxHoldTime = TimeSpan.MaxValue }
        let observation = probe "processed doc 2"
        let! output = runWorkflow stage db observation input true
        assertRecoveryLease resource observation rejected
        do! assertRejectedFirstRow fixture (List.head input) output
    }

let private verifyForwarding (expected: Document.T) (forward: bool) (output: ChannelReader<Document.T>) =
    task {
        Assert.Equal((if forward then 1 else 0), output.Count)
        if forward then
            let! actual = output.ReadAsync().AsTask().WaitAsync(timeout)
            Assert.Equal<Document.T>(expected, actual)
        Assert.Equal(0, output.Count)
    }

let private missingDependency (original: Document.T) (alreadyDone: bool) : Document.T =
    let missing = original |> Map.remove "saved_path"
    let input = if alreadyDone then complete missing else missing
    Assert.False(Document.hasKey "saved_path" input)
    Assert.Equal(alreadyDone, Document.hasKey "extracted_at" input)
    input

[<Theory>]
[<InlineData(false, false)>]
[<InlineData(false, true)>]
[<InlineData(true, false)>]
[<InlineData(true, true)>]
[<Trait("Category", "Integration")>]
let ``Workflow missing dependencies and existing output pass through without a write`` alreadyDone forward =
    task {
        use fixture = fixture ()
        do! seed fixture 1L "received"
        let! original = row fixture.Db 1L
        let input = missingDependency original alreadyDone
        let processor _ = Task.FromException<Document.T>(InvalidOperationException("Must not process"))
        let observation = probe "skip doc 1:"
        let! output = runWorkflow (workflowStage processor) fixture.Db observation [ input ] forward
        do! verifyForwarding input forward output
        let! unchanged = row fixture.Db 1L
        Assert.Equal<Document.T>(original, unchanged)
        Assert.False(saw observation "WARN")
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Workflow hydration excludes terminal rows and retains complete source identity`` () =
    task {
        use fixture = fixture ()
        do! seedMany fixture [ 1L, "received"; 2L, "embedded"; 3L, "failed"; 4L, "extracted" ]
        let channel = Channel.CreateUnbounded<Document.T>()
        let observation = probe "Hydrated"
        let! hydrated = Workflow.hydrate fixture.Db observation.Logger channel.Writer
        Assert.Equal(2, hydrated)
        Assert.Equal(2, channel.Reader.Count)
        let! first = channel.Reader.ReadAsync().AsTask().WaitAsync(timeout)
        let! second = channel.Reader.ReadAsync().AsTask().WaitAsync(timeout)
        Assert.Equal<Set<int64>>(set [ 1L; 4L ], set [ Document.id first; Document.id second ])
        [ first; second ] |> List.iter (assertOriginal fixture)
        Assert.True(saw observation "Hydrated 2 documents into pipeline")
    }
