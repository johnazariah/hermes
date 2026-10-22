namespace Hermes.Service

open System.Threading
open System.Threading.Tasks
open Hermes.Core
open Hermes.Core.LocalAccess

module private OwnerWireMapping =
    open HttpAccessJson

    let identity (value: Identity) : Hermes.Core.LocalAccessWire.Identity =
        { InstanceId = InstanceId.text value.InstanceId; ProcessId = value.ProcessId
          ProcessStartTimeUtc = value.ProcessStartTimeUtc
          Origin = ApiAudience.text value.ApiOrigin; ConfigKey = value.ConfigKey }

    let phase (value: AccessPhase) : Hermes.Core.LocalAccessWire.Phase =
        match value with
        | AccessPhase.Starting -> Hermes.Core.LocalAccessWire.Phase.Starting
        | AccessPhase.Ready -> Hermes.Core.LocalAccessWire.Phase.Ready
        | AccessPhase.Stopping -> Hermes.Core.LocalAccessWire.Phase.Stopping

    let private error (value: AccessError) : Hermes.Core.LocalAccessWire.ErrorCode =
        match value with
        | AccessError.InvalidRequest -> Hermes.Core.LocalAccessWire.ErrorCode.InvalidRequest
        | AccessError.WrongInstance -> Hermes.Core.LocalAccessWire.ErrorCode.WrongInstance
        | AccessError.InstanceNotReady -> Hermes.Core.LocalAccessWire.ErrorCode.InstanceNotReady
        | AccessError.PairingNotFound -> Hermes.Core.LocalAccessWire.ErrorCode.PairingNotFound
        | AccessError.PairingExpired -> Hermes.Core.LocalAccessWire.ErrorCode.PairingExpired
        | AccessError.PairingConsumed -> Hermes.Core.LocalAccessWire.ErrorCode.PairingConsumed
        | AccessError.PairingConflict -> Hermes.Core.LocalAccessWire.ErrorCode.PairingConflict
        | AccessError.InsufficientCapability -> Hermes.Core.LocalAccessWire.ErrorCode.InsufficientCapability
        | AccessError.AccessCapacityReached -> Hermes.Core.LocalAccessWire.ErrorCode.AccessCapacityReached
        | _ -> Hermes.Core.LocalAccessWire.ErrorCode.InternalError

    let private pairing (value: PairingView) : Hermes.Core.LocalAccessWire.PairingResult =
        { PairingId = PairingId.text value.PairingId; UserCode = DisplayCode.text value.UserCode
          Challenge = Challenge.text value.Challenge
          BrowserOrigin = BrowserOrigin.text value.BrowserOrigin
          ApiOrigin = ApiAudience.text value.ApiOrigin
          RequestedCapabilities = HttpAccessValues.authorityNames value.RequestedCapabilities |> Array.toList
          CreatedAt = value.CreatedAt; ExpiresAt = value.ExpiresAt
          State =
            if value.State = PairingPhase.Pending then Hermes.Core.LocalAccessWire.PairingState.Pending
            else Hermes.Core.LocalAccessWire.PairingState.Approved }

    let private approved (value: ApprovalView) : Hermes.Core.LocalAccessWire.ApprovalResult =
        { PairingId = PairingId.text value.PairingId
          Capabilities = HttpAccessValues.authorityNames value.Capabilities |> Array.toList
          ExpiresAt = value.ExpiresAt }

    let private status (state: AccessPhase) (capabilities: Set<Capability>)
        : Hermes.Core.LocalAccessWire.StatusResult =
        { Phase = phase state
          Capabilities =
            EffectiveCapability.name EffectiveCapability.ArchiveRead
            :: (HttpAccessValues.authorityNames capabilities |> Array.toList) }

    let private reply (expected: Identity) (value: Response)
        : Result<Hermes.Core.LocalAccessWire.Reply, Hermes.Core.LocalAccessWire.ErrorCode> =
        match value with
        | Response.ControlStatus(current, state, capabilities) when current = expected ->
            status state capabilities |> Hermes.Core.LocalAccessWire.Reply.Status |> Ok
        | Response.PairingResolved found ->
            pairing found |> Hermes.Core.LocalAccessWire.Reply.Resolved |> Ok
        | Response.PairingApproved grant ->
            approved grant |> Hermes.Core.LocalAccessWire.Reply.Approved |> Ok
        | _ -> Error Hermes.Core.LocalAccessWire.ErrorCode.InternalError

    let response (identityValue: Identity) (request: Hermes.Core.LocalAccessWire.Request)
        (outcome: Result<Response, AccessError>) : Hermes.Core.LocalAccessWire.Response =
        let wireOutcome: Result<Hermes.Core.LocalAccessWire.Reply, Hermes.Core.LocalAccessWire.ErrorCode> =
            outcome |> Result.mapError error |> Result.bind (reply identityValue)
        { RequestId = request.RequestId
          Instance = identity identityValue
          Outcome = wireOutcome }

    let pin (identity: Identity) (request: Hermes.Core.LocalAccessWire.Request)
        : Result<InstanceId option, AccessError> =
        request.ExpectedInstanceId |> Option.map (InstanceId.tryParse >> Result.map Some)
        |> Option.defaultValue (Ok None)
        |> Result.bind (fun expected ->
            // Composition identity wins before phase-sensitive owner dispatch.
            if expected |> Option.exists (fun instance -> instance <> identity.InstanceId) then
                Error AccessError.WrongInstance
            else Ok expected)

    let approval (instance: InstanceId) (value: Hermes.Core.LocalAccessWire.ApprovalPayload)
        : Result<ApprovalRequest, AccessError> =
        result {
            let! pairingId = PairingId.tryParse value.PairingId
            let! userCode = DisplayCode.tryParse value.UserCode
            let! challenge = Challenge.tryParse value.Challenge
            let! capabilities = HttpAccessValues.capabilities value.Capabilities
                                |> Result.mapError (fun _ -> AccessError.InvalidRequest)
            return
                { InstanceId = instance; PairingId = pairingId; UserCode = userCode
                  Challenge = challenge; Capabilities = capabilities }
        }

