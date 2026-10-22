namespace Hermes.Service

open System
open System.ComponentModel
open System.Diagnostics
open System.Globalization
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Hermes.Core
open Hermes.Core.LocalAccessWire
open Hermes.Core.OwnerControlPipe

[<RequireQualifiedAccess>]
module OwnerUiInput =
    [<Literal>]
    let OpenName = "open-ui"
    [<Literal>]
    let PairName = "pair-ui"
    [<Literal>]
    let DirectoryFlag = "--config-dir"
    [<Literal>]
    let PortFlag = "--port"
    [<Literal>]
    let Usage = "Usage: open-ui [--config-dir ABS] [--port N] | pair-ui CODE [--config-dir ABS] [--port N]"

    [<RequireQualifiedAccess>]
    type Mode = Open | Pair of string

    [<RequireQualifiedAccess>]
    type Failure = Rejected | Cancelled

    type Options =
        { Mode: Mode
          ConfigDirectory: string option
          Port: int option }

    type Selection =
        { Mode: Mode
          Target: OwnerControlTarget.Target }

    type Settings =
        { ConfigDirectory: unit -> string
          Port: unit -> string | null }

    type Effects =
        { Client: OwnerControlTarget.Target -> ClientOnly
          Status: OwnerControlTarget.Target -> CancellationToken -> Task<Result<byte array, Failure>>
          ReadLine: CancellationToken -> Task<string option>
          Write: string -> unit
          Browser: Uri -> CancellationToken -> Task<Result<unit, Failure>>
          UtcNow: unit -> DateTimeOffset
          Elapsed: unit -> TimeSpan }

    type Pin =
        { Target: OwnerControlTarget.Target
          Client: ClientOnly
          Identity: Identity
          Capabilities: string list }

    type Route = Service | Command of Result<Options, unit>

    let code (value: string) =
        Hermes.Core.LocalAccess.DisplayCode.tryParse value
        |> Result.map Hermes.Core.LocalAccess.DisplayCode.text
        |> Result.mapError (fun _ -> ())

    let private port (value: string) =
        match Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, parsed when parsed > 0 && parsed <= 65535 -> Ok parsed
        | _ -> Error ()

    let rec private walk (state: Options) = function
        | [] -> Ok state
        | DirectoryFlag :: value :: rest when Option.isNone state.ConfigDirectory ->
            walk { state with ConfigDirectory = Some value } rest
        | PortFlag :: value :: rest when Option.isNone state.Port ->
            port value |> Result.bind (fun selected -> walk { state with Port = Some selected } rest)
        | _ -> Error ()

    let private parse (mode: Mode) (arguments: string list) =
        if List.length arguments > 4
           || arguments |> List.exists (fun value -> value.Length > 4096) then Error ()
        else walk { Mode = mode; ConfigDirectory = None; Port = None } arguments

    let route (arguments: string array) =
        let tokens = Array.toList arguments
        let reserved =
            tokens |> List.exists (fun value ->
                value = OpenName || value = PairName || value = DirectoryFlag)
        match tokens with
        | OpenName :: rest -> Command(parse Mode.Open rest)
        | PairName :: displayed :: rest ->
            Command(code displayed |> Result.bind (fun parsed -> parse (Mode.Pair parsed) rest))
        | head :: _ when reserved
                         || head.StartsWith(OpenName, StringComparison.OrdinalIgnoreCase)
                         || head.StartsWith(PairName, StringComparison.OrdinalIgnoreCase) ->
            Command(Error ())
        | _ -> Service

    let private environmentPort (settings: Settings) () =
        settings.Port()
        |> ServicePolicy.selectPort
        |> Result.mapError (fun _ -> ())

    let select (settings: Settings) (options: Options) : Result<Selection, unit> =
        options.Port |> Option.map Ok |> Option.defaultWith (environmentPort settings)
        |> Result.bind (fun selectedPort ->
            let directory = options.ConfigDirectory |> Option.defaultWith settings.ConfigDirectory
            OwnerControlTarget.tryCreate directory selectedPort
            |> Result.mapError (fun _ -> ())
            |> Result.map (fun target -> { Mode = options.Mode; Target = target }))

