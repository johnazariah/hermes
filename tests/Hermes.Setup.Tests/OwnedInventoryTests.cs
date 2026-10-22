using System.Collections.Immutable;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed class OwnedInventoryTests
{
    [Fact]
    public void UnknownCollisionIsRefusedEvenWhenBytesWouldMatch()
    {
        using var fixture = new IoFixture();
        var manifest = Manifest("service/owned.dat", [1, 2, 3]);
        Write(fixture, "service/owned.dat", [1, 2, 3]);
        var inventory = new OwnedPayloadInventory(fixture.Disk, fixture.Scope);
        var error = Assert.Throws<SetupIoException>(() => inventory.RefuseCollisions(
            manifest, ImmutableHashSet<string>.Empty));
        Assert.Equal(SetupCode.Collision, error.Code);
        Assert.Equal(new byte[] { 1, 2, 3 },
            File.ReadAllBytes(Path.Combine(fixture.Scope.AppDirectory, "service", "owned.dat")));
    }

    [Fact]
    public void RegisteredOwnedPathMayBeReplacedByStandardMsi()
    {
        using var fixture = new IoFixture();
        var manifest = Manifest("service/owned.dat", [1, 2, 3]);
        Write(fixture, "service/owned.dat", [9, 9, 9]);
        var inventory = new OwnedPayloadInventory(fixture.Disk, fixture.Scope);
        inventory.RefuseCollisions(manifest,
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "service/owned.dat"));
        Assert.Equal(new byte[] { 9, 9, 9 },
            File.ReadAllBytes(Path.Combine(fixture.Scope.AppDirectory, "service", "owned.dat")));
    }

    [Fact]
    public void CompleteOwnedInventoryDetectsChangedBytes()
    {
        using var fixture = new IoFixture();
        var manifest = Manifest("service/owned.dat", [1, 2, 3]);
        Write(fixture, "service/owned.dat", [9, 9, 9]);
        var inventory = new OwnedPayloadInventory(fixture.Disk, fixture.Scope);
        Assert.False(OwnedPayloadInventory.Matches(manifest, inventory.Read(manifest)));
    }

    [Fact]
    public void CompleteOwnedInventoryAcceptsExactOwnedBytes()
    {
        using var fixture = new IoFixture();
        var manifest = Manifest("service/owned.dat", [1, 2, 3]);
        Write(fixture, "service/owned.dat", [1, 2, 3]);
        var inventory = new OwnedPayloadInventory(fixture.Disk, fixture.Scope);
        Assert.True(OwnedPayloadInventory.Matches(manifest, inventory.Read(manifest)));
    }

    [Fact]
    public void UnknownFilesAreNeitherReadIntoOwnedInventoryNorRemoved()
    {
        using var fixture = new IoFixture();
        var manifest = Manifest("service/owned.dat", [1, 2, 3]);
        Write(fixture, "service/owned.dat", [1, 2, 3]);
        Write(fixture, "service/unknown.dat", [7, 8, 9]);
        var inventory = new OwnedPayloadInventory(fixture.Disk, fixture.Scope);
        var actual = inventory.Read(manifest);
        Assert.Single(actual);
        Assert.Equal("service/owned.dat", actual[0].RelativePath);
        Assert.Equal(new byte[] { 7, 8, 9 },
            File.ReadAllBytes(Path.Combine(fixture.Scope.AppDirectory, "service", "unknown.dat")));
    }

    [Fact]
    public void FileWhereCandidateNeedsDirectoryIsRefused()
    {
        using var fixture = new IoFixture();
        Directory.CreateDirectory(fixture.Scope.AppDirectory);
        File.WriteAllBytes(Path.Combine(fixture.Scope.AppDirectory, "service"), [1]);
        var inventory = new OwnedPayloadInventory(fixture.Disk, fixture.Scope);
        var error = Assert.Throws<SetupIoException>(() => inventory.RefuseCollisions(
            Manifest("service/sub/owned.dat", [2]), ImmutableHashSet<string>.Empty));
        Assert.Equal(SetupCode.Collision, error.Code);
    }

    [Fact]
    public void OldOwnedResidualBlocksVerifiedAbsence()
    {
        using var fixture = new IoFixture();
        var manifest = Manifest("service/owned.dat", [1]);
        Write(fixture, "service/owned.dat", [1]);
        var inventory = new OwnedPayloadInventory(fixture.Disk, fixture.Scope);
        Assert.False(inventory.RemovedFilesAbsent([manifest], ImmutableHashSet<string>.Empty));
        Assert.True(File.Exists(Path.Combine(
            fixture.Scope.AppDirectory, "service", "owned.dat")));
    }

    private static PayloadManifest Manifest(string path, byte[] bytes) =>
        new(1, 1, PackageMediaReader.RootToken, PackageMediaReader.ScopeToken,
            Hermes.Servicing.HermesServicingProtocol.UpgradeCode,
            Guid.NewGuid(), Guid.NewGuid(), new Version(1, 0, 0),
            [new(path, PackageMediaReader.Hash(bytes), bytes.Length,
                null, "File", "Component", Guid.NewGuid())]);

    private static void Write(IoFixture fixture, string relative, byte[] bytes)
    {
        var path = Path.Combine(fixture.Scope.AppDirectory,
            relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException());
        File.WriteAllBytes(path, bytes);
    }
}
