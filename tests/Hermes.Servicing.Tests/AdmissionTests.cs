using System.Collections.Immutable;
using System.Text.Json;
using Hermes.Servicing;
using Xunit;

namespace Hermes.Servicing.Tests;

public sealed class AdmissionTests
{
    private static readonly Guid ServiceMvid = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TrayMvid = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid LibraryMvid = new("33333333-3333-3333-3333-333333333333");
    private static readonly string Root = Path.GetFullPath(Path.Combine("synthetic", "Hermes", "App"));

    [Theory]
    [InlineData(RuntimeEntry.Service)]
    [InlineData(RuntimeEntry.Operator)]
    [InlineData(RuntimeEntry.Tray)]
    public void Every_supported_entry_retains_its_lease(RuntimeEntry entry)
    {
        var (client, lease, lifetime) = Create(code: Loaded(entry));
        Assert.IsType<Admitted>(client.Enter(entry));
        Assert.Equal(1, lifetime.RetainedCount);
        Assert.False(lease.Disposed);
    }

    [Fact]
    public void Loaded_entry_mvid_not_replacement_file_identity_controls_admission()
    {
        var oldLoaded = Loaded(RuntimeEntry.Service) with { EntryModuleVersionId = Guid.NewGuid() };
        var (client, lease, lifetime) = Create(code: oldLoaded);
        Assert.Equal(new Refused(RefusalReason.WrongGeneration), client.Enter(RuntimeEntry.Service));
        Assert.True(lease.Disposed);
        Assert.Equal(0, lifetime.RetainedCount);
    }

    [Fact]
    public void Old_loaded_shared_library_is_rejected()
    {
        var loaded = Loaded(RuntimeEntry.Service);
        var oldLibrary = loaded.Images[1] with { ModuleVersionId = Guid.NewGuid() };
        var (client, lease, _) = Create(code: loaded with { Images = [loaded.Images[0], oldLibrary] });
        Assert.Equal(new Refused(RefusalReason.WrongGeneration), client.Enter(RuntimeEntry.Service));
        Assert.True(lease.Disposed);
    }

    [Fact]
    public void Image_outside_the_enrolled_root_is_rejected()
    {
        var loaded = Loaded(RuntimeEntry.Service) with
        {
            EntryAssemblyPath = Path.GetFullPath(Path.Combine("other", "Hermes.Service.dll"))
        };
        var (client, lease, _) = Create(code: loaded);
        Assert.Equal(new Refused(RefusalReason.WrongGeneration), client.Enter(RuntimeEntry.Service));
        Assert.True(lease.Disposed);
    }

    [Fact]
    public void Verified_absent_state_does_not_admit_a_leftover_executable()
    {
        var (client, lease, _) = Create(state: new GateReadyState(null));
        Assert.Equal(new Refused(RefusalReason.UnownedInstallation), client.Enter(RuntimeEntry.Service));
        Assert.True(lease.Disposed);
    }

    [Fact]
    public void Busy_gate_never_reads_loaded_code_or_retains_a_lease()
    {
        var lifetime = new FakeLifetime();
        var client = new StartupAdmission(
            new FixedGate(new GateUnavailable(RefusalReason.Busy)),
            new UnexpectedLoadedCode(), lifetime);
        Assert.Equal(new Refused(RefusalReason.Busy), client.Enter(RuntimeEntry.Service));
        Assert.Equal(0, lifetime.RetainedCount);
    }

    [Fact]
    public void Failed_gate_read_disposes_without_retention()
    {
        var lease = new FakeLease(Ready(), failRead: true);
        var lifetime = new FakeLifetime();
        var client = new StartupAdmission(new FixedGate(new GateOpened(lease)),
            new UnexpectedLoadedCode(), lifetime);
        Assert.Equal(new Refused(RefusalReason.InvalidState), client.Enter(RuntimeEntry.Service));
        Assert.True(lease.Disposed);
        Assert.Equal(0, lifetime.RetainedCount);
    }

