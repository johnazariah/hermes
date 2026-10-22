module Hermes.Tests.LocalAccessDomainTests

open System
open System.Security.Cryptography
open System.Text
open FsCheck.Xunit
open Xunit
open Hermes.Core.LocalAccess

module internal Fixture =
    let ok (result: Result<'value, 'error>) : 'value = result |> Result.defaultWith (fun error -> failwithf "Unexpected rejection: %A" error)
    let epoch = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let all = Set.ofList [ Capability.ConfigRead; Capability.ModelInvoke; Capability.Mutate ]
    let origin = BrowserOrigin.tryParse "http://127.0.0.1:21741" |> ok
    let preview = BrowserOrigin.tryParse "http://localhost:3000" |> ok
    let verifier = Verifier.tryParse "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk" |> ok
    let challenge = Challenge.ofVerifier verifier
    let instance = InstanceId.ofBytes (Array.zeroCreate 16) |> ok
    let foreign = InstanceId.ofBytes (Array.create 16 1uy) |> ok
    let audience = ApiAudience.create 21741 |> ok
    let identity = Identity.create instance 42 epoch audience (String.replicate 64 "0") |> ok
    type Rig = { State: AuthorityState; Index: int }
    type Attempt = { Rig: Rig; Outcome: Result<Response, AccessError> }
    type Pair = { Rig: Rig; Receipt: PairingReceipt }
    type Issued = { Rig: Rig; Session: IssuedSession; Receipt: PairingReceipt }
    let entropy (index: int) : Entropy =
        { Bytes = fun size ->
            Encoding.ASCII.GetBytes($"{index}/{size}") |> SHA256.HashData |> Array.take size }
    let evidence (seconds: int) (utcSeconds: int) (index: int) : Evidence =
        let clock: Clock = { Read = fun () -> { Elapsed = TimeSpan.FromSeconds(float seconds); Utc = epoch.AddSeconds(float utcSeconds) } }
        Evidence.capture clock (entropy index) |> ok
    let at (seconds: int) (utcSeconds: int) (command: Command) (rig: Rig) : Attempt =
        let decision: Result<Transition, Rejection> = Domain.transition (evidence seconds utcSeconds rig.Index) command rig.State
        let state: AuthorityState =
            decision |> Result.map (fun (value: Transition) -> value.Next)
            |> Result.defaultWith (fun (error: Rejection) -> error.Next)
        { Rig = { State = state; Index = rig.Index + 1 }
          Outcome = decision |> Result.map (fun value -> value.Response) |> Result.mapError (fun error -> error.Error) }
    let run (seconds: int) (command: Command) (rig: Rig) : Attempt = at seconds seconds command rig
    let response (attempt: Attempt) : Response = attempt.Outcome |> ok
    let error (expected: AccessError) (attempt: Attempt) : unit =
        attempt.Outcome |> Result.map (fun _ -> failwith "Expected rejection.")
        |> Result.defaultWith (fun actual -> Assert.Equal<AccessError>(expected, actual))
    let initial (policy: Set<Capability>) (extras: Set<BrowserOrigin>) : Rig = { State = Domain.create identity policy extras |> ok; Index = 1 }
    let ready (policy: Set<Capability>) (extras: Set<BrowserOrigin>) : Rig = initial policy extras |> run 0 Command.EnterReady |> fun attempt -> attempt.Rig
    let context (credential: CredentialInput) (browserOrigin: BrowserOrigin option) : SessionContext = { Credential = credential; BrowserOrigin = browserOrigin }
    let anonymous = context CredentialInput.Absent None
    let creation (requested: Set<Capability>) (origins: BrowserOrigin list) : CreateRequest =
        { InstanceId = instance; Challenge = challenge; RequestedCapabilities = requested; BrowserOrigins = origins }
    let pairAt (seconds: int) (requested: Set<Capability>) (origins: BrowserOrigin list) (rig: Rig) : Pair =
        let attempt = run seconds (Command.Create(creation requested origins)) rig
        match response attempt with
        | Response.PairingCreated receipt -> { Pair.Rig = attempt.Rig; Receipt = receipt }
        | _ -> failwith "Expected pairing receipt."
    let approval (capabilities: Set<Capability>) (pair: Pair) : ApprovalRequest =
        { InstanceId = instance; PairingId = pair.Receipt.PairingId; UserCode = pair.Receipt.UserCode
          Challenge = challenge; Capabilities = capabilities }
    let approveAt (seconds: int) (capabilities: Set<Capability>) (pair: Pair) : Pair =
        let attempt = run seconds (Command.Approve(approval capabilities pair)) pair.Rig
        response attempt |> ignore
        { pair with Rig = attempt.Rig }
    let exchangeRequest (browserOrigin: BrowserOrigin option) (proof: Verifier) (pair: Pair) : ExchangeRequest =
        { InstanceId = instance; PairingId = pair.Receipt.PairingId; Verifier = proof; BrowserOrigin = browserOrigin }
    let exchangeAt (seconds: int) (browserOrigin: BrowserOrigin option) (proof: Verifier) (pair: Pair) : Attempt =
        run seconds (Command.Exchange(exchangeRequest browserOrigin proof pair)) pair.Rig
    let issueAt (seconds: int) (rig: Rig) : Issued =
        let pair = pairAt seconds all [ origin ] rig |> approveAt seconds all
        let attempt = exchangeAt seconds (Some origin) verifier pair
        match response attempt with
        | Response.SessionIssued session -> { Issued.Rig = attempt.Rig; Session = session; Receipt = pair.Receipt }
        | _ -> failwith "Expected issued session."
    let supplied (session: IssuedSession) (origin: BrowserOrigin option) : SessionContext = context (CredentialInput.Present session.Credential) origin

