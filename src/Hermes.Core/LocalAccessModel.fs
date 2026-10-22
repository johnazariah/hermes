namespace Hermes.Core.LocalAccess

open System
open System.Security.Cryptography
open System.Text

[<RequireQualifiedAccess>]
type AccessError =
    | InvalidRequest
    | WrongInstance
    | InstanceNotReady
    | AccessUnavailable
    | PairingNotFound
    | PairingExpired
    | PairingConsumed
    | PairingConflict
    | InsufficientCapability
    | AccessCapacityReached
    | AccessRateLimited
    | InvalidPairingProof
    | AuthenticationRequired
    | InvalidCredential
    | CredentialExpired
    | SessionOriginMismatch
    | InternalError

module internal ModelResult =
    type Builder() =
        member _.Bind(value, continuation) = Result.bind continuation value
        member _.Return(value) = Ok value
        member _.ReturnFrom(value) = value

    let result = Builder()
    let require error condition = if condition then Ok () else Error error
    let redacted = "<redacted access value>"

module internal CanonicalBase64 =
    let encode (bytes: byte array) =
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let private alphabet character =
        (character >= 'A' && character <= 'Z')
        || (character >= 'a' && character <= 'z')
        || (character >= '0' && character <= '9')
        || character = '-' || character = '_'

    let private decode size (text: string) =
        let length = (size * 8 + 5) / 6
        if text.Length <> length || not (text |> Seq.forall alphabet) then
            Error AccessError.InvalidRequest
        else
            let padding = String('=', (4 - text.Length % 4) % 4)
            let bytes = Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + padding)
            if bytes.Length = size && encode bytes = text then Ok bytes
            else Error AccessError.InvalidRequest

    let read size (input: string | null) =
        input |> Option.ofObj |> Option.map (decode size)
        |> Option.defaultValue (Error AccessError.InvalidRequest)

    let sized size (bytes: byte array) =
        if bytes.Length = size then Ok(encode bytes)
        else Error AccessError.InvalidRequest

type InstanceId = private InstanceId of string

[<RequireQualifiedAccess>]
module InstanceId =
    let text (InstanceId value) = value
    let ofBytes bytes = CanonicalBase64.sized 16 bytes |> Result.map InstanceId
    let tryParse input = CanonicalBase64.read 16 input |> Result.map (CanonicalBase64.encode >> InstanceId)

type PairingId = private PairingId of string

[<RequireQualifiedAccess>]
module PairingId =
    let text (PairingId value) = value
    let ofBytes bytes = CanonicalBase64.sized 16 bytes |> Result.map PairingId
    let tryParse input = CanonicalBase64.read 16 input |> Result.map (CanonicalBase64.encode >> PairingId)

[<NoEquality; NoComparison; StructuredFormatDisplay("{SafeDisplay}")>]
type Verifier =
    private | Verifier of string
    member _.SafeDisplay = ModelResult.redacted
    override this.ToString() = this.SafeDisplay

[<RequireQualifiedAccess>]
module Verifier =
    let internal text (Verifier value) = value
    let ofBytes bytes = CanonicalBase64.sized 32 bytes |> Result.map Verifier
    let tryParse input = CanonicalBase64.read 32 input |> Result.map (CanonicalBase64.encode >> Verifier)

[<NoEquality; NoComparison; StructuredFormatDisplay("{SafeDisplay}")>]
type Challenge =
    private | Challenge of string
    member _.SafeDisplay = ModelResult.redacted
    override this.ToString() = this.SafeDisplay

[<RequireQualifiedAccess>]
module Challenge =
    let text (Challenge value) = value
    let tryParse (input: string | null) = CanonicalBase64.read 32 input |> Result.map (CanonicalBase64.encode >> Challenge)

    let ofVerifier verifier =
        verifier |> Verifier.text |> Encoding.UTF8.GetBytes |> SHA256.HashData
        |> CanonicalBase64.encode |> Challenge

    let private bytes (Challenge value) =
        value.Replace('-', '+').Replace('_', '/') + "=" |> Convert.FromBase64String

    let equal expected supplied =
        CryptographicOperations.FixedTimeEquals(
            ReadOnlySpan<byte>(bytes expected), ReadOnlySpan<byte>(bytes supplied))

    let verifies expected verifier = verifier |> ofVerifier |> equal expected