module UiInput = OwnerUiInput

[<RequireQualifiedAccess>]
module OwnerUiStatus =

    exception private Invalid
    let private reject () : 'T = raise Invalid
    let private require condition = if not condition then reject ()

    let private fields =
        [ "version"; "instanceId"; "processId"; "processStartTimeUtc"; "origin"
          "state"; "capabilities"; "expiresAt"; "pairingMethod" ]

    let private exact (value: JsonElement) =
        require (value.ValueKind = JsonValueKind.Object)
        let actual = value.EnumerateObject() |> Seq.map (fun item -> item.Name) |> Seq.toList
        require (actual.Length = fields.Length && Set.ofList actual = Set.ofList fields)

    let private text (maximum: int) (name: string) (value: JsonElement) : string =
        let field = value.GetProperty(name)
        require (field.ValueKind = JsonValueKind.String)
        let result: string =
            field.GetString() |> Option.ofObj |> Option.defaultWith reject
        require (result.Length > 0 && result.Length <= maximum)
        result

    let private integer (name: string) (value: JsonElement) : int =
        let field = value.GetProperty(name)
        require (field.ValueKind = JsonValueKind.Number)
        match field.TryGetInt32() with
        | true, parsed -> parsed
        | _ -> reject ()

    let private timestamp (value: JsonElement) =
        let field = value.GetProperty("processStartTimeUtc")
        require (field.ValueKind = JsonValueKind.String)
        match field.TryGetDateTimeOffset() with
        | true, parsed when parsed.Offset = TimeSpan.Zero -> parsed
        | _ -> reject ()

    let private identity (expected: Identity) (value: JsonElement) =
        let identifier = text 22 "instanceId" value
        require (Hermes.Core.LocalAccess.InstanceId.tryParse identifier |> Result.isOk)
        let pid = integer "processId" value
        require (pid > 0 && pid = expected.ProcessId)
        require (identifier = expected.InstanceId)
        require (timestamp value = expected.ProcessStartTimeUtc)
        require (text 256 "origin" value = expected.Origin)

    let private anonymous (value: JsonElement) =
        require (text 16 "state" value = "anonymous")
        require (text 32 "pairingMethod" value = "ownerApproval")
        require (value.GetProperty("expiresAt").ValueKind = JsonValueKind.Null)
        let capabilities = value.GetProperty("capabilities")
        require (capabilities.ValueKind = JsonValueKind.Array && capabilities.GetArrayLength() = 1)
        require (capabilities[0].ValueKind = JsonValueKind.String)
        require (capabilities[0].GetString() = "archive.read")

    let matches (expected: Identity) (bytes: byte array | null)
        : Result<unit, UiInput.Failure> =
        try
            let input = bytes |> Option.ofObj |> Option.defaultWith reject
            require (input.Length > 0 && input.Length <= 4096)
            let source = UTF8Encoding(false, true).GetString(input)
            require (not (source.StartsWith("\uFEFF", StringComparison.Ordinal)))
            let options = JsonDocumentOptions(MaxDepth = 8, CommentHandling = JsonCommentHandling.Disallow)
            use document = JsonDocument.Parse(source, options)
            exact document.RootElement
            require (integer "version" document.RootElement = 1)
            identity expected document.RootElement
            anonymous document.RootElement
            Ok ()
        with
        | Invalid
        | :? JsonException
        | :? DecoderFallbackException -> Error UiInput.Failure.Rejected

