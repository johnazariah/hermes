module Hermes.Tests.LocalAccessRuntimeTests

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core.LocalAccess
open Hermes.Tests.LocalAccessDomainTests

module internal Probe =
    let timeout = TimeSpan.FromSeconds 10.0
    let token = CancellationToken.None

    type Gate =
        { Entered: TaskCompletionSource<unit>
          Resume: TaskCompletionSource<unit> }

    type Held = { Gate: Gate; Pending: AccessReply }

    type Effects =
        { Reads: ConcurrentQueue<unit>
          Sizes: ConcurrentQueue<int>
          Moments: ConcurrentQueue<Moment>
          Blocks: ConcurrentQueue<Gate>
          AllBlocks: ConcurrentQueue<Gate>
          ClockFaults: ConcurrentQueue<exn>
          EntropyFaults: ConcurrentQueue<exn>
          ForcedSecrets: ConcurrentQueue<byte array>
          Secrets: ConcurrentQueue<byte array>
          Notifications: ConcurrentQueue<unit> }

    let release (gate: Gate) : unit = gate.Resume.TrySetResult(()) |> ignore

    type Rig =
        { Runtime: LocalAccessRuntime
          Effects: Effects }
        interface IDisposable with
            member this.Dispose() =
                this.Effects.AllBlocks |> Seq.iter release
                this.Runtime.Lifecycle.Stop().WaitAsync(timeout).GetAwaiter().GetResult()

    let private effects () : Effects =
        { Reads = ConcurrentQueue<unit>(); Sizes = ConcurrentQueue<int>()
          Moments = ConcurrentQueue<Moment>(); Blocks = ConcurrentQueue<Gate>()
          AllBlocks = ConcurrentQueue<Gate>(); ClockFaults = ConcurrentQueue<exn>()
          EntropyFaults = ConcurrentQueue<exn>(); ForcedSecrets = ConcurrentQueue<byte array>()
          Secrets = ConcurrentQueue<byte array>(); Notifications = ConcurrentQueue<unit>() }

    let private throwNext (faults: ConcurrentQueue<exn>) : unit =
        match faults.TryDequeue() with
        | true, error -> raise error
        | _ -> ()

    let private read (probe: Effects) () : Moment =
        probe.Reads.Enqueue(())
        match probe.Blocks.TryDequeue() with
        | true, gate ->
            gate.Entered.TrySetResult(()) |> ignore
            gate.Resume.Task.WaitAsync(timeout).GetAwaiter().GetResult()
        | _ -> ()
        throwNext probe.ClockFaults
        match probe.Moments.TryDequeue() with
        | true, moment -> moment
        | _ -> { Elapsed = TimeSpan.Zero; Utc = Fixture.epoch }

    let private bytes (probe: Effects) (size: int) : byte array =
        probe.Sizes.Enqueue(size)
        throwNext probe.EntropyFaults
        let generated = (Fixture.entropy probe.Sizes.Count).Bytes size
        let selected =
            if size = 32 then
                match probe.ForcedSecrets.TryDequeue() with
                | true, forced -> forced
                | _ -> generated
            else generated
        if size = 32 then probe.Secrets.Enqueue(selected)
        selected

    let make () : Rig =
        let probe = effects ()
        let clock: Clock = { Read = read probe }
        let entropy: Entropy = { Bytes = bytes probe }
        let supervisor: RuntimeSupervisor =
            { Unavailable = fun () -> probe.Notifications.Enqueue(()) }
        let runtime =
            LocalAccessRuntime.createWith clock entropy supervisor
                Fixture.identity Fixture.all Set.empty |> Fixture.ok
        { Runtime = runtime; Effects = probe }

    let at (seconds: int) (rig: Rig) : unit =
        rig.Effects.Moments.Enqueue(
            { Elapsed = TimeSpan.FromSeconds(float seconds)
              Utc = Fixture.epoch.AddSeconds(float seconds) })

    let status (rig: Rig) : AccessReply = rig.Runtime.Public.Status Fixture.anonymous token

    let hold (rig: Rig) (invoke: unit -> AccessReply) : Task<Held> =
        task {
            let gate =
                { Entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                  Resume = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously) }
            rig.Effects.AllBlocks.Enqueue(gate)
            rig.Effects.Blocks.Enqueue(gate)
            let pending = invoke ()
            do! gate.Entered.Task.WaitAsync(timeout)
            return { Gate = gate; Pending = pending }
        }

    let resume (held: Held) : AccessReply =
        release held.Gate
        held.Pending.WaitAsync(timeout)

