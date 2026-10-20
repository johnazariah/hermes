using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Hermes.Setup;

internal static partial class PackageAuthoringValidator
{
    private const string InstallerKey = @"Software\Hermes\Installer";
    private const string ComponentKey = InstallerKey + @"\Components";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string TrayTarget = @"[INSTALLFOLDER]tray\Hermes.Tray.exe";
    private const string ShortcutComponent = "StartMenuShortcut";
    private const string RunComponent = "AutoStart";
    private const string FeatureId = "ProductFeature";
    private const string MenuDirectory = "HermesMenuFolder";
    private static readonly Guid ShortcutCode = Guid.Parse("8B3E2F4D-5C6A-7D8E-9F0A-1B2C3D4E5F6B");
    private static readonly Guid RunCode = Guid.Parse("9C4F3A5E-6D7B-8E9F-0A1B-2C3D4E5F6A7B");

    private static string PathHash(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())));

    private static (string Id, Guid Code) StableComponent(string path)
    {
        var hash = PathHash(path);
        return ("Payload_" + hash[..32],
            Guid.Parse($"{hash[..8]}-{hash[8..12]}-5{hash[13..16]}-a{hash[17..20]}-{hash[20..32]}"));
    }

    private static string DirectoryId(string path) => "Dir_" + PathHash(path)[..32];
    private static string Parent(string path) => path[..path.LastIndexOf('/')];

    private static IEnumerable<string> Ancestors(string file)
    {
        var parts = file.Split('/');
        return Enumerable.Range(1, parts.Length - 1)
            .Select(count => string.Join("/", parts.Take(count)));
    }

    private static ImmutableDictionary<string, (string Parent, string Name)> PayloadDirectories(
        PayloadManifest manifest) =>
        manifest.Files.SelectMany(file => Ancestors(file.Path))
            .Distinct(StringComparer.Ordinal)
            .ToImmutableDictionary(DirectoryId,
                path => (path.Contains('/') ? DirectoryId(Parent(path)) : "INSTALLFOLDER",
                    path.Split('/')[^1]), StringComparer.Ordinal);

    private static void ValidateDirectories(
        PackageAuthoringSnapshot snapshot, PayloadManifest manifest)
    {
        var expected = PayloadDirectories(manifest)
            .Add("INSTALLFOLDER", ("HERMES_COMPANY", "App"))
            .Add("HERMES_COMPANY", ("LocalAppDataFolder", "Hermes"))
            .Add(MenuDirectory, ("ProgramMenuFolder", "Hermes"));
        var rows = snapshot.RequireTable("Directory");
        Require(rows.Length == expected.Count + 3);
        foreach (var pair in expected)
            Require(rows.Count(row => row[0] == pair.Key &&
                row[1] == pair.Value.Parent && row[2] == pair.Value.Name) == 1);
        Require(rows.Count(row => row.SequenceEqual(["TARGETDIR", "", "SourceDir"])) == 1);
        RequireStandardDirectory(rows, "LocalAppDataFolder",
            [".", "LocalApp", "LocalAppData", "LocalAppDataFolder"]);
        RequireStandardDirectory(rows, "ProgramMenuFolder",
            [".", "PMenu", "Programs", "PMenu|Programs", "ProgramMenuFolder"]);
    }

    private static void RequireStandardDirectory(
        ImmutableArray<ImmutableArray<string>> rows, string id, ImmutableArray<string> defaults) =>
        Require(rows.Count(row => row[0] == id && row[1] == "TARGETDIR" &&
            defaults.Contains(row[2], StringComparer.Ordinal)) == 1);

    private static ImmutableArray<ProfileRegistryValue> ReadRegistry(
        PackageAuthoringSnapshot snapshot, PayloadManifest manifest)
    {
        RequireEmpty(snapshot, ["Condition", "RemoveRegistry", "CreateFolder", "Icon"]);
        var rows = snapshot.RequireTable("Registry");
        Require(rows.Length == manifest.Files.Length + 4 &&
            rows.All(row => row[1] == "1"));
        var payload = manifest.Files.Select(file =>
            RegistryValue(rows, file.ComponentId, ComponentKey, file.ComponentId, "#1"));
        return payload.Concat([
            RegistryValue(rows, ShortcutComponent, InstallerKey, "Version", manifest.Version.ToString()),
            RegistryValue(rows, ShortcutComponent, InstallerKey, "InstallDirectory", "[INSTALLFOLDER]"),
            RegistryValue(rows, ShortcutComponent, InstallerKey, "ProductCode", "[ProductCode]"),
            RegistryValue(rows, RunComponent, RunKey, "Hermes", "\"" + TrayTarget + "\"")
        ]).ToImmutableArray();
    }

    private static void RequireEmpty(
        PackageAuthoringSnapshot snapshot, ImmutableArray<string> tables)
    {
        foreach (var table in tables)
            Require(snapshot.OptionalTable(table).Length == 0);
    }

    private static ProfileRegistryValue RegistryValue(
        ImmutableArray<ImmutableArray<string>> rows,
        string component, string key, string name, string value)
    {
        var matches = rows.Where(row => row[5] == component && row[2] == key &&
            row[3] == name && row[4] == value).ToArray();
        Require(matches.Length == 1);
        return new(matches[0][0], component, key, name, value);
    }

    private static ImmutableArray<ProfileComponent> ReadComponents(
        PackageAuthoringSnapshot snapshot,
        PayloadManifest manifest,
        ImmutableArray<ProfileRegistryValue> registry)
    {
        var expected = manifest.Files.Select(file => new ProfileComponent(
            file.ComponentId, file.ComponentCode, DirectoryId(Parent(file.Path)),
            registry.Single(value => value.ComponentId == file.ComponentId).Id, file))
            .Concat([
                new(ShortcutComponent, ShortcutCode, MenuDirectory,
                    registry.Single(value => value.ComponentId == ShortcutComponent &&
                        value.Name == "Version").Id, null),
                new(RunComponent, RunCode, "INSTALLFOLDER",
                    registry.Single(value => value.ComponentId == RunComponent).Id, null)
            ]).ToImmutableArray();
        var rows = snapshot.RequireTable("Component");
        Require(rows.Length == expected.Length);
        Require(expected.All(component => rows.Count(row =>
            ComponentMatches(row, component)) == 1));
        return expected;
    }

    private static bool ComponentMatches(ImmutableArray<string> row, ProfileComponent component) =>
        row[0] == component.Id && Guid.Parse(row[1]) == component.Code &&
        row[2] == component.DirectoryId &&
        row[3] == "260" && row[4] == "" && row[5] == component.RegistryKeyPath;
        // RegistryKeyPath | 64bit only: no shared, permanent, transitive,
        // advertised/ODBC keypath, shared-DLL refcount or patch policy bits.

    private static void ValidateFeature(
        PackageAuthoringSnapshot snapshot, ImmutableArray<ProfileComponent> components)
    {
        var feature = snapshot.RequireTable("Feature");
        Require(feature.Length == 1 && feature[0][0] == FeatureId &&
            feature[0][1] == "" && feature[0][2] == "Hermes" && feature[0][3] == "" &&
            feature[0][5] == "1" && feature[0][6] == "" && feature[0][7] == "0" &&
            (feature[0][4] == "" || Number(feature[0][4]) is >= 0 and <= 32767));
        var membership = snapshot.RequireTable("FeatureComponents");
        Require(membership.Length == components.Length &&
            membership.All(row => row[0] == FeatureId &&
                components.Any(component => component.Id == row[1])));
    }

    private static ProfileShortcut ReadShortcut(
        PackageAuthoringSnapshot snapshot, PayloadManifest manifest)
    {
        var rows = snapshot.RequireTable("Shortcut");
        Require(rows.Length == 1);
        var row = rows[0];
        Require(row[0] == "HermesShortcut" && row[1] == MenuDirectory &&
            row[2] == "Hermes" && row[3] == ShortcutComponent && row[4] == TrayTarget &&
            row[5] == "" && row[6] == "Hermes Document Intelligence" &&
            row[7] == "" && row[8] == "" && row[9] == "" &&
            row[10] is "" or "1" && row[11] == "INSTALLFOLDER" &&
            row.Skip(12).All(value => value == ""));
        Require(manifest.Files.Any(file => file.Path == "tray/Hermes.Tray.exe"));
        return new(row[0], row[3], row[1], row[2], row[4], row[5], row[11]);
    }

    private static ImmutableDictionary<string, (string Directory, string Component)> RemovalSpecs(
        PayloadManifest manifest) =>
        manifest.Files.Select(file => Parent(file.Path)).Distinct(StringComparer.Ordinal)
            .ToImmutableDictionary(path => "Remove_" + DirectoryId(path),
                path => (DirectoryId(path), ""), StringComparer.Ordinal)
            .Add("RemoveMenuFolder", (MenuDirectory, ShortcutComponent))
            .Add("RemoveApplicationFolder", ("INSTALLFOLDER", RunComponent))
            .Add("RemoveCompanyFolder", ("HERMES_COMPANY", RunComponent));

    private static ImmutableArray<ProfileDirectoryRemoval> ReadRemovals(
        PackageAuthoringSnapshot snapshot,
        PayloadManifest manifest,
        ImmutableArray<ProfileComponent> components)
    {
        var expected = RemovalSpecs(manifest);
        var rows = snapshot.RequireTable("RemoveFile");
        Require(rows.Length == expected.Count);
        Require(rows.All(row => row[2] == "" && row[4] == "2" &&
            expected.TryGetValue(row[0], out var spec) && row[3] == spec.Directory &&
            (spec.Component.Length > 0 ? row[1] == spec.Component :
                components.Any(component => component.Id == row[1] &&
                    component.File is not null && component.DirectoryId == spec.Directory))));
        return rows.Select(row => new ProfileDirectoryRemoval(row[0], row[1], row[3]))
            .ToImmutableArray();
    }
}
