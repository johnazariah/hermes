using System.Collections.Immutable;
using Hermes.Servicing;
using Xunit;

namespace Hermes.Servicing.Tests;

public sealed class EnrolledServiceTests
{
    private const string EntryRelative = "service/Hermes.Service.dll";
    private const string LibraryRelative = "service/Hermes.Servicing.dll";
    private static readonly string Root = Path.GetFullPath("synthetic-enrolled-service");
    private static readonly string Directory = Path.Combine(Root, "service");
    private static readonly string Executable = Path.Combine(Directory, "Hermes.Service.exe");
    private static readonly string Entry = Path.Combine(Directory, "Hermes.Service.dll");
    private static readonly string Library = Path.Combine(Directory, "Hermes.Servicing.dll");
    private static readonly Guid EntryMvid = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LibraryMvid = typeof(StartupAdmission).Assembly.ManifestModule.ModuleVersionId;
    private static readonly string[] PhysicalOrder = [Root, Directory, Executable, Entry, Library];

    [Fact]
    public void Fixed_enrolled_child_is_physically_checked_before_its_metadata_is_read()
    {
        var paths = new RecordingPaths(path => path);
        var metadata = new RecordingMetadata(path =>
        {
            Assert.Contains(path, paths.Reads);
            return ExpectedMvid(path);
        });
        var capability = Capability(Installation(), paths, metadata);
        Assert.Equal(new ServiceSelected(Executable), capability.SelectService(Executable));
        Assert.Equal(PhysicalOrder, paths.Reads);
        Assert.Equal([Entry, Library], metadata.Reads);
        Assert.False(capability is IDisposable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fixed_child_accepts_native_or_forward_slash_apphost_paths(bool forwardSlashes)
    {
        var supplied = forwardSlashes
            ? Executable.Replace(Path.DirectorySeparatorChar, '/')
            : Executable;
        var paths = new RecordingPaths(path => path);
        var metadata = Metadata();
        Assert.Equal(new ServiceSelected(Executable),
            Capability(Installation(), paths, metadata).SelectService(supplied));
        Assert.Equal(PhysicalOrder, paths.Reads);
        Assert.Equal([Entry, Library], metadata.Reads);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Redirected_root_directory_apphost_or_managed_image_is_rejected(int redirected)
    {
        var paths = new RecordingPaths(path =>
            path == PhysicalOrder[redirected] ? path + ".redirected" : path);
        var metadata = Metadata();
        var result = Capability(Installation(), paths, metadata).SelectService(Executable);
        Assert.Equal(new ServiceRefused(RefusalReason.UnownedInstallation), result);
        Assert.Equal(PhysicalOrder.Take(redirected + 1), paths.Reads);
        Assert.Equal(Math.Max(0, redirected - 3), metadata.Reads.Count);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("sibling")]
    [InlineData("legacy")]
    public void Overrides_outside_the_fixed_service_entry_are_not_probed_or_adopted(string kind)
    {
        var supplied = kind switch
        {
            "source" => Path.GetFullPath("development/Hermes.Service.exe"),
            "sibling" => Path.Combine(Root + ".old", "service", "Hermes.Service.exe"),
            _ => Path.Combine(Root, "Hermes.Service.exe")
        };
        var paths = new RecordingPaths(path => path);
        var metadata = Metadata();
        Assert.Equal(new ServiceRefused(RefusalReason.UnownedInstallation),
            Capability(Installation(), paths, metadata).SelectService(supplied));
        Assert.Empty(paths.Reads);
        Assert.Empty(metadata.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Incompatible_entry_or_shared_library_is_rejected(bool library)
    {
        var paths = new RecordingPaths(path => path);
        var metadata = new RecordingMetadata(path =>
            path == (library ? Library : Entry) ? Guid.Empty :
                ExpectedMvid(path));
        Assert.Equal(new ServiceRefused(RefusalReason.WrongGeneration),
            Capability(Installation(), paths, metadata).SelectService(Executable));
        Assert.Equal(library ? new[] { Entry, Library } : [Entry], metadata.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unenrolled_or_legacy_service_is_rejected_before_metadata_reads(bool legacy)
    {
        var installation = Installation() with
        {
            Images = legacy ? [new(EntryRelative, EntryMvid)] : [new(LibraryRelative, LibraryMvid)]
        };
        var metadata = Metadata();
        var result = Capability(installation, new RecordingPaths(path => path), metadata)
            .SelectService(Executable);
        Assert.Equal(new ServiceRefused(legacy
            ? RefusalReason.WrongGeneration : RefusalReason.UnownedInstallation), result);
        Assert.Empty(metadata.Reads);
    }

    [Fact]
    public void An_enrolled_but_incompatible_servicing_library_is_not_accepted()
    {
        var installation = Installation() with
        {
            Images = [new(EntryRelative, EntryMvid), new(LibraryRelative, Guid.NewGuid())]
        };
        var paths = new RecordingPaths(path => path);
        var metadata = new RecordingMetadata(path => path == Library
            ? installation.Images[1].ModuleVersionId : ExpectedMvid(path));
        Assert.Equal(new ServiceRefused(RefusalReason.WrongGeneration),
            Capability(installation, paths, metadata).SelectService(Executable));
        Assert.Equal(PhysicalOrder.Take(3), paths.Reads);
        Assert.Empty(metadata.Reads);
    }

    [Fact]
    public void Ambiguous_inventory_keeps_the_existing_strict_validation_contract()
    {
        var installation = Installation();
        installation = installation with { Images = installation.Images.Add(installation.Images[0]) };
        var paths = new RecordingPaths(path => path);
        Assert.Equal(new ServiceRefused(RefusalReason.InvalidState),
            Capability(installation, paths, Metadata()).SelectService(Executable));
        Assert.Empty(paths.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_physical_or_invalid_metadata_evidence_never_selects_a_child(bool metadataFailure)
    {
        var paths = new RecordingPaths(path => metadataFailure
            ? path : throw new AdmissionException(RefusalReason.Unavailable));
        var metadata = new RecordingMetadata(_ => throw new BadImageFormatException());
        Assert.Equal(new ServiceRefused(metadataFailure
            ? RefusalReason.InvalidState : RefusalReason.Unavailable),
            Capability(Installation(), paths, metadata).SelectService(Executable));
        Assert.Equal(metadataFailure ? 1 : 0, metadata.Reads.Count);
    }

    [Fact]
    public void Production_fixed_root_composition_retains_before_exposing_child_capability()
    {
        var tray = Path.Combine(Root, "tray", "Hermes.Tray.dll");
        var installation = Installation();
        installation = installation with { Images = installation.Images.Add(new("tray/Hermes.Tray.dll", EntryMvid)) };
        var lease = new ReadLease(new(installation));
        var lifetime = new Retention();
        var paths = new RecordingPaths(path => path);
        var startup = WindowsStartupAdmission.Create(Root, paths,
            new Gate(lease), new Code(new(tray, EntryMvid, [new(tray, EntryMvid)])), lifetime);
        var admitted = Assert.IsType<Admitted>(startup.Enter(RuntimeEntry.Tray));
        Assert.IsAssignableFrom<IEnrolledServiceLease>(admitted.Lease);
        Assert.Equal(1, lifetime.Count);
        Assert.False(lease.Disposed);
        Assert.False(admitted.Lease is IDisposable);
        Assert.Empty(paths.Reads);
    }

    [Fact]
    public void Metadata_reader_uses_the_file_module_identity_without_loading_it()
    {
        var assembly = typeof(StartupAdmission).Assembly;
        Assert.Equal(assembly.ManifestModule.ModuleVersionId,
            new PortableExecutableMetadata().ReadModuleVersionId(assembly.Location));
    }

    [Fact]
    public void Metadata_reader_rejects_nonmanaged_bytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hermes-invalid-image-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            Assert.Throws<BadImageFormatException>(() =>
                new PortableExecutableMetadata().ReadModuleVersionId(path));
        }
        finally { File.Delete(path); }
    }

    private static ReadyInstallation Installation() => new(
        new(HermesServicingProtocol.UpgradeCode, Root, HermesServicingProtocol.AdmissionAbi),
        new Guid("22222222-2222-2222-2222-222222222222"),
        [new(EntryRelative, EntryMvid), new(LibraryRelative, LibraryMvid)]);

    private static IEnrolledServiceLease Capability(
        ReadyInstallation installation, IPhysicalPaths paths, IManagedImageMetadata metadata) =>
        new EnrolledServiceLease(new Token(), installation, paths, metadata);

    private static RecordingMetadata Metadata() =>
        new(ExpectedMvid);

    private static Guid ExpectedMvid(string path) => path switch
    {
        var value when value == Entry => EntryMvid,
        var value when value == Library => LibraryMvid,
        _ => throw new Xunit.Sdk.XunitException($"Unexpected managed image path: {path}")
    };

    private sealed record Token : IProcessLease;

    private sealed class RecordingPaths(Func<string, string> resolve) : IPhysicalPaths
    {
        private ImmutableArray<string> _reads = [];
        internal IReadOnlyList<string> Reads => _reads;
        public string Directory(string path) => Read(path);
        public string Image(string path) => Read(path);
        private string Read(string path)
        {
            _reads = _reads.Add(path);
            return resolve(path);
        }
    }

    private sealed class RecordingMetadata(Func<string, Guid> read) : IManagedImageMetadata
    {
        private ImmutableArray<string> _reads = [];
        internal IReadOnlyList<string> Reads => _reads;
        public Guid ReadModuleVersionId(string path)
        {
            _reads = _reads.Add(path);
            return read(path);
        }
    }

    private sealed record Gate(IRuntimeReadLease Lease) : IRuntimeGate
    {
        public GateOpen TryOpen() => new GateOpened(Lease);
    }

    private sealed record Code(LoadedCode Loaded) : ILoadedCode
    {
        public LoadedCode Read() => Loaded;
    }

    private sealed class ReadLease(GateReadyState state) : IRuntimeReadLease
    {
        private bool _disposed;
        internal bool Disposed => _disposed;
        public GateReadyState ReadReady() => state;
        public void Dispose() => _disposed = true;
    }

    private sealed class Retention : ILeaseLifetime
    {
        private ImmutableArray<IRuntimeReadLease> _leases = [];
        internal int Count => _leases.Length;
        public IProcessLease Retain(IRuntimeReadLease lease)
        {
            _leases = _leases.Add(lease);
            return new Token();
        }
    }
}
