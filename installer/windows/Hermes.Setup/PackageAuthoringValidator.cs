using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Hermes.Servicing;

namespace Hermes.Setup;

internal enum PackageAuthoringMode
{
    CandidateSource,
    RetainedSource,
    RegisteredCache
}

// Supplied from trusted build/release inputs, never derived from the candidate.
// Multiple compatible guard generations are allowed; no installation history
// or CI receipt is required.
internal sealed record ApprovedGuardProfile(
    int AdmissionAbi,
    string Root,
    ImmutableHashSet<string> Sha256);

internal sealed record GuardImageObservation(
    string Sha256,
    ImmutableArray<string> Exports);

internal sealed record ProfileComponent(
    string Id,
    Guid Code,
    string DirectoryId,
    string RegistryKeyPath,
    PayloadFile? File);

internal sealed record ProfileRegistryValue(
    string Id,
    string ComponentId,
    string Key,
    string Name,
    string Value);

internal sealed record ProfileShortcut(
    string Id,
    string ComponentId,
    string DirectoryId,
    string Name,
    string Target,
    string Arguments,
    string WorkingDirectoryId);

internal sealed record ProfileDirectoryRemoval(
    string Id,
    string ComponentId,
    string DirectoryId);

// An authoring prerequisite, not runtime admission or installation evidence.
// Registry values are HKCU, preserving their authored MSI formatting.
internal sealed record ValidatedPackageProfile(
    PayloadManifest Manifest,
    PackageAuthoringMode Mode,
    GuardImageObservation Guard,
    ImmutableArray<ProfileComponent> Components,
    ImmutableArray<ProfileRegistryValue> Registry,
    ProfileShortcut Shortcut,
    ImmutableArray<ProfileDirectoryRemoval> DirectoryRemovals)
{
    internal bool HasCompleteSource =>
        Mode is PackageAuthoringMode.CandidateSource or PackageAuthoringMode.RetainedSource;

    internal PackageIdentity Identity => new(
        Manifest.UpgradeCode, Manifest.ProductCode, Manifest.PackageCode,
        Manifest.Version, Manifest.AdmissionAbi, PackageScope.PerUser);
}

internal static class PackageAuthoringSchema
{
    internal static ImmutableDictionary<string, ImmutableArray<string>> BaseColumns { get; } =
        new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["Property"] = ["Property", "Value"],
            ["Directory"] = ["Directory", "Directory_Parent", "DefaultDir"],
            ["Component"] =
                ["Component", "ComponentId", "Directory_", "Attributes", "Condition", "KeyPath"],
            ["File"] =
                ["File", "Component_", "FileName", "FileSize",
                 "Version", "Language", "Attributes", "Sequence"],
            ["Media"] =
                ["DiskId", "LastSequence", "DiskPrompt", "Cabinet", "VolumeLabel", "Source"]
        }.ToImmutableDictionary(StringComparer.Ordinal);

    // WiX v6 Signature schema: s72, s255, S20, S20, I4, I4, I4, I4, S255.
    // Pin the complete native type words, including localization, key and
    // nullability flags. Only an observed empty table is supported below.
    internal static ImmutableArray<int> SignatureColumnTypes { get; } =
        [0x2d48, 0x0dff, 0x1d14, 0x1d14,
         0x1104, 0x1104, 0x1104, 0x1104, 0x1dff];

    // Unknown tables are unsupported, even when empty. These additional tables
    // contain metadata, not install-time resource/action authoring.
    internal static ImmutableDictionary<string, ImmutableArray<string>> Columns { get; } =
        BaseColumns.SetItems(PackageAuthoringQueries.Columns)
            .SetItems(new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
            {
                ["Binary"] = ["Name", "Data"],
                ["Icon"] = ["Name", "Data"],
                ["MsiFileHash"] =
                    ["File_", "Options", "HashPart1", "HashPart2", "HashPart3", "HashPart4"],
                ["Error"] = ["Error", "Message"],
                ["_Validation"] =
                    ["Table", "Column", "Nullable", "MinValue", "MaxValue",
                     "KeyTable", "KeyColumn", "Category", "Set", "Description"],
                ["MsiDigitalCertificate"] = ["DigitalCertificate", "CertData"],
                ["MsiDigitalSignature"] =
                    ["Table", "SignObject", "DigitalCertificate_", "Hash"]
            });
}