[<RequireQualifiedAccess>]
module OwnerUiHttp =

    let private mediaType (value: System.Net.Http.Headers.MediaTypeHeaderValue) =
        String.Equals(value.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
        && value.Parameters.Count <= 1
        && value.Parameters |> Seq.forall (fun item ->
            String.Equals(item.Name, "charset", StringComparison.OrdinalIgnoreCase))
        && (value.CharSet |> Option.ofObj |> Option.forall (fun charset ->
            String.Equals(charset.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)))

    let rec private fill (stream: Stream) (buffer: byte array)
        (offset: int) (cancellation: CancellationToken)
        : Task<Result<byte array, UiInput.Failure>> =
        task {
            let! count = stream.ReadAsync(Memory<byte>(buffer, offset, buffer.Length - offset), cancellation)
            let next = offset + count
            if next > 4096 then return Error UiInput.Failure.Rejected
            elif count = 0 then return Ok(Array.take next buffer)
            else return! fill stream buffer next cancellation
        }

    let private body (response: HttpResponseMessage) (cancellation: CancellationToken)
        : Task<Result<byte array, UiInput.Failure>> =
        task {
            let validType = response.Content.Headers.ContentType |> Option.ofObj |> Option.exists mediaType
            let length = response.Content.Headers.ContentLength
            if response.StatusCode <> HttpStatusCode.OK || not validType
               || response.Content.Headers.ContentEncoding.Count <> 0
               || (length.HasValue && length.Value > 4096L) then
                return Error UiInput.Failure.Rejected
            else
                use! stream = response.Content.ReadAsStreamAsync(cancellation)
                return! fill stream (Array.zeroCreate 4097) 0 cancellation
        }

    let private fetch (target: OwnerControlTarget.Target) (cancellation: CancellationToken)
        : Task<Result<byte array, UiInput.Failure>> =
        task {
            use handler =
                new SocketsHttpHandler(
                    AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
                    PreAuthenticate = false, AutomaticDecompression = DecompressionMethods.None,
                    ConnectTimeout = TimeSpan.FromSeconds 2.0)
            use client = new HttpClient(handler, false)
            client.Timeout <- Timeout.InfiniteTimeSpan
            use request = new HttpRequestMessage(HttpMethod.Get, target.ApiOrigin + "/api/access")
            use! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation)
            return! body response cancellation
        }

    let get (target: OwnerControlTarget.Target) (cancellation: CancellationToken)
        : Task<Result<byte array, UiInput.Failure>> =
        task {
            use deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation)
            deadline.CancelAfter(TimeSpan.FromSeconds 5.0)
            try return! fetch target deadline.Token
            with
            | :? OperationCanceledException ->
                return Error(
                    if cancellation.IsCancellationRequested then UiInput.Failure.Cancelled
                    else UiInput.Failure.Rejected)
            | :? HttpRequestException
            | :? IOException -> return Error UiInput.Failure.Rejected
        }

[<RequireQualifiedAccess>]
module OwnerUiWire =

    let private failure (value: PipeFailure) : UiInput.Failure =
        match value with
        | PipeFailure.Cancelled -> UiInput.Failure.Cancelled
        | _ -> UiInput.Failure.Rejected

    let private request (expected: string option) (operation: Operation) : Request =
        { RequestId = Codec.newRequestId(); ExpectedInstanceId = expected; Operation = operation }

    let private peer (identity: Identity) : PeerExpectation =
        { ProcessId = identity.ProcessId; ProcessStartTimeUtc = identity.ProcessStartTimeUtc
          Origin = identity.Origin; ConfigKey = identity.ConfigKey }

    let private validate decoder (request: Request) (response: Response) =
        Codec.encodeResponse request response
        |> Result.bind decoder
        |> Result.mapError (fun _ -> UiInput.Failure.Rejected)
        |> Task.FromResult

    let discover (target: OwnerControlTarget.Target) (client: ClientOnly)
        (cancellation: CancellationToken) =
        let outgoing = request None Operation.Status
        client.DiscoverStatus outgoing cancellation
        |> Prelude.TaskResult.mapError failure
        |> Prelude.TaskResult.bind
            (validate (Codec.decodeDiscoveryResponse outgoing target) outgoing)

    let ready (target: OwnerControlTarget.Target) (client: ClientOnly) (response: Response) =
        response.Outcome |> Result.mapError (fun _ -> UiInput.Failure.Rejected)
        |> Result.bind (function
            | Reply.Status status when status.Phase = Phase.Ready ->
                Ok ({ Target = target; Client = client; Identity = response.Instance
                      Capabilities = status.Capabilities }: UiInput.Pin)
            | _ -> Error UiInput.Failure.Rejected)
        |> Task.FromResult

    let send (pin: UiInput.Pin) (operation: Operation) (cancellation: CancellationToken) =
        let outgoing = request (Some pin.Identity.InstanceId) operation
        let expected = peer pin.Identity
        pin.Client.Send outgoing expected cancellation
        |> Prelude.TaskResult.mapError failure
        |> Prelude.TaskResult.bind
            (validate (Codec.decodeResponse outgoing expected) outgoing)
        |> Prelude.TaskResult.bind (fun response ->
            response.Outcome
            |> Result.mapError (fun _ -> UiInput.Failure.Rejected)
            |> Task.FromResult)

