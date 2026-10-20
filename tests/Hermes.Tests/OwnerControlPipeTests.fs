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

    let dispatch target (calls: ConcurrentQueue<string>)
        (written: TaskCompletionSource<unit>) (request: Request)
        (cancellation: CancellationToken) =
        task {
            calls.Enqueue(request.RequestId)
            do! written.Task.WaitAsync(cancellation)
            return response target request
        }

    let run target (first: Request) (written: TaskCompletionSource<unit>)
        (cancellation: CancellationToken) =
        task {
            use! pipe = connect target cancellation
            let payload = first |> Codec.encodeRequest |> ok |> frame
            do! pipe.WriteAsync(ReadOnlyMemory<byte>(Array.append payload payload), cancellation).AsTask()
            // Both frames are written and the first read is issued before dispatch replies.
            let reply = Framing.read pipe cancellation
            written.SetResult(())
            let! encoded = reply
            let actual = Codec.decodeResponse first (peer target) encoded |> ok
            Assert.Equal<Response>(response target first, actual)
            do! closed pipe cancellation
        }

module OwnerControlPipePipelineFixtureTests =
    open OwnerPipeFixture

    [<Fact>]
    let ``pipelined dispatch gate observes cancellation before write completion`` () =
        task {
            let target = target ()
            let calls = ConcurrentQueue<string>()
            let written = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            use cancellation = new CancellationTokenSource()
            let pending =
                PipelinedOwnerExchange.dispatch target calls written (request ()) cancellation.Token
            Assert.False(pending.IsCompleted)
            cancellation.Cancel()
            let! _ =
                Assert.ThrowsAnyAsync<OperationCanceledException>(Func<Task>(fun () -> pending :> Task))
            Assert.Single(calls) |> ignore
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
            let written = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let rig = start target (PipelinedOwnerExchange.dispatch target calls written)
            use run = rig.Run
            use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 8.0)
            do! ready rig
            let first = request ()
            do! PipelinedOwnerExchange.run target first written deadline.Token
            Assert.Equal<string>([| first.RequestId |], calls.ToArray())
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
