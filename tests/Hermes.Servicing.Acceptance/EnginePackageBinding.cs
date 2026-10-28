namespace Hermes.Servicing.Acceptance;

internal sealed record EnginePackageBinding(
    string EnginePath,
    string EngineSha256,
    PackageKey Source);

internal sealed class EnginePackageBinder : IDisposable
{
    private readonly SetupPackageCatalog _catalog;
    private readonly WindowsPackageDatabase _database = new();
    private readonly ImmutableArray<SetupPackage> _sources;

    internal ImmutableArray<PackageKey> Sources { get; }

    internal EnginePackageBinder(AcceptanceContext context)
    {
        var scope = WindowsSetupScope.ForCurrentUser();
        var disk = new SetupDisk(new PhysicalSetupFileSystem(),
            new WindowsSetupPathSafety(), new WindowsAtomicFileCommit());
        var reader = new PackageMediaReader(disk, scope, _database);
        _catalog = new(disk, scope,
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            reader, _database, context.ApprovedGuard());
        _sources = new[] { context.Options.OldMsi, context.Options.NewMsi }
            .Select(path => _catalog.Source(path, PackageAuthoringMode.CandidateSource))
            .ToImmutableArray();
        Sources = _sources.Select(Key).ToImmutableArray();
    }

    private PackageKey Key(SetupPackage package)
    {
        var image = _database.Read(package.Evidence.SourcePath);
        string Property(string name) => image.Table("Property")
            .Where(row => row[0] == name).Select(row => row[1]).Single();
        var evidence = package.Evidence;
        return new(evidence.Identity.ProductCode, evidence.Identity.PackageCode,
            evidence.PackageSha256, evidence.ManifestSha256,
            Property("ProductName"), Property("ProductVersion"),
            Property("ProductLanguage"), Property("Manufacturer"));
    }

    internal EnginePackageBinding Bind(string path)
    {
        var absolute = Path.GetFullPath(path);
        new WindowsSetupPathSafety().RequireFile(absolute);
        var cached = _catalog.Cached(absolute);
        var source = _sources.Single(package =>
            package.Evidence.Identity == cached.Profile.Identity &&
            package.Evidence.ManifestSha256 == cached.ManifestSha256);
        SetupPackageCatalog.RequireCachedMatches(cached, source);
        return new(absolute, Files.Hash(absolute),
            Sources.Single(key => key.ProductCode == source.Evidence.Identity.ProductCode));
    }

    internal PackageKey Product(Guid product) =>
        Sources.Single(value => value.ProductCode == product);

    internal PackageKey ForCase(AcceptanceContext context, CrashCase item)
    {
        var path = item.Operation == "new" ? context.Options.NewMsi : context.Options.OldMsi;
        return Sources.Single(value => value.WholeMsiSha256 == Files.Hash(path));
    }

    public void Dispose() => _catalog.Dispose();
}
