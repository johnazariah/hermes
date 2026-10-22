using System.Collections.Immutable;
using System.Globalization;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed class SignatureAuthoringTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AbsentAndCorrectlyEmptySignaturePreserveTheProfile(int value)
    {
        var fixture = AuthoringFixture.Create();
        var mode = (PackageAuthoringMode)value;
        Assert.False(fixture.Snapshot.HasTable(SignatureFixture.Name));
        var bare = AggregateResult.Require(fixture.Validate(mode));
        var empty = AggregateResult.Require(
            SignatureFixture.WithEmpty(fixture).Validate(mode));
        Assert.Equal(bare.Identity, empty.Identity);
        Assert.Equal<ProfileComponent>(bare.Components, empty.Components);
        Assert.Equal<ProfileRegistryValue>(bare.Registry, empty.Registry);
        Assert.Equal(bare.Shortcut, empty.Shortcut);
        Assert.Equal(bare.HasCompleteSource, empty.HasCompleteSource);
    }

    [Fact]
    public void TheReaderRequestsEveryStandardSignatureColumn()
    {
        var names = SignatureFixture.Definitions.Select(row => row[2]);
        Assert.Equal<string>(names,
            PackageAuthoringQueries.Columns[SignatureFixture.Name]);
        Assert.Equal<string>(names,
            PackageAuthoringSchema.Columns[SignatureFixture.Name]);
    }

    [Fact]
    public void EverySignatureRowFailsIncludingTheExistingRegistrySearchKey()
    {
        var fixture = SignatureFixture.WithEmpty(AuthoringFixture.Create());
        string[] keys = ["ExistingInstallDirectory", "UnreferencedSignature"];
        foreach (var key in keys)
            Refused(fixture.Rows(SignatureFixture.Name,
                [SignatureFixture.Row.SetItem(0, key)]));
    }

    [Theory]
    [InlineData(0x0001)]
    [InlineData(0x0100)]
    [InlineData(0x0200)]
    [InlineData(0x0400)]
    [InlineData(0x0800)]
    [InlineData(0x1000)]
    [InlineData(0x2000)]
    [InlineData(0x4000)]
    [InlineData(0x8000)]
    public void NativeTypeLengthAndFlagsArePinnedAtEveryOrdinal(int bit)
    {
        var fixture = SignatureFixture.WithEmpty(AuthoringFixture.Create());
        foreach (var pair in SignatureFixture.Definitions
            .Select((row, index) => (Row: row, Index: index)))
        {
            var type = int.Parse(pair.Row[3], CultureInfo.InvariantCulture) ^ bit;
            var changed = pair.Row.SetItem(3, type.ToString(CultureInfo.InvariantCulture));
            Refused(SignatureFixture.WithDefinitions(fixture,
                SignatureFixture.Definitions.SetItem(pair.Index, changed)));
        }
    }

    [Theory]
    [InlineData(0, "ForeignTable")]
    [InlineData(1, "0")]
    [InlineData(1, "10")]
    [InlineData(1, "invalid")]
    [InlineData(2, "ForeignColumn")]
    [InlineData(3, "0")]
    [InlineData(3, "-1")]
    [InlineData(3, "32768")]
    [InlineData(3, "invalid")]
    public void MalformedColumnMetadataFailsAtEveryOrdinal(int cell, string value)
    {
        var fixture = SignatureFixture.WithEmpty(AuthoringFixture.Create());
        foreach (var pair in SignatureFixture.Definitions
            .Select((row, index) => (Row: row, Index: index)))
            Refused(SignatureFixture.WithDefinitions(fixture,
                SignatureFixture.Definitions.SetItem(
                    pair.Index, pair.Row.SetItem(cell, value))));
    }

    [Fact]
    public void MissingExtraAndDuplicateSignatureColumnsFail()
    {
        var fixture = SignatureFixture.WithEmpty(AuthoringFixture.Create());
        var definitions = SignatureFixture.Definitions;
        foreach (var index in Enumerable.Range(0, definitions.Length))
            Refused(SignatureFixture.WithDefinitions(fixture, definitions.RemoveAt(index)));
        Refused(SignatureFixture.WithDefinitions(fixture, definitions.Add(
            ImmutableArray.Create(SignatureFixture.Name, "10", "ForeignColumn", "7423"))));
        Refused(SignatureFixture.WithDefinitions(fixture, definitions.Add(definitions[0])));
        Refused(SignatureFixture.WithDefinitions(fixture, definitions.SetItem(0,
            definitions[0].SetItem(1, "2")).SetItem(1, definitions[1].SetItem(1, "1"))));
    }

    [Fact]
    public void MissingOrMalformedObservationsAreNotSuccessfulEmptyQueries()
    {
        var fixture = SignatureFixture.WithEmpty(AuthoringFixture.Create());
        Refused(fixture with { Snapshot = fixture.Snapshot with
            { Tables = fixture.Snapshot.Tables.Remove(SignatureFixture.Name) } });
        Refused(SignatureFixture.WithDefinitions(fixture, []));
        Refused(fixture with { Snapshot = fixture.Snapshot with
            { TableNames = fixture.Snapshot.TableNames.Remove(SignatureFixture.Name) } });
        Refused(fixture.Rows(SignatureFixture.Name, default));
        Refused(fixture.Rows(SignatureFixture.Name, [default]));
        Refused(fixture.Rows(SignatureFixture.Name, [ImmutableArray<string>.Empty]));
        Refused(fixture.Rows(SignatureFixture.Name, [SignatureFixture.Row.Add("extra")]));
    }

    [Theory]
    [InlineData("AppSearch", 0, "ALLUSERS")]
    [InlineData("AppSearch", 1, "OtherSignature")]
    [InlineData("RegLocator", 0, "OtherSignature")]
    [InlineData("RegLocator", 1, "2")]
    [InlineData("RegLocator", 2, @"Software\Foreign")]
    [InlineData("RegLocator", 3, "OtherValue")]
    [InlineData("RegLocator", 4, "0")]
    [InlineData("RegLocator", 4, "1")]
    [InlineData("RegLocator", 4, "3")]
    [InlineData("RegLocator", 4, "16")]
    [InlineData("RegLocator", 4, "17")]
    [InlineData("RegLocator", 4, "19")]
    public void EmptySignatureDoesNotRelaxTheFixedRawHkcuSearch(
        string table, int column, string value) =>
        Refused(SignatureFixture.WithEmpty(AuthoringFixture.Create())
            .Cell(table, 0, column, value));

    [Theory]
    [InlineData("2")]
    [InlineData("18")]
    public void BothPreviouslySupportedRawRegistryViewsRemainValid(string type)
    {
        var fixture = SignatureFixture.WithEmpty(AuthoringFixture.Create())
            .Cell("RegLocator", 0, 4, type);
        foreach (var mode in Enum.GetValues<PackageAuthoringMode>())
            Assert.IsType<Effect<ValidatedPackageProfile>.Success>(fixture.Validate(mode));
    }

    [Theory]
    [InlineData("AppSearch")]
    [InlineData("RegLocator")]
    public void FixedSearchCardinalityRemainsExact(string table)
    {
        var fixture = SignatureFixture.WithEmpty(AuthoringFixture.Create());
        Refused(fixture.Rows(table, []));
        Refused(fixture.Rows(table,
            fixture.Snapshot.RequireTable(table).Add(fixture.Snapshot.RequireTable(table)[0])));
    }

    [Theory]
    [InlineData("CompLocator")]
    [InlineData("DrLocator")]
    [InlineData("IniLocator")]
    [InlineData("ForeignTable")]
    public void OtherUnknownTablesRemainUnsupportedEvenWhenEmpty(string table)
    {
        var fixture = SignatureFixture.WithEmpty(AuthoringFixture.Create());
        Refused(fixture with { Snapshot = fixture.Snapshot with
            { TableNames = fixture.Snapshot.TableNames.Add(table) } });
    }

    private static void Refused(AuthoringFixture fixture)
    {
        foreach (var mode in Enum.GetValues<PackageAuthoringMode>())
            Assert.Equal(SetupCode.MediaInvalid,
                Assert.IsType<Effect<ValidatedPackageProfile>.Failure>(
                    fixture.Validate(mode)).Code);
    }
}