[<RequireQualifiedAccess>]
module OwnerUiApproval =

    type Frozen =
        { Pin: UiInput.Pin
          Pairing: PairingResult
          Observed: TimeSpan
          Remaining: TimeSpan }

    let private permitted (pin: UiInput.Pin) (code: string)
        (now: DateTimeOffset) (pairing: PairingResult) =
        pairing.UserCode = code && pairing.ApiOrigin = pin.Identity.Origin
        && pairing.State = PairingState.Pending
        && pairing.CreatedAt <= now && now < pairing.ExpiresAt
        && pairing.ExpiresAt - pairing.CreatedAt <= TimeSpan.FromSeconds 180.0
        && pairing.RequestedCapabilities |> List.forall (fun value -> List.contains value pin.Capabilities)
        && (Hermes.Core.LocalAccess.BrowserOrigin.tryParse pairing.BrowserOrigin |> Result.isOk)

    let private freeze (effects: UiInput.Effects) (pin: UiInput.Pin)
        (code: string) (reply: Reply) : Result<Frozen, UiInput.Failure> =
        let now = effects.UtcNow()
        match reply with
        | Reply.Resolved pairing when permitted pin code now pairing ->
            Ok { Pin = pin; Pairing = pairing; Observed = effects.Elapsed()
                 Remaining = pairing.ExpiresAt - now }
        | _ -> Error UiInput.Failure.Rejected

    let private live (effects: UiInput.Effects) (frozen: Frozen) =
        let elapsed = effects.Elapsed() - frozen.Observed
        let now = effects.UtcNow()
        elapsed >= TimeSpan.Zero && elapsed < frozen.Remaining
        && frozen.Pairing.CreatedAt <= now && now < frozen.Pairing.ExpiresAt

    let private display (effects: UiInput.Effects) (frozen: Frozen) =
        [ "Browser origin: " + frozen.Pairing.BrowserOrigin
          "API audience: " + frozen.Pairing.ApiOrigin
          "Target config key: " + frozen.Pin.Identity.ConfigKey
          "Pairing code: " + frozen.Pairing.UserCode
          "Permissions: " + String.concat ", " frozen.Pairing.RequestedCapabilities
          "Type YES to approve exactly this request; anything else cancels." ]
        |> List.iter effects.Write

    let private confirm (effects: UiInput.Effects)
        (cancellation: CancellationToken) (frozen: Frozen) =
        task {
            cancellation.ThrowIfCancellationRequested()
            display effects frozen
            let! line = effects.ReadLine cancellation
            cancellation.ThrowIfCancellationRequested()
            let accepted =
                line |> Option.exists (fun value -> String.Equals(value, "YES", StringComparison.OrdinalIgnoreCase))
            return
                if accepted && live effects frozen then Ok frozen
                else Error UiInput.Failure.Rejected
        }

    let private recheck (effects: UiInput.Effects)
        (cancellation: CancellationToken) (frozen: Frozen) =
        if not (live effects frozen) then Task.FromResult(Error UiInput.Failure.Rejected)
        else
            OwnerUiWire.send frozen.Pin (Operation.Resolve frozen.Pairing.UserCode) cancellation
            |> Prelude.TaskResult.bind (fun reply ->
                match reply with
                | Reply.Resolved current when current = frozen.Pairing && live effects frozen ->
                    Task.FromResult(Ok frozen)
                | _ -> Task.FromResult(Error UiInput.Failure.Rejected))

    let private receipt (effects: UiInput.Effects) (frozen: Frozen) (reply: Reply) =
        let now = effects.UtcNow()
        match reply with
        | Reply.Approved grant when grant.PairingId = frozen.Pairing.PairingId
                                    && grant.Capabilities = frozen.Pairing.RequestedCapabilities
                                    && now < grant.ExpiresAt
                                    && grant.ExpiresAt <= frozen.Pairing.ExpiresAt
                                    && grant.ExpiresAt <= now.AddSeconds(60.0) -> Ok ()
        | _ -> Error UiInput.Failure.Rejected

    let private approve (effects: UiInput.Effects)
        (cancellation: CancellationToken) (frozen: Frozen) =
        let payload: ApprovalPayload =
            { PairingId = frozen.Pairing.PairingId; UserCode = frozen.Pairing.UserCode
              Challenge = frozen.Pairing.Challenge; Capabilities = frozen.Pairing.RequestedCapabilities }
        if not (live effects frozen) then Task.FromResult(Error UiInput.Failure.Rejected)
        else
            OwnerUiWire.send frozen.Pin (Operation.Approve payload) cancellation
            |> Prelude.TaskResult.bind (receipt effects frozen >> Task.FromResult)

    let run (effects: UiInput.Effects) (pin: UiInput.Pin)
        (code: string) (cancellation: CancellationToken) =
        OwnerUiWire.send pin (Operation.Resolve code) cancellation
        |> Prelude.TaskResult.bind (freeze effects pin code >> Task.FromResult)
        |> Prelude.TaskResult.bind (confirm effects cancellation)
        |> Prelude.TaskResult.bind (recheck effects cancellation)
        |> Prelude.TaskResult.bind (approve effects cancellation)

