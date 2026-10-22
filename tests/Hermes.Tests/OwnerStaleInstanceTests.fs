namespace Hermes.Tests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Core.LocalAccess
open Hermes.Core.LocalAccessWire
open Hermes.Core.OwnerControlPipe
open Hermes.Service
open Hermes.Tests.LocalAccessDomainTests

module private StaleOwnerFixture =
    type Authority =
        { Identity: Hermes.Core.LocalAccess.Identity
          Runtime: LocalAccessRuntime
          Notifications: ConcurrentQueue<unit>
          OwnerCalls: ConcurrentQueue<unit> }
        interface IAsyncDisposable with
            member authority.DisposeAsync() = ValueTask(authority.Runtime.Lifecycle.Stop())

    let private observe (calls: ConcurrentQueue<unit>) (owner: OwnerAccess) : OwnerAccess =
        { ControlStatus = fun expected cancellation ->
              calls.Enqueue(())
              owner.ControlStatus expected cancellation
          Resolve = fun instance code cancellation ->
              calls.Enqueue(())
              owner.Resolve instance code cancellation
          Approve = fun request cancellation ->
              calls.Enqueue(())
              owner.Approve request cancellation }

    let create (target: OwnerControlTarget.Target) : Authority =
        use currentProcess = Process.GetCurrentProcess()
        let instance = RandomNumberGenerator.GetBytes(16) |> InstanceId.ofBytes |> Fixture.ok
        let audience = ApiAudience.create target.Port |> Fixture.ok
        let started = DateTimeOffset(currentProcess.StartTime.ToUniversalTime())
        let identity =
            Hermes.Core.LocalAccess.Identity.create
                instance currentProcess.Id started audience target.ConfigKey |> Fixture.ok
        let notifications = ConcurrentQueue<unit>()
        let runtime =
            LocalAccessRuntime.create { Unavailable = fun () -> notifications.Enqueue(()) }
                identity Fixture.all Set.empty |> Fixture.ok
        let ownerCalls = ConcurrentQueue<unit>()
        { Identity = identity
          Runtime = { runtime with Owner = observe ownerCalls runtime.Owner }
          Notifications = notifications; OwnerCalls = ownerCalls }

    let wireIdentity (authority: Authority) : Identity =
        { InstanceId = InstanceId.text authority.Identity.InstanceId
          ProcessId = authority.Identity.ProcessId
          ProcessStartTimeUtc = authority.Identity.ProcessStartTimeUtc
          Origin = ApiAudience.text authority.Identity.ApiOrigin
          ConfigKey = authority.Identity.ConfigKey }

    let peer (identity: Identity) : PeerExpectation =
        { ProcessId = identity.ProcessId
          ProcessStartTimeUtc = identity.ProcessStartTimeUtc
          Origin = identity.Origin; ConfigKey = identity.ConfigKey }

    let pinned (instance: string) (operation: Operation) : Request =
        { RequestId = Codec.newRequestId()
          ExpectedInstanceId = Some instance; Operation = operation }

    let ready (authority: Authority) cancellation =
        task {
            let! outcome = authority.Runtime.Lifecycle.StartReady cancellation
            Fixture.ok outcome |> ignore
        }

    let dispatch (authority: Authority) (request: Request) cancellation : Task<Response> =
        task {
            let! outcome =
                OwnerAccessAdapter.dispatch authority.Identity authority.Runtime.Owner request cancellation
            return Fixture.ok outcome
        }

    let listen authority target (fatals: ConcurrentQueue<PipeFailure>) cancellation =
        let factory = Factory.createCancellable target (dispatch authority) fatals.Enqueue
        factory.Start cancellation

    let previousIdentity authority target cancellation =
        task {
            do! ready authority cancellation
            let fatals = ConcurrentQueue<PipeFailure>()
            use running = listen authority target fatals cancellation
            let! listening = running.Ready
            Assert.Equal<Result<unit, PipeFailure>>(Ok (), listening)
            let request: Request =
                { RequestId = Codec.newRequestId(); ExpectedInstanceId = None; Operation = Operation.Status }
            let! discovered = (Factory.client target).DiscoverStatus request cancellation
            let identity = (Fixture.ok discovered).Instance
            let! stopped = running.Stop()
            Assert.Equal<Result<unit, PipeFailure>>(Ok (), stopped)
            Assert.Empty(fatals)
            return identity
        }

    let raw target (request: Request) (cancellation: CancellationToken) =
        task {
            use pipe = Native.client target
            do! pipe.ConnectAsync(cancellation)
            do! Framing.write pipe (Codec.encodeRequest request |> Fixture.ok) cancellation
            return! Framing.read pipe cancellation
        }

    let browser (authority: Authority) =
        ApiAudience.text authority.Identity.ApiOrigin |> BrowserOrigin.tryParse |> Fixture.ok

    let pairing (authority: Authority) cancellation : Task<PairingReceipt> =
        task {
            let input: CreateRequest =
                { InstanceId = authority.Identity.InstanceId; Challenge = Fixture.challenge
                  RequestedCapabilities = Fixture.all; BrowserOrigins = [ browser authority ] }
            let! outcome = authority.Runtime.Public.Create input cancellation
            match Fixture.ok outcome with
            | Hermes.Core.LocalAccess.Response.PairingCreated receipt -> return receipt
            | _ -> return failwith "Expected a live pairing."
        }

    let approve (authority: Authority) (receipt: PairingReceipt) cancellation =
        task {
            let input: ApprovalRequest =
                { InstanceId = authority.Identity.InstanceId; PairingId = receipt.PairingId
                  UserCode = receipt.UserCode; Challenge = Fixture.challenge; Capabilities = Fixture.all }
            let! outcome = authority.Runtime.Owner.Approve input cancellation
            Fixture.ok outcome |> ignore
        }

    let issue (authority: Authority) cancellation : Task<SessionContext> =
        task {
            let! receipt = pairing authority cancellation
            do! approve authority receipt cancellation
            let input: ExchangeRequest =
                { InstanceId = authority.Identity.InstanceId; PairingId = receipt.PairingId
                  Verifier = Fixture.verifier; BrowserOrigin = Some(browser authority) }
            let! outcome = authority.Runtime.Public.Exchange (Ok input) cancellation
            match Fixture.ok outcome with
            | Hermes.Core.LocalAccess.Response.SessionIssued session ->
                return
                    { Credential = CredentialInput.Present session.Credential
                      BrowserOrigin = Some(browser authority) }
            | _ -> return failwith "Expected an issued session."
        }

