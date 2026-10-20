namespace Hermes.Core.OwnerControlPipe

open System
open System.Buffers.Binary
open System.IO
open System.IO.Pipes
open System.Security
open System.Security.Principal
open System.Threading
open System.Threading.Tasks
open Hermes.Core
open Hermes.Core.LocalAccessWire

[<RequireQualifiedAccess>]
type PipeFailure =
    | Cancelled
    | TimedOut
    | Unavailable
    | PrincipalRejected
    | OwnershipUnavailable
    | InvalidTarget
    | InvalidFrame
    | InvalidWire of CodecFailure
    | DispatchFailed
    | ListenerFailed
    | SupervisorFailed

[<RequireQualifiedAccess>]
module PipeFailure =
    let message (_: PipeFailure) = "Owner control transport failed."

/// Must not wait for human input. Awaiting dispatch must be bounded or observe its token.
type Dispatch = Request -> CancellationToken -> Task<Response>

type Running =
    { Ready: Task<Result<unit, PipeFailure>>
      RunTask: Task<Result<unit, PipeFailure>>
      Stop: unit -> Task<Result<unit, PipeFailure>> }
    interface IAsyncDisposable with
        member running.DisposeAsync() =
            let joined: Task = running.Stop()
            ValueTask(joined)

type Factory =
    { Start: CancellationToken -> Running
      Send: Request -> PeerExpectation -> CancellationToken -> Task<Result<Response, PipeFailure>> }

/// Client-only capabilities: construction never creates or starts a listener.
/// Discovery establishes peer assertions; it does not approve any authority.
type ClientOnly =
    { DiscoverStatus: Request -> CancellationToken -> Task<Result<Response, PipeFailure>>
      Send: Request -> PeerExpectation -> CancellationToken -> Task<Result<Response, PipeFailure>> }

type internal Readiness = TaskCompletionSource<Result<unit, PipeFailure>>

module internal Deadlines =
    let connect = TimeSpan.FromSeconds 2.0
    let exchange = TimeSpan.FromSeconds 5.0
    let header = TimeSpan.FromSeconds 1.0
    let payload = TimeSpan.FromSeconds 2.0

    let start (budget: TimeSpan) (parent: CancellationToken) =
        let deadline = CancellationTokenSource.CreateLinkedTokenSource(parent)
        deadline.CancelAfter budget
        deadline

module internal Native =
    exception Fault of PipeFailure

    let options = PipeOptions.Asynchronous ||| PipeOptions.CurrentUserOnly

    // The standard seven-argument overload defaults to non-inheritable handles.
    // No FirstPipeInstance: retain the Unix runtime's standard stale-path recovery.
    let server (target: OwnerControlTarget.Target) =
        new NamedPipeServerStream(
            target.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, options, 0, 0)

    let client (target: OwnerControlTarget.Target) =
        new NamedPipeClientStream(
            ".", target.PipeName, PipeDirection.InOut, options,
            TokenImpersonationLevel.Anonymous, HandleInheritability.None)

    let failure (error: exn) =
        match error with
        | Fault failure -> failure
        | :? UnauthorizedAccessException -> PipeFailure.PrincipalRejected
        | :? NotSupportedException
        | :? SecurityException -> PipeFailure.OwnershipUnavailable
        | :? ArgumentException -> PipeFailure.InvalidTarget
        | _ -> PipeFailure.Unavailable

    let interrupted (parent: CancellationToken) (deadline: CancellationToken) (error: exn) =
        if parent.IsCancellationRequested then PipeFailure.Cancelled
        elif deadline.IsCancellationRequested then PipeFailure.TimedOut
        else failure error

module internal Framing =
    let private headerBytes (length: int) =
        if length < 1 || length > 8192 then raise (Native.Fault PipeFailure.InvalidFrame)
        let header = Array.zeroCreate<byte> 4
        BinaryPrimitives.WriteUInt32BigEndian(Span<byte>(header), uint32 length)
        header

    let private payloadLength (header: byte array) =
        let length = BinaryPrimitives.ReadUInt32BigEndian(ReadOnlySpan<byte>(header))
        if length < 1u || length > 8192u then raise (Native.Fault PipeFailure.InvalidFrame)
        int length

    let private readPart (stream: Stream) (buffer: byte array) budget (parent: CancellationToken) =
        task {
            use deadline = Deadlines.start budget parent
            try
                do! stream.ReadExactlyAsync(Memory<byte>(buffer), deadline.Token).AsTask()
            with
            | :? OperationCanceledException when deadline.IsCancellationRequested ->
                parent.ThrowIfCancellationRequested()
                return raise (Native.Fault PipeFailure.TimedOut)
            | :? EndOfStreamException ->
                return raise (Native.Fault PipeFailure.InvalidFrame)
        }

    let read (stream: Stream) (cancellation: CancellationToken) : Task<byte array> =
        task {
            let header = Array.zeroCreate<byte> 4
            do! readPart stream header Deadlines.header cancellation
            let length = payloadLength header
            let payload = Array.zeroCreate<byte> length
            do! readPart stream payload Deadlines.payload cancellation
            return payload
        }

    let write (stream: Stream) (payload: byte array) (cancellation: CancellationToken) =
        task {
            let header = headerBytes payload.Length
            do! stream.WriteAsync(ReadOnlyMemory<byte>(header), cancellation).AsTask()
            do! stream.WriteAsync(ReadOnlyMemory<byte>(payload), cancellation).AsTask()
            do! stream.FlushAsync(cancellation)
        }

