using System.Collections.Immutable;
using System.Security.Cryptography;
using Hermes.Servicing;

namespace Hermes.Setup;

internal sealed record PayloadFile(
    string Path,
    string Sha256,
    long Length,
    Guid? ModuleVersionId,
    string FileId,
    string ComponentId,
    Guid ComponentCode);

internal sealed record PayloadManifest(
    int SchemaVersion,
    int AdmissionAbi,
    string Root,
    string Scope,
    Guid UpgradeCode,
    Guid ProductCode,
    Guid PackageCode,
    Version Version,
    ImmutableArray<PayloadFile> Files);

internal sealed record PackageDatabaseImage(
    string PackageCode,
    string Template,
    int WordCount,
    ImmutableDictionary<string, ImmutableArray<ImmutableArray<string>>> Tables,
    ImmutableDictionary<string, uint> Streams,
    ImmutableArray<byte> ManifestBytes)
{
    internal ImmutableArray<ImmutableArray<string>> Table(string name) =>
        Tables.TryGetValue(name, out var rows)
            ? rows : throw new SetupIoException(SetupCode.MediaInvalid);
}

internal interface IPackageDatabase
{
    PackageDatabaseImage Read(string packagePath);
}

internal sealed record PackageMedia(
    PackageEvidence Evidence,
    PayloadManifest Manifest,
    ImmutableArray<byte> ManifestBytes);

internal interface IPackageMediaReader
{
    PackageMedia Read(string packagePath);
}

