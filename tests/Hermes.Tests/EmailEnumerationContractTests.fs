module Hermes.Tests.EmailEnumerationContractTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures

let private failingProvider (requests: ConcurrentQueue<string option>)
    (failure: exn) : Algebra.EmailProvider =
    { TestHelpers.emptyProvider with
        listStubPage = fun token _ _ ->
            requests.Enqueue(token)
            Task.FromException<Algebra.StubPage>(failure) }

let private enumerationDeadline = TimeSpan.FromSeconds(5.0)
let private cleanupDeadline = TimeSpan.FromSeconds(5.0)

// Observe faults even when the bounded wait expires before the producer finishes.
let private observeCompletion (operation: Task) : Task =
    operation.ContinueWith(
        Action<Task>(fun completed ->
            if completed.IsFaulted then
                completed.Exception |> ignore),
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default)

let private stopProducer (cancellation: CancellationTokenSource) (completion: Task) : unit =
    try
        cancellation.Cancel()
    finally
        try
            completion.WaitAsync(cleanupDeadline).GetAwaiter().GetResult()
        with :? TimeoutException as error ->
            raise (TimeoutException("Enumeration producer exceeded the cleanup deadline.", error))

let private withBoundedProducer (deadline: TimeSpan)
    (start: CancellationToken -> Task<int>) (verify: Task<int> -> Task<unit>) : Task<unit> =
    task {
        use cancellation = new CancellationTokenSource()
        let operation = start cancellation.Token
        let completion = observeCompletion (operation :> Task)
        try
            do! completion.WaitAsync(deadline)
            do! verify operation
        finally
            stopProducer cancellation completion
    }

let private assertEnumerationFailure
    (failure: exn) (requests: ConcurrentQueue<string option>) (warnings: ConcurrentQueue<string>)
    (output: ChannelReader<string>) (count: int ref) (operation: Task<int>) : Task<unit> =
    task {
        let! actual =
            Assert.ThrowsAnyAsync<Exception>(Func<Task>(fun () -> operation :> Task))
        Assert.Equal<Type>(failure.GetType(), actual.GetType())
        Assert.Equal<string>(failure.Message, actual.Message)
        do! output.Completion.WaitAsync(enumerationDeadline)
        Assert.Equal(0, count.Value)
        Assert.Equal<string option list>([ None ], requests |> Seq.toList)
        Assert.Contains("synthetic enumeration fault", Assert.Single<string>(warnings))
    }

let private checkEnumerationFailure (failure: exn) : Task<unit> =
    let requests = ConcurrentQueue<string option>()
    let warnings = ConcurrentQueue<string>()
    let logger = { TestHelpers.silentLogger with warn = warnings.Enqueue }
    let output = Channel.CreateUnbounded<string>()
    let count = ref 0
    let start ct =
        EmailSync.enumerateIds (failingProvider requests failure) logger
            "enumeration-account" "after:1" output.Writer count ct
    withBoundedProducer enumerationDeadline start
        (assertEnumerationFailure failure requests warnings output.Reader count)

let private waitUntilCancelled (pending: TaskCompletionSource<int>)
    (stopped: TaskCompletionSource<unit>) (ct: CancellationToken) : Task<int> =
    task {
        use registration = ct.Register(fun () -> pending.TrySetCanceled(ct) |> ignore)
        try
            return! pending.Task
        finally
            stopped.TrySetResult(()) |> ignore
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Bounded enumeration wait times out and joins its cancelled producer`` () =
    let pending = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
    let stopped = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let operation =
        withBoundedProducer TimeSpan.Zero (waitUntilCancelled pending stopped)
            (fun _ -> Task.FromResult())
    let assertion = Assert.ThrowsAsync<TimeoutException>(Func<Task>(fun () -> operation :> Task))
    let completion = observeCompletion (assertion :> Task)
    task {
        try
            let! _ = assertion.WaitAsync(enumerationDeadline)
            Assert.True(pending.Task.IsCanceled)
            Assert.True(stopped.Task.IsCompletedSuccessfully)
        finally
            pending.TrySetResult(0) |> ignore
            stopped.Task.WaitAsync(cleanupDeadline).GetAwaiter().GetResult()
            completion.WaitAsync(cleanupDeadline).GetAwaiter().GetResult()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Enumeration forwards ordered pages updates its counter and completes input`` () =
    let first = message "enumerated-first" "first-thread" None
    let second = message "enumerated-second" "second-thread" None
    let feed =
        provider (twoPages first second) [ first; second ] Map.empty
        |> since (DateTimeOffset.FromUnixTimeSeconds(1L))
    let output = Channel.CreateUnbounded<string>()
    let count = ref 0
    task {
        let! total =
            EmailSync.enumerateIds feed TestHelpers.silentLogger "enumeration-account"
                "after:1" output.Writer count CancellationToken.None
        let! firstId = output.Reader.ReadAsync().AsTask()
        let! secondId = output.Reader.ReadAsync().AsTask()
        do! output.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(5.0))
        Assert.Equal(2, total)
        Assert.Equal(2, count.Value)
        Assert.Equal<string list>([ first.ProviderId; second.ProviderId ], [ firstId; secondId ])
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Enumeration provider failure faults once and closes input`` () =
    checkEnumerationFailure (InvalidOperationException("synthetic enumeration fault"))

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Enumeration request timeout is not mistaken for cooperative cancellation`` () =
    checkEnumerationFailure (TaskCanceledException("synthetic enumeration fault"))
