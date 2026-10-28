using System.Collections.Immutable;
using System.Globalization;
using System.Xml.Linq;
using WixToolset.Data.Symbols;
using WixToolset.Data.WindowsInstaller;

namespace Hermes.Setup.Tests;

// Primary-bound reconstruction, not actual binder execution or install evidence.
// Required membership: WiX v6.0.0 SequenceActionsCommand.GetRequiredActionIds
// and GetActionsForSymbolType: Package, AppSearch, Component, File,
// LaunchCondition, Registry, RemoveFile, Shortcut and Upgrade.
// Source SHA256:
// b1bfe23fa9dddd62dcc75d9c66cd518688f01c25f82ee75364997b88fa1519a7
// Standard names/conditions/positions come from the exact pinned WiX package,
// independently of PackageAuthoringValidator. Explicit schedules come from
// the actual Package.wxs, not AuthoringFixture or the validator's policy.
internal static class WixV6SequenceFixture
{
    private static XNamespace Wix { get; } =
        "http://wixtoolset.org/schemas/v4/wxs";

    private static ImmutableDictionary<SequenceTable, ImmutableArray<string>> Required { get; } =
        new Dictionary<SequenceTable, ImmutableArray<string>>
        {
            [SequenceTable.AdminExecuteSequence] =
                ["CostFinalize", "CostInitialize", "FileCost", "InstallAdminPackage",
                 "InstallFiles", "InstallFinalize", "InstallInitialize", "InstallValidate"],
            [SequenceTable.AdminUISequence] =
                ["CostFinalize", "CostInitialize", "ExecuteAction", "FileCost"],
            [SequenceTable.AdvertiseExecuteSequence] =
                ["CostFinalize", "CostInitialize", "InstallInitialize", "InstallFinalize",
                 "InstallValidate", "PublishFeatures", "PublishProduct", "CreateShortcuts"],
            [SequenceTable.InstallExecuteSequence] =
                ["CostFinalize", "CostInitialize", "FileCost", "InstallFinalize",
                 "InstallInitialize", "InstallValidate", "ProcessComponents",
                 "PublishFeatures", "PublishProduct", "RegisterProduct", "RegisterUser",
                 "UnpublishFeatures", "ValidateProductID", "AppSearch", "CreateFolders",
                 "RemoveFolders", "InstallFiles", "RemoveFiles", "LaunchConditions",
                 "WriteRegistryValues", "RemoveRegistryValues", "CreateShortcuts",
                 "RemoveShortcuts", "FindRelatedProducts"],
            [SequenceTable.InstallUISequence] =
                ["CostFinalize", "CostInitialize", "ExecuteAction", "FileCost",
                 "ValidateProductID", "AppSearch", "LaunchConditions", "FindRelatedProducts"]
        }.ToImmutableDictionary();

    internal static AuthoringFixture Apply(AuthoringFixture fixture)
    {
        var package = ReadPackage();
        return Required.Aggregate(fixture, (current, entry) =>
            current.Rows(TableName(entry.Key), Rows(entry.Key, entry.Value, package)));
    }

    private static XElement ReadPackage()
    {
        using var stream = typeof(WixV6SequenceFixture).Assembly.GetManifestResourceStream(
            "Hermes.Setup.Tests.Package.wxs")
            ?? throw new InvalidOperationException("Package authoring resource is absent.");
        return XDocument.Load(stream).Root?.Element(Wix + "Package")
            ?? throw new InvalidOperationException("Package authoring element is absent.");
    }

    private static ImmutableArray<ImmutableArray<string>> Rows(
        SequenceTable table, ImmutableArray<string> required, XElement package)
    {
        var standards = required.Select(action => Standard(table, action));
        var authored = package.Element(Wix + table.ToString())?.Elements()
            .Select(Authored) ?? Enumerable.Empty<ImmutableArray<string>>();
        return standards.Concat(authored)
            .GroupBy(row => row[0], StringComparer.Ordinal)
            .Select(group => group.Last()).ToImmutableArray();
    }

    private static ImmutableArray<string> Standard(SequenceTable table, string action)
    {
        var symbol = WindowsInstallerStandard.StandardActions()
            .Single(value => value.SequenceTable == table && value.Action == action);
        var position = symbol.Sequence is { } sequence
            ? sequence.ToString(CultureInfo.InvariantCulture)
            : throw new InvalidOperationException("Required standard has no default position.");
        return [symbol.Action, symbol.Condition ?? "", position];
    }

    private static ImmutableArray<string> Authored(XElement element)
    {
        var action = element.Name.LocalName == "Custom"
            ? (string?)element.Attribute("Action") : element.Name.LocalName;
        return [
            action ?? throw new InvalidOperationException("Authored action is absent."),
            (string?)element.Attribute("Condition") ?? "",
            (string?)element.Attribute("Sequence")
                ?? throw new InvalidOperationException("Expected an absolute authored schedule.")
        ];
    }

    private static string TableName(SequenceTable table) => table switch
    {
        SequenceTable.AdvertiseExecuteSequence => "AdvtExecuteSequence",
        _ => table.ToString()
    };
}