internal sealed class PackageMediaReader(
    SetupDisk disk,
    UserInstallationScope scope,
    IPackageDatabase database) : IPackageMediaReader
{
    internal const string BinaryName = "HermesPayloadManifest";
    internal const string RootToken = "LocalApplicationData/Hermes/App";
    internal const string ScopeToken = "perUser";

    public PackageMedia Read(string packagePath)
    {
        using var source = disk.Read(packagePath);
        var sourceHash = Hash(source);
        var image = database.Read(packagePath);
        var bytes = image.ManifestBytes.ToArray();
        var manifest = StrictSetupJson.Read(bytes, SetupStorageJson.Default.PayloadManifest);
        ValidateIdentity(manifest, image);
        ValidateFiles(manifest, image);
        ValidateMedia(image);
        var evidence = Evidence(manifest, packagePath, sourceHash, Hash(bytes));
        return new(evidence, manifest, image.ManifestBytes);
    }

    private PackageEvidence Evidence(
        PayloadManifest manifest, string path, string packageHash, string manifestHash) =>
        new(new(manifest.UpgradeCode, manifest.ProductCode, manifest.PackageCode,
                manifest.Version, manifest.AdmissionAbi, PackageScope.PerUser),
            scope.AppDirectory, path, MediaCachePaths.Manifest(scope, packageHash),
            packageHash, manifestHash,
            manifest.Files.Where(file => file.ModuleVersionId is not null)
                .Select(file => new ManagedImage(file.Path,
                    file.ModuleVersionId ?? throw new SetupIoException(SetupCode.MediaInvalid)))
                .ToImmutableArray());

    internal static void ValidateIdentity(PayloadManifest manifest, PackageDatabaseImage image)
    {
        SetupIo.Require(manifest.SchemaVersion == 1 &&
            manifest.AdmissionAbi == HermesServicingProtocol.AdmissionAbi &&
            manifest.Root == RootToken && manifest.Scope == ScopeToken &&
            manifest.UpgradeCode == HermesServicingProtocol.UpgradeCode &&
            manifest.ProductCode != Guid.Empty && manifest.PackageCode != Guid.Empty &&
            manifest.Version.Build >= 0 && manifest.Version.Revision == -1,
            SetupCode.MediaInvalid);
        SetupIo.Require(Guid.Parse(image.PackageCode) == manifest.PackageCode &&
            image.Template.Split(';')[0] == "x64" && image.WordCount is 8 or 10,
            SetupCode.MediaInvalid);
        RequireProperty(image, "UpgradeCode", manifest.UpgradeCode.ToString("B"), true);
        RequireProperty(image, "ProductCode", manifest.ProductCode.ToString("B"), true);
        RequireProperty(image, "ProductVersion", manifest.Version.ToString(), false);
        SetupIo.Require(Property(image, "ALLUSERS") == "" &&
            Property(image, "INSTALLFOLDER") == "", SetupCode.RootMismatch);
    }

    private static void RequireProperty(
        PackageDatabaseImage image, string name, string expected, bool ignoreCase) =>
        SetupIo.Require(string.Equals(Property(image, name), expected,
            ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
            SetupCode.MediaIdentityMismatch);

    private static string Property(PackageDatabaseImage image, string name)
    {
        var matches = image.Table("Property").Where(row => row[0] == name).ToArray();
        SetupIo.Require(matches.Length <= 1, SetupCode.MediaInvalid);
        return matches.Length == 0 ? "" : matches[0][1];
    }

    internal static void ValidateFiles(PayloadManifest manifest, PackageDatabaseImage image)
    {
        SetupIo.Require(!manifest.Files.IsDefaultOrEmpty &&
            manifest.Files.All(ValidFile) &&
            manifest.Files.Select(file => file.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() == manifest.Files.Length &&
            manifest.Files.Select(file => file.FileId).Distinct().Count() == manifest.Files.Length &&
            manifest.Files.Select(file => file.ComponentId).Distinct().Count() == manifest.Files.Length,
            SetupCode.MediaInvalid);
        RequireRoot(image);
        SetupIo.Require(image.Table("File").Length == manifest.Files.Length &&
            manifest.Files.All(file => FileMatches(file, image)), SetupCode.MediaInvalid);
        RequireEntry(manifest, "service/Hermes.Service.dll");
        RequireEntry(manifest, "tray/Hermes.Tray.dll");
    }

    private static bool ValidFile(PayloadFile file) =>
        ValidRelative(file.Path) && file.Length >= 0 &&
        file.Sha256.Length == 64 && file.Sha256.All(Uri.IsHexDigit) &&
        file.ComponentCode != Guid.Empty &&
        file.ModuleVersionId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(file.FileId) &&
        !string.IsNullOrWhiteSpace(file.ComponentId);

    internal static bool ValidRelative(string path) =>
        (path.StartsWith("service/", StringComparison.Ordinal) ||
         path.StartsWith("tray/", StringComparison.Ordinal)) &&
        path.Split('/').All(segment =>
            segment.Length > 0 && segment is not "." and not ".." &&
            segment[^1] != '.' &&
            segment.All(character => char.IsAsciiLetterOrDigit(character) ||
                character is '_' or '@' or '.' or '+' or '-'));

    private static void RequireEntry(PayloadManifest manifest, string path) =>
        SetupIo.Require(manifest.Files.Any(file =>
            file.Path == path && file.ModuleVersionId is { } id && id != Guid.Empty),
            SetupCode.MediaInvalid);

    private static bool FileMatches(PayloadFile file, PackageDatabaseImage image)
    {
        var rows = image.Table("File").Where(row => row[0] == file.FileId).ToArray();
        var components = image.Table("Component")
            .Where(row => row[0] == file.ComponentId).ToArray();
        if (rows.Length != 1 || components.Length != 1)
            return false;
        var row = rows[0];
        var component = components[0];
        var relative = RelativeDirectory(image, component[2], []);
        var name = relative.Length == 0 ? LongName(row[2]) : relative + "/" + LongName(row[2]);
        return row[1] == file.ComponentId && name == file.Path &&
            long.Parse(row[3], System.Globalization.CultureInfo.InvariantCulture) == file.Length &&
            Guid.Parse(component[1]) == file.ComponentCode &&
            component[4] == "" &&
            int.Parse(row[7], System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static string RelativeDirectory(
        PackageDatabaseImage image, string id, ImmutableHashSet<string> visited)
    {
        if (id == "INSTALLFOLDER")
            return "";
        SetupIo.Require(visited.Count < 64 && !visited.Contains(id), SetupCode.MediaInvalid);
        var rows = image.Table("Directory").Where(row => row[0] == id).ToArray();
        SetupIo.Require(rows.Length == 1, SetupCode.MediaInvalid);
        var parent = RelativeDirectory(image, rows[0][1], visited.Add(id));
        var name = LongName(rows[0][2].Split(':')[0]);
        SetupIo.Require(name != "." && name.Length > 0, SetupCode.MediaInvalid);
        return parent.Length == 0 ? name : parent + "/" + name;
    }

    private static void RequireRoot(PackageDatabaseImage image)
    {
        RequireDirectory(image, "INSTALLFOLDER", "HERMES_COMPANY", "App");
        RequireDirectory(image, "HERMES_COMPANY", "LocalAppDataFolder", "Hermes");
    }

    private static void RequireDirectory(
        PackageDatabaseImage image, string id, string parent, string name) =>
        SetupIo.Require(image.Table("Directory").Count(row =>
            row[0] == id && row[1] == parent &&
            LongName(row[2].Split(':')[0]) == name) == 1, SetupCode.RootMismatch);

    private static string LongName(string value) => value.Split('|')[^1];

    internal static void ValidateMedia(PackageDatabaseImage image)
    {
        var media = image.Table("Media");
        SetupIo.Require(!media.IsDefaultOrEmpty &&
            media.All(row => row[3].StartsWith('#') &&
                image.Streams.TryGetValue(row[3][1..], out var length) && length > 0),
            SetupCode.MediaInvalid);
        var lastSequence = media.Max(row =>
            int.Parse(row[1], System.Globalization.CultureInfo.InvariantCulture));
        SetupIo.Require(image.Table("File").All(row =>
            SelfContainedFile(row, image.WordCount) &&
            int.Parse(row[7], System.Globalization.CultureInfo.InvariantCulture) <= lastSequence),
            SetupCode.MediaInvalid);
    }

    internal static bool SelfContainedFile(ImmutableArray<string> row, int wordCount)
    {
        const int compressed = 0x4000;
        // ReadOnly, Hidden, System, Vital, Checksum and Compressed only.
        // Noncompressed, PatchAdded and reserved attributes are unsupported.
        const int supported = 0x0607 | compressed;
        return int.TryParse(row[6] is "" ? "0" : row[6],
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var attributes) &&
            (attributes & ~supported) == 0 &&
            ((attributes & compressed) != 0 || (wordCount & 2) != 0);
    }

    internal static string Hash(Stream stream) =>
        Convert.ToHexStringLower(SHA256.HashData(stream));

    internal static string Hash(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}
