using System.Collections.Immutable;
using System.Runtime.Versioning;

namespace Hermes.Setup;

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsPackageDatabase
{
    private const int MaximumAuthoringBinaryLength = 32 * 1024 * 1024;

    public PackageAuthoringSnapshot ReadAuthoring(string packagePath)
    {
        var package = Read(packagePath);
        var opened = new uint[1];
        Check(OpenDatabase(packagePath, 0, opened));
        using var database = new MsiHandle(opened[0]);
        var names = ReadTableNames(database.Value);
        var definitions = Rows(database.Value, "_Columns",
            ["Table", "Number", "Name", "Type"]);
        RequireColumnDefinitions(definitions);
        var tables = ReadAuthoringTables(database.Value, names, definitions);
        return new(package, names, definitions,
            package.Tables.SetItems(tables), ReadBinaries(database.Value, names))
        {
            NullCustomActionSources = ReadNullCustomActionSources(database.Value, names)
        };
    }

    private static ImmutableHashSet<string> ReadNullCustomActionSources(
        uint database, ImmutableHashSet<string> names)
    {
        if (!names.Contains("CustomAction"))
            return ImmutableHashSet.Create<string>(StringComparer.Ordinal);
        using var view = View(database,
            "SELECT `Action` FROM `CustomAction` WHERE `Source` IS NULL");
        var result = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        while (FetchRecord(view.Value) is { } record)
        {
            using (record)
                SetupIo.Require(result.Add(Text(record.Value, 1)) &&
                    result.Count <= 100_000, SetupCode.MediaInvalid);
        }
        return result.ToImmutable();
    }

    private static ImmutableHashSet<string> ReadTableNames(uint database)
    {
        var rows = Rows(database, "_Tables", ["Name"]);
        SetupIo.Require(rows.All(row => row.Length == 1 && row[0].Length > 0) &&
            rows.Select(row => row[0]).Distinct(StringComparer.Ordinal).Count() ==
                rows.Length, SetupCode.MediaInvalid);
        return rows.Select(row => row[0]).ToImmutableHashSet(StringComparer.Ordinal);
    }

    private static void RequireColumnDefinitions(
        ImmutableArray<ImmutableArray<string>> definitions) =>
        SetupIo.Require(definitions.All(row =>
            row.Length == 4 && row[0].Length > 0 && row[2].Length > 0) &&
            definitions.Select(row => (row[0], row[2])).Distinct().Count() ==
                definitions.Length, SetupCode.MediaInvalid);

    private static ImmutableDictionary<string, ImmutableArray<ImmutableArray<string>>>
        ReadAuthoringTables(
            uint database,
            ImmutableHashSet<string> names,
            ImmutableArray<ImmutableArray<string>> definitions) =>
        PackageAuthoringQueries.Columns
            .Where(pair => names.Contains(pair.Key))
            .ToImmutableDictionary(pair => pair.Key,
                pair => Rows(database, pair.Key,
                    PackageAuthoringQueries.ActualColumns(
                        pair.Key, pair.Value, definitions)),
                StringComparer.Ordinal);

    private static ImmutableDictionary<string, ImmutableArray<byte>> ReadBinaries(
        uint database, ImmutableHashSet<string> names)
    {
        SetupIo.Require(names.Contains("Binary"), SetupCode.MediaInvalid);
        var identifiers = Rows(database, "Binary", ["Name"]);
        SetupIo.Require(identifiers.Length <= 256 &&
            identifiers.All(row => row[0].Length is > 0 and <= 255) &&
            identifiers.Select(row => row[0]).Distinct(StringComparer.Ordinal).Count() ==
                identifiers.Length, SetupCode.MediaInvalid);
        return identifiers.ToImmutableDictionary(row => row[0],
            row => ReadNamedBinary(database, row[0]), StringComparer.Ordinal);
    }

    private static ImmutableArray<byte> ReadNamedBinary(uint database, string name)
    {
        var escaped = name.Replace("'", "''", StringComparison.Ordinal);
        using var view = View(database,
            $"SELECT `Data` FROM `Binary` WHERE `Name` = '{escaped}'");
        using var record = FetchRecord(view.Value)
            ?? throw new SetupIoException(SetupCode.MediaInvalid);
        var length = DataSize(record.Value, 1);
        SetupIo.Require(length is > 0 and <= MaximumAuthoringBinaryLength,
            SetupCode.MediaInvalid);
        using var output = new MemoryStream();
        ReadAuthoringBinary(record.Value, output);
        SetupIo.Require(output.Length == length, SetupCode.MediaInvalid);
        using var extra = FetchRecord(view.Value);
        SetupIo.Require(extra is null, SetupCode.MediaInvalid);
        return output.ToArray().ToImmutableArray();
    }

    private static void ReadAuthoringBinary(uint record, Stream output)
    {
        var buffer = new byte[65_536];
        while (true)
        {
            uint[] length = [(uint)buffer.Length];
            Check(ReadStream(record, 1, buffer, length));
            if (length[0] == 0)
                return;
            output.Write(buffer, 0, checked((int)length[0]));
            SetupIo.Require(output.Length <= MaximumAuthoringBinaryLength,
                SetupCode.MediaInvalid);
        }
    }
}
