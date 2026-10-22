namespace Hermes.Service

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Hermes.Core
open Hermes.Core.LocalAccess

module internal HttpPairingInput =
    open HttpAccessJson

    let private identifier (value: JsonElement) : Result<InstanceId, HttpAccessFailure> =
        text 22 "instanceId" value |> Result.bind (InstanceId.tryParse >> Result.mapError HttpAccessFailure.Domain)

    let create (origin: BrowserOrigin) (value: JsonElement) : Result<CreateRequest, HttpAccessFailure> =
        result {
            do! exact [ "version"; "instanceId"; "challenge"; "requestedCapabilities" ] value
            do! version value
            let! instance = identifier value
            let! challenge = text 43 "challenge" value
                             |> Result.bind (Challenge.tryParse >> Result.mapError HttpAccessFailure.Domain)
            let! capabilities = strings "requestedCapabilities" value |> Result.bind HttpAccessValues.capabilities
            return
                { InstanceId = instance; Challenge = challenge
                  RequestedCapabilities = capabilities; BrowserOrigins = [ origin ] }
        }

    let exchange (pairing: PairingId) (origin: BrowserOrigin option)
        (value: JsonElement) : Result<ExchangeRequest, HttpAccessFailure> =
        result {
            do! exact [ "version"; "instanceId"; "verifier" ] value
            do! version value
            let! instance = identifier value
            let! proof = text 43 "verifier" value
            let! verifier = Verifier.tryParse proof
                            |> Result.mapError (fun _ -> HttpAccessFailure.Domain AccessError.InvalidPairingProof)
            return
                { InstanceId = instance; PairingId = pairing
                  Verifier = verifier; BrowserOrigin = origin }
        }

module internal HttpPairingOutput =
    let status (context: HttpContext) (value: AccessStatus) : Task =
        HttpAccessResponse.json context 200
            {| version = 1; instanceId = InstanceId.text value.Identity.InstanceId
               processId = value.Identity.ProcessId
               processStartTimeUtc = value.Identity.ProcessStartTimeUtc
               origin = ApiAudience.text value.Identity.ApiOrigin
               state = if value.State = AuthenticationState.Anonymous then "anonymous" else "authenticated"
               capabilities = HttpAccessValues.effectiveNames value.Capabilities
               expiresAt = value.ExpiresAt |> Option.toNullable
               pairingMethod = "ownerApproval" |}

    let receipt (context: HttpContext) (value: PairingReceipt) : Task =
        HttpAccessResponse.json context 201
            {| version = 1; instanceId = InstanceId.text value.InstanceId
               pairingId = PairingId.text value.PairingId; userCode = DisplayCode.text value.UserCode
               browserOrigin = BrowserOrigin.text value.BrowserOrigin
               apiOrigin = ApiAudience.text value.ApiOrigin
               requestedCapabilities = HttpAccessValues.authorityNames value.RequestedCapabilities
               expiresAt = value.ExpiresAt; status = "pending" |}

    let pending (instance: InstanceId) (expires: DateTimeOffset) (context: HttpContext) : Task =
        HttpAccessResponse.header context "Retry-After" "2"
        HttpAccessResponse.json context 202
            {| version = 1; instanceId = InstanceId.text instance
               status = "pending"; expiresAt = expires |}

    let issued (context: HttpContext) (value: IssuedSession) : Task =
        HttpAccessResponse.json context 201
            {| version = 1; instanceId = InstanceId.text value.InstanceId
               tokenType = "Bearer"; accessToken = SessionCredential.text value.Credential
               capabilities = HttpAccessValues.authorityNames value.Capabilities
               expiresAt = value.ExpiresAt |}

    let render (operation: AccessProtocolOperation) (instance: InstanceId)
        (context: HttpContext) (outcome: Result<Response, AccessError>) : Task =
        let success (response: Response) : Task =
            match response with
            | Response.Status value when operation = AccessProtocolOperation.Status -> status context value
            | Response.PairingCreated value when operation = AccessProtocolOperation.CreatePairing -> receipt context value
            | Response.ExchangePending expires when operation = AccessProtocolOperation.Exchange ->
                pending instance expires context
            | Response.SessionIssued value when operation = AccessProtocolOperation.Exchange -> issued context value
            | Response.Revoked when operation = AccessProtocolOperation.Revoke ->
                context.Response.StatusCode <- StatusCodes.Status204NoContent
                Task.CompletedTask
            | _ -> HttpAccessResponse.failure instance Set.empty context HttpAccessValues.unavailable
        outcome |> Result.map success
        |> Result.defaultWith (HttpAccessFailure.Domain >> HttpAccessResponse.failure instance Set.empty context)

    let complete (operation: AccessProtocolOperation) (instance: InstanceId)
        (context: HttpContext) (pending: AccessReply) : Task =
        task {
            let! outcome = pending
            do! render operation instance context outcome
        }

