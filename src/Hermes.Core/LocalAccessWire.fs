namespace Hermes.Core.LocalAccessWire

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json

[<RequireQualifiedAccess>]
type Phase = Starting | Ready | Stopping

[<RequireQualifiedAccess>]
type PairingState = Pending | Approved

[<RequireQualifiedAccess>]
type ErrorCode =
    | InvalidRequest
    | UnsupportedVersion
    | WrongInstance
    | InstanceNotReady
    | PairingNotFound
    | PairingExpired
    | PairingConsumed
    | PairingConflict
    | InsufficientCapability
    | AccessCapacityReached
    | InternalError

[<RequireQualifiedAccess>]
type CodecFailure =
    | Malformed
    | UnsupportedVersion
    | CorrelationMismatch
    | IdentityMismatch

type ApprovalPayload =
    { PairingId: string
      UserCode: string
      Challenge: string
      Capabilities: string list }

[<RequireQualifiedAccess>]
type Operation =
    | Status
    | Resolve of userCode: string
    | Approve of ApprovalPayload

type Request =
    { RequestId: string
      ExpectedInstanceId: string option
      Operation: Operation }

type Identity =
    { InstanceId: string
      ProcessId: int
      ProcessStartTimeUtc: DateTimeOffset
      Origin: string
      ConfigKey: string }

/// Instance pinning is supplied separately by Request.ExpectedInstanceId.
type PeerExpectation =
    { ProcessId: int
      ProcessStartTimeUtc: DateTimeOffset
      Origin: string
      ConfigKey: string }

type StatusResult =
    { Phase: Phase
      Capabilities: string list }

type PairingResult =
    { PairingId: string
      UserCode: string
      Challenge: string
      BrowserOrigin: string
      ApiOrigin: string
      RequestedCapabilities: string list
      CreatedAt: DateTimeOffset
      ExpiresAt: DateTimeOffset
      State: PairingState }

type ApprovalResult =
    { PairingId: string
      Capabilities: string list
      ExpiresAt: DateTimeOffset }

[<RequireQualifiedAccess>]
type Reply =
    | Status of StatusResult
    | Resolved of PairingResult
    | Approved of ApprovalResult

type Response =
    { RequestId: string
      Instance: Identity
      Outcome: Result<Reply, ErrorCode> }

[<RequireQualifiedAccess>]
module Phase =
    let name = function
        | Phase.Starting -> "starting"
        | Phase.Ready -> "ready"
        | Phase.Stopping -> "stopping"

    let tryParse (text: string) =
        [ Phase.Starting; Phase.Ready; Phase.Stopping ]
        |> List.tryFind (fun phase -> name phase = text)

[<RequireQualifiedAccess>]
module PairingState =
    let name = function
        | PairingState.Pending -> "pending"
        | PairingState.Approved -> "approved"

    let tryParse (text: string) =
        [ PairingState.Pending; PairingState.Approved ]
        |> List.tryFind (fun state -> name state = text)

[<RequireQualifiedAccess>]
module ErrorCode =
    let name = function
        | ErrorCode.InvalidRequest -> "invalid_request"
        | ErrorCode.UnsupportedVersion -> "unsupported_version"
        | ErrorCode.WrongInstance -> "wrong_instance"
        | ErrorCode.InstanceNotReady -> "instance_not_ready"
        | ErrorCode.PairingNotFound -> "pairing_not_found"
        | ErrorCode.PairingExpired -> "pairing_expired"
        | ErrorCode.PairingConsumed -> "pairing_consumed"
        | ErrorCode.PairingConflict -> "pairing_conflict"
        | ErrorCode.InsufficientCapability -> "insufficient_capability"
        | ErrorCode.AccessCapacityReached -> "access_capacity_reached"
        | ErrorCode.InternalError -> "internal_error"

    let private values =
        [ ErrorCode.InvalidRequest; ErrorCode.UnsupportedVersion; ErrorCode.WrongInstance
          ErrorCode.InstanceNotReady; ErrorCode.PairingNotFound; ErrorCode.PairingExpired
          ErrorCode.PairingConsumed; ErrorCode.PairingConflict; ErrorCode.InsufficientCapability
          ErrorCode.AccessCapacityReached; ErrorCode.InternalError ]

    let tryParse (text: string) =
        values |> List.tryFind (fun code -> name code = text)

    // ADR 32 fixes codes but does not enumerate pipe message wording.
    // Never serialize exception text or retain a peer-supplied message.
    let message (_: ErrorCode) = "Owner control request failed."

module internal OperationNames =
    let status = "status"
    let resolve = "resolve"
    let approve = "approve"

