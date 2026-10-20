using System.Collections.Immutable;

namespace Hermes.Setup;

internal sealed class FileMediaRetention(
    SetupDisk disk,
    UserInstallationScope scope,
    IPackageMediaReader packages) : IMediaRetention, IDisposable
{
    private readonly List<FileStream> _pins = [];

    public Task<Effect<ImmutableArray<RetainedMedia>>> RetainAsync(
        IReadOnlyList<PackageEvidence> required, CancellationToken cancellationToken) =>
        SetupIo.RunAsync(() => required.Select(Retain).ToImmutableArray(),
            SetupCode.MediaInvalid, cancellationToken);

    private RetainedMedia Retain(PackageEvidence expected)
    {
        var source = packages.Read(expected.SourcePath);
        SetupIo.Require(SetupIdentity.SameContent(expected, source.Evidence),
            SetupCode.MediaIdentityMismatch);
        var directory = MediaCachePaths.Directory(scope, expected.PackageSha256);
        var packagePath = MediaCachePaths.Package(scope, expected.PackageSha256);
        var manifestPath = MediaCachePaths.Manifest(scope, expected.PackageSha256);
        disk.EnsureDirectory(directory);
        EnsurePackage(expected, packagePath);
        EnsureManifest(source, manifestPath);
        var retained = packages.Read(packagePath);
        SetupIo.Require(SetupIdentity.SameContent(expected, retained.Evidence),
            SetupCode.MediaIdentityMismatch);
        Pin(packagePath, expected.PackageSha256);
        Pin(manifestPath, expected.ManifestSha256);
        return new(expected, packagePath, manifestPath);
    }

    private void EnsurePackage(PackageEvidence expected, string path)
    {
        if (disk.Files.Entry(path).Kind != DiskEntryKind.Missing)
            return;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            disk.CopyNew(expected.SourcePath, temporary);
            using (var copy = disk.Read(temporary))
                SetupIo.Require(PackageMediaReader.Hash(copy) == expected.PackageSha256,
                    SetupCode.MediaIdentityMismatch);
            disk.CommitNew(temporary, path);
        }
        finally { disk.DeleteTemporary(temporary); }
    }

    private void EnsureManifest(PackageMedia source, string path)
    {
        if (disk.Files.Entry(path).Kind != DiskEntryKind.Missing)
            return;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            disk.WriteNew(temporary, source.ManifestBytes.AsSpan());
            disk.CommitNew(temporary, path);
        }
        finally { disk.DeleteTemporary(temporary); }
    }

    private void Pin(string path, string expectedHash)
    {
        var pin = disk.Read(path);
        try
        {
            SetupIo.Require(PackageMediaReader.Hash(pin) == expectedHash,
                SetupCode.MediaIdentityMismatch);
            pin.Position = 0;
            _pins.Add(pin);
        }
        catch { pin.Dispose(); throw; }
    }

    public void Dispose()
    {
        foreach (var pin in _pins)
            pin.Dispose();
        _pins.Clear();
    }
}