module internal Flow =
    open Probe

    let error (expected: AccessError) (reply: Result<Response, AccessError>) : unit =
        reply |> Result.map (fun _ -> failwith "Expected rejection.")
        |> Result.defaultWith (fun actual -> Assert.Equal<AccessError>(expected, actual))

    let controlPhase (expected: AccessPhase) (reply: Result<Response, AccessError>) : unit =
        match Fixture.ok reply with
        | Response.ControlStatus(_, phase, _) -> Assert.Equal<AccessPhase>(expected, phase)
        | _ -> failwith "Expected control status."

    let receipt (reply: Result<Response, AccessError>) : PairingReceipt =
        match Fixture.ok reply with
        | Response.PairingCreated value -> value
        | _ -> failwith "Expected pairing receipt."

    let session (reply: Result<Response, AccessError>) : IssuedSession =
        match Fixture.ok reply with
        | Response.SessionIssued value -> value
        | _ -> failwith "Expected issued session."

    let admitted (reply: Result<Response, AccessError>) : AccessStatus =
        match Fixture.ok reply with
        | Response.Admitted value -> value
        | _ -> failwith "Expected admission."

    let revoked (reply: Result<Response, AccessError>) : unit =
        match Fixture.ok reply with
        | Response.Revoked -> ()
        | _ -> failwith "Expected revocation."

    let private resolved (expected: PairingReceipt)
        (reply: Result<Response, AccessError>) : unit =
        match Fixture.ok reply with
        | Response.PairingResolved view ->
            Assert.Equal(expected.PairingId, view.PairingId)
            Assert.Equal(expected.UserCode, view.UserCode)
            Assert.True(Challenge.equal Fixture.challenge view.Challenge)
            Assert.Equal(expected.BrowserOrigin, view.BrowserOrigin)
            Assert.Equal(expected.ApiOrigin, view.ApiOrigin)
            Assert.Equal<Set<Capability>>(expected.RequestedCapabilities, view.RequestedCapabilities)
        | _ -> failwith "Expected resolved pairing."

    let ready (rig: Rig) : Task<unit> =
        task {
            let! reply = rig.Runtime.Lifecycle.StartReady token |> fun work -> work.WaitAsync(timeout)
            Fixture.ok reply |> ignore
        }

    let pair (rig: Rig) : Task<PairingReceipt> =
        task {
            let request = Fixture.creation Fixture.all [ Fixture.origin ]
            let! reply = rig.Runtime.Public.Create request token |> fun work -> work.WaitAsync(timeout)
            return receipt reply
        }

    let approve (rig: Rig) (pairing: PairingReceipt) : Task<unit> =
        task {
            let! found = rig.Runtime.Owner.Resolve Fixture.instance pairing.UserCode token
            resolved pairing found
            let request: ApprovalRequest =
                { InstanceId = Fixture.instance; PairingId = pairing.PairingId
                  UserCode = pairing.UserCode; Challenge = Fixture.challenge; Capabilities = Fixture.all }
            let! reply = rig.Runtime.Owner.Approve request token
            Fixture.ok reply |> ignore
        }

    let approved (rig: Rig) : Task<PairingReceipt> =
        task {
            let! pairing = pair rig
            do! approve rig pairing
            return pairing
        }

    let exchange (rig: Rig) (pairing: PairingReceipt)
        (cancellation: CancellationToken) : AccessReply =
        let request: ExchangeRequest =
            { InstanceId = Fixture.instance; PairingId = pairing.PairingId
              Verifier = Fixture.verifier; BrowserOrigin = Some Fixture.origin }
        rig.Runtime.Public.Exchange (Ok request) cancellation

    let issue (rig: Rig) : Task<IssuedSession> =
        task {
            let! pairing = approved rig
            let! reply = exchange rig pairing token
            return session reply
        }

