namespace Hermes.Core.LocalAccess

open System
open ModelResult

[<NoEquality; NoComparison>]
type internal LivePairing =
    { View: PairingView
      CreatedTick: TimeSpan
      Deadline: TimeSpan
      Grant: Set<Capability> option
      NextPoll: TimeSpan option }

type internal LiveSession =
    { Digest: CredentialDigest
      BrowserOrigin: BrowserOrigin
      Capabilities: Set<Capability>
      Deadline: TimeSpan
      ExpiresAt: DateTimeOffset }

[<RequireQualifiedAccess>]
type internal TombstoneKey =
    | Pairing of PairingId
    | Credential of CredentialDigest

type internal Tombstone =
    { Error: AccessError
      UserCode: DisplayCode option
      Until: TimeSpan }

[<NoEquality; NoComparison; StructuredFormatDisplay("{SafeDisplay}")>]
type AuthorityState =
    internal
        { Identity: Identity
          Phase: AccessPhase
          Policy: Set<Capability>
          AllowedOrigins: Set<BrowserOrigin>
          Pairings: Map<PairingId, LivePairing>
          Sessions: Map<CredentialDigest, LiveSession>
          Tombstones: Map<TombstoneKey, Tombstone>
          Creations: TimeSpan list
          InvalidExchanges: TimeSpan list
          LastElapsed: TimeSpan }
    member _.SafeDisplay = "<access state>"
    override this.ToString() = this.SafeDisplay

[<NoEquality; NoComparison>]
type Transition = { Response: Response; Next: AuthorityState }

[<NoEquality; NoComparison>]
type Rejection = { Error: AccessError; Next: AuthorityState }

type StateCounts =
    { Phase: AccessPhase
      Pairings: int
      Sessions: int
      Tombstones: int
      Creations: int
      InvalidExchanges: int
      HighWater: TimeSpan }

[<NoEquality; NoComparison>]
type internal Outcome = { State: AuthorityState; Result: Result<Response, AccessError> }

module internal StateModel =
    let pendingLife = TimeSpan.FromSeconds 180.0
    let approvalLife = TimeSpan.FromSeconds 60.0
    let sessionLife = TimeSpan.FromMinutes 30.0
    let minute = TimeSpan.FromMinutes 1.0
    let pollInterval = TimeSpan.FromSeconds 2.0

    let accept (response: Response) (state: AuthorityState) : Outcome =
        { State = state; Result = Ok response }

    let reject (error: AccessError) (state: AuthorityState) : Outcome =
        { State = state; Result = Error error }

    let finish (state: AuthorityState) (computation: Result<Outcome, AccessError>) : Outcome =
        computation |> Result.defaultWith (fun error -> reject error state)

    let remember (eventTick: TimeSpan) (key: TombstoneKey) (error: AccessError)
        (code: DisplayCode option) (state: AuthorityState) : AuthorityState =
        let until = eventTick + pendingLife
        if until <= state.LastElapsed then state
        else
            let tombstone: Tombstone = { Error = error; UserCode = code; Until = until }
            let bounded: Map<TombstoneKey, Tombstone> =
                state.Tombstones |> Map.add key tombstone |> Map.toList
                |> List.sortByDescending (fun (_, (entry: Tombstone)) -> entry.Until)
                |> List.truncate 256 |> Map.ofList
            { state with Tombstones = bounded }

    let private prunePairings (now: TimeSpan) (state: AuthorityState) : AuthorityState =
        state.Pairings |> Map.fold (fun (current: AuthorityState) (identifier: PairingId) (pairing: LivePairing) ->
            if pairing.Deadline > now then current
            else
                { current with Pairings = current.Pairings |> Map.remove identifier }
                |> remember pairing.Deadline (TombstoneKey.Pairing identifier)
                    AccessError.PairingExpired (Some pairing.View.UserCode)) state

    let private pruneSessions (now: TimeSpan) (state: AuthorityState) : AuthorityState =
        state.Sessions |> Map.fold (fun (current: AuthorityState) (digest: CredentialDigest) (session: LiveSession) ->
            if session.Deadline > now then current
            else
                { current with Sessions = current.Sessions |> Map.remove digest }
                |> remember session.Deadline (TombstoneKey.Credential digest)
                    AccessError.CredentialExpired None) state

    let prepare (evidence: Evidence) (state: AuthorityState) : AuthorityState =
        let now = max state.LastElapsed evidence.Now.Elapsed
        { state with
            LastElapsed = now
            Creations = state.Creations |> List.filter (fun tick -> tick > now - minute)
            InvalidExchanges = state.InvalidExchanges |> List.filter (fun tick -> tick > now - minute)
            Tombstones = state.Tombstones |> Map.filter (fun _ (entry: Tombstone) -> entry.Until > now) }
        |> prunePairings now |> pruneSessions now

    let clear (state: AuthorityState) : AuthorityState =
        { state with Phase = AccessPhase.Stopping; Pairings = Map.empty; Sessions = Map.empty
                     Tombstones = Map.empty; Creations = []; InvalidExchanges = [] }

    let findPairing (identifier: PairingId) (state: AuthorityState) : Result<LivePairing, AccessError> =
        state.Pairings |> Map.tryFind identifier |> Option.map Ok
        |> Option.defaultWith (fun () ->
            state.Tombstones |> Map.tryFind (TombstoneKey.Pairing identifier)
            |> Option.map (fun (entry: Tombstone) -> Error entry.Error)
            |> Option.defaultValue (Error AccessError.PairingNotFound))

    let sameInstance (identifier: InstanceId) (state: AuthorityState) : bool =
        identifier = state.Identity.InstanceId

    let receipt (state: AuthorityState) (view: PairingView) : PairingReceipt =
        { InstanceId = state.Identity.InstanceId; PairingId = view.PairingId
          UserCode = view.UserCode; BrowserOrigin = view.BrowserOrigin; ApiOrigin = view.ApiOrigin
          RequestedCapabilities = view.RequestedCapabilities; ExpiresAt = view.ExpiresAt }