type DisplayCode = private DisplayCode of string

[<RequireQualifiedAccess>]
module DisplayCode =
    let private alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
    let text (DisplayCode value) = value

    let ofBytes (bytes: byte array) =
        if bytes.Length <> 12 then Error AccessError.InvalidRequest
        else
            bytes |> Array.map (fun value -> alphabet[int value &&& 31])
            |> Array.chunkBySize 4 |> Array.map (fun characters -> String(characters))
            |> String.concat "-" |> DisplayCode |> Ok

    let private valid (value: string) =
        value.Length = 14 && value[4] = '-' && value[9] = '-'
        && (value |> Seq.mapi (fun index character ->
                if index = 4 || index = 9 then character = '-'
                else alphabet.IndexOf(character) >= 0) |> Seq.forall id)

    let tryParse (input: string | null) =
        input |> Option.ofObj |> Option.filter valid |> Option.map (DisplayCode >> Ok)
        |> Option.defaultValue (Error AccessError.InvalidRequest)

type BrowserOrigin = private BrowserOrigin of string

[<RequireQualifiedAccess>]
module BrowserOrigin =
    let text (BrowserOrigin value) = value

    let private candidate (value: string) =
        match Uri.TryCreate(value, UriKind.Absolute) with
        | true, uri -> uri |> Option.ofObj |> Option.map (fun address -> {| Text = value; Uri = address |})
        | _ -> None

    let private permitted (uri: Uri) =
        let host = uri.Host.Trim([| '['; ']' |])
        (uri.Scheme = "http" || uri.Scheme = "https")
        && (host = "127.0.0.1" || host = "localhost" || host = "::1")
        && uri.UserInfo = "" && uri.AbsolutePath = "/" && uri.Query = "" && uri.Fragment = ""

    let tryParse (input: string | null) =
        input |> Option.ofObj |> Option.bind candidate
        |> Option.filter (fun value ->
            permitted value.Uri && value.Text = value.Uri.GetLeftPart(UriPartial.Authority))
        |> Option.map (fun value -> Ok(BrowserOrigin value.Text))
        |> Option.defaultValue (Error AccessError.InvalidRequest)

type ApiAudience = private ApiAudience of int

[<RequireQualifiedAccess>]
module ApiAudience =
    let create port =
        if port >= 1 && port <= 65535 then Ok(ApiAudience port)
        else Error AccessError.InvalidRequest

    let port (ApiAudience value) = value
    let text audience = $"http://127.0.0.1:{port audience}"

    let internal browserOrigins audience =
        [ "127.0.0.1"; "localhost"; "[::1]" ]
        |> List.map (fun host ->
            let uri = Uri($"http://{host}:{port audience}")
            BrowserOrigin(uri.GetLeftPart(UriPartial.Authority)))
        |> Set.ofList

type Identity =
    { InstanceId: InstanceId
      ProcessId: int
      ProcessStartTimeUtc: DateTimeOffset
      ApiOrigin: ApiAudience
      ConfigKey: string }

