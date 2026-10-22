using System.Collections.Immutable;
using Hermes.Servicing;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed partial class SetupEvidenceTests
{
    private const string PropertyTable = "Property";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PropertyCountDenialStillPrecedesNameAndValueInspection(bool extra)
    {
        using var world = new RootFixture();
        var changed = world.New.Authoring.Cell(PropertyTable, 5, 0, "sensitive")
            .Cell(PropertyTable, 8, 1, "sensitive");
        changed = extra
            ? changed.Append(PropertyTable, "sensitive.extra", "sensitive")
            : changed.Rows(PropertyTable, changed.Snapshot.RequireTable(PropertyTable).RemoveAt(3));
        await AssertPropertyRefusedAsync(world, changed, SetupEvidenceCause.CandidatePropertyCount);
    }

    [Theory]
    [InlineData("sensitive")]
    [InlineData("manufacturer")]
    public async Task UnknownPropertyNamesRemainClosedAndPrecedeLaterValues(string name)
    {
        using var world = new RootFixture();
        var changed = world.New.Authoring.Cell(PropertyTable, 5, 0, name)
            .Cell(PropertyTable, 8, 1, "sensitive");
        await AssertPropertyRefusedAsync(world, changed, SetupEvidenceCause.CandidatePropertyName);
    }

    [Theory]
    [InlineData(3, "hermes")]
    [InlineData(4, "0")]
    [InlineData(5, "sensitive")]
    [InlineData(6, "Force")]
    [InlineData(7, "DisableShutdown")]
    public async Task ScalarComparisonsRemainExactAndPrecedeLaterSecureSetDenial(int row, string value)
    {
        using var world = new RootFixture();
        var changed = world.New.Authoring.Cell(PropertyTable, row, 1, value)
            .Cell(PropertyTable, 8, 1, "sensitive");
        await AssertPropertyRefusedAsync(world, changed, SetupEvidenceCause.CandidatePropertyValue);
    }

    [Theory]
    [InlineData(SecureSetChange.Missing)]
    [InlineData(SecureSetChange.Extra)]
    [InlineData(SecureSetChange.Duplicate)]
    [InlineData(SecureSetChange.Case)]
    [InlineData(SecureSetChange.Empty)]
    public async Task SecurePropertySetStillRejectsEveryUnsupportedChange(object mutation)
    {
        using var world = new RootFixture();
        var change = Assert.IsType<SecureSetChange>(mutation);
        var value = ChangedSecureSet(world.New.Authoring, change);
        var changed = world.New.Authoring.Cell(PropertyTable, 8, 1, value);
        await AssertPropertyRefusedAsync(world, changed, SetupEvidenceCause.CandidatePropertySecureSet);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllStillReportsTheFirstRejectedRowInItsOriginalOrder(bool reverse)
    {
        using var world = new RootFixture();
        var changed = world.New.Authoring.Cell(PropertyTable, 5, 1, "sensitive")
            .Cell(PropertyTable, 8, 1, "sensitive");
        if (reverse)
            changed = changed.Rows(PropertyTable,
                changed.Snapshot.RequireTable(PropertyTable).Reverse().ToImmutableArray());
        var cause = reverse
            ? SetupEvidenceCause.CandidatePropertySecureSet
            : SetupEvidenceCause.CandidatePropertyValue;
        await AssertPropertyRefusedAsync(world, changed, cause);
    }

    [Theory]
    [InlineData("0.1.0")]
    [InlineData("0.1.1")]
    public void SyntheticProfilesStillAcceptReorderedRowsAndSecureSets(string version)
    {
        var original = AuthoringFixture.Create();
        var manifest = AggregateResult.Require(original.Validate()).Manifest with
            { Version = Version.Parse(version) };
        var fixture = AuthoringFixture.Create(manifest);
        var names = fixture.Snapshot.RequireTable(PropertyTable)[8][1].Split(';');
        var changed = fixture.Cell(PropertyTable, 8, 1, string.Join(";", names.Reverse()));
        changed = changed.Rows(PropertyTable,
            changed.Snapshot.RequireTable(PropertyTable).Reverse().ToImmutableArray());
        Assert.IsType<Effect<ValidatedPackageProfile>.Success>(changed.Validate());
    }

    private static async Task AssertPropertyRefusedAsync(
        RootFixture world, AuthoringFixture changed, SetupEvidenceCause cause)
    {
        var failure = Assert.IsType<Effect<ValidatedPackageProfile>.Failure>(changed.Validate());
        Assert.Equal(SetupCode.MediaInvalid, failure.Code);
        Assert.Equal(CandidateMediaEvidence.At(cause), failure.Evidence);
        var database = new BoundaryDatabase(world.Database, authoring: changed.Snapshot);
        AssertRefused(await RunBoundaryAsync(world, database), SetupVerb.Install,
            BoundaryEvidence(cause), SetupCode.MediaInvalid);
        AssertNoMutation(world);
    }

    private static string ChangedSecureSet(AuthoringFixture fixture, SecureSetChange change)
    {
        var value = fixture.Snapshot.RequireTable(PropertyTable)[8][1];
        var names = value.Split(';');
        return change switch
        {
            SecureSetChange.Missing => string.Join(";", names.Skip(1)),
            SecureSetChange.Extra => value + ";sensitive",
            SecureSetChange.Duplicate => value + ";" + names[0],
            SecureSetChange.Case => value.ToLowerInvariant(),
            SecureSetChange.Empty => "",
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
    }

    private enum SecureSetChange { Missing, Extra, Duplicate, Case, Empty }
}
