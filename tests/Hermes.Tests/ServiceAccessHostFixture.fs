module internal Hermes.Tests.ServiceAccessHostFixture

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Xunit
open Hermes.Core
open Hermes.Core.OwnerControlPipe
open Hermes.Service
open Hermes.Tests.ServiceAccessHostTestSupport

let rec private freePort () : int =
    use listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let port =
        match listener.LocalEndpoint with
        | :? IPEndPoint as endpoint -> endpoint.Port
        | _ -> invalidOp "Missing owned loopback endpoint."
    listener.Stop()
    if port = 21741 || port = 21742 then freePort () else port

let private probe (root: string) : Probe =
    { Root = root; Effects = System.Collections.Concurrent.ConcurrentQueue<Effect>()
      ControlStarted = cell (); Listening = cell (); Release = cell ()
      Fatal = cell (); WorkerStarted = cell (); WorkerDraining = cell ()
      JoinRelease = cell (); WorkerStopped = cell (); QueuesClosed = cell () }

let private delayedReady
    (probe: Probe)
    (token: CancellationToken)
    (ready: Task<Result<unit, PipeFailure>>)
    : Task<Result<unit, PipeFailure>> =
    task {
        let! outcome = ready
        match outcome with
        | Error failure -> return Error failure
        | Ok () ->
            probe.Listening.TrySetResult(()) |> ignore
            try return! probe.Release.Task.WaitAsync(token)
            with :? OperationCanceledException when token.IsCancellationRequested ->
                return Error PipeFailure.Cancelled
    }

let private stopDecorated
    (running: Running)
    (ready: Task<Result<unit, PipeFailure>>)
    ()
    : Task<Result<unit, PipeFailure>> =
    task {
        let! outcomes = Task.WhenAll([| running.Stop(); ready |])
        return outcomes[0]
    }

let private controlFactory
    (probe: Probe)
    (target: OwnerControlTarget.Target)
    (dispatch: Dispatch)
    (fatal: PipeFailure -> unit)
    : Factory =
    probe.ControlStarted.TrySetResult(()) |> ignore
    probe.Fatal.TrySetResult(fatal) |> ignore
    let actual = Factory.createCancellable target dispatch fatal
    { actual with
        Start = fun token ->
            let running = actual.Start token
            let ready = delayedReady probe token running.Ready
            { running with
                Ready = ready
                Stop = stopDecorated running ready } }

let private configure
    (probe: Probe)
    (captured: TaskCompletionSource<CapturedHost>)
    (extra: WebApplication -> unit)
    (app: WebApplication)
    (context: HostContext)
    : AccessHostWorkload =
    mapRoutes probe app
    extra app
    captured.SetResult(
        { App = app; Lifetime = app.Lifetime; Context = context })
    { Workers = [ { Name = "synthetic-worker"; Start = worker probe } ]
      StopQueues = fun () -> probe.QueuesClosed.TrySetResult(()) |> ignore }

let private start (shutdownBudget: TimeSpan) (extra: WebApplication -> unit) : Server =
    let root = Path.Combine(Path.GetTempPath(), "hermes-host-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    let port = freePort ()
    let origin = $"http://127.0.0.1:{port}"
    let observations = probe root
    let captured = cell<CapturedHost> ()
    let options: AccessHostOptions =
        { ConfigDirectory = root; Port = port; ExtraOrigins = []
          Models = { IsAvailable = fun _ -> true } }
    let logger: Algebra.Logger =
        { info = ignore; warn = ignore; error = ignore; debug = ignore }
    let run: Task<AccessHostResult> =
        Task.Run(Func<AccessHostResult>(fun () ->
            ServiceAccessHost.runWithControlAndShutdownBudget shutdownBudget
                (controlFactory observations)
                options logger (configure observations captured extra)))
    let handler = new HttpClientHandler(AllowAutoRedirect = false)
    { Probe = observations; Origin = origin; Captured = captured.Task; Run = run
      Client = new HttpClient(handler, true, BaseAddress = Uri(origin),
                              Timeout = TimeSpan.FromSeconds 2.0) }

let private requestStop (server: Server) : unit =
    server.Probe.Release.TrySetResult(Ok ()) |> ignore
    server.Probe.JoinRelease.TrySetResult(()) |> ignore
    let candidates: Task array = [| server.Captured; server.Run |]
    Task.WhenAny(candidates).WaitAsync(TimeSpan.FromSeconds 20.0)
        .GetAwaiter().GetResult() |> ignore
    if server.Captured.IsCompletedSuccessfully && not server.Run.IsCompleted then
        server.Captured.Result.Lifetime.StopApplication()

let private releaseApplication
    (requireQuiescence: bool)
    (server: Server)
    (result: AccessHostResult)
    : unit =
    if requireQuiescence then
        Assert.Equal(Lifetime.Quiesced, result.Disposition)
    if server.Captured.IsCompletedSuccessfully then
        let captured = server.Captured.Result
        captured.Context.RequestsDrained.WaitAsync(TimeSpan.FromSeconds 20.0)
            .GetAwaiter().GetResult()
        if result.Disposition <> Lifetime.Quiesced then
            captured.App.DisposeAsync().AsTask().GetAwaiter().GetResult()

let private cleanup (requireQuiescence: bool) (server: Server) : unit =
    requestStop server
    let result = (wait server.Run).GetAwaiter().GetResult()
    server.Client.Dispose()
    releaseApplication requireQuiescence server result
    if server.Probe.WorkerStarted.Task.IsCompletedSuccessfully then
        (wait server.Probe.WorkerStopped.Task).GetAwaiter().GetResult()
    assertPortReleased server
    Directory.Delete(server.Probe.Root, true)

let withServer
    (extra: WebApplication -> unit)
    (verify: Server -> Task<unit>)
    : Task =
    task {
        let server = start (TimeSpan.FromSeconds 60.0) extra
        try do! verify server
        finally cleanup true server
    } :> Task

let withShutdownBudget
    (shutdownBudget: TimeSpan)
    (extra: WebApplication -> unit)
    (verify: Server -> Task<unit>)
    : Task =
    task {
        let server = start shutdownBudget extra
        try do! verify server
        finally cleanup false server
    } :> Task

let becomeReady (server: Server) : Task<unit> =
    task {
        do! wait server.Probe.Listening.Task
        server.Probe.Release.SetResult(Ok ())
        do! wait server.Probe.WorkerStarted.Task
    }