internal static partial class PackageAuthoringValidator
{
    private const string GuardBinary = "HermesServicingGuard";
    private const string Preflight = "HermesPreflight";
    private const string Fence = "HermesTransactionFence";
    private const string RejectAdvertisement = "HermesRejectAdvertisement";
    private const string AdvertisementMessage =
        "Hermes advertisement is unsupported; use Hermes.Setup.";
    private const string Related = "HERMES_RELATED_PRODUCTS";
    private const string Newer = "HERMES_NEWER_PRODUCT";
    private const string Rollback = "HERMES_ALLOW_ROLLBACK";
    private const string RollbackCondition =
        "NOT HERMES_NEWER_PRODUCT OR HERMES_ALLOW_ROLLBACK = 1";

    internal static Effect<ValidatedPackageProfile> Validate(
        PackageAuthoringSnapshot snapshot,
        PackageAuthoringMode mode,
        ApprovedGuardProfile approvedGuards)
    {
        try
        {
            ValidateInputs(mode, approvedGuards);
            return Effect<ValidatedPackageProfile>.Ok(Build(snapshot, mode, approvedGuards));
        }
        catch (SetupIoException error)
        { return Effect<ValidatedPackageProfile>.Fail(error.Code); }
        catch (Exception error) when (SetupIo.Expected(error))
        { return Effect<ValidatedPackageProfile>.Fail(SetupCode.MediaInvalid); }
    }

    private static ValidatedPackageProfile Build(
        PackageAuthoringSnapshot snapshot,
        PackageAuthoringMode mode,
        ApprovedGuardProfile approvedGuards)
    {
        ValidateInventory(snapshot);
        var manifest = ReadManifest(snapshot);
        ValidatePayload(snapshot, manifest, mode);
        ValidateProperties(snapshot, manifest);
        ValidateSearch(snapshot);
        ValidateUpgrade(snapshot, manifest);
        ValidateActions(snapshot);
        ValidateSequences(snapshot);
        ValidateDirectories(snapshot, manifest);
        var registry = ReadRegistry(snapshot, manifest);
        var components = ReadComponents(snapshot, manifest, registry);
        ValidateFeature(snapshot, components);
        return new(manifest, mode, ReadGuard(snapshot, approvedGuards),
            components, registry, ReadShortcut(snapshot, manifest),
            ReadRemovals(snapshot, manifest));
    }

