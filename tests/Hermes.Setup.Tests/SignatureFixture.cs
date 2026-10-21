using System.Collections.Immutable;
using System.Text;
using Hermes.Setup;

namespace Hermes.Setup.Tests;

internal static class SignatureFixture
{
    internal const string Name = "Signature";
    internal const int ImportHeaderByteLimit = 4096;
    internal const string UnknownImportHeader = "unknown";

    // Independent expected native observations, not derived from production schema.
    internal static ImmutableArray<ImmutableArray<string>> Definitions { get; } =
    [
        [Name, "1", "Signature", "11592"],
        [Name, "2", "FileName", "3583"],
        [Name, "3", "MinVersion", "7444"],
        [Name, "4", "MaxVersion", "7444"],
        [Name, "5", "MinSize", "4356"],
        [Name, "6", "MaxSize", "4356"],
        [Name, "7", "MinDate", "4356"],
        [Name, "8", "MaxDate", "4356"],
        [Name, "9", "Languages", "7679"]
    ];

    internal static ImmutableArray<string> Row { get; } =
        ["ExistingInstallDirectory", "foreign.exe", "", "", "", "", "", "", ""];

    internal static ImmutableArray<string> BaseTableKeys(
        string table, ImmutableArray<string> columns) => table switch
        {
            "FeatureComponents" => [columns[0], columns[1]],
            // WiX v6: Upgrade's first five columns are keys, not ActionProperty.
            "Upgrade" => [.. columns.Take(5)],
            _ => [columns[0]]
        };

    internal static void WriteIdt(
        string path, string table,
        ImmutableArray<string> columns, ImmutableArray<string> types,
        ImmutableArray<string> keys, ImmutableArray<ImmutableArray<string>> rows)
    {
        var lines = new[]
        {
            string.Join('\t', columns),
            string.Join('\t', types),
            string.Join('\t', keys.Prepend(table))
        }.Concat(rows.Select(row => string.Join('\t', row)));
        File.WriteAllText(path,
            string.Join("\r\n", lines) + "\r\n", Encoding.ASCII);
    }

    internal static (string Table, string Hex) CaptureImportHeader(
        string table, string root, string filename)
    {
        var schemaTable = UnknownImportHeader;
        try
        {
            schemaTable = PackageAuthoringSchema.Columns.Keys
                .FirstOrDefault(name => name.Length <= 72 && name == table)
                ?? UnknownImportHeader;
            if (schemaTable == UnknownImportHeader)
                return (schemaTable, UnknownImportHeader);
            using var source = new FileStream(Path.Combine(root, filename),
                FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1);
            return (schemaTable, ReadThirdHeaderLineHex(source));
        }
        catch { return (schemaTable, UnknownImportHeader); }
    }

    internal static string ReadThirdHeaderLineHex(Stream source)
    {
        try { return ReadThirdHeaderLine(source); }
        catch { return UnknownImportHeader; }
    }

    // Count LF-delimited physical lines without decoding, normalizing or
    // reading past line three. Retain CR and LF in the captured byte sequence.
    private static string ReadThirdHeaderLine(Stream source)
    {
        var bytes = new byte[ImportHeaderByteLimit];
        var length = 0;
        var line = 1;
        for (var count = 0; count < ImportHeaderByteLimit; count++)
        {
            var value = source.ReadByte();
            if (value < 0)
                return UnknownImportHeader;
            if (line == 3)
                bytes[length++] = (byte)value;
            if (value != '\n')
                continue;
            if (line++ == 3)
                return Convert.ToHexString(bytes.AsSpan(0, length));
        }
        return UnknownImportHeader;
    }

    internal static void WriteImportDiagnostic(
        Action<string> write, string field, string value)
    {
        try { write(field + value); }
        catch
        {
            try { write(field + UnknownImportHeader); }
            catch { /* Optional output must not replace the Import exception. */ }
        }
    }

    internal static AuthoringFixture WithEmpty(AuthoringFixture fixture) =>
        fixture with
        {
            Snapshot = fixture.Snapshot with
            {
                TableNames = fixture.Snapshot.TableNames.Add(Name),
                ColumnDefinitions = fixture.Snapshot.ColumnDefinitions.AddRange(Definitions),
                Tables = fixture.Snapshot.Tables.SetItem(Name, [])
            }
        };

    internal static AuthoringFixture WithDefinitions(
        AuthoringFixture fixture, ImmutableArray<ImmutableArray<string>> definitions) =>
        fixture with
        {
            Snapshot = fixture.Snapshot with
            {
                ColumnDefinitions = fixture.Snapshot.ColumnDefinitions
                    .Where(row => row[0] != Name).Concat(definitions).ToImmutableArray()
            }
        };
}
