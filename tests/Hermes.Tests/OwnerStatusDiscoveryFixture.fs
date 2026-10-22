namespace Hermes.Tests

open System
open System.IO.Pipes
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Core.LocalAccessWire
open Hermes.Core.OwnerControlPipe

module internal OwnerStatusDiscoveryFixture =
    /// Cancellation and joining also run when a test assertion throws.
    type Joined<'T> =
        { Lifetime: CancellationTokenSource
          Completion: Task<'T> }
        interface IAsyncDisposable with
            member worker.DisposeAsync() =
                ValueTask(task {
                    worker.Lifetime.Cancel()
                    try
                        let! _ = worker.Completion
                        return ()
                    with :? OperationCanceledException when worker.Lifetime.IsCancellationRequested ->
                        return ()
                })

    let peer (identity: Identity) : PeerExpectation =
        { ProcessId = identity.ProcessId
          ProcessStartTimeUtc = identity.ProcessStartTimeUtc
          Origin = identity.Origin; ConfigKey = identity.ConfigKey }

    let pinned (identity: Identity) : Request =
        { RequestId = Codec.newRequestId()
          ExpectedInstanceId = Some identity.InstanceId
          Operation = Operation.Status }

    let dispatch (target: OwnerControlTarget.Target) (request: Request) (_: CancellationToken) =
        Task.FromResult(OwnerPipeFixture.response target request)

    let withOwned (target: OwnerControlTarget.Target) (dispatch: Dispatch)
        (action: ClientOnly -> CancellationToken -> Task<'T>) =
        task {
            use lifetime = new CancellationTokenSource(TimeSpan.FromSeconds 10.0)
            let factory = Factory.createCancellable target dispatch ignore
            use running = factory.Start lifetime.Token
            let! ready = running.Ready
            Assert.Equal<Result<unit, PipeFailure>>(Ok (), ready)
            let! outcome = action (Factory.client target) lifetime.Token
            let! stopped = running.Stop()
            Assert.Equal<Result<unit, PipeFailure>>(Ok (), stopped)
            return outcome
        }

    let private accept (pipe: NamedPipeServerStream)
        (serve: NamedPipeServerStream -> CancellationToken -> Task<unit>) cancellation =
        task {
            do! pipe.WaitForConnectionAsync(cancellation)
            do! serve pipe cancellation
        }

    /// A deliberately malformed same-owner test peer, not another protocol parser.
    let withRaw (target: OwnerControlTarget.Target)
        (serve: NamedPipeServerStream -> CancellationToken -> Task<unit>)
        (action: ClientOnly -> CancellationToken -> Task<'T>) =
        task {
            use lifetime = new CancellationTokenSource(TimeSpan.FromSeconds 10.0)
            use pipe = Native.server target
            let completion = accept pipe serve lifetime.Token
            use _joined = { Lifetime = lifetime; Completion = completion }
            return! action (Factory.client target) lifetime.Token
        }

    let rawResponse (target: OwnerControlTarget.Target) (change: byte array -> byte array)
        (pipe: NamedPipeServerStream) (cancellation: CancellationToken) =
        task {
            let! payload = Framing.read pipe cancellation
            let request = Codec.decodeRequest payload |> OwnerPipeFixture.ok
            let response = OwnerPipeFixture.response target request
            let encoded = Codec.encodeResponse request response |> OwnerPipeFixture.ok
            do! Framing.write pipe (change encoded) cancellation
        }

    let hold (entered: TaskCompletionSource<unit>) (finished: TaskCompletionSource<unit>)
        (pipe: NamedPipeServerStream) (cancellation: CancellationToken) =
        task {
            try
                let! payload = Framing.read pipe cancellation
                let request = Codec.decodeRequest payload |> OwnerPipeFixture.ok
                Assert.Equal<Operation>(Operation.Status, request.Operation)
                entered.TrySetResult(()) |> ignore
                do! Task.Delay(Timeout.Infinite, cancellation)
            finally
                finished.TrySetResult(()) |> ignore
        }
