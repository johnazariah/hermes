module internal Hermes.Tests.ServiceAccessHostDrainSupport

open System
open System.Collections.Concurrent
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Options
open Xunit
open Hermes.Core
open Hermes.Core.LocalAccess
open Hermes.Service
open Hermes.Tests.ServiceAccessHostTestSupport

module Host = Hermes.Tests.ServiceAccessHostFixture
module Session = Hermes.Tests.ServiceAccessHostSessionSupport

type Pending =
    { Calls: ConcurrentQueue<unit>
      Aborted: TaskCompletionSource<unit>
      Release: TaskCompletionSource<unit>
      Finish: TaskCompletionSource<unit>
      Provider: TaskCompletionSource<Task<int64>>
      Invocation: TaskCompletionSource<Task>
      Settled: TaskCompletionSource<unit> }

type StopGate =
    { Closed: TaskCompletionSource<unit>
      Release: TaskCompletionSource<unit>
      Closing: Task
      Registration: CancellationTokenRegistration }
    interface IDisposable with
        member gate.Dispose() =
            gate.Release.TrySetResult(()) |> ignore
            gate.Registration.Dispose()
            gate.Closing.GetAwaiter().GetResult()

let pendingPath: string = "/api/host/pending"

let private create () : Pending =
    { Calls = ConcurrentQueue<unit>(); Aborted = cell ()
      Release = cell (); Finish = cell ()
      Provider = cell (); Invocation = cell (); Settled = cell () }

let private query (pending: Pending) (db: Algebra.Database) : Task<int64> =
    task {
        // Neither the provider wait nor its DB continuation uses RequestAborted.
        do! pending.Release.Task
        let! result = db.execScalar "SELECT 52" []
        return unbox<int64> result
    }

let private watchAbort (pending: Pending) (context: HttpContext)
    : CancellationTokenRegistration =
    context.RequestAborted.Register(
        Action(fun () -> pending.Aborted.TrySetResult(()) |> ignore))

let private execute (pending: Pending) (db: Algebra.Database)
    (context: HttpContext) : Task =
    task {
        try
            use aborted = watchAbort pending context
            context.Response.StatusCode <- StatusCodes.Status204NoContent
            pending.Calls.Enqueue(())
            let provider = query pending db
            pending.Provider.TrySetResult(provider) |> ignore
            let! _ = provider
            do! pending.Finish.Task
        finally
            pending.Settled.TrySetResult(()) |> ignore
    } :> Task

let private handle (pending: Pending) (db: Algebra.Database)
    (context: HttpContext) : Task =
    let invocation = execute pending db context
    pending.Invocation.TrySetResult(invocation) |> ignore
    invocation

let private map (pending: Pending) (db: Algebra.Database) (app: WebApplication) : unit =
    app.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout <-
        TimeSpan.Zero
    app.MapPost(pendingPath, RequestDelegate(handle pending db))
    |> HttpAccessPolicy.classify
        (HttpAccessClassification.RequiredCaps(Set.singleton Capability.Mutate))
    |> ignore
    app.MapPost("/mcp", RequestDelegate(handle pending db))
    |> HttpAccessPolicy.classify HttpAccessClassification.McpIndependent
    |> ignore

let beginStop (captured: CapturedHost) : StopGate =
    let closed = cell<unit> ()
    let release = cell<unit> ()
    let registration =
        captured.Context.Cancellation.Register(Action(fun () ->
            closed.TrySetResult(()) |> ignore
            release.Task.GetAwaiter().GetResult()))
    { Closed = closed; Release = release; Registration = registration
      Closing = Task.Run(Action(captured.Lifetime.StopApplication)) }

let assertClosed (server: Server) (pending: Pending) (gate: StopGate) : Task<unit> =
    task {
        do! wait gate.Closed.Task
        let! denied = post server pendingPath None "{}"
        assertUnavailable denied
        let! independent = post server "/mcp" None "{}"
        assertUnavailable independent
        Assert.Single(pending.Calls) |> ignore
    }

let assertPending (server: Server) (pending: Pending) : Task<unit> =
    task {
        let! invocation = wait pending.Invocation.Task
        let! captured = wait server.Captured
        Assert.False(invocation.IsCompleted)
        Assert.False(pending.Settled.Task.IsCompleted)
        Assert.False(captured.Context.RequestsDrained.IsCompleted)
    }

let stopHttp (server: Server) (pending: Pending) (gate: StopGate) : Task<unit> =
    task {
        gate.Release.TrySetResult(()) |> ignore
        do! gate.Closing.WaitAsync(TimeSpan.FromSeconds 20.0)
        server.Probe.JoinRelease.TrySetResult(()) |> ignore
        let! captured = wait server.Captured
        do! captured.App.StopAsync(CancellationToken.None)
        do! wait pending.Aborted.Task
        let! provider = wait pending.Provider.Task
        Assert.False(provider.IsCompleted)
        do! assertPending server pending
    }

let private observeRequest (request: Task<Reply>) : Task<unit> =
    task {
        try
            let! _ = wait request
            ()
        with
        | :? HttpRequestException
        | :? OperationCanceledException -> ()
    }

let private resumeProvider (pending: Pending) : Task<unit> =
    task {
        let! provider = wait pending.Provider.Task
        pending.Release.TrySetResult(()) |> ignore
        let! value = wait provider
        Assert.Equal<int64>(52L, value)
    }

let private settleHandler (pending: Pending) : Task<unit> =
    task {
        let! invocation = wait pending.Invocation.Task
        pending.Finish.TrySetResult(()) |> ignore
        do! invocation.WaitAsync(TimeSpan.FromSeconds 20.0)
        Assert.True(pending.Settled.Task.IsCompletedSuccessfully)
    }

let complete (server: Server) (pending: Pending) (request: Task<Reply>) : Task<unit> =
    task {
        do! resumeProvider pending
        // DB work has succeeded, but the actual returned handler task is held.
        do! assertPending server pending
        do! settleHandler pending
        let! captured = wait server.Captured
        do! captured.Context.RequestsDrained.WaitAsync(TimeSpan.FromSeconds 20.0)
        do! observeRequest request
    }

let startRequest (server: Server) (pending: Pending) (path: string)
    : Task<Task<Reply>> =
    task {
        do! Host.becomeReady server
        let! authority = Session.credentialForPath pendingPath (fun () -> Session.issue server) path
        let request = post server path authority "{}"
        let! invocation = wait pending.Invocation.Task
        Assert.False(invocation.IsCompleted)
        return request
    }

let private verifyOwned (pending: Pending)
    (verify: Pending -> Server -> Task<unit>) (server: Server) : Task<unit> =
    task {
        try do! verify pending server
        finally
            pending.Release.TrySetResult(()) |> ignore
            pending.Finish.TrySetResult(()) |> ignore
    }

let withPending (budget: TimeSpan) (owner: Hermes.Service.Program.DatabaseScope)
    (verify: Pending -> Server -> Task<unit>) : Task<unit> =
    task {
        let pending = create ()
        do! Host.withShutdownBudget budget (map pending owner.Database)
                (verifyOwned pending verify)
    }
