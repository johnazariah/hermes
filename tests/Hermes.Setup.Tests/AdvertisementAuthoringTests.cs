using System.Collections.Immutable;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed class AdvertisementAuthoringTests
{
    private const string Actions = "CustomAction";
    private const string Advertising = "AdvtExecuteSequence";
    private const string RejectAction = "HermesRejectAdvertisement";
    private const string Message =
        "Hermes advertisement is unsupported; use Hermes.Setup.";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ExactBuiltInRefusalIsAcceptedInEveryMode(int mode)
    {
        var fixture = AuthoringFixture.Create();
        Assert.IsType<Effect<ValidatedPackageProfile>.Success>(
            fixture.Validate((PackageAuthoringMode)mode));
        var rows = fixture.Snapshot.RequireTable(Actions);
        Assert.Equal(3, rows.Length);
        Assert.Equal<string>([RejectAction, "19", "", Message],
            Assert.Single(rows, row => row[0] == RejectAction));
        Assert.Equal<string>([RejectAction], fixture.Snapshot.NullCustomActionSources);
        Assert.Equal<string>([RejectAction, "", "1399"],
            Assert.Single(fixture.Snapshot.RequireTable(Advertising)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("18")]
    [InlineData("35")]
    [InlineData("51")]
    [InlineData("65")]
    [InlineData("83")]
    [InlineData("147")]
    [InlineData("275")]
    [InlineData("1043")]
    [InlineData("2067")]
    [InlineData("4115")]
    public void RefusalTypeCannotAcquireDifferentExecutionSemantics(string type) =>
        Refused(AuthoringFixture.Create().Cell(Actions, 2, 1, type));

    [Theory]
    [InlineData(0, "ForeignAction")]
    [InlineData(2, "HermesServicingGuard")]
    [InlineData(2, "HERMES_SERVICING_RUN")]
    [InlineData(3, "")]
    [InlineData(3, "Hermes advertisement is supported.")]
    [InlineData(3, Message + " [HERMES_SERVICING_RUN]")]
    public void RefusalIdentitySourceAndMessageRemainExact(int column, string value) =>
        Refused(AuthoringFixture.Create().Cell(Actions, 2, column, value));

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("Installed")]
    [InlineData("NOT Installed")]
    [InlineData("HERMES_SERVICING_RUN")]
    [InlineData("HERMES_ALLOW_ROLLBACK = 1")]
    public void RefusalCannotBeMadeConditional(string condition) =>
        Refused(AuthoringFixture.Create().Cell(Advertising, 0, 1, condition));

    [Theory]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("1398")]
    [InlineData("1400")]
    [InlineData("1500")]
    [InlineData("1501")]
    [InlineData("6601")]
    public void RefusalCannotBeMovedFromItsPreEffectPosition(string sequence) =>
        Refused(AuthoringFixture.Create().Cell(Advertising, 0, 2, sequence));

    [Theory]
    [InlineData("HermesPreflight")]
    [InlineData("HermesTransactionFence")]
    [InlineData("ForeignAction")]
    public void AdvertisingCannotReplaceTheBuiltInRefusalWithAnotherAction(string action) =>
        Refused(AuthoringFixture.Create().Cell(Advertising, 0, 0, action));

    [Theory]
    [InlineData("InstallExecuteSequence")]
    [InlineData("AdminExecuteSequence")]
    [InlineData("InstallUISequence")]
    [InlineData("AdminUISequence")]
    public void RefusalCannotAcquireAnotherSequence(string table) =>
        Refused(AuthoringFixture.Create().Append(table, RejectAction, "", "1399"));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EachCustomActionRemainsRequired(int index)
    {
        var fixture = AuthoringFixture.Create();
        Refused(fixture.Rows(Actions, fixture.Snapshot.RequireTable(Actions).RemoveAt(index)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EmptySourceWithoutObservedSqlNullFailsInEveryMode(int mode)
    {
        var fixture = AuthoringFixture.Create();
        fixture = fixture with
        {
            Snapshot = fixture.Snapshot with
            {
                NullCustomActionSources = ImmutableHashSet<string>.Empty
            }
        };
        Refused(fixture, (PackageAuthoringMode)mode);
    }

    [Fact]
    public void UnexpectedNullSourceObservationFails()
    {
        var fixture = AuthoringFixture.Create();
        Refused(fixture with
        {
            Snapshot = fixture.Snapshot with
            {
                NullCustomActionSources =
                    fixture.Snapshot.NullCustomActionSources.Add("HermesPreflight")
            }
        });
    }

    private static void Refused(
        AuthoringFixture fixture,
        PackageAuthoringMode mode = PackageAuthoringMode.CandidateSource) =>
        Assert.Equal(SetupCode.MediaInvalid,
            Assert.IsType<Effect<ValidatedPackageProfile>.Failure>(fixture.Validate(mode)).Code);
}