module internal PairingOperations =
    let private origin (request: CreateRequest) (state: AuthorityState) : Result<BrowserOrigin, AccessError> =
        match request.BrowserOrigins with
        | [ supplied ] when Set.contains supplied state.AllowedOrigins -> Ok supplied
        | _ -> Error AccessError.InvalidRequest

    let private unused (evidence: Evidence) (state: AuthorityState) : bool =
        not (Map.containsKey evidence.PairingId state.Pairings)
        && not (Map.containsKey (TombstoneKey.Pairing evidence.PairingId) state.Tombstones)
        && not (state.Pairings |> Map.exists (fun _ (pairing: LivePairing) -> pairing.View.UserCode = evidence.UserCode))
        && not (state.Tombstones |> Map.exists (fun _ (entry: Tombstone) -> entry.UserCode = Some evidence.UserCode))

    let private pending (evidence: Evidence) (request: CreateRequest)
        (browserOrigin: BrowserOrigin) (state: AuthorityState) : LivePairing =
        let expires = evidence.Now.Utc + StateModel.pendingLife
        let view: PairingView =
            { PairingId = evidence.PairingId; UserCode = evidence.UserCode; Challenge = request.Challenge
              BrowserOrigin = browserOrigin; ApiOrigin = state.Identity.ApiOrigin
              RequestedCapabilities = request.RequestedCapabilities; CreatedAt = evidence.Now.Utc
              ExpiresAt = expires; State = PairingPhase.Pending }
        { View = view
          CreatedTick = state.LastElapsed; Deadline = state.LastElapsed + StateModel.pendingLife
          Grant = None; NextPoll = None }

    let create (evidence: Evidence) (request: CreateRequest) (state: AuthorityState) : Outcome =
        result {
            do! require AccessError.WrongInstance (StateModel.sameInstance request.InstanceId state)
            let! browserOrigin = origin request state
            do! require AccessError.AccessRateLimited (List.length state.Creations < 30)
            do! require AccessError.AccessCapacityReached (Map.count state.Pairings < 32)
            do! require AccessError.InternalError (unused evidence state)
            let pairing: LivePairing = pending evidence request browserOrigin state
            let next: AuthorityState =
                { state with Pairings = state.Pairings |> Map.add evidence.PairingId pairing
                             Creations = state.LastElapsed :: state.Creations }
            return StateModel.accept (Response.PairingCreated(StateModel.receipt state pairing.View)) next
        } |> StateModel.finish state

    let resolve (instance: InstanceId) (code: DisplayCode) (state: AuthorityState) : Outcome =
        result {
            do! require AccessError.WrongInstance (StateModel.sameInstance instance state)
            return!
                state.Pairings |> Map.tryPick (fun _ (pairing: LivePairing) ->
                    if pairing.View.UserCode = code then Some pairing.View else None)
                |> Option.map (fun (view: PairingView) -> Ok(StateModel.accept (Response.PairingResolved view) state))
                |> Option.defaultWith (fun () ->
                    state.Tombstones |> Map.tryPick (fun _ (entry: Tombstone) ->
                        if entry.UserCode = Some code then Some entry.Error else None)
                    |> Option.defaultValue AccessError.PairingNotFound |> Error)
        } |> StateModel.finish state

    let private approvalView (pairing: LivePairing) (capabilities: Set<Capability>) : ApprovalView =
        { PairingId = pairing.View.PairingId; Capabilities = capabilities; ExpiresAt = pairing.View.ExpiresAt }

    let private applyApproval (capabilities: Set<Capability>) (pairing: LivePairing) (state: AuthorityState) : LivePairing =
        let deadline = min pairing.Deadline (state.LastElapsed + StateModel.approvalLife)
        let expires = pairing.View.CreatedAt + (deadline - pairing.CreatedTick)
        { pairing with Deadline = deadline; Grant = Some capabilities
                       View = { pairing.View with ExpiresAt = expires; State = PairingPhase.Approved } }

    let private grant (capabilities: Set<Capability>) (pairing: LivePairing)
        (state: AuthorityState) : Result<LivePairing, AccessError> =
        match pairing.Grant with
        | Some existing when existing <> capabilities -> Error AccessError.PairingConflict
        | Some _ -> Ok pairing
        | None -> Ok(applyApproval capabilities pairing state)

    let approve (request: ApprovalRequest) (state: AuthorityState) : Outcome =
        result {
            do! require AccessError.WrongInstance (StateModel.sameInstance request.InstanceId state)
            let! pairing = StateModel.findPairing request.PairingId state
            do! require AccessError.PairingConflict
                    (request.UserCode = pairing.View.UserCode && Challenge.equal pairing.View.Challenge request.Challenge)
            do! require AccessError.InsufficientCapability
                    (Set.isSubset request.Capabilities pairing.View.RequestedCapabilities
                     && Set.isSubset request.Capabilities state.Policy)
            let! approved = grant request.Capabilities pairing state
            let next: AuthorityState = { state with Pairings = state.Pairings |> Map.add request.PairingId approved }
            return StateModel.accept (Response.PairingApproved(approvalView approved request.Capabilities)) next
        } |> StateModel.finish state

