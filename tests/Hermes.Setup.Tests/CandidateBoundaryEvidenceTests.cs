using System.Collections.Immutable;
using System.ComponentModel;
using Hermes.Servicing;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed partial class SetupEvidenceTests
{
    [Fact]
    public async Task CandidateBoundariesHaveOnlyFiveFixedBoundedNumericalDiscriminators()
    {
        using var world = new RootFixture();
        var source = BoundaryPackagePath(world);
        var causes = Enum.GetValues<SetupEvidenceCause>().Where(cause => (int)cause >= 19).ToArray();
        Assert.Equal(Enumerable.Range(19, 5), causes.Select(cause => (int)cause));
        foreach (var cause in causes)
        {
            var evidence = BoundaryEvidence(cause);
            Assert.True(evidence.IsCandidateMedia);
            Assert.InRange(evidence.ToDiagnosticLine().Length, 1,
                SetupRefusalEvidence.MaximumDiagnosticLength);
            Assert.Matches(
                @"\AHERMES_SETUP_EVIDENCE v=1 stage=10 cause=(19|2[0-3]) fault=0 errorKind=0 error=0\z",
                evidence.ToDiagnosticLine());
            var result = await RunAsync(new ThrowingFactory(
                new SetupIoException(SetupCode.MediaInvalid, evidence)), SetupVerb.Install, source);
            AssertRefused(result, SetupVerb.Install, evidence, SetupCode.MediaInvalid);
        }
    }

    [Fact]
    public async Task OnlyExactlyTaggedCandidateMediaInvalidGetsTheNewOutput()
    {
        using var world = new RootFixture();
        var source = BoundaryPackagePath(world);
        var valid = BoundaryEvidence(SetupEvidenceCause.CandidateMediaRead);
        SetupRefusalEvidence?[] values = [null,
            valid with { Stage = SetupEvidenceStage.CurrentPackage },
            valid with { Cause = SetupEvidenceCause.Rejected },
            valid with { Cause = (SetupEvidenceCause)24 },
            valid with { Fault = ObservationFault.NativeQuery },
            valid with { ErrorKind = StartupEvidenceErrorKind.HResult },
            valid with { ErrorCode = 1 }];
        foreach (var evidence in values)
        {
            var result = await RunAsync(new ThrowingFactory(
                new SetupIoException(SetupCode.MediaInvalid, evidence)), SetupVerb.Install, source);
            Assert.Equal(1603, result.ExitCode);
            Assert.Equal(SetupExecution.Refused(SetupVerb.Install, SetupCode.MediaInvalid).Report.ToJson()
                + Environment.NewLine, result.Output);
            Assert.Equal("Hermes Setup: MediaInvalid." + Environment.NewLine, result.Diagnostics);
        }
    }

    [Theory]
    [InlineData(SetupCode.None)]
    [InlineData(SetupCode.MediaIdentityMismatch)]
    [InlineData(SetupCode.RootMismatch)]
    public async Task BoundaryEvidenceDoesNotBroadenOtherDiagnosticOutputs(object resultCode)
    {
        using var world = new RootFixture();
        var source = BoundaryPackagePath(world);
        var code = Assert.IsType<SetupCode>(resultCode);
        var factory = new ThrowingFactory(new SetupIoException(
            code, BoundaryEvidence(SetupEvidenceCause.CandidateMediaRead)));
        var result = await RunAsync(factory, SetupVerb.Install, source);
        Assert.Equal(1603, result.ExitCode);
        Assert.Equal($"Hermes Setup: {code}." + Environment.NewLine, result.Diagnostics);
        Assert.Equal(SetupExecution.Refused(SetupVerb.Install, code).Report.ToJson()
            + Environment.NewLine, result.Output);
    }

    [Fact]
    public async Task MediaDenialStillPrecedesAuthoringWithoutMutation()
    {
        using var world = new RootFixture();
        var database = new BoundaryDatabase(world.Database,
            media: world.New.Authoring.Snapshot.Package with { WordCount = 0 },
            authoring: world.New.Authoring.Cell("Property", 5, 1, "sensitive").Snapshot);
        AssertRefused(await RunBoundaryAsync(world, database), SetupVerb.Install,
            BoundaryEvidence(SetupEvidenceCause.CandidateMediaRead), SetupCode.MediaInvalid);
        Assert.Equal(0, database.AuthoringReads);
        AssertNoMutation(world);
    }

    [Fact]
    public async Task AuthoringMaterialisationDenialIsDistinctFromValidatorDenial()
    {
        using var world = new RootFixture();
        var error = new SetupIoException(SetupCode.MediaInvalid);
        var database = new BoundaryDatabase(world.Database, authoringError: error);
        AssertRefused(await RunBoundaryAsync(world, database), SetupVerb.Install,
            BoundaryEvidence(SetupEvidenceCause.CandidateAuthoringRead), SetupCode.MediaInvalid);
        Assert.Equal(CandidateMediaEvidence.At(SetupEvidenceCause.CandidateAuthoringRead), error.Evidence);
        Assert.Equal(1, database.AuthoringReads);
        AssertNoMutation(world);
    }

    [Fact]
    public async Task InventoryDenialStillPrecedesRemainingValidatorDenial()
    {
        using var world = new RootFixture();
        var later = world.New.Authoring.Cell("Property", 5, 1, "sensitive").Snapshot;
        var database = new BoundaryDatabase(world.Database, authoring: later with
            { TableNames = later.TableNames.Add("sensitive") });
        AssertRefused(await RunBoundaryAsync(world, database), SetupVerb.Install,
            BoundaryEvidence(SetupEvidenceCause.CandidateValidatorInventory), SetupCode.MediaInvalid);
        AssertNoMutation(world);
    }

    [Fact]
    public async Task RemainingValidatorDenialSurvivesTheExistingEffectBoundaries()
    {
        using var world = new RootFixture();
        var database = new BoundaryDatabase(world.Database,
            authoring: world.New.Authoring.Cell("Property", 5, 1, "sensitive").Snapshot);
        AssertRefused(await RunBoundaryAsync(world, database), SetupVerb.Install,
            BoundaryEvidence(SetupEvidenceCause.CandidateValidatorRemainder), SetupCode.MediaInvalid);
        AssertNoMutation(world);
    }

    [Fact]
    public async Task ExistingValidatorExpectedExceptionConversionRemainsMediaInvalid()
    {
        using var world = new RootFixture();
        var snapshot = world.New.Authoring.Snapshot;
        var database = new BoundaryDatabase(world.Database, authoring: snapshot with
        {
            Binaries = snapshot.Binaries.SetItem("HermesServicingGuard", ImmutableArray.Create((byte)0))
        });
        AssertRefused(await RunBoundaryAsync(world, database), SetupVerb.Install,
            BoundaryEvidence(SetupEvidenceCause.CandidateValidatorRemainder), SetupCode.MediaInvalid);
        AssertNoMutation(world);
    }

    [Fact]
    public void ResourceConstructionKeepsItsExistingDenialAndOnlyAddsTheBoundary()
    {
        using var world = new RootFixture();
        var fixture = world.New.Authoring;
        var profile = AggregateResult.Require(fixture.Validate());
        var malformed = profile with { Registry = profile.Registry.SetItem(0,
            profile.Registry[0] with { Value = "[sensitive]" }) };
        var error = Assert.Throws<SetupIoException>(() =>
            CandidateMediaEvidence.Read(SetupEvidenceCause.CandidateResources, () =>
                SetupResources.Create(fixture.Snapshot, malformed, world.Scope, RootFixture.Programs)));
        Assert.Equal(SetupCode.MediaInvalid, error.Code);
        Assert.Equal(CandidateMediaEvidence.At(SetupEvidenceCause.CandidateResources), error.Evidence);
        Assert.DoesNotContain("sensitive", error.Message);
        AssertNoMutation(world);
    }

    [Fact]
    public void BoundaryAnnotationPreservesTheOriginalExceptionInstanceAndCode()
    {
        var error = new SetupIoException(SetupCode.MediaInvalid);
        var actual = Assert.Throws<SetupIoException>(() =>
            CandidateMediaEvidence.Read<int>(SetupEvidenceCause.CandidateMediaRead, () => throw error));
        Assert.Same(error, actual);
        Assert.Equal(SetupCode.MediaInvalid, actual.Code);
        Assert.Equal(CandidateMediaEvidence.At(SetupEvidenceCause.CandidateMediaRead), actual.Evidence);
    }

    [Fact]
    public void ExistingNativeEvidenceHasPriorityOverTheCoarseBoundary()
    {
        var evidence = new SetupRefusalEvidence(SetupEvidenceStage.Unspecified,
            SetupEvidenceCause.MsiObservation, ObservationFault.NativeQuery,
            StartupEvidenceErrorKind.Win32, 87);
        var error = new SetupIoException(SetupCode.MediaInvalid, evidence);
        var actual = Assert.Throws<SetupIoException>(() =>
            CandidateMediaEvidence.Read<int>(SetupEvidenceCause.CandidateAuthoringRead, () => throw error));
        Assert.Same(error, actual);
        Assert.Same(evidence, actual.Evidence);
        Assert.Equal(SetupCode.MediaInvalid, actual.Code);
    }

    [Fact]
    public void BoundaryObservationDoesNotCatchOrReclassifyOtherExceptions()
    {
        Exception[] errors = [new IOException("sensitive"), new UnauthorizedAccessException("sensitive"),
            new Win32Exception(87, "sensitive"), new BadImageFormatException("sensitive"),
            new OperationCanceledException("sensitive"), new InvalidOperationException("sensitive"),
            new SetupIoException(SetupCode.MediaIdentityMismatch)];
        foreach (var error in errors)
        {
            var actual = Assert.Throws(error.GetType(), () =>
                CandidateMediaEvidence.Read<int>(SetupEvidenceCause.CandidateMediaRead, () => throw error));
            Assert.Same(error, actual);
        }
    }

    [Fact]
    public void InvalidTrustedInputsKeepTheirOriginalCodeAndDoNotAcquireMediaEvidence()
    {
        var fixture = AuthoringFixture.Create();
        var result = (fixture with { Approved = fixture.Approved with { Root = "sensitive" } }).Validate();
        Assert.Equal(SetupCode.InvalidEvidence, result.FailureCode);
        Assert.Null(result.Evidence);
    }

    [Theory]
    [InlineData(WriterFault.Io, false)]
    [InlineData(WriterFault.Io, true)]
    [InlineData(WriterFault.AccessDenied, false)]
    [InlineData(WriterFault.AccessDenied, true)]
    public async Task BoundaryEvidenceWriterFailureCannotReplaceTheOriginalRefusal(
        object fault, bool asynchronous)
    {
        using var world = new RootFixture();
        var source = BoundaryPackagePath(world);
        var factory = new ThrowingFactory(new SetupIoException(SetupCode.MediaInvalid,
            BoundaryEvidence(SetupEvidenceCause.CandidateMediaRead)));
        var original = await RunAsync(factory, SetupVerb.Install, source);
        using var output = new FailingLineWriter(0, WriterError(fault), asynchronous);
        using var diagnostics = new FailingLineWriter(2, WriterError(fault), asynchronous);
        var exit = await SetupEntryPoint.RunAsync(["install", "--package", source], true,
            factory, output, diagnostics, CancellationToken.None);
        Assert.Equal(original.ExitCode, exit);
        Assert.Equal(original.Output, output.ToString());
        Assert.Equal("Hermes Setup: MediaInvalid." + Environment.NewLine, diagnostics.ToString());
        Assert.Equal(1, output.Writes);
        Assert.Equal(2, diagnostics.Writes);
    }

    private static string BoundaryPackagePath(RootFixture world)
    {
        var source = Path.ChangeExtension(world.New.SourcePath, "sensitive.msi");
        world.Files.Put(source, world.New.SourceBytes.AsSpan());
        return source;
    }

    private static SetupRefusalEvidence BoundaryEvidence(SetupEvidenceCause cause) =>
        new(SetupEvidenceStage.CandidatePackage, cause);

    private static Task<Captured> RunBoundaryAsync(RootFixture world, BoundaryDatabase database) =>
        RunAsync(world, SetupVerb.Install, world.Capabilities with
            { Database = database, Authoring = database });

    private sealed class BoundaryDatabase(
        RootDatabase inner,
        PackageDatabaseImage? media = null,
        PackageAuthoringSnapshot? authoring = null,
        Exception? authoringError = null) : IPackageDatabase, IPackageAuthoringDatabase
    {
        private int _authoringReads;
        internal int AuthoringReads => _authoringReads;

        public PackageDatabaseImage Read(string packagePath) => media ?? inner.Read(packagePath);

        public PackageAuthoringSnapshot ReadAuthoring(string packagePath)
        {
            _authoringReads++;
            if (authoringError is { } error)
                throw error;
            return authoring ?? inner.ReadAuthoring(packagePath);
        }
    }
}