    [Theory]
    [InlineData("../user-owned.dll")]
    [InlineData("service/../user-owned.dll")]
    [InlineData("service\\Hermes.Service.dll")]
    public void Manifest_path_aliases_are_rejected(string path)
    {
        var installation = Installation() with { Images = [new(path, ServiceMvid)] };
        var (client, lease, _) = Create(new GateReadyState(installation));
        Assert.Equal(new Refused(RefusalReason.InvalidState), client.Enter(RuntimeEntry.Service));
        Assert.True(lease.Disposed);
    }

    [Theory]
    [InlineData("service/Hermes.Service.dll.")]
    [InlineData("service/Hermes.Service.dll ")]
    [InlineData("service./Hermes.Service.dll")]
    [InlineData("service /Hermes.Service.dll")]
    [InlineData("SERVICE/HERMES.SERVICE.DLL")]
    public void Contradictory_surplus_inventory_is_not_ignored(string path)
    {
        var installation = Installation();
        var contradictory = installation with
        {
            Images = installation.Images.Add(new(path, LibraryMvid))
        };
        var (client, lease, lifetime) = Create(new GateReadyState(contradictory));
        Assert.Equal(new Refused(RefusalReason.InvalidState), client.Enter(RuntimeEntry.Service));
        Assert.True(lease.Disposed);
        Assert.Equal(0, lifetime.RetainedCount);
    }

    [Fact]
    public void A_single_case_variant_of_each_inventory_path_remains_valid()
    {
        var installation = Installation();
        var caseVariant = installation with
        {
            Images = installation.Images.Select(image =>
                image with { RelativePath = image.RelativePath.ToUpperInvariant() }).ToImmutableArray()
        };
        var (client, lease, lifetime) = Create(new GateReadyState(caseVariant));
        Assert.IsType<Admitted>(client.Enter(RuntimeEntry.Service));
        Assert.False(lease.Disposed);
        Assert.Equal(1, lifetime.RetainedCount);
    }

    [Fact]
    public void Unknown_abi_is_not_treated_as_ready()
    {
        var installation = Installation();
        var incompatible = installation with
        {
            Binding = installation.Binding with { AdmissionAbi = 2 }
        };
        var (client, _, _) = Create(new GateReadyState(incompatible));
        Assert.Equal(new Refused(RefusalReason.UnsupportedProtocol), client.Enter(RuntimeEntry.Service));
    }

    [Theory]
    [InlineData(0, 0, RefusalReason.InvalidState)]
    [InlineData(8, 2, RefusalReason.UnsupportedProtocol)]
    [InlineData(9, 67, RefusalReason.Closed)]
    [InlineData(9, 0, RefusalReason.InvalidState)]
    [InlineData(10, 1, RefusalReason.InvalidState)]
    public void Header_corruption_never_admits(int offset, byte value, RefusalReason reason)
    {
        var bytes = Encode(Ready());
        bytes[offset] = value;
        using var stream = new MemoryStream(bytes);
        var error = Assert.Throws<AdmissionException>(() => GateCodec.ReadReady(stream));
        Assert.Equal(reason, error.Reason);
    }

    [Fact]
    public void Ready_body_round_trips_without_changing_image_identities()
    {
        using var stream = new MemoryStream(Encode(Ready()));
        var parsed = GateCodec.ReadReady(stream);
        var installation = Assert.IsType<ReadyInstallation>(parsed.Installation);
        Assert.Equal(Installation().ProductCode, installation.ProductCode);
        Assert.True(Installation().Images.SequenceEqual(installation.Images));
    }

    [Fact]
    public void Truncated_ready_body_is_not_completion()
    {
        var bytes = Encode(Ready());
        using var stream = new MemoryStream(bytes[..^1]);
        var error = Assert.Throws<AdmissionException>(() => GateCodec.ReadReady(stream));
        Assert.Equal(RefusalReason.InvalidState, error.Reason);
    }

    [Fact]
    public void Install_parser_preserves_only_the_supported_options()
    {
        var package = Path.GetFullPath("candidate.msi");
        var parsed = Assert.IsType<ParsedCommand>(SetupCommandLine.Parse(
            ["install", "--package", package, "--quiet", "--allow-rollback"]));
        Assert.Equal(new InstallCommand(package, true, true), parsed.Command);
    }

