module Hermes.Tests.LocalAccessBoundaryTests

open System
open Xunit
open Hermes.Core.LocalAccess
open Hermes.Tests.LocalAccessDomainTests

let private ready () = Fixture.ready Fixture.all Set.empty
let private pair () = Fixture.pairAt 0 Fixture.all [ Fixture.origin ] (ready ())

[<Fact>]
let ``resolve is pinned and discloses only the immutable pairing`` () =
    let paired = pair ()
    let code = paired.Receipt.UserCode
    paired.Rig |> Fixture.run 0 (Command.Resolve(Fixture.foreign, code)) |> Fixture.error AccessError.WrongInstance
    match paired.Rig |> Fixture.run 0 (Command.Resolve(Fixture.instance, code)) |> Fixture.response with
    | Response.PairingResolved view ->
        Assert.True(Challenge.equal Fixture.challenge view.Challenge)
        Assert.Equal(Fixture.origin, view.BrowserOrigin)
        Assert.Equal(Fixture.audience, view.ApiOrigin)
        Assert.Equal(PairingPhase.Pending, view.State)
    | _ -> failwith "Expected resolved pairing."

[<Fact>]
let ``approval requires exact code challenge and instance without broadening`` () =
    let paired = pair ()
    let request = Fixture.approval (Set.singleton Capability.Mutate) paired
    let wrongCode = DisplayCode.ofBytes (Array.create 12 31uy) |> Fixture.ok
    let wrongProof = Verifier.ofBytes (Array.create 32 1uy) |> Fixture.ok |> Challenge.ofVerifier
    [ { request with InstanceId = Fixture.foreign }, AccessError.WrongInstance
      { request with UserCode = wrongCode }, AccessError.PairingConflict
      { request with Challenge = wrongProof }, AccessError.PairingConflict ]
    |> List.iter (fun (input, error) ->
        paired.Rig |> Fixture.run 0 (Command.Approve input) |> Fixture.error error)
    let approved = Fixture.approveAt 10 request.Capabilities paired
    approved.Rig |> Fixture.run 20 (Command.Approve(Fixture.approval Fixture.all approved))
    |> Fixture.error AccessError.PairingConflict

[<Fact>]
let ``server policy and requested capabilities both constrain approval`` () =
    let policy = Set.singleton Capability.ConfigRead
    let requested = Fixture.pairAt 0 Fixture.all [ Fixture.origin ] (Fixture.ready policy Set.empty)
    requested.Rig |> Fixture.run 0 (Command.Approve(Fixture.approval Fixture.all requested))
    |> Fixture.error AccessError.InsufficientCapability
    let narrow = Fixture.pairAt 0 policy [ Fixture.origin ] (ready ())
    narrow.Rig |> Fixture.run 0 (Command.Approve(Fixture.approval Fixture.all narrow))
    |> Fixture.error AccessError.InsufficientCapability

[<Theory>]
[<InlineData(10, 70)>]
[<InlineData(170, 180)>]
let ``approval shortens expiry and identical reapproval cannot extend it`` approvedAt expiresAt =
    let approved = pair () |> Fixture.approveAt approvedAt Fixture.all
    let repeated = Fixture.run (expiresAt - 1) (Command.Approve(Fixture.approval Fixture.all approved)) approved.Rig
    match Fixture.response repeated with
    | Response.PairingApproved view -> Assert.Equal(Fixture.epoch.AddSeconds(float expiresAt), view.ExpiresAt)
    | _ -> failwith "Expected approval."
    { approved with Rig = repeated.Rig } |> Fixture.exchangeAt expiresAt (Some Fixture.origin) Fixture.verifier
    |> Fixture.error AccessError.PairingExpired

[<Theory>]
[<InlineData(179, true)>]
[<InlineData(180, false)>]
let ``pending lifetime is exactly one hundred eighty monotonic seconds`` seconds live =
    let paired = pair ()
    let attempt = paired.Rig |> Fixture.run seconds (Command.Resolve(Fixture.instance, paired.Receipt.UserCode))
    Assert.Equal(live, attempt.Outcome |> Result.isOk)
    if not live then Fixture.error AccessError.PairingExpired attempt

