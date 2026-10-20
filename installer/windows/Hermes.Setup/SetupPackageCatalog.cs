using System.Collections.Immutable;
using System.Security;
using Hermes.Servicing;

namespace Hermes.Setup;

internal static class AggregateResult
{
    internal static T Require<T>(Effect<T> effect) => effect switch
    {
        Effect<T>.Success(var value) => value,
        _ => throw new SetupIoException(effect.FailureCode, effect.Evidence)
    };

    internal static Task<Effect<T>> ReadAsync<T>(
        Func<T> read, SetupCode failure, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Effect<T>.Ok(read()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Task.FromResult(Effect<T>.Fail(SetupCode.Cancelled)); }
        catch (SetupIoException error)
        { return Task.FromResult(Effect<T>.Fail(error.Code, error.Evidence)); }
        catch (Exception error) when (Expected(error))
        { return Task.FromResult(Effect<T>.Fail(failure)); }
    }

    internal static bool Expected(Exception error) =>
        SetupIo.Expected(error) || error is SecurityException;
}

internal sealed record RegistryExpectation(string Key, string Name, RegistryAtom Value);

internal sealed record SetupResources(
    ImmutableDictionary<string, string> Directories,
    ImmutableArray<RegistryExpectation> Registry,
    ImmutableDictionary<Guid, string> ComponentKeyPaths,
    string ShortcutPath,
    ShortcutFields Shortcut)
{
    internal const string InstallerKey = @"Software\Hermes\Installer";
    internal const string ComponentsKey = InstallerKey + @"\Components";
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    internal static SetupResources Create(
        PackageAuthoringSnapshot snapshot, ValidatedPackageProfile profile,
        UserInstallationScope scope, string programsDirectory)
    {
        var directories = ResolveDirectories(snapshot, scope, programsDirectory);
        var registry = profile.Registry.Select(value => new RegistryExpectation(
            value.Key, value.Name, ResolveValue(value.Value, profile, scope))).ToImmutableArray();
        var keys = profile.Components.ToImmutableDictionary(component => component.Code,
            component => RegistryKeyPath(profile.Registry.Single(value =>
                value.Id == component.RegistryKeyPath)));
        var shortcut = profile.Shortcut;
        return new(directories, registry, keys,
            Path.Combine(directories[shortcut.DirectoryId], shortcut.Name + ".lnk"),
            new(Format(shortcut.Target, profile, scope), shortcut.Arguments,
                directories[shortcut.WorkingDirectoryId]));
    }

    private static ImmutableDictionary<string, string> ResolveDirectories(
        PackageAuthoringSnapshot snapshot, UserInstallationScope scope, string programs)
    {
        var local = Path.GetDirectoryName(Path.GetDirectoryName(scope.AppDirectory))
            ?? throw new SetupIoException(SetupCode.RootMismatch);
        var known = ImmutableDictionary<string, string>.Empty
            .Add("LocalAppDataFolder", local).Add("ProgramMenuFolder", programs);
        var rows = snapshot.RequireTable("Directory").ToImmutableDictionary(
            row => row[0], row => (Parent: row[1], Name: row[2]), StringComparer.Ordinal);
        return rows.Keys.Where(id => id != "TARGETDIR").ToImmutableDictionary(
            id => id, id => ResolveDirectory(id, rows, known, []), StringComparer.Ordinal);
    }

    private static string ResolveDirectory(
        string id, ImmutableDictionary<string, (string Parent, string Name)> rows,
        ImmutableDictionary<string, string> known, ImmutableHashSet<string> visited)
    {
        if (known.GetValueOrDefault(id) is { } folder)
            return folder;
        SetupIo.Require(visited.Count < 64 && !visited.Contains(id) && rows.ContainsKey(id),
            SetupCode.MediaInvalid);
        var row = rows[id];
        var name = MsiTargetName.Read(row.Name);
        return Path.Combine(ResolveDirectory(row.Parent, rows, known, visited.Add(id)), name);
    }

    private static RegistryAtom ResolveValue(
        string value, ValidatedPackageProfile profile, UserInstallationScope scope) =>
        value == "#1"
            ? new(RegistryAtomKind.Integer, null, 1, [])
            : new(RegistryAtomKind.Text, Format(value, profile, scope), null, []);

    private static string Format(
        string value, ValidatedPackageProfile profile, UserInstallationScope scope)
    {
        var root = scope.AppDirectory.Replace('/', '\\').TrimEnd('\\') + "\\";
        var resolved = value.Replace("[INSTALLFOLDER]", root, StringComparison.Ordinal)
            .Replace("[ProductCode]", profile.Identity.ProductCode.ToString("B"),
                StringComparison.Ordinal);
        SetupIo.Require(!resolved.Contains('[') && !resolved.Contains(']'),
            SetupCode.MediaInvalid);
        return resolved;
    }

    // MSI's HKCU registry-keypath spelling, not a filesystem identity.
    private static string RegistryKeyPath(ProfileRegistryValue value) =>
        @"01:\" + value.Key + "\\" + value.Name;
}

internal sealed record SetupPackage(
    PackageMedia Media, ValidatedPackageProfile Profile, SetupResources Resources)
{
    internal PackageEvidence Evidence => Media.Evidence;
}

internal sealed record SetupCachedPackage(
    ValidatedPackageProfile Profile, string ManifestSha256);

