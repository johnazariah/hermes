namespace Hermes.Tests

open System
open System.Text.Json
open Xunit
open Hermes.Core
open Hermes.Core.LocalAccessWire

module internal OwnerDiscoverySamples =
    let quote (value: string) = JsonSerializer.Serialize value

    let differentKey (key: string) =
        key.Substring(0, 63) + (if key.EndsWith("0", StringComparison.Ordinal) then "1" else "0")

    let section (name: string) =
        if name = "" then None else Some name

    let source (target: OwnerControlTarget.Target) =
        OwnerWireSamples.responseBytes 0
        |> OwnerWireSamples.change (Some "instance") "origin" (quote target.ApiOrigin)
        |> OwnerWireSamples.change (Some "instance") "configKey" (quote target.ConfigKey)

    let decode (target: OwnerControlTarget.Target) (bytes: byte array | null) =
        Codec.decodeDiscoveryResponse (OwnerWireSamples.request 0) target bytes

    let forbidden () : Request list =
        let status = OwnerWireSamples.request 0
        let resolve = OwnerWireSamples.request 1
        let approve = OwnerWireSamples.request 2
        [ { status with ExpectedInstanceId = Some OwnerWireSamples.id }
          { status with ExpectedInstanceId = Some "" }
          { status with RequestId = "" }
          { status with RequestId = "AAAAAAAAAAAAAAAAAAAAAB" }
          resolve
          { resolve with ExpectedInstanceId = None }
          approve
          { approve with ExpectedInstanceId = None } ]

