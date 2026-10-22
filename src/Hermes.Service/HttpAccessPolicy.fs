namespace Hermes.Service

open System
open System.Globalization
open System.IO
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Primitives
open Hermes.Core
open Hermes.Core.LocalAccess

[<RequireQualifiedAccess>]
type HttpAccessFailure =
    | Domain of AccessError
    | InvalidAuthorization
    | RequestTooLarge
    | UnsupportedMediaType
    | ModelUnavailable

[<RequireQualifiedAccess>]
type AccessProtocolOperation = Status | CreatePairing | Exchange | Revoke

[<RequireQualifiedAccess>]
type InteractiveKind = Chat | Search

[<RequireQualifiedAccess>]
type ModelOperation = Chat | Search

[<RequireQualifiedAccess>]
type ModelWorkPolicy = ArchiveOnly | InvokeModel of ModelOperation

type HttpChatRequest =
    { Query: string
      AiEnabled: bool }

[<RequireQualifiedAccess>]
type InteractiveRequest =
    | Chat of HttpChatRequest
    | Search of SearchContract.Request

type PreparsedRequest =
    { Request: InteractiveRequest
      Policy: ModelWorkPolicy }

type ModelAvailability =
    { IsAvailable: ModelOperation -> bool }

[<RequireQualifiedAccess>]
type HttpAccessClassification =
    | AnonymousArchiveRead
    | RequiredCaps of Set<Capability>
    | ParsedChatOrSearch of InteractiveKind
    | AccessProtocol of AccessProtocolOperation
    | McpIndependent

type HttpPolicyValidationFailure =
    { EndpointName: string
      ClassificationCount: int }

type internal IHttpAccessFeature =
    abstract Public: PublicAccess
    abstract Session: SessionContext

type internal IPreparsedAccessFeature =
    abstract Value: PreparsedRequest

module internal HttpAccessValues =
    let invalid: HttpAccessFailure = HttpAccessFailure.Domain AccessError.InvalidRequest
    let unavailable: HttpAccessFailure = HttpAccessFailure.Domain AccessError.AccessUnavailable

    let ordinal (left: string) (right: string) : int =
        StringComparer.Ordinal.Compare(left, right)

    let authorityNames (values: Set<Capability>) : string array =
        values |> Seq.map Capability.name |> Seq.sortWith ordinal |> Seq.toArray

    let effectiveNames (values: Set<EffectiveCapability>) : string array =
        values |> Seq.map EffectiveCapability.name |> Seq.sortWith ordinal |> Seq.toArray

    let private capability (value: string) : Result<Capability, HttpAccessFailure> =
        match value with
        | "config.read" -> Ok Capability.ConfigRead
        | "model.invoke" -> Ok Capability.ModelInvoke
        | "mutate" -> Ok Capability.Mutate
        | _ -> Error invalid

    let traverse (parser: 'Input -> Result<'Output, 'Error>) (values: 'Input list)
        : Result<'Output list, 'Error> =
        values
        |> List.fold (fun state value ->
            state |> Result.bind (fun collected ->
                parser value |> Result.map (fun parsed -> parsed :: collected))) (Ok [])
        |> Result.map List.rev

    let capabilities (values: string list) : Result<Set<Capability>, HttpAccessFailure> =
        if values.Length > 3 || Set.count (Set.ofList values) <> values.Length
           || values <> List.sortWith ordinal values then Error invalid
        else values |> traverse capability |> Result.map Set.ofList