[<RequireQualifiedAccess>]
module Identity =
    let private validKey (value: string) =
        value.Length = 64
        && (value |> Seq.forall (fun character ->
            (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))

    let create (instanceId: InstanceId) (pid: int) (started: DateTimeOffset)
        (audience: ApiAudience) (configKey: string | null) : Result<Identity, AccessError> =
        configKey |> Option.ofObj |> Option.filter validKey
        |> Option.filter (fun _ -> pid > 0 && started.Offset = TimeSpan.Zero)
        |> Option.map (fun key ->
            Ok { InstanceId = instanceId; ProcessId = pid; ProcessStartTimeUtc = started
                 ApiOrigin = audience; ConfigKey = key })
        |> Option.defaultValue (Error AccessError.InvalidRequest)

    let internal validate (identity: Identity) : Result<Identity, AccessError> =
        create identity.InstanceId identity.ProcessId identity.ProcessStartTimeUtc
            identity.ApiOrigin identity.ConfigKey

[<RequireQualifiedAccess>]
type Capability = ConfigRead | ModelInvoke | Mutate

[<RequireQualifiedAccess>]
module Capability =
    let name = function
        | Capability.ConfigRead -> "config.read"
        | Capability.ModelInvoke -> "model.invoke"
        | Capability.Mutate -> "mutate"

[<RequireQualifiedAccess>]
type EffectiveCapability = ArchiveRead | Authority of Capability

[<RequireQualifiedAccess>]
module EffectiveCapability =
    let name = function
        | EffectiveCapability.ArchiveRead -> "archive.read"
        | EffectiveCapability.Authority capability -> Capability.name capability

    let internal ofGrant capabilities =
        capabilities |> Set.map EffectiveCapability.Authority
        |> Set.add EffectiveCapability.ArchiveRead

[<NoEquality; NoComparison; StructuredFormatDisplay("{SafeDisplay}")>]
type SessionSecret =
    private | SessionSecret of string
    member _.SafeDisplay = ModelResult.redacted
    override this.ToString() = this.SafeDisplay

[<RequireQualifiedAccess>]
module SessionSecret =
    let internal ofBytes bytes = CanonicalBase64.sized 32 bytes |> Result.map SessionSecret
    let internal text (SessionSecret value) = value

[<NoEquality; NoComparison; StructuredFormatDisplay("{SafeDisplay}")>]
type SessionCredential =
    private | SessionCredential of InstanceId * string
    member _.SafeDisplay = ModelResult.redacted
    override this.ToString() = this.SafeDisplay

[<RequireQualifiedAccess>]
module SessionCredential =
    let instanceId (SessionCredential(instance, _)) = instance
    let text (SessionCredential(_, value)) = value

    let internal create instance secret =
        let value = $"h1.{InstanceId.text instance}.{SessionSecret.text secret}"
        SessionCredential(instance, value)

    let private parse (value: string) =
        match value.Split([| '.' |]) with
        | [| "h1"; instance; secret |] when value.Length = 69 ->
            ModelResult.result {
                let! identifier = InstanceId.tryParse instance
                let! _bytes = CanonicalBase64.read 32 secret
                return SessionCredential(identifier, value)
            }
        | _ -> Error AccessError.InvalidCredential

    let tryParse (input: string | null) =
        input |> Option.ofObj |> Option.map parse
        |> Option.defaultValue (Error AccessError.InvalidCredential)
        |> Result.mapError (fun _ -> AccessError.InvalidCredential)

type internal CredentialDigest = private CredentialDigest of string

module internal CredentialDigest =
    let create credential =
        credential |> SessionCredential.text |> Encoding.UTF8.GetBytes |> SHA256.HashData
        |> Convert.ToHexString |> fun value -> CredentialDigest(value.ToLowerInvariant())

    let equal (CredentialDigest expected) (CredentialDigest supplied) =
        CryptographicOperations.FixedTimeEquals(
            ReadOnlySpan<byte>(Convert.FromHexString expected),
            ReadOnlySpan<byte>(Convert.FromHexString supplied))

type Moment = { Elapsed: TimeSpan; Utc: DateTimeOffset }
type Clock = { Read: unit -> Moment }
type Entropy = { Bytes: int -> byte array }

[<NoEquality; NoComparison; StructuredFormatDisplay("{SafeDisplay}")>]
type Evidence =
    internal
        { Now: Moment
          PairingId: PairingId
          UserCode: DisplayCode
          SessionSecret: SessionSecret }
    member _.SafeDisplay = ModelResult.redacted
    override this.ToString() = this.SafeDisplay

[<RequireQualifiedAccess>]
module Evidence =
    /// Sample under the eventual interpreter's atomic commit boundary.
    /// Domain.transition itself performs no clock or entropy calls.
    let capture clock entropy =
        ModelResult.result {
            let now = clock.Read()
            do! ModelResult.require AccessError.InternalError
                    (now.Elapsed >= TimeSpan.Zero && now.Utc.Offset = TimeSpan.Zero)
            let! pairing = entropy.Bytes 16 |> PairingId.ofBytes
            let! code = entropy.Bytes 12 |> DisplayCode.ofBytes
            let! secret = entropy.Bytes 32 |> SessionSecret.ofBytes
            return { Now = now; PairingId = pairing; UserCode = code; SessionSecret = secret }
        }
        |> Result.mapError (fun _ -> AccessError.InternalError)

[<RequireQualifiedAccess>]
type AccessPhase = Starting | Ready | Stopping

[<RequireQualifiedAccess>]
type PairingPhase = Pending | Approved

[<RequireQualifiedAccess>]
type AuthenticationState = Anonymous | Authenticated

[<NoEquality; NoComparison>]
type CreateRequest =
    { InstanceId: InstanceId
      Challenge: Challenge
      RequestedCapabilities: Set<Capability>
      BrowserOrigins: BrowserOrigin list }

[<NoEquality; NoComparison>]
type ApprovalRequest =
    { InstanceId: InstanceId
      PairingId: PairingId
      UserCode: DisplayCode
      Challenge: Challenge
      Capabilities: Set<Capability> }

[<NoEquality; NoComparison>]
type ExchangeRequest =
    { InstanceId: InstanceId
      PairingId: PairingId
      Verifier: Verifier
      BrowserOrigin: BrowserOrigin option }

[<NoEquality; NoComparison; RequireQualifiedAccess>]
type CredentialInput = Absent | Present of SessionCredential | Invalid

[<NoEquality; NoComparison>]
type SessionContext =
    { Credential: CredentialInput
      BrowserOrigin: BrowserOrigin option }

type PairingReceipt =
    { InstanceId: InstanceId
      PairingId: PairingId
      UserCode: DisplayCode
      BrowserOrigin: BrowserOrigin
      ApiOrigin: ApiAudience
      RequestedCapabilities: Set<Capability>
      ExpiresAt: DateTimeOffset }

[<NoEquality; NoComparison>]
type PairingView =
    { PairingId: PairingId
      UserCode: DisplayCode
      Challenge: Challenge
      BrowserOrigin: BrowserOrigin
      ApiOrigin: ApiAudience
      RequestedCapabilities: Set<Capability>
      CreatedAt: DateTimeOffset
      ExpiresAt: DateTimeOffset
      State: PairingPhase }

type ApprovalView =
    { PairingId: PairingId
      Capabilities: Set<Capability>
      ExpiresAt: DateTimeOffset }

[<NoEquality; NoComparison>]
type IssuedSession =
    { InstanceId: InstanceId
      Credential: SessionCredential
      Capabilities: Set<Capability>
      ExpiresAt: DateTimeOffset }

type AccessStatus =
    { Identity: Identity
      State: AuthenticationState
      Capabilities: Set<EffectiveCapability>
      ExpiresAt: DateTimeOffset option }

[<RequireQualifiedAccess>]
type InvalidExchange = MalformedRequest | NoncanonicalProof

[<NoEquality; NoComparison; RequireQualifiedAccess>]
type Command =
    | EnterReady
    | Stop
    | ControlStatus of expectedInstance: InstanceId option
    | Create of CreateRequest
    | Resolve of instance: InstanceId * userCode: DisplayCode
    | Approve of ApprovalRequest
    | Exchange of ExchangeRequest
    | RejectExchange of InvalidExchange
    | Status of SessionContext
    | Revoke of SessionContext
    | Admit of required: Set<Capability> * context: SessionContext

[<NoEquality; NoComparison; RequireQualifiedAccess>]
type Response =
    | PhaseChanged of AccessPhase
    | ControlStatus of Identity * AccessPhase * Set<Capability>
    | PairingCreated of PairingReceipt
    | PairingResolved of PairingView
    | PairingApproved of ApprovalView
    | ExchangePending of expiresAt: DateTimeOffset
    | SessionIssued of IssuedSession
    | Status of AccessStatus
    | Revoked
    | Admitted of AccessStatus
