using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Hermes.Servicing;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed partial class SetupEvidenceTests
{
    [Theory]
    [InlineData(SetupEvidenceCause.CandidateValidatorPayload, "File", 0, 4, "sensitive")]
    [InlineData(SetupEvidenceCause.CandidatePropertyValue, "Property", 5, 1, "sensitive")]
    [InlineData(SetupEvidenceCause.CandidateValidatorSearch, "RegLocator", 0, 4, "1")]
    [InlineData(SetupEvidenceCause.CandidateValidatorUpgrade, "Upgrade", 0, 4, "257")]
    [InlineData(SetupEvidenceCause.CandidateValidatorActions, "CustomAction", 0, 3, "sensitive")]
    [InlineData(SetupEvidenceCause.CandidateValidatorSequences, "InstallExecuteSequence", 7, 2, "1401")]
    [InlineData(SetupEvidenceCause.CandidateValidatorDirectories, "Directory", 2, 2, "sensitive")]
    [InlineData(SetupEvidenceCause.CandidateValidatorRegistry, "Registry", 0, 4, "sensitive")]
    [InlineData(SetupEvidenceCause.CandidateValidatorComponents, "Component", 0, 3, "256")]
    [InlineData(SetupEvidenceCause.CandidateValidatorFeature, "Feature", 0, 5, "0")]
    [InlineData(SetupEvidenceCause.CandidateValidatorShortcut, "Shortcut", 0, 5, "sensitive")]
    [InlineData(SetupEvidenceCause.CandidateValidatorRemovals, "RemoveFile", 0, 2, "*")]
    public async Task ExistingTopLevelDenialsKeepTheirReportAndIdentifyOnlyTheirCall(
        object sourcePoint, string table, int row, int column, string value)
    {
        using var world = new RootFixture();
        var cause = Assert.IsType<SetupEvidenceCause>(sourcePoint);
        var changed = world.New.Authoring.Cell(table, row, column, value);
        var failure = Assert.IsType<Effect<ValidatedPackageProfile>.Failure>(changed.Validate());
        Assert.Equal(SetupCode.MediaInvalid, failure.Code);
        Assert.Equal(CandidateMediaEvidence.At(cause), failure.Evidence);
        var database = new BoundaryDatabase(world.Database, authoring: changed.Snapshot);
        AssertRefused(await RunBoundaryAsync(world, database), SetupVerb.Install,
            BoundaryEvidence(cause), SetupCode.MediaInvalid);
        AssertNoMutation(world);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{")]
    public async Task ManifestShapeAndExpectedJsonFailuresRemainAtTheManifestCall(string text)
    {
        using var world = new RootFixture();
        var snapshot = world.New.Authoring.Snapshot;
        var bytes = Encoding.UTF8.GetBytes(text).ToImmutableArray();
        var changed = snapshot with
        {
            Package = snapshot.Package with { ManifestBytes = bytes },
            Binaries = snapshot.Binaries.SetItem(PackageMediaReader.BinaryName, bytes)
        };
        var database = new BoundaryDatabase(world.Database, authoring: changed);
        AssertRefused(await RunBoundaryAsync(world, database), SetupVerb.Install,
            BoundaryEvidence(SetupEvidenceCause.CandidateValidatorManifest), SetupCode.MediaInvalid);
        AssertNoMutation(world);
    }

    [Fact]
    public async Task EarlierPropertyDenialStillPrecedesExpectedGuardConversion()
    {
        using var world = new RootFixture();
        var changed = world.New.Authoring.Cell("Property", 5, 1, "sensitive")
            .Guard([0], approveSyntheticImage: false);
        var database = new BoundaryDatabase(world.Database, authoring: changed.Snapshot);
        AssertRefused(await RunBoundaryAsync(world, database), SetupVerb.Install,
            BoundaryEvidence(SetupEvidenceCause.CandidatePropertyValue), SetupCode.MediaInvalid);
        AssertNoMutation(world);
    }

    [Fact]
    public async Task GuardConversionStillPrecedesShortcutAndRemovalDenials()
    {
        using var world = new RootFixture();
        var changed = world.New.Authoring.Cell("Shortcut", 0, 5, "sensitive")
            .Cell("RemoveFile", 0, 2, "*").Guard([0], approveSyntheticImage: false);
        var database = new BoundaryDatabase(world.Database, authoring: changed.Snapshot);
        AssertRefused(await RunBoundaryAsync(world, database), SetupVerb.Install,
            BoundaryEvidence(SetupEvidenceCause.CandidateValidatorGuard), SetupCode.MediaInvalid);
        AssertNoMutation(world);
    }

    [Fact]
    public async Task PayloadIdentityDenialKeepsItsOriginalCodeWithoutCandidateOutput()
    {
        using var world = new RootFixture();
        var changed = world.New.Authoring.Cell("Property", 2, 1, "0.0.0");
        var database = new BoundaryDatabase(world.Database, authoring: changed.Snapshot);
        var result = await RunBoundaryAsync(world, database);
        Assert.Equal(1603, result.ExitCode);
        Assert.Equal("Hermes Setup: MediaIdentityMismatch." + Environment.NewLine, result.Diagnostics);
        Assert.Equal(SetupExecution.Refused(SetupVerb.Install, SetupCode.MediaIdentityMismatch)
            .Report.ToJson() + Environment.NewLine, result.Output);
        AssertNoMutation(world);
    }

    [Fact]
    public void NestedSourceTrackingPreservesExceptionIdentityAndExistingNativeEvidence()
    {
        var point = new CandidateValidatorSourcePoint();
        var untouched = new CandidateValidatorSourcePoint();
        var evidence = new SetupRefusalEvidence(SetupEvidenceStage.Unspecified,
            SetupEvidenceCause.MsiObservation, ObservationFault.NativeQuery,
            StartupEvidenceErrorKind.Win32, 87);
        var error = new SetupIoException(SetupCode.MediaInvalid, evidence);
        var actual = Assert.Throws<SetupIoException>(() =>
            point.Read(SetupEvidenceCause.CandidateValidatorRemainder, () =>
                point.Read<int>(SetupEvidenceCause.CandidateValidatorGuard, () => throw error)));
        Assert.Same(error, actual);
        Assert.Same(evidence, actual.Evidence);
        Assert.Equal(SetupEvidenceCause.CandidateValidatorGuard, point.Cause);
        Assert.Equal(SetupEvidenceCause.CandidateValidatorInventory, untouched.Cause);
    }

    [Fact]
    public void SourcePointChecksDoNotCatchOrConvertExceptions()
    {
        Exception[] errors = [new IOException("sensitive"), new JsonException("sensitive"),
            new BadImageFormatException("sensitive"), new OperationCanceledException("sensitive"),
            new InvalidOperationException("sensitive")];
        foreach (var error in errors)
        {
            var point = new CandidateValidatorSourcePoint();
            var actual = Assert.Throws(error.GetType(), () =>
                point.Check(SetupEvidenceCause.CandidateValidatorPayload, () => throw error));
            Assert.Same(error, actual);
            Assert.Equal(SetupEvidenceCause.CandidateValidatorPayload, point.Cause);
        }
    }

    private static async Task AssertCandidateBoundaryAsync(SetupEvidenceCause cause, string source)
    {
        var evidence = BoundaryEvidence(cause);
        Assert.True(evidence.IsCandidateMedia);
        Assert.InRange(evidence.ToDiagnosticLine().Length, 1,
            SetupRefusalEvidence.MaximumDiagnosticLength);
        Assert.Matches(
            @"\AHERMES_SETUP_EVIDENCE v=1 stage=10 cause=(19|[23][0-9]|4[01]) fault=0 errorKind=0 error=0\z",
            evidence.ToDiagnosticLine());
        var result = await RunAsync(new ThrowingFactory(
            new SetupIoException(SetupCode.MediaInvalid, evidence)), SetupVerb.Install, source);
        AssertRefused(result, SetupVerb.Install, evidence, SetupCode.MediaInvalid);
    }
}