[<Fact>]
let ``valid pending polls are limited without consuming invalid attempt budget`` () =
    let paired = pair ()
    let first = paired |> Fixture.exchangeAt 0 (Some Fixture.origin) Fixture.verifier
    Assert.True(first.Outcome |> Result.isOk)
    let denied = { paired with Rig = first.Rig } |> Fixture.exchangeAt 1 (Some Fixture.origin) Fixture.verifier
    Fixture.error AccessError.AccessRateLimited denied
    Assert.Equal(0, (Domain.counts denied.Rig.State).InvalidExchanges)
    { paired with Rig = denied.Rig } |> Fixture.exchangeAt 2 (Some Fixture.origin) Fixture.verifier
    |> Fixture.response |> ignore

[<Fact>]
let ``invalid exchanges are bounded and rejected state must be retained`` () =
    let denied =
        [ 1 .. 120 ] |> List.fold (fun rig _ ->
            let attempt = Fixture.run 0 (Command.RejectExchange InvalidExchange.NoncanonicalProof) rig
            Fixture.error AccessError.InvalidPairingProof attempt
            attempt.Rig) (ready ())
    let limited = Fixture.run 59 (Command.RejectExchange InvalidExchange.MalformedRequest) denied
    Fixture.error AccessError.AccessRateLimited limited
    Assert.Equal(120, (Domain.counts limited.Rig.State).InvalidExchanges)
    let renewed = Fixture.run 60 (Command.RejectExchange InvalidExchange.MalformedRequest) limited.Rig
    Fixture.error AccessError.InvalidRequest renewed
    Assert.Equal(1, (Domain.counts renewed.Rig.State).InvalidExchanges)

[<Fact>]
let ``exchange pins U not A and requires the verifier even after approval`` () =
    let preview = Fixture.pairAt 0 Fixture.all [ Fixture.preview ] (Fixture.ready Fixture.all (Set.singleton Fixture.preview))
    let approved = Fixture.approveAt 0 Fixture.all preview
    [ None; Some Fixture.origin ] |> List.iter (fun origin ->
        approved |> Fixture.exchangeAt 0 origin Fixture.verifier |> Fixture.error AccessError.SessionOriginMismatch)
    let incorrect = Verifier.ofBytes (Array.create 32 1uy) |> Fixture.ok
    approved |> Fixture.exchangeAt 0 (Some Fixture.preview) incorrect |> Fixture.error AccessError.InvalidPairingProof
    approved |> Fixture.exchangeAt 0 (Some Fixture.preview) Fixture.verifier |> Fixture.response |> ignore

[<Fact>]
let ``successful exchange consumes approval once including when reply is lost`` () =
    let approved = pair () |> Fixture.approveAt 0 Fixture.all
    let first = approved |> Fixture.exchangeAt 0 (Some Fixture.origin) Fixture.verifier
    let committed = { approved with Rig = first.Rig }
    committed |> Fixture.exchangeAt 0 (Some Fixture.origin) Fixture.verifier |> Fixture.error AccessError.PairingConsumed
    committed.Rig |> Fixture.run 0 (Command.Resolve(Fixture.instance, approved.Receipt.UserCode))
    |> Fixture.error AccessError.PairingConsumed
    committed.Rig |> Fixture.run 0 (Command.Approve(Fixture.approval Fixture.all committed))
    |> Fixture.error AccessError.PairingConsumed
    Assert.Equal(1, (Domain.counts committed.Rig.State).Sessions)