module internal SessionOperations =
    let private expired (digest: CredentialDigest) (state: AuthorityState) : AccessError =
        state.Tombstones |> Map.fold (fun (found: AccessError option) (key: TombstoneKey) (entry: Tombstone) ->
            match key with
            | TombstoneKey.Credential expected when CredentialDigest.equal expected digest -> Some entry.Error
            | _ -> found) None
        |> Option.defaultValue AccessError.InvalidCredential

    let find (credential: SessionCredential) (state: AuthorityState) : Result<LiveSession, AccessError> =
        let digest = CredentialDigest.create credential
        state.Sessions |> Map.fold (fun (found: LiveSession option) (_: CredentialDigest) (session: LiveSession) ->
            if CredentialDigest.equal session.Digest digest then Some session else found) None
        |> Option.map Ok |> Option.defaultWith (fun () -> Error(expired digest state))

    let authenticate (context: SessionContext) (state: AuthorityState) : Result<LiveSession option, AccessError> =
        match context.Credential with
        | CredentialInput.Absent -> Ok None
        | CredentialInput.Invalid -> Error AccessError.InvalidCredential
        | CredentialInput.Present credential ->
            result {
                do! require AccessError.WrongInstance
                        (StateModel.sameInstance (SessionCredential.instanceId credential) state)
                let! session = find credential state
                do! require AccessError.SessionOriginMismatch
                        (context.BrowserOrigin |> Option.forall (fun origin -> origin = session.BrowserOrigin))
                return Some session
            }

    let private status (state: AuthorityState) (session: LiveSession option) : AccessStatus =
        let grant = session |> Option.map (fun (value: LiveSession) -> value.Capabilities) |> Option.defaultValue Set.empty
        { Identity = state.Identity
          State = if Option.isSome session then AuthenticationState.Authenticated else AuthenticationState.Anonymous
          Capabilities = EffectiveCapability.ofGrant grant
          ExpiresAt = session |> Option.map (fun (value: LiveSession) -> value.ExpiresAt) }

    let getStatus (context: SessionContext) (state: AuthorityState) : Outcome =
        authenticate context state |> Result.map (status state >> Response.Status >> fun reply -> StateModel.accept reply state)
        |> StateModel.finish state

    let admit (required: Set<Capability>) (context: SessionContext) (state: AuthorityState) : Outcome =
        result {
            let! session = authenticate context state
            let grant = session |> Option.map (fun (value: LiveSession) -> value.Capabilities) |> Option.defaultValue Set.empty
            let error = if Option.isSome session then AccessError.InsufficientCapability else AccessError.AuthenticationRequired
            do! require error (Set.isSubset required grant)
            return StateModel.accept (Response.Admitted(status state session)) state
        } |> StateModel.finish state

    let revoke (context: SessionContext) (state: AuthorityState) : Outcome =
        result {
            let! authenticated = authenticate context state
            let! session =
                authenticated |> Option.map Ok |> Option.defaultValue (Error AccessError.AuthenticationRequired)
            let next: AuthorityState =
                { state with Sessions = state.Sessions |> Map.remove session.Digest }
                |> StateModel.remember state.LastElapsed (TombstoneKey.Credential session.Digest)
                    AccessError.InvalidCredential None
            return StateModel.accept Response.Revoked next
        } |> StateModel.finish state