module private StaleOwnerPhaseChecks =
    open StaleOwnerFixture

    let approval (receipt: PairingReceipt) : ApprovalPayload =
        { PairingId = PairingId.text receipt.PairingId
          UserCode = DisplayCode.text receipt.UserCode
          Challenge = Challenge.text Fixture.challenge
          Capabilities = Fixture.all |> Set.toList |> List.map Capability.name }

    let operations (receipt: PairingReceipt) =
        [ Operation.Resolve(DisplayCode.text receipt.UserCode)
          Operation.Approve(approval receipt) ]

    let response (authority: Authority) (request: Request) outcome : Response =
        { RequestId = request.RequestId
          Instance = wireIdentity authority
          Outcome = outcome }

    let adapter authority request outcome cancellation =
        task {
            let expected = response authority request outcome
            let! actual =
                OwnerAccessAdapter.dispatch authority.Identity authority.Runtime.Owner request cancellation
            Assert.Equal<Result<Response, CodecFailure>>(Ok expected, actual)
            let bytes = Codec.encodeRequest request |> Fixture.ok
            let! encoded =
                OwnerAccessAdapter.dispatchBytes authority.Identity authority.Runtime.Owner bytes cancellation
            Assert.Equal<byte>(
                Codec.encodeResponse request expected |> Fixture.ok, Fixture.ok encoded)
        }

    let send target (identity: Identity) (request: Request) cancellation =
        task {
            let! outcome = (Factory.client target).Send request (peer identity) cancellation
            let actual = Fixture.ok outcome
            Assert.Equal(request.RequestId, actual.RequestId)
            Assert.Equal<Identity>(identity, actual.Instance)
            return actual
        }

    let current target authority operation outcome cancellation =
        task {
            let identity = wireIdentity authority
            let request = pinned identity.InstanceId operation
            do! adapter authority request outcome cancellation
            let! actual = send target identity request cancellation
            Assert.Equal<Result<Reply, ErrorCode>>(outcome, actual.Outcome)
        }

    let private stalePipe target (previous: Identity) authority request cancellation =
        task {
            let identity = wireIdentity authority
            let! encoded = raw target request cancellation
            Assert.Equal<Result<Response, CodecFailure>>(
                Error CodecFailure.IdentityMismatch, Codec.decodeResponse request (peer previous) encoded)
            let inspect = { request with ExpectedInstanceId = Some identity.InstanceId }
            let actual = Codec.decodeResponse inspect (peer identity) encoded |> Fixture.ok
            Assert.Equal<Response>(
                response authority request (Error ErrorCode.WrongInstance), actual)
            let! rejected = (Factory.client target).Send request (peer previous) cancellation
            Assert.Equal<Result<Response, PipeFailure>>(
                Error(PipeFailure.InvalidWire CodecFailure.IdentityMismatch), rejected)
        }

    let stale target (previous: Identity) authority receipt cancellation =
        task {
            for operation in Operation.Status :: operations receipt do
                let request = pinned previous.InstanceId operation
                let before = authority.OwnerCalls.Count
                do! stalePipe target previous authority request cancellation
                do! adapter authority request (Error ErrorCode.WrongInstance) cancellation
                Assert.Equal(before, authority.OwnerCalls.Count)
        }

    let private invalidAdapter (authority: Authority) request bytes cancellation =
        task {
            let before = authority.OwnerCalls.Count
            let! actual =
                OwnerAccessAdapter.dispatch authority.Identity authority.Runtime.Owner request cancellation
            Assert.Equal<Result<Response, CodecFailure>>(Error CodecFailure.Malformed, actual)
            let! encoded =
                OwnerAccessAdapter.dispatchBytes authority.Identity authority.Runtime.Owner bytes cancellation
            Assert.Equal<Result<byte array, CodecFailure>>(Error CodecFailure.Malformed, encoded)
            Assert.Equal(before, authority.OwnerCalls.Count)
        }

    let unpinned target authority receipt cancellation =
        task {
            for operation in operations receipt do
                let identity = wireIdentity authority
                let canonical = pinned identity.InstanceId operation
                let request = { canonical with ExpectedInstanceId = None }
                let bytes = Codec.encodeRequest canonical |> Fixture.ok
                            |> OwnerWireSamples.change None "expectedInstanceId" "null"
                do! invalidAdapter authority request bytes cancellation
                let! rejected = (Factory.client target).Send request (peer identity) cancellation
                Assert.Equal<Result<Response, PipeFailure>>(
                    Error(PipeFailure.InvalidWire CodecFailure.Malformed), rejected)
                let! discovery = (Factory.client target).DiscoverStatus request cancellation
                Assert.Equal<Result<Response, PipeFailure>>(
                    Error(PipeFailure.InvalidWire CodecFailure.Malformed), discovery)
        }

    let statusOutcome phase =
        Ok(Reply.Status
            { Phase = phase
              Capabilities = "archive.read" :: (Fixture.all |> Set.toList |> List.map Capability.name) })

    let status target authority outcome cancellation =
        task {
            do! current target authority Operation.Status outcome cancellation
            let request: Request =
                { RequestId = Codec.newRequestId(); ExpectedInstanceId = None; Operation = Operation.Status }
            let! discovered = (Factory.client target).DiscoverStatus request cancellation
            Assert.Equal<Result<Response, PipeFailure>>(
                Ok(response authority request outcome), discovered)
        }

    let alive (authority: Authority) (fatals: ConcurrentQueue<PipeFailure>) (running: Running) =
        Assert.False(running.RunTask.IsCompleted)
        Assert.Empty(fatals)
        Assert.Empty(authority.Notifications)

