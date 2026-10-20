namespace Hermes.Tests

open System
open System.Buffers.Binary
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.IO.Pipes
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Core.LocalAccessWire
open Hermes.Core.OwnerControlPipe

module internal OwnerPipeFixture =
    type Rig =
        { Target: OwnerControlTarget.Target
          Api: Factory
          Run: Running
          Fatals: ConcurrentQueue<PipeFailure> }

    let ok value = value |> Result.defaultWith (fun failure -> failwithf "Unexpected pipe failure: %A" failure)

    let target () =
        let directory =
            Path.Combine(Path.GetTempPath(), "hermes-owner-pipe-tests", Guid.NewGuid().ToString("N"))
        OwnerControlTarget.tryCreate directory 31991 |> ok

    // JSON identity fields are synthetic assertions; these tests claim no kernel PID proof.
    let peer (target: OwnerControlTarget.Target) : PeerExpectation =
        { OwnerWireSamples.peer with ConfigKey = target.ConfigKey; Origin = target.ApiOrigin }

    let response (target: OwnerControlTarget.Target) (request: Request) : Response =
        let expected = peer target
        let identity: Identity =
            { InstanceId = OwnerWireSamples.id
              ProcessId = expected.ProcessId
              ProcessStartTimeUtc = expected.ProcessStartTimeUtc
              Origin = expected.Origin
              ConfigKey = expected.ConfigKey }
        let status: StatusResult = { Phase = Phase.Ready; Capabilities = [ "archive.read" ] }
        { RequestId = request.RequestId; Instance = identity; Outcome = Ok(Reply.Status status) }

    let request () : Request =
        { RequestId = Codec.newRequestId(); ExpectedInstanceId = None; Operation = Operation.Status }

    let start target dispatch =
        let fatals = ConcurrentQueue<PipeFailure>()
        let api = Factory.createCancellable target dispatch fatals.Enqueue
        { Target = target; Api = api; Run = api.Start CancellationToken.None; Fatals = fatals }

    let ready (rig: Rig) =
        task {
            let! result = rig.Run.Ready
            Assert.Equal<Result<unit, PipeFailure>>(Ok (), result)
            Assert.False(rig.Run.RunTask.IsCompleted)
        }

    let healthy (rig: Rig) cancellation =
        task {
            let! result = rig.Api.Send (request ()) (peer rig.Target) cancellation
            let actual = result |> ok
            Assert.Equal(OwnerWireSamples.id, actual.Instance.InstanceId)
            Assert.Empty(rig.Fatals)
        }

    let stopped (run: Running) =
        task {
            let! result = run.Stop()
            Assert.Equal<Result<unit, PipeFailure>>(Ok (), result)
            Assert.True(run.RunTask.IsCompletedSuccessfully)
        }

    let header (length: uint32) =
        let bytes = Array.zeroCreate<byte> 4
        BinaryPrimitives.WriteUInt32BigEndian(Span<byte>(bytes), length)
        bytes

    let frame (payload: byte array) =
        Array.append (header (uint32 payload.Length)) payload

    let connect target (cancellation: CancellationToken) =
        task {
            let pipe = Native.client target
            try
                do! pipe.ConnectAsync(cancellation)
                return pipe
            with error ->
                pipe.Dispose()
                return raise error
        }

    let closed (pipe: Stream) (cancellation: CancellationToken) =
        task {
            try
                let buffer = Array.zeroCreate<byte> 1
                let! count = pipe.ReadAsync(Memory<byte>(buffer), cancellation).AsTask()
                Assert.Equal<int>(0, count)
            with :? IOException when not cancellation.IsCancellationRequested -> ()
        }

    let rejected (rig: Rig) payload cancellation =
        task {
            use! pipe = connect rig.Target cancellation
            do! pipe.WriteAsync(ReadOnlyMemory<byte>(payload), cancellation).AsTask()
            do! closed pipe cancellation
        }

    let blocked target (entered: TaskCompletionSource<unit>) (finished: TaskCompletionSource<unit>)
        (request: Request) (cancellation: CancellationToken) =
        task {
            entered.TrySetResult(()) |> ignore
            try
                do! Task.Delay(Timeout.Infinite, cancellation)
                return response target request
            finally
                finished.TrySetResult(()) |> ignore
        }

