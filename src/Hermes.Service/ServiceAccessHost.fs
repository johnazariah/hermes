namespace Hermes.Service

open System
open System.Diagnostics
open System.Net
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open Microsoft.Extensions.Configuration
open Hermes.Core
open Hermes.Core.LocalAccess
open Hermes.Core.OwnerControlPipe

type AccessHostOptions =
    { ConfigDirectory: string
      Port: int
      ExtraOrigins: string list
      Models: ModelAvailability }

type AccessHostWorker =
    { Name: string
      Start: CancellationToken -> Task }

type AccessHostWorkload =
    { Workers: AccessHostWorker list
      StopQueues: unit -> unit }

[<RequireQualifiedAccess>]
type AccessHostFailure =
    | AuthorityUnavailable
    | OwnerControlFailed of PipeFailure
    | WorkerFaulted of string
    | UnexpectedWorkerExit of string
    | StartupFailed
    | ShutdownFailed

type AccessHostResult =
    { ExitCode: int
      Disposition: Lifetime.Disposition }

/// Public HTTP composition capability. Owner authority is deliberately absent.
type HostContext =
    { Identity: Identity
      Target: OwnerControlTarget.Target
      Cancellation: CancellationToken
      Public: unit -> PublicAccess option
      RequestsDrained: Task
      Failure: Task<AccessHostFailure> }

type AccessHostControlFactory =
    OwnerControlTarget.Target ->
        Dispatch ->
        (PipeFailure -> unit) ->
        Factory

type private PreparedHost =
    { Identity: Identity
      Target: OwnerControlTarget.Target
      Origins: Set<BrowserOrigin> }

type private ActiveControl =
    { Running: Running
      Observed: Task }

type private HostSignals =
    { Gate: obj
      Closed: CancellationTokenSource
      Requests: HttpRequestLifetime.Capability
      Failure: TaskCompletionSource<AccessHostFailure>
      Runtime: TaskCompletionSource<LocalAccessRuntime>
      ControlClaimed: TaskCompletionSource<unit>
      Control: TaskCompletionSource<ActiveControl option>
      StopApplication: unit -> unit }

type private OwnedAccessHost =
    { Context: HostContext
      Runtime: LocalAccessRuntime
      Signals: HostSignals
      Factory: AccessHostControlFactory }

module private AccessHostIdentity =
    let policy: Set<Capability> =
        Set.ofList
            [ Capability.ConfigRead
              Capability.ModelInvoke
              Capability.Mutate ]

    let private origins (values: string list) : Result<Set<BrowserOrigin>, string> =
        values
        |> List.fold (fun state value ->
            state
            |> Result.bind (fun collected ->
                BrowserOrigin.tryParse value
                |> Result.map (fun parsed -> Set.add parsed collected)
                |> Result.mapError (fun _ -> "Invalid configured browser origin.")))
            (Ok Set.empty)

    let private identity
        (target: OwnerControlTarget.Target)
        : Result<Identity, string> =
        try
            use current = Process.GetCurrentProcess()
            let started = DateTimeOffset(current.StartTime.ToUniversalTime())
            RandomNumberGenerator.GetBytes(16)
            |> InstanceId.ofBytes
            |> Result.bind (fun instance ->
                ApiAudience.create target.Port
                |> Result.bind (fun audience ->
                    Identity.create instance current.Id started audience target.ConfigKey))
            |> Result.mapError (fun _ -> "Cannot establish service identity.")
        with _ ->
            Error "Cannot establish service identity."

    let prepare (options: AccessHostOptions) : Result<PreparedHost, string> =
        OwnerControlTarget.tryCreate options.ConfigDirectory options.Port
        |> Result.mapError (fun _ -> "Invalid owner-control target.")
        |> Result.bind (fun target ->
            origins options.ExtraOrigins
            |> Result.bind (fun extraOrigins ->
                identity target
                |> Result.map (fun value ->
                    { Identity = value
                      Target = target
                      Origins = extraOrigins })))

