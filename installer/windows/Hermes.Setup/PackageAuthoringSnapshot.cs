using System.Collections.Immutable;

namespace Hermes.Setup;

// This is an observation, not authorization or validated PackageEvidence.
// TableNames comes from a successful _Tables query. Optional-table absence is
// represented only when that query proves the table is absent.
internal sealed record PackageAuthoringSnapshot(
    PackageDatabaseImage Package,
    ImmutableHashSet<string> TableNames,
    ImmutableArray<ImmutableArray<string>> ColumnDefinitions,
    ImmutableDictionary<string, ImmutableArray<ImmutableArray<string>>> Tables,
    ImmutableDictionary<string, ImmutableArray<byte>> Binaries)
{
    internal ImmutableHashSet<string> NullCustomActionSources { get; init; } =
        ImmutableHashSet.Create<string>(StringComparer.Ordinal);

    internal bool HasTable(string name) => TableNames.Contains(name);

    internal ImmutableArray<ImmutableArray<string>> RequireTable(string name) =>
        HasTable(name) && Tables.TryGetValue(name, out var rows)
            ? rows
            : throw new SetupIoException(SetupCode.MediaInvalid);

    internal ImmutableArray<ImmutableArray<string>> OptionalTable(string name)
    {
        if (!HasTable(name))
            return [];
        return RequireTable(name);
    }

    internal ImmutableArray<byte> RequireBinary(string name) =>
        Binaries.TryGetValue(name, out var bytes) && !bytes.IsDefaultOrEmpty
            ? bytes
            : throw new SetupIoException(SetupCode.MediaInvalid);
}

internal interface IPackageAuthoringDatabase
{
    PackageAuthoringSnapshot ReadAuthoring(string packagePath);
}

internal static class PackageAuthoringQueries
{
    // Every listed table is read if present. A present table whose query fails
    // is never represented as absent or empty.
    internal static ImmutableDictionary<string, ImmutableArray<string>> Columns { get; } =
        new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["CustomAction"] = ["Action", "Type", "Source", "Target"],
            ["InstallExecuteSequence"] = ["Action", "Condition", "Sequence"],
            ["AdminExecuteSequence"] = ["Action", "Condition", "Sequence"],
            ["AdvtExecuteSequence"] = ["Action", "Condition", "Sequence"],
            ["InstallUISequence"] = ["Action", "Condition", "Sequence"],
            ["AdminUISequence"] = ["Action", "Condition", "Sequence"],
            ["Upgrade"] =
                ["UpgradeCode", "VersionMin", "VersionMax", "Language",
                 "Attributes", "Remove", "ActionProperty"],
            ["Feature"] =
                ["Feature", "Feature_Parent", "Title", "Description",
                 "Display", "Level", "Directory_", "Attributes"],
            ["FeatureComponents"] = ["Feature_", "Component_"],
            ["Condition"] = ["Feature_", "Level", "Condition"],
            ["Registry"] =
                ["Registry", "Root", "Key", "Name", "Value", "Component_"],
            ["RemoveRegistry"] =
                ["RemoveRegistry", "Root", "Key", "Name", "Component_"],
            ["Shortcut"] =
                ["Shortcut", "Directory_", "Name", "Component_", "Target",
                 "Arguments", "Description", "Hotkey", "Icon_", "IconIndex",
                 "ShowCmd", "WkDir"],
            ["CreateFolder"] = ["Directory_", "Component_"],
            ["RemoveFile"] =
                ["FileKey", "Component_", "FileName", "DirProperty", "InstallMode"],
            ["LaunchCondition"] = ["Condition", "Description"],
            ["Icon"] = ["Name"],
            ["AppSearch"] = ["Property", "Signature_"],
            ["RegLocator"] = ["Signature_", "Root", "Key", "Name", "Type"]
        }.ToImmutableDictionary(StringComparer.Ordinal);

    internal static ImmutableArray<string> ActualColumns(
        string table,
        ImmutableArray<string> requested,
        ImmutableArray<ImmutableArray<string>> definitions)
    {
        ImmutableArray<string> optional = table switch
        {
            "CustomAction" => ["ExtendedType"],
            "Shortcut" =>
                ["DisplayResourceDLL", "DisplayResourceId",
                 "DescriptionResourceDLL", "DescriptionResourceId"],
            _ => []
        };
        return requested.AddRange(optional.Where(column =>
            definitions.Any(row => row[0] == table && row[2] == column)));
    }
}