module Behavior =
    open Probe
    open Flow

    [<Fact>]
    let ``starting is default and only lifecycle explicitly enters ready`` () =
        task {
            use rig = make ()
            let! starting = rig.Runtime.Owner.ControlStatus None token
            controlPhase AccessPhase.Starting starting
            Assert.Equal<int array>([| 16; 12; 32 |], rig.Effects.Sizes.ToArray())
            let! denied = status rig
            error AccessError.AccessUnavailable denied
            let code = DisplayCode.ofBytes (Array.zeroCreate 12) |> Fixture.ok
            let! unresolved = rig.Runtime.Owner.Resolve Fixture.instance code token
            error AccessError.InstanceNotReady unresolved
            do! ready rig
            let! live = rig.Runtime.Owner.ControlStatus (Some Fixture.instance) token
            controlPhase AccessPhase.Ready live
        }

    [<Fact>]
    let ``synthetic owner approval produces scoped public admission`` () =
        task {
            use rig = make ()
            do! ready rig
            let! issued = issue rig
            let context = Fixture.supplied issued (Some Fixture.origin)
            let! reply = rig.Runtime.Public.Admit Fixture.all context token
            let access = admitted reply
            Assert.Equal(AuthenticationState.Authenticated, access.State)
            Assert.Equal<Set<EffectiveCapability>>(EffectiveCapability.ofGrant Fixture.all, access.Capabilities)
            Assert.Empty(rig.Effects.Notifications)
        }

    [<Fact>]
    let ``rejected malformed exchanges commit their rate window and its pruning`` () =
        task {
            use rig = make ()
            do! ready rig
            for _ in 1 .. 120 do
                let! denied = rig.Runtime.Public.Exchange (Error InvalidExchange.NoncanonicalProof) token
                error AccessError.InvalidPairingProof denied
            let! limited = rig.Runtime.Public.Exchange (Error InvalidExchange.NoncanonicalProof) token
            error AccessError.AccessRateLimited limited
            at 60 rig
            let! renewed = rig.Runtime.Public.Exchange (Error InvalidExchange.NoncanonicalProof) token
            error AccessError.InvalidPairingProof renewed
        }

    type private Collision =
        { Original: IssuedSession
          Pairing: PairingReceipt
          Secret: byte array }

    let private retiring (rig: Rig) : Task<Collision> =
        task {
            let! original = issue rig
            let secret = rig.Effects.Secrets.ToArray() |> Array.last
            at 1799 rig
            let! pairing = approved rig
            return { Original = original; Pairing = pairing; Secret = secret }
        }

    [<Fact>]
    let ``collision rejection commits expiry pruning and monotonic high water`` () =
        task {
            use rig = make ()
            do! ready rig
            let! collision = retiring rig
            at 1800 rig
            rig.Effects.ForcedSecrets.Enqueue(collision.Secret)
            let! denied = exchange rig collision.Pairing token
            error AccessError.AccessUnavailable denied
            at 1799 rig
            let! expired = rig.Runtime.Public.Status (Fixture.supplied collision.Original None) token
            error AccessError.CredentialExpired expired
            at 1800 rig
            let! fresh = exchange rig collision.Pairing token
            session fresh |> ignore
        }

module InterpreterFailure =
    open Probe
    open Flow

    let private fatal (rig: Rig) : Task<unit> =
        task {
            let! failed = status rig
            error AccessError.AccessUnavailable failed
            do! rig.Runtime.Lifecycle.Stop().WaitAsync(timeout)
            Assert.Single(rig.Effects.Notifications) |> ignore
            let reads = rig.Effects.Reads.Count
            let! unavailable = rig.Runtime.Lifecycle.StartReady token
            error AccessError.AccessUnavailable unavailable
            Assert.Equal(reads, rig.Effects.Reads.Count)
        }

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``known interpreter exceptions fail closed and notify once`` (clockFailure: bool) =
        task {
            use rig = make ()
            do! ready rig
            if clockFailure then
                rig.Effects.ClockFaults.Enqueue(InvalidOperationException("Synthetic clock failure."))
            else
                rig.Effects.EntropyFaults.Enqueue(CryptographicException("Synthetic entropy failure."))
            do! fatal rig
        }

    [<Fact>]
    let ``invalid entropy has no fallback and also fails closed`` () =
        task {
            use rig = make ()
            do! ready rig
            rig.Effects.ForcedSecrets.Enqueue([||])
            do! fatal rig
        }
