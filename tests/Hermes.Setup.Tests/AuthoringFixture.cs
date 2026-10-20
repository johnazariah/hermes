using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hermes.Servicing;
using Hermes.Setup;

namespace Hermes.Setup.Tests;

internal sealed record AuthoringFixture(
    PackageAuthoringSnapshot Snapshot,
    ApprovedGuardProfile Approved)
{
    internal static AuthoringFixture Create() => Create(Manifest());

    internal static AuthoringFixture Create(PayloadManifest manifest)
    {
        var tables = Tables(manifest);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            manifest, SetupStorageJson.Default.PayloadManifest).ToImmutableArray();
        var guard = NativeImage().ToImmutableArray();
        var package = new PackageDatabaseImage(manifest.PackageCode.ToString("B"),
            "x64;1033", 10, tables.Where(pair =>
                PackageAuthoringSchema.BaseColumns.ContainsKey(pair.Key)).ToImmutableDictionary(),
            ImmutableDictionary<string, uint>.Empty.Add("payload.cab", 1024), bytes);
        var names = tables.Keys.Append("Binary").ToImmutableHashSet(StringComparer.Ordinal);
        var snapshot = new PackageAuthoringSnapshot(package, names, Definitions(names),
            tables, ImmutableDictionary<string, ImmutableArray<byte>>.Empty
                .Add("HermesPayloadManifest", bytes).Add("HermesServicingGuard", guard));
        return new(snapshot, new(1, PackageMediaReader.RootToken,
            ImmutableHashSet.Create(PackageMediaReader.Hash(guard.ToArray()))));
    }

    internal Effect<ValidatedPackageProfile> Validate(
        PackageAuthoringMode mode = PackageAuthoringMode.CandidateSource) =>
        PackageAuthoringValidator.Validate(Snapshot, mode, Approved);

    internal AuthoringFixture Rows(string table, ImmutableArray<ImmutableArray<string>> rows)
    {
        var package = PackageAuthoringSchema.BaseColumns.ContainsKey(table)
            ? Snapshot.Package with { Tables = Snapshot.Package.Tables.SetItem(table, rows) }
            : Snapshot.Package;
        return this with
        {
            Snapshot = Snapshot with
            {
                Package = package,
                Tables = Snapshot.Tables.SetItem(table, rows)
            }
        };
    }

    internal AuthoringFixture Cell(string table, int row, int column, string value) =>
        Rows(table, Snapshot.RequireTable(table).SetItem(row,
            Snapshot.RequireTable(table)[row].SetItem(column, value)));

    internal AuthoringFixture Append(string table, params string[] row) =>
        Rows(table, Snapshot.RequireTable(table).Add(row.ToImmutableArray()));

    internal AuthoringFixture Guard(byte[] bytes, bool approveSyntheticImage)
    {
        var approved = approveSyntheticImage
            ? Approved with { Sha256 = ImmutableHashSet.Create(PackageMediaReader.Hash(bytes)) }
            : Approved;
        return this with
        {
            Approved = approved,
            Snapshot = Snapshot with
            {
                Binaries = Snapshot.Binaries.SetItem("HermesServicingGuard", bytes.ToImmutableArray())
            }
        };
    }

    internal static ImmutableArray<ImmutableArray<string>> R(params string[][] rows) =>
        rows.Select(row => row.ToImmutableArray()).ToImmutableArray();

    private static ImmutableArray<ImmutableArray<string>> Definitions(IEnumerable<string> names) =>
        names.SelectMany(name => PackageAuthoringSchema.Columns[name]
            .Select((column, index) => ImmutableArray.Create(
                name, (index + 1).ToString(CultureInfo.InvariantCulture), column,
                index == 0 ? "11592" : "7496"))).ToImmutableArray();

    private static PayloadManifest Manifest() =>
        new(1, 1, PackageMediaReader.RootToken, "perUser",
            HermesServicingProtocol.UpgradeCode,
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Guid.Parse("66666666-7777-8888-9999-AAAAAAAAAAAA"),
            new Version(1, 2, 3),
            new[] { "service/Hermes.Service.dll", "service/Hermes.Service.exe",
                "tray/Hermes.Tray.dll", "tray/Hermes.Tray.exe" }
                .Select(File).ToImmutableArray());

    internal static PayloadFile File(string path)
    {
        var hash = Hash(path);
        var id = "Payload_" + hash[..32];
        var code = Guid.Parse(
            $"{hash[..8]}-{hash[8..12]}-5{hash[13..16]}-a{hash[17..20]}-{hash[20..32]}");
        return new(path, new string('a', 64), 32,
            path.EndsWith(".dll", StringComparison.Ordinal)
                ? Guid.Parse("12345678-1234-1234-1234-123456789ABC") : null,
            "File_" + id, id, code);
    }

    private static string Hash(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())));

    private static string Dir(string path) => "Dir_" + Hash(path)[..32];

    private static ImmutableDictionary<string, ImmutableArray<ImmutableArray<string>>> Tables(
        PayloadManifest manifest)
    {
        var tables = CoreTables(manifest).SetItems(ResourceTables(manifest));
        return tables.SetItems(new Dictionary<string, ImmutableArray<ImmutableArray<string>>>
        {
            ["CustomAction"] = R(
                ["HermesPreflight", "1", "HermesServicingGuard", "HermesPreflight"],
                ["HermesTransactionFence", "1", "HermesServicingGuard", "HermesTransactionFence"]),
            ["InstallExecuteSequence"] = ExecuteSequence(),
            ["AdminExecuteSequence"] = R(["HermesPreflight", "", "1399"]),
            ["AdvtExecuteSequence"] = R(["HermesPreflight", "", "1399"]),
            ["InstallUISequence"] = [],
            ["AdminUISequence"] = [],
            ["Condition"] = [], ["RemoveRegistry"] = [], ["CreateFolder"] = [], ["Icon"] = [],
            ["AppSearch"] = R(["INSTALLFOLDER", "ExistingInstallDirectory"]),
            ["RegLocator"] = R(
                ["ExistingInstallDirectory", "1", @"Software\Hermes\Installer", "InstallDirectory", "2"])
        });
    }

    private static ImmutableDictionary<string, ImmutableArray<ImmutableArray<string>>> CoreTables(
        PayloadManifest manifest) =>
        new Dictionary<string, ImmutableArray<ImmutableArray<string>>>
        {
            ["Property"] = Properties(manifest),
            ["Directory"] = R(
                ["TARGETDIR", "", "SourceDir"],
                ["LocalAppDataFolder", "TARGETDIR", "LocalApp"],
                ["ProgramMenuFolder", "TARGETDIR", "PMenu|Programs"],
                ["HERMES_COMPANY", "LocalAppDataFolder", "Hermes"],
                ["INSTALLFOLDER", "HERMES_COMPANY", "App"],
                ["HermesMenuFolder", "ProgramMenuFolder", "Hermes"],
                [Dir("service"), "INSTALLFOLDER", "service"],
                [Dir("tray"), "INSTALLFOLDER", "tray"]),
            ["File"] = manifest.Files.Select((file, index) => ImmutableArray.Create(
                file.FileId, file.ComponentId, file.Path.Split('/')[^1],
                file.Length.ToString(CultureInfo.InvariantCulture), "", "", "512",
                (index + 1).ToString(CultureInfo.InvariantCulture))).ToImmutableArray(),
            ["Media"] = R(["1", manifest.Files.Length.ToString(CultureInfo.InvariantCulture),
                "", "#payload.cab", "", ""]),
            ["Upgrade"] = R(
                [manifest.UpgradeCode.ToString("B"), "0.0.0", "", "", "256", "", "HERMES_RELATED_PRODUCTS"],
                [manifest.UpgradeCode.ToString("B"), manifest.Version.ToString(3), "", "", "2", "", "HERMES_NEWER_PRODUCT"]),
            ["LaunchCondition"] = R(
                ["NOT HERMES_NEWER_PRODUCT OR HERMES_ALLOW_ROLLBACK = 1",
                 "A newer Hermes version is installed. Explicit rollback requires HERMES_ALLOW_ROLLBACK=1."])
        }.ToImmutableDictionary();

    private static ImmutableArray<ImmutableArray<string>> Properties(PayloadManifest manifest) =>
        R(["ProductCode", manifest.ProductCode.ToString("B")],
          ["UpgradeCode", manifest.UpgradeCode.ToString("B")],
          ["ProductVersion", manifest.Version.ToString(3)], ["ProductName", "Hermes"],
          ["ProductLanguage", "1033"], ["Manufacturer", "John Azariah"],
          ["REBOOT", "ReallySuppress"], ["MSIRESTARTMANAGERCONTROL", "Disable"],
          ["SecureCustomProperties",
           "HERMES_SERVICING_RUN;HERMES_SERVICING_ABI;HERMES_ALLOW_ROLLBACK;" +
           "HERMES_RELATED_PRODUCTS;HERMES_NEWER_PRODUCT;INSTALLFOLDER"]);

    private static ImmutableDictionary<string, ImmutableArray<ImmutableArray<string>>> ResourceTables(
        PayloadManifest manifest) =>
        new Dictionary<string, ImmutableArray<ImmutableArray<string>>>
        {
            ["Component"] = Components(manifest),
            ["Registry"] = Registry(manifest),
            ["Feature"] = R(["ProductFeature", "", "Hermes", "", "2", "1", "", "0"]),
            ["FeatureComponents"] = manifest.Files.Select(file =>
                    ImmutableArray.Create("ProductFeature", file.ComponentId))
                .Concat(R(["ProductFeature", "StartMenuShortcut"], ["ProductFeature", "AutoStart"]))
                .ToImmutableArray(),
            ["Shortcut"] = R(
                ["HermesShortcut", "HermesMenuFolder", "Hermes", "StartMenuShortcut",
                 @"[INSTALLFOLDER]tray\Hermes.Tray.exe", "", "Hermes Document Intelligence",
                 "", "", "", "", "INSTALLFOLDER"]),
            ["RemoveFile"] = Removals(manifest)
        }.ToImmutableDictionary();

    private static ImmutableArray<ImmutableArray<string>> Components(PayloadManifest manifest) =>
        manifest.Files.Select(file => ImmutableArray.Create(file.ComponentId,
            file.ComponentCode.ToString("B"), Dir(file.Path.Split('/')[0]), "260", "",
            "Reg_" + file.ComponentId)).Concat(R(
                ["StartMenuShortcut", "{8B3E2F4D-5C6A-7D8E-9F0A-1B2C3D4E5F6B}",
                 "HermesMenuFolder", "260", "", "RegVersion"],
                ["AutoStart", "{9C4F3A5E-6D7B-8E9F-0A1B-2C3D4E5F6A7B}",
                 "INSTALLFOLDER", "260", "", "RegRun"])).ToImmutableArray();

    private static ImmutableArray<ImmutableArray<string>> Registry(PayloadManifest manifest) =>
        manifest.Files.Select(file => ImmutableArray.Create("Reg_" + file.ComponentId,
            "1", @"Software\Hermes\Installer\Components", file.ComponentId, "#1", file.ComponentId))
            .Concat(R(
                ["RegVersion", "1", @"Software\Hermes\Installer", "Version", manifest.Version.ToString(3), "StartMenuShortcut"],
                ["RegRoot", "1", @"Software\Hermes\Installer", "InstallDirectory", "[INSTALLFOLDER]",
                 "StartMenuShortcut"],
                ["RegProduct", "1", @"Software\Hermes\Installer", "ProductCode", "[ProductCode]",
                 "StartMenuShortcut"],
                ["RegRun", "1", @"Software\Microsoft\Windows\CurrentVersion\Run", "Hermes",
                 "\"[INSTALLFOLDER]tray\\Hermes.Tray.exe\"", "AutoStart"])).ToImmutableArray();

    private static ImmutableArray<ImmutableArray<string>> Removals(PayloadManifest manifest) =>
        manifest.Files.GroupBy(file => file.Path.Split('/')[0]).Select(group =>
            ImmutableArray.Create("Remove_" + Dir(group.Key), group.First().ComponentId,
                "", Dir(group.Key), "2")).Concat(R(
                    ["RemoveMenuFolder", "StartMenuShortcut", "", "HermesMenuFolder", "2"],
                    ["RemoveApplicationFolder", "AutoStart", "", "INSTALLFOLDER", "2"],
                    ["RemoveCompanyFolder", "AutoStart", "", "HERMES_COMPANY", "2"]))
            .ToImmutableArray();

    private static ImmutableArray<ImmutableArray<string>> ExecuteSequence() =>
        R(["FindRelatedProducts", "", "25"], ["AppSearch", "", "50"],
          ["LaunchConditions", "", "100"], ["ValidateProductID", "", "700"],
          ["CostInitialize", "", "800"], ["FileCost", "", "900"], ["CostFinalize", "", "1000"],
          ["HermesPreflight", "", "1399"], ["InstallValidate", "", "1400"],
          ["InstallInitialize", "", "1500"], ["HermesTransactionFence", "", "1501"],
          ["RemoveExistingProducts", "HERMES_RELATED_PRODUCTS", "1502"],
          ["ProcessComponents", "", "1600"], ["UnpublishFeatures", "", "1800"],
          ["RemoveRegistryValues", "", "2600"], ["RemoveShortcuts", "", "3200"],
          ["RemoveFiles", "", "3500"], ["RemoveFolders", "", "3600"],
          ["InstallFiles", "", "4000"], ["CreateShortcuts", "", "4500"],
          ["WriteRegistryValues", "", "5000"], ["RegisterUser", "", "6000"],
          ["RegisterProduct", "", "6100"], ["PublishFeatures", "", "6300"],
          ["PublishProduct", "", "6400"], ["InstallFinalize", "", "6600"]);

    // A synthetic PE fixture, not a real guard or an executable identity claim.
    internal static byte[] NativeImage()
    {
        var bytes = new byte[0x600];
        Put16(bytes, 0, 0x5A4D); Put32(bytes, 0x3C, 0x80);
        Put32(bytes, 0x80, 0x4550); Put16(bytes, 0x84, 0x8664);
        Put16(bytes, 0x86, 1); Put16(bytes, 0x94, 0xF0); Put16(bytes, 0x96, 0x2022);
        Put16(bytes, 0x98, 0x20B); Put32(bytes, 0xA8, 0x1000);
        Put32(bytes, 0xAC, 0x1000); Put32(bytes, 0xB8, 0x1000);
        Put32(bytes, 0xBC, 0x200); Put32(bytes, 0xD0, 0x2000);
        Put32(bytes, 0xD4, 0x200); Put16(bytes, 0xDC, 2);
        Put32(bytes, 0x104, 16); Put32(bytes, 0x108, 0x1100);
        Put32(bytes, 0x10C, 0x100);
        Section(bytes);
        ExportDirectory(bytes);
        return bytes;
    }

    private static void Section(byte[] bytes)
    {
        Encoding.ASCII.GetBytes(".text").CopyTo(bytes, 0x188);
        Put32(bytes, 0x190, 0x400); Put32(bytes, 0x194, 0x1000);
        Put32(bytes, 0x198, 0x400); Put32(bytes, 0x19C, 0x200);
        Put32(bytes, 0x1AC, 0x60000020);
    }

    private static void ExportDirectory(byte[] bytes)
    {
        Put32(bytes, 0x310, 1); Put32(bytes, 0x314, 2); Put32(bytes, 0x318, 2);
        Put32(bytes, 0x31C, 0x1140); Put32(bytes, 0x320, 0x1148);
        Put32(bytes, 0x324, 0x1150); Put32(bytes, 0x340, 0x1000);
        Put32(bytes, 0x344, 0x1010); Put32(bytes, 0x348, 0x1160);
        Put32(bytes, 0x34C, 0x1180); Put16(bytes, 0x350, 0); Put16(bytes, 0x352, 1);
        Encoding.ASCII.GetBytes("HermesPreflight\0").CopyTo(bytes, 0x360);
        Encoding.ASCII.GetBytes("HermesTransactionFence\0").CopyTo(bytes, 0x380);
    }

    internal static void Put16(byte[] bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);

    internal static void Put32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
}