[<RequireQualifiedAccess>]
module LocalAccessHttp =
    let private feature (context: HttpContext) : Result<IHttpAccessFeature, HttpAccessFailure> =
        context.Features.Get<IHttpAccessFeature>() |> Option.ofObj
        |> Option.map Ok |> Option.defaultValue (Error HttpAccessValues.unavailable)

    let private withFeature (instance: InstanceId) (context: HttpContext)
        (action: IHttpAccessFeature -> Task) : Task =
        feature context |> Result.map action
        |> Result.defaultWith (HttpAccessResponse.failure instance Set.empty context)

    let private sessionOperation (instance: InstanceId) (operation: AccessProtocolOperation)
        (context: HttpContext)
        (invoke: PublicAccess -> SessionContext -> CancellationToken -> AccessReply) : Task =
        withFeature instance context (fun feature ->
            invoke feature.Public feature.Session context.RequestAborted
            |> HttpPairingOutput.complete operation instance context)

    let private createFrom (instance: InstanceId) (context: HttpContext)
        (feature: IHttpAccessFeature) (origin: BrowserOrigin) : Task =
        task {
            let! parsed = HttpAccessJson.read (HttpPairingInput.create origin) context
            match parsed with
            | Error failure -> do! HttpAccessResponse.failure instance Set.empty context failure
            | Ok request ->
                let! outcome = feature.Public.Create request context.RequestAborted
                do! HttpPairingOutput.render AccessProtocolOperation.CreatePairing instance context outcome
        }

    let private create (instance: InstanceId) (context: HttpContext) : Task =
        withFeature instance context (fun feature ->
            feature.Session.BrowserOrigin
            |> Option.map (createFrom instance context feature)
            |> Option.defaultWith (fun () ->
                HttpAccessResponse.failure instance Set.empty context HttpAccessValues.invalid))

    let private rejection (failure: HttpAccessFailure) : InvalidExchange =
        match failure with
        | HttpAccessFailure.Domain AccessError.InvalidPairingProof -> InvalidExchange.NoncanonicalProof
        | _ -> InvalidExchange.MalformedRequest

    let private rejectedExchange (instance: InstanceId) (context: HttpContext)
        (feature: IHttpAccessFeature) (failure: HttpAccessFailure) : Task =
        task {
            let! outcome = feature.Public.Exchange (Error(rejection failure)) context.RequestAborted
            let selected =
                outcome |> Result.map (fun _ -> HttpAccessValues.unavailable)
                |> Result.defaultWith (function
                    | AccessError.InvalidRequest | AccessError.InvalidPairingProof -> failure
                    | error -> HttpAccessFailure.Domain error)
            do! HttpAccessResponse.failure instance Set.empty context selected
        }

    let private exchangeFrom (instance: InstanceId) (context: HttpContext)
        (feature: IHttpAccessFeature) (pairing: PairingId) : Task =
        task {
            let parser = HttpPairingInput.exchange pairing feature.Session.BrowserOrigin
            let! parsed = HttpAccessJson.read parser context
            match parsed with
            | Error failure -> do! rejectedExchange instance context feature failure
            | Ok request ->
                let! outcome = feature.Public.Exchange (Ok request) context.RequestAborted
                do! HttpPairingOutput.render AccessProtocolOperation.Exchange instance context outcome
        }

    let private exchange (instance: InstanceId) (context: HttpContext) (pairingId: string) : Task =
        withFeature instance context (fun feature ->
            PairingId.tryParse pairingId
            |> Result.map (exchangeFrom instance context feature)
            |> Result.defaultWith (fun _ ->
                rejectedExchange instance context feature HttpAccessValues.invalid))

    let private mapSessionEndpoints (app: WebApplication) (instance: InstanceId) : unit =
        app.MapGet("/api/access", Func<HttpContext, Task>(fun context ->
            sessionOperation instance AccessProtocolOperation.Status context (fun api -> api.Status)))
        |> HttpAccessPolicy.classify (HttpAccessClassification.AccessProtocol AccessProtocolOperation.Status)
        |> ignore
        app.MapDelete("/api/access/session", Func<HttpContext, Task>(fun context ->
            sessionOperation instance AccessProtocolOperation.Revoke context (fun api -> api.Revoke)))
        |> HttpAccessPolicy.classify (HttpAccessClassification.AccessProtocol AccessProtocolOperation.Revoke)
        |> ignore

    let private mapPairingEndpoints (app: WebApplication) (instance: InstanceId) : unit =
        app.MapPost("/api/access/pairings", Func<HttpContext, Task>(create instance))
        |> HttpAccessPolicy.classify (HttpAccessClassification.AccessProtocol AccessProtocolOperation.CreatePairing)
        |> ignore
        app.MapPost("/api/access/pairings/{pairingId}/exchange",
            Func<HttpContext, string, Task>(fun context pairingId -> exchange instance context pairingId))
        |> HttpAccessPolicy.classify (HttpAccessClassification.AccessProtocol AccessProtocolOperation.Exchange)
        |> ignore

    let mapEndpoints (app: WebApplication) (instance: InstanceId) : unit =
        mapSessionEndpoints app instance
        mapPairingEndpoints app instance
