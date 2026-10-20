namespace Hermes.Tests

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core.LocalAccessWire
open Hermes.Core.OwnerControlPipe

module OwnerStatusDiscoveryRejectionTests =
    open OwnerStatusDiscoveryFixture

    let private rejectDiscovery (failure: CodecFailure) (client: ClientOnly) cancellation =
        task {
            let! result = client.DiscoverStatus (OwnerWireSamples.request 0) cancellation
            Assert.Equal<Result<Response, PipeFailure>>(
                Error(PipeFailure.InvalidWire failure), result)
        }

    [<Theory>]
    [<InlineData("origin")>]
    [<InlineData("configKey")>]
    let ``real same-owner transport rejects a response for a different target`` (field: string) =
        let target = OwnerPipeFixture.target ()
        let replacement =
            if field = "origin" then target.ApiOrigin.Replace("127.0.0.1", "localhost")
            else OwnerDiscoverySamples.differentKey target.ConfigKey
        let changed =
            OwnerWireSamples.change (Some "instance") field (OwnerDiscoverySamples.quote replacement)
        withRaw target (rawResponse target changed) (rejectDiscovery CodecFailure.IdentityMismatch)

    [<Fact>]
    let ``real same-owner transport requires exact correlation`` () =
        let target = OwnerPipeFixture.target ()
        let changed =
            OwnerWireSamples.change None "requestId"
                (OwnerDiscoverySamples.quote "AQEBAQEBAQEBAQEBAQEBAQ")
        withRaw target (rawResponse target changed) (rejectDiscovery CodecFailure.CorrelationMismatch)

    [<Fact>]
    let ``real same-owner transport rejects an unsupported wire version`` () =
        let target = OwnerPipeFixture.target ()
        let changed = OwnerWireSamples.change None "version" "2"
        withRaw target (rawResponse target changed) (rejectDiscovery CodecFailure.UnsupportedVersion)

    [<Theory>]
    [<InlineData("processId", "0")>]
    [<InlineData("instanceId", "\"AAAAAAAAAAAAAAAAAAAAAB\"")>]
    [<InlineData("processStartTimeUtc", "\"2026-10-20T10:00:00+10:00\"")>]
    let ``real same-owner transport still rejects malformed identity fields``
        (field: string) (replacement: string) =
        let target = OwnerPipeFixture.target ()
        let changed = OwnerWireSamples.change (Some "instance") field replacement
        withRaw target (rawResponse target changed) (rejectDiscovery CodecFailure.Malformed)

    [<Fact>]
    let ``real same-owner transport never accepts invalid UTF8`` () =
        let target = OwnerPipeFixture.target ()
        let changed (_: byte array) = [| 0xC3uy; 0x28uy |]
        withRaw target (rawResponse target changed) (rejectDiscovery CodecFailure.Malformed)

module OwnerStatusDiscoveryCancellationTests =
    open OwnerStatusDiscoveryFixture

    [<Fact>]
    let ``client-only discovery uses the bounded existing connect deadline`` () =
        task {
            use lifetime = new CancellationTokenSource(TimeSpan.FromSeconds 8.0)
            let client = Factory.client (OwnerPipeFixture.target ())
            let elapsed = Stopwatch.StartNew()
            let! result = client.DiscoverStatus (OwnerPipeFixture.request ()) lifetime.Token
            Assert.Equal<Result<Response, PipeFailure>>(Error PipeFailure.TimedOut, result)
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds 5.0)
        }

    [<Fact>]
    let ``pre-cancelled discovery returns cancellation without contacting a listener`` () =
        task {
            use lifetime = new CancellationTokenSource()
            lifetime.Cancel()
            let client = Factory.client (OwnerPipeFixture.target ())
            let! result = client.DiscoverStatus (OwnerPipeFixture.request ()) lifetime.Token
            Assert.Equal<Result<Response, PipeFailure>>(Error PipeFailure.Cancelled, result)
        }

    [<Fact>]
    let ``cancellation during discovery connection is not reported as timeout`` () =
        task {
            use lifetime = new CancellationTokenSource(TimeSpan.FromMilliseconds 100.0)
            let client = Factory.client (OwnerPipeFixture.target ())
            let! result = client.DiscoverStatus (OwnerPipeFixture.request ()) lifetime.Token
            Assert.Equal<Result<Response, PipeFailure>>(Error PipeFailure.Cancelled, result)
        }

    let private cancelAccepted (entered: TaskCompletionSource<unit>) (client: ClientOnly)
        (cancellation: CancellationToken) =
        task {
            use lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation)
            let completion = client.DiscoverStatus (OwnerPipeFixture.request ()) lifetime.Token
            use _joined = { Lifetime = lifetime; Completion = completion }
            do! entered.Task.WaitAsync(cancellation)
            lifetime.Cancel()
            let! result = completion
            Assert.Equal<Result<Response, PipeFailure>>(Error PipeFailure.Cancelled, result)
        }

    [<Fact>]
    let ``accepted discovery cancellation joins client IO and the owned peer worker`` () =
        task {
            let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let finished = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let target = OwnerPipeFixture.target ()
            do! withRaw target (hold entered finished) (cancelAccepted entered)
            Assert.True(finished.Task.IsCompletedSuccessfully)
        }

    let private rejectSilentPeer (client: ClientOnly) cancellation =
        task {
            let elapsed = Stopwatch.StartNew()
            let! result = client.DiscoverStatus (OwnerPipeFixture.request ()) cancellation
            Assert.Equal<Result<Response, PipeFailure>>(Error PipeFailure.TimedOut, result)
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds 5.0)
        }

    [<Fact>]
    let ``silent accepted discovery retains the existing response header deadline`` () =
        task {
            let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let finished = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let target = OwnerPipeFixture.target ()
            do! withRaw target (hold entered finished) rejectSilentPeer
            Assert.True(entered.Task.IsCompletedSuccessfully)
            Assert.True(finished.Task.IsCompletedSuccessfully)
        }