module private AccessHostSignals =
    let cell<'Value> () : TaskCompletionSource<'Value> =
        TaskCompletionSource<'Value>(
            TaskCreationOptions.RunContinuationsAsynchronously)

    let create (instance: InstanceId) (stopApplication: unit -> unit) : HostSignals =
        { Gate = obj ()
          Closed = new CancellationTokenSource()
          Requests = HttpRequestLifetime.create instance
          Failure = cell ()
          Runtime = cell ()
          ControlClaimed = cell ()
          Control = cell ()
          StopApplication = stopApplication }

    let private stopRuntime (signals: HostSignals) : unit =
        if signals.Runtime.Task.IsCompletedSuccessfully then
            // Signal only: never synchronously join the authority worker here.
            signals.Runtime.Task.Result.Lifecycle.Stop() |> ignore

    let close (signals: HostSignals) : unit =
        lock signals.Gate (fun () ->
            // Close handler admission before cancellation callbacks or host stop.
            signals.Requests.Close() |> ignore
            try
                if not signals.Closed.IsCancellationRequested then
                    signals.Closed.Cancel()
            with _ ->
                signals.Failure.TrySetResult(AccessHostFailure.ShutdownFailed)
                |> ignore
            stopRuntime signals
            if signals.ControlClaimed.TrySetResult(()) then
                signals.Control.TrySetResult(None) |> ignore)

    let fail (signals: HostSignals) (failure: AccessHostFailure) : unit =
        signals.Failure.TrySetResult(failure) |> ignore
        close signals
        signals.StopApplication()

module private AccessHostControl =
    let private dispatch
        (identity: Identity)
        (owner: OwnerAccess)
        (request: Hermes.Core.LocalAccessWire.Request)
        (cancellation: CancellationToken)
        : Task<Hermes.Core.LocalAccessWire.Response> =
        task {
            let! outcome =
                OwnerAccessAdapter.dispatch identity owner request cancellation
            return
                outcome
                |> Result.defaultWith (fun _ ->
                    invalidOp "Owner control request failed validation.")
        }

    let private report
        (signals: HostSignals)
        (unexpectedCompletion: bool)
        (outcome: Result<unit, PipeFailure>)
        : unit =
        match outcome with
        | Error PipeFailure.Cancelled when signals.Closed.IsCancellationRequested -> ()
        | Error failure ->
            AccessHostSignals.fail signals
                (AccessHostFailure.OwnerControlFailed failure)
        | Ok () when unexpectedCompletion && not signals.Closed.IsCancellationRequested ->
            AccessHostSignals.fail signals
                (AccessHostFailure.OwnerControlFailed PipeFailure.ListenerFailed)
        | Ok () -> ()

    let private observe
        (signals: HostSignals)
        (unexpectedCompletion: bool)
        (work: Task<Result<unit, PipeFailure>>)
        : Task =
        task {
            try
                let! outcome = work
                report signals unexpectedCompletion outcome
            with
            | :? OperationCanceledException when signals.Closed.IsCancellationRequested -> ()
            | _ ->
                AccessHostSignals.fail signals
                    (AccessHostFailure.OwnerControlFailed PipeFailure.ListenerFailed)
        } :> Task

    let create
        (factory: AccessHostControlFactory)
        (prepared: PreparedHost)
        (stopApplication: unit -> unit)
        : OwnedAccessHost =
        let signals =
            AccessHostSignals.create prepared.Identity.InstanceId stopApplication
        let supervisor: RuntimeSupervisor =
            { Unavailable = fun () ->
                AccessHostSignals.fail signals AccessHostFailure.AuthorityUnavailable }
        let runtime =
            LocalAccessRuntime.create supervisor prepared.Identity
                AccessHostIdentity.policy prepared.Origins
            |> Result.defaultWith (fun _ ->
                signals.Closed.Dispose()
                invalidOp "Cannot initialise local access authority.")
        signals.Runtime.SetResult(runtime)
        let context: HostContext =
            { Identity = prepared.Identity; Target = prepared.Target
              Cancellation = signals.Closed.Token; Failure = signals.Failure.Task
              RequestsDrained = signals.Requests.Drained
              Public = fun () ->
                  if signals.Closed.IsCancellationRequested then None
                  else Some runtime.Public }
        { Context = context; Runtime = runtime; Signals = signals; Factory = factory }

    let start (host: OwnedAccessHost) : ActiveControl =
        lock host.Signals.Gate (fun () ->
            host.Context.Cancellation.ThrowIfCancellationRequested()
            if not (host.Signals.ControlClaimed.TrySetResult(())) then
                invalidOp "Owner control can start only once."
            try
                let factory =
                    host.Factory host.Context.Target
                        (dispatch host.Context.Identity host.Runtime.Owner)
                        (AccessHostFailure.OwnerControlFailed
                         >> AccessHostSignals.fail host.Signals)
                let running = factory.Start host.Context.Cancellation
                let active =
                    { Running = running
                      Observed = observe host.Signals true running.RunTask }
                host.Signals.Control.SetResult(Some active)
                active
            with _ ->
                host.Signals.Control.TrySetResult(None) |> ignore
                reraise ())

    let awaitReady (host: OwnedAccessHost) : Task<unit> =
        task {
            let active = start host
            let! outcome =
                active.Running.Ready.WaitAsync(host.Context.Cancellation)
            host.Context.Cancellation.ThrowIfCancellationRequested()
            outcome
            |> Result.defaultWith (fun failure ->
                AccessHostSignals.fail host.Signals
                    (AccessHostFailure.OwnerControlFailed failure)
                invalidOp "Owner control did not become ready.")
        }

    let private stopPipe (signals: HostSignals) (active: ActiveControl) : Task =
        task {
            try
                let pending = active.Running.Stop()
                do! observe signals false pending
            with _ ->
                AccessHostSignals.fail signals
                    (AccessHostFailure.OwnerControlFailed PipeFailure.ListenerFailed)
        } :> Task

    let private joinControl (signals: HostSignals) : Task =
        task {
            let! active = signals.Control.Task
            do!
                active
                |> Option.map (fun control ->
                    Task.WhenAll(
                        [| stopPipe signals control
                           control.Observed |]))
                |> Option.defaultValue Task.CompletedTask
        } :> Task

    let private joinRuntime (host: OwnedAccessHost) : Task =
        task {
            try
                do! host.Runtime.Lifecycle.Stop()
            with _ ->
                AccessHostSignals.fail host.Signals
                    AccessHostFailure.AuthorityUnavailable
        } :> Task

    let join (host: OwnedAccessHost) : Task =
        Task.WhenAll(
            [| joinControl host.Signals
               joinRuntime host |])

