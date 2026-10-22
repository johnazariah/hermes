using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hermes.Tray.Access;
using Xunit;

namespace Hermes.Tray.Tests;

public sealed class OwnerWireTests
{
    [Theory]
    [InlineData(@"C:\HERMES\CONFIG", 31991)]
    [InlineData(@"C:\", 21741)]
    [InlineData("/synthetic/Hermes/config", 65535)]
    public void Hashing_uses_the_exact_canonical_preimage_without_host_path_interpretation(
        string canonical, int port)
    {
        var preimage = "hermes-owner-control-v1\n" + canonical + "\n" +
            port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n";
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(preimage))).ToLowerInvariant();
        Assert.Equal(expected, OwnerNaming.HashCanonicalDirectory(canonical, port));
        Assert.Equal(64, expected.Length);
    }

    [Fact]
    public void Windows_normalization_preserves_roots_and_removes_all_trailing_separators()
    {
        Assert.Equal(@"C:\", OwnerTarget.Create(@"c:\", 31991).CanonicalDirectory);
        var target = OwnerTarget.Create(@"c:\synthetic\config\\\", 31991);
        Assert.Equal(@"C:\SYNTHETIC\CONFIG", target.CanonicalDirectory);
        Assert.Equal("hmop1-" + target.ConfigKey[..32], target.PipeName);
        Assert.Equal("http://127.0.0.1:31991", target.ApiOrigin);
        Assert.Throws<ArgumentException>(() => OwnerTarget.Create("relative", 31991));
        Assert.Throws<ArgumentOutOfRangeException>(() => OwnerTarget.Create(@"C:\synthetic", 0));
        Assert.NotEqual(target.ConfigKey, OwnerTarget.Create(@"c:\synthetic\config", 31992).ConfigKey);
    }

    [Theory]
    [InlineData("""{"a":1,"a":2}""")]
    [InlineData("""{"a":{"x":1,"x":2}}""")]
    [InlineData("""{"a":[{"x":1,"x":2}]}""")]
    [InlineData("""{"a":1}{}""")]
    [InlineData("""{"a":1,}""")]
    [InlineData("""{/*x*/"a":1}""")]
    [InlineData("\uFEFF{}")]
    [InlineData("""[[[[[[[[[0]]]]]]]]]""")]
    public void Strict_json_rejects_ambiguous_or_excessive_documents(string json) =>
        Assert.Throws<AccessProtocolFault>(() =>
            AccessJson.Read(Encoding.UTF8.GetBytes(json), value => value.ValueKind));

    [Fact]
    public void Invalid_utf8_empty_and_oversize_json_fail_before_interpretation()
    {
        foreach (var bytes in new byte[][] { [], [0xC3, 0x28], new byte[8193] })
            Assert.Throws<AccessProtocolFault>(() => AccessJson.Read(bytes, _ => true));
    }

    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAB")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAA/")]
    public void Noncanonical_identifiers_are_rejected(string value) =>
        Assert.Throws<AccessProtocolFault>(() => AccessJson.RequireProof(value, 16));

    [Fact]
    public void Synthetic_identifiers_and_display_codes_are_canonical()
    {
        Assert.Equal(AccessFixture.Id, AccessJson.RequireProof(AccessFixture.Id, 16));
        Assert.Equal(AccessFixture.Challenge, AccessJson.RequireProof(AccessFixture.Challenge, 32));
        Assert.Equal(AccessFixture.Code, AccessJson.RequireCode(AccessFixture.Code));
        Assert.Throws<AccessProtocolFault>(() => AccessJson.RequireCode("IIII-OOOO-UUUU"));
        var generated = Enumerable.Range(0, 64).Select(_ => AccessJson.NewIdentifier()).ToArray();
        Assert.Equal(generated.Length, generated.Distinct().Count());
        Assert.All(generated, id => Assert.Equal(id, AccessJson.RequireProof(id, 16)));
    }

