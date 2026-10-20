using System.Collections.Immutable;
using System.Globalization;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed class MsiTargetNameIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SameTargetsSurviveSourceRetentionAndRegisteredCache(
        bool fileAliases, bool directoryAliases)
    {
        var bare = Bare();
        using var world = new CatalogFixture(Aliases(bare, fileAliases, directoryAliases));
        var candidate = world.Catalog.Source(
            world.SourcePath, PackageAuthoringMode.CandidateSource);
        AssertPackage(world, bare, candidate, PackageAuthoringMode.CandidateSource);
        await AssertRetentionAsync(world, bare, candidate, CancellationToken.None);
        AssertRegisteredCache(world, bare);
    }

    [Theory]
    [InlineData("HERMES~1.DLL")]
    [InlineData("FILE")]
    [InlineData("A_$~1.DLL")]
    [InlineData("test~1.dll")]
    public void LegitimateShortNamesDoNotChangeTheLongTarget(string shortName)
    {
        var bare = Bare();
        var target = bare.Snapshot.RequireTable("File")[0][2];
        using var world = new CatalogFixture(
            bare.Cell("File", 0, 2, shortName + "|" + target));
        AssertPackage(world, bare, world.Catalog.Source(
            world.SourcePath, PackageAuthoringMode.CandidateSource),
            PackageAuthoringMode.CandidateSource);
    }

    [Theory]
    [InlineData("")]
    [InlineData("|{0}")]
    [InlineData("{0}|")]
    [InlineData("SHORT||{0}")]
    [InlineData("SHORT|{0}|EXTRA")]
    [InlineData("TOOLONG99|{0}")]
    [InlineData("BAD.LONG|{0}")]
    [InlineData("A.B.C|{0}")]
    [InlineData("BAD.|{0}")]
    [InlineData(".|{0}")]
    [InlineData("..|{0}")]
    [InlineData("../BAD|{0}")]
    [InlineData("/BAD|{0}")]
    [InlineData(@"C:\BAD|{0}")]
    [InlineData(@"BAD\PATH|{0}")]
    [InlineData("BAD/PATH|{0}")]
    [InlineData("BAD:ADS|{0}")]
    [InlineData("BAD NAME|{0}")]
    [InlineData("BAD+ONE|{0}")]
    [InlineData("BAD,ONE|{0}")]
    [InlineData("BAD;ONE|{0}")]
    [InlineData("BAD=ONE|{0}")]
    [InlineData("[BAD]|{0}")]
    [InlineData("CON|{0}")]
    [InlineData("COM1|{0}")]
    [InlineData("LPT³|{0}")]
    [InlineData("\0|{0}")]
    [InlineData("{0}:source")]
    [InlineData("SHORT|{0}:SOURCE")]
    [InlineData("SHORT|../{0}")]
    [InlineData("SHORT|/{0}")]
    [InlineData(@"SHORT|C:\{0}")]
    [InlineData(@"SHORT|sub\{0}")]
    [InlineData("SHORT|sub/{0}")]
    [InlineData("SHORT|{0}.")]
    [InlineData("SHORT|{0} ")]
    [InlineData("SHORT|[INSTALLFOLDER]{0}")]
    [InlineData("SHORT|{0}\0")]
    [InlineData("SHORT|NUL")]
    [InlineData("aux.dat")]
    [InlineData("AUX.DAT|{0}")]
    [InlineData("SHORT|aux.dat")]
    public void MalformedAliasesFailAcrossValidationMediaCatalogAndDirectoryResolution(
        string pattern) => AssertInvalidPattern(pattern);

    [Theory]
    [InlineData("aux.dat", "aux.dat")]
    [InlineData("aux.dat", "FILE~1.DAT|aux.dat")]
    [InlineData("auxiliary.dat", "AUX.DAT|auxiliary.dat")]
    public void ReservedDeviceNamesFailEvenWhenTheLongTargetMatchesTheManifest(
        string basename, string encoded)
    {
        var manifest = AggregateResult.Require(Bare().Validate()).Manifest;
        var file = AuthoringFixture.File("service/" + basename);
        var fixture = AuthoringFixture.Create(manifest with
        {
            Files = manifest.Files.Add(file)
        });
        fixture = fixture.Rows("File", fixture.Snapshot.RequireTable("File")
            .Select(row => row[0] == file.FileId ? row.SetItem(2, encoded) : row)
            .ToImmutableArray());
        using var world = new CatalogFixture(fixture);
        AssertRejected(world, fixture, SetupCode.MediaInvalid);
        var error = Assert.Throws<SetupIoException>(() => world.Media.Read(world.SourcePath));
        Assert.Equal(SetupCode.MediaInvalid, error.Code);
    }

    [Fact]
    public void OverlongSegmentsAreNotAcceptedAsNamesOrAliases()
    {
        AssertInvalidPattern(new string('A', 256) + "|{0}");
        AssertInvalidPattern("{0}" + new string('A', 256));
    }

    [Theory]
    [InlineData("File", 0, "HERMES~1.DLL|Hermes.Other.dll")]
    [InlineData("File", 0, "HERMES~1.DLL|hermes.Service.dll")]
    [InlineData("Directory", 3, "HERMES|Other")]
    [InlineData("Directory", 4, "APP|Other")]
    [InlineData("Directory", 5, "HERMES|Other")]
    [InlineData("Directory", 6, "service|other")]
    [InlineData("Directory", 8, "LONGDI~1|long-directory-other")]
    [InlineData("Directory", 9, "DEEPER~1|deeper-directory-other")]
    public void LongTargetsMustRetainExactManifestAndRootIdentity(
        string table, int index, string name)
    {
        var changed = Aliases(Bare(), true, true).Cell(table, index, 2, name);
        using var world = new CatalogFixture(changed);
        var expected = table == "Directory" && index is 3 or 4
            ? SetupCode.RootMismatch : SetupCode.MediaInvalid;
        AssertRejected(world, changed, expected);
    }

    [Theory]
    [InlineData("File", 0, 1, "AutoStart")]
    [InlineData("File", 0, 3, "33")]
    [InlineData("Component", 0, 2, "INSTALLFOLDER")]
    [InlineData("Component", 0, 3, "256")]
    [InlineData("Component", 0, 5, "ForeignRegistryKeyPath")]
    [InlineData("Registry", 0, 2, @"Software\Foreign")]
    [InlineData("Registry", 0, 4, "1")]
    [InlineData("CustomAction", 0, 3, "WrongExport")]
    [InlineData("Directory", 6, 1, "HermesMenuFolder")]
    [InlineData("Directory", 3, 1, "ProgramMenuFolder")]
    public void AcceptedAliasesDoNotBypassOtherOwnershipOrAncestryChecks(
        string table, int index, int column, string value)
    {
        var changed = Aliases(Bare(), true, true).Cell(table, index, column, value);
        using var world = new CatalogFixture(changed);
        AssertRejected(world, changed, table == "Directory" && index == 3
            ? SetupCode.RootMismatch : SetupCode.MediaInvalid);
    }

    [Theory]
    [InlineData("TARGETDIR", "ROOT|SourceDir")]
    [InlineData("LocalAppDataFolder", "LOCAL~1|LocalApp")]
    [InlineData("LocalAppDataFolder", "LocalApp:source")]
    [InlineData("ProgramMenuFolder", "MENU~1|Programs")]
    [InlineData("ProgramMenuFolder", "SHORT||Programs")]
    public void KnownFolderDefaultsKeepTheirExistingExactAllowlist(string id, string name)
    {
        var fixture = Aliases(Bare(), true, true);
        var index = fixture.Snapshot.RequireTable("Directory")
            .Select((row, position) => (row, position))
            .Single(pair => pair.row[0] == id).position;
        var changed = fixture.Cell("Directory", index, 2, name);
        using var world = new CatalogFixture(changed);
        AssertRejected(world, changed, SetupCode.MediaInvalid);
    }

    private static AuthoringFixture Bare()
    {
        var manifest = AggregateResult.Require(AuthoringFixture.Create().Validate()).Manifest;
        return AuthoringFixture.Create(manifest with
        {
            Files = manifest.Files.Add(AuthoringFixture.File(
                "service/long-directory/deeper-directory/ordinary-file.json"))
        });
    }

    private static bool KnownDirectory(string id) =>
        id is "TARGETDIR" or "LocalAppDataFolder" or "ProgramMenuFolder";

    private static AuthoringFixture Aliases(
        AuthoringFixture fixture, bool files, bool directories)
    {
        if (files)
            fixture = fixture.Rows("File", fixture.Snapshot.RequireTable("File")
                .Select((row, index) => row.SetItem(2,
                    $"HERMES~{index + 1}.{row[2].Split('.')[^1][..3].ToUpperInvariant()}|{row[2]}"))
                .ToImmutableArray());
        if (directories)
            fixture = fixture.Rows("Directory", fixture.Snapshot.RequireTable("Directory")
                .Select((row, index) => KnownDirectory(row[0])
                    ? row : row.SetItem(2, $"DIR{index}~1|{row[2]}"))
                .ToImmutableArray());
        return fixture;
    }

    private static SetupResources Expected(CatalogFixture world, AuthoringFixture bare) =>
        SetupResources.Create(bare.Snapshot, AggregateResult.Require(bare.Validate()),
            world.Scope, RootFixture.Programs);

    private static void AssertPackage(
        CatalogFixture world, AuthoringFixture bare, SetupPackage actual,
        PackageAuthoringMode mode)
    {
        var expected = AggregateResult.Require(bare.Validate());
        Assert.Equal(mode, actual.Profile.Mode);
        Assert.Equal(expected.Identity, actual.Evidence.Identity);
        Assert.Equal(expected.Manifest.Files.ToArray(), actual.Profile.Manifest.Files.ToArray());
        Assert.Equal(expected.DirectoryRemovals.ToArray(),
            actual.Profile.DirectoryRemovals.ToArray());
        AssertResources(Expected(world, bare), actual.Resources);
        AssertPayloadPaths(world, actual);
    }

    private static void AssertResources(SetupResources expected, SetupResources actual)
    {
        Assert.Equal(expected.Directories.OrderBy(pair => pair.Key).ToArray(),
            actual.Directories.OrderBy(pair => pair.Key).ToArray());
        Assert.Equal(expected.Registry.ToArray(), actual.Registry.ToArray());
        Assert.Equal(expected.ComponentKeyPaths.OrderBy(pair => pair.Key).ToArray(),
            actual.ComponentKeyPaths.OrderBy(pair => pair.Key).ToArray());
        Assert.Equal(expected.ShortcutPath, actual.ShortcutPath);
        Assert.Equal(expected.Shortcut, actual.Shortcut);
        Assert.All(actual.Directories.Values, path => Assert.DoesNotContain("|", path));
    }

    private static void AssertPayloadPaths(CatalogFixture world, SetupPackage package)
    {
        foreach (var component in package.Profile.Components)
            if (component.File is { } file)
                Assert.True(WindowsEvidencePath.Same(
                    Path.Combine(world.Scope.AppDirectory,
                        file.Path.Replace('/', Path.DirectorySeparatorChar)),
                    Path.Combine(package.Resources.Directories[component.DirectoryId],
                        file.Path.Split('/')[^1])));
    }

    private static async Task AssertRetentionAsync(
        CatalogFixture world, AuthoringFixture bare, SetupPackage candidate,
        CancellationToken cancellationToken)
    {
        using var retention = new FileMediaRetention(world.Disk, world.Scope, world.Media);
        var retained = AggregateResult.Require(await retention.RetainAsync(
            [candidate.Evidence], cancellationToken));
        Assert.Single(retained);
        AssertPackage(world, bare, world.Catalog.Retained(candidate.Evidence),
            PackageAuthoringMode.RetainedSource);
    }

    private static void AssertRegisteredCache(CatalogFixture world, AuthoringFixture bare)
    {
        var cached = world.Catalog.Cached(world.CachedPath);
        Assert.Equal(PackageAuthoringMode.RegisteredCache, cached.Profile.Mode);
        Assert.False(cached.Profile.HasCompleteSource);
        Assert.Empty(world.Database.ReadAuthoring(world.CachedPath).Package.Streams);
        AssertResources(Expected(world, bare), SetupResources.Create(
            world.Database.ReadAuthoring(world.CachedPath), cached.Profile,
            world.Scope, RootFixture.Programs));
        AssertPackage(world, bare, world.Catalog.RegisteredSource(cached, null),
            PackageAuthoringMode.RetainedSource);
    }

    private static IEnumerable<(string Table, int Index, string Name)> Targets(
        AuthoringFixture fixture) =>
        new[] { (Table: "File", Index: 0, Name: fixture.Snapshot.RequireTable("File")[0][2]) }
            .Concat(fixture.Snapshot.RequireTable("Directory")
                .Select((row, index) => (Row: row, Index: index))
                .Where(pair => !KnownDirectory(pair.Row[0]))
                .Select(pair => ("Directory", pair.Index, pair.Row[2])));

    private static void AssertInvalidPattern(string pattern)
    {
        var bare = Bare();
        foreach (var target in Targets(bare))
        {
            var name = string.Format(CultureInfo.InvariantCulture, pattern, target.Name);
            var changed = bare.Cell(target.Table, target.Index, 2, name);
            using var world = new CatalogFixture(changed);
            AssertRejected(world, changed, SetupCode.MediaInvalid);
            if (target.Table == "Directory")
                AssertResolverRefused(world, bare, changed);
            if (target.Table == "File" ||
                changed.Snapshot.RequireTable("Directory")[target.Index][0] != "HermesMenuFolder")
                Assert.Equal(SetupCode.MediaInvalid, Assert.Throws<SetupIoException>(
                    () => world.Media.Read(world.SourcePath)).Code);
        }
    }

    private static void AssertResolverRefused(
        CatalogFixture world, AuthoringFixture bare, AuthoringFixture changed)
    {
        var profile = AggregateResult.Require(bare.Validate());
        var error = Assert.Throws<SetupIoException>(() => SetupResources.Create(
            changed.Snapshot, profile, world.Scope, RootFixture.Programs));
        Assert.Equal(SetupCode.MediaInvalid, error.Code);
    }

    private static void AssertRejected(
        CatalogFixture world, AuthoringFixture fixture, SetupCode expected)
    {
        foreach (var mode in Enum.GetValues<PackageAuthoringMode>())
            Assert.Equal(expected,
                Assert.IsType<Effect<ValidatedPackageProfile>.Failure>(
                    fixture.Validate(mode)).Code);
        foreach (var mode in new[]
            { PackageAuthoringMode.CandidateSource, PackageAuthoringMode.RetainedSource })
            Assert.Equal(expected, Assert.Throws<SetupIoException>(
                () => world.Catalog.Source(world.SourcePath, mode)).Code);
        Assert.Equal(expected, Assert.Throws<SetupIoException>(
            () => world.Catalog.Cached(world.CachedPath)).Code);
    }

    // Uses the existing virtual Windows filesystem and synthetic MSI observations.
    // No native MSI database, installation, registry or real user profile is used.
    private sealed class CatalogFixture : IDisposable
    {
        private readonly RootFiles _files = new();

        internal CatalogFixture(AuthoringFixture authoring)
        {
            var package = Package(authoring);
            Scope = UserInstallationScope.FromKnownFolder(RootFixture.LocalAppData);
            Disk = new(_files, new RootPaths(), _files);
            Database = new(Disk, [package]);
            Media = new(Disk, Scope, Database);
            SourcePath = package.SourcePath;
            CachedPath = package.CachedPath;
            _files.Put(SourcePath, package.SourceBytes.AsSpan());
            _files.Put(CachedPath, package.SourceBytes.AsSpan());
            Catalog = new(Disk, Scope, RootFixture.Programs,
                Media, Database, authoring.Approved);
        }

        internal UserInstallationScope Scope { get; }
        internal SetupDisk Disk { get; }
        internal RootDatabase Database { get; }
        internal PackageMediaReader Media { get; }
        internal SetupPackageCatalog Catalog { get; }
        internal string SourcePath { get; }
        internal string CachedPath { get; }

        private static RootPackage Package(AuthoringFixture authoring)
        {
            var manifest = StrictSetupJson.Read(
                authoring.Snapshot.Package.ManifestBytes.ToArray(),
                SetupStorageJson.Default.PayloadManifest);
            return new(authoring, manifest,
                ImmutableDictionary<string, ImmutableArray<byte>>.Empty,
                RootFixture.Prefix + "/media/aliases.msi", [17, 29, 41]);
        }

        public void Dispose()
        {
            Catalog.Dispose();
            _files.Dispose();
        }
    }
}
