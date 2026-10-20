namespace Hermes.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open FsCheck.Xunit
open Xunit
open Hermes.Core.LocalAccessWire

module internal OwnerWireSamples =
    let ok result = result |> Result.defaultWith (fun failure -> failwithf "Unexpected rejection: %A" failure)
    let bytes (text: string) = Encoding.UTF8.GetBytes text
    let text (bytes: byte array) = Encoding.UTF8.GetString bytes
    let id = "AAAAAAAAAAAAAAAAAAAAAA"

    let peer: PeerExpectation =
        { ProcessId = 4123
          ProcessStartTimeUtc = DateTimeOffset(2026, 10, 19, 23, 59, 0, TimeSpan.Zero)
          Origin = "http://127.0.0.1:31991"
          ConfigKey = String.replicate 64 "0" }

    let samples (name: string) =
        let path = Path.Combine(AppContext.BaseDirectory, "SyntheticFixtures", "http-access-v1.json")
        use document = JsonDocument.Parse(File.ReadAllText path)
        document.RootElement.GetProperty(name).EnumerateArray()
        |> Seq.map (fun item -> item.GetRawText() |> bytes)
        |> Seq.toArray

    let request (index: int) : Request = (samples "requests").[index] |> Codec.decodeRequest |> ok
    let responseBytes (index: int) : byte array = (samples "responses").[index]

    let private requiredNode (value: JsonNode | null) : JsonNode =
        match value with
        | null -> failwith "Expected a non-null synthetic JSON node."
        | node -> node

    let change (section: string option) (name: string) (replacement: string) (input: byte array) =
        let root = JsonNode.Parse(text input) |> requiredNode
        let target =
            section
            |> Option.map (fun key -> root.[key] |> requiredNode)
            |> Option.defaultValue root
        target.[name] <- JsonNode.Parse replacement
        root.ToJsonString() |> bytes

    let proof (size: int) (input: byte list) =
        let digest = input |> List.toArray |> SHA256.HashData |> Array.take size
        Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_')

