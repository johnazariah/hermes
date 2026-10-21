using Hermes.Servicing;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed partial class SetupEvidenceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(32 * 1024 * 1024 + 1)]
    public async Task GuardInputBoundsStillPrecedeImageInspection(int length)
    {
        using var world = new RootFixture();
        var changed = world.New.Authoring.Guard(new byte[length], approveSyntheticImage: false);
        await AssertGuardRefusedAsync(world, changed, SetupEvidenceCause.CandidateValidatorGuard);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedGuardImageIsNotAdmittedByApprovedHash(bool approveSyntheticImage)
    {
        using var world = new RootFixture();
        var changed = world.New.Authoring.Guard([0], approveSyntheticImage);
        Assert.Throws<BadImageFormatException>(() =>
            GuardImageInspection.Read(changed.Snapshot.RequireBinary("HermesServicingGuard")));
        await AssertGuardRefusedAsync(world, changed, SetupEvidenceCause.CandidateGuardInspection);
    }

    [Theory]
    [InlineData(0x314, 3u)]
    [InlineData(0x318, 3u)]
    [InlineData(0x340, 0x1100u)]
    public async Task StrictExportInspectionStillRejectsHashApprovedSyntheticImages(
        int offset, uint value)
    {
        using var world = new RootFixture();
        var bytes = AuthoringFixture.NativeImage();
        AuthoringFixture.Put32(bytes, offset, value);
        var changed = world.New.Authoring.Guard(bytes, approveSyntheticImage: true);
        Assert.Contains(PackageMediaReader.Hash(bytes), changed.Approved.Sha256, changed.Approved.Sha256.KeyComparer);
        await AssertGuardRefusedAsync(world, changed, SetupEvidenceCause.CandidateGuardInspection);
    }

    [Fact]
    public async Task GuardApprovedHashDenialStillPrecedesLaterResourceDenials()
    {
        using var world = new RootFixture(badGuard: true);
        var changed = world.New.Authoring.Cell("Shortcut", 0, 5, "sensitive")
            .Cell("RemoveFile", 0, 2, "*");
        var observation = GuardImageInspection.Read(
            changed.Snapshot.RequireBinary("HermesServicingGuard"));
        Assert.DoesNotContain(observation.Sha256, changed.Approved.Sha256, changed.Approved.Sha256.KeyComparer);
        await AssertGuardRefusedAsync(world, changed, SetupEvidenceCause.CandidateGuardApprovedHash);
    }

    [Theory]
    [InlineData(PackageAuthoringMode.CandidateSource)]
    [InlineData(PackageAuthoringMode.RetainedSource)]
    [InlineData(PackageAuthoringMode.RegisteredCache)]
    public void ApprovedSyntheticGuardStillAcceptsInEveryExistingMode(object sourceMode)
    {
        var mode = Assert.IsType<PackageAuthoringMode>(sourceMode);
        var fixture = AuthoringFixture.Create();
        var profile = Assert.IsType<Effect<ValidatedPackageProfile>.Success>(
            fixture.Validate(mode)).Value;
        Assert.Equal(mode, profile.Mode);
        Assert.Equal(fixture.Approved.Sha256.Single(), profile.Guard.Sha256);
        Assert.Equal(new[] { "HermesPreflight", "HermesTransactionFence" },
            profile.Guard.Exports.ToArray());
    }

    [Theory]
    [InlineData(SetupEvidenceCause.CandidateGuardInspection)]
    [InlineData(SetupEvidenceCause.CandidateGuardApprovedHash)]
    public void GuardSourcePointsDoNotCatchOrReplaceExceptions(object sourcePoint)
    {
        var cause = Assert.IsType<SetupEvidenceCause>(sourcePoint);
        var evidence = new SetupRefusalEvidence(SetupEvidenceStage.Unspecified,
            SetupEvidenceCause.MsiObservation, ObservationFault.NativeQuery,
            StartupEvidenceErrorKind.Win32, 87);
        Exception[] errors = [new SetupIoException(SetupCode.MediaInvalid, evidence),
            new BadImageFormatException("sensitive"), new OverflowException("sensitive"),
            new ArgumentOutOfRangeException("sensitive"),
            new InvalidOperationException("sensitive"), new OperationCanceledException("sensitive")];
        foreach (var error in errors)
        {
            var point = new CandidateValidatorSourcePoint();
            var actual = Assert.Throws(error.GetType(), () =>
                point.Read(SetupEvidenceCause.CandidateValidatorGuard, () =>
                    point.Read<int>(cause, () => throw error)));
            Assert.Same(error, actual);
            Assert.Equal(cause, point.Cause);
        }
        Assert.Same(evidence, Assert.IsType<SetupIoException>(errors[0]).Evidence);
    }

    private static async Task AssertGuardRefusedAsync(
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
}
