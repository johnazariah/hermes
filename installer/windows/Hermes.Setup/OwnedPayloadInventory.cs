using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Hermes.Setup;

internal sealed record OwnedFileState(
    string RelativePath,
    DiskEntryKind Kind,
    long? Length,
    string? Sha256,
    Guid? ModuleVersionId);

internal sealed class OwnedPayloadInventory(
    SetupDisk disk, UserInstallationScope scope)
{
    internal ImmutableArray<OwnedFileState> Read(PayloadManifest manifest) =>
        manifest.Files.Select(ReadFile).ToImmutableArray();

    private OwnedFileState ReadFile(PayloadFile file)
    {
        var path = OwnedPath(file.Path);
        var entry = disk.Files.Entry(path);
        SetupIo.Require(!entry.ReparsePoint, SetupCode.OwnershipRefused);
        if (entry.Kind != DiskEntryKind.File)
            return new(file.Path, entry.Kind, null, null, null);
        using var stream = disk.Read(path);
        var length = stream.Length;
        var hash = PackageMediaReader.Hash(stream);
        stream.Position = 0;
        var mvid = file.ModuleVersionId is not null ? ReadMvid(stream) : null;
        return new(file.Path, DiskEntryKind.File, length, hash, mvid);
    }

    internal static bool Matches(
        PayloadManifest manifest, ImmutableArray<OwnedFileState> inventory) =>
        !inventory.IsDefault && inventory.Length == manifest.Files.Length &&
        manifest.Files.All(file => inventory.Count(actual =>
            actual.RelativePath == file.Path && actual.Kind == DiskEntryKind.File &&
            actual.Length == file.Length &&
            string.Equals(actual.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase) &&
            actual.ModuleVersionId == file.ModuleVersionId) == 1);

    internal void RefuseCollisions(
        PayloadManifest candidate, IReadOnlySet<string> presentlyOwned)
    {
        foreach (var file in candidate.Files)
        {
            var path = OwnedPath(file.Path);
            RequireDirectoryAncestors(path);
            var entry = disk.Files.Entry(path);
            SetupIo.Require(!entry.ReparsePoint &&
                entry.Kind != DiskEntryKind.Directory &&
                (entry.Kind == DiskEntryKind.Missing || presentlyOwned.Contains(file.Path)),
                SetupCode.Collision);
            if (entry.Kind == DiskEntryKind.File)
                disk.Paths.RequireFile(path);
        }
    }

    internal bool RemovedFilesAbsent(
        IEnumerable<PayloadManifest> previous, IReadOnlySet<string> desiredPaths) =>
        previous.SelectMany(manifest => manifest.Files)
            .Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !desiredPaths.Contains(path))
            .All(RemovedFileAbsent);

    private bool RemovedFileAbsent(string relative)
    {
        var path = OwnedPath(relative);
        RequireDirectoryAncestors(path);
        var entry = disk.Files.Entry(path);
        SetupIo.Require(!entry.ReparsePoint, SetupCode.OwnershipRefused);
        return entry.Kind == DiskEntryKind.Missing;
    }

    private string OwnedPath(string relative)
    {
        SetupIo.Require(PackageMediaReader.ValidRelative(relative), SetupCode.MediaInvalid);
        return Path.Combine(scope.AppDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private void RequireDirectoryAncestors(string path)
    {
        var parent = Path.GetDirectoryName(path)
            ?? throw new SetupIoException(SetupCode.Collision);
        disk.Paths.RequireDirectory(parent);
        while (!SetupIdentity.SamePath(parent, scope.AppDirectory))
        {
            var entry = disk.Files.Entry(parent);
            SetupIo.Require(!entry.ReparsePoint && entry.Kind != DiskEntryKind.File,
                SetupCode.Collision);
            parent = Path.GetDirectoryName(parent)
                ?? throw new SetupIoException(SetupCode.Collision);
        }
        var root = disk.Files.Entry(parent);
        SetupIo.Require(!root.ReparsePoint && root.Kind != DiskEntryKind.File,
            SetupCode.Collision);
    }

    private static Guid? ReadMvid(Stream stream)
    {
        using var image = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (!image.HasMetadata)
            return null;
        var metadata = image.GetMetadataReader();
        return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
    }
}
