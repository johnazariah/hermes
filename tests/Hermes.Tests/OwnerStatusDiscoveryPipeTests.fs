namespace Hermes.Tests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Core.LocalAccessWire
open Hermes.Core.OwnerControlPipe

module OwnerStatusDiscoveryPipeTests =
    open OwnerStatusDiscoveryFixture

    let private discoverAndPin (target: OwnerControlTarget.Target) (client: ClientOnly)
        (cancellation: CancellationToken) =
        task {
            let request = OwnerPipeFixture.request ()
            let! result = client.DiscoverStatus request cancellation
            let response = result |> OwnerPipeFixture.ok
            Assert.Equal<Response>(OwnerPipeFixture.response target request, response)
            let! checkedStatus = client.Send (pinned response.Instance) (peer response.Instance) cancellation
            let pinnedResponse = checkedStatus |> OwnerPipeFixture.ok
            Assert.Equal<Identity>(response.Instance, pinnedResponse.Instance)
        }

    [<Fact>]
    let ``client-only discovery connects to a real owned factory and then pins all identity`` () =
        let target = OwnerPipeFixture.target ()
        withOwned target (dispatch target) (discoverAndPin target)

    let private rejectForbidden (client: ClientOnly) (cancellation: CancellationToken) =
        task {
            for request in OwnerDiscoverySamples.forbidden () do
                let! result = client.DiscoverStatus request cancellation
                Assert.Equal<Result<Response, PipeFailure>>(
                    Error(PipeFailure.InvalidWire CodecFailure.Malformed), result)
        }

    let private rejectUnpinnedAuthority (target: OwnerControlTarget.Target) (client: ClientOnly)
        (cancellation: CancellationToken) =
        task {
            for index in [ 1; 2 ] do
                let request = { OwnerWireSamples.request index with ExpectedInstanceId = None }
                let! result = client.Send request (OwnerPipeFixture.peer target) cancellation
                Assert.Equal<Result<Response, PipeFailure>>(
                    Error(PipeFailure.InvalidWire CodecFailure.Malformed), result)
        }

    let private rejectBeforeDispatch (target: OwnerControlTarget.Target)
        (seen: ConcurrentQueue<Request>) (client: ClientOnly) cancellation =
        task {
            do! rejectForbidden client cancellation
            do! rejectUnpinnedAuthority target client cancellation
            Assert.Empty(seen)
            let! healthy = client.DiscoverStatus (OwnerPipeFixture.request ()) cancellation
            Assert.True(Result.isOk healthy)
            Assert.Equal<int>(1, seen.Count)
        }

    [<Fact>]
    let ``invalid discovery and unpinned authority requests never dispatch`` () =
        let target = OwnerPipeFixture.target ()
        let seen = ConcurrentQueue<Request>()
        let recorded (request: Request) (cancellation: CancellationToken) =
            seen.Enqueue(request)
            dispatch target request cancellation
        withOwned target recorded (rejectBeforeDispatch target seen)

    let private badPeers (identity: Identity) : PeerExpectation list =
        let expected = peer identity
        [ { expected with ProcessId = expected.ProcessId + 1 }
          { expected with ProcessStartTimeUtc = expected.ProcessStartTimeUtc.AddSeconds(1.0) }
          { expected with Origin = expected.Origin.Replace("127.0.0.1", "localhost") }
          { expected with ConfigKey = OwnerDiscoverySamples.differentKey expected.ConfigKey } ]

    let private rejectBadPeers (client: ClientOnly) cancellation =
        task {
            let! result = client.DiscoverStatus (OwnerPipeFixture.request ()) cancellation
            let identity = (result |> OwnerPipeFixture.ok).Instance
            for expected in badPeers identity do
                let! rejected = client.Send (pinned identity) expected cancellation
                Assert.Equal<Result<Response, PipeFailure>>(
                    Error(PipeFailure.InvalidWire CodecFailure.IdentityMismatch), rejected)
        }

    [<Fact>]
    let ``discovery does not weaken mandatory pinned Send peer fields`` () =
        let target = OwnerPipeFixture.target ()
        withOwned target (dispatch target) rejectBadPeers

    let private rejectReplacement (target: OwnerControlTarget.Target) (client: ClientOnly) cancellation =
        task {
            let identity = (OwnerPipeFixture.response target (OwnerPipeFixture.request ())).Instance
            let! result = client.Send (pinned identity) (peer identity) cancellation
            Assert.Equal<Result<Response, PipeFailure>>(
                Error(PipeFailure.InvalidWire CodecFailure.IdentityMismatch), result)
        }

    [<Fact>]
    let ``pinned Send rejects a canonical replacement instance rather than rediscovering`` () =
        let target = OwnerPipeFixture.target ()
        let changed =
            OwnerWireSamples.change (Some "instance") "instanceId"
                (OwnerDiscoverySamples.quote "AQEBAQEBAQEBAQEBAQEBAQ")
        withRaw target (rawResponse target changed) (rejectReplacement target)