module private AccessHostExecution =
    let private validate (app: WebApplication) : unit =
        let routes: IEndpointRouteBuilder = app
        routes.DataSources
        |> Seq.collect (fun source -> source.Endpoints)
        |> HttpAccessPolicy.validateEndpoints
        |> Result.defaultWith (fun _ ->
            invalidOp "Every application endpoint requires exactly one HTTP access classification.")

    let private enterReady (host: OwnedAccessHost) : Task<unit> =
        task {
            let! outcome =
                host.Runtime.Lifecycle.StartReady host.Context.Cancellation
            host.Context.Cancellation.ThrowIfCancellationRequested()
            match outcome with
            | Ok(Response.PhaseChanged AccessPhase.Ready) -> ()
            | _ ->
                AccessHostSignals.fail host.Signals
                    AccessHostFailure.AuthorityUnavailable
                invalidOp "Local access authority did not become ready."
        }

    let private observeWorker
        (host: OwnedAccessHost)
        (worker: AccessHostWorker)
        : Task =
        task {
            try
                host.Context.Cancellation.ThrowIfCancellationRequested()
                do! worker.Start host.Context.Cancellation
                if not host.Context.Cancellation.IsCancellationRequested then
                    AccessHostSignals.fail host.Signals
                        (AccessHostFailure.UnexpectedWorkerExit worker.Name)
            with
            | :? OperationCanceledException when host.Context.Cancellation.IsCancellationRequested -> ()
            | _ ->
                AccessHostSignals.fail host.Signals
                    (AccessHostFailure.WorkerFaulted worker.Name)
        } :> Task

    let private startWorkers
        (host: OwnedAccessHost)
        (workers: AccessHostWorker list)
        : Task list =
        lock host.Signals.Gate (fun () ->
            if host.Context.Cancellation.IsCancellationRequested then []
            else
                workers
                |> List.map (fun worker ->
                    Task.Run(Func<Task>(fun () -> observeWorker host worker))))

    let private start
        (host: OwnedAccessHost)
        (app: WebApplication)
        (workload: AccessHostWorkload)
        : Task<Task list> =
        task {
            validate app
            do! app.StartAsync(host.Context.Cancellation)
            do! AccessHostControl.awaitReady host
            do! enterReady host
            return startWorkers host workload.Workers
        }

    let private startOrFail
        (host: OwnedAccessHost)
        (app: WebApplication)
        (workload: AccessHostWorkload)
        : Task list =
        try
            (start host app workload).GetAwaiter().GetResult()
        with
        | :? OperationCanceledException when host.Context.Cancellation.IsCancellationRequested -> []
        | _ ->
            AccessHostSignals.fail host.Signals AccessHostFailure.StartupFailed
            []

    let private waitForStopping (app: WebApplication) : unit =
        let stopped = AccessHostSignals.cell<unit> ()
        use registration =
            app.Lifetime.ApplicationStopping.Register(
                Action(fun () -> stopped.TrySetResult(()) |> ignore))
        stopped.Task.GetAwaiter().GetResult()

    let private stopQueues (host: OwnedAccessHost) (stop: unit -> unit) : unit =
        try stop ()
        with _ ->
            AccessHostSignals.fail host.Signals AccessHostFailure.ShutdownFailed

    let private stopHttp (host: OwnedAccessHost) (app: WebApplication) : Task =
        task {
            try
                do! app.StopAsync(CancellationToken.None)
            with _ ->
                AccessHostSignals.fail host.Signals AccessHostFailure.ShutdownFailed
        } :> Task

    let private disposeHttp (host: OwnedAccessHost) (app: WebApplication) : bool =
        try
            app.DisposeAsync().AsTask().GetAwaiter().GetResult()
            host.Signals.Closed.Dispose()
            true
        with _ ->
            AccessHostSignals.fail host.Signals AccessHostFailure.ShutdownFailed
            false

    let private result
        (host: OwnedAccessHost)
        (logger: Algebra.Logger)
        (disposition: Lifetime.Disposition)
        : AccessHostResult =
        match disposition with
        | Lifetime.Abandoned _ ->
            { ExitCode = Lifetime.AbandonedExitCode; Disposition = disposition }
        | Lifetime.Quiesced ->
            let failed = host.Context.Failure.IsCompletedSuccessfully
            if failed then
                logger.error $"Service host failed: {host.Context.Failure.Result}"
            { ExitCode = if failed then 1 else 0
              Disposition = disposition }

    let private finish
        (shutdownBudget: TimeSpan)
        (host: OwnedAccessHost)
        (app: WebApplication)
        (logger: Algebra.Logger)
        (workers: Task list)
        : AccessHostResult =
        AccessHostSignals.close host.Signals
        let tasks =
            workers
            @ [ AccessHostControl.join host
                stopHttp host app
                host.Signals.Requests.Close() ]
        let disposition =
            Lifetime.shutdown logger shutdownBudget tasks
        let released =
            match disposition with
            | Lifetime.Quiesced when not (disposeHttp host app) -> Lifetime.Abandoned 1
            | other -> other
        result host logger released

    let private configureOrFail
        (host: OwnedAccessHost)
        (app: WebApplication)
        (configure: WebApplication -> HostContext -> AccessHostWorkload)
        : AccessHostWorkload =
        try configure app host.Context
        with _ ->
            AccessHostSignals.fail host.Signals AccessHostFailure.StartupFailed
            { Workers = []; StopQueues = ignore }

    let run
        (shutdownBudget: TimeSpan)
        (host: OwnedAccessHost)
        (app: WebApplication)
        (logger: Algebra.Logger)
        (configure: WebApplication -> HostContext -> AccessHostWorkload)
        : AccessHostResult =
        use stopping =
            app.Lifetime.ApplicationStopping.Register(
                Action(fun () -> AccessHostSignals.close host.Signals))
        let workload = configureOrFail host app configure
        use queues =
            host.Context.Cancellation.Register(
                Action(fun () -> stopQueues host workload.StopQueues))
        let workers = startOrFail host app workload
        waitForStopping app
        finish shutdownBudget host app logger workers

