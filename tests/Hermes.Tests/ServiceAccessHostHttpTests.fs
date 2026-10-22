module Hermes.Tests.ServiceAccessHostHttpTests

open System
open System.Collections.Concurrent
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Xunit
open Hermes.Tests.ServiceAccessHostTestSupport
open Hermes.Tests.ServiceAccessHostFixture

type private HeldStartup =
    { PoolThread: bool
      HttpCompleted: bool
      ControlReleased: bool }

type private StartupHttpProbe =
    { Begin: TaskCompletionSource<unit>
      Entered: TaskCompletionSource<unit>
      Release: TaskCompletionSource<unit>
      Finishing: TaskCompletionSource<unit>
      CancellationObserved: TaskCompletionSource<unit>
      Finish: TaskCompletionSource<unit>
      Disposals: ConcurrentQueue<unit> }

type private VerificationFailure =
    { Original: exn
      Transport: exn option
      WaitForEntry: bool }

let private httpProbe (holdEntry: bool) (holdSettlement: bool) : StartupHttpProbe =
    let beginning = cell<unit> ()
    let finish = cell<unit> ()
    if not holdEntry then beginning.SetResult(())
    if not holdSettlement then finish.SetResult(())
    { Begin = beginning; Finish = finish
      Entered = cell (); Release = cell (); Finishing = cell ()
      CancellationObserved = cell ()
      Disposals = ConcurrentQueue<unit>() }

let private withStartupHttp (expected: exn option) (handler: HttpMessageHandler)
    (server: Server) (verify: Task<Reply> -> Task<unit>) : Task<unit> =
    task {
        use cancellation = new CancellationTokenSource()
        let pending = startingHealthUsing handler cancellation.Token server
        let! verified = observeStartup (fun () -> verify pending)
        let! cancelled =
            observeStartup (fun () ->
                cancellation.Cancel()
                Task.FromResult(()))
        let! settled = observeStartup (fun () -> pending)
        finishStartup expected verified cancelled settled
    }

type private DelayedHandler(probe: StartupHttpProbe) =
    inherit HttpMessageHandler()
    let transport =
        new HttpMessageInvoker(new HttpClientHandler(AllowAutoRedirect = false), true)
    let forward (request: HttpRequestMessage) (token: CancellationToken) : Task<HttpResponseMessage> =
        task {
            do! probe.Begin.Task.WaitAsync(token)
            probe.Entered.TrySetResult(()) |> ignore
            do! probe.Release.Task.WaitAsync(token)
            return! transport.SendAsync(request, token)
        }

    override _.SendAsync(request, token) =
        task {
            use registration =
                token.Register(fun () -> probe.CancellationObserved.TrySetResult(()) |> ignore)
            let! outcome = observeStartup (fun () -> forward request token)
            probe.Finishing.TrySetResult(()) |> ignore
            do! probe.Finish.Task.WaitAsync(testBudget)
            return outcome |> Result.defaultWith raise
        }

    override _.Dispose(disposing) =
        if disposing then
            transport.Dispose()
            probe.Disposals.Enqueue(())
        base.Dispose(disposing)

let private verifyDelayed (probe: StartupHttpProbe) (runner: Task<bool>)
    (server: Server) (pending: Task<Reply>) : Task<unit> =
    task {
        do! wait probe.Entered.Task
        let! onPool = wait runner
        let observed =
            { PoolThread = onPool; HttpCompleted = pending.IsCompleted
              ControlReleased = server.Probe.Release.Task.IsCompleted }
        probe.Release.SetResult(())
        let! health = wait pending
        Assert.False(observed.PoolThread)
        Assert.False(observed.HttpCompleted)
        Assert.False(observed.ControlReleased)
        assertUnavailable health
        do! failStartup server
    }

[<Fact>]
let ``delayed startup HTTP completes before control failure and real listener cleanup`` () : Task =
    let probe = httpProbe false false
    let runner = cell<bool> ()
    let configure (_: WebApplication) : unit =
        runner.SetResult(Thread.CurrentThread.IsThreadPoolThread)
    withServer configure (fun server ->
        withStartupHttp None (new DelayedHandler(probe)) server
            (verifyDelayed probe runner.Task server))