module OwnerControlPipeFrameTests =
    open OwnerPipeFixture

    type private OneByteStream(input: byte array) =
        inherit MemoryStream(input, false)
        override _.ReadAsync(buffer: Memory<byte>, cancellation: CancellationToken) : ValueTask<int> =
            base.ReadAsync(buffer.Slice(0, min 1 buffer.Length), cancellation)

    type private BlockedWriter() =
        inherit MemoryStream()
        override _.WriteAsync(_buffer: ReadOnlyMemory<byte>, cancellation: CancellationToken) : ValueTask =
            ValueTask(Task.Delay(Timeout.Infinite, cancellation))

    let private invalidFrame (stream: Stream) cancellation =
        task {
            try
                let! _ = Framing.read stream cancellation
                return false
            with Native.Fault PipeFailure.InvalidFrame -> return true
        }

    [<Fact>]
    let ``partial exact reads preserve frame contents`` () =
        task {
            let body = [| 1uy; 2uy; 3uy; 4uy; 5uy |]
            use stream = new OneByteStream(frame body)
            let! actual = Framing.read stream CancellationToken.None
            Assert.Equal<byte>(body, actual)
        }

    [<Fact>]
    let ``invalid lengths stop after the header and truncated payloads fail`` () =
        task {
            for length in [ 0u; 8193u; UInt32.MaxValue ] do
                use stream = new MemoryStream(header length, false)
                let! rejected = invalidFrame stream CancellationToken.None
                Assert.True(rejected)
                Assert.Equal<int64>(4L, stream.Position)
            use truncated = new MemoryStream(Array.append (header 2u) [| 1uy |], false)
            let! rejected = invalidFrame truncated CancellationToken.None
            Assert.True(rejected)
        }

    [<Fact>]
    let ``blocked writes observe cancellation and their IO task completes`` () =
        task {
            use cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds 100.0)
            use stream = new BlockedWriter()
            try
                do! Framing.write stream [| 1uy |] cancellation.Token
                failwith "Expected write cancellation."
            with :? OperationCanceledException ->
                Assert.True(cancellation.IsCancellationRequested)
        }

module private PipelinedOwnerExchange =
    open OwnerPipeFixture

    [<RequireQualifiedAccess>]
    type Platform =
        | Windows
        | Other

    // HRESULT_FROM_WIN32(ERROR_BROKEN_PIPE), not ERROR_PIPE_NOT_CONNECTED.
    [<Literal>]
    let brokenPipeHResult = -2147024787

    // Capture faults for joining; acceptance is decided only after receiving proof.
    let observe (work: unit -> Task) =
        task {
            try
                do! work ()
                return Ok ()
            with error ->
                return Error error
        }

    let private brokenPipe platform (cancellation: CancellationToken) (error: IOException) =
        platform = Platform.Windows
        && error.HResult = brokenPipeHResult
        && not cancellation.IsCancellationRequested

    let private expectClose platform (pipe: Stream) cancellation =
        task {
            try
                let buffer = Array.zeroCreate<byte> 1
                let! count = pipe.ReadAsync(Memory<byte>(buffer), cancellation).AsTask()
                Assert.Equal<int>(0, count)
            with :? IOException as error when brokenPipe platform cancellation error -> ()
        }

    let private receive platform target first (pipe: Stream) cancellation =
        task {
            let! encoded = Framing.read pipe cancellation
            let actual = Codec.decodeResponse first (peer target) encoded |> ok
            Assert.Equal<Response>(response target first, actual)
            do! expectClose platform pipe cancellation
        }

    let private finishWrite platform (cancellation: CancellationToken) (outcome: Result<unit, exn>) =
        cancellation.ThrowIfCancellationRequested()
        outcome
        |> Result.defaultWith (function
            | :? IOException as error when brokenPipe platform cancellation error -> ()
            | error -> raise error)

    let frames first second =
        [| first; second |]
        |> Array.collect (fun request -> request |> Codec.encodeRequest |> ok |> frame)

    let dispatch target (calls: ConcurrentQueue<string>)
        (issued: TaskCompletionSource<unit>) (request: Request)
        (cancellation: CancellationToken) =
        task {
            calls.Enqueue(request.RequestId)
            do! issued.Task.WaitAsync(cancellation)
            return response target request
        }

    let exchange platform target first second (issued: TaskCompletionSource<unit>)
        (pipe: Stream) (parent: CancellationToken) =
        task {
            let payload = frames first second
            use lifetime = CancellationTokenSource.CreateLinkedTokenSource(parent)
            use interrupted = lifetime.Token.Register(fun () -> pipe.Dispose())
            let receiving = observe (fun () -> receive platform target first pipe lifetime.Token :> Task)
            let writing = observe (fun () ->
                pipe.WriteAsync(ReadOnlyMemory<byte>(payload), lifetime.Token).AsTask())
            // Both operations are issued; surplus write completion must not gate dispatch.
            issued.TrySetResult(()) |> ignore
            let! received = receiving
            if Result.isError received then lifetime.Cancel()
            let! sent = writing
            received |> Result.defaultWith raise
            finishWrite platform parent sent
        }

    let run target first issued cancellation =
        task {
            use! pipe = connect target cancellation
            let platform = if OperatingSystem.IsWindows() then Platform.Windows else Platform.Other
            return! exchange platform target first (request ()) issued pipe cancellation
        }

