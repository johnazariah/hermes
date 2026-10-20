module Hermes.Tests.LocalAccessOutboundTests

open Xunit
open Hermes.Core.LocalAccessWire
open Hermes.Tests.OwnerWireSamples

let private replacement = "AQEBAQEBAQEBAQEBAQEBAQ"

let private baseline () : Response =
    Codec.decodeResponse (request 1) peer (responseBytes 1) |> ok

let private staleResponse () : Response =
    let original = baseline ()
    { original with
        Instance = { original.Instance with InstanceId = replacement }
        Outcome = Error ErrorCode.WrongInstance }

[<Fact>]
let ``outbound wrong instance is canonical while the stale client still rejects it`` () =
    let pinned = request 1
    let response = staleResponse ()
    let encoded = Codec.encodeResponse pinned response |> ok
    Assert.Equal<Result<Response, CodecFailure>>(
        Error CodecFailure.IdentityMismatch, Codec.decodeResponse pinned peer encoded)
    let current = { pinned with ExpectedInstanceId = Some replacement }
    let decoded = Codec.decodeResponse current peer encoded |> ok
    Assert.Equal(response, decoded)
    Assert.Equal<Result<Reply, ErrorCode>>(Error ErrorCode.WrongInstance, decoded.Outcome)

[<Fact>]
let ``outbound stale success and unrelated errors are inconsistent`` () =
    let original = baseline ()
    let stale = staleResponse ()
    let contradictions =
        [ { stale with Outcome = original.Outcome }
          { stale with Outcome = Error ErrorCode.PairingNotFound }
          { original with Outcome = Error ErrorCode.WrongInstance } ]
    contradictions
    |> List.iter (fun response ->
        Assert.True(Codec.encodeResponse (request 1) response |> Result.isError))

[<Fact>]
let ``outbound stale rejection retains request correlation and canonical identity grammar`` () =
    let stale = staleResponse ()
    let malformed =
        [ { stale with RequestId = Codec.newRequestId() }
          { stale with Instance = { stale.Instance with ProcessId = 0 } }
          { stale with Instance = { stale.Instance with InstanceId = "bad" } }
          { stale with Instance = { stale.Instance with ConfigKey = "bad" } } ]
    malformed
    |> List.iter (fun response ->
        Assert.True(Codec.encodeResponse (request 1) response |> Result.isError))
    let unpinned = { request 1 with ExpectedInstanceId = None }
    Assert.True(Codec.encodeResponse unpinned stale |> Result.isError)
    let malformedPin = { request 1 with ExpectedInstanceId = Some "bad" }
    Assert.True(Codec.encodeResponse malformedPin stale |> Result.isError)

[<Fact>]
let ``outbound success retains the operation result grammar`` () =
    let original = baseline ()
    let status: StatusResult = { Phase = Phase.Ready; Capabilities = [ "archive.read" ] }
    let wrongOperation = { original with Outcome = Ok(Reply.Status status) }
    Assert.True(Codec.encodeResponse (request 1) wrongOperation |> Result.isError)
    match original.Outcome |> ok with
    | Reply.Resolved found ->
        let malformed =
            { original with
                Outcome = Ok(Reply.Resolved
                    { found with RequestedCapabilities = [ "mutate"; "config.read" ] }) }
        Assert.True(Codec.encodeResponse (request 1) malformed |> Result.isError)
    | _ -> failwith "Expected the synthetic resolve result."