module internal Client =
    type private Prepared =
        { Payload: byte array
          Decode: byte array -> Result<Response, CodecFailure> }

    let private connect (pipe: NamedPipeClientStream) (parent: CancellationToken) =
        task {
            use deadline = Deadlines.start Deadlines.connect parent
            use interrupted = deadline.Token.Register(fun () -> pipe.Dispose())
            try
                do! pipe.ConnectAsync(deadline.Token)
            with error ->
                return raise (Native.Fault(Native.interrupted parent deadline.Token error))
        }

    let private exchange (pipe: NamedPipeClientStream) (prepared: Prepared) (parent: CancellationToken) =
        task {
            use deadline = Deadlines.start Deadlines.exchange parent
            use interrupted = deadline.Token.Register(fun () -> pipe.Dispose())
            try
                do! Framing.write pipe prepared.Payload deadline.Token
                let! response = Framing.read pipe deadline.Token
                return prepared.Decode response |> Result.mapError PipeFailure.InvalidWire
            with error ->
                return raise (Native.Fault(Native.interrupted parent deadline.Token error))
        }

    let private run target (prepared: Prepared) (cancellation: CancellationToken) =
        task {
            try
                cancellation.ThrowIfCancellationRequested()
                use pipe = Native.client target
                do! connect pipe cancellation
                return! exchange pipe prepared cancellation
            with error ->
                let failure =
                    if cancellation.IsCancellationRequested then PipeFailure.Cancelled
                    else Native.failure error
                return Result.Error failure
        }

    let private validate (target: OwnerControlTarget.Target) (request: Request) (peer: PeerExpectation) =
        if peer.ConfigKey <> target.ConfigKey || peer.Origin <> target.ApiOrigin then
            Result.Error(PipeFailure.InvalidWire CodecFailure.IdentityMismatch)
        else
            Codec.encodeRequest request |> Result.mapError PipeFailure.InvalidWire

    let private execute target decode encoded cancellation =
        encoded
        |> Result.map (fun payload ->
            run target { Payload = payload; Decode = decode } cancellation)
        |> Result.defaultWith (fun failure -> Task.FromResult(Result.Error failure))

    let send target request peer cancellation : Task<Result<Response, PipeFailure>> =
        execute target (Codec.decodeResponse request peer) (validate target request peer) cancellation

    let discoverStatus target request cancellation : Task<Result<Response, PipeFailure>> =
        let encoded =
            Codec.encodeDiscoveryRequest request
            |> Result.mapError PipeFailure.InvalidWire
        execute target (Codec.decodeDiscoveryResponse request target) encoded cancellation

