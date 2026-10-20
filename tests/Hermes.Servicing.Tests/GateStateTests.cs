using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hermes.Servicing;
using Xunit;

namespace Hermes.Servicing.Tests;

public sealed class GateStateTests
{
    private const string ServicePath = "service/Hermes.Service.dll";
    private static readonly Guid ServiceMvid = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherMvid = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ProductCode = new("44444444-4444-4444-4444-444444444444");

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"installation\":null,\"extra\":1}")]
    [InlineData("{\"Installation\":null}")]
    [InlineData("{\"installation\":{},\"installation\":null}")]
    [InlineData("{\"installation\":null,\"installation\":null}")]
    [InlineData("{\"installation\":null} trailing")]
    public void Malformed_ready_bytes_do_not_return_verified_absence(string body)
    {
        using var sandbox = new GateSandbox();
        sandbox.WriteBody(body);
        AssertReadRefusal(sandbox, RefusalReason.InvalidState);
    }

    [Theory]
    [InlineData("", "installation")]
    [InlineData("installation", "binding")]
    [InlineData("installation", "productCode")]
    [InlineData("installation", "images")]
    [InlineData("binding", "upgradeCode")]
    [InlineData("binding", "appDirectory")]
    [InlineData("binding", "admissionAbi")]
    [InlineData("image", "relativePath")]
    [InlineData("image", "moduleVersionId")]
    public void Every_wire_constructor_field_is_required(string section, string property)
    {
        using var sandbox = new GateSandbox();
        var body = Body(sandbox);
        Assert.True(Section(body, section).Remove(property));
        sandbox.WriteBody(body.ToJsonString());
        AssertReadRefusal(sandbox, RefusalReason.InvalidState);
    }

    [Theory]
    [InlineData("installation", "binding", "null", RefusalReason.InvalidState)]
    [InlineData("installation", "productCode", "null", RefusalReason.InvalidState)]
    [InlineData("installation", "images", "null", RefusalReason.InvalidState)]
    [InlineData("binding", "upgradeCode", "null", RefusalReason.InvalidState)]
    [InlineData("binding", "appDirectory", "null", RefusalReason.InvalidState)]
    [InlineData("binding", "admissionAbi", "null", RefusalReason.InvalidState)]
    [InlineData("image", "relativePath", "null", RefusalReason.InvalidState)]
    [InlineData("image", "moduleVersionId", "null", RefusalReason.InvalidState)]
    [InlineData("installation", "images", "[]", RefusalReason.InvalidState)]
    [InlineData("installation", "images", "[null]", RefusalReason.InvalidState)]
    [InlineData("installation", "productCode",
        "\"00000000-0000-0000-0000-000000000000\"", RefusalReason.InvalidState)]
    [InlineData("image", "moduleVersionId",
        "\"00000000-0000-0000-0000-000000000000\"", RefusalReason.InvalidState)]
    [InlineData("binding", "appDirectory", "\"relative\"", RefusalReason.InvalidState)]
    [InlineData("binding", "admissionAbi", "2", RefusalReason.UnsupportedProtocol)]
    [InlineData("binding", "upgradeCode",
        "\"00000000-0000-0000-0000-000000000000\"", RefusalReason.UnsupportedProtocol)]
    public void Malformed_installations_fail_the_read_contract(
        string section, string property, string replacement, RefusalReason reason)
    {
        using var sandbox = new GateSandbox();
        var body = Body(sandbox);
        Section(body, section)[property] = JsonNode.Parse(replacement);
        sandbox.WriteBody(body.ToJsonString());
        AssertReadRefusal(sandbox, reason);
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("relativePath")]
    [InlineData("moduleVersionId")]
    public void Nested_duplicate_properties_are_not_last_value_wins(string property)
    {
        using var sandbox = new GateSandbox();
        var marker = $"\"{property}\":";
        var body = JsonSerializer.Serialize(
            new GateReadyState(Installation(sandbox)), ServicingJson.Default.GateReadyState);
        sandbox.WriteBody(body.Replace(marker, $"{marker}null,{marker}", StringComparison.Ordinal));
        AssertReadRefusal(sandbox, RefusalReason.InvalidState);
    }

    [Fact]
    public void A_short_header_has_a_typed_invalid_state_refusal()
    {
        using var sandbox = new GateSandbox();
        File.WriteAllBytes(sandbox.GatePath, [0x48]);
        AssertReadRefusal(sandbox, RefusalReason.InvalidState);
    }

    [Fact]
    public void Explicit_verified_absence_is_valid_on_read_and_publish()
    {
        using var sandbox = new GateSandbox();
        sandbox.WriteBody("{\"installation\":null}");
        Assert.Null(sandbox.ReadReady().Installation);
        sandbox.Publish(new GateReadyState(null));
        Assert.Null(sandbox.ReadReady().Installation);
        Assert.Equal("{\"installation\":null}", Encoding.UTF8.GetString(
            File.ReadAllBytes(sandbox.GatePath)[GateCodec.HeaderSize..]));
    }

    [Theory]
    [InlineData("service/Hermes.Service.dll.")]
    [InlineData("service/Hermes.Service.dll ")]
    [InlineData("service./Hermes.Service.dll")]
    [InlineData("service /Hermes.Service.dll")]
    [InlineData("SERVICE/HERMES.SERVICE.DLL")]
    [InlineData("service//Hermes.Service.dll")]
    [InlineData("service/./Hermes.Service.dll")]
    [InlineData("service/../Hermes.Service.dll")]
    [InlineData("service\\Hermes.Service.dll")]
    [InlineData("/service/Hermes.Service.dll")]
    [InlineData("service/Hermes.Service.dll:alternate")]
    [InlineData(@"\\?\C:\Test\App\service\Hermes.Service.dll")]
    [InlineData("service/CON.dll")]
    [InlineData("service/LPT1.dll")]
    [InlineData("service/COM¹.dll")]
    [InlineData("service/Hermes?.dll")]
    [InlineData("service/Hermes*.dll")]
    [InlineData("service/Hermes\u0001.dll")]
    public void Invalid_surplus_inventory_fails_read_and_publish_without_writing(string path)
    {
        using var sandbox = new GateSandbox();
        var installation = Installation(sandbox);
        var state = new GateReadyState(installation with
        {
            Images = installation.Images.Add(new(path, OtherMvid))
        });
        sandbox.WriteState(state);
        AssertReadRefusal(sandbox, RefusalReason.InvalidState);
        var before = File.ReadAllBytes(sandbox.GatePath);
        var error = Assert.Throws<AdmissionException>(() => sandbox.Publish(state));
        Assert.Equal(RefusalReason.InvalidState, error.Reason);
        Assert.Equal(before, File.ReadAllBytes(sandbox.GatePath));
    }

    [Theory]
    [InlineData("tray/Hermes.Service.dll")]
    [InlineData("SERVICE/Hermes.Other.dll")]
    [InlineData("service/Hermes.Service.extra.dll")]
    public void Distinct_canonical_inventory_paths_round_trip(string path)
    {
        using var sandbox = new GateSandbox();
        var installation = Installation(sandbox);
        var expected = installation with { Images = installation.Images.Add(new(path, OtherMvid)) };
        sandbox.Publish(new GateReadyState(expected));
        var actual = Assert.IsType<ReadyInstallation>(sandbox.ReadReady().Installation);
        Assert.True(expected.Images.SequenceEqual(actual.Images));
    }

    [Theory]
    [InlineData(".")]
    [InlineData(" ")]
    public void Noncanonical_root_segments_are_invalid_on_read_and_publish(string suffix)
    {
        using var sandbox = new GateSandbox();
        var installation = Installation(sandbox);
        var state = new GateReadyState(installation with
        {
            Binding = installation.Binding with { AppDirectory = sandbox.AppRoot + suffix }
        });
        sandbox.WriteState(state);
        AssertReadRefusal(sandbox, RefusalReason.InvalidState);
        Assert.Equal(RefusalReason.InvalidState,
            Assert.Throws<AdmissionException>(() => sandbox.Publish(state)).Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative")]
    public void Invalid_canonical_root_retains_its_original_rejection(string? path) =>
        AssertInvariant(() => RuntimePaths.Canonical(path),
            AdmissionInvariant.CanonicalRootInvalid);

    [Fact]
    public void Missing_ready_binding_retains_its_original_rejection()
    {
        // A hostile in-memory value, without weakening the non-nullable production contract.
        var installation = Assert.IsType<ReadyInstallation>(
            System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                typeof(ReadyInstallation)));
        AssertInvariant(() => ReadyValidation.Require(installation),
            AdmissionInvariant.ReadyBindingMissing);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Invalid_ready_inventory_retains_the_existing_combined_guard(int defect)
    {
        using var sandbox = new GateSandbox();
        var valid = Installation(sandbox);
        var invalid = defect switch
        {
            0 => valid with { ProductCode = Guid.Empty },
            1 => valid with { Images = [] },
            2 => valid with { Binding = valid.Binding with { AppDirectory = "relative" } },
            _ => valid with { Images = valid.Images.Add(valid.Images[0]) }
        };
        AssertInvariant(() => ReadyValidation.Require(invalid),
            AdmissionInvariant.ReadyInventoryInvalid);
    }

    [Fact]
    public void Unsupported_ready_protocol_is_still_unannotated()
    {
        using var sandbox = new GateSandbox();
        var valid = Installation(sandbox);
        var invalid = valid with
        {
            Binding = valid.Binding with { AdmissionAbi = HermesServicingProtocol.AdmissionAbi + 1 }
        };
        var error = Assert.Throws<AdmissionException>(() => ReadyValidation.Require(invalid));
        Assert.Equal(RefusalReason.UnsupportedProtocol, error.Reason);
        Assert.Equal(AdmissionInvariant.None, error.Invariant);
        Assert.Null(error.Evidence);
        Assert.Equal(new Exception().HResult, error.HResult);
    }

    [Fact]
    public void Oversized_publication_identifies_the_guard_without_closing_or_writing()
    {
        using var sandbox = new GateSandbox();
        sandbox.Publish(new GateReadyState(null));
        var before = File.ReadAllBytes(sandbox.GatePath);
        var oversized = Installation(sandbox) with
        {
            Images = [new(new string('x', GateCodec.MaximumBodyLength), ServiceMvid)]
        };
        ReadyValidation.Require(oversized);
        AssertInvariant(() => sandbox.Publish(new GateReadyState(oversized)),
            AdmissionInvariant.ReadyBodyTooLarge);
        Assert.Equal(before, File.ReadAllBytes(sandbox.GatePath));
    }

    private static void AssertInvariant(Action action, AdmissionInvariant invariant)
    {
        var error = Assert.Throws<AdmissionException>(action);
        Assert.Equal(RefusalReason.InvalidState, error.Reason);
        Assert.Equal(RefusalReason.InvalidState.ToString(), error.Message);
        Assert.Equal(new Exception().HResult, error.HResult);
        Assert.Equal(invariant, error.Invariant);
        Assert.Null(error.Evidence);
    }

    private static ReadyInstallation Installation(GateSandbox sandbox) => new(
        new(HermesServicingProtocol.UpgradeCode, sandbox.AppRoot,
            HermesServicingProtocol.AdmissionAbi),
        ProductCode, [new(ServicePath, ServiceMvid)]);

    private static JsonObject Body(GateSandbox sandbox) =>
        Assert.IsType<JsonObject>(JsonSerializer.SerializeToNode(
            new GateReadyState(Installation(sandbox)), ServicingJson.Default.GateReadyState));

    private static JsonObject Section(JsonObject body, string section) => section switch
    {
        "" => body,
        "installation" => Assert.IsType<JsonObject>(body["installation"]),
        "binding" => Assert.IsType<JsonObject>(Section(body, "installation")["binding"]),
        "image" => Assert.IsType<JsonObject>(
            Assert.IsType<JsonArray>(Section(body, "installation")["images"])[0]),
        _ => throw new ArgumentException("Unknown synthetic section.", nameof(section))
    };

    private static void AssertReadRefusal(GateSandbox sandbox, RefusalReason reason) =>
        Assert.Equal(reason,
            Assert.Throws<AdmissionException>(() => sandbox.ReadReady()).Reason);

    private sealed class GateSandbox : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), $"hermes-gate-codec-{Guid.NewGuid():N}");

        internal string GatePath => Path.Combine(_directory, "admission.bin");
        internal string AppRoot => Path.Combine(_directory, "App");

        internal GateSandbox() => Directory.CreateDirectory(_directory);

        internal void WriteBody(string text)
        {
            var body = Encoding.UTF8.GetBytes(text);
            File.WriteAllBytes(GatePath,
                [.. GateCodec.Header(GateStatus.Ready, body.Length), .. body]);
        }

        internal void WriteState(GateReadyState state) =>
            WriteBody(JsonSerializer.Serialize(state, ServicingJson.Default.GateReadyState));

        internal GateReadyState ReadReady()
        {
            using var stream = File.OpenRead(GatePath);
            return GateCodec.ReadReady(stream);
        }

        internal void Publish(GateReadyState state)
        {
            using var stream = new FileStream(
                GatePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            GateCodec.PublishReady(stream, state);
        }

        public void Dispose()
        {
            File.Delete(GatePath);
            Directory.Delete(_directory);
        }
    }
}