[<Fact>]
let ``clock rollback polling and status cannot extend pairings or sessions`` () =
    let paired = pair () |> Fixture.approveAt 100 Fixture.all
    let repeated = Fixture.at 50 -1000 (Command.Approve(Fixture.approval Fixture.all paired)) paired.Rig
    Assert.Equal(TimeSpan.FromSeconds 100.0, (Domain.counts repeated.Rig.State).HighWater)
    { paired with Rig = repeated.Rig } |> Fixture.exchangeAt 160 (Some Fixture.origin) Fixture.verifier
    |> Fixture.error AccessError.PairingExpired
    let issued = Fixture.issueAt 0 (ready ())
    let context = Fixture.supplied issued.Session None
    let polled = Fixture.at 1799 -1000 (Command.Status context) issued.Rig
    Fixture.response polled |> ignore
    let expired = Fixture.at 1800 -2000 (Command.Status context) polled.Rig
    Fixture.error AccessError.CredentialExpired expired

[<Fact>]
let ``session status admission and revoke preserve scoped origin bound authority`` () =
    let approved = pair () |> Fixture.approveAt 0 (Set.singleton Capability.ConfigRead)
    let exchanged = approved |> Fixture.exchangeAt 0 (Some Fixture.origin) Fixture.verifier
    let session =
        match Fixture.response exchanged with
        | Response.SessionIssued session -> session
        | _ -> failwith "Expected session."
    let context = Fixture.supplied session None
    exchanged.Rig |> Fixture.run 0 (Command.Admit(Set.singleton Capability.ModelInvoke, context))
    |> Fixture.error AccessError.InsufficientCapability
    exchanged.Rig |> Fixture.run 0 (Command.Admit(Set.singleton Capability.ConfigRead, context))
    |> Fixture.response |> ignore
    let wrongOrigin = Fixture.supplied session (Some Fixture.preview)
    exchanged.Rig |> Fixture.run 0 (Command.Status wrongOrigin) |> Fixture.error AccessError.SessionOriginMismatch
    let revoked = exchanged.Rig |> Fixture.run 0 (Command.Revoke context)
    Fixture.response revoked |> ignore
    revoked.Rig |> Fixture.run 0 (Command.Revoke context) |> Fixture.error AccessError.InvalidCredential
    revoked.Rig |> Fixture.run 0 (Command.Admit(Set.empty, context)) |> Fixture.error AccessError.InvalidCredential

[<Fact>]
let ``one hundred twenty eight sessions are retained and exhaustion does not consume approval`` () =
    let full =
        [ 0 .. 127 ] |> List.fold (fun rig index ->
            (Fixture.issueAt ((index / 30) * 60) rig).Rig) (ready ())
    Assert.Equal(128, (Domain.counts full.State).Sessions)
    let approved = Fixture.pairAt 240 Fixture.all [ Fixture.origin ] full |> Fixture.approveAt 240 Fixture.all
    let denied = approved |> Fixture.exchangeAt 240 (Some Fixture.origin) Fixture.verifier
    Fixture.error AccessError.AccessCapacityReached denied
    Assert.Equal(1, (Domain.counts denied.Rig.State).Pairings)
    Assert.Equal(128, (Domain.counts denied.Rig.State).Sessions)

[<Fact>]
let ``tombstones are capped at two hundred fifty six and expire without evicting sessions`` () =
    let issued = Fixture.issueAt 0 (ready ())
    let bounded =
        [ 1 .. 300 ] |> List.fold (fun state index ->
            let evidence = Fixture.evidence 0 0 (1000 + index)
            StateModel.remember (TimeSpan.FromMilliseconds(float index))
                (TombstoneKey.Pairing evidence.PairingId) AccessError.PairingConsumed
                (Some evidence.UserCode) state) issued.Rig.State
    Assert.Equal(256, (Domain.counts bounded).Tombstones)
    Assert.Equal(1, (Domain.counts bounded).Sessions)
    let pruned = Fixture.run 181 (Command.ControlStatus None) { issued.Rig with State = bounded }
    Assert.Equal(0, (Domain.counts pruned.Rig.State).Tombstones)
    Assert.Equal(1, (Domain.counts pruned.Rig.State).Sessions)