[<Property(MaxTest = 100)>]
let ``canonical verifier round trips arbitrary SHA256 bytes`` (values: byte list) =
    let proof = values |> List.toArray |> SHA256.HashData |> Verifier.ofBytes |> Fixture.ok
    let parsed = Verifier.text proof |> Verifier.tryParse |> Fixture.ok
    Challenge.verifies (Challenge.ofVerifier proof) parsed

[<Fact>]
let ``S256 uses verifier UTF8 text and the RFC7636 public vector`` () =
    Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Challenge.text Fixture.challenge)
    Assert.Equal("<redacted access value>", string Fixture.verifier)
    Assert.Equal("<redacted access value>", sprintf "%A" Fixture.challenge)

[<Theory>]
[<InlineData("")>]
[<InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")>]
[<InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")>]
[<InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")>]
[<InlineData(" AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")>]
let ``noncanonical verifier text is rejected`` (value: string) =
    Assert.True(Verifier.tryParse value |> Result.isError)

[<Fact>]
let ``identifier code credential and capability models have exact representations`` () =
    Assert.Equal(String.replicate 22 "A", InstanceId.text Fixture.instance)
    Assert.True(InstanceId.tryParse (String.replicate 21 "A" + "B") |> Result.isError)
    Assert.True(PairingId.tryParse (String.replicate 22 "A") |> Result.isOk)
    Assert.True(PairingId.ofBytes [||] |> Result.isError)
    Assert.True(Verifier.ofBytes [||] |> Result.isError)
    Assert.True(Challenge.tryParse null |> Result.isError)
    Assert.Equal<string list>([ "config.read"; "model.invoke"; "mutate" ], Fixture.all |> Set.toList |> List.map Capability.name)
    let code = DisplayCode.ofBytes (Array.zeroCreate 12) |> Fixture.ok
    Assert.Equal("0000-0000-0000", DisplayCode.text code)
    Assert.True(DisplayCode.tryParse (DisplayCode.text code) |> Result.isOk)
    Assert.True(DisplayCode.tryParse "OOOO-OOOO-OOOO" |> Result.isError)
    Assert.True(DisplayCode.ofBytes [||] |> Result.isError)

[<Theory>]
[<InlineData("http://localhost:21741", true)>]
[<InlineData("http://[::1]:21741", true)>]
[<InlineData("https://localhost:3000", true)>]
[<InlineData("http://localhost", true)>]
[<InlineData("http://localhost:80", false)>]
[<InlineData("http://127.0.0.2:21741", false)>]
[<InlineData("http://localhost:21741/", false)>]
[<InlineData("http://name@localhost:21741", false)>]
[<InlineData("http://localhost:21741/path", false)>]
[<InlineData("http://localhost:21741?x=1", false)>]
[<InlineData("http://example.com:21741", false)>]
[<InlineData("null", false)>]
let ``browser origins preserve canonical actual origins`` (value: string) expected =
    Assert.Equal(expected, BrowserOrigin.tryParse value |> Result.isOk)

[<Fact>]
let ``invalid identity audience clock and entropy cannot initialize evidence`` () =
    Assert.True(ApiAudience.create 0 |> Result.isError)
    Assert.True(ApiAudience.create 65536 |> Result.isError)
    Assert.Equal("http://127.0.0.1:80", ApiAudience.create 80 |> Fixture.ok |> ApiAudience.text)
    Assert.True(Identity.create Fixture.instance 0 Fixture.epoch Fixture.audience (String.replicate 64 "0") |> Result.isError)
    Assert.True(Identity.create Fixture.instance 1 Fixture.epoch Fixture.audience "BAD" |> Result.isError)
    let clock = { Read = fun () -> { Elapsed = TimeSpan.Zero; Utc = Fixture.epoch } }
    Assert.True(Evidence.capture clock { Bytes = fun _ -> [||] } |> Result.isError)
    let rollback = { Read = fun () -> { Elapsed = TimeSpan.FromSeconds -1.0; Utc = Fixture.epoch } }
    Assert.True(Evidence.capture rollback (Fixture.entropy 1) |> Result.isError)

[<Fact>]
let ``starting ready and stopping are admission states not authority bypasses`` () =
    let starting = Fixture.initial Fixture.all Set.empty
    starting |> Fixture.run 0 (Command.Status Fixture.anonymous) |> Fixture.error AccessError.AccessUnavailable
    let status = starting |> Fixture.run 0 (Command.ControlStatus None) |> Fixture.response
    match status with
    | Response.ControlStatus(_, phase, _) -> Assert.Equal(AccessPhase.Starting, phase)
    | _ -> failwith "Expected owner status."
    let ready = starting |> Fixture.run 0 Command.EnterReady |> fun attempt -> attempt.Rig
    let stopped = ready |> Fixture.run 0 Command.Stop |> fun attempt -> attempt.Rig
    stopped |> Fixture.run 0 Command.EnterReady |> Fixture.error AccessError.AccessUnavailable
    Assert.Equal(AccessPhase.Stopping, (Domain.counts stopped.State).Phase)

[<Fact>]
let ``only selected port loopbacks and explicit extras admit pairings`` () =
    let rig = Fixture.ready Fixture.all (Set.singleton Fixture.preview)
    let other = BrowserOrigin.tryParse "http://localhost:21742" |> Fixture.ok
    [ []; [ other ]; [ Fixture.origin; Fixture.origin ] ]
    |> List.iter (fun origins ->
        rig |> Fixture.run 0 (Command.Create(Fixture.creation Fixture.all origins))
        |> Fixture.error AccessError.InvalidRequest)
    let pair = Fixture.pairAt 0 Fixture.all [ Fixture.preview ] rig
    Assert.Equal(Fixture.preview, pair.Receipt.BrowserOrigin)
    Assert.Equal("http://127.0.0.1:21741", ApiAudience.text pair.Receipt.ApiOrigin)

[<Fact>]
let ``creation rate is thirty per rolling minute without unbounded counters`` () =
    let rig =
        [ 1 .. 30 ] |> List.fold (fun rig _ ->
            (Fixture.pairAt 0 Fixture.all [ Fixture.origin ] rig).Rig) (Fixture.ready Fixture.all Set.empty)
    let denied = rig |> Fixture.run 59 (Command.Create(Fixture.creation Fixture.all [ Fixture.origin ]))
    Fixture.error AccessError.AccessRateLimited denied
    Assert.Equal(30, (Domain.counts denied.Rig.State).Creations)
    let next = Fixture.pairAt 60 Fixture.all [ Fixture.origin ] denied.Rig
    Assert.Equal(1, (Domain.counts next.Rig.State).Creations)

[<Fact>]
let ``thirty two live pairings reject capacity without eviction`` () =
    let rig =
        [ 1 .. 32 ] |> List.fold (fun rig index ->
            (Fixture.pairAt (if index <= 30 then 0 else 60) Fixture.all [ Fixture.origin ] rig).Rig)
            (Fixture.ready Fixture.all Set.empty)
    let denied = rig |> Fixture.run 60 (Command.Create(Fixture.creation Fixture.all [ Fixture.origin ]))
    Fixture.error AccessError.AccessCapacityReached denied
    Assert.Equal(32, (Domain.counts denied.Rig.State).Pairings)

[<Fact>]
let ``anonymous status admits archive only and present invalid credentials are errors`` () =
    let rig = Fixture.ready Fixture.all Set.empty
    match rig |> Fixture.run 0 (Command.Status Fixture.anonymous) |> Fixture.response with
    | Response.Status status ->
        Assert.Equal(AuthenticationState.Anonymous, status.State)
        Assert.Equal<string list>([ "archive.read" ], status.Capabilities |> Set.toList |> List.map EffectiveCapability.name)
        Assert.True(Option.isNone status.ExpiresAt)
    | _ -> failwith "Expected status."
    rig |> Fixture.run 0 (Command.Admit(Set.empty, Fixture.anonymous)) |> Fixture.response |> ignore
    rig |> Fixture.run 0 (Command.Admit(Fixture.all, Fixture.anonymous)) |> Fixture.error AccessError.AuthenticationRequired
    let invalid = Fixture.context CredentialInput.Invalid None
    rig |> Fixture.run 0 (Command.Status invalid) |> Fixture.error AccessError.InvalidCredential