module internal Server =
    let private wait (pipe: NamedPipeServerStream) (ready: Readiness) cancellation =
        task {
            try
                let pending = pipe.WaitForConnectionAsync(cancellation)
                if not pending.IsFaulted && not pending.IsCanceled then
                    ready.TrySetResult(Result.Ok ()) |> ignore
                do! pending
                return true
            with :? UnauthorizedAccessException ->
                // Rejected before acceptance: Disconnect would be invalid here.
                return false
        }

    let private invoke (dispatch: Dispatch) request (cancellation: CancellationToken) =
        task {
            try
                return! dispatch request cancellation
            with
            | :? OperationCanceledException when cancellation.IsCancellationRequested ->
                return raise (OperationCanceledException(cancellation))
            | _ -> return raise (Native.Fault PipeFailure.DispatchFailed)
        }

    let private handle dispatch (pipe: NamedPipeServerStream) (cancellation: CancellationToken) =
        task {
            let! payload = Framing.read pipe cancellation
            let request =
                Codec.decodeRequest payload
                |> Result.defaultWith (fun failure -> raise (Native.Fault(PipeFailure.InvalidWire failure)))
            cancellation.ThrowIfCancellationRequested()
            let! response = invoke dispatch request cancellation
            cancellation.ThrowIfCancellationRequested()
            let encoded =
                Codec.encodeResponse request response
                |> Result.defaultWith (fun _ -> raise (Native.Fault PipeFailure.DispatchFailed))
            do! Framing.write pipe encoded cancellation
        }

    let private exchange dispatch pipe cancellation =
        task {
            use deadline = Deadlines.start Deadlines.exchange cancellation
            try
                do! handle dispatch pipe deadline.Token
            with
            | Native.Fault PipeFailure.TimedOut
            | Native.Fault PipeFailure.InvalidFrame
            | Native.Fault(PipeFailure.InvalidWire _) -> ()
            | :? IOException -> ()
            | :? OperationCanceledException when deadline.IsCancellationRequested -> ()
        }

    let private accepted dispatch (pipe: NamedPipeServerStream) cancellation =
        task {
            try
                do! exchange dispatch pipe cancellation
            finally
                // Also required for PipeState.Broken; IsConnected is not a sufficient guard.
                pipe.Disconnect()
        }

    let private loop dispatch pipe ready (cancellation: CancellationToken) =
        task {
            // Constant-state iteration: no recursive task chain, spawned workers, or app queue.
            // This does not assert a bound on the operating system's connection backlog.
            for () in Seq.unfold (fun () -> Some((), ())) () do
                cancellation.ThrowIfCancellationRequested()
                let! connected = wait pipe ready cancellation
                if connected then do! accepted dispatch pipe cancellation
        }

    let private listen construct dispatch ready (cancellation: CancellationToken) =
        task {
            cancellation.ThrowIfCancellationRequested()
            use pipe = construct ()
            do! loop dispatch pipe ready cancellation
        }

    let private report (ready: Readiness) fatal (error: exn) =
        let failure =
            match error with
            | Native.Fault failure -> failure
            | :? NotSupportedException
            | :? SecurityException -> PipeFailure.OwnershipUnavailable
            | :? ArgumentException -> PipeFailure.InvalidTarget
            | _ -> PipeFailure.ListenerFailed
        ready.TrySetResult(Result.Error failure) |> ignore
        try fatal failure; Result.Error failure
        with _ -> Result.Error PipeFailure.SupervisorFailed

    let private supervise construct dispatch fatal (ready: Readiness) (lifetime: CancellationTokenSource) =
        task {
            use lifetime = lifetime
            try
                do! listen construct dispatch ready lifetime.Token
                return Result.Ok ()
            with
            | :? OperationCanceledException when lifetime.IsCancellationRequested ->
                ready.TrySetResult(Result.Error PipeFailure.Cancelled) |> ignore
                return Result.Ok ()
            | error -> return report ready fatal error
        }

    let private cancel (lifetime: CancellationTokenSource) =
        try lifetime.Cancel(); Result.Ok ()
        with
        | :? ObjectDisposedException -> Result.Ok ()
        | _ -> Result.Error PipeFailure.SupervisorFailed

    let private stop lifetime (work: Task<Result<unit, PipeFailure>>) () =
        task {
            let cancellation = cancel lifetime
            let! completed = work
            return cancellation |> Result.bind (fun () -> completed)
        }

    let start
        (construct: unit -> NamedPipeServerStream)
        (dispatch: Dispatch)
        (fatal: PipeFailure -> unit)
        (cancellation: CancellationToken)
        : Running =
        let lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation)
        let ready = Readiness(TaskCreationOptions.RunContinuationsAsynchronously)
        let work = supervise construct dispatch fatal ready lifetime
        { Ready = ready.Task
          RunTask = work
          Stop = stop lifetime work }

[<RequireQualifiedAccess>]
module Factory =
    /// No listener, dispatch callback, HTTP lookup, or filesystem lookup is constructed.
    /// Require a successful ready status before comparing public HTTP identity.
    /// Preserve that complete identity when using Send after discovery.
    let client (target: OwnerControlTarget.Target) : ClientOnly =
        { DiscoverStatus = Client.discoverStatus target
          Send = Client.send target }

    /// Construction does not bind. Call Start only after the exclusive HTTP listener is bound.
    /// CurrentUserOnly proves an OS principal, not executable identity or a kernel-attested PID.
    let createCancellable
        (target: OwnerControlTarget.Target)
        (dispatch: Dispatch)
        (fatal: PipeFailure -> unit)
        : Factory =
        { Start = Server.start (fun () -> Native.server target) dispatch fatal
          Send = Client.send target }

    /// Adapter for the separately bounded authority actor. Its task is always joined.
    let create
        (target: OwnerControlTarget.Target)
        (dispatch: Request -> Task<Response>)
        (fatal: PipeFailure -> unit)
        : Factory =
        createCancellable target (fun request _ -> dispatch request) fatal