module private PipelinedOwnerProbe =
    open OwnerPipeFixture

    type Scenario =
        { Target: OwnerControlTarget.Target
          First: Request
          Second: Request
          Issued: TaskCompletionSource<unit> }

    type ReplyScript =
        { Bytes: byte array
          CloseError: exn option }

    let private signal () =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    type PeerStream(expected: byte array, script: ReplyScript, release: Task,
        completeWrite: Task -> CancellationToken -> Task) =
        inherit MemoryStream()

        let input = new MemoryStream(script.Bytes, false)
        let readStarted = signal ()
        let writeStarted = signal ()
        let writeSettled = signal ()
        let endRead = signal ()
        let disposed = signal ()

        let read buffer (cancellation: CancellationToken) =
            task {
                readStarted.TrySetResult(()) |> ignore
                do! release.WaitAsync(cancellation)
                let! count = input.ReadAsync(buffer, cancellation).AsTask()
                if count = 0 then
                    endRead.TrySetResult(()) |> ignore
                    script.CloseError |> Option.iter raise
                return count
            }

        let write (buffer: ReadOnlyMemory<byte>) cancellation : Task =
            task {
                try
                    Assert.True(readStarted.Task.IsCompletedSuccessfully)
                    Assert.Equal<byte>(expected, buffer.ToArray())
                    writeStarted.TrySetResult(()) |> ignore
                    do! completeWrite endRead.Task cancellation
                finally
                    writeSettled.TrySetResult(()) |> ignore
            }

        member _.WriteStarted = writeStarted.Task.IsCompletedSuccessfully
        member _.WriteSettled = writeSettled.Task.IsCompletedSuccessfully
        member _.Disposed = disposed.Task.IsCompletedSuccessfully

        override _.ReadAsync(buffer: Memory<byte>, cancellation: CancellationToken) : ValueTask<int> =
            ValueTask<int>(read buffer cancellation)

        override _.WriteAsync(buffer: ReadOnlyMemory<byte>, cancellation: CancellationToken) : ValueTask =
            ValueTask(write buffer cancellation)

        override _.Dispose(disposing) =
            if disposing then
                input.Dispose()
                disposed.TrySetResult(()) |> ignore
            base.Dispose(disposing)

    let scenario () =
        { Target = target ()
          First = request ()
          Second = request ()
          Issued = signal () }

    let private encode request response =
        Codec.encodeResponse request response |> ok |> frame

    let reply sample =
        { Bytes = response sample.Target sample.First |> encode sample.First
          CloseError = None }

    let invalidReplies sample =
        let valid = reply sample
        let expected = response sample.Target sample.First
        let foreign =
            { expected with
                Instance = { expected.Instance with ProcessId = expected.Instance.ProcessId + 1 } }
        [ Array.empty
          header 0u
          Array.append (header 2u) [| 1uy |]
          response sample.Target sample.Second |> encode sample.Second
          encode sample.First foreign ]
        |> List.map (fun bytes -> { valid with Bytes = bytes })

    let stream sample script completeWrite =
        let first = sample.First |> Codec.encodeRequest |> ok |> frame
        let second = sample.Second |> Codec.encodeRequest |> ok |> frame
        new PeerStream(Array.append first second, script, sample.Issued.Task, completeWrite)

    let afterClose error (closed: Task) (cancellation: CancellationToken) : Task =
        task {
            do! closed.WaitAsync(cancellation)
            error |> Option.iter raise
        }

    let immediately (error: exn) (_: Task) (_: CancellationToken) : Task =
        Task.FromException(error)

    let blocked (_: Task) (cancellation: CancellationToken) : Task =
        Task.Delay(Timeout.Infinite, cancellation)

    let cancelAfterClose (cancellation: CancellationTokenSource) error
        (closed: Task) (token: CancellationToken) : Task =
        task {
            do! closed.WaitAsync(token)
            cancellation.Cancel()
            raise error
        }

    let run platform sample (pipe: PeerStream) cancellation =
        task {
            use owned = pipe
            return! PipelinedOwnerExchange.observe (fun () ->
                PipelinedOwnerExchange.exchange platform sample.Target sample.First sample.Second
                    sample.Issued owned cancellation :> Task)
        }

    let settled (pipe: PeerStream) =
        Assert.True(pipe.WriteSettled)
        Assert.True(pipe.Disposed)

    let check platform sample script completeWrite verify =
        task {
            use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 8.0)
            let pipe = stream sample script completeWrite
            let! actual = run platform sample pipe deadline.Token
            settled pipe
            verify actual
        }

    let succeeded actual = Assert.Equal<Result<unit, exn>>(Ok (), actual)
    let failed actual = Assert.True(Result.isError actual)
    let failedWith error actual = Assert.Equal<Result<unit, exn>>(Error error, actual)
    let ioFailure (code: int) = IOException("Synthetic owner-pipe IO failure.", code)