module OwnerStatusDiscoveryWireTests =
    open OwnerDiscoverySamples

    [<Fact>]
    let ``discovery returns the complete canonical identity without a PID baseline`` () =
        let target = OwnerPipeFixture.target ()
        let identifier = "AQEBAQEBAQEBAQEBAQEBAQ"
        let start = DateTimeOffset(2026, 10, 20, 0, 1, 0, TimeSpan.Zero)
        let input =
            source target
            |> OwnerWireSamples.change (Some "instance") "instanceId" (quote identifier)
            |> OwnerWireSamples.change (Some "instance") "processId" "6789"
            |> OwnerWireSamples.change (Some "instance") "processStartTimeUtc" (JsonSerializer.Serialize start)
        let actual = decode target input |> OwnerWireSamples.ok
        let expected: Identity =
            { InstanceId = identifier; ProcessId = 6789; ProcessStartTimeUtc = start
              Origin = target.ApiOrigin; ConfigKey = target.ConfigKey }
        Assert.Equal<Identity>(expected, actual.Instance)
        Assert.True(Result.isOk actual.Outcome)

    [<Fact>]
    let ``both discovery codec entry points reject every non-discovery request`` () =
        let target = OwnerPipeFixture.target ()
        for request in forbidden () do
            Assert.Equal<Result<byte array, CodecFailure>>(
                Error CodecFailure.Malformed, Codec.encodeDiscoveryRequest request)
            Assert.Equal<Result<Response, CodecFailure>>(
                Error CodecFailure.Malformed,
                Codec.decodeDiscoveryResponse request target (source target))

    [<Fact>]
    let ``full config key and exact selected numeric audience must match`` () =
        let target = OwnerPipeFixture.target ()
        let wrongKey =
            source target
            |> OwnerWireSamples.change (Some "instance") "configKey" (quote (differentKey target.ConfigKey))
        let wrongOrigin =
            source target
            |> OwnerWireSamples.change (Some "instance") "origin"
                (quote (target.ApiOrigin.Replace("127.0.0.1", "localhost")))
        for input in [ wrongKey; wrongOrigin ] do
            Assert.Equal<Result<Response, CodecFailure>>(
                Error CodecFailure.IdentityMismatch, decode target input)

    [<Theory>]
    [<InlineData("", "version", "2")>]
    [<InlineData("", "version", "1.0")>]
    [<InlineData("", "version", "1e0")>]
    [<InlineData("", "requestId", "\"AQEBAQEBAQEBAQEBAQEBAQ\"")>]
    [<InlineData("", "unknown", "true")>]
    [<InlineData("", "ok", "\"true\"")>]
    [<InlineData("", "error", "{}")>]
    [<InlineData("", "result", "null")>]
    [<InlineData("instance", "instanceId", "\"AAAAAAAAAAAAAAAAAAAAAB\"")>]
    [<InlineData("instance", "instanceId", "\"AAAAAAAAAAAAAAAAAAAAAA==\"")>]
    [<InlineData("instance", "processId", "0")>]
    [<InlineData("instance", "processId", "-1")>]
    [<InlineData("instance", "processId", "2147483648")>]
    [<InlineData("instance", "processId", "1.0")>]
    [<InlineData("instance", "processStartTimeUtc", "\"not-a-time\"")>]
    [<InlineData("instance", "processStartTimeUtc", "\"2026-10-20T10:00:00+10:00\"")>]
    [<InlineData("instance", "configKey", "\"ABCDEF\"")>]
    [<InlineData("instance", "unknown", "true")>]
    [<InlineData("result", "phase", "\"running\"")>]
    [<InlineData("result", "capabilities", "[\"unknown\"]")>]
    [<InlineData("result", "capabilities", "[\"mutate\",\"config.read\"]")>]
    [<InlineData("result", "capabilities", "[\"mutate\",\"mutate\"]")>]
    [<InlineData("result", "capabilities", "[null]")>]
    [<InlineData("result", "capabilities", "null")>]
    [<InlineData("result", "unknown", "true")>]
    let ``discovery retains the exact strict response grammar``
        (location: string) (field: string) (replacement: string) =
        let target = OwnerPipeFixture.target ()
        source target
        |> OwnerWireSamples.change (section location) field replacement
        |> decode target
        |> Result.isError
        |> Assert.True

    [<Fact>]
    let ``discovery retains byte depth duplicate BOM and trailing JSON bounds`` () =
        let target = OwnerPipeFixture.target ()
        let text = source target |> OwnerWireSamples.text
        let malformed =
            [ OwnerWireSamples.bytes ("\uFEFF" + text)
              OwnerWireSamples.bytes (text + "{}")
              OwnerWireSamples.bytes (text.Replace("\"version\":1", "\"version\":1,\"version\":1"))
              OwnerWireSamples.bytes (text.Replace("\"phase\":\"ready\"", "\"phase\":/*x*/\"ready\""))
              OwnerWireSamples.bytes (text.Replace("\"phase\":\"ready\"", "\"phase\":[[[[[[[[[0]]]]]]]]]"))
              OwnerWireSamples.bytes (text.PadRight(8193))
              [| 0xC3uy; 0x28uy |]
              [||] ]
        for input in malformed do Assert.True(decode target input |> Result.isError)
        Assert.True(decode target (OwnerWireSamples.bytes (text.PadRight(8192))) |> Result.isOk)
        let absent: byte array | null = null
        Assert.Equal<Result<Response, CodecFailure>>(Error CodecFailure.Malformed, decode target absent)

    [<Fact>]
    let ``discovery preserves application rejection without retaining peer error text`` () =
        let target = OwnerPipeFixture.target ()
        let input =
            source target
            |> OwnerWireSamples.change None "ok" "false"
            |> OwnerWireSamples.change None "result" "null"
            |> OwnerWireSamples.change None "error" (JsonSerializer.Serialize
                {| code = "instance_not_ready"; message = "synthetic discarded peer detail" |})
        let actual = decode target input |> OwnerWireSamples.ok
        Assert.Equal<Result<Reply, ErrorCode>>(Error ErrorCode.InstanceNotReady, actual.Outcome)
        Assert.DoesNotContain("synthetic discarded peer detail", sprintf "%A" actual)