module private StaleOwnerPhases =
    open StaleOwnerFixture
    open StaleOwnerPhaseChecks

    let private starting target previous receipt authority cancellation =
        task {
            do! status target authority (statusOutcome Phase.Starting) cancellation
            do! stale target previous authority receipt cancellation
            do! unpinned target authority receipt cancellation
            for operation in operations receipt do
                do! current target authority operation (Error ErrorCode.InstanceNotReady) cancellation
            do! status target authority (statusOutcome Phase.Starting) cancellation
        }

    let private resolved target authority (receipt: PairingReceipt) cancellation =
        task {
            let identity = wireIdentity authority
            let request = pinned identity.InstanceId (Operation.Resolve(DisplayCode.text receipt.UserCode))
            let! actual = send target identity request cancellation
            match Fixture.ok actual.Outcome with
            | Reply.Resolved found ->
                Assert.Equal(PairingId.text receipt.PairingId, found.PairingId)
                Assert.Equal(PairingState.Pending, found.State)
                do! adapter authority request actual.Outcome cancellation
                return found
            | _ -> return failwith "Expected the current pending pairing."
        }

    let private admitted (authority: Authority) session cancellation : Task<AccessStatus> =
        task {
            let! outcome = authority.Runtime.Public.Admit Fixture.all session cancellation
            match Fixture.ok outcome with
            | Hermes.Core.LocalAccess.Response.Admitted status ->
                Assert.Equal(AuthenticationState.Authenticated, status.State)
                Assert.Equal(authority.Identity, status.Identity)
                let expected =
                    Fixture.all |> Set.map EffectiveCapability.Authority
                    |> Set.add EffectiveCapability.ArchiveRead
                Assert.Equal<Set<EffectiveCapability>>(expected, status.Capabilities)
                return status
            | _ -> return failwith "Expected retained current-runtime authority."
        }

    let private approve target authority (receipt: PairingReceipt) cancellation =
        task {
            let identity = wireIdentity authority
            let payload = approval receipt
            let request = pinned identity.InstanceId (Operation.Approve payload)
            let! actual = send target identity request cancellation
            match Fixture.ok actual.Outcome with
            | Reply.Approved grant ->
                Assert.Equal(payload.PairingId, grant.PairingId)
                Assert.Equal<string list>(payload.Capabilities, grant.Capabilities)
                Assert.True(grant.ExpiresAt <= receipt.ExpiresAt)
                do! adapter authority request actual.Outcome cancellation
            | _ -> failwith "Expected a correctly pinned owner approval."
        }

    let private ready target previous authority cancellation =
        task {
            do! StaleOwnerFixture.ready authority cancellation
            do! status target authority (statusOutcome Phase.Ready) cancellation
            let! session = issue authority cancellation
            let! receipt = pairing authority cancellation
            let! before = resolved target authority receipt cancellation
            let! originalAuthority = admitted authority session cancellation
            do! stale target previous authority receipt cancellation
            do! unpinned target authority receipt cancellation
            let! after = resolved target authority receipt cancellation
            Assert.Equal<PairingResult>(before, after)
            do! approve target authority receipt cancellation
            let! retained = admitted authority session cancellation
            Assert.Equal<AccessStatus>(originalAuthority, retained)
        }

    let private stopped target previous receipt authority cancellation =
        task {
            do! authority.Runtime.Lifecycle.Stop()
            do! stale target previous authority receipt cancellation
            do! unpinned target authority receipt cancellation
            for operation in operations receipt do
                do! current target authority operation (Error ErrorCode.InternalError) cancellation
            do! status target authority (Error ErrorCode.InternalError) cancellation
        }

    let exercise target previous receipt authority cancellation =
        task {
            let fatals = ConcurrentQueue<PipeFailure>()
            use running = listen authority target fatals cancellation
            let! listening = running.Ready
            Assert.Equal<Result<unit, PipeFailure>>(Ok (), listening)
            do! starting target previous receipt authority cancellation
            alive authority fatals running
            do! ready target previous authority cancellation
            alive authority fatals running
            do! stopped target previous receipt authority cancellation
            alive authority fatals running
            do! OwnerPipeFixture.stopped running
            Assert.Empty(fatals)
            Assert.Empty(authority.Notifications)
        }