module OwnerControlPipePipelineFixtureTests =
    open PipelinedOwnerExchange
    open PipelinedOwnerProbe

    let rec private sequentially (checks: (unit -> Task<unit>) list) : Task<unit> =
        task {
            match checks with
            | [] -> ()
            | next :: remaining ->
                do! next ()
                return! sequentially remaining
        }

    [<Fact>]
    let ``combined write can remain pending until the reply and EOF are read`` () =
        task {
            let sample = scenario ()
            Assert.NotEqual<string>(sample.First.RequestId, sample.Second.RequestId)
            do! check Platform.Other sample (reply sample) (afterClose None) succeeded
        }

    [<Fact>]
    let ``Windows broken surplus write requires a valid first reply and closure`` () =
        task {
            let sample = scenario ()
            let error = ioFailure brokenPipeHResult
            do! check Platform.Windows sample (reply sample) (afterClose (Some error)) succeeded
        }

    [<Fact>]
    let ``other write faults are preserved whether immediate or after closure`` () =
        let checkCase (platform: Platform, code: int) () : Task<unit> =
            task {
                let sample = scenario ()
                let error = ioFailure code
                do! check platform sample (reply sample) (afterClose (Some error)) (failedWith error)
                do! check platform sample (reply sample) (immediately error) (failedWith error)
            }
        [ Platform.Other, brokenPipeHResult
          Platform.Windows, -2147024664
          Platform.Windows, -2147024891
          Platform.Windows, 109 ]
        |> List.map checkCase
        |> sequentially

    [<Fact>]
    let ``broken surplus writes cannot hide absent malformed or uncorrelated replies`` () =
        task {
            let sample = scenario ()
            let error = ioFailure brokenPipeHResult
            for script in invalidReplies sample do
                do! check Platform.Windows sample script (afterClose (Some error)) failed
        }

    [<Fact>]
    let ``broken first read is not proof of a first reply`` () =
        task {
            let sample = scenario ()
            let error = ioFailure brokenPipeHResult
            let script = { Bytes = Array.empty; CloseError = Some error }
            do! check Platform.Windows sample script (immediately error) (failedWith error)
        }

    [<Fact>]
    let ``closure faults are restricted to the exact Windows broken pipe result`` () =
        let broken = ioFailure brokenPipeHResult
        let unexpected = ioFailure -2147024891
        let checkCase (platform: Platform, error: IOException, verify: Result<unit, exn> -> unit) () : Task<unit> =
            task {
                let sample = scenario ()
                let script = { reply sample with CloseError = Some error }
                do! check platform sample script (afterClose None) verify
            }
        [ Platform.Windows, broken, succeeded
          Platform.Other, broken, failedWith broken
          Platform.Windows, unexpected, failedWith unexpected ]
        |> List.map checkCase
        |> sequentially

    [<Fact>]
    let ``second reply bytes fail and the unfinished write is joined`` () =
        task {
            let sample = scenario ()
            let first = reply sample
            let script = { first with Bytes = Array.append first.Bytes first.Bytes }
            do! check Platform.Windows sample script (afterClose None) failed
        }