module internal Shape =
    exception Rejected of CodecFailure

    let reject (failure: CodecFailure) : 'T = raise (Rejected failure)
    let invalid () : 'T = reject CodecFailure.Malformed
    let require (condition: bool) = if not condition then invalid ()

    let boundedText (maximum: int) (text: string | null) : string =
        match text with
        | null -> invalid ()
        | value when value.Length > 0 && value.Length <= maximum -> value
        | _ -> invalid ()

    let field (name: string) (value: JsonElement) = value.GetProperty name

    let stringValue (maximum: int) (value: JsonElement) =
        require (value.ValueKind = JsonValueKind.String)
        value.GetString() |> boundedText maximum

    let text (name: string) (value: JsonElement) = value |> field name |> stringValue 256

    let exact (names: string list) (value: JsonElement) =
        require (value.ValueKind = JsonValueKind.Object)
        let actual = value.EnumerateObject() |> Seq.map (fun property -> property.Name) |> Seq.toList
        require (actual.Length = names.Length && Set.ofList actual = Set.ofList names)

    let integer (name: string) (value: JsonElement) =
        let number = field name value
        require (number.ValueKind = JsonValueKind.Number)
        match number.TryGetInt32() with
        | true, result -> result
        | _ -> invalid ()

    let version (value: JsonElement) =
        if integer "version" value <> 1 then reject CodecFailure.UnsupportedVersion

    let encode (bytes: byte array) =
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let proofText (size: int) (candidate: string) =
        let candidate = boundedText 43 candidate
        require (candidate.Length = (if size = 16 then 22 else 43))
        require (candidate |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit(c) || c = '-' || c = '_'))
        let padding = String('=', (4 - candidate.Length % 4) % 4)
        let decoded = Convert.FromBase64String(candidate.Replace('-', '+').Replace('_', '/') + padding)
        require (decoded.Length = size && encode decoded = candidate)
        candidate

    let proof (size: int) (name: string) (value: JsonElement) =
        value |> field name |> stringValue 43 |> proofText size

    let codeText (candidate: string) =
        let candidate = boundedText 14 candidate
        let alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
        require (candidate.Length = 14 && candidate.[4] = '-' && candidate.[9] = '-')
        candidate
        |> Seq.mapi (fun index c -> index = 4 || index = 9 || alphabet.IndexOf(c) >= 0)
        |> Seq.forall id
        |> require
        candidate

    let code (name: string) (value: JsonElement) = text name value |> codeText

    let utc (value: DateTimeOffset) =
        require (value.Offset = TimeSpan.Zero)
        value

    let timestamp (name: string) (value: JsonElement) =
        let timestamp = field name value
        require (timestamp.ValueKind = JsonValueKind.String)
        match timestamp.TryGetDateTimeOffset() with
        | true, result -> utc result
        | _ -> invalid ()

    let keyText (candidate: string) =
        let candidate = boundedText 64 candidate
        require (candidate.Length = 64)
        require (candidate |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
        candidate

    let capabilityTexts (allowArchive: bool) (values: string list) =
        let archive = "archive.read"
        let known = [ archive; "config.read"; "model.invoke"; "mutate" ]
        require (values.Length <= 4 && values |> List.forall (fun value -> List.contains value known))
        require (allowArchive || not (List.contains archive values))
        require (values = List.distinct values)
        require (values = (values |> List.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))))
        values

    let capabilities (allowArchive: bool) (name: string) (value: JsonElement) =
        let array = field name value
        require (array.ValueKind = JsonValueKind.Array && array.GetArrayLength() <= 4)
        array.EnumerateArray()
        |> Seq.map (stringValue 256)
        |> Seq.toList
        |> capabilityTexts allowArchive

    let rec private unique (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.Object ->
            value.EnumerateObject()
            |> Seq.fold (fun seen property ->
                require (not (Set.contains property.Name seen))
                unique property.Value
                Set.add property.Name seen) Set.empty
            |> ignore
        | JsonValueKind.Array -> value.EnumerateArray() |> Seq.iter unique
        | _ -> ()

    let attempt (action: unit -> 'T) : Result<'T, CodecFailure> =
        try Ok(action ())
        with
        | Rejected failure -> Error failure
        | :? JsonException
        | :? DecoderFallbackException
        | :? FormatException
        | :? InvalidOperationException -> Error CodecFailure.Malformed

    let private boundedBytes (bytes: byte array | null) : byte array =
        match bytes with
        | null -> invalid ()
        | value when value.Length >= 1 && value.Length <= 8192 -> value
        | _ -> invalid ()

    let read (bytes: byte array | null) (reader: JsonElement -> 'T) : Result<'T, CodecFailure> =
        attempt (fun () ->
            let bytes = boundedBytes bytes
            let text = UTF8Encoding(false, true).GetString bytes
            require (not (text.StartsWith("\uFEFF", StringComparison.Ordinal)))
            let options =
                JsonDocumentOptions(MaxDepth = 8, AllowTrailingCommas = false,
                                    CommentHandling = JsonCommentHandling.Disallow)
            use document = JsonDocument.Parse(text, options)
            unique document.RootElement
            reader document.RootElement)

module internal ReadRequest =
    let private approval (value: JsonElement) : ApprovalPayload =
        Shape.exact [ "pairingId"; "userCode"; "challenge"; "capabilities" ] value
        { PairingId = Shape.proof 16 "pairingId" value
          UserCode = Shape.code "userCode" value
          Challenge = Shape.proof 32 "challenge" value
          Capabilities = Shape.capabilities false "capabilities" value }

    let private operation (value: JsonElement) : Operation =
        let payload = Shape.field "payload" value
        let name = Shape.text "operation" value
        if name = OperationNames.status then
            Shape.exact [] payload
            Operation.Status
        elif name = OperationNames.resolve then
            Shape.exact [ "userCode" ] payload
            Operation.Resolve(Shape.code "userCode" payload)
        elif name = OperationNames.approve then
            Operation.Approve(approval payload)
        else
            Shape.invalid ()

    let private expectedInstance (value: JsonElement) =
        let expected = Shape.field "expectedInstanceId" value
        if expected.ValueKind = JsonValueKind.Null then None
        else Some(expected |> Shape.stringValue 22 |> Shape.proofText 16)

    let read (value: JsonElement) : Request =
        Shape.exact [ "version"; "requestId"; "expectedInstanceId"; "operation"; "payload" ] value
        Shape.version value
        let operation = operation value
        let expected = expectedInstance value
        let needsPin =
            match operation with
            | Operation.Status -> false
            | Operation.Resolve _
            | Operation.Approve _ -> true
        Shape.require (not needsPin || Option.isSome expected)
        { RequestId = Shape.proof 16 "requestId" value
          ExpectedInstanceId = expected
          Operation = operation }

module internal ReadResponse =
    let private identity (value: JsonElement) : Identity =
        Shape.exact [ "instanceId"; "processId"; "processStartTimeUtc"; "origin"; "configKey" ] value
        let processId = Shape.integer "processId" value
        Shape.require (processId > 0)
        { InstanceId = Shape.proof 16 "instanceId" value
          ProcessId = processId
          ProcessStartTimeUtc = Shape.timestamp "processStartTimeUtc" value
          Origin = Shape.text "origin" value
          ConfigKey = Shape.text "configKey" value |> Shape.keyText }

    let private status (value: JsonElement) : StatusResult =
        Shape.exact [ "phase"; "capabilities" ] value
        { Phase = Shape.text "phase" value |> Phase.tryParse |> Option.defaultWith Shape.invalid
          Capabilities = Shape.capabilities true "capabilities" value }

    let private pairing (value: JsonElement) : PairingResult =
        Shape.exact
            [ "pairingId"; "userCode"; "challenge"; "browserOrigin"; "apiOrigin"
              "requestedCapabilities"; "createdAt"; "expiresAt"; "state" ] value
        let created = Shape.timestamp "createdAt" value
        let expires = Shape.timestamp "expiresAt" value
        Shape.require (created < expires)
        { PairingId = Shape.proof 16 "pairingId" value
          UserCode = Shape.code "userCode" value
          Challenge = Shape.proof 32 "challenge" value
          BrowserOrigin = Shape.text "browserOrigin" value
          ApiOrigin = Shape.text "apiOrigin" value
          RequestedCapabilities = Shape.capabilities false "requestedCapabilities" value
          CreatedAt = created
          ExpiresAt = expires
          State = Shape.text "state" value |> PairingState.tryParse |> Option.defaultWith Shape.invalid }

    let private approval (value: JsonElement) : ApprovalResult =
        Shape.exact [ "pairingId"; "state"; "capabilities"; "expiresAt" ] value
        Shape.require (Shape.text "state" value = PairingState.name PairingState.Approved)
        { PairingId = Shape.proof 16 "pairingId" value
          Capabilities = Shape.capabilities false "capabilities" value
          ExpiresAt = Shape.timestamp "expiresAt" value }

    let private reply (operation: Operation) (value: JsonElement) : Reply =
        match operation with
        | Operation.Status -> Reply.Status(status value)
        | Operation.Resolve _ -> Reply.Resolved(pairing value)
        | Operation.Approve _ -> Reply.Approved(approval value)

    let private error (value: JsonElement) : ErrorCode =
        Shape.exact [ "code"; "message" ] value
        value |> Shape.field "message" |> Shape.stringValue 1024 |> ignore
        Shape.text "code" value |> ErrorCode.tryParse |> Option.defaultWith Shape.invalid

    let private outcome (operation: Operation) (value: JsonElement) : Result<Reply, ErrorCode> =
        let result = Shape.field "result" value
        let failure = Shape.field "error" value
        match (Shape.field "ok" value).ValueKind with
        | JsonValueKind.True ->
            Shape.require (failure.ValueKind = JsonValueKind.Null)
            Ok(reply operation result)
        | JsonValueKind.False ->
            Shape.require (result.ValueKind = JsonValueKind.Null)
            Error(error failure)
        | _ -> Shape.invalid ()

    let private envelope (request: Request) (value: JsonElement) : Response =
        Shape.exact [ "version"; "requestId"; "instance"; "ok"; "result"; "error" ] value
        Shape.version value
        let requestId = Shape.proof 16 "requestId" value
        if requestId <> request.RequestId then Shape.reject CodecFailure.CorrelationMismatch
        let instance = value |> Shape.field "instance" |> identity
        { RequestId = requestId
          Instance = instance
          Outcome = outcome request.Operation value }

    let read (request: Request) (value: JsonElement) : Response =
        let response = envelope request value
        request.ExpectedInstanceId
        |> Option.iter (fun expected ->
            if response.Instance.InstanceId <> expected then Shape.reject CodecFailure.IdentityMismatch)
        response

    /// A stale pin may be rejected by the actual runtime, but cannot produce success.
    /// This shares the complete grammar with client decoding, not its peer-admission rule.
    let readOutbound (request: Request) (value: JsonElement) : Response =
        let response = envelope request value
        match request.ExpectedInstanceId with
        | Some expected when expected <> response.Instance.InstanceId ->
            if response.Outcome <> Error ErrorCode.WrongInstance then
                Shape.reject CodecFailure.IdentityMismatch
        | Some _ when response.Outcome = Error ErrorCode.WrongInstance -> Shape.invalid ()
        | _ -> ()
        response

module internal WriteRequest =
    type Payload =
        { Name: string
          Value: JsonElement }

    let private approval (value: ApprovalPayload) =
        JsonSerializer.SerializeToElement
            {| pairingId = Shape.proofText 16 value.PairingId
               userCode = Shape.codeText value.UserCode
               challenge = Shape.proofText 32 value.Challenge
               capabilities = value.Capabilities |> Shape.capabilityTexts false |> List.toArray |}

    let private payload (operation: Operation) : Payload =
        match operation with
        | Operation.Status ->
            { Name = OperationNames.status
              Value = JsonSerializer.SerializeToElement {| |} }
        | Operation.Resolve code ->
            { Name = OperationNames.resolve
              Value = JsonSerializer.SerializeToElement {| userCode = Shape.codeText code |} }
        | Operation.Approve value ->
            { Name = OperationNames.approve; Value = approval value }

    let bytes (request: Request) =
        let operation = payload request.Operation
        let expected = request.ExpectedInstanceId |> Option.map (Shape.proofText 16) |> Option.toObj
        JsonSerializer.SerializeToUtf8Bytes
            {| version = 1
               requestId = Shape.proofText 16 request.RequestId
               expectedInstanceId = expected
               operation = operation.Name
               payload = operation.Value |}

module internal WriteResponse =
    type Outcome =
        { Success: bool
          Result: JsonElement
          Error: JsonElement }

    let private nullElement () : JsonElement =
        JsonSerializer.SerializeToElement<string | null>(null)

    let private identity (value: Identity) =
        Shape.require (value.ProcessId > 0)
        JsonSerializer.SerializeToElement
            {| instanceId = Shape.proofText 16 value.InstanceId
               processId = value.ProcessId
               processStartTimeUtc = Shape.utc value.ProcessStartTimeUtc
               origin = Shape.boundedText 256 value.Origin
               configKey = Shape.keyText value.ConfigKey |}

    let private status (value: StatusResult) =
        JsonSerializer.SerializeToElement
            {| phase = Phase.name value.Phase
               capabilities = value.Capabilities |> Shape.capabilityTexts true |> List.toArray |}

    let private pairing (value: PairingResult) =
        Shape.require (value.CreatedAt < value.ExpiresAt)
        JsonSerializer.SerializeToElement
            {| pairingId = Shape.proofText 16 value.PairingId
               userCode = Shape.codeText value.UserCode
               challenge = Shape.proofText 32 value.Challenge
               browserOrigin = Shape.boundedText 256 value.BrowserOrigin
               apiOrigin = Shape.boundedText 256 value.ApiOrigin
               requestedCapabilities = value.RequestedCapabilities |> Shape.capabilityTexts false |> List.toArray
               createdAt = Shape.utc value.CreatedAt
               expiresAt = Shape.utc value.ExpiresAt
               state = PairingState.name value.State |}

    let private approval (value: ApprovalResult) =
        JsonSerializer.SerializeToElement
            {| pairingId = Shape.proofText 16 value.PairingId
               state = PairingState.name PairingState.Approved
               capabilities = value.Capabilities |> Shape.capabilityTexts false |> List.toArray
               expiresAt = Shape.utc value.ExpiresAt |}

    let private reply (value: Reply) : JsonElement =
        match value with
        | Reply.Status value -> status value
        | Reply.Resolved value -> pairing value
        | Reply.Approved value -> approval value

    let private outcome (value: Result<Reply, ErrorCode>) : Outcome =
        value
        |> Result.map (fun (result: Reply) ->
            { Success = true
              Result = reply result
              Error = nullElement () })
        |> Result.defaultWith (fun (code: ErrorCode) ->
            { Success = false
              Result = nullElement ()
              Error = JsonSerializer.SerializeToElement
                  {| code = ErrorCode.name code; message = ErrorCode.message code |} })

    let bytes (response: Response) =
        let result = outcome response.Outcome
        JsonSerializer.SerializeToUtf8Bytes
            {| version = 1
               requestId = Shape.proofText 16 response.RequestId
               instance = identity response.Instance
               ok = result.Success
               result = result.Result
               error = result.Error |}

[<RequireQualifiedAccess>]
module Codec =
    let newRequestId () = RandomNumberGenerator.GetBytes(16) |> Shape.encode

    let decodeRequest (bytes: byte array | null) : Result<Request, CodecFailure> =
        Shape.read bytes ReadRequest.read

    let private samePeer (peer: PeerExpectation) (instance: Identity) =
        instance.ProcessId = peer.ProcessId
        && instance.ProcessStartTimeUtc = peer.ProcessStartTimeUtc
        && instance.Origin = peer.Origin
        && instance.ConfigKey = peer.ConfigKey

    /// Peer assertions must come from the selected target and an independently owned identity.
    let decodeResponse
        (request: Request)
        (peer: PeerExpectation)
        (bytes: byte array | null)
        : Result<Response, CodecFailure> =
        Shape.read bytes (fun root ->
            let response = ReadResponse.read request root
            if not (samePeer peer response.Instance) then Shape.reject CodecFailure.IdentityMismatch
            response)

    let encodeRequest (request: Request) : Result<byte array, CodecFailure> =
        Shape.attempt (fun () -> WriteRequest.bytes request)
        |> Result.bind (fun bytes ->
            decodeRequest bytes
            |> Result.bind (fun parsed ->
                if parsed = request then Ok bytes else Error CodecFailure.Malformed))

    /// Discovery is restricted to canonical status requests without an instance pin.
    let encodeDiscoveryRequest (request: Request) : Result<byte array, CodecFailure> =
        if request.Operation <> Operation.Status || Option.isSome request.ExpectedInstanceId then
            Error CodecFailure.Malformed
        else
            encodeRequest request

    /// Decode only bytes received over the selected OS-owner-validated connection.
    /// PID/start are peer assertions discovered here, not kernel-attested identities.
    /// Subsequent requests must use decodeResponse with the complete pinned identity.
    let decodeDiscoveryResponse
        (request: Request)
        (target: Hermes.Core.OwnerControlTarget.Target)
        (bytes: byte array | null)
        : Result<Response, CodecFailure> =
        encodeDiscoveryRequest request
        |> Result.bind (fun _ ->
            Shape.read bytes (fun root ->
                let response = ReadResponse.read request root
                if response.Instance.ConfigKey <> target.ConfigKey
                   || response.Instance.Origin <> target.ApiOrigin then
                    Shape.reject CodecFailure.IdentityMismatch
                response))

    let encodeResponse (request: Request) (response: Response) : Result<byte array, CodecFailure> =
        encodeRequest request
        |> Result.bind (fun _ -> Shape.attempt (fun () -> WriteResponse.bytes response))
        |> Result.bind (fun bytes ->
            Shape.read bytes (ReadResponse.readOutbound request)
            |> Result.bind (fun parsed ->
                if parsed = response then Ok bytes else Error CodecFailure.Malformed))
