module internal Hermes.Tests.ServiceAccessHostSessionSupport

open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core.LocalAccess
open Hermes.Core.OwnerControlPipe
open Hermes.Service
open Hermes.Tests.ServiceAccessHostTestSupport

type PendingPairing =
    { Receipt: JsonElement
      Verifier: Verifier
      Challenge: Challenge }

let private unusedDispatch
    (_: Hermes.Core.LocalAccessWire.Request)
    (_: CancellationToken)
    : Task<Hermes.Core.LocalAccessWire.Response> =
    Task.FromException<Hermes.Core.LocalAccessWire.Response>(
        System.InvalidOperationException("Client-only owner-control factory."))

let ownerRequest
    (context: HostContext)
    (operation: Hermes.Core.LocalAccessWire.Operation)
    : Task<Result<Hermes.Core.LocalAccessWire.Response, PipeFailure>> =
    let request: Hermes.Core.LocalAccessWire.Request =
        { RequestId = Hermes.Core.LocalAccessWire.Codec.newRequestId()
          ExpectedInstanceId = Some(InstanceId.text context.Identity.InstanceId)
          Operation = operation }
    let peer: Hermes.Core.LocalAccessWire.PeerExpectation =
        { ProcessId = context.Identity.ProcessId
          ProcessStartTimeUtc = context.Identity.ProcessStartTimeUtc
          Origin = ApiAudience.text context.Identity.ApiOrigin
          ConfigKey = context.Identity.ConfigKey }
    let client = Factory.createCancellable context.Target unusedDispatch ignore
    client.Send request peer CancellationToken.None

let createPairing (server: Server) : Task<PendingPairing> =
    task {
        let! captured = wait server.Captured
        let verifier = RandomNumberGenerator.GetBytes(32) |> Verifier.ofBytes |> ok
        let challenge = Challenge.ofVerifier verifier
        let body =
            JsonSerializer.Serialize
                {| version = 1
                   instanceId = InstanceId.text captured.Context.Identity.InstanceId
                   challenge = Challenge.text challenge
                   requestedCapabilities = [| Capability.name Capability.Mutate |] |}
        let! reply = post server pairingsPath None body
        Assert.Equal(201, reply.Status)
        return { Receipt = json reply; Verifier = verifier; Challenge = challenge }
    }

let exchange (server: Server) (pairing: PendingPairing) : Task<Reply> =
    task {
        let! captured = wait server.Captured
        let body =
            JsonSerializer.Serialize
                {| version = 1
                   instanceId = InstanceId.text captured.Context.Identity.InstanceId
                   verifier = Verifier.text pairing.Verifier |}
        let path =
            pairingsPath + "/" + field "pairingId" pairing.Receipt + "/exchange"
        return! post server path None body
    }

let approve (server: Server) (pairing: PendingPairing) : Task<unit> =
    task {
        let! captured = wait server.Captured
        let payload: Hermes.Core.LocalAccessWire.ApprovalPayload =
            { PairingId = field "pairingId" pairing.Receipt
              UserCode = field "userCode" pairing.Receipt
              Challenge = Challenge.text pairing.Challenge
              Capabilities = [ Capability.name Capability.Mutate ] }
        let! outcome =
            ownerRequest captured.Context
                (Hermes.Core.LocalAccessWire.Operation.Approve payload)
        match (ok outcome).Outcome |> ok with
        | Hermes.Core.LocalAccessWire.Reply.Approved grant ->
            Assert.Equal(payload.PairingId, grant.PairingId)
        | _ -> failwith "Expected private owner approval."
    }

let issue (server: Server) : Task<string> =
    task {
        let! pairing = createPairing server
        do! approve server pairing
        let! exchanged = exchange server pairing
        Assert.Equal(201, exchanged.Status)
        return json exchanged |> field "accessToken"
    }

/// Independent routes must not depend on unused HTTP-session setup.
let credentialForPath
    (protectedPath: string)
    (issueCredential: unit -> Task<string>)
    (path: string)
    : Task<string option> =
    task {
        if path = protectedPath then
            let! credential = issueCredential ()
            return Some credential
        else
            return None
    }