[<RequireQualifiedAccess>]
module OwnerUiOperator =

    let private publicMatch (effects: UiInput.Effects)
        (cancellation: CancellationToken) (pin: UiInput.Pin) =
        effects.Status pin.Target cancellation
        |> Prelude.TaskResult.bind (OwnerUiStatus.matches pin.Identity >> Task.FromResult)
        |> Prelude.TaskResult.map (fun () -> pin)

    let private readCode (effects: UiInput.Effects) (cancellation: CancellationToken) =
        task {
            cancellation.ThrowIfCancellationRequested()
            effects.Write "In the browser choose Unlock, then paste its nonsecret pairing code here."
            let! line = effects.ReadLine cancellation
            cancellation.ThrowIfCancellationRequested()
            return
                line |> Option.map UiInput.code |> Option.defaultValue (Error ())
                |> Result.mapError (fun () -> UiInput.Failure.Rejected)
        }

    let private chooseCode (effects: UiInput.Effects) (mode: UiInput.Mode)
        (pin: UiInput.Pin) (cancellation: CancellationToken) =
        match mode with
        | UiInput.Mode.Pair code -> Task.FromResult(Ok code)
        | UiInput.Mode.Open ->
            effects.Browser (Uri(pin.Target.ApiOrigin + "/", UriKind.Absolute)) cancellation
            |> Prelude.TaskResult.bind (fun () -> readCode effects cancellation)

    let private journey (effects: UiInput.Effects) (selection: UiInput.Selection)
        (cancellation: CancellationToken) =
        let client = effects.Client selection.Target
        OwnerUiWire.discover selection.Target client cancellation
        |> Prelude.TaskResult.bind (OwnerUiWire.ready selection.Target client)
        |> Prelude.TaskResult.bind (publicMatch effects cancellation)
        |> Prelude.TaskResult.bind (fun pin ->
            chooseCode effects selection.Mode pin cancellation
            |> Prelude.TaskResult.bind (fun code -> OwnerUiApproval.run effects pin code cancellation))

    let private report (effects: UiInput.Effects) (outcome: Result<unit, UiInput.Failure>) =
        outcome
        |> Result.map (fun () ->
            effects.Write "Pairing approved. Complete unlocking in the browser."
            0)
        |> Result.defaultWith (fun failure ->
            effects.Write "Owner UI command failed."
            if failure = UiInput.Failure.Cancelled then 130 else 1)

    let execute (effects: UiInput.Effects) (selection: UiInput.Selection)
        (cancellation: CancellationToken) =
        task {
            try
                cancellation.ThrowIfCancellationRequested()
                let! outcome = journey effects selection cancellation
                cancellation.ThrowIfCancellationRequested()
                return report effects outcome
            with
            | :? OperationCanceledException ->
                return report effects (Error UiInput.Failure.Cancelled)
            | :? IOException | :? HttpRequestException | :? UnauthorizedAccessException
            | :? InvalidOperationException | :? Win32Exception ->
                return report effects (Error UiInput.Failure.Rejected)
        }