module OwnerControlPipePipelineCancellationTests =
    open OwnerPipeFixture
    open PipelinedOwnerExchange
    open PipelinedOwnerProbe

    [<Fact>]
    let ``pipelined dispatch gate observes cancellation before IO is issued`` () =
        task {
            let sample = scenario ()
            let calls = ConcurrentQueue<string>()
            use cancellation = new CancellationTokenSource()
            let pending = dispatch sample.Target calls sample.Issued sample.First cancellation.Token
            let wasPending = not pending.IsCompleted
            cancellation.Cancel()
            let! _ =
                Assert.ThrowsAnyAsync<OperationCanceledException>(Func<Task>(fun () -> pending :> Task))
            Assert.True(wasPending)
            Assert.Equal<string>([| sample.First.RequestId |], calls.ToArray())
            Assert.False(sample.Issued.Task.IsCompleted)
        }

    [<Fact>]
    let ``cancellation disposes the peer and joins pending read and write`` () =
        task {
            let sample = scenario ()
            let unreleased = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            use cancellation = new CancellationTokenSource()
            let script = reply sample
            let pipe = new PeerStream(frames sample.First sample.Second, script, unreleased.Task, blocked)
            let pending = run Platform.Windows sample pipe cancellation.Token
            let wasWriting = pipe.WriteStarted
            cancellation.Cancel()
            let! actual = pending
            settled pipe
            Assert.True(wasWriting)
            failed actual
            actual |> Result.mapError (fun error ->
                Assert.IsAssignableFrom<OperationCanceledException>(error) |> ignore) |> ignore
        }

    [<Fact>]
    let ``cancellation cannot turn a proven reply and broken write into success`` () =
        task {
            let sample = scenario ()
            use cancellation = new CancellationTokenSource()
            let error = ioFailure brokenPipeHResult
            let pipe = stream sample (reply sample) (cancelAfterClose cancellation error)
            let! actual = run Platform.Windows sample pipe cancellation.Token
            settled pipe
            Assert.True(cancellation.IsCancellationRequested)
            failed actual
        }

module OwnerControlPipeTests =
    open OwnerPipeFixture

    [<Fact>]
    let ``same owner connects disconnects reconnects and cleanly restarts`` () =
        task {
            let target = target ()
            use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 10.0)
            for _ in [ 1; 2 ] do
                let rig = start target (fun request _ -> Task.FromResult(response target request))
                use run = rig.Run
                do! ready rig
                for _ in [ 1; 2; 3 ] do do! healthy rig deadline.Token
                do! stopped run
        }

    [<Fact>]
    let ``a broken accepted client does not kill the persistent listener`` () =
        task {
            let target = target ()
            let rig = start target (fun request _ -> Task.FromResult(response target request))
            use run = rig.Run
            use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 8.0)
            do! ready rig
            let! broken = connect target deadline.Token
            do! broken.WriteAsync(ReadOnlyMemory<byte>([| 0uy |]), deadline.Token).AsTask()
            broken.Dispose()
            do! healthy rig deadline.Token
            do! stopped run
        }

    [<Fact>]
    let ``real partial writes succeed and extra frames never dispatch twice`` () =
        task {
            let target = target ()
            let calls = ConcurrentQueue<string>()
            let dispatch (request: Request) (_: CancellationToken) = calls.Enqueue(request.RequestId); Task.FromResult(response target request)
            let rig = start target dispatch
            use run = rig.Run
            use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 8.0)
            do! ready rig
            use! pipe = connect target deadline.Token
            let payload = request () |> Codec.encodeRequest |> ok |> frame
            for value in payload do
                do! pipe.WriteAsync(ReadOnlyMemory<byte>([| value |]), deadline.Token).AsTask()
            let! _ = Framing.read pipe deadline.Token
            do! closed pipe deadline.Token
            Assert.Equal<int>(1, calls.Count)
        }

    [<Fact>]
    let ``two pipelined frames dispatch exactly one operation and close`` () =
        task {
            let target = target ()
            let calls = ConcurrentQueue<string>()
            let issued = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let rig = start target (PipelinedOwnerExchange.dispatch target calls issued)
            use run = rig.Run
            use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 8.0)
            do! ready rig
            let first = request ()
            do! PipelinedOwnerExchange.run target first issued deadline.Token
            Assert.Equal<string>([| first.RequestId |], calls.ToArray())
            do! healthy rig deadline.Token
            Assert.Equal<int>(2, calls.Count)
            do! stopped run
        }

    [<Fact>]
    let ``oversized zero and invalid UTF8 peers never dispatch and acceptance continues`` () =
        task {
            let target = target ()
            let calls = ConcurrentQueue<string>()
            let dispatch (request: Request) (_: CancellationToken) = calls.Enqueue(request.RequestId); Task.FromResult(response target request)
            let rig = start target dispatch
            use run = rig.Run
            use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 8.0)
            do! ready rig
            for payload in [ header 0u; header UInt32.MaxValue; frame [| 0xC3uy; 0x28uy |] ] do
                do! rejected rig payload deadline.Token
            Assert.Empty(calls)
            do! healthy rig deadline.Token
        }