    private static void ValidateInputs(
        PackageAuthoringMode mode, ApprovedGuardProfile approved)
    {
        SetupIo.Require(mode is PackageAuthoringMode.CandidateSource
            or PackageAuthoringMode.RetainedSource or PackageAuthoringMode.RegisteredCache,
            SetupCode.InvalidEvidence);
        SetupIo.Require(approved.AdmissionAbi == HermesServicingProtocol.AdmissionAbi &&
            approved.Root == PackageMediaReader.RootToken &&
            approved.Sha256.Count > 0 && approved.Sha256.All(hash =>
                hash.Length == 64 &&
                hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')),
            SetupCode.InvalidEvidence);
    }

    private static void Require(bool condition) =>
        SetupIo.Require(condition, SetupCode.MediaInvalid);

    private static int Number(string value) =>
        int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static string RowKey(ImmutableArray<string> row) =>
        string.Concat(row.Select(value =>
            value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value));

    private static void ValidateInventory(PackageAuthoringSnapshot snapshot)
    {
        Require(snapshot.TableNames.Count is > 0 and <= 128 &&
            snapshot.TableNames.All(PackageAuthoringSchema.Columns.ContainsKey));
        Require(!snapshot.ColumnDefinitions.IsDefault &&
            snapshot.ColumnDefinitions.All(row => !row.IsDefault && row.Length == 4 &&
                row.All(value => value is not null) && snapshot.HasTable(row[0])));
        Require(snapshot.ColumnDefinitions.Select(row => (row[0], row[1])).Distinct().Count() ==
            snapshot.ColumnDefinitions.Length);
        Require(snapshot.ColumnDefinitions.Select(row => (row[0], row[2])).Distinct().Count() ==
            snapshot.ColumnDefinitions.Length);
        foreach (var name in snapshot.TableNames)
            ValidateColumns(snapshot, name);
        Require(snapshot.Tables.Keys.All(snapshot.HasTable));
        ValidateObservedRows(snapshot);
        ValidateBaseImage(snapshot);
        Require(snapshot.Binaries.Keys.Order(StringComparer.Ordinal)
            .SequenceEqual(new[] { "HermesPayloadManifest", GuardBinary }.Order(StringComparer.Ordinal)));
        Require(snapshot.RequireBinary(PackageMediaReader.BinaryName)
            .SequenceEqual(snapshot.Package.ManifestBytes));
    }

    private static void ValidateColumns(PackageAuthoringSnapshot snapshot, string table)
    {
        var expected = PackageAuthoringQueries.ActualColumns(
            table, PackageAuthoringSchema.Columns[table], snapshot.ColumnDefinitions);
        var actual = snapshot.ColumnDefinitions.Where(row => row[0] == table)
            .OrderBy(row => Number(row[1])).ToArray();
        Require(actual.Length == expected.Length);
        Require(actual.Select(row => Number(row[1]))
            .SequenceEqual(Enumerable.Range(1, expected.Length)));
        Require(actual.Select(row => row[2]).SequenceEqual(expected));
        // Reject malformed, invalid and temporary column definitions. Resource
        // semantics below are checked independently of SQL key/nullability flags.
        Require(actual.All(row => Number(row[3]) is > 0 and < 0x4000 &&
            (Number(row[3]) & 0x100) != 0));
        if (table == "Signature")
            Require(actual.Select(row => Number(row[3]))
                .SequenceEqual(PackageAuthoringSchema.SignatureColumnTypes));
    }

    private static void ValidateObservedRows(PackageAuthoringSnapshot snapshot)
    {
        var queries = PackageAuthoringSchema.BaseColumns
            .SetItems(PackageAuthoringQueries.Columns);
        Require(snapshot.Tables.Keys.All(queries.ContainsKey));
        foreach (var pair in queries.Where(pair => snapshot.HasTable(pair.Key)))
        {
            var columns = PackageAuthoringQueries.ActualColumns(
                pair.Key, pair.Value, snapshot.ColumnDefinitions);
            var rows = snapshot.RequireTable(pair.Key);
            Require(!rows.IsDefault && rows.Length <= 100_000);
            Require(rows.All(row => !row.IsDefault && row.Length == columns.Length &&
                row.All(value => value is not null) && row[0].Length > 0));
            Require(rows.Select(RowKey).Distinct(StringComparer.Ordinal).Count() == rows.Length);
            Require(rows.Select(row => PrimaryKey(pair.Key, row))
                .Distinct(StringComparer.Ordinal).Count() == rows.Length);
        }
    }

    private static string PrimaryKey(string table, ImmutableArray<string> row) => table switch
    {
        "FeatureComponents" or "CreateFolder" => RowKey(row),
        "Upgrade" => row[6],
        "Condition" => RowKey(row.Take(2).ToImmutableArray()),
        _ => row[0]
    };

    private static void ValidateBaseImage(PackageAuthoringSnapshot snapshot)
    {
        Require(snapshot.Package.Tables.Keys.Order(StringComparer.Ordinal)
            .SequenceEqual(PackageAuthoringSchema.BaseColumns.Keys.Order(StringComparer.Ordinal)));
        foreach (var table in PackageAuthoringSchema.BaseColumns.Keys)
        {
            var observed = snapshot.RequireTable(table);
            var package = snapshot.Package.Table(table);
            Require(!package.IsDefault && package.Length == observed.Length &&
                package.Zip(observed).All(pair => pair.First.SequenceEqual(pair.Second)));
        }
        Require(snapshot.HasTable("Binary"));
    }

    private static PayloadManifest ReadManifest(PackageAuthoringSnapshot snapshot)
    {
        var bytes = snapshot.RequireBinary(PackageMediaReader.BinaryName).ToArray();
        Require(bytes.Length <= 16 * 1024 * 1024);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        RequireObject(root,
            ["schemaVersion", "admissionAbi", "root", "scope", "upgradeCode",
             "productCode", "packageCode", "version", "files"]);
        RequireStrings(root, ["root", "scope", "upgradeCode", "productCode", "packageCode", "version"]);
        Require(root.GetProperty("schemaVersion").ValueKind == JsonValueKind.Number &&
            root.GetProperty("admissionAbi").ValueKind == JsonValueKind.Number &&
            root.GetProperty("files").ValueKind == JsonValueKind.Array);
        foreach (var file in root.GetProperty("files").EnumerateArray())
            ValidateManifestFileShape(file);
        return StrictSetupJson.Read(bytes, SetupStorageJson.Default.PayloadManifest);
    }

    private static void RequireObject(JsonElement value, ImmutableArray<string> names)
    {
        Require(value.ValueKind == JsonValueKind.Object);
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        Require(actual.Length == names.Length &&
            actual.Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal)));
    }

    private static void RequireStrings(JsonElement value, ImmutableArray<string> names) =>
        Require(names.All(name => value.GetProperty(name).ValueKind == JsonValueKind.String));

    private static void ValidateManifestFileShape(JsonElement file)
    {
        RequireObject(file,
            ["path", "sha256", "length", "moduleVersionId", "fileId", "componentId", "componentCode"]);
        RequireStrings(file, ["path", "sha256", "fileId", "componentId", "componentCode"]);
        Require(file.GetProperty("length").ValueKind == JsonValueKind.Number &&
            file.GetProperty("moduleVersionId").ValueKind is
                JsonValueKind.String or JsonValueKind.Null);
    }

    private static void ValidatePayload(
        PackageAuthoringSnapshot snapshot, PayloadManifest manifest, PackageAuthoringMode mode)
    {
        PackageMediaReader.ValidateIdentity(manifest, snapshot.Package);
        PackageMediaReader.ValidateFiles(manifest, snapshot.Package);
        Require(manifest.Version.Major <= 255 && manifest.Version.Minor <= 255 &&
            manifest.Version.Build <= 65535 &&
            manifest.ProductCode != manifest.PackageCode &&
            manifest.ProductCode != manifest.UpgradeCode &&
            manifest.PackageCode != manifest.UpgradeCode);
        if (mode is PackageAuthoringMode.RegisteredCache)
            Require(snapshot.RequireTable("File").All(row =>
                PackageMediaReader.SelfContainedFile(row, snapshot.Package.WordCount)));
        else
            PackageMediaReader.ValidateMedia(snapshot.Package);
        ValidateFileMetadata(snapshot, manifest);
    }

    private static void ValidateFileMetadata(
        PackageAuthoringSnapshot snapshot, PayloadManifest manifest)
    {
        Require(manifest.Files.Any(file => file.Path == "service/Hermes.Service.exe") &&
            manifest.Files.Any(file => file.Path == "tray/Hermes.Tray.exe"));
        foreach (var file in manifest.Files)
        {
            var identity = StableComponent(file.Path);
            Require(file.ComponentId == identity.Id && file.ComponentCode == identity.Code &&
                file.FileId == "File_" + identity.Id);
            var row = snapshot.RequireTable("File").Single(row => row[0] == file.FileId);
            Require(MsiTargetName.Read(row[2]) == file.Path.Split('/')[^1] &&
                NumericParts(row[4], '.') && NumericParts(row[5], ','));
        }
        var sequences = snapshot.RequireTable("File").Select(row => Number(row[7])).ToArray();
        Require(sequences.Distinct().Count() == sequences.Length);
    }

    private static bool NumericParts(string text, char separator) =>
        text.Length == 0 || text.Split(separator) is { Length: > 0 and <= 4 } parts &&
        parts.All(part => part.Length is > 0 and <= 5 &&
            part.All(char.IsAsciiDigit) && Number(part) <= ushort.MaxValue);

    private static void ValidateProperties(
        PackageAuthoringSnapshot snapshot, PayloadManifest manifest)
    {
        var expected = ExpectedProperties(manifest);
        var rows = snapshot.RequireTable("Property");
        Require(rows.Length == expected.Count &&
            rows.All(row => expected.TryGetValue(row[0], out var value) &&
                (row[0] == "SecureCustomProperties"
                    ? SamePropertySet(row[1], value)
                    : string.Equals(row[1], value, row[0] is "ProductCode" or "UpgradeCode"
                        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))));
    }

    private static ImmutableDictionary<string, string> ExpectedProperties(PayloadManifest manifest) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ProductCode"] = manifest.ProductCode.ToString("B"),
            ["UpgradeCode"] = manifest.UpgradeCode.ToString("B"),
            ["ProductVersion"] = manifest.Version.ToString(),
            ["ProductName"] = "Hermes",
            ["ProductLanguage"] = "1033",
            ["Manufacturer"] = "John Azariah",
            ["REBOOT"] = "ReallySuppress",
            ["MSIRESTARTMANAGERCONTROL"] = "Disable",
            ["SecureCustomProperties"] =
                "HERMES_SERVICING_RUN;HERMES_SERVICING_ABI;HERMES_ALLOW_ROLLBACK;" +
                "HERMES_RELATED_PRODUCTS;HERMES_NEWER_PRODUCT;INSTALLFOLDER"
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static bool SamePropertySet(string actual, string expected)
    {
        var names = actual.Split(';');
        return names.Length == names.Distinct(StringComparer.Ordinal).Count() &&
            names.Order(StringComparer.Ordinal)
                .SequenceEqual(expected.Split(';').Order(StringComparer.Ordinal));
    }

    private static void ValidateSearch(PackageAuthoringSnapshot snapshot)
    {
        Require(snapshot.OptionalTable("Signature").IsEmpty);
        var search = snapshot.RequireTable("AppSearch");
        var locator = snapshot.RequireTable("RegLocator");
        Require(search.Length == 1 &&
            search[0].SequenceEqual(["INSTALLFOLDER", "ExistingInstallDirectory"]));
        Require(locator.Length == 1 &&
            locator[0][0] == "ExistingInstallDirectory" && locator[0][1] == "1" &&
            locator[0][2] == InstallerKey && locator[0][3] == "InstallDirectory" &&
            locator[0][4] is "2" or "18");
        // Raw value, 32- or 64-bit view. This HKCU key is not an authority;
        // the independently approved native guards enforce the resolved root.
    }

    private static void ValidateUpgrade(
        PackageAuthoringSnapshot snapshot, PayloadManifest manifest)
    {
        var rows = snapshot.RequireTable("Upgrade");
        Require(rows.Length == 2);
        Require(rows.All(row => Guid.Parse(row[0]) == manifest.UpgradeCode &&
            row[2] == "" && row[3] == "" && row[5] is "" or "ALL"));
        Require(rows.Count(row => row[1] == "0.0.0" &&
            row[4] == "256" && row[6] == Related) == 1);
        Require(rows.Count(row => row[1] == manifest.Version.ToString() &&
            row[4] == "2" && row[6] == Newer) == 1);
        var launch = snapshot.RequireTable("LaunchCondition");
        Require(launch.Length == 1 && launch[0][0] == RollbackCondition &&
            launch[0][1] ==
                "A newer Hermes version is installed. Explicit rollback requires HERMES_ALLOW_ROLLBACK=1.");
    }

    private static void ValidateActions(PackageAuthoringSnapshot snapshot)
    {
        var rows = snapshot.RequireTable("CustomAction");
        Require(rows.Length == 3);
        foreach (var action in new[] { Preflight, Fence })
            Require(rows.Count(row => row[0] == action && row[1] == "1" &&
                row[2] == GuardBinary && row[3] == action &&
                (row.Length == 4 || row[4] is "" or "0")) == 1);
        Require(rows.Count(row => row[0] == RejectAdvertisement &&
            row[1] == "19" && row[2] == "" && row[3] == AdvertisementMessage &&
            (row.Length == 4 || row[4] is "" or "0")) == 1);
        Require(snapshot.NullCustomActionSources.SetEquals([RejectAdvertisement]));
    }

    private static GuardImageObservation ReadGuard(
        PackageAuthoringSnapshot snapshot, ApprovedGuardProfile approved)
    {
        var bytes = snapshot.RequireBinary(GuardBinary);
        Require(bytes.Length <= 32 * 1024 * 1024);
        var observation = GuardImageInspection.Read(bytes);
        Require(approved.Sha256.Contains(observation.Sha256));
        return observation;
    }

    private static ImmutableDictionary<string, int> ActionPositions { get; } =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["FindRelatedProducts"] = 25, ["AppSearch"] = 50, ["LaunchConditions"] = 100,
            ["ValidateProductID"] = 700, ["CostInitialize"] = 800, ["FileCost"] = 900,
            ["CostFinalize"] = 1000, ["ExecuteAction"] = 1300,
            [RejectAdvertisement] = 1399,
            [Preflight] = 1399, ["InstallValidate"] = 1400,
            ["InstallInitialize"] = 1500, [Fence] = 1501, ["RemoveExistingProducts"] = 1502,
            ["ProcessComponents"] = 1600, ["UnpublishFeatures"] = 1800,
            ["RemoveRegistryValues"] = 2600, ["RemoveShortcuts"] = 3200,
            ["RemoveFiles"] = 3500, ["RemoveFolders"] = 3600, ["CreateFolders"] = 3700,
            ["AdminPackage"] = 3900, ["InstallFiles"] = 4000, ["CreateShortcuts"] = 4500,
            ["WriteRegistryValues"] = 5000, ["RegisterUser"] = 6000,
            ["RegisterProduct"] = 6100, ["PublishFeatures"] = 6300,
            ["PublishProduct"] = 6400, ["InstallFinalize"] = 6600
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static ImmutableArray<string> ReadOnlyActions { get; } =
        ["FindRelatedProducts", "AppSearch", "LaunchConditions", "ValidateProductID",
         "CostInitialize", "FileCost", "CostFinalize"];

    private static ImmutableArray<string> InstallActions { get; } =
        [Preflight, "InstallValidate", "InstallInitialize", Fence, "RemoveExistingProducts",
         "ProcessComponents", "UnpublishFeatures", "RemoveRegistryValues", "RemoveShortcuts",
         "RemoveFiles", "RemoveFolders", "InstallFiles", "CreateShortcuts", "WriteRegistryValues",
         "RegisterUser", "RegisterProduct", "PublishFeatures", "PublishProduct", "InstallFinalize"];

    private static void ValidateSequences(PackageAuthoringSnapshot snapshot)
    {
        ValidateSequence(snapshot, "InstallExecuteSequence",
            ReadOnlyActions.AddRange(InstallActions).Add("CreateFolders"));
        RequireActions(snapshot, "InstallExecuteSequence",
            InstallActions.AddRange(ImmutableArray.Create("FindRelatedProducts", "AppSearch", "LaunchConditions",
                "CostInitialize", "FileCost", "CostFinalize")));
        ValidateSequence(snapshot, "AdminExecuteSequence",
            ReadOnlyActions.AddRange(ImmutableArray.Create(Preflight, "InstallValidate", "InstallInitialize",
                "AdminPackage", "InstallFiles", "InstallFinalize")));
        ValidateSequence(snapshot, "AdvtExecuteSequence",
            ReadOnlyActions.AddRange(ImmutableArray.Create(RejectAdvertisement,
                "InstallValidate", "InstallInitialize", "CreateShortcuts",
                "PublishFeatures", "PublishProduct", "InstallFinalize")));
        RequireActions(snapshot, "AdminExecuteSequence", [Preflight]);
        RequireActions(snapshot, "AdvtExecuteSequence", [RejectAdvertisement]);
        ValidateSequence(snapshot, "InstallUISequence", ReadOnlyActions.Add("ExecuteAction"));
        ValidateSequence(snapshot, "AdminUISequence", ReadOnlyActions.Add("ExecuteAction"));
    }

    private static void ValidateSequence(
        PackageAuthoringSnapshot snapshot, string table, ImmutableArray<string> allowed)
    {
        var rows = snapshot.OptionalTable(table);
        Require(rows.All(row => allowed.Contains(row[0], StringComparer.Ordinal) &&
            ActionPositions.TryGetValue(row[0], out var sequence) &&
            Number(row[2]) == sequence && ValidSequenceCondition(row)));
        Require(rows.Select(row => Number(row[2])).Distinct().Count() == rows.Length);
    }

    private static bool ValidSequenceCondition(ImmutableArray<string> row) => row[0] switch
    {
        "RemoveExistingProducts" => row[1] == Related,
        "RegisterUser" => row[1] is "" or "NOT Installed",
        _ => row[1] == ""
    };

    private static void RequireActions(
        PackageAuthoringSnapshot snapshot, string table, ImmutableArray<string> actions)
    {
        var rows = snapshot.RequireTable(table);
        Require(actions.All(action => rows.Count(row => row[0] == action) == 1));
    }
}
