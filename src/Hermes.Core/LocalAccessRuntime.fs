namespace Hermes.Core.LocalAccess

open System
open System.Runtime.ExceptionServices
open System.Security.Cryptography
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

type AccessReply = Task<Result<Response, AccessError>>

[<NoEquality; NoComparison>]
type PublicAccess =
    { Status: SessionContext -> CancellationToken -> AccessReply
      Create: CreateRequest -> CancellationToken -> AccessReply
      Exchange: Result<ExchangeRequest, InvalidExchange> -> CancellationToken -> AccessReply
      Revoke: SessionContext -> CancellationToken -> AccessReply
      Admit: Set<Capability> -> SessionContext -> CancellationToken -> AccessReply }

[<NoEquality; NoComparison>]
type OwnerAccess =
    { ControlStatus: InstanceId option -> CancellationToken -> AccessReply
      Resolve: InstanceId -> DisplayCode -> CancellationToken -> AccessReply
      Approve: ApprovalRequest -> CancellationToken -> AccessReply }

[<NoEquality; NoComparison>]
type AccessLifecycle =
    { StartReady: CancellationToken -> AccessReply
      Stop: unit -> Task<unit> }

/// Signal shutdown without synchronously joining this runtime.
type RuntimeSupervisor = { Unavailable: unit -> unit }

[<NoEquality; NoComparison>]
type LocalAccessRuntime =
    { Public: PublicAccess
      Owner: OwnerAccess
      Lifecycle: AccessLifecycle }

module private RuntimeTransition =
    let private commit (decision: Result<Transition, Rejection>) : Outcome =
        decision
        |> Result.map (fun (accepted: Transition) ->
            { State = accepted.Next; Result = Ok accepted.Response })
        |> Result.defaultWith (fun (rejected: Rejection) ->
            { State = rejected.Next; Result = Error rejected.Error })

    let private capture (clock: Clock) (entropy: Entropy) : Result<Evidence, unit> =
        try
            Evidence.capture clock entropy |> Result.mapError (fun _ -> ())
        with
        | :? CryptographicException
        | :? InvalidOperationException
        | :? ArgumentException
        | :? NotSupportedException
        | :? OverflowException -> Error ()

    let evaluate (clock: Clock) (entropy: Entropy) (command: Command)
        (state: AuthorityState) : Result<Outcome, exn option> =
        try
            capture clock entropy
            |> Result.map (fun evidence -> Domain.transition evidence command state |> commit)
            |> Result.mapError (fun () -> None)
        with error ->
            Error(Some error)

module private RuntimeQueue =
    [<Literal>]
    let Capacity = 64

    [<NoEquality; NoComparison>]
    type Request =
        { Command: Command
          Cancellation: CancellationToken
          Completion: TaskCompletionSource<Result<Response, AccessError>>
          Registration: CancellationTokenRegistration }

    [<NoEquality; NoComparison>]
    type Inbox =
        { Channel: Channel<Request>
          Closing: TaskCompletionSource<unit>
          Gate: obj }

    let create () : Inbox =
        let options = BoundedChannelOptions(Capacity)
        options.SingleReader <- true
        options.SingleWriter <- false
        options.AllowSynchronousContinuations <- false
        options.FullMode <- BoundedChannelFullMode.Wait
        { Channel = Channel.CreateBounded<Request>(options)
          Closing = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
          Gate = obj () }

    let isClosed (inbox: Inbox) : bool = inbox.Closing.Task.IsCompleted

    let close (inbox: Inbox) : unit =
        lock inbox.Gate (fun () ->
            inbox.Closing.TrySetResult(()) |> ignore
            inbox.Channel.Writer.TryComplete() |> ignore)

    let private enqueue (inbox: Inbox) (command: Command)
        (cancellation: CancellationToken) : AccessReply =
        let completion =
            TaskCompletionSource<Result<Response, AccessError>>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        let registration =
            cancellation.Register(
                Action(fun () -> completion.TrySetCanceled(cancellation) |> ignore))
        let request =
            { Command = command; Cancellation = cancellation
              Completion = completion; Registration = registration }
        if inbox.Channel.Writer.TryWrite(request) then completion.Task
        else
            registration.Dispose()
            Task.FromResult(Error AccessError.AccessCapacityReached)

    let submit (inbox: Inbox) (command: Command)
        (cancellation: CancellationToken) : AccessReply =
        lock inbox.Gate (fun () ->
            if cancellation.IsCancellationRequested then
                Task.FromCanceled<Result<Response, AccessError>>(cancellation)
            elif isClosed inbox then
                Task.FromResult(Error AccessError.AccessUnavailable)
            else
                enqueue inbox command cancellation)

    let complete (request: Request) (reply: Result<Response, AccessError>) : unit =
        request.Completion.TrySetResult(reply) |> ignore
        request.Registration.Dispose()

    let cancel (request: Request) : unit =
        request.Completion.TrySetCanceled(request.Cancellation) |> ignore
        request.Registration.Dispose()

