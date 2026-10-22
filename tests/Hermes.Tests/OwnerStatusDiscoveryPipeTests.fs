namespace Hermes.Tests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Core.LocalAccessWire
open Hermes.Core.OwnerControlPipe

module internal OwnerStatusDiscoveryDiagnostics =
    [<RequireQualifiedAccess>]
    type PeerField =
        | ProcessId
        | ProcessStartTimeUtc
        | Origin
        | ConfigKey

    let fieldCode = function
        | PeerField.ProcessId -> 1
        | PeerField.ProcessStartTimeUtc -> 2
        | PeerField.Origin -> 3
        | PeerField.ConfigKey -> 4

    let private codecCode = function
        | CodecFailure.Malformed -> 101
        | CodecFailure.UnsupportedVersion -> 102
        | CodecFailure.CorrelationMismatch -> 103
        | CodecFailure.IdentityMismatch -> 104

    let private failureCode = function
        | PipeFailure.Cancelled -> 1
        | PipeFailure.TimedOut -> 2
        | PipeFailure.Unavailable -> 3
        | PipeFailure.PrincipalRejected -> 4
        | PipeFailure.OwnershipUnavailable -> 5
        | PipeFailure.InvalidTarget -> 6
        | PipeFailure.InvalidFrame -> 7
        | PipeFailure.DispatchFailed -> 8
        | PipeFailure.ListenerFailed -> 9
        | PipeFailure.SupervisorFailed -> 10
        | PipeFailure.InvalidWire failure -> codecCode failure

    let resultCode (result: Result<'T, PipeFailure>) =
        result
        |> Result.map (fun _ -> 0)
        |> Result.defaultWith failureCode

    let assertMismatch field (actual: Result<Response, PipeFailure>) =
        let expected: Result<Response, PipeFailure> =
            Error(PipeFailure.InvalidWire CodecFailure.IdentityMismatch)
        let message =
            $"Owner discovery field={fieldCode field}; expected={resultCode expected}; actual={resultCode actual}."
        Assert.True((expected = actual), message)

module OwnerStatusDiscoveryPipeTests =
    open OwnerStatusDiscoveryFixture
    open OwnerStatusDiscoveryDiagnostics

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

    let private badPeers (identity: Identity) : (PeerField * PeerExpectation) list =
        let expected = peer identity
        [ PeerField.ProcessId, { expected with ProcessId = expected.ProcessId + 1 }
          PeerField.ProcessStartTimeUtc,
              { expected with ProcessStartTimeUtc = expected.ProcessStartTimeUtc.AddSeconds(1.0) }
          PeerField.Origin, { expected with Origin = expected.Origin.Replace("127.0.0.1", "localhost") }
          PeerField.ConfigKey,
              { expected with ConfigKey = OwnerDiscoverySamples.differentKey expected.ConfigKey } ]

    let internal rejectBadPeers (client: ClientOnly) cancellation =
        task {
            let! result = client.DiscoverStatus (OwnerPipeFixture.request ()) cancellation
            let identity = (result |> OwnerPipeFixture.ok).Instance
            for (field, expected) in badPeers identity do
                let! rejected = client.Send (pinned identity) expected cancellation
                assertMismatch field rejected
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

module OwnerStatusDiscoveryDiagnosticTests =
    open OwnerStatusDiscoveryDiagnostics

    type private Probe =
        { Client: ClientOnly
          Calls: ConcurrentQueue<PeerExpectation> }

    let private mismatch: Result<Response, PipeFailure> =
        Error(PipeFailure.InvalidWire CodecFailure.IdentityMismatch)

    let private scripted rejectedAt (rejection: Result<Response, PipeFailure>) =
        let calls = ConcurrentQueue<PeerExpectation>()
        let response = OwnerPipeFixture.response (OwnerPipeFixture.target ()) (OwnerPipeFixture.request ())
        let client: ClientOnly =
            { DiscoverStatus = fun request _ ->
                  Task.FromResult(Ok { response with RequestId = request.RequestId })
              Send = fun request expected _ ->
                  Assert.True(request.ExpectedInstanceId = Some response.Instance.InstanceId,
                              "Script requires the complete instance pin.")
                  calls.Enqueue(expected)
                  Task.FromResult(if calls.Count = rejectedAt then rejection else mismatch) }
        { Client = client; Calls = calls }

    [<Fact>]
    let ``closed result codes distinguish success and every transport failure`` () =
        let failures =
            [ PipeFailure.Cancelled; PipeFailure.TimedOut; PipeFailure.Unavailable
              PipeFailure.PrincipalRejected; PipeFailure.OwnershipUnavailable
              PipeFailure.InvalidTarget; PipeFailure.InvalidFrame; PipeFailure.DispatchFailed
              PipeFailure.ListenerFailed; PipeFailure.SupervisorFailed
              PipeFailure.InvalidWire CodecFailure.Malformed
              PipeFailure.InvalidWire CodecFailure.UnsupportedVersion
              PipeFailure.InvalidWire CodecFailure.CorrelationMismatch
              PipeFailure.InvalidWire CodecFailure.IdentityMismatch ]
        let actual =
            failures
            |> List.map (fun failure -> resultCode (Error failure: Result<unit, PipeFailure>))
        Assert.Equal<int>([ 1 .. 10 ] @ [ 101 .. 104 ], actual)
        Assert.Equal<int>(0, resultCode (Ok (): Result<unit, PipeFailure>))

    [<Fact>]
    let ``closed mismatch assertion fails on success without rendering the response`` () =
        let response =
            { OwnerPipeFixture.response (OwnerPipeFixture.target ()) (OwnerPipeFixture.request ()) with
                RequestId = "diagnostic-response-sentinel" }
        let error: Xunit.Sdk.TrueException =
            Assert.Throws<Xunit.Sdk.TrueException>(Action(fun () ->
                assertMismatch PeerField.ProcessId (Ok response)))
        Assert.Contains("Owner discovery field=1; expected=104; actual=0.", error.Message)
        Assert.True(not (error.Message.Contains(response.RequestId, StringComparison.Ordinal)),
                    "Response data must not enter closed diagnostics.")

    [<Theory>]
    [<InlineData(1)>]
    [<InlineData(2)>]
    [<InlineData(3)>]
    [<InlineData(4)>]
    let ``each sequential peer control reports its own unexpected transport result`` (field: int) =
        task {
            let probe = scripted field (Error PipeFailure.TimedOut)
            let! error =
                Assert.ThrowsAsync<Xunit.Sdk.TrueException>(Func<Task>(fun () ->
                    OwnerStatusDiscoveryPipeTests.rejectBadPeers probe.Client CancellationToken.None))
            Assert.Contains($"Owner discovery field={field}; expected=104; actual=2.", error.Message)
            Assert.Equal<int>(field, probe.Calls.Count)
        }

    [<Fact>]
    let ``closed diagnostics retain all four sequential negative controls`` () =
        task {
            let probe = scripted 0 mismatch
            do! OwnerStatusDiscoveryPipeTests.rejectBadPeers probe.Client CancellationToken.None
            Assert.Equal<int>(4, probe.Calls.Count)
        }
