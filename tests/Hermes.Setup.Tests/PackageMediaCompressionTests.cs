using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed class PackageMediaCompressionTests
{
    private const string Cabinet = "payload.cab";
    private const string ServiceDirectory = "ServiceDirectory";
    private const string TrayDirectory = "TrayDirectory";

    [Theory]
    [InlineData(10, "")]
    [InlineData(10, "0")]
    [InlineData(10, "512")]
    [InlineData(10, "1543")]
    [InlineData(10, "16384")]
    [InlineData(10, "17927")]
    [InlineData(8, "16384")]
    public void RealReaderAcceptsDefaultOrExplicitCompression(int wordCount, string attributes)
    {
        using var fixture = new MediaFixture();
        var image = Attributes(fixture.Image with { WordCount = wordCount }, attributes);
        var actual = fixture.Reader(image).Read(fixture.Source);
        Assert.Equal(fixture.Manifest.ProductCode, actual.Evidence.Identity.ProductCode);
        Assert.Equal(3, actual.Manifest.Files.Length);
        Assert.Equal(fixture.Manifest.Files.Select(file => (file.Path, file.ModuleVersionId)),
            actual.Manifest.Files.Select(file => (file.Path, file.ModuleVersionId)));
        Assert.Null(actual.Manifest.Files.Single(file => file.FileId == "AuxFile").ModuleVersionId);
        Assert.Equal(PackageMediaReader.Hash(File.ReadAllBytes(fixture.Source)),
            actual.Evidence.PackageSha256);
        Assert.Equal(PackageMediaReader.Hash(image.ManifestBytes.ToArray()),
            actual.Evidence.ManifestSha256);
    }

    [Theory]
    [InlineData(10, "512")]
    [InlineData(10, "16384")]
    [InlineData(8, "16384")]
    public async Task RetentionUsesTheRealReaderAndRetainsCompleteAcceptedMedia(
        int wordCount, string attributes)
    {
        using var fixture = new MediaFixture();
        var image = Attributes(fixture.Image with { WordCount = wordCount }, attributes);
        var expected = fixture.Reader(image).Read(fixture.Source).Evidence;
        using var cache = fixture.Retention(image);
        var success = Assert.IsType<Effect<ImmutableArray<RetainedMedia>>.Success>(
            await cache.RetainAsync([expected], CancellationToken.None));
        var retained = Assert.Single(success.Value);
        Assert.Equal(File.ReadAllBytes(fixture.Source), File.ReadAllBytes(retained.PackagePath));
        Assert.Equal(image.ManifestBytes.ToArray(), File.ReadAllBytes(retained.ManifestPath));
        Assert.Equal(new[] { DiskEvent.Flush, DiskEvent.Commit,
            DiskEvent.Flush, DiskEvent.Commit }, fixture.Events);
    }

    [Theory]
    [InlineData(10, "8192")]
    [InlineData(10, "8704")]
    [InlineData(10, "24576")]
    [InlineData(10, "8")]
    [InlineData(10, "4096")]
    [InlineData(10, "32768")]
    [InlineData(10, "-1")]
    [InlineData(10, "not-an-integer")]
    [InlineData(8, "512")]
    [InlineData(8, "")]
    [InlineData(8, "16384")]
    public async Task LooseOrInvalidFileMediaRefusesBeforeCachePublication(
        int wordCount, string auxiliaryAttributes)
    {
        using var fixture = new MediaFixture();
        var image = Cell(fixture.Image with { WordCount = wordCount },
            "File", 2, 6, auxiliaryAttributes);
        await AssertRefusedAsync(fixture, image);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(11)]
    [InlineData(14)]
    [InlineData(26)]
    [InlineData(-1)]
    public async Task UnsupportedSummaryFlagsDoNotRelaxThePackageContract(int wordCount)
    {
        using var fixture = new MediaFixture();
        await AssertRefusedAsync(fixture, fixture.Image with { WordCount = wordCount });
    }

    [Theory]
    [InlineData("Media", 0, 3, "payload.cab")]
    [InlineData("Media", 0, 3, "")]
    [InlineData("Media", 0, 1, "2")]
    [InlineData("File", 2, 7, "0")]
    [InlineData("File", 2, 7, "4")]
    [InlineData("File", 2, 2, "different.dat")]
    [InlineData("Directory", 2, 2, "different")]
    public async Task InvalidCabinetCoverageOrPayloadLayoutRemainsARefusal(
        string table, int row, int column, string value)
    {
        using var fixture = new MediaFixture();
        await AssertRefusedAsync(fixture, Cell(fixture.Image, table, row, column, value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmbeddedCabinetMustHaveAPresentNonemptyStream(bool present)
    {
        using var fixture = new MediaFixture();
        var streams = present
            ? ImmutableDictionary<string, uint>.Empty.Add(Cabinet, 0)
            : ImmutableDictionary<string, uint>.Empty;
        await AssertRefusedAsync(fixture, fixture.Image with { Streams = streams });
    }

    [Fact]
    public async Task MissingMediaRowsCannotDescribeACompleteCache()
    {
        using var fixture = new MediaFixture();
        var image = fixture.Image;
        await AssertRefusedAsync(fixture, image with
        {
            Tables = image.Tables.SetItem("Media", ImmutableArray<ImmutableArray<string>>.Empty)
        });
    }

    private static async Task AssertRefusedAsync(MediaFixture fixture, PackageDatabaseImage image)
    {
        var expected = fixture.Reader(fixture.Image).Read(fixture.Source).Evidence;
        var error = Assert.Throws<SetupIoException>(() => fixture.Reader(image).Read(fixture.Source));
        Assert.Equal(SetupCode.MediaInvalid, error.Code);
        using var cache = fixture.Retention(image);
        var failure = Assert.IsType<Effect<ImmutableArray<RetainedMedia>>.Failure>(
            await cache.RetainAsync([expected], CancellationToken.None));
        Assert.Equal(SetupCode.MediaInvalid, failure.Code);
        Assert.Empty(fixture.Events);
        Assert.False(File.Exists(fixture.PackagePath(expected)));
        Assert.False(File.Exists(fixture.ManifestPath(expected)));
        Assert.Equal(new byte[] { 10, 20, 30 }, File.ReadAllBytes(fixture.Source));
    }

    private static PackageDatabaseImage Attributes(PackageDatabaseImage image, string attributes) =>
        image with
        {
            Tables = image.Tables.SetItem("File", image.Table("File")
                .Select(row => row.SetItem(6, attributes)).ToImmutableArray())
        };

    private static PackageDatabaseImage Cell(
        PackageDatabaseImage image, string table, int row, int column, string value) =>
        image with
        {
            Tables = image.Tables.SetItem(table,
                image.Table(table).SetItem(row, image.Table(table)[row].SetItem(column, value)))
        };

    private static PackageDatabaseImage Snapshot(PayloadManifest manifest) =>
        new(manifest.PackageCode.ToString("B"), "x64;1033", 10, Tables(manifest),
            ImmutableDictionary<string, uint>.Empty.Add(Cabinet, 128),
            JsonSerializer.SerializeToUtf8Bytes(
                manifest, SetupStorageJson.Default.PayloadManifest).ToImmutableArray());

    private static ImmutableDictionary<string, ImmutableArray<ImmutableArray<string>>> Tables(
        PayloadManifest manifest) =>
        new Dictionary<string, ImmutableArray<ImmutableArray<string>>>
        {
            ["Property"] = Rows(
                ["UpgradeCode", manifest.UpgradeCode.ToString("B")],
                ["ProductCode", manifest.ProductCode.ToString("B")],
                ["ProductVersion", manifest.Version.ToString()]),
            ["Directory"] = Rows(
                ["INSTALLFOLDER", "HERMES_COMPANY", "App"],
                ["HERMES_COMPANY", "LocalAppDataFolder", "Hermes"],
                [ServiceDirectory, "INSTALLFOLDER", "service"],
                [TrayDirectory, "INSTALLFOLDER", "tray"]),
            ["Component"] = manifest.Files.Select(ComponentRow).ToImmutableArray(),
            ["File"] = manifest.Files.Select(FileRow).ToImmutableArray(),
            ["Media"] = Rows(["1", manifest.Files.Length.ToString(CultureInfo.InvariantCulture),
                "", "#" + Cabinet, "", ""])
        }.ToImmutableDictionary();

    private static ImmutableArray<string> ComponentRow(PayloadFile file) =>
        ImmutableArray.Create(file.ComponentId, file.ComponentCode.ToString("B"),
            file.Path.StartsWith("service/", StringComparison.Ordinal)
                ? ServiceDirectory : TrayDirectory, "0", "", file.FileId);

    private static ImmutableArray<string> FileRow(PayloadFile file, int index) =>
        ImmutableArray.Create(file.FileId, file.ComponentId, file.Path.Split('/')[^1],
            file.Length.ToString(CultureInfo.InvariantCulture), "", "", "512",
            (index + 1).ToString(CultureInfo.InvariantCulture));

    private static ImmutableArray<ImmutableArray<string>> Rows(params string[][] rows) =>
        rows.Select(row => row.ToImmutableArray()).ToImmutableArray();

    private sealed class MediaFixture : IDisposable
    {
        private readonly IoFixture _io = new();

        internal MediaFixture()
        {
            var seed = _io.Evidence([10, 20, 30], [0]);
            var manifest = _io.Media(seed, []).Manifest;
            Manifest = manifest with
            {
                Files = manifest.Files.Add(new("service/auxiliary.dat", new string('b', 64), 3,
                    null, "AuxFile", "AuxComponent", Guid.NewGuid()))
            };
            Image = Snapshot(Manifest);
        }

        internal PayloadManifest Manifest { get; }
        internal PackageDatabaseImage Image { get; }
        internal string Source => _io.Source;
        internal IReadOnlyList<DiskEvent> Events => _io.Files.Events;

        internal PackageMediaReader Reader(PackageDatabaseImage image) =>
            new(_io.Disk, _io.Scope, new SnapshotDatabase(image));

        internal FileMediaRetention Retention(PackageDatabaseImage image) =>
            new(_io.Disk, _io.Scope, Reader(image));

        internal string PackagePath(PackageEvidence evidence) =>
            MediaCachePaths.Package(_io.Scope, evidence.PackageSha256);

        internal string ManifestPath(PackageEvidence evidence) =>
            MediaCachePaths.Manifest(_io.Scope, evidence.PackageSha256);

        public void Dispose() => _io.Dispose();
    }

    private sealed class SnapshotDatabase(PackageDatabaseImage image) : IPackageDatabase
    {
        public PackageDatabaseImage Read(string packagePath) => image;
    }
}