module private OwnerDispatch =
    let private failed (error: AccessError) : AccessReply =
        Task.FromResult(Error error)

    let private pinned (owner: OwnerAccess) (request: Hermes.Core.LocalAccessWire.Request)
        (instance: InstanceId) (cancellation: CancellationToken) : AccessReply =
        match request.Operation with
        | Hermes.Core.LocalAccessWire.Operation.Status -> owner.ControlStatus (Some instance) cancellation
        | Hermes.Core.LocalAccessWire.Operation.Resolve text ->
            DisplayCode.tryParse text
            |> Result.map (fun code -> owner.Resolve instance code cancellation)
            |> Result.defaultWith failed
        | Hermes.Core.LocalAccessWire.Operation.Approve payload ->
            OwnerWireMapping.approval instance payload
            |> Result.map (fun value -> owner.Approve value cancellation)
            |> Result.defaultWith failed

    let run (identity: Identity) (owner: OwnerAccess) (request: Hermes.Core.LocalAccessWire.Request)
        (cancellation: CancellationToken) : AccessReply =
        OwnerWireMapping.pin identity request
        |> Result.map (fun pin ->
            pin |> Option.map (fun instance -> pinned owner request instance cancellation)
            |> Option.defaultWith (fun () ->
                match request.Operation with
                | Hermes.Core.LocalAccessWire.Operation.Status -> owner.ControlStatus None cancellation
                | Hermes.Core.LocalAccessWire.Operation.Resolve _
                | Hermes.Core.LocalAccessWire.Operation.Approve _ -> failed AccessError.InvalidRequest))
        |> Result.defaultWith failed

/// This adapter is for an already owner-validated private transport, never HTTP registration.
[<RequireQualifiedAccess>]
module OwnerAccessAdapter =
    let private invoke (identity: Identity) (owner: OwnerAccess)
        (request: Hermes.Core.LocalAccessWire.Request) (cancellation: CancellationToken)
        : Task<Result<Hermes.Core.LocalAccessWire.Response, Hermes.Core.LocalAccessWire.CodecFailure>> =
        task {
            let! outcome = OwnerDispatch.run identity owner request cancellation
            let response = OwnerWireMapping.response identity request outcome
            return
                Hermes.Core.LocalAccessWire.Codec.encodeResponse request response
                |> Result.map (fun _ -> response)
        }

    let dispatch (identity: Identity) (owner: OwnerAccess)
        (request: Hermes.Core.LocalAccessWire.Request) (cancellation: CancellationToken)
        : Task<Result<Hermes.Core.LocalAccessWire.Response, Hermes.Core.LocalAccessWire.CodecFailure>> =
        Hermes.Core.LocalAccessWire.Codec.encodeRequest request
        |> Result.map (fun _ -> invoke identity owner request cancellation)
        |> Result.defaultWith (fun failure -> Task.FromResult(Error failure))

    let dispatchBytes (identity: Identity) (owner: OwnerAccess) (bytes: byte array)
        (cancellation: CancellationToken)
        : Task<Result<byte array, Hermes.Core.LocalAccessWire.CodecFailure>> =
        Hermes.Core.LocalAccessWire.Codec.decodeRequest bytes
        |> Result.map (fun request ->
            dispatch identity owner request cancellation
            |> Prelude.TaskResult.bind (fun response ->
                Hermes.Core.LocalAccessWire.Codec.encodeResponse request response |> Task.FromResult))
        |> Result.defaultWith (fun failure -> Task.FromResult(Error failure))