    [Theory]
    [InlineData("repair", "--allow-rollback")]
    [InlineData("uninstall", "--package")]
    [InlineData("recover", "HERMES_SERVICING_RUN=anything")]
    [InlineData("repair", "--root")]
    public void Maintenance_has_no_property_or_root_bypass(string verb, string flag) =>
        Assert.IsType<InvalidCommand>(SetupCommandLine.Parse([verb, flag]));

    [Fact]
    public void Duplicate_flags_and_relative_packages_are_rejected()
    {
        Assert.IsType<InvalidCommand>(SetupCommandLine.Parse(["repair", "--quiet", "--quiet"]));
        Assert.IsType<InvalidCommand>(SetupCommandLine.Parse(["install", "--package", "relative.msi"]));
    }

    [Fact]
    public void Report_is_bounded_typed_json_without_exception_or_path_content()
    {
        var json = new SetupReport(SetupVerb.Recover,
            SetupOutcome.RecoveryRequired, false, 1618).ToJson();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Recover", root.GetProperty("operation").GetString());
        Assert.Equal("RecoveryRequired", root.GetProperty("outcome").GetString());
        Assert.False(root.GetProperty("ready").GetBoolean());
        Assert.Equal(1618, root.GetProperty("msiExitCode").GetInt32());
    }

    private static (StartupAdmission Client, FakeLease Lease, FakeLifetime Lifetime) Create(
        GateReadyState? state = null, LoadedCode? code = null)
    {
        var lease = new FakeLease(state ?? Ready());
        var lifetime = new FakeLifetime();
        return (new(new FixedGate(new GateOpened(lease)),
            new FixedLoadedCode(code ?? Loaded(RuntimeEntry.Service)), lifetime), lease, lifetime);
    }

    private static ReadyInstallation Installation() => new(
        new(HermesServicingProtocol.UpgradeCode, Root, HermesServicingProtocol.AdmissionAbi),
        new Guid("44444444-4444-4444-4444-444444444444"),
        [new("service/Hermes.Service.dll", ServiceMvid),
         new("tray/Hermes.Tray.dll", TrayMvid),
         new("service/Hermes.Servicing.dll", LibraryMvid),
         new("tray/Hermes.Servicing.dll", LibraryMvid)]);

    private static GateReadyState Ready() => new(Installation());

    private static LoadedCode Loaded(RuntimeEntry entry)
    {
        var tray = entry == RuntimeEntry.Tray;
        var directory = tray ? "tray" : "service";
        var main = Path.Combine(Root, directory, tray ? "Hermes.Tray.dll" : "Hermes.Service.dll");
        var mvid = tray ? TrayMvid : ServiceMvid;
        return new(main, mvid,
            [new(main, mvid), new(Path.Combine(Root, directory, "Hermes.Servicing.dll"), LibraryMvid)]);
    }

    private static byte[] Encode(GateReadyState state)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(state, ServicingJson.Default.GateReadyState);
        return [.. GateCodec.Header(GateStatus.Ready, body.Length), .. body];
    }

    private sealed record FixedGate(GateOpen Value) : IRuntimeGate
    {
        public GateOpen TryOpen() => Value;
    }

    private sealed record FixedLoadedCode(LoadedCode Value) : ILoadedCode
    {
        public LoadedCode Read() => Value;
    }

    private sealed class UnexpectedLoadedCode : ILoadedCode
    {
        public LoadedCode Read() => throw new InvalidOperationException("Unexpected code inspection.");
    }

    private sealed class FakeLease(GateReadyState state, bool failRead = false) : IRuntimeReadLease
    {
        private bool _disposed;
        internal bool Disposed => _disposed;
        public GateReadyState ReadReady() =>
            failRead ? throw new IOException("Synthetic failure.") : state;
        public void Dispose() => _disposed = true;
    }

    private sealed class FakeLifetime : ILeaseLifetime
    {
        private readonly List<IRuntimeReadLease> _retained = [];
        internal int RetainedCount => _retained.Count;
        public IProcessLease Retain(IRuntimeReadLease lease)
        {
            _retained.Add(lease);
            return new Token();
        }
        private sealed record Token : IProcessLease;
    }
}
