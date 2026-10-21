using System.Collections.Immutable;
using System.Text;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed class SignatureIdtAuthoringTests
{
    private const string UpgradeTable = "Upgrade";

    // Independent expected bytes: genuine WiX v6 key order, with the
    // scratch fixture's existing column types deliberately unchanged.
    private const string UpgradeHeader =
        "UpgradeCode\tVersionMin\tVersionMax\tLanguage\tAttributes\tRemove\tActionProperty\r\n" +
        "s72\tS0\tS0\tS0\tS0\tS0\tS0\r\n" +
        "Upgrade\tUpgradeCode\tVersionMin\tVersionMax\tLanguage\tAttributes\r\n";

    [Theory]
    [InlineData(UpgradeTable, UpgradeHeader)]
    [InlineData("FeatureComponents",
        "Feature_\tComponent_\r\n" +
        "s72\tS0\r\n" +
        "FeatureComponents\tFeature_\tComponent_\r\n")]
    [InlineData("Property",
        "Property\tValue\r\n" +
        "s72\tS0\r\n" +
        "Property\tProperty\r\n")]
    public void EmptyBaseTableEmissionIsByteExact(string table, string expected)
    {
        Assert.Equal<byte>(Encoding.ASCII.GetBytes(expected),
            EmitBaseTable(table, []));
    }

    [Fact]
    public void PopulatedUpgradeUsesTheSameGenuineFiveKeyHeader()
    {
        var rows = AuthoringFixture.Create().Snapshot.RequireTable(UpgradeTable);
        Assert.Equal(2, rows.Length);
        var actual = EmitBaseTable(UpgradeTable, rows);
        var expectedHeader = Encoding.ASCII.GetBytes(UpgradeHeader);
        Assert.Equal<byte>(expectedHeader, actual.Take(expectedHeader.Length));
        Assert.Equal(3 + rows.Length, actual.Count(value => value == (byte)'\n'));
    }

    private static byte[] EmitBaseTable(
        string table, ImmutableArray<ImmutableArray<string>> rows)
    {
        var columns = PackageAuthoringSchema.Columns[table];
        var types = columns.Select((_, index) => index == 0 ? "s72" : "S0")
            .ToImmutableArray();
        var keys = SignatureFixture.BaseTableKeys(table, columns);
        var root = Directory.CreateTempSubdirectory("Hermes.Signature.Idt.Tests-");
        try
        {
            var path = Path.Combine(root.FullName, "control.idt");
            SignatureFixture.WriteIdt(path, table, columns, types, keys, rows);
            return File.ReadAllBytes(path);
        }
        finally { root.Delete(recursive: true); }
    }
}