    [Theory]
    [InlineData("version", "2")]
    [InlineData("requestId", "\"AQEBAQEBAQEBAQEBAQEBAQ\"")]
    [InlineData("unknown", "true")]
    [InlineData("ok", "\"true\"")]
    [InlineData("error", "{}")]
    public void Envelope_corruption_is_not_success(string field, string replacement)
    {
        var root = ReplyNode();
        root[field] = JsonNode.Parse(replacement);
        Assert.Throws<AccessProtocolFault>(() => Read(root));
    }

    [Theory]
    [InlineData("processId", "4124")]
    [InlineData("processStartTimeUtc", "\"2026-10-19T23:58:59Z\"")]
    [InlineData("instanceId", "\"AQEBAQEBAQEBAQEBAQEBAQ\"")]
    [InlineData("origin", "\"http://localhost:31991\"")]
    [InlineData("configKey", "\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")]
    public void Every_identity_component_is_pinned_including_same_pid_different_start(
        string field, string replacement)
    {
        var root = ReplyNode();
        var instance = Assert.IsType<JsonObject>(root["instance"]);
        instance[field] = JsonNode.Parse(replacement)
            ?? throw new InvalidOperationException("Synthetic identity replacement must be non-null.");
        var error = Assert.Throws<AccessProtocolFault>(() => Read(root));
        Assert.Equal(OwnerFailure.IdentityMismatch, error.Failure);
    }

    [Theory]
    [InlineData("""["mutate","config.read"]""")]
    [InlineData("""["config.read","config.read"]""")]
    [InlineData("""["unknown"]""")]
    [InlineData("""[null]""")]
    [InlineData("""[1]""")]
    public void Capability_lists_are_recognized_unique_and_ordinal(string capabilities)
    {
        var root = ReplyNode();
        var result = Assert.IsType<JsonObject>(root["result"]);
        result["capabilities"] = Assert.IsType<JsonArray>(JsonNode.Parse(capabilities));
        Assert.Throws<AccessProtocolFault>(() => Read(root));
    }

    [Fact]
    public void Every_fixed_application_code_is_typed_and_peer_message_is_not_exposed()
    {
        var codes = new[] { "invalid_request", "unsupported_version", "wrong_instance",
            "instance_not_ready", "pairing_not_found", "pairing_expired", "pairing_consumed",
            "pairing_conflict", "insufficient_capability", "access_capacity_reached", "internal_error" };
        foreach (var code in codes)
        {
            var root = ReplyNode();
            root["ok"] = false;
            root["result"] = null;
            root["error"] = new JsonObject { ["code"] = code, ["message"] = "synthetic peer text" };
            var failure = Assert.IsType<OwnerResult<OwnerStatus>.Failure>(Read(root));
            Assert.Equal(OwnerWire.ErrorCode(code), failure.Code);
            Assert.DoesNotContain("synthetic", failure.ToString());
        }
        Assert.Throws<AccessProtocolFault>(() => OwnerWire.ErrorCode("invented_error"));
    }