[<Fact>]
let ``restart stopping stale instance and strict credentials cannot revive authority`` () =
    let issued = Fixture.issueAt 0 (ready ())
    let text = SessionCredential.text issued.Session.Credential
    Assert.Equal(69, text.Length)
    Assert.True(SessionCredential.tryParse text |> Result.isOk)
    [ text + " "; " " + text; text.Replace("h1.", "H1."); text + "="; "Bearer " + text ]
    |> List.iter (fun value -> Assert.True(SessionCredential.tryParse value |> Result.isError))
    let stopped = Fixture.run 1 Command.Stop issued.Rig
    Assert.Equal(0, (Domain.counts stopped.Rig.State).Sessions)
    Assert.Equal(0, (Domain.counts stopped.Rig.State).Pairings)
    let replacement = { Fixture.identity with InstanceId = Fixture.foreign }
    let state = Domain.create replacement Fixture.all Set.empty |> Fixture.ok
    let restarted = Fixture.run 0 Command.EnterReady { State = state; Index = 10000 }
    restarted.Rig |> Fixture.run 1 (Command.Status(Fixture.supplied issued.Session None))
    |> Fixture.error AccessError.WrongInstance
    Assert.DoesNotContain(text, string issued.Rig.State)
    Assert.DoesNotContain(text, sprintf "%A" issued.Rig.State)
    Assert.DoesNotContain(text, sprintf "%A" issued.Session)