[<RequireQualifiedAccess>]
module OwnerUiCommand =

    let rec private readLoop (reader: TextReader) (reversed: char list)
        (cancellation: CancellationToken) : Task<string option> =
        task {
            cancellation.ThrowIfCancellationRequested()
            let buffer = Array.zeroCreate<char> 1
            let! count = reader.ReadAsync(Memory<char>(buffer), cancellation)
            if count = 0 then return None
            elif buffer[0] = '\n' then
                let line = reversed |> List.rev |> List.toArray |> fun chars -> String(chars)
                return Some(if line.EndsWith("\r", StringComparison.Ordinal) then line[..line.Length - 2] else line)
            elif List.length reversed >= 64 then return None
            else return! readLoop reader (buffer[0] :: reversed) cancellation
        }

    /// EOF, including a partial line at EOF, is not confirmation.
    let readLineFrom (reader: TextReader) (cancellation: CancellationToken) =
        readLoop reader [] cancellation

    let private browser (address: Uri) (cancellation: CancellationToken)
        : Task<Result<unit, UiInput.Failure>> =
        task {
            cancellation.ThrowIfCancellationRequested()
            let info = ProcessStartInfo(FileName = address.AbsoluteUri, UseShellExecute = true)
            Process.Start(info) |> Option.ofObj |> Option.iter (fun child -> child.Dispose())
            return Ok ()
        }

    let systemEffects () : UiInput.Effects =
        { Client = Factory.client
          Status = OwnerUiHttp.get
          ReadLine = readLineFrom Console.In
          Write = fun message -> Console.Out.WriteLine(message)
          Browser = browser
          UtcNow = fun () -> DateTimeOffset.UtcNow
          Elapsed = fun () ->
              TimeSpan.FromSeconds(float (Stopwatch.GetTimestamp()) / float Stopwatch.Frequency) }

    let runWith (settings: UiInput.Settings) (create: unit -> UiInput.Effects)
        (write: string -> unit) (ordinary: string array -> int)
        (arguments: string array) (cancellation: CancellationToken) =
        match UiInput.route arguments with
        | UiInput.Service -> Task.FromResult(ordinary arguments)
        | UiInput.Command parsed ->
            parsed |> Result.bind (UiInput.select settings)
            |> Result.map (fun selection -> OwnerUiOperator.execute (create ()) selection cancellation)
            |> Result.defaultWith (fun () ->
                write UiInput.Usage
                Task.FromResult 2)

    let private consoleRun (arguments: string array) (ordinary: string array -> int) =
        use lifetime = new CancellationTokenSource()
        let handler =
            ConsoleCancelEventHandler(fun _ event ->
                event.Cancel <- true
                lifetime.Cancel())
        let settings: UiInput.Settings =
            { ConfigDirectory = fun () -> Config.configDir Interpreters.systemEnvironment
              Port = fun () -> Environment.GetEnvironmentVariable("HERMES_PORT") }
        Console.CancelKeyPress.AddHandler(handler)
        try
            (runWith settings systemEffects (fun text -> Console.Out.WriteLine(text))
                ordinary arguments lifetime.Token).GetAwaiter().GetResult()
        finally
            Console.CancelKeyPress.RemoveHandler(handler)

    let run (arguments: string array) (ordinary: string array -> int) =
        match UiInput.route arguments with
        | UiInput.Service -> ordinary arguments
        | UiInput.Command _ -> consoleRun arguments ordinary