internal sealed class SetupPackageCatalog(
    SetupDisk disk,
    UserInstallationScope scope,
    string programsDirectory,
    IPackageMediaReader mediaReader,
    IPackageAuthoringDatabase authoring,
    ApprovedGuardProfile approved) : IDisposable
{
    private readonly List<FileStream> _pins = [];

    internal SetupPackage Source(string path, PackageAuthoringMode mode)
    {
        RequireOutsideRoot(path);
        Pin(path);
        var media = mediaReader.Read(path);
        var snapshot = authoring.ReadAuthoring(path);
        var profile = Validate(snapshot, mode);
        SetupIo.Require(media.Evidence.Identity == profile.Identity &&
            media.ManifestBytes.SequenceEqual(snapshot.Package.ManifestBytes) &&
            media.Evidence.ManifestSha256 == PackageMediaReader.Hash(
                snapshot.Package.ManifestBytes.ToArray()), SetupCode.MediaIdentityMismatch);
        return new(media, profile,
            SetupResources.Create(snapshot, profile, scope, programsDirectory));
    }

    internal SetupCachedPackage Cached(string path)
    {
        RequireOutsideRoot(path);
        // Do not retain Windows Installer's LocalPackage handle across MSI:
        // standard upgrade/uninstall must remain able to remove that cache.
        using var stable = disk.Read(path);
        var snapshot = authoring.ReadAuthoring(path);
        return new(Validate(snapshot, PackageAuthoringMode.RegisteredCache),
            PackageMediaReader.Hash(snapshot.Package.ManifestBytes.ToArray()));
    }

    internal SetupPackage Retained(PackageEvidence expected)
    {
        var source = Source(MediaCachePaths.Package(scope, expected.PackageSha256),
            PackageAuthoringMode.RetainedSource);
        SetupIo.Require(SetupIdentity.SameContent(expected, source.Evidence),
            SetupCode.MediaIdentityMismatch);
        var manifest = Pin(MediaCachePaths.Manifest(scope, expected.PackageSha256));
        SetupIo.Require(manifest.Length is > 0 and <= 16 * 1024 * 1024 &&
            PackageMediaReader.Hash(manifest) == expected.ManifestSha256,
            SetupCode.MediaIdentityMismatch);
        return source;
    }

    internal SetupPackage RegisteredSource(
        SetupCachedPackage cached, OperationJournal? journal)
    {
        var recorded = (journal?.Plan.Packages ?? [])
            .Where(package => package.Identity == cached.Profile.Identity &&
                package.ManifestSha256 == cached.ManifestSha256)
            .DistinctBy(package => package.PackageSha256).ToArray();
        var sources = recorded.Length > 0
            ? recorded.Select(Retained).ToArray()
            : Discover(cached).ToArray();
        SetupIo.Require(sources.Length == 1, SetupCode.RecoveryMediaUnavailable);
        RequireCachedMatches(cached, sources[0]);
        return sources[0];
    }

    internal static void RequireCachedMatches(
        SetupCachedPackage cached, SetupPackage source) =>
        SetupIo.Require(cached.Profile.Identity == source.Evidence.Identity &&
            cached.ManifestSha256 == source.Evidence.ManifestSha256 &&
            cached.Profile.Guard.Sha256 == source.Profile.Guard.Sha256,
            SetupCode.MediaIdentityMismatch);

    private IEnumerable<SetupPackage> Discover(SetupCachedPackage cached)
    {
        disk.Paths.RequireDirectory(scope.PackagesDirectory);
        if (disk.Files.Entry(scope.PackagesDirectory).Kind == DiskEntryKind.Missing)
            yield break;
        foreach (var directory in disk.Files.Children(scope.PackagesDirectory))
        {
            var hash = Path.GetFileName(directory);
            if (CanonicalHash(hash) && MatchingManifest(directory, cached.ManifestSha256))
            {
                var source = Source(MediaCachePaths.Package(scope, hash),
                    PackageAuthoringMode.RetainedSource);
                SetupIo.Require(source.Evidence.PackageSha256 == hash,
                    SetupCode.MediaIdentityMismatch);
                yield return Retained(source.Evidence);
            }
        }
    }

    private bool MatchingManifest(string directory, string expectedHash)
    {
        disk.Paths.RequireDirectory(directory);
        var entry = disk.Files.Entry(directory);
        SetupIo.Require(entry.Kind == DiskEntryKind.Directory && !entry.ReparsePoint,
            SetupCode.OwnershipRefused);
        var path = Path.Combine(directory, "manifest.json");
        if (disk.Files.Entry(path).Kind == DiskEntryKind.Missing)
            return false;
        return PackageMediaReader.Hash(disk.ReadBytes(path, 16 * 1024 * 1024)) == expectedHash;
    }

    private ValidatedPackageProfile Validate(
        PackageAuthoringSnapshot snapshot, PackageAuthoringMode mode) =>
        AggregateResult.Require(PackageAuthoringValidator.Validate(snapshot, mode, approved));

    private void RequireOutsideRoot(string path)
    {
        disk.Paths.RequireFile(path);
        SetupIo.Require(!WindowsEvidencePath.Intersects(path, scope.AppDirectory),
            SetupCode.OwnershipRefused);
    }

    private FileStream Pin(string path)
    {
        var stream = disk.Read(path);
        _pins.Add(stream);
        return stream;
    }

    internal static bool CanonicalHash(string hash) =>
        hash.Length == 64 &&
        hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public void Dispose()
    {
        foreach (var pin in _pins)
            pin.Dispose();
        _pins.Clear();
    }
}
