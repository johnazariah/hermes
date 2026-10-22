namespace Hermes.Tests

open System
open System.Collections.Concurrent
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Core.LocalAccessWire
open Hermes.Core.OwnerControlPipe
open Hermes.Service

module OwnerUiFixtures =
    module Input = Hermes.Service.OwnerUiInput

    let epoch = DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero)
    let capabilities = [ "config.read"; "model.invoke"; "mutate" ]
    let userCode = "0123-4567-89AB"
    let ok value = value |> Result.defaultWith (fun _ -> failwith "Synthetic owner fixture rejected.")

    type State =
        { Trace: ConcurrentQueue<string>
          Output: ConcurrentQueue<string>
          Requests: ConcurrentQueue<Request>
          Approvals: ConcurrentQueue<ApprovalPayload>
          Lines: ConcurrentQueue<string option> }

    type Rig =
        { Target: OwnerControlTarget.Target
          Identity: Identity
          Pairing: PairingResult
          State: State
          Effects: Input.Effects }

    let publicStatus (identity: Identity) : byte array =
        JsonSerializer.SerializeToUtf8Bytes
            {| version = 1; instanceId = identity.InstanceId; processId = identity.ProcessId
               processStartTimeUtc = identity.ProcessStartTimeUtc; origin = identity.Origin
               state = "anonymous"; capabilities = [| "archive.read" |]
               expiresAt = Nullable<DateTimeOffset>(); pairingMethod = "ownerApproval" |}

    let payload (pairing: PairingResult) : ApprovalPayload =
        { PairingId = pairing.PairingId; UserCode = pairing.UserCode
          Challenge = pairing.Challenge; Capabilities = pairing.RequestedCapabilities }

    let private approved (pairing: PairingResult) (state: State) (candidate: ApprovalPayload) =
        if candidate <> payload pairing then Error ErrorCode.PairingConflict
        else
            state.Approvals.Enqueue(candidate)
            let grant: ApprovalResult =
                { PairingId = pairing.PairingId; Capabilities = candidate.Capabilities
                  ExpiresAt = epoch.AddSeconds(60.0) }
            Ok(Reply.Approved grant)

    let respond (identity: Identity) (pairing: PairingResult) (state: State) (request: Request) : Response =
        state.Requests.Enqueue(request)
        let outcome =
            match request.Operation with
            | Operation.Status ->
                Ok(Reply.Status { Phase = Phase.Ready; Capabilities = "archive.read" :: capabilities })
            | Operation.Resolve code when code = pairing.UserCode -> Ok(Reply.Resolved pairing)
            | Operation.Approve candidate -> approved pairing state candidate
            | _ -> Error ErrorCode.PairingNotFound
        { RequestId = request.RequestId; Instance = identity; Outcome = outcome }

    let private client (identity: Identity) (pairing: PairingResult) (state: State) : ClientOnly =
        { DiscoverStatus = fun request _ ->
              state.Trace.Enqueue("discover")
              Task.FromResult(Ok(respond identity pairing state request))
          Send = fun request peer _ ->
              Assert.Equal(identity.ProcessId, peer.ProcessId)
              Assert.Equal(identity.ProcessStartTimeUtc, peer.ProcessStartTimeUtc)
              Assert.Equal(identity.ConfigKey, peer.ConfigKey)
              Assert.Equal(identity.Origin, peer.Origin)
              Assert.Equal(Some identity.InstanceId, request.ExpectedInstanceId)
              state.Trace.Enqueue(match request.Operation with Operation.Approve _ -> "approve" | _ -> "resolve")
              Task.FromResult(Ok(respond identity pairing state request)) }

    let read (state: State) (_: CancellationToken) : Task<string option> =
        state.Trace.Enqueue("read")
        let line = match state.Lines.TryDequeue() with true, value -> value | _ -> None
        Task.FromResult line

    let private effects (identity: Identity) (pairing: PairingResult)
        (state: State) : Input.Effects =
        { Client = fun _ -> state.Trace.Enqueue("client"); client identity pairing state
          Status = fun _ _ -> state.Trace.Enqueue("http"); Task.FromResult(Ok(publicStatus identity))
          ReadLine = read state
          Write = state.Output.Enqueue
          Browser = fun address _ ->
              Assert.Equal(identity.Origin + "/", address.OriginalString)
              state.Trace.Enqueue("browser")
              Task.FromResult(Ok ())
          UtcNow = fun () -> epoch
          Elapsed = fun () -> TimeSpan.Zero }

    let makeAt (target: OwnerControlTarget.Target) : Rig =
        let identity: Identity =
            { InstanceId = OwnerWireSamples.id; ProcessId = 4123
              ProcessStartTimeUtc = epoch.AddMinutes(-1.0)
              Origin = target.ApiOrigin; ConfigKey = target.ConfigKey }
        let pairing: PairingResult =
            { PairingId = "AQEBAQEBAQEBAQEBAQEBAQ"; UserCode = userCode
              Challenge = String.replicate 43 "A"; BrowserOrigin = "http://localhost:5173"
              ApiOrigin = target.ApiOrigin; RequestedCapabilities = capabilities
              CreatedAt = epoch; ExpiresAt = epoch.AddSeconds(180.0); State = PairingState.Pending }
        let state: State =
            { Trace = ConcurrentQueue(); Output = ConcurrentQueue(); Requests = ConcurrentQueue()
              Approvals = ConcurrentQueue(); Lines = ConcurrentQueue([ Some "yes" ]) }
        { Target = target; Identity = identity; Pairing = pairing; State = state
          Effects = effects identity pairing state }

    let make () =
        let directory = Path.Combine(Path.GetTempPath(), "hermes-cli-" + Guid.NewGuid().ToString("N"))
        OwnerControlTarget.tryCreate directory 31991 |> ok |> makeAt

    let settings (rig: Rig) : Input.Settings =
        { ConfigDirectory = fun () -> rig.Target.CanonicalDirectory
          Port = fun () -> rig.Target.Port.ToString(Globalization.CultureInfo.InvariantCulture) }

    let runUsing (rig: Rig) (environment: Input.Settings) (effects: Input.Effects)
        (arguments: string array) (cancellation: CancellationToken) : Task<int> =
        ServiceEntry.runEarlyWith environment
            (fun () -> rig.State.Trace.Enqueue("create"); effects) rig.State.Output.Enqueue
            (fun _ -> rig.State.Trace.Enqueue("ordinary"); 73) arguments cancellation

    let run (rig: Rig) (arguments: string array) : Task<int> =
        runUsing rig (settings rig) rig.Effects arguments CancellationToken.None

    let pairArguments = [| "pair-ui"; userCode |]

    let exit (expected: int) (pending: Task<int>) : Task<unit> =
        task {
            let! actual = pending
            Assert.Equal<int>(expected, actual)
        }
