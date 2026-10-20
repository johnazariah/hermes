module Hermes.Tests.WorkflowLeaseRegressionTests

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.CoreRegressionSupport

let private lockEvents (observation: Probe) (fragment: string) =
    observation.Events
    |> Seq.filter (fun entry -> entry.Contains(fragment, StringComparison.Ordinal))
    |> Seq.length

let private completeUnderLock (resource: SemaphoreSlim) (document: Document.T) : Task<Document.T> =
    Assert.Equal(0, resource.CurrentCount)
    document |> complete |> Task.FromResult

let private assertAllExtracted (fixture: Fixture) : Task<unit> =
    task {
        let! persisted = documents fixture
        Assert.Equal(2, persisted.Length)
        persisted |> List.iter assertExtracted
        persisted |> List.iter (assertOriginal fixture)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Workflow yields and reacquires its resource without losing the remaining burst`` () =
    task {
        use fixture = fixture ()
        use resource = new SemaphoreSlim(1, 1)
        do! seedMany fixture [ 1L, "received"; 2L, "received" ]
        let! input = documents fixture
        let stage =
            { workflowStage (completeUnderLock resource) with
                ResourceLock = Some resource; MaxHoldTime = TimeSpan.FromTicks(-1L) }
        let observation = probe "processed doc 2"
        let! output = runWorkflow stage fixture.Db observation input false
        Assert.Equal(0, output.Count)
        Assert.Equal(1, resource.CurrentCount)
        Assert.Equal(2, lockEvents observation "acquired resource lock")
        Assert.Equal(2, lockEvents observation "yielding resource lock")
        Assert.Equal(2, lockEvents observation "released resource lock")
        do! assertAllExtracted fixture
    }

let private verifyBlocked (worker: Task<unit>) (input: Channel<Document.T>) () =
    task {
        Assert.False(worker.IsCompleted)
        Assert.Equal(1, input.Reader.Count)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Workflow cancellation while acquiring a resource does not release an unowned permit`` () =
    task {
        use fixture = fixture ()
        use cancellation = new CancellationTokenSource(timeout)
        use resource = new SemaphoreSlim(0, 1)
        do! seed fixture 1L "received"
        let! original = row fixture.Db 1L
        let input = Channel.CreateUnbounded<Document.T>()
        Assert.True(input.Writer.TryWrite original)
        input.Writer.Complete()
        let observation = probe "consumer started"
        let stage = { workflowStage (complete >> Task.FromResult) with ResourceLock = Some resource }
        let worker = Workflow.runStage stage fixture.Db observation.Logger input.Reader None cancellation.Token
        do! settle cancellation.Cancel [ worker :> Task ] (verifyBlocked worker input)
        let! unchanged = row fixture.Db 1L
        Assert.Equal<Document.T>(original, unchanged)
        Assert.Equal(0, resource.CurrentCount)
        Assert.True(saw observation "consumer stopped")
        Assert.False(saw observation "acquired resource lock")
    }

let private delayedFirst (release: TaskCompletionSource<unit>) (ct: CancellationToken)
    (document: Document.T) : Task<Document.T> =
    task {
        if Document.id document = 1L then
            do! release.Task.WaitAsync(ct)
        return complete document
    }

let private verifyOutOfOrder
    (output: Channel<Document.T>) (release: TaskCompletionSource<unit>) (ct: CancellationToken) () =
    task {
        let! second = output.Reader.ReadAsync(ct).AsTask().WaitAsync(timeout)
        Assert.Equal(2L, Document.id second)
        assertExtracted second
        release.TrySetResult(()) |> ignore
        let! first = output.Reader.ReadAsync(ct).AsTask().WaitAsync(timeout)
        Assert.Equal(1L, Document.id first)
        assertExtracted first
        Assert.Equal(0, output.Reader.Count)
    }

type private Consumers =
    { Output: Channel<Document.T>
      Workers: Task<unit> list }

let private startConsumers (fixture: Fixture) (inputDocuments: Document.T list)
    (release: TaskCompletionSource<unit>) (ct: CancellationToken) : Consumers =
    let input = Channel.CreateUnbounded<Document.T>()
    let output = Channel.CreateUnbounded<Document.T>()
    inputDocuments |> List.iter (fun document -> Assert.True(input.Writer.TryWrite document))
    let stage = workflowStage (delayedFirst release ct)
    let workers =
        Workflow.launchConsumers stage fixture.Db TestHelpers.silentLogger
            2 input.Reader (Some output.Writer) ct
    input.Writer.Complete()
    { Output = output; Workers = workers }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Workflow competing consumers persist and forward each document despite out of order completion`` () =
    task {
        use fixture = fixture ()
        use cancellation = new CancellationTokenSource()
        let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        do! seedMany fixture [ 1L, "received"; 2L, "received" ]
        let! inputDocuments = documents fixture
        let consumers = startConsumers fixture inputDocuments release cancellation.Token
        let cleanup () = release.TrySetResult(()) |> ignore; cancellation.Cancel()
        do! finishNaturally cleanup (consumers.Workers |> List.map (fun worker -> worker :> Task))
                (verifyOutOfOrder consumers.Output release cancellation.Token)
        Assert.False(cancellation.IsCancellationRequested)
        do! assertAllExtracted fixture
    }