module LocalAccessWireTests =
    open OwnerWireSamples

    [<Fact>]
    let ``CSharp compatible request and result vectors round trip structurally`` () =
        for index in [ 0; 1; 2 ] do
            let request = request index
            let requestBytes = Codec.encodeRequest request |> ok
            Assert.Equal(Ok request, Codec.decodeRequest requestBytes)
            let source = responseBytes index
            let response = Codec.decodeResponse request peer source |> ok
            let encoded = Codec.encodeResponse request response |> ok
            Assert.Equal(Ok response, Codec.decodeResponse request peer encoded)
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text source), JsonNode.Parse(text encoded)))

    [<Theory>]
    [<InlineData("version", "1.0")>]
    [<InlineData("version", "1e0")>]
    [<InlineData("version", "\"1\"")>]
    [<InlineData("version", "2")>]
    [<InlineData("Version", "1")>]
    [<InlineData("requestId", "\"AAAAAAAAAAAAAAAAAAAAAB\"")>]
    [<InlineData("requestId", "\"AAAAAAAAAAAAAAAAAAAAAA==\"")>]
    [<InlineData("requestId", "\"AAAAAAAAAAAAAAAAAAAAA+\"")>]
    [<InlineData("operation", "\"Status\"")>]
    [<InlineData("operation", "\"invented\"")>]
    [<InlineData("payload", "null")>]
    [<InlineData("payload", "[]")>]
    [<InlineData("payload", "{\"unknown\":true}")>]
    let ``request envelope and status payload are exact`` (field: string) (replacement: string) =
        (samples "requests").[0]
        |> change None field replacement
        |> Codec.decodeRequest
        |> Result.isError
        |> Assert.True

    [<Fact>]
    let ``nonstatus operations require a canonical instance pin`` () =
        for index in [ 1; 2 ] do
            for pin in [ "null"; "\"AAAAAAAAAAAAAAAAAAAAAB\""; "123" ] do
                (samples "requests").[index]
                |> change None "expectedInstanceId" pin
                |> Codec.decodeRequest
                |> Result.isError
                |> Assert.True

    [<Theory>]
    [<InlineData("pairingId", "\"AAAAAAAAAAAAAAAAAAAAAB\"")>]
    [<InlineData("userCode", "\"IIII-OOOO-UUUU\"")>]
    [<InlineData("userCode", "\"0123-4567-89ab\"")>]
    [<InlineData("challenge", "\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB\"")>]
    [<InlineData("challenge", "\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\"")>]
    [<InlineData("capabilities", "[\"archive.read\"]")>]
    [<InlineData("capabilities", "[\"mutate\",\"config.read\"]")>]
    [<InlineData("capabilities", "[\"mutate\",\"mutate\"]")>]
    [<InlineData("capabilities", "[\"unknown\"]")>]
    [<InlineData("capabilities", "[null]")>]
    [<InlineData("capabilities", "[1]")>]
    [<InlineData("capabilities", "null")>]
    [<InlineData("unknown", "true")>]
    let ``approval payload fields and grant arrays are strict`` (field: string) (replacement: string) =
        (samples "requests").[2]
        |> change (Some "payload") field replacement
        |> Codec.decodeRequest
        |> Result.isError
        |> Assert.True

    [<Fact>]
    let ``UTF8 BOM duplicates comments depth and trailing JSON are rejected`` () =
        let source = (samples "requests").[0] |> text
        let malformed =
            [ "\uFEFF" + source
              source + "{}"
              source.Replace("\"version\":1", "\"version\":1,\"version\":1")
              source.Replace("\"payload\":{}", "\"payload\":{\"x\":[[[[[[[[[0]]]]]]]]]}")
              source.Replace("\"payload\":{}", "\"payload\":{/* comment */}")
              source.Replace("\"payload\":{}", "\"payload\":{,}") ]
        for input in malformed |> List.map bytes |> List.append [ [| 0xC3uy; 0x28uy |] ] do
            Assert.True(Codec.decodeRequest input |> Result.isError)

    [<Fact>]
    let ``empty and oversized bodies fail while an 8192 byte document succeeds`` () =
        let source = (samples "requests").[0] |> text
        Assert.True(Codec.decodeRequest [||] |> Result.isError)
        Assert.True(Codec.decodeRequest (source.PadRight(8192) |> bytes) |> Result.isOk)
        Assert.True(Codec.decodeRequest (source.PadRight(8193) |> bytes) |> Result.isError)

    [<Fact>]
    let ``null request and response bodies return typed malformed failures`` () =
        let absent: byte array | null = null
        Assert.Equal<Result<Request, CodecFailure>>(
            Result.Error CodecFailure.Malformed, Codec.decodeRequest absent)
        Assert.Equal<Result<Response, CodecFailure>>(
            Result.Error CodecFailure.Malformed, Codec.decodeResponse (request 0) peer absent)

    [<Fact>]
    let ``nested duplicate known properties fail`` () =
        let source = (samples "requests").[1] |> text
        source.Replace("\"userCode\":\"0123-4567-89AB\"",
                       "\"userCode\":\"0123-4567-89AB\",\"userCode\":\"0123-4567-89AB\"")
        |> bytes
        |> Codec.decodeRequest
        |> Result.isError
        |> Assert.True

    [<Theory>]
    [<InlineData("version", "2")>]
    [<InlineData("requestId", "\"AQEBAQEBAQEBAQEBAQEBAQ\"")>]
    [<InlineData("unknown", "true")>]
    [<InlineData("ok", "\"true\"")>]
    [<InlineData("error", "{}")>]
    [<InlineData("result", "null")>]
    let ``response corruption is never success`` (field: string) (replacement: string) =
        responseBytes 0
        |> change None field replacement
        |> Codec.decodeResponse (request 0) peer
        |> Result.isError
        |> Assert.True

    [<Theory>]
    [<InlineData("processId", "4124")>]
    [<InlineData("processStartTimeUtc", "\"2026-10-19T23:58:59Z\"")>]
    [<InlineData("instanceId", "\"AQEBAQEBAQEBAQEBAQEBAQ\"")>]
    [<InlineData("origin", "\"http://localhost:31991\"")>]
    [<InlineData("configKey", "\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")>]
    let ``all five identity components are pinned`` (field: string) (replacement: string) =
        let pinned: Request = { request 0 with ExpectedInstanceId = Some id }
        let actual =
            responseBytes 0
            |> change (Some "instance") field replacement
            |> Codec.decodeResponse pinned peer
        Assert.Equal<Result<Response, CodecFailure>>(Result.Error CodecFailure.IdentityMismatch, actual)

    [<Theory>]
    [<InlineData(0, "phase", "\"running\"")>]
    [<InlineData(0, "capabilities", "[\"mutate\",\"config.read\"]")>]
    [<InlineData(1, "state", "\"consumed\"")>]
    [<InlineData(1, "requestedCapabilities", "[\"archive.read\"]")>]
    [<InlineData(1, "createdAt", "\"2026-10-20T10:00:00+10:00\"")>]
    [<InlineData(1, "expiresAt", "\"2026-10-19T00:00:00Z\"")>]
    [<InlineData(2, "state", "\"pending\"")>]
    [<InlineData(2, "unknown", "0")>]
    let ``operation results have exact fields and timestamp shapes``
        (index: int) (field: string) (replacement: string) =
        responseBytes index
        |> change (Some "result") field replacement
        |> Codec.decodeResponse (request index) peer
        |> Result.isError
        |> Assert.True

    [<Theory>]
    [<InlineData("invalid_request")>]
    [<InlineData("unsupported_version")>]
    [<InlineData("wrong_instance")>]
    [<InlineData("instance_not_ready")>]
    [<InlineData("pairing_not_found")>]
    [<InlineData("pairing_expired")>]
    [<InlineData("pairing_consumed")>]
    [<InlineData("pairing_conflict")>]
    [<InlineData("insufficient_capability")>]
    [<InlineData("access_capacity_reached")>]
    [<InlineData("internal_error")>]
    let ``application errors are typed and peer message text is discarded`` (code: string) =
        let source =
            responseBytes 0
            |> change None "ok" "false"
            |> change None "result" "null"
            |> change None "error" (JsonSerializer.Serialize
                {| code = code; message = "synthetic peer text" |})
        let parsed = Codec.decodeResponse (request 0) peer source |> ok
        let expected =
            ErrorCode.tryParse code
            |> Option.defaultWith (fun () -> failwith "Unknown synthetic application code.")
        Assert.Equal<Result<Reply, ErrorCode>>(Result.Error expected, parsed.Outcome)
        Assert.DoesNotContain("synthetic peer text", sprintf "%A" parsed)
        let encoded = Codec.encodeResponse (request 0) parsed |> ok
        Assert.Equal(Ok parsed, Codec.decodeResponse (request 0) peer encoded)

    [<Property(MaxTest = 100)>]
    let ``arbitrary canonical identifiers round trip and noncanonical tail bits fail`` (input: byte list) =
        let identifier = proof 16 input
        let value: Request = { RequestId = identifier; ExpectedInstanceId = None; Operation = Operation.Status }
        let encoded = Codec.encodeRequest value |> ok
        let corrupt = text encoded |> fun json -> json.Replace(identifier, identifier.Substring(0, 21) + "B")
        Codec.decodeRequest encoded = Ok value
        && (corrupt |> bytes |> Codec.decodeRequest |> Result.isError)

    [<Property(MaxTest = 100)>]
    let ``any additional request property is rejected`` (input: string | null) =
        let suffix = input |> Option.ofObj |> Option.defaultValue ""
        (samples "requests").[0]
        |> change None ("unknown:" + suffix) "true"
        |> Codec.decodeRequest
        |> Result.isError
