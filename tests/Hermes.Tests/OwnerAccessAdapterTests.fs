module Hermes.Tests.OwnerAccessAdapterTests

open System
open System.Threading.Tasks
open Xunit
open Hermes.Core.LocalAccess
open Hermes.Service
open Hermes.Tests.LocalAccessDomainTests
open Hermes.Tests.LocalAccessHttpFixture

let private request (expected: string option) (operation: Hermes.Core.LocalAccessWire.Operation)
    : Hermes.Core.LocalAccessWire.Request =
    { RequestId = Hermes.Core.LocalAccessWire.Codec.newRequestId()
      ExpectedInstanceId = expected
      Operation = operation }

let private peer (identity: Identity) : Hermes.Core.LocalAccessWire.PeerExpectation =
    { ProcessId = identity.ProcessId; ProcessStartTimeUtc = identity.ProcessStartTimeUtc
      Origin = ApiAudience.text identity.ApiOrigin; ConfigKey = identity.ConfigKey }

let private dispatch (server: Server) (value: Hermes.Core.LocalAccessWire.Request)
    : Task<Result<Hermes.Core.LocalAccessWire.Response, Hermes.Core.LocalAccessWire.CodecFailure>> =
    OwnerAccessAdapter.dispatch server.Identity server.Runtime.Owner value cancellation

[<Fact>]
let ``owner status preserves full identity and correlation through strict codec`` () : Task =
    withServer true true (fun server -> task {
        let value = request None Hermes.Core.LocalAccessWire.Operation.Status
        let! result = dispatch server value
        let response = Fixture.ok result
        let bytes = Hermes.Core.LocalAccessWire.Codec.encodeResponse value response |> Fixture.ok
        let decoded = Hermes.Core.LocalAccessWire.Codec.decodeResponse value (peer server.Identity) bytes |> Fixture.ok
        Assert.Equal(value.RequestId, decoded.RequestId)
        Assert.Equal(server.Identity.ConfigKey, decoded.Instance.ConfigKey)
        Assert.Equal(server.Origin, decoded.Instance.Origin)
        Assert.Equal(server.Identity.ProcessId, decoded.Instance.ProcessId)
        Assert.Equal(server.Identity.ProcessStartTimeUtc, decoded.Instance.ProcessStartTimeUtc)
    })

[<Fact>]
let ``owner resolve serializes the actual immutable challenge without changing safe displays`` () : Task =
    withServer true true (fun server -> task {
        let! pairing = pair server Fixture.all
        let value = request (Some(InstanceId.text server.Identity.InstanceId))
                        (Hermes.Core.LocalAccessWire.Operation.Resolve(text "userCode" pairing))
        let! result = dispatch server value
        match (Fixture.ok result).Outcome |> Fixture.ok with
        | Hermes.Core.LocalAccessWire.Reply.Resolved found ->
            Assert.True(found.Challenge = Challenge.text Fixture.challenge)
            Assert.Equal(server.Origin, found.BrowserOrigin)
            Assert.Equal(server.Origin, found.ApiOrigin)
        | _ -> failwith "Expected resolved owner result."
        Assert.Equal("<redacted access value>", string Fixture.challenge)
        Assert.Equal("<redacted access value>", sprintf "%A" Fixture.challenge)
    })

[<Fact>]
let ``owner adapter rejects malformed DTOs before owner dispatch`` () : Task =
    withServer true true (fun server -> task {
        let value = request None (Hermes.Core.LocalAccessWire.Operation.Resolve("0000-0000-0000"))
        let! result = dispatch server value
        Assert.True(Result.isError result)
        let baseline = request None Hermes.Core.LocalAccessWire.Operation.Status
        let malformed = { baseline with RequestId = "not-canonical" }
        let! rejected = dispatch server malformed
        Assert.True(Result.isError rejected)
    })

[<Fact>]
let ``owner approval adapter accepts only a pinned strict approval DTO`` () : Task =
    withServer true true (fun server -> task {
        let! pairing = pair server (Set.singleton Capability.Mutate)
        let payload: Hermes.Core.LocalAccessWire.ApprovalPayload =
            { PairingId = text "pairingId" pairing; UserCode = text "userCode" pairing
              Challenge = Challenge.text Fixture.challenge; Capabilities = [ "mutate" ] }
        let value =
            request (Some(InstanceId.text server.Identity.InstanceId))
                (Hermes.Core.LocalAccessWire.Operation.Approve payload)
        let! approved = dispatch server value
        match (Fixture.ok approved).Outcome |> Fixture.ok with
        | Hermes.Core.LocalAccessWire.Reply.Approved result ->
            Assert.Equal<string list>([ "mutate" ], result.Capabilities)
        | _ -> failwith "Expected owner approval."
        let! issued = exchange server pairing
        Assert.Equal(201, issued.Status)
    })
