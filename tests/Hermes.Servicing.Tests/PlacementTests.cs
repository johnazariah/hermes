using System.Collections.Immutable;
using Hermes.Servicing;
using Xunit;

namespace Hermes.Servicing.Tests;

public sealed class PlacementTests
{
    private const string ServiceRelative = "service/Hermes.Service.dll";
    private static readonly Guid ServiceMvid = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherMvid = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid LibraryMvid = new("33333333-3333-3333-3333-333333333333");
    private static readonly string Base = Path.GetFullPath(Path.Combine("synthetic", "placement"));
    private static readonly string Root = Path.Combine(Base, "Hermes", "App");
    private static readonly string Source = Path.Combine(Base, "Source");

    public enum ReadyScenario
    {
        Valid, VerifiedAbsent, OtherRoot, OldMvid, BadInventory,
        UnsupportedAbi, Closed, Malformed
    }

    [Theory]
    [InlineData(RuntimeEntry.Service)]
    [InlineData(RuntimeEntry.Operator)]
    [InlineData(RuntimeEntry.Tray)]
    public void Positively_outside_code_can_run_without_a_gate(RuntimeEntry entry)
    {
        var (client, gate, _, lifetime) = Create(Code(Source),
            unavailable: new GateUnavailable(RefusalReason.UnownedInstallation));
        Assert.IsType<OutsideKnownInstallation>(client.Enter(entry));
        Assert.Equal(1, gate.Opens);
        Assert.Equal(0, lifetime.Retained);
    }