module private SessionCollisionFixture =
    [<NoEquality; NoComparison>]
    type Original =
        { Rig: Fixture.Rig
          Session: IssuedSession
          Secret: SessionSecret }

    [<NoEquality; NoComparison>]
    type Retired =
        { Original: Original
          Rig: Fixture.Rig
          At: int
          Error: AccessError }

    let issued (attempt: Fixture.Attempt) : IssuedSession =
        match Fixture.response attempt with
        | Response.SessionIssued session -> session
        | _ -> failwith "Expected issued session."

    let original () : Original =
        let narrow = Set.singleton Capability.ConfigRead
        let rig = Fixture.ready Fixture.all (Set.singleton Fixture.preview)
        let paired = Fixture.pairAt 0 narrow [ Fixture.origin ] rig |> Fixture.approveAt 0 narrow
        let secret = (Fixture.evidence 0 0 paired.Rig.Index).SessionSecret
        let attempt = Fixture.exchangeAt 0 (Some Fixture.origin) Fixture.verifier paired
        { Rig = attempt.Rig; Session = issued attempt; Secret = secret }

    let revokeAt (seconds: int) (session: IssuedSession) (rig: Fixture.Rig) : Fixture.Rig =
        let attempt = Fixture.run seconds (Command.Revoke(Fixture.supplied session None)) rig
        match Fixture.response attempt with
        | Response.Revoked -> attempt.Rig
        | _ -> failwith "Expected revocation."

    let retire (revoked: bool) : Retired =
        let old = original ()
        if revoked then
            { Original = old; Rig = revokeAt 0 old.Session old.Rig
              At = 0; Error = AccessError.InvalidCredential }
        else
            let expired = Fixture.run 1800 (Command.Status(Fixture.supplied old.Session None)) old.Rig
            Fixture.error AccessError.CredentialExpired expired
            { Original = old; Rig = expired.Rig; At = 1800; Error = AccessError.CredentialExpired }

    let approved (seconds: int) (rig: Fixture.Rig) : Fixture.Pair =
        Fixture.pairAt seconds Fixture.all [ Fixture.preview ] rig
        |> Fixture.approveAt seconds Fixture.all

    let exchangeWith (secret: SessionSecret) (seconds: int) (paired: Fixture.Pair) : Fixture.Attempt =
        let sampled: Evidence = Fixture.evidence seconds seconds paired.Rig.Index
        let evidence: Evidence = { sampled with SessionSecret = secret }
        let command = Command.Exchange(Fixture.exchangeRequest (Some Fixture.preview) Fixture.verifier paired)
        let decision: Result<Transition, Rejection> = Domain.transition evidence command paired.Rig.State
        let state: AuthorityState =
            decision |> Result.map (fun (value: Transition) -> value.Next)
            |> Result.defaultWith (fun (error: Rejection) -> error.Next)
        { Rig = { State = state; Index = paired.Rig.Index + 1 }
          Outcome = decision |> Result.map (fun value -> value.Response)
                    |> Result.mapError (fun error -> error.Error) }

    let unchanged (expected: AuthorityState) (actual: AuthorityState) : unit =
        Assert.Equal<StateCounts>(Domain.counts expected, Domain.counts actual)
        Assert.Same(box expected.Pairings, box actual.Pairings)
        Assert.Same(box expected.Sessions, box actual.Sessions)
        Assert.Equal<(TombstoneKey * Tombstone) list>(
            Map.toList expected.Tombstones, Map.toList actual.Tombstones)
        Assert.Equal<TimeSpan list>(expected.Creations, actual.Creations)
        Assert.Equal<TimeSpan list>(expected.InvalidExchanges, actual.InvalidExchanges)

    let freshAfter (retired: Retired) (paired: Fixture.Pair) : Fixture.Rig =
        let attempt = Fixture.exchangeAt retired.At (Some Fixture.preview) Fixture.verifier paired
        let session = issued attempt
        Assert.Equal<Set<Capability>>(Fixture.all, session.Capabilities)
        Assert.Equal(Fixture.epoch.AddSeconds(float (retired.At + 1800)), session.ExpiresAt)
        let context = Fixture.supplied session (Some Fixture.preview)
        let admitted = Fixture.run retired.At (Command.Admit(Fixture.all, context)) attempt.Rig
        match Fixture.response admitted with
        | Response.Admitted status ->
            Assert.Equal(AuthenticationState.Authenticated, status.State)
            Assert.Equal<Set<EffectiveCapability>>(EffectiveCapability.ofGrant Fixture.all, status.Capabilities)
            Assert.Equal<DateTimeOffset option>(Some session.ExpiresAt, status.ExpiresAt)
        | _ -> failwith "Expected admitted fresh session."
        let oldContext = Fixture.supplied retired.Original.Session None
        let denied = Fixture.run retired.At (Command.Admit(Fixture.all, oldContext)) admitted.Rig
        Fixture.error retired.Error denied
        denied.Rig

    let fillRecent (rig: Fixture.Rig) : Fixture.Rig =
        let state: AuthorityState =
            [ 1 .. 256 ] |> List.fold (fun (state: AuthorityState) index ->
                let evidence = Fixture.evidence 0 0 (1000 + index)
                StateModel.remember TimeSpan.Zero (TombstoneKey.Pairing evidence.PairingId)
                    AccessError.PairingConsumed (Some evidence.UserCode) state) rig.State
        { rig with State = state }

    let controlAt (seconds: int) (rig: Fixture.Rig) : Fixture.Rig =
        let attempt = Fixture.run seconds (Command.ControlStatus None) rig
        match Fixture.response attempt with
        | Response.ControlStatus(_, AccessPhase.Ready, _) -> attempt.Rig
        | _ -> failwith "Expected ready control status."