module internal HttpAccessResponse =
    type Description = { Status: int; Code: string }

    let private domainCode (error: AccessError) : string =
        match error with
        | AccessError.InvalidRequest -> "invalid_request"
        | AccessError.WrongInstance -> "wrong_instance"
        | AccessError.PairingNotFound -> "pairing_not_found"
        | AccessError.PairingExpired -> "pairing_expired"
        | AccessError.PairingConsumed -> "pairing_consumed"
        | AccessError.InsufficientCapability -> "insufficient_capability"
        | AccessError.AccessCapacityReached -> "access_capacity_reached"
        | AccessError.AccessRateLimited -> "access_rate_limited"
        | AccessError.InvalidPairingProof -> "invalid_pairing_proof"
        | AccessError.AuthenticationRequired -> "authentication_required"
        | AccessError.InvalidCredential -> "invalid_credential"
        | AccessError.CredentialExpired -> "credential_expired"
        | AccessError.SessionOriginMismatch -> "session_origin_mismatch"
        | AccessError.InstanceNotReady | AccessError.AccessUnavailable
        | AccessError.PairingConflict | AccessError.InternalError -> "access_unavailable"

    let private domainStatus (error: AccessError) : int =
        match error with
        | AccessError.InvalidRequest -> 400
        | AccessError.WrongInstance | AccessError.InvalidPairingProof
        | AccessError.AuthenticationRequired | AccessError.InvalidCredential
        | AccessError.CredentialExpired -> 401
        | AccessError.InsufficientCapability | AccessError.SessionOriginMismatch -> 403
        | AccessError.PairingNotFound -> 404
        | AccessError.PairingConsumed -> 409
        | AccessError.PairingExpired -> 410
        | AccessError.AccessCapacityReached | AccessError.AccessRateLimited -> 429
        | AccessError.InstanceNotReady | AccessError.AccessUnavailable
        | AccessError.PairingConflict | AccessError.InternalError -> 503

    let private describe (failure: HttpAccessFailure) : Description =
        match failure with
        | HttpAccessFailure.Domain error -> { Status = domainStatus error; Code = domainCode error }
        | HttpAccessFailure.InvalidAuthorization -> { Status = 401; Code = "invalid_authorization" }
        | HttpAccessFailure.RequestTooLarge -> { Status = 413; Code = "request_too_large" }
        | HttpAccessFailure.UnsupportedMediaType -> { Status = 415; Code = "unsupported_media_type" }
        | HttpAccessFailure.ModelUnavailable -> { Status = 503; Code = "model_unavailable" }

    let header (context: HttpContext) (name: string) (value: string) : unit =
        context.Response.Headers[name] <- StringValues(value)

    let private expose (context: HttpContext) : unit =
        let existing =
            context.Response.Headers["Access-Control-Expose-Headers"].ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
        Seq.append existing [ "Retry-After"; "WWW-Authenticate" ]
        |> Seq.distinctBy (fun value -> value.ToLowerInvariant())
        |> String.concat ", "
        |> header context "Access-Control-Expose-Headers"

    let private applyHeaders (context: HttpContext) : unit =
        header context "Cache-Control" "no-store"
        header context "X-Content-Type-Options" "nosniff"
        expose context

    let secure (context: HttpContext) : unit =
        applyHeaders context
        context.Response.OnStarting(Func<Task>(fun () ->
            applyHeaders context
            Task.CompletedTask))

    let json (context: HttpContext) (status: int) (value: 'Value) : Task =
        Results.Json(value, statusCode = status).ExecuteAsync(context)

    let failure (instance: InstanceId) (required: Set<Capability>)
        (context: HttpContext) (error: HttpAccessFailure) : Task =
        let description = describe error
        applyHeaders context
        if description.Status = 401 then
            header context "WWW-Authenticate" (AuthenticationHeaderValue("Bearer", "realm=\"Hermes\"").ToString())
        json context description.Status
            {| error = description.Code
               message = "Local access request failed."
               instanceId = InstanceId.text instance
               requiredCapabilities = HttpAccessValues.authorityNames required |}

module internal HttpAccessJson =
    type Builder() =
        member _.Bind(value: Result<'Value, 'Error>, continuation: 'Value -> Result<'Next, 'Error>)
            : Result<'Next, 'Error> = Result.bind continuation value
        member _.Return(value: 'Value) : Result<'Value, 'Error> = Ok value
        member _.ReturnFrom(value: Result<'Value, 'Error>) : Result<'Value, 'Error> = value

    let result: Builder = Builder()
    let maximumBytes: int = 4096
    let private invalid: HttpAccessFailure = HttpAccessValues.invalid

    let rec private unique (value: JsonElement) : bool =
        match value.ValueKind with
        | JsonValueKind.Object ->
            let properties = value.EnumerateObject() |> Seq.toArray
            let names = properties |> Array.map (fun property -> property.Name)
            Set.count (Set.ofArray names) = names.Length
            && properties |> Array.forall (fun property -> unique property.Value)
        | JsonValueKind.Array -> value.EnumerateArray() |> Seq.forall unique
        | JsonValueKind.String ->
            value.GetString() |> ignore
            true
        | _ -> true

    // JsonDocument may retain invalid escaped UTF-16 until Name/GetString materialises it.
    // Catch only around this bounded DOM walk, never around the supplied parser or effects.
    let private materialized (value: JsonElement) : bool =
        try unique value
        with :? InvalidOperationException -> false

    let exact (names: string list) (value: JsonElement) : Result<unit, HttpAccessFailure> =
        if value.ValueKind <> JsonValueKind.Object then Error invalid
        else
            let actual = value.EnumerateObject() |> Seq.map (fun property -> property.Name) |> Seq.toList
            if actual.Length = names.Length && Set.ofList actual = Set.ofList names then Ok ()
            else Error invalid

    let stringValue (maximum: int) (value: JsonElement) : Result<string, HttpAccessFailure> =
        if value.ValueKind <> JsonValueKind.String then Error invalid
        else
            value.GetString() |> Option.ofObj
            |> Option.filter (fun text -> text.Length > 0 && text.Length <= maximum)
            |> Option.map Ok |> Option.defaultValue (Error invalid)

    let text (maximum: int) (name: string) (value: JsonElement) : Result<string, HttpAccessFailure> =
        value.GetProperty(name) |> stringValue maximum

    let version (value: JsonElement) : Result<unit, HttpAccessFailure> =
        let field = value.GetProperty("version")
        if field.ValueKind <> JsonValueKind.Number then Error invalid
        else
            match field.TryGetInt32() with
            | true, 1 -> Ok ()
            | _ -> Error invalid

    let strings (name: string) (value: JsonElement) : Result<string list, HttpAccessFailure> =
        let field = value.GetProperty(name)
        if field.ValueKind <> JsonValueKind.Array || field.GetArrayLength() > 3 then Error invalid
        else field.EnumerateArray() |> Seq.toList |> HttpAccessValues.traverse (stringValue 32)

    let private decode (parser: JsonElement -> Result<'Value, HttpAccessFailure>) (bytes: byte array)
        : Result<'Value, HttpAccessFailure> =
        try
            let text = UTF8Encoding(false, true).GetString(bytes)
            if text.StartsWith("\uFEFF", StringComparison.Ordinal) then Error invalid
            else
                let options = JsonDocumentOptions(MaxDepth = 8, AllowTrailingCommas = false)
                use document = JsonDocument.Parse(text, options)
                if materialized document.RootElement then parser document.RootElement else Error invalid
        with
        | :? JsonException | :? DecoderFallbackException -> Error invalid

    let private validMediaType (candidate: MediaTypeHeaderValue) : bool =
        String.Equals(candidate.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
        && candidate.Parameters.Count <= 1
        && (candidate.Parameters |> Seq.forall (fun parameter ->
            String.Equals(parameter.Name, "charset", StringComparison.OrdinalIgnoreCase)))
        && (candidate.CharSet |> Option.ofObj
            |> Option.forall (fun charset ->
                String.Equals(charset.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)))

    let private mediaType (context: HttpContext) : bool =
        let values = context.Request.Headers["Content-Type"]
        if values.Count <> 1 || context.Request.Headers.ContainsKey("Content-Encoding") then false
        else
            match MediaTypeHeaderValue.TryParse(values.ToString()) with
            | true, candidate ->
                candidate |> Option.ofObj |> Option.exists validMediaType
            | _ -> false

    let rec private fill (context: HttpContext) (buffer: byte array) (offset: int)
        : Task<Result<byte array, HttpAccessFailure>> =
        task {
            let memory = Memory<byte>(buffer, offset, buffer.Length - offset)
            let! count = context.Request.Body.ReadAsync(memory, context.RequestAborted)
            let next = offset + count
            if next > maximumBytes then return Error HttpAccessFailure.RequestTooLarge
            elif count = 0 then return Ok(buffer |> Array.take next)
            else return! fill context buffer next
        }

    let private readBytes (context: HttpContext) : Task<Result<byte array, HttpAccessFailure>> =
        task {
            if not (mediaType context) then return Error HttpAccessFailure.UnsupportedMediaType
            elif context.Request.ContentLength.HasValue
                 && context.Request.ContentLength.Value > int64 maximumBytes then
                return Error HttpAccessFailure.RequestTooLarge
            else
                try return! fill context (Array.zeroCreate (maximumBytes + 1)) 0
                with
                | :? BadHttpRequestException | :? IOException -> return Error invalid
        }

    let read (parser: JsonElement -> Result<'Value, HttpAccessFailure>) (context: HttpContext)
        : Task<Result<'Value, HttpAccessFailure>> =
        readBytes context
        |> Prelude.TaskResult.bind (fun bytes -> decode parser bytes |> Task.FromResult)

module internal HttpAccessInput =
    open HttpAccessJson

    let origin (context: HttpContext) : Result<BrowserOrigin option, HttpAccessFailure> =
        let headers = context.Request.Headers
        let values = headers["Origin"]
        if not (headers.ContainsKey("Origin")) then Ok None
        elif values.Count <> 1 then Error HttpAccessValues.invalid
        else
            values.ToString() |> BrowserOrigin.tryParse
            |> Result.map Some |> Result.mapError HttpAccessFailure.Domain

    let private bearer (value: string) : Result<CredentialInput, HttpAccessFailure> =
        if Encoding.UTF8.GetByteCount(value) > 1024
           || not (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) then
            Error HttpAccessFailure.InvalidAuthorization
        else
            let token = value.Substring(7)
            if token.Contains(',') || token |> Seq.exists Char.IsWhiteSpace then
                Error HttpAccessFailure.InvalidAuthorization
            else
                token |> SessionCredential.tryParse |> Result.map CredentialInput.Present
                |> Result.mapError HttpAccessFailure.Domain

    let private credential (context: HttpContext) : Result<CredentialInput, HttpAccessFailure> =
        let headers = context.Request.Headers
        let values = headers["Authorization"]
        if not (headers.ContainsKey("Authorization")) then Ok CredentialInput.Absent
        elif values.Count <> 1 then Error HttpAccessFailure.InvalidAuthorization
        else bearer (values.ToString())

    let session (context: HttpContext) : Result<SessionContext, HttpAccessFailure> =
        result {
            let! suppliedOrigin = origin context
            let! suppliedCredential = credential context
            return { Credential = suppliedCredential; BrowserOrigin = suppliedOrigin }
        }

module internal HttpInteractiveInput =
    open HttpAccessJson

    let private chat (value: JsonElement) : Result<InteractiveRequest, HttpAccessFailure> =
        result {
            do! exact [ "query"; "aiEnabled" ] value
            let! query = text 3072 "query" value
            let enabled = value.GetProperty("aiEnabled")
            if enabled.ValueKind <> JsonValueKind.True && enabled.ValueKind <> JsonValueKind.False then
                return! Error HttpAccessValues.invalid
            else
                let request: HttpChatRequest = { Query = query; AiEnabled = enabled.GetBoolean() }
                return InteractiveRequest.Chat request
        }

    let private queryNames: Set<string> =
        Set.ofList [ "q"; "mode"; "limit"; "category"; "sender"; "dateFrom"
                     "dateTo"; "account"; "sourceType" ]

    let private validQuery (context: HttpContext) : bool =
        let raw = context.Request.QueryString.Value |> Option.ofObj |> Option.defaultValue ""
        Encoding.UTF8.GetByteCount(raw) <= maximumBytes
        && (context.Request.Query |> Seq.forall (fun pair ->
            Set.contains pair.Key queryNames && pair.Value.Count = 1))

    let private optional (name: string) (context: HttpContext) : string option =
        if context.Request.Query.ContainsKey(name) then Some(context.Request.Query[name].ToString())
        else None

    let private limit (context: HttpContext) : Result<int, HttpAccessFailure> =
        optional "limit" context
        |> Option.map (fun value ->
            match Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, count when count > 0 -> Ok count
            | _ -> Error HttpAccessValues.invalid)
        |> Option.defaultValue (Ok 50)

    let private searchRequest (context: HttpContext) (mode: SearchContract.Mode)
        (count: int) : SearchContract.Request =
        { Mode = mode
          Filter =
            { Search.defaultFilter (optional "q" context |> Option.defaultValue "") with
                Category = optional "category" context; Sender = optional "sender" context
                DateFrom = optional "dateFrom" context; DateTo = optional "dateTo" context
                Account = optional "account" context; SourceType = optional "sourceType" context
                Limit = count } }

    let private search (context: HttpContext) : Result<InteractiveRequest, HttpAccessFailure> =
        result {
            if not (validQuery context) then return! Error HttpAccessValues.invalid
            else
                let! mode = optional "mode" context |> SearchContract.parseMode
                            |> Result.mapError (fun _ -> HttpAccessValues.invalid)
                let! count = limit context
                return! searchRequest context mode count |> SearchContract.validate
                        |> Result.map InteractiveRequest.Search
                        |> Result.mapError (fun _ -> HttpAccessValues.invalid)
        }

    let private policy (value: InteractiveRequest) : ModelWorkPolicy =
        match value with
        | InteractiveRequest.Chat request when request.AiEnabled ->
            ModelWorkPolicy.InvokeModel ModelOperation.Chat
        | InteractiveRequest.Search request when request.Mode <> SearchContract.Keyword ->
            ModelWorkPolicy.InvokeModel ModelOperation.Search
        | _ -> ModelWorkPolicy.ArchiveOnly

    let read (kind: InteractiveKind) (context: HttpContext)
        : Task<Result<PreparsedRequest, HttpAccessFailure>> =
        let pending =
            match kind with
            | InteractiveKind.Chat -> HttpAccessJson.read chat context
            | InteractiveKind.Search -> search context |> Task.FromResult
        pending |> Prelude.TaskResult.map (fun request -> { Request = request; Policy = policy request })

module internal HttpEndpointValidation =
    let validClassification (classification: HttpAccessClassification) : bool =
        match classification with
        | HttpAccessClassification.RequiredCaps capabilities -> not (Set.isEmpty capabilities)
        | HttpAccessClassification.AnonymousArchiveRead
        | HttpAccessClassification.ParsedChatOrSearch _
        | HttpAccessClassification.AccessProtocol _
        | HttpAccessClassification.McpIndependent -> true

    let private failure (endpoint: Endpoint) : HttpPolicyValidationFailure option =
        let metadata = endpoint.Metadata.GetOrderedMetadata<HttpAccessClassification>()
        if metadata.Count = 1 && validClassification metadata[0] then None
        else
            Some
                { EndpointName = endpoint.DisplayName |> Option.ofObj |> Option.defaultValue "<unnamed>"
                  ClassificationCount = metadata.Count }

    let validate (endpoints: seq<Endpoint>) : Result<unit, HttpPolicyValidationFailure list> =
        let failures = endpoints |> Seq.choose failure |> Seq.toList
        if List.isEmpty failures then Ok () else Error failures

    let register (services: IServiceCollection) : unit =
        services.AddSingleton<IStartupFilter>(
            { new IStartupFilter with
                member _.Configure(next: Action<IApplicationBuilder>) =
                    Action<IApplicationBuilder>(fun app ->
                        next.Invoke(app)
                        app.ApplicationServices.GetServices<EndpointDataSource>()
                        |> Seq.collect (fun source -> source.Endpoints)
                        |> validate
                        |> Result.defaultWith (fun _ ->
                            invalidOp "Every application endpoint requires exactly one valid HTTP access classification.")) })
        |> ignore

[<RequireQualifiedAccess>]
module HttpAccessPolicy =
    let private anonymous: SessionContext =
        { Credential = CredentialInput.Absent; BrowserOrigin = None }

    let classify<'Builder when 'Builder :> IEndpointConventionBuilder>
        (classification: HttpAccessClassification) (builder: 'Builder) : 'Builder =
        builder.Add(Action<EndpointBuilder>(fun endpoint ->
            endpoint.Metadata.Add(classification :> obj)))
        builder

    let validateEndpoints (endpoints: seq<Endpoint>) : Result<unit, HttpPolicyValidationFailure list> =
        HttpEndpointValidation.validate endpoints

    /// Register before builder.Build(); validation executes while the HTTP application starts.
    let addStartupValidation (services: IServiceCollection) : unit =
        HttpEndpointValidation.register services

    let preparsed (context: HttpContext) : Result<PreparsedRequest, HttpAccessFailure> =
        context.Features.Get<IPreparsedAccessFeature>() |> Option.ofObj
        |> Option.map (fun feature -> Ok feature.Value)
        |> Option.defaultValue (Error HttpAccessValues.unavailable)

    let private remember (api: PublicAccess) (session: SessionContext) (context: HttpContext) : unit =
        context.Features.Set<IHttpAccessFeature>(
            { new IHttpAccessFeature with
                member _.Public = api
                member _.Session = session })

    let private rememberParsed (value: PreparsedRequest) (context: HttpContext) : unit =
        context.Features.Set<IPreparsedAccessFeature>(
            { new IPreparsedAccessFeature with member _.Value = value })

    let private admission (api: PublicAccess) (required: Set<Capability>)
        (session: SessionContext) (context: HttpContext)
        : Task<Result<AccessStatus, HttpAccessFailure>> =
        api.Admit required session context.RequestAborted
        |> Prelude.TaskResult.bind (function
            | Response.Admitted status -> Task.FromResult(Ok status)
            | _ -> Task.FromResult(Error AccessError.AccessUnavailable))
        |> Prelude.TaskResult.mapError HttpAccessFailure.Domain

    let private admit (instance: InstanceId) (api: PublicAccess) (required: Set<Capability>)
        (session: SessionContext) (context: HttpContext) (next: RequestDelegate) : Task =
        task {
            let! outcome = admission api required session context
            match outcome with
            | Error failure -> do! HttpAccessResponse.failure instance required context failure
            | Ok _ ->
                remember api session context
                do! next.Invoke(context)
        }

    let private required (policy: ModelWorkPolicy) : Set<Capability> =
        match policy with
        | ModelWorkPolicy.ArchiveOnly -> Set.empty
        | ModelWorkPolicy.InvokeModel _ -> Set.singleton Capability.ModelInvoke

    let private available (availability: ModelAvailability) (policy: ModelWorkPolicy) : bool =
        match policy with
        | ModelWorkPolicy.ArchiveOnly -> true
        | ModelWorkPolicy.InvokeModel operation -> availability.IsAvailable operation

    let private runParsed (instance: InstanceId) (api: PublicAccess) (availability: ModelAvailability)
        (session: SessionContext) (value: PreparsedRequest) (context: HttpContext)
        (next: RequestDelegate) : Task =
        task {
            let capabilities = required value.Policy
            let! outcome = admission api capabilities session context
            match outcome with
            | Error failure -> do! HttpAccessResponse.failure instance capabilities context failure
            | Ok _ when not (available availability value.Policy) ->
                do! HttpAccessResponse.failure instance capabilities context HttpAccessFailure.ModelUnavailable
            | Ok _ ->
                remember api session context
                rememberParsed value context
                do! next.Invoke(context)
        }

    let private parseAndRun (instance: InstanceId) (api: PublicAccess) (availability: ModelAvailability)
        (session: SessionContext) (kind: InteractiveKind) (context: HttpContext)
        (next: RequestDelegate) : Task =
        task {
            let! authenticated = admission api Set.empty session context
            match authenticated with
            | Error failure -> do! HttpAccessResponse.failure instance Set.empty context failure
            | Ok _ ->
                let! parsed = HttpInteractiveInput.read kind context
                do! parsed |> Result.map (fun value ->
                        runParsed instance api availability session value context next)
                    |> Result.defaultWith (HttpAccessResponse.failure instance Set.empty context)
        }

    let private dispatch (instance: InstanceId) (api: PublicAccess) (availability: ModelAvailability)
        (classification: HttpAccessClassification) (session: SessionContext)
        (context: HttpContext) (next: RequestDelegate) : Task =
        match classification with
        | HttpAccessClassification.RequiredCaps capabilities when Set.isEmpty capabilities ->
            HttpAccessResponse.failure instance Set.empty context HttpAccessValues.unavailable
        | HttpAccessClassification.RequiredCaps capabilities ->
            admit instance api capabilities session context next
        | HttpAccessClassification.ParsedChatOrSearch kind ->
            parseAndRun instance api availability session kind context next
        | HttpAccessClassification.AnonymousArchiveRead
        | HttpAccessClassification.AccessProtocol _ -> admit instance api Set.empty session context next
        | HttpAccessClassification.McpIndependent -> next.Invoke(context)

    let private route (instance: InstanceId) (api: PublicAccess) (availability: ModelAvailability)
        (classification: HttpAccessClassification) (context: HttpContext)
        (next: RequestDelegate) : Task =
        match classification with
        | HttpAccessClassification.McpIndependent -> next.Invoke(context)
        | _ ->
            HttpAccessInput.session context
            |> Result.map (fun session -> dispatch instance api availability classification session context next)
            |> Result.defaultWith (HttpAccessResponse.failure instance Set.empty context)

    let private barrier (instance: InstanceId) (api: PublicAccess) (availability: ModelAvailability)
        (classification: HttpAccessClassification) (context: HttpContext)
        (next: RequestDelegate) : Task =
        task {
            let! ready = admission api Set.empty anonymous context
            do! ready |> Result.map (fun _ ->
                    route instance api availability classification context next)
                |> Result.defaultWith (HttpAccessResponse.failure instance Set.empty context)
        }

    let private classified (instance: InstanceId) (getPublic: unit -> PublicAccess option)
        (availability: ModelAvailability) (classification: HttpAccessClassification)
        (context: HttpContext) (next: RequestDelegate) : Task =
        HttpAccessResponse.secure context
        getPublic ()
        |> Option.map (fun api -> barrier instance api availability classification context next)
        |> Option.defaultWith (fun () ->
            HttpAccessResponse.failure instance Set.empty context HttpAccessValues.unavailable)

    let private rejectEndpoint (instance: InstanceId) (context: HttpContext) : Task =
        HttpAccessResponse.secure context
        HttpAccessResponse.failure instance Set.empty context HttpAccessValues.unavailable

    let private methodRejection (instance: InstanceId) (context: HttpContext) (selected: Endpoint) : Task =
        HttpAccessResponse.secure context
        match selected.RequestDelegate with
        | null -> rejectEndpoint instance context
        | handler -> handler.Invoke(context)

    let private endpoint (instance: InstanceId) (getPublic: unit -> PublicAccess option)
        (availability: ModelAvailability) (context: HttpContext)
        (next: RequestDelegate) (selected: Endpoint) : Task =
        let metadata = selected.Metadata.GetOrderedMetadata<HttpAccessClassification>()
        if metadata.Count = 1 && HttpEndpointValidation.validClassification metadata[0] then
            classified instance getPublic availability metadata[0] context next
        elif metadata.Count = 0 && selected.Metadata.Count = 0
             && selected.DisplayName = "405 HTTP Method Not Supported" then
            methodRejection instance context selected
        else rejectEndpoint instance context

    /// Attach after routing. Canonical resources must also be classified endpoints.
    let attach (app: WebApplication) (instance: InstanceId)
        (getPublic: unit -> PublicAccess option) (availability: ModelAvailability) : unit =
        app.Use(Func<HttpContext, RequestDelegate, Task>(fun context next ->
            match context.GetEndpoint() with
            | null ->
                HttpAccessResponse.secure context
                context.Response.StatusCode <- StatusCodes.Status404NotFound
                Task.CompletedTask
            | selected -> endpoint instance getPublic availability context next selected))
        |> ignore