    [Fact]
    public void Operation_results_and_requests_have_exact_shapes()
    {
        var target = AccessFixture.Target();
        var pairing = AccessFixture.Pairing(target);
        var json = PairingJson(pairing);
        var parsed = AccessJson.Read(json, OwnerWire.Pairing);
        Assert.Equal(pairing.PairingId, parsed.PairingId);
        Assert.True(AccessCapabilities.Equal(pairing.RequestedCapabilities, parsed.RequestedCapabilities));
        var approval = AccessJson.Read(Encoding.UTF8.GetBytes(
            $$"""{"pairingId":"{{pairing.PairingId}}","state":"approved","capabilities":["config.read","model.invoke","mutate"],"expiresAt":"2026-10-20T00:01:00Z"}"""),
            OwnerWire.Approval);
        Assert.Equal(pairing.PairingId, approval.PairingId);
        var request = OwnerWire.Request(AccessFixture.Id, null, "status", new { });
        using var document = JsonDocument.Parse(request);
        AccessJson.Object(document.RootElement, "version", "requestId", "expectedInstanceId", "operation", "payload");
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("expectedInstanceId").ValueKind);
        Assert.Throws<AccessProtocolFault>(() => OwnerWire.Request(AccessFixture.Id, null, "resolve", new { }));
        Assert.Throws<AccessProtocolFault>(() => OwnerWire.Request(AccessFixture.Id, null, "invented", new { }));
    }

    [Theory]
    [InlineData("state", "\"consumed\"")]
    [InlineData("requestedCapabilities", "[\"archive.read\"]")]
    [InlineData("challenge", "\"not-a-proof\"")]
    [InlineData("expiresAt", "\"2026-10-19T00:00:00Z\"")]
    [InlineData("createdAt", "\"2026-10-20T10:00:00+10:00\"")]
    [InlineData("extra", "0")]
    public void Resolve_result_rejects_invalid_fields(string field, string replacement)
    {
        var root = JsonNode.Parse(PairingJson(AccessFixture.Pairing(AccessFixture.Target()))) as JsonObject
            ?? throw new InvalidOperationException();
        root[field] = JsonNode.Parse(replacement);
        Assert.Throws<AccessProtocolFault>(() =>
            AccessJson.Read(Encoding.UTF8.GetBytes(root.ToJsonString()), OwnerWire.Pairing));
    }

    [Fact]
    public void Challenge_bearing_records_redact_direct_and_nested_formatting_without_changing_proofs()
    {
        var pairing = AccessFixture.Pairing(AccessFixture.Target());
        var notice = Assert.IsType<AccessPairingNotice>(AccessBridge.Read(
            AccessFixture.PairingNotice(new(AccessFixture.Id, AccessFixture.Id))));
        Assert.Equal(AccessFixture.Challenge, notice.Challenge);
        Assert.Equal(AccessFixture.Challenge, AccessJson.Read(PairingJson(pairing), OwnerWire.Pairing).Challenge);
        Assert.All(ChallengeBearingFormats(pairing, notice), value =>
        {
            var text = value.ToString() ?? string.Empty;
            Assert.DoesNotContain(AccessFixture.Challenge, text);
            Assert.Contains("[redacted]", text);
        });
    }

    private static IReadOnlyList<object> ChallengeBearingFormats(
        OwnerPairing pairing, AccessPairingNotice notice) =>
    [
        pairing, notice,
        new OwnerResult<OwnerPairing>.Success(pairing),
        new OwnerResult<AccessPairingNotice>.Success(notice),
        new OwnerResult<AccessBrowserNotice>.Success(notice),
        new OwnerResult<OwnerResult<OwnerPairing>>.Success(
            new OwnerResult<OwnerPairing>.Success(pairing)),
        new OwnerResult<OwnerResult<AccessBrowserNotice>>.Success(
            new OwnerResult<AccessBrowserNotice>.Success(notice)),
        new { Pairing = pairing, Notice = notice }
    ];

    private static byte[] PairingJson(OwnerPairing pairing) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        pairingId = pairing.PairingId, userCode = pairing.UserCode, challenge = pairing.Challenge,
        browserOrigin = pairing.BrowserOrigin, apiOrigin = pairing.ApiOrigin,
        requestedCapabilities = new[] { "config.read", "model.invoke", "mutate" },
        createdAt = pairing.CreatedAt, expiresAt = pairing.ExpiresAt, state = "pending"
    });

    private static JsonObject ReplyNode() =>
        JsonNode.Parse(AccessFixture.StatusReply(AccessFixture.Target(), AccessFixture.Id)) as JsonObject
        ?? throw new InvalidOperationException();

    private static OwnerResult<OwnerStatus> Read(JsonObject root) =>
        OwnerWire.Response(Encoding.UTF8.GetBytes(root.ToJsonString()), AccessFixture.Id,
            AccessFixture.Target(), AccessFixture.Process, AccessFixture.Id, OwnerWire.Status);
}