module internal ExchangeOperations =
    let private unused (digest: CredentialDigest) (state: AuthorityState) : bool =
        not (Map.containsKey digest state.Sessions)
        && not (Map.containsKey (TombstoneKey.Credential digest) state.Tombstones)

    let private consume (pairing: LivePairing) (session: LiveSession)
        (state: AuthorityState) : AuthorityState =
        { state with Pairings = state.Pairings |> Map.remove pairing.View.PairingId
                     Sessions = state.Sessions |> Map.add session.Digest session }
        |> StateModel.remember state.LastElapsed (TombstoneKey.Pairing pairing.View.PairingId)
            AccessError.PairingConsumed (Some pairing.View.UserCode)

    let private pending (pairing: LivePairing) (state: AuthorityState) : Result<Outcome, AccessError> =
        result {
            do! require AccessError.AccessRateLimited
                    (pairing.NextPoll |> Option.forall (fun next -> next <= state.LastElapsed))
            let updated: LivePairing = { pairing with NextPoll = Some(state.LastElapsed + StateModel.pollInterval) }
            let next: AuthorityState = { state with Pairings = state.Pairings |> Map.add pairing.View.PairingId updated }
            return StateModel.accept (Response.ExchangePending pairing.View.ExpiresAt) next
        }

    let private issue (evidence: Evidence) (capabilities: Set<Capability>)
        (pairing: LivePairing) (state: AuthorityState) : Result<Outcome, AccessError> =
        result {
            do! require AccessError.AccessCapacityReached (Map.count state.Sessions < 128)
            let credential = SessionCredential.create state.Identity.InstanceId evidence.SessionSecret
            let digest = CredentialDigest.create credential
            do! require AccessError.AccessUnavailable (unused digest state)
            let session: LiveSession =
                { Digest = digest; BrowserOrigin = pairing.View.BrowserOrigin; Capabilities = capabilities
                  Deadline = state.LastElapsed + StateModel.sessionLife; ExpiresAt = evidence.Now.Utc + StateModel.sessionLife }
            let next: AuthorityState = consume pairing session state
            let issued: IssuedSession =
                { InstanceId = state.Identity.InstanceId; Credential = credential
                  Capabilities = capabilities; ExpiresAt = session.ExpiresAt }
            return StateModel.accept (Response.SessionIssued issued) next
        }

    let private invalid (error: AccessError) (state: AuthorityState) : Outcome =
        if List.length state.InvalidExchanges >= 120 then
            StateModel.reject AccessError.AccessRateLimited state
        else
            { state with InvalidExchanges = state.LastElapsed :: state.InvalidExchanges }
            |> StateModel.reject error

    let reject (reason: InvalidExchange) (state: AuthorityState) : Outcome =
        let error =
            match reason with
            | InvalidExchange.MalformedRequest -> AccessError.InvalidRequest
            | InvalidExchange.NoncanonicalProof -> AccessError.InvalidPairingProof
        invalid error state

    let private countRejected (outcome: Outcome) : Outcome =
        outcome.Result |> Result.map (fun _ -> outcome) |> Result.defaultWith (fun error ->
            match error with
            | AccessError.AccessRateLimited | AccessError.AccessCapacityReached
            | AccessError.AccessUnavailable | AccessError.InternalError -> outcome
            | _ -> invalid error outcome.State)

    let exchange (evidence: Evidence) (request: ExchangeRequest) (state: AuthorityState) : Outcome =
        result {
            do! require AccessError.WrongInstance (StateModel.sameInstance request.InstanceId state)
            let! pairing = StateModel.findPairing request.PairingId state
            do! require AccessError.SessionOriginMismatch (request.BrowserOrigin = Some pairing.View.BrowserOrigin)
            do! require AccessError.InvalidPairingProof (Challenge.verifies pairing.View.Challenge request.Verifier)
            return!
                pairing.Grant |> Option.map (fun grant -> issue evidence grant pairing state)
                |> Option.defaultWith (fun () -> pending pairing state)
        } |> StateModel.finish state |> countRejected