module OwnerStaleInstanceTests =
    open StaleOwnerFixture

    let private rejectStale target (previous: Identity) (current: Identity)
        (receipt: PairingReceipt) cancellation =
        task {
            let request = pinned previous.InstanceId (Operation.Resolve(DisplayCode.text receipt.UserCode))
            let! encoded = raw target request cancellation
            Assert.Equal<Result<Response, CodecFailure>>(
                Error CodecFailure.IdentityMismatch, Codec.decodeResponse request (peer previous) encoded)
            let inspect = { request with ExpectedInstanceId = Some current.InstanceId }
            let actual = Codec.decodeResponse inspect (peer current) encoded |> Fixture.ok
            Assert.Equal(current, actual.Instance)
            Assert.Equal<Result<Reply, ErrorCode>>(Error ErrorCode.WrongInstance, actual.Outcome)
            let! rejected = (Factory.client target).Send request (peer previous) cancellation
            Assert.Equal<Result<Response, PipeFailure>>(
                Error(PipeFailure.InvalidWire CodecFailure.IdentityMismatch), rejected)
        }

    let private retainedSession (authority: Authority) session cancellation =
        task {
            let! outcome = authority.Runtime.Public.Admit Fixture.all session cancellation
            match Fixture.ok outcome with
            | Hermes.Core.LocalAccess.Response.Admitted status ->
                Assert.Equal(AuthenticationState.Authenticated, status.State)
                Assert.Equal(authority.Identity, status.Identity)
            | _ -> failwith "Expected retained current-runtime authority."
        }

    let private correctRequest target authority (receipt: PairingReceipt) session cancellation =
        task {
            let current = wireIdentity authority
            let request = pinned current.InstanceId (Operation.Resolve(DisplayCode.text receipt.UserCode))
            let! outcome = (Factory.client target).Send request (peer current) cancellation
            match (Fixture.ok outcome).Outcome |> Fixture.ok with
            | Reply.Resolved found ->
                Assert.Equal(PairingId.text receipt.PairingId, found.PairingId)
                Assert.Equal(PairingState.Pending, found.State)
            | _ -> failwith "Expected the subsequent correctly pinned resolve."
            do! retainedSession authority session cancellation
        }

    let private exercise target previous authority cancellation =
        task {
            do! ready authority cancellation
            let fatals = ConcurrentQueue<PipeFailure>()
            use running = listen authority target fatals cancellation
            let! listening = running.Ready
            Assert.Equal<Result<unit, PipeFailure>>(Ok (), listening)
            let! session = issue authority cancellation
            let! receipt = pairing authority cancellation
            do! rejectStale target previous (wireIdentity authority) receipt cancellation
            do! correctRequest target authority receipt session cancellation
            Assert.False(running.RunTask.IsCompleted)
            Assert.Empty(fatals)
            Assert.Empty(authority.Notifications)
            let! stopped = running.Stop()
            Assert.Equal<Result<unit, PipeFailure>>(Ok (), stopped)
        }

    [<Fact>]
    let ``stale authority request after same target restart cannot terminate the current owner`` () : Task =
        task {
            let target = OwnerPipeFixture.target ()
            use lifetime = new CancellationTokenSource(TimeSpan.FromSeconds 15.0)
            use former = create target
            let! previous = previousIdentity former target lifetime.Token
            do! former.Runtime.Lifecycle.Stop()
            use current = create target
            Assert.NotEqual<string>(previous.InstanceId, InstanceId.text current.Identity.InstanceId)
            do! exercise target previous current lifetime.Token
            Assert.Empty(former.Notifications)
        }

    [<Fact>]
    let ``stale pinned operations cannot terminate an owner while starting ready or stopped`` () : Task =
        task {
            let target = OwnerPipeFixture.target ()
            use lifetime = new CancellationTokenSource(TimeSpan.FromSeconds 30.0)
            use former = create target
            let! previous = previousIdentity former target lifetime.Token
            let! receipt = pairing former lifetime.Token
            do! former.Runtime.Lifecycle.Stop()
            use current = create target
            Assert.NotEqual<string>(previous.InstanceId, InstanceId.text current.Identity.InstanceId)
            do! StaleOwnerPhases.exercise target previous receipt current lifetime.Token
            Assert.Empty(former.Notifications)
        }