    [Theory]
    [InlineData("App", false)]
    [InlineData("APP", false)]
    [InlineData("AppSibling", true)]
    [InlineData("App.old", true)]
    public void Root_containment_is_component_and_case_aware(string name, bool outside)
    {
        var directory = Path.Combine(Base, "Hermes", name, "service");
        var (client, _, _, _) = Create(Code(directory),
            unavailable: new GateUnavailable(RefusalReason.UnownedInstallation));
        var result = client.Enter(RuntimeEntry.Service);
        if (outside)
            Assert.IsType<OutsideKnownInstallation>(result);
        else
            Assert.Equal(new Refused(RefusalReason.UnownedInstallation), result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mixed_entry_and_library_placement_refuses_before_opening_gate(bool entryInside)
    {
        var code = Code(entryInside ? Path.Combine(Root, "service") : Source);
        var library = Path.Combine(entryInside ? Source : Root, "Hermes.Servicing.dll");
        var mixed = code with { Images = [code.Images[0], new(library, LibraryMvid)] };
        var (client, gate, _, lifetime) = Create(mixed);
        Assert.Equal(new Refused(RefusalReason.WrongGeneration), client.Enter(RuntimeEntry.Service));
        Assert.Equal(0, gate.Opens);
        Assert.Equal(0, lifetime.Retained);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Physical_image_or_root_alias_into_installation_is_not_outside(bool rootAlias)
    {
        var code = Code(Source);
        var physical = rootAlias
            ? new TestPaths(directory: Source, image: code.EntryAssemblyPath)
            : new TestPaths(image: Path.Combine(Root, ServiceRelative));
        var (client, gate, _, _) = Create(code, physical: physical);
        Assert.Equal(new Refused(RefusalReason.WrongGeneration), client.Enter(RuntimeEntry.Service));
        Assert.Equal(0, gate.Opens);
    }

    [Fact]
    public void A_resolved_alias_still_wholly_outside_can_be_positive()
    {
        var physical = new TestPaths(
            directory: Path.Combine(Base, "Physical", "App"),
            image: Path.Combine(Base, "Physical", "Source", "Hermes.Service.dll"));
        var (client, _, _, _) = Create(Code(Source), physical: physical,
            unavailable: new GateUnavailable(RefusalReason.UnownedInstallation));
        Assert.IsType<OutsideKnownInstallation>(client.Enter(RuntimeEntry.Service));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Case_distinct_loaded_paths_cannot_hide_inside_or_unavailable_images(bool unavailable)
    {
        var code = CaseDistinctCode();
        var lower = code.Images[1].AssemblyPath;
        var physical = new RecordingPaths(path => (path == lower, unavailable) switch
        {
            (true, true) => throw new AdmissionException(RefusalReason.Unavailable),
            (true, false) => Path.Combine(Root, ServiceRelative),
            _ => path
        });
        var (client, gate, _, lifetime) = Create(code, physical: physical,
            unavailable: new GateUnavailable(RefusalReason.UnownedInstallation));
        var reason = unavailable ? RefusalReason.Unavailable : RefusalReason.WrongGeneration;
        Assert.Equal(new Refused(reason), client.Enter(RuntimeEntry.Service));
        Assert.Equal(new[] { Root, code.EntryAssemblyPath, lower }, physical.Reads);
        Assert.Equal(0, gate.Opens);
        Assert.Equal(0, lifetime.Retained);
    }

    [Fact]
    public void Case_distinct_loaded_paths_are_all_probed_before_opening_the_gate()
    {
        var code = CaseDistinctCode();
        var physical = new RecordingPaths(path => path);
        string[] expected =
            [Root, code.EntryAssemblyPath, code.Images[1].AssemblyPath, code.Images[2].AssemblyPath];
        var (client, gate, _, lifetime) = Create(code, physical: physical,
            unavailable: new GateUnavailable(RefusalReason.UnownedInstallation),
            beforeOpen: () => Assert.Equal(expected, physical.Reads));
        Assert.IsType<OutsideKnownInstallation>(client.Enter(RuntimeEntry.Service));
        Assert.Equal(expected, physical.Reads);
        Assert.Equal(1, gate.Opens);
        Assert.Equal(0, lifetime.Retained);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_physical_evidence_is_not_outside(bool failDirectory)
    {
        var physical = new TestPaths(
            failDirectory: failDirectory, failImage: !failDirectory);
        var (client, gate, _, _) = Create(Code(Source), physical: physical,
            unavailable: new GateUnavailable(RefusalReason.UnownedInstallation));
        Assert.Equal(new Refused(RefusalReason.Unavailable), client.Enter(RuntimeEntry.Service));
        Assert.Equal(0, gate.Opens);
    }

    [Theory]
    [InlineData("Hermes.Service.dll.")]
    [InlineData("Hermes.Service.dll ")]
    [InlineData("directory./Hermes.Service.dll")]
    [InlineData("directory /Hermes.Service.dll")]
    public void Ambiguous_loaded_paths_cannot_establish_outside_placement(string path)
    {
        var code = Code(Source) with { EntryAssemblyPath = Path.Combine(Source, path) };
        var (client, gate, _, _) = Create(code);
        Assert.Equal(new Refused(RefusalReason.InvalidState), client.Enter(RuntimeEntry.Service));
        Assert.Equal(0, gate.Opens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Incomplete_loaded_identity_cannot_establish_outside_placement(bool emptyImages)
    {
        var code = Code(Source);
        var incomplete = emptyImages
            ? code with { Images = [] }
            : code with { EntryModuleVersionId = Guid.Empty };
        var (client, gate, _, _) = Create(incomplete);
        Assert.Equal(new Refused(RefusalReason.InvalidState), client.Enter(RuntimeEntry.Service));
        Assert.Equal(0, gate.Opens);
    }

    [Theory]
    [InlineData(ReadyScenario.Valid, null)]
    [InlineData(ReadyScenario.VerifiedAbsent, RefusalReason.UnownedInstallation)]
    [InlineData(ReadyScenario.OtherRoot, RefusalReason.UnownedInstallation)]
    [InlineData(ReadyScenario.OldMvid, RefusalReason.WrongGeneration)]
    [InlineData(ReadyScenario.BadInventory, RefusalReason.InvalidState)]
    [InlineData(ReadyScenario.UnsupportedAbi, RefusalReason.UnsupportedProtocol)]
    [InlineData(ReadyScenario.Closed, RefusalReason.Closed)]
    [InlineData(ReadyScenario.Malformed, RefusalReason.InvalidState)]
    public void Installed_branch_keeps_strict_state_and_lease_rules(
        ReadyScenario scenario, RefusalReason? reason)
    {
        var (client, gate, lease, lifetime) = Create(Code(Path.Combine(Root, "service")),
            scenario: scenario, physical: new TestPaths(failDirectory: true, failImage: true));
        var result = client.Enter(RuntimeEntry.Service);
        Assert.Equal(1, gate.Opens);
        if (reason is { } refusal)
        {
            Assert.Equal(new Refused(refusal), result);
            Assert.True(lease.Disposed);
            Assert.Equal(0, lifetime.Retained);
        }
        else
        {
            Assert.IsType<Admitted>(result);
            Assert.False(lease.Disposed);
            Assert.Equal(1, lifetime.Retained);
        }
    }

    [Theory]
    [InlineData(ReadyScenario.Valid, null)]
    [InlineData(ReadyScenario.VerifiedAbsent, null)]
    [InlineData(ReadyScenario.OtherRoot, RefusalReason.UnownedInstallation)]
    [InlineData(ReadyScenario.OldMvid, null)]
    [InlineData(ReadyScenario.BadInventory, RefusalReason.InvalidState)]
    [InlineData(ReadyScenario.UnsupportedAbi, RefusalReason.UnsupportedProtocol)]
    [InlineData(ReadyScenario.Closed, RefusalReason.Closed)]
    [InlineData(ReadyScenario.Malformed, RefusalReason.InvalidState)]
    public void Existing_gate_evidence_is_not_ignored_after_outside_classification(
        ReadyScenario scenario, RefusalReason? reason)
    {
        var (client, _, lease, lifetime) = Create(Code(Source), scenario: scenario);
        var result = client.Enter(RuntimeEntry.Service);
        if (reason is { } refusal)
            Assert.Equal(new Refused(refusal), result);
        else
            Assert.IsType<OutsideKnownInstallation>(result);
        Assert.True(lease.Disposed);
        Assert.Equal(0, lifetime.Retained);
    }

    [Fact]
    public void Busy_is_not_reinterpreted_as_an_absent_gate_for_outside_code()
    {
        var (client, _, _, lifetime) = Create(Code(Source),
            unavailable: new GateUnavailable(RefusalReason.Busy));
        Assert.Equal(new Refused(RefusalReason.Busy), client.Enter(RuntimeEntry.Service));
        Assert.Equal(0, lifetime.Retained);
    }

    private static LoadedCode CaseDistinctCode()
    {
        var code = Code(Path.Combine(Base, "Dev", "Build"));
        var lower = Path.Combine(Base, "Dev", "build", "Hermes.Service.dll");
        return code with
        {
            Images = [code.Images[0], new(lower, ServiceMvid), code.Images[1]]
        };
    }

    private static LoadedCode Code(string directory)
    {
        var entry = Path.Combine(directory, "Hermes.Service.dll");
        return new(entry, ServiceMvid,
            [new(entry, ServiceMvid),
             new(Path.Combine(directory, "Hermes.Servicing.dll"), LibraryMvid)]);
    }

    private static ReadyInstallation Installation() => new(
        new(HermesServicingProtocol.UpgradeCode, Root, HermesServicingProtocol.AdmissionAbi),
        new Guid("44444444-4444-4444-4444-444444444444"),
        [new(ServiceRelative, ServiceMvid), new("service/Hermes.Servicing.dll", LibraryMvid)]);

    private static GateReadyState State(ReadyScenario scenario)
    {
        var installation = Installation();
        return new(scenario switch
        {
            ReadyScenario.Valid or ReadyScenario.Closed or ReadyScenario.Malformed => installation,
            ReadyScenario.VerifiedAbsent => null,
            ReadyScenario.OtherRoot => installation with
            { Binding = installation.Binding with { AppDirectory = Path.Combine(Base, "Legacy") } },
            ReadyScenario.OldMvid => installation with
            { Images = installation.Images.SetItem(0, new(ServiceRelative, OtherMvid)) },
            ReadyScenario.BadInventory => installation with
            { Images = installation.Images.Add(new(ServiceRelative + ".", OtherMvid)) },
            ReadyScenario.UnsupportedAbi => installation with
            { Binding = installation.Binding with { AdmissionAbi = 2 } },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        });
    }

    private static RefusalReason? ReadFailure(ReadyScenario scenario) => scenario switch
    {
        ReadyScenario.Closed => RefusalReason.Closed,
        ReadyScenario.Malformed => RefusalReason.InvalidState,
        ReadyScenario.Valid or ReadyScenario.VerifiedAbsent or ReadyScenario.OtherRoot or
            ReadyScenario.OldMvid or ReadyScenario.BadInventory or ReadyScenario.UnsupportedAbi => null,
        _ => throw new ArgumentOutOfRangeException(nameof(scenario))
    };

    private static (IStartupAdmission Client, CountingGate Gate, TestLease Lease, TestLifetime Lifetime)
        Create(LoadedCode code, ReadyScenario scenario = ReadyScenario.Valid,
            IPhysicalPaths? physical = null, GateUnavailable? unavailable = null,
            Action? beforeOpen = null)
    {
        var lease = new TestLease(State(scenario), ReadFailure(scenario));
        GateOpen result = unavailable is { } failure ? failure : new GateOpened(lease);
        var gate = new CountingGate(result, beforeOpen);
        var lifetime = new TestLifetime();
        return (WindowsStartupAdmission.Create(
            Root, physical ?? new TestPaths(), gate, new FixedLoaded(code), lifetime),
            gate, lease, lifetime);
    }

    private sealed record FixedLoaded(LoadedCode Code) : ILoadedCode
    {
        public LoadedCode Read() => Code;
    }

    private sealed class CountingGate(GateOpen result, Action? beforeOpen = null) : IRuntimeGate
    {
        private int _opens;
        internal int Opens => _opens;
        public GateOpen TryOpen()
        {
            beforeOpen?.Invoke();
            _opens++;
            return result;
        }
    }

    private sealed class TestLease(GateReadyState state, RefusalReason? failure) : IRuntimeReadLease
    {
        private bool _disposed;
        internal bool Disposed => _disposed;
        public GateReadyState ReadReady() =>
            failure is { } reason ? throw new AdmissionException(reason) : state;
        public void Dispose() => _disposed = true;
    }

    private sealed class TestLifetime : ILeaseLifetime
    {
        private int _retained;
        internal int Retained => _retained;
        public IProcessLease Retain(IRuntimeReadLease lease)
        {
            _retained++;
            return new Token();
        }
        private sealed record Token : IProcessLease;
    }

    private sealed class RecordingPaths(Func<string, string> resolveImage) : IPhysicalPaths
    {
        private ImmutableArray<string> _reads = [];
        internal IReadOnlyList<string> Reads => _reads;

        public string Directory(string path)
        {
            _reads = _reads.Add(path);
            return path;
        }

        public string Image(string path)
        {
            _reads = _reads.Add(path);
            return resolveImage(path);
        }
    }

    private sealed class TestPaths(
        string? directory = null, string? image = null,
        bool failDirectory = false, bool failImage = false) : IPhysicalPaths
    {
        public string Directory(string path) => failDirectory
            ? throw new AdmissionException(RefusalReason.Unavailable) : directory ?? path;
        public string Image(string path) => failImage
            ? throw new AdmissionException(RefusalReason.Unavailable) : image ?? path;
    }
}