module OwnerControlPipeDeadlineTests =
    open OwnerPipeFixture

    [<Fact>]
    let ``slow header and slow payload expire and permit the next caller`` () =
        task {
            let target = target ()
            let rig = start target (fun request _ -> Task.FromResult(response target request))
            use run = rig.Run
            use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 12.0)
            do! ready rig
            for partial in [ [| 0uy |]; Array.append (header 200u) [| 123uy |] ] do
                let elapsed = Stopwatch.StartNew()
                do! rejected rig partial deadline.Token
                Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds 5.0)
                do! healthy rig deadline.Token
        }

    [<Fact>]
    let ``connect timeout and caller cancellation are distinct typed failures`` () =
        task {
            let target = target ()
            let api = Factory.create target (fun request -> Task.FromResult(response target request)) ignore
            use cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds 100.0)
            let! cancelled = api.Send (request ()) (peer target) cancellation.Token
            Assert.Equal<Result<Response, PipeFailure>>(Error PipeFailure.Cancelled, cancelled)
            let elapsed = Stopwatch.StartNew()
            let! timedOut = api.Send (request ()) (peer target) CancellationToken.None
            Assert.Equal<Result<Response, PipeFailure>>(Error PipeFailure.TimedOut, timedOut)
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds 5.0)
        }

    [<Fact>]
    let ``five second accepted exchange cancels dispatch joins it and recovers`` () =
        task {
            let target = target ()
            let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let finished = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let first = request ()
            let dispatch (request: Request) (cancellation: CancellationToken) =
                if request.RequestId = first.RequestId then blocked target entered finished request cancellation
                else Task.FromResult(response target request)
            let rig = start target dispatch
            use run = rig.Run
            use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 9.0)
            do! ready rig
            do! rejected rig (first |> Codec.encodeRequest |> ok |> frame) deadline.Token
            Assert.True(finished.Task.IsCompletedSuccessfully)
            do! healthy rig deadline.Token
        }

    [<Fact>]
    let ``shutdown cancels and joins dispatch and closes its accepted peer`` () =
        task {
            let target = target ()
            let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let finished = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let rig = start target (blocked target entered finished)
            use run = rig.Run
            use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 8.0)
            do! ready rig
            use! pipe = connect target deadline.Token
            do! Framing.write pipe (request () |> Codec.encodeRequest |> ok) deadline.Token
            do! entered.Task.WaitAsync(deadline.Token)
            do! stopped run
            Assert.True(finished.Task.IsCompletedSuccessfully)
            do! closed pipe deadline.Token
        }

module OwnerControlPipeFailureTests =
    open OwnerPipeFixture

    [<Fact>]
    let ``constructor failure is fatal and readiness is not healthy`` () =
        task {
            let target = target ()
            let fatals = ConcurrentQueue<PipeFailure>()
            let construct () : NamedPipeServerStream = raise (IOException("Synthetic listener failure."))
            let dispatch request _ = Task.FromResult(response target request)
            use run = Server.start construct dispatch fatals.Enqueue CancellationToken.None
            let! ready = run.Ready
            let! completed = run.RunTask
            Assert.Equal<Result<unit, PipeFailure>>(Error PipeFailure.ListenerFailed, ready)
            Assert.Equal<Result<unit, PipeFailure>>(Error PipeFailure.ListenerFailed, completed)
            Assert.Equal<int>(1, fatals.Count)
        }

    [<Fact>]
    let ``closing a live listener reports terminal failure and joins its wait`` () =
        task {
            let target = target ()
            let fatals = ConcurrentQueue<PipeFailure>()
            let listener = Native.server target
            let dispatch request _ = Task.FromResult(response target request)
            use run = Server.start (fun () -> listener) dispatch fatals.Enqueue CancellationToken.None
            let! ready = run.Ready
            Assert.Equal<Result<unit, PipeFailure>>(Ok (), ready)
            listener.Dispose()
            let! completed = run.RunTask.WaitAsync(TimeSpan.FromSeconds 5.0)
            Assert.Equal<Result<unit, PipeFailure>>(Error PipeFailure.ListenerFailed, completed)
            Assert.Equal<int>(1, fatals.Count)
        }
