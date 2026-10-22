using Hermes.Servicing;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed partial class SetupEvidenceTests
{
    [Theory]
    [InlineData(AdmissionInvariant.CanonicalRootInvalid, 10)]
    [InlineData(AdmissionInvariant.ImageIsDirectory, 11)]
    [InlineData(AdmissionInvariant.ImageLinkCountNotOne, 12)]
    [InlineData(AdmissionInvariant.AncestorDepthExceeded, 13)]
    [InlineData(AdmissionInvariant.DirectoryIsNotDirectory, 14)]
    [InlineData(AdmissionInvariant.FinalPathPrefixUnsupported, 15)]
    [InlineData(AdmissionInvariant.ReadyBindingMissing, 16)]
    [InlineData(AdmissionInvariant.ReadyInventoryInvalid, 17)]
    [InlineData(AdmissionInvariant.ReadyBodyTooLarge, 18)]
    public async Task ManagedAdmissionSourcesKeepTheOuterCatchAndDistinctV1Cause(
        object source, int cause)
    {
        var invariant = Assert.IsType<AdmissionInvariant>(source);
        var error = new AdmissionException(RefusalReason.InvalidState, invariant: invariant);
        Assert.False(AggregateResult.Expected(error));
        Assert.Same(error, await Assert.ThrowsAsync<AdmissionException>(() =>
            RunAsync(new ThrowingFactory(error), SetupVerb.Recover, "")));
        var actual = await NativeAsync(error.Evidence, error.HResult, error.Invariant);
        AssertRefused(actual, SetupVerb.Recover,
            new(SetupEvidenceStage.Entry, (SetupEvidenceCause)cause), SetupCode.Unavailable);
        AssertEvidenceBound(actual);
    }

    [Fact]
    public void AdmissionSourceVocabularyIsFiniteDistinctAndExplicitlyMapped()
    {
        var invariants = Enum.GetValues<AdmissionInvariant>()
            .Where(value => value != AdmissionInvariant.None).ToArray();
        var evidence = invariants.Select(value =>
            SetupRefusalEvidence.FromAdmission(null, new Exception().HResult, value)).ToArray();
        Assert.Equal(9, invariants.Length);
        Assert.Equal(Enumerable.Range(10, 9), evidence.Select(value => (int)value.Cause).Order());
        Assert.All(evidence, value =>
        {
            Assert.Equal(SetupEvidenceStage.Entry, value.Stage);
            Assert.Equal(ObservationFault.None, value.Fault);
            Assert.Equal(StartupEvidenceErrorKind.None, value.ErrorKind);
            Assert.Equal(0L, value.ErrorCode);
        });
    }

    [Fact]
    public async Task ActualCanonicalRejectionReachesTheExistingBridge()
    {
        var error = Assert.Throws<AdmissionException>(() => RuntimePaths.Canonical("relative"));
        Assert.Equal(RefusalReason.InvalidState, error.Reason);
        Assert.Equal(AdmissionInvariant.CanonicalRootInvalid, error.Invariant);
        var actual = await NativeAsync(error.Evidence, error.HResult, error.Invariant);
        AssertRefused(actual, SetupVerb.Recover,
            new(SetupEvidenceStage.Entry, SetupEvidenceCause.CanonicalRootInvalid),
            SetupCode.Unavailable);
        AssertEvidenceBound(actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public async Task UnannotatedOrUnknownMetadataKeepsTheOriginalHResultFallback(int source)
    {
        var error = new AdmissionException(RefusalReason.InvalidState,
            invariant: (AdmissionInvariant)source);
        var actual = await NativeAsync(error.Evidence, error.HResult, error.Invariant);
        AssertRefused(actual, SetupVerb.Recover,
            new(SetupEvidenceStage.Entry, SetupEvidenceCause.ExpectedException,
                ErrorKind: StartupEvidenceErrorKind.HResult, ErrorCode: error.HResult),
            SetupCode.Unavailable);
        AssertEvidenceBound(actual);
    }

    [Theory]
    [InlineData(StartupEvidenceErrorKind.None, 0)]
    [InlineData(StartupEvidenceErrorKind.Win32, 5)]
    [InlineData(StartupEvidenceErrorKind.HResult, -2147024891)]
    public async Task RetainedNativeMetadataKeepsPrecedenceAndItsOriginalNumbers(
        StartupEvidenceErrorKind kind, int code)
    {
        var evidence = new StartupRefusalEvidence(
            StartupEvidenceStage.LoadedImage, StartupEvidenceOperation.StandardInformation,
            StartupEvidenceFailure.NativeCallFailed, kind, code);
        var actual = await NativeAsync(evidence, new Exception().HResult,
            AdmissionInvariant.CanonicalRootInvalid);
        AssertRefused(actual, SetupVerb.Recover,
            new(SetupEvidenceStage.Entry, SetupEvidenceCause.ExpectedException,
                ErrorKind: kind, ErrorCode: code), SetupCode.Unavailable);
        AssertEvidenceBound(actual);
    }

    [Theory]
    [InlineData(WriterFault.Io, false)]
    [InlineData(WriterFault.Io, true)]
    [InlineData(WriterFault.AccessDenied, false)]
    [InlineData(WriterFault.AccessDenied, true)]
    public async Task OptionalManagedSourceEvidenceIoCannotReplaceTheNativeRefusal(
        object fault, bool asynchronous)
    {
        var error = WriterError(fault);
        using var output = new FailingLineWriter(0, error, asynchronous);
        using var diagnostics = new FailingLineWriter(2, error, asynchronous);
        var exit = await SetupEntryPoint.NativeRefusalAsync(
            ["recover", "--quiet"], output, diagnostics, null, new Exception().HResult,
            AdmissionInvariant.CanonicalRootInvalid);
        Assert.Equal(1603, exit);
        Assert.Equal(SetupExecution.Refused(SetupVerb.Recover, SetupCode.Unavailable)
            .Report.ToJson() + Environment.NewLine, output.ToString());
        Assert.Equal("Hermes Setup: Unavailable." + Environment.NewLine, diagnostics.ToString());
        Assert.Equal(1, output.Writes);
        Assert.Equal(2, diagnostics.Writes);
    }

    private static async Task<Captured> NativeAsync(
        StartupRefusalEvidence? evidence, int hResult,
        AdmissionInvariant invariant = AdmissionInvariant.None)
    {
        using var output = new StringWriter();
        using var diagnostics = new StringWriter();
        var exit = await SetupEntryPoint.NativeRefusalAsync(
            ["recover", "--quiet"], output, diagnostics, evidence, hResult, invariant);
        return new(exit, output.ToString(), diagnostics.ToString());
    }

    private static void AssertEvidenceBound(Captured actual)
    {
        var lines = actual.Diagnostics.Split(
            Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        var line = Assert.Single(lines, value =>
            value.StartsWith("HERMES_SETUP", StringComparison.Ordinal));
        Assert.InRange(line.Length, 1, SetupRefusalEvidence.MaximumDiagnosticLength);
        Assert.InRange(actual.Diagnostics.Length, 1, 4096);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
    }
}