[<Fact>]
let ``startup HTTP transport failure propagates without releasing control readiness`` () : Task =
    let probe = httpProbe false false
    let expected = HttpRequestException("Synthetic startup transport failure.")
    withServer ignore (fun server ->
        withStartupHttp (Some(expected :> exn)) (new DelayedHandler(probe)) server (fun pending -> task {
            do! wait probe.Entered.Task
            probe.Release.SetException(expected)
            let! error =
                Assert.ThrowsAsync<HttpRequestException>(Func<Task>(fun () -> pending :> Task))
            Assert.Same(expected, error)
            Assert.False(server.Probe.Release.Task.IsCompleted)
            Assert.False(server.Probe.WorkerStarted.Task.IsCompleted)
            Assert.Empty(server.Probe.Effects)
        }))

let private failVerification (probe: StartupHttpProbe) (failure: VerificationFailure)
    (_: Task<Reply>) : Task<unit> =
    task {
        if failure.WaitForEntry then do! wait probe.Entered.Task
        failure.Transport |> Option.iter (fun error -> probe.Release.SetException(error))
        if Option.isSome failure.Transport then do! wait probe.Finishing.Task
        return raise failure.Original
    }

let private assertHeldSettlement (probe: StartupHttpProbe) (operation: Task<unit>) : Task<unit> =
    task {
        do! wait probe.CancellationObserved.Task
        do! wait probe.Finishing.Task
        Assert.False(operation.IsCompleted)
        Assert.False(probe.Finish.Task.IsCompleted)
        Assert.Empty(probe.Disposals)
    }

let private assertUnwound (failure: VerificationFailure) (probe: StartupHttpProbe)
    (operation: Task<unit>) (outcome: Result<unit, exn>) : unit =
    let error =
        outcome |> Result.map (fun () -> failwith "Expected verification failure.")
        |> Result.defaultWith id
    let aggregate = Assert.IsType<AggregateException>(error)
    Assert.Equal(2, aggregate.InnerExceptions.Count)
    Assert.Same(failure.Original, aggregate.InnerExceptions[0])
    failure.Transport
    |> Option.map (fun expected ->
        let cancelled = Assert.IsType<TaskCanceledException>(aggregate.InnerExceptions[1])
        Assert.True(cancelled.CancellationToken.IsCancellationRequested)
        Assert.Same(expected, cancelled.InnerException))
    |> Option.defaultWith (fun () ->
        Assert.IsAssignableFrom<OperationCanceledException>(aggregate.InnerExceptions[1]) |> ignore)
    Assert.True(operation.IsCompleted)
    Assert.Equal(failure.WaitForEntry, probe.Entered.Task.IsCompletedSuccessfully)
    Assert.Equal(Option.isSome failure.Transport, probe.Release.Task.IsCompleted)
    Assert.Equal(1, probe.Disposals.Count)

let private verifyUnwinding (failure: VerificationFailure) (probe: StartupHttpProbe)
    (operation: Task<unit>) : Task<unit> =
    task {
        let! checks = observeStartup (fun () -> assertHeldSettlement probe operation)
        if probe.CancellationObserved.Task.IsCompletedSuccessfully then
            probe.Finish.TrySetResult(()) |> ignore
        let! outcome = observeStartup (fun () -> operation)
        let! validation =
            observeStartup (fun () ->
                assertUnwound failure probe operation outcome
                Task.FromResult(()))
        finishStartup None checks (Ok ()) validation
    }

[<Theory>]
[<InlineData(false, false)>]
[<InlineData(true, false)>]
[<InlineData(true, true)>]
let ``verification failure joins gated startup HTTP before fixture cleanup``
    (waitForEntry: bool) (transportFailure: bool) : Task =
    let failure: VerificationFailure =
        { Original = InvalidOperationException("Synthetic verification failure.") :> exn
          Transport =
              if transportFailure then
                  Some(HttpRequestException("Unrelated startup transport failure.") :> exn)
              else None
          WaitForEntry = waitForEntry }
    let probe = httpProbe (not waitForEntry) true
    withServer ignore (fun server -> task {
        do! wait server.Probe.Listening.Task
        let operation =
            withStartupHttp None (new DelayedHandler(probe)) server (failVerification probe failure)
        do! verifyUnwinding failure probe operation
    })
