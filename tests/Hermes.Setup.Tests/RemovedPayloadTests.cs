using System.Collections.Immutable;
using Hermes.Servicing;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed class RemovedPayloadTests
{
    private const string Removed = "service/old/owned.dat";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OrdinaryMissingDirectoriesPermitVerifiedAbsenceAfterPhysicalProof(int depth)
    {
        using var fixture = new IoFixture();
        var entries = new[] { ".", "service", "service/old" }.Take(depth)
            .ToDictionary(path => path, _ => new DiskEntry(DiskEntryKind.Directory, false));
        var probe = new AbsenceProbe(fixture.Scope, entries);
        Assert.True(probe.Inventory.RemovedFilesAbsent(
            [Manifest(Removed)], ImmutableHashSet<string>.Empty));
        Assert.Equal(new[]
        {
            "proof:service/old", "entry:service/old", "entry:service",
            "entry:.", "entry:" + Removed
        }, probe.Calls);
    }

    [Theory]
    [InlineData("service/old", 2, true)]
    [InlineData("service", 2, true)]
    [InlineData(".", 2, true)]
    [InlineData("service/old", 0, true)]
    [InlineData("service/old", 1, false)]
    [InlineData("service", 1, false)]
    [InlineData(".", 1, false)]
    public void RedirectedOrNonDirectoryAncestorsRefuseBeforeMissingChildLookup(
        string ancestor, int kind, bool reparse)
    {
        using var fixture = new IoFixture();
        var entries = new Dictionary<string, DiskEntry>
        {
            [ancestor] = new((DiskEntryKind)kind, reparse)
        };
        var probe = new AbsenceProbe(fixture.Scope, entries);
        var error = Assert.Throws<SetupIoException>(() => probe.Inventory.RemovedFilesAbsent(
            [Manifest(Removed)], ImmutableHashSet<string>.Empty));
        Assert.Equal(SetupCode.Collision, error.Code);
        Assert.Equal("proof:service/old", probe.Calls[0]);
        Assert.DoesNotContain("entry:" + Removed, probe.Calls);
    }

    [Fact]
    public void UnresolvedPhysicalParentRefusesBeforeAttributeLookup()
    {
        using var fixture = new IoFixture();
        var probe = new AbsenceProbe(fixture.Scope,
            ImmutableDictionary<string, DiskEntry>.Empty, unresolved: true);
        var error = Assert.Throws<SetupIoException>(() => probe.Inventory.RemovedFilesAbsent(
            [Manifest(Removed)], ImmutableHashSet<string>.Empty));
        Assert.Equal(SetupCode.OwnershipRefused, error.Code);
        Assert.Equal(new[] { "proof:service/old" }, probe.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ReparseLeafCannotEstablishAbsence(int kind)
    {
        using var fixture = new IoFixture();
        var entries = new Dictionary<string, DiskEntry>
        {
            [Removed] = new((DiskEntryKind)kind, true)
        };
        var probe = new AbsenceProbe(fixture.Scope, entries);
        var error = Assert.Throws<SetupIoException>(() => probe.Inventory.RemovedFilesAbsent(
            [Manifest(Removed)], ImmutableHashSet<string>.Empty));
        Assert.Equal(SetupCode.OwnershipRefused, error.Code);
        Assert.Equal("entry:" + Removed, probe.Calls[^1]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void OrdinaryResidualEntryDoesNotEstablishAbsence(int kind)
    {
        using var fixture = new IoFixture();
        var entries = new Dictionary<string, DiskEntry>
        {
            [Removed] = new((DiskEntryKind)kind, false)
        };
        var probe = new AbsenceProbe(fixture.Scope, entries);
        Assert.False(probe.Inventory.RemovedFilesAbsent(
            [Manifest(Removed)], ImmutableHashSet<string>.Empty));
        Assert.Equal("entry:" + Removed, probe.Calls[^1]);
    }

    [Fact]
    public void AbsenceVerificationPreservesDesiredOwnedAndUnknownFiles()
    {
        using var fixture = new IoFixture();
        var directory = Path.Combine(fixture.Scope.AppDirectory, "service");
        Directory.CreateDirectory(directory);
        var owned = Path.Combine(directory, "current.dat");
        var unknown = Path.Combine(directory, "unknown.dat");
        File.WriteAllBytes(owned, [1, 2, 3]);
        File.WriteAllBytes(unknown, [7, 8, 9]);
        var inventory = new OwnedPayloadInventory(fixture.Disk, fixture.Scope);
        Assert.True(inventory.RemovedFilesAbsent(
            [Manifest(Removed, "service/current.dat")],
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "service/current.dat")));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(owned));
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(unknown));
    }

    private static PayloadManifest Manifest(params string[] paths) =>
        new(1, HermesServicingProtocol.AdmissionAbi,
            PackageMediaReader.RootToken, PackageMediaReader.ScopeToken,
            HermesServicingProtocol.UpgradeCode, Guid.NewGuid(), Guid.NewGuid(),
            new Version(1, 0, 0), paths.Select((path, index) => new PayloadFile(
                path, new string('a', 64), 1, null,
                "File" + index, "Component" + index, Guid.NewGuid())).ToImmutableArray());

    private sealed class AbsenceProbe(
        UserInstallationScope scope,
        IReadOnlyDictionary<string, DiskEntry> entries,
        bool unresolved = false) : ISetupFileSystem, ISetupPathSafety, IAtomicFileCommit
    {
        internal List<string> Calls { get; } = [];
        internal OwnedPayloadInventory Inventory => new(new(this, this, this), scope);

        public DiskEntry Entry(string path)
        {
            var relative = Relative(path);
            Calls.Add("entry:" + relative);
            return entries.TryGetValue(relative, out var entry)
                ? entry : new(DiskEntryKind.Missing, false);
        }

        public void RequireDirectory(string path)
        {
            Calls.Add("proof:" + Relative(path));
            SetupIo.Require(!unresolved, SetupCode.OwnershipRefused);
        }

        private string Relative(string path) =>
            Path.GetRelativePath(scope.AppDirectory, path)
                .Replace(Path.DirectorySeparatorChar, '/');

        public void RequireFile(string path) => throw new InvalidOperationException();
        public IReadOnlyList<string> Children(string path) => throw new InvalidOperationException();
        public void CreateDirectory(string path) => throw new InvalidOperationException();
        public void DeleteFile(string path) => throw new InvalidOperationException();
        public void Flush(FileStream stream) => throw new InvalidOperationException();
        public FileStream Open(string path, FileMode mode, FileAccess access, FileShare share) =>
            throw new InvalidOperationException();
        public void Commit(string temporary, string destination, bool overwrite) =>
            throw new InvalidOperationException();
    }
}