[<RequireQualifiedAccess>]
module Domain =
    let create (identity: Identity) (policy: Set<Capability>)
        (extraOrigins: Set<BrowserOrigin>) : Result<AuthorityState, AccessError> =
        Identity.validate identity |> Result.map (fun (valid: Identity) ->
            let initial: AuthorityState =
                { Identity = valid; Policy = policy; Phase = AccessPhase.Starting
                  AllowedOrigins = Set.union extraOrigins (ApiAudience.browserOrigins identity.ApiOrigin)
                  Pairings = Map.empty; Sessions = Map.empty; Tombstones = Map.empty
                  Creations = []; InvalidExchanges = []; LastElapsed = TimeSpan.Zero }
            initial)

    let counts (state: AuthorityState) : StateCounts =
        { Phase = state.Phase; Pairings = Map.count state.Pairings; Sessions = Map.count state.Sessions
          Tombstones = Map.count state.Tombstones; Creations = List.length state.Creations
          InvalidExchanges = List.length state.InvalidExchanges; HighWater = state.LastElapsed }

    let private ready (state: AuthorityState) : Outcome =
        if state.Phase = AccessPhase.Stopping then StateModel.reject AccessError.AccessUnavailable state
        else StateModel.accept (Response.PhaseChanged AccessPhase.Ready) { state with Phase = AccessPhase.Ready }

    let private controlStatus (expected: InstanceId option) (state: AuthorityState) : Outcome =
        if expected |> Option.forall (fun instance -> StateModel.sameInstance instance state) then
            StateModel.accept (Response.ControlStatus(state.Identity, state.Phase, state.Policy)) state
        else StateModel.reject AccessError.WrongInstance state

    let private dispatch (evidence: Evidence) (command: Command) (state: AuthorityState) : Outcome =
        match command with
        | Command.EnterReady -> ready state
        | Command.Stop -> StateModel.clear state |> StateModel.accept (Response.PhaseChanged AccessPhase.Stopping)
        | Command.ControlStatus expected -> controlStatus expected state
        | Command.Resolve _ | Command.Approve _ when state.Phase <> AccessPhase.Ready ->
            StateModel.reject AccessError.InstanceNotReady state
        | _ when state.Phase <> AccessPhase.Ready -> StateModel.reject AccessError.AccessUnavailable state
        | Command.Create request -> PairingOperations.create evidence request state
        | Command.Resolve(instance, code) -> PairingOperations.resolve instance code state
        | Command.Approve request -> PairingOperations.approve request state
        | Command.Exchange request -> ExchangeOperations.exchange evidence request state
        | Command.RejectExchange reason -> ExchangeOperations.reject reason state
        | Command.Status context -> SessionOperations.getStatus context state
        | Command.Revoke context -> SessionOperations.revoke context state
        | Command.Admit(required, context) -> SessionOperations.admit required context state

    /// Commit Next on either branch. In particular, rejected exchanges update
    /// their bounded rate window. This function performs no external effects.
    let transition (evidence: Evidence) (command: Command) (state: AuthorityState) : Result<Transition, Rejection> =
        let prepared: AuthorityState = StateModel.prepare evidence state
        let outcome: Outcome = dispatch evidence command prepared
        let accepted (response: Response) : Transition = { Response = response; Next = outcome.State }
        let rejected (error: AccessError) : Rejection = { Error = error; Next = outcome.State }
        outcome.Result
        |> Result.map accepted
        |> Result.mapError rejected
