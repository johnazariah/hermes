using System.Runtime.InteropServices;
using Hermes.Servicing;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed class ShellLinkReaderTests
{
    private const string Link = "C:/Synthetic/Programs/Hermes/Hermes.lnk";
    private const string Redirected = "C:/Synthetic/Elsewhere/Hermes.lnk";
    private static readonly ShortcutFields Fields =
        new("C:/Synthetic/App/tray.exe", "--synthetic", "C:/Synthetic/App");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Proven_missing_file_or_ancestor_reaches_absent_inventory(bool parentExists)
    {
        using var world = new RootFixture();
        var parent = Path.Combine(RootFixture.Programs, "Hermes");
        if (parentExists)
            world.Files.CreateDirectory(parent);
        var reader = new ShellLinkReader(world.Paths, world.Files, UnexpectedLoader());
        var observed = reader.Read(Path.Combine(parent, "Hermes.lnk"));
        Assert.Equal(ReadObservation<ShortcutFields>.Missing(), observed);
        world.Registry.Set(world.Registry.Current with { Shortcut = observed });
        var proof = Proof(world);
        Assert.True(proof.Matches(null, [], proof.Read([])));
        Assert.Equal(parentExists ? DiskEntryKind.Directory : DiskEntryKind.Missing,
            world.Files.Entry(parent).Kind);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void Missing_entry_requires_physical_identity_before_and_after_observation(
        int failedRead, bool redirected)
    {
        var reads = 0;
        var paths = new Paths(path => ++reads == failedRead
            ? redirected
                ? ReadObservation<PathIdentity>.Known(new(Redirected))
                : ReadObservation<PathIdentity>.Unknown(ObservationFault.Identity, 5)
            : ReadObservation<PathIdentity>.Known(new(path)));
        var reader = Reader(new(DiskEntryKind.Missing, false), paths: paths);
        Assert.Equal(ReadObservation<ShortcutFields>.Unknown(ObservationFault.Identity),
            reader.Read(Link));
        Assert.Equal(failedRead, reads);
    }

    [Fact]
    public void A_missing_identity_record_is_not_positive_ancestor_proof()
    {
        var paths = new Paths(_ => ReadObservation<PathIdentity>.Missing());
        var reader = Reader(new(DiskEntryKind.Missing, false), paths: paths);
        Assert.Equal(ReadObservation<ShortcutFields>.Unknown(ObservationFault.Identity),
            reader.Read(Link));
    }

    [Theory]
    [InlineData(DiskEntryKind.File, true)]
    [InlineData(DiskEntryKind.Directory, false)]
    [InlineData(DiskEntryKind.Directory, true)]
    [InlineData(DiskEntryKind.Missing, true)]
    public void Reparse_entries_and_directory_collisions_never_load_or_become_absent(
        object kind, bool reparse)
    {
        var entry = new DiskEntry(Assert.IsType<DiskEntryKind>(kind), reparse);
        Assert.Equal(ReadObservation<ShortcutFields>.Unknown(ObservationFault.Identity),
            Reader(entry).Read(Link));
    }

    [Theory]
    [InlineData(DiskEntryKind.File, false)]
    [InlineData(DiskEntryKind.Directory, false)]
    [InlineData(DiskEntryKind.Missing, true)]
    public void A_collision_during_the_missing_probe_is_not_absence(
        object kind, bool reparse)
    {
        var reads = 0;
        var changed = new DiskEntry(Assert.IsType<DiskEntryKind>(kind), reparse);
        var files = new Files(_ => ++reads == 1
            ? new(DiskEntryKind.Missing, false) : changed);
        var reader = new ShellLinkReader(StablePaths(), files, UnexpectedLoader());
        Assert.Equal(ReadObservation<ShortcutFields>.Unknown(ObservationFault.Identity),
            reader.Read(Link));
        Assert.Equal(2, reads);
    }

    [Fact]
    public void Present_file_retains_loaded_fields_without_rewriting_ownership()
    {
        var loaded = new List<string>();
        var loader = new Loader(path =>
        {
            loaded.Add(path);
            return Fields;
        });
        var observed = Reader(new(DiskEntryKind.File, false), loader).Read(Link);
        Assert.Equal(ReadObservation<ShortcutFields>.Known(Fields), observed);
        Assert.Equal(new[] { Link }, loaded);
    }

    [Theory]
    [InlineData(0x80070002u, false)]
    [InlineData(0x80070003u, false)]
    [InlineData(0x80070002u, true)]
    [InlineData(0x80070003u, true)]
    [InlineData(0x80070005u, false)]
    [InlineData(0x80070005u, true)]
    [InlineData(0x800300FBu, false)]
    public void Load_errors_retain_unknown_fault_and_initial_inventory_evidence(
        uint code, bool managed)
    {
        var loader = new Loader(_ => throw Failure(code, managed));
        var observed = Reader(new(DiskEntryKind.File, false), loader).Read(Link);
        Assert.Equal(ReadObservation<ShortcutFields>.Unknown(
            ObservationFault.Unreadable, code), observed);
        using var world = new RootFixture();
        world.Registry.Set(world.Registry.Current with { Shortcut = observed });
        var error = Assert.Throws<SetupIoException>(() => InspectionEvidence.Read(
            SetupEvidenceStage.InitialInventory, () => Proof(world).Read([])));
        Assert.Equal(SetupCode.InvalidEvidence, error.Code);
        Assert.Equal(new SetupRefusalEvidence(SetupEvidenceStage.InitialInventory,
            SetupEvidenceCause.ShortcutObservation, ObservationFault.Unreadable,
            StartupEvidenceErrorKind.HResult, code), error.Evidence);
    }

    [Theory]
    [InlineData(0x80070002u)]
    [InlineData(0x80070003u)]
    [InlineData(0x80070005u)]
    public void Entry_query_errors_are_not_absence(uint code)
    {
        var files = new Files(_ => throw Failure(code, managed: true));
        var reader = new ShellLinkReader(StablePaths(), files, UnexpectedLoader());
        Assert.Equal(ReadObservation<ShortcutFields>.Unknown(
            ObservationFault.Unreadable, code), reader.Read(Link));
    }

    [Fact]
    public void Programming_faults_are_not_converted_to_observations()
    {
        var loader = new Loader(_ => throw new InvalidOperationException());
        var reader = Reader(new(DiskEntryKind.File, false), loader);
        Assert.Throws<InvalidOperationException>(() => reader.Read(Link));
    }

    private static ShellLinkReader Reader(
        DiskEntry entry, IShellLinkLoader? loader = null, IPathIdentityQuery? paths = null) =>
        new(paths ?? StablePaths(), new Files(_ => entry), loader ?? UnexpectedLoader());

    private static IPathIdentityQuery StablePaths() =>
        new Paths(path => ReadObservation<PathIdentity>.Known(new(path)));

    private static IShellLinkLoader UnexpectedLoader() =>
        new Loader(_ => throw new InvalidOperationException());

    private static SetupStateProof Proof(RootFixture world) =>
        new(world.Disk, world.Scope, RootFixture.Programs, world.Registry, world.Paths);

    private static Exception Failure(uint code, bool managed) => (managed, code) switch
    {
        (true, 0x80070002u) => new FileNotFoundException(),
        (true, 0x80070003u) => new DirectoryNotFoundException(),
        (true, 0x80070005u) => new UnauthorizedAccessException(),
        _ => new COMException("Synthetic load failure.", unchecked((int)code))
    };

    private sealed record Paths(
        Func<string, ReadObservation<PathIdentity>> Observe) : IPathIdentityQuery
    {
        public ReadObservation<PathIdentity> Read(string path) => Observe(path);
    }

    private sealed record Loader(Func<string, ShortcutFields> Read) : IShellLinkLoader
    {
        public ShortcutFields Load(string path) => Read(path);
    }

    private sealed record Files(Func<string, DiskEntry> Read) : ISetupFileSystem
    {
        public DiskEntry Entry(string path) => Read(path);
        public IReadOnlyList<string> Children(string directory) =>
            throw new InvalidOperationException();
        public void CreateDirectory(string path) => throw new InvalidOperationException();
        public FileStream Open(string path, FileMode mode, FileAccess access, FileShare share) =>
            throw new InvalidOperationException();
        public void Flush(FileStream stream) => throw new InvalidOperationException();
        public void DeleteFile(string path) => throw new InvalidOperationException();
    }
}