module SessionCollisionTests =
    open SessionCollisionFixture

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``recent expired and revoked credentials cannot be reissued`` (revoked: bool) =
        let retired = retire revoked
        let counted = Fixture.run retired.At (Command.RejectExchange InvalidExchange.MalformedRequest) retired.Rig
        Fixture.error AccessError.InvalidRequest counted
        let paired = approved retired.At counted.Rig
        let denied = exchangeWith retired.Original.Secret retired.At paired
        Fixture.error AccessError.AccessUnavailable denied
        unchanged paired.Rig.State denied.Rig.State
        let oldContext = Fixture.supplied retired.Original.Session None
        let stillDenied = Fixture.run retired.At (Command.Status oldContext) denied.Rig
        Fixture.error retired.Error stillDenied
        let recovered = freshAfter retired { paired with Rig = stillDenied.Rig }
        Assert.Equal(0, (Domain.counts recovered.State).Pairings)
        Assert.Equal(1, (Domain.counts recovered.State).Sessions)

    [<Fact>]
    let ``live credential collision preserves the original narrow session`` () =
        let old = original ()
        let paired = approved 0 old.Rig
        let denied = exchangeWith old.Secret 0 paired
        Fixture.error AccessError.AccessUnavailable denied
        unchanged paired.Rig.State denied.Rig.State
        let narrow = Set.singleton Capability.ConfigRead
        let context = Fixture.supplied old.Session None
        let admitted = Fixture.run 0 (Command.Admit(narrow, context)) denied.Rig
        match Fixture.response admitted with
        | Response.Admitted status ->
            Assert.Equal<Set<EffectiveCapability>>(EffectiveCapability.ofGrant narrow, status.Capabilities)
        | _ -> failwith "Expected original narrow session."
        let broad = Fixture.run 0 (Command.Admit(Fixture.all, context)) admitted.Rig
        Fixture.error AccessError.InsufficientCapability broad

    [<Fact>]
    let ``collision rejection retains expiry pruning performed by that transition`` () =
        let old = original ()
        let paired = approved 1799 old.Rig
        let denied = exchangeWith old.Secret 1800 paired
        Fixture.error AccessError.AccessUnavailable denied
        Assert.Equal(0, (Domain.counts denied.Rig.State).Sessions)
        Assert.Equal(1, (Domain.counts denied.Rig.State).Pairings)
        Assert.Equal(TimeSpan.FromSeconds 1800.0, (Domain.counts denied.Rig.State).HighWater)
        Assert.Same(box paired.Rig.State.Pairings, box denied.Rig.State.Pairings)
        let key = TombstoneKey.Credential(CredentialDigest.create old.Session.Credential)
        let entry: Tombstone = denied.Rig.State.Tombstones |> Map.find key
        Assert.Equal(AccessError.CredentialExpired, entry.Error)
        Assert.Equal(TimeSpan.FromSeconds 1980.0, entry.Until)
        let retired: Retired =
            { Original = old; Rig = denied.Rig; At = 1800; Error = AccessError.CredentialExpired }
        freshAfter retired { paired with Rig = denied.Rig } |> ignore

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``credential tombstone retention ends exactly after one hundred eighty seconds`` (revoked: bool) =
        let retired = retire revoked
        let context = Fixture.supplied retired.Original.Session None
        let key = TombstoneKey.Credential(CredentialDigest.create retired.Original.Session.Credential)
        let before = Fixture.run (retired.At + 179) (Command.Status context) retired.Rig
        Fixture.error retired.Error before
        let entry: Tombstone = before.Rig.State.Tombstones |> Map.find key
        Assert.Equal(TimeSpan.FromSeconds(float (retired.At + 180)), entry.Until)
        let after = Fixture.run (retired.At + 180) (Command.Status context) before.Rig
        Fixture.error AccessError.InvalidCredential after
        Assert.False(Map.containsKey key after.Rig.State.Tombstones)
        Assert.Equal(0, (Domain.counts after.Rig.State).Tombstones)

    [<Fact>]
    let ``revocation shares the exact tombstone cap without evicting live sessions`` () =
        let old = original ()
        let survivor = Fixture.issueAt 0 old.Rig
        let full = fillRecent survivor.Rig
        Assert.Equal(256, (Domain.counts full.State).Tombstones)
        let revoked = revokeAt 1 old.Session full
        Assert.Equal(256, (Domain.counts revoked.State).Tombstones)
        Assert.Equal(1, (Domain.counts revoked.State).Sessions)
        let key = TombstoneKey.Credential(CredentialDigest.create old.Session.Credential)
        Assert.True(Map.containsKey key revoked.State.Tombstones)
        let pruned = controlAt 180 revoked
        Assert.Equal(1, (Domain.counts pruned.State).Tombstones)
        let gone = controlAt 181 pruned
        Assert.Equal(0, (Domain.counts gone.State).Tombstones)
        gone |> Fixture.run 181 (Command.Admit(Fixture.all, Fixture.supplied survivor.Session None))
        |> Fixture.response |> ignore