module private RuntimeWorker =
    [<NoEquality; NoComparison>]
    type Progress =
        { State: AuthorityState
          Failure: exn option }

    let private notify (supervisor: RuntimeSupervisor) (failure: exn option) : exn option =
        try
            supervisor.Unavailable()
            failure
        with error ->
            failure |> Option.orElse (Some error)

    let private fail (inbox: RuntimeQueue.Inbox) (supervisor: RuntimeSupervisor)
        (progress: Progress) (request: RuntimeQueue.Request) (failure: exn option) : Progress =
        let stopped = StateModel.clear progress.State
        RuntimeQueue.close inbox
        RuntimeQueue.complete request (Error AccessError.AccessUnavailable)
        { State = stopped; Failure = notify supervisor failure }

    let private execute (inbox: RuntimeQueue.Inbox) (clock: Clock) (entropy: Entropy)
        (supervisor: RuntimeSupervisor) (progress: Progress)
        (request: RuntimeQueue.Request) : Progress =
        match RuntimeTransition.evaluate clock entropy request.Command progress.State with
        | Ok outcome ->
            let committed = { progress with State = outcome.State }
            RuntimeQueue.complete request outcome.Result
            committed
        | Error failure ->
            fail inbox supervisor progress request failure

    let private processRequest (inbox: RuntimeQueue.Inbox) (clock: Clock) (entropy: Entropy)
        (supervisor: RuntimeSupervisor) (progress: Progress)
        (request: RuntimeQueue.Request) : Progress =
        if request.Cancellation.IsCancellationRequested then
            RuntimeQueue.cancel request
            progress
        elif RuntimeQueue.isClosed inbox then
            let stopped = { progress with State = StateModel.clear progress.State }
            RuntimeQueue.complete request (Error AccessError.AccessUnavailable)
            stopped
        else
            execute inbox clock entropy supervisor progress request

    let rec private loop (inbox: RuntimeQueue.Inbox) (clock: Clock) (entropy: Entropy)
        (supervisor: RuntimeSupervisor) (progress: Progress) : Async<unit> =
        async {
            let! available = inbox.Channel.Reader.WaitToReadAsync().AsTask() |> Async.AwaitTask
            if available then
                let! request = inbox.Channel.Reader.ReadAsync().AsTask() |> Async.AwaitTask
                let next = processRequest inbox clock entropy supervisor progress request
                return! loop inbox clock entropy supervisor next
            else
                let stopped = { progress with State = StateModel.clear progress.State }
                stopped.Failure
                |> Option.iter (fun error -> ExceptionDispatchInfo.Capture(error).Throw())
        }

    let start (inbox: RuntimeQueue.Inbox) (clock: Clock) (entropy: Entropy)
        (supervisor: RuntimeSupervisor) (state: AuthorityState) : Task<unit> =
        let initial = { State = state; Failure = None }
        Async.StartAsTask(
            loop inbox clock entropy supervisor initial,
            cancellationToken = CancellationToken.None)

[<RequireQualifiedAccess>]
module LocalAccessRuntime =
    /// Queue entries exclude the single currently executing request.
    let QueueCapacity = RuntimeQueue.Capacity

    let private publicAccess
        (submit: Command -> CancellationToken -> AccessReply) : PublicAccess =
        { Status = Command.Status >> submit
          Create = Command.Create >> submit
          Exchange = fun input ->
              input |> Result.map Command.Exchange
              |> Result.defaultWith Command.RejectExchange |> submit
          Revoke = Command.Revoke >> submit
          Admit = fun required context -> submit (Command.Admit(required, context)) }

    let private ownerAccess
        (submit: Command -> CancellationToken -> AccessReply) : OwnerAccess =
        { ControlStatus = Command.ControlStatus >> submit
          Resolve = fun instance code -> submit (Command.Resolve(instance, code))
          Approve = Command.Approve >> submit }

    let private capabilities (inbox: RuntimeQueue.Inbox)
        (worker: Task<unit>) : LocalAccessRuntime =
        let submit = RuntimeQueue.submit inbox
        { Public = publicAccess submit
          Owner = ownerAccess submit
          Lifecycle =
            { StartReady = submit Command.EnterReady
              Stop = fun () -> RuntimeQueue.close inbox; worker } }

    let internal createWith (clock: Clock) (entropy: Entropy)
        (supervisor: RuntimeSupervisor) (identity: Identity)
        (policy: Set<Capability>) (extraOrigins: Set<BrowserOrigin>)
        : Result<LocalAccessRuntime, AccessError> =
        Domain.create identity policy extraOrigins
        |> Result.map (fun state ->
            let inbox = RuntimeQueue.create ()
            let worker = RuntimeWorker.start inbox clock entropy supervisor state
            capabilities inbox worker)

    let create (supervisor: RuntimeSupervisor) (identity: Identity)
        (policy: Set<Capability>) (extraOrigins: Set<BrowserOrigin>)
        : Result<LocalAccessRuntime, AccessError> =
        let provider = TimeProvider.System
        let started = provider.GetTimestamp()
        let clock: Clock =
            { Read = fun () ->
                { Elapsed = provider.GetElapsedTime(started)
                  Utc = provider.GetUtcNow() } }
        let entropy: Entropy = { Bytes = fun size -> RandomNumberGenerator.GetBytes(size) }
        createWith clock entropy supervisor identity policy extraOrigins
