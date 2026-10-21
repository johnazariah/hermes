using Hermes.Servicing;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed partial class SetupEvidenceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PinnedWixDefaultsAndActualExplicitSchedulesValidateInEveryMode(int mode)
    {
        var fixture = WixV6SequenceFixture.Apply(AuthoringFixture.Create());
        Assert.Equal(27, fixture.Snapshot.RequireTable("InstallExecuteSequence").Length);
        Assert.Equal(9, fixture.Snapshot.RequireTable("AdminExecuteSequence").Length);
        Assert.Equal(9, fixture.Snapshot.RequireTable("AdvtExecuteSequence").Length);
        Assert.Equal(8, fixture.Snapshot.RequireTable("InstallUISequence").Length);
        Assert.Equal(4, fixture.Snapshot.RequireTable("AdminUISequence").Length);
        Assert.Equal<string>(["InstallAdminPackage", "", "3900"],
            Assert.Single(fixture.Snapshot.RequireTable("AdminExecuteSequence"),
                row => row[0] == "InstallAdminPackage"));
        Assert.IsType<Effect<ValidatedPackageProfile>.Success>(
            fixture.Validate((PackageAuthoringMode)mode));
    }

    [Theory]
    [InlineData("AdminExecuteSequence", "InstallAdminPackage", 0, "AdminPackage")]
    [InlineData("AdminExecuteSequence", "InstallAdminPackage", 0, "ForeignAction")]
    [InlineData("AdminExecuteSequence", "InstallAdminPackage", 1, "1")]
    [InlineData("AdminExecuteSequence", "InstallAdminPackage", 1, "NOT Installed")]
    [InlineData("AdminExecuteSequence", "InstallAdminPackage", 2, "-1")]
    [InlineData("AdminExecuteSequence", "InstallAdminPackage", 2, "0")]
    [InlineData("AdminExecuteSequence", "InstallAdminPackage", 2, "3899")]
    [InlineData("AdminExecuteSequence", "InstallAdminPackage", 2, "3901")]
    [InlineData("AdminExecuteSequence", "InstallAdminPackage", 2, "not-an-integer")]
    [InlineData("AdminExecuteSequence", "InstallAdminPackage", 2, "2147483648")]
    [InlineData("AdminExecuteSequence", "HermesPreflight", 1, "NOT Installed")]
    [InlineData("AdminExecuteSequence", "HermesPreflight", 2, "1501")]
    [InlineData("InstallExecuteSequence", "HermesPreflight", 1, "NOT Installed")]
    [InlineData("InstallExecuteSequence", "HermesPreflight", 2, "1401")]
    [InlineData("InstallExecuteSequence", "InstallInitialize", 2, "1499")]
    [InlineData("InstallExecuteSequence", "HermesTransactionFence", 1, "Installed")]
    [InlineData("InstallExecuteSequence", "HermesTransactionFence", 2, "1499")]
    [InlineData("InstallExecuteSequence", "RemoveExistingProducts", 1, "REMOVE=\"ALL\"")]
    [InlineData("InstallExecuteSequence", "RemoveExistingProducts", 2, "1503")]
    [InlineData("InstallExecuteSequence", "InstallFinalize", 2, "6599")]
    [InlineData("AdvtExecuteSequence", "HermesRejectAdvertisement", 0, "HermesPreflight")]
    [InlineData("AdvtExecuteSequence", "HermesRejectAdvertisement", 1, "Installed")]
    [InlineData("AdvtExecuteSequence", "HermesRejectAdvertisement", 2, "-1")]
    [InlineData("AdvtExecuteSequence", "HermesRejectAdvertisement", 2, "1501")]
    public async Task PrimaryBoundSequenceMutationsRefuseBeforeAnyMutation(
        string table, string action, int column, string value)
    {
        using var world = new RootFixture();
        var fixture = WixV6SequenceFixture.Apply(world.New.Authoring);
        var changed = fixture.Cell(table, SequenceIndex(fixture, table, action), column, value);
        await AssertSequenceBoundaryAsync(world, fixture, changed);
    }

    [Theory]
    [InlineData("InstallExecuteSequence")]
    [InlineData("AdvtExecuteSequence")]
    [InlineData("InstallUISequence")]
    [InlineData("AdminUISequence")]
    public async Task CorrectAdminActionCannotAcquireAnotherSequence(string table)
    {
        using var world = new RootFixture();
        var fixture = WixV6SequenceFixture.Apply(world.New.Authoring);
        await AssertSequenceBoundaryAsync(world, fixture,
            fixture.Append(table, "InstallAdminPackage", "", "3900"));
    }

    [Theory]
    [InlineData("InstallExecuteSequence", "HermesPreflight")]
    [InlineData("InstallExecuteSequence", "HermesTransactionFence")]
    [InlineData("InstallExecuteSequence", "RemoveExistingProducts")]
    [InlineData("InstallExecuteSequence", "InstallFinalize")]
    [InlineData("AdminExecuteSequence", "HermesPreflight")]
    [InlineData("AdvtExecuteSequence", "HermesRejectAdvertisement")]
    public async Task PrimaryBoundRequiredActionsCannotDisappear(string table, string action)
    {
        using var world = new RootFixture();
        var fixture = WixV6SequenceFixture.Apply(world.New.Authoring);
        var rows = fixture.Snapshot.RequireTable(table)
            .RemoveAt(SequenceIndex(fixture, table, action));
        await AssertSequenceBoundaryAsync(world, fixture, fixture.Rows(table, rows));
    }

    [Fact]
    public async Task CorrectAdminActionCannotBeDuplicated()
    {
        using var world = new RootFixture();
        var fixture = WixV6SequenceFixture.Apply(world.New.Authoring);
        await AssertSequenceBoundaryAsync(world, fixture,
            fixture.Append("AdminExecuteSequence", "InstallAdminPackage", "", "3900"),
            SetupEvidenceCause.CandidateValidatorInventory);
    }

    private static int SequenceIndex(AuthoringFixture fixture, string table, string action) =>
        fixture.Snapshot.RequireTable(table).Select((row, index) => (row, index))
            .Single(pair => pair.row[0] == action).index;

    private static async Task AssertSequenceBoundaryAsync(
        RootFixture world, AuthoringFixture baseline, AuthoringFixture changed,
        SetupEvidenceCause expectedCause = SetupEvidenceCause.CandidateValidatorSequences)
    {
        foreach (var mode in Enum.GetValues<PackageAuthoringMode>())
        {
            Assert.IsType<Effect<ValidatedPackageProfile>.Success>(baseline.Validate(mode));
            var failure = Assert.IsType<Effect<ValidatedPackageProfile>.Failure>(
                changed.Validate(mode));
            Assert.Equal(SetupCode.MediaInvalid, failure.Code);
            Assert.Equal(CandidateMediaEvidence.At(expectedCause), failure.Evidence);
        }
        var database = new BoundaryDatabase(world.Database, authoring: changed.Snapshot);
        AssertRefused(await RunBoundaryAsync(world, database), SetupVerb.Install,
            BoundaryEvidence(expectedCause), SetupCode.MediaInvalid);
        AssertNoMutation(world);
    }
}