/// The single HTTP/authority/worker composition root.
[<RequireQualifiedAccess>]
module ServiceAccessHost =
    let private build (options: AccessHostOptions) : WebApplication =
        let builder = WebApplication.CreateBuilder()
        let endpoints = ConfigurationBuilder().Build()
        HttpAccessPolicy.addStartupValidation builder.Services
        builder.WebHost.ConfigureKestrel(fun server ->
            // Do not inherit additional Kestrel listeners from external configuration.
            server.Configure(endpoints) |> ignore
            server.Listen(IPAddress.Loopback, options.Port))
        |> ignore
        builder.Build()

    let private securityHeaders
        (context: HttpContext)
        (next: RequestDelegate)
        : Task =
        context.Response.Headers["X-Content-Type-Options"] <-
            Microsoft.Extensions.Primitives.StringValues("nosniff")
        next.Invoke(context)

    let private attach
        (options: AccessHostOptions)
        (app: WebApplication)
        (context: HostContext)
        (requests: HttpRequestLifetime.Capability)
        : unit =
        LocalBoundary.attach app options.ExtraOrigins
        app.Use(Func<HttpContext, RequestDelegate, Task>(securityHeaders))
        |> ignore
        app.UseRouting() |> ignore
        HttpAccessPolicy.attach app context.Identity.InstanceId
            context.Public options.Models
        app.Use(Func<HttpContext, RequestDelegate, Task>(requests.Invoke))
        |> ignore

    let private mapHealth (app: WebApplication) (context: HostContext) : unit =
        app.MapGet("/health", Func<IResult>(fun () ->
            Results.Json(
                {| status = "healthy"
                   service = "hermes"
                   processId = context.Identity.ProcessId |})))
        |> HttpAccessPolicy.classify HttpAccessClassification.AnonymousArchiveRead
        |> ignore

    let private createOwned
        (factory: AccessHostControlFactory)
        (prepared: PreparedHost)
        (app: WebApplication)
        : OwnedAccessHost =
        try
            AccessHostControl.create factory prepared app.Lifetime.StopApplication
        with _ ->
            app.DisposeAsync().AsTask().GetAwaiter().GetResult()
            reraise ()

    let runWithControlAndShutdownBudget
        (shutdownBudget: TimeSpan)
        (factory: AccessHostControlFactory)
        (options: AccessHostOptions)
        (logger: Algebra.Logger)
        (configure: WebApplication -> HostContext -> AccessHostWorkload)
        : AccessHostResult =
        let prepared =
            AccessHostIdentity.prepare options |> Result.defaultWith invalidOp
        let app = build options
        let host = createOwned factory prepared app
        let configureApplication (http: WebApplication) (context: HostContext) =
            attach options http context host.Signals.Requests
            LocalAccessHttp.mapEndpoints http context.Identity.InstanceId
            mapHealth http context
            configure http context
        AccessHostExecution.run shutdownBudget host app logger configureApplication

    let runWithControl
        (factory: AccessHostControlFactory)
        (options: AccessHostOptions)
        (logger: Algebra.Logger)
        (configure: WebApplication -> HostContext -> AccessHostWorkload)
        : AccessHostResult =
        runWithControlAndShutdownBudget (TimeSpan.FromSeconds 60.0)
            factory options logger configure

    let run
        (options: AccessHostOptions)
        (logger: Algebra.Logger)
        (configure: WebApplication -> HostContext -> AccessHostWorkload)
        : AccessHostResult =
        runWithControl Factory.createCancellable options logger configure
