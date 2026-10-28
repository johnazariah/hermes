using System.ComponentModel;
using System.Text.Json;
using Hermes.Servicing;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed partial class SetupEvidenceTests
{
    private const string InvalidEvidenceMarker = "Hermes Setup: InvalidEvidence.";

    [Theory]
    [InlineData(SetupVerb.Install, SetupEvidenceCause.MsiObservation)]
    [InlineData(SetupVerb.Recover, SetupEvidenceCause.MsiObservation)]
    [InlineData(SetupVerb.Install, SetupEvidenceCause.ShortcutObservation)]
    [InlineData(SetupVerb.Recover, SetupEvidenceCause.ShortcutObservation)]
    [InlineData(SetupVerb.Install, SetupEvidenceCause.PendingUnknown)]
    [InlineData(SetupVerb.Recover, SetupEvidenceCause.PendingUnknown)]
    [InlineData(SetupVerb.Install, SetupEvidenceCause.PendingAlias)]
    [InlineData(SetupVerb.Recover, SetupEvidenceCause.PendingAlias)]
    [InlineData(SetupVerb.Install, SetupEvidenceCause.RegistryUnsupported)]
    [InlineData(SetupVerb.Recover, SetupEvidenceCause.RegistryUnsupported)]
    public async Task CommonInitialObservationRefusalsKeepTheirTypedCause(object operation, object defect)
    {
        var verb = Assert.IsType<SetupVerb>(operation);
        var cause = Assert.IsType<SetupEvidenceCause>(defect);
        using var world = new RootFixture();
        world.Registry.Set(Broken(world.Registry.Current, cause));
        AssertRefused(await RunAsync(world, verb), verb,
            Expected(SetupEvidenceStage.InitialInventory, cause));
        AssertNoMutation(world);
    }

    [Theory]
    [InlineData(SetupVerb.Install)]
    [InlineData(SetupVerb.Recover)]
    public async Task FinalObservationFailureIsNotMisreportedAsInitialInspection(object operation)
    {
        var verb = Assert.IsType<SetupVerb>(operation);
        using var world = new RootFixture();
        var registry = new ChangingInventory(world.Registry,
            value => Broken(value, SetupEvidenceCause.MsiObservation));
        AssertRefused(await RunAsync(world, verb, world.Capabilities with { Registry = registry }),
            verb, Expected(SetupEvidenceStage.FinalInventory, SetupEvidenceCause.MsiObservation));
        AssertNoMutation(world);
    }

    [Fact]
    public async Task ChangedInventoryHasItsOwnCauseWithoutPublishingClosure()
    {
        using var world = new RootFixture();
        var registry = new ChangingInventory(world.Registry, value => value with
        {
            Msi = ReadObservation<MsiRegistration>.Known(
                SetupStateProof.Msi(value) with { InvokingSid = "S-1-5-21-999" })
        });
        AssertRefused(await RunAsync(world, SetupVerb.Install,
            world.Capabilities with { Registry = registry }), SetupVerb.Install,
            new(SetupEvidenceStage.Ownership, SetupEvidenceCause.InventoryChanged));
        AssertNoMutation(world);
    }

    [Fact]
    public async Task MissingComponentQueryEvidenceIsNotAnEmptyClientInventory()
    {
        using var world = new RootFixture();
        await world.SeedAsync();
        world.Registry.Mutate(RootDefect.MissingQueryEvidence);
        AssertRefused(await RunAsync(world, SetupVerb.Install), SetupVerb.Install,
            new(SetupEvidenceStage.Ownership, SetupEvidenceCause.ComponentClientsIncomplete));
        AssertNoMutation(world);
        Assert.Equal(GateDisposition.Ready, world.Admission.Snapshot.Disposition);
    }

    [Theory]
    [InlineData(SetupVerb.Install)]
    [InlineData(SetupVerb.Recover)]
    public async Task RealAdmissionLeaseReleasesAnUnchangedMalformedGate(object operation)
    {
        var verb = Assert.IsType<SetupVerb>(operation);
        using var world = new RootFixture();
        world.Files.Put(world.Scope.GatePath, [99]);
        var admission = new FileAdmissionSource(world.Disk, world.Scope, new SyntheticGateCodec());
        AssertRefused(await RunAsync(world, verb, world.Capabilities with { Admission = admission }),
            verb, new(SetupEvidenceStage.Gate, SetupEvidenceCause.Rejected));
        Assert.Equal(new byte[] { 99 }, world.Files.RawBytes(world.Scope.GatePath));
        AssertNoMutation(world);
    }

    [Theory]
    [InlineData(SetupVerb.Install)]
    [InlineData(SetupVerb.Recover)]
    public async Task InvalidJournalIsIdentifiedBeforeRegistrationOrCandidateInspection(object operation)
    {
        var verb = Assert.IsType<SetupVerb>(operation);
        using var world = new RootFixture();
        world.Files.Put(Path.Combine(world.Scope.ServicingDirectory, "operation.json"), []);
        AssertRefused(await RunAsync(world, verb), verb,
            new(SetupEvidenceStage.Journal, SetupEvidenceCause.Rejected));
        Assert.Equal(0, world.Registry.Reads);
        AssertNoMutation(world);
    }

    [Theory]
    [InlineData(SetupVerb.Install, false)]
    [InlineData(SetupVerb.Recover, false)]
    [InlineData(SetupVerb.Install, true)]
    [InlineData(SetupVerb.Recover, true)]
    public async Task InvalidOrMissingCompiledTrustIsDistinguishedFromAcquiredAdmission(
        object operation, bool missing)
    {
        var verb = Assert.IsType<SetupVerb>(operation);
        using var world = new RootFixture();
        world.Factory.Trust(missing ? [] :
            [new(CompiledGuardTrust.MetadataName, "sensitive invalid metadata")]);
        AssertRefused(await RunAsync(world, verb), verb,
            new(SetupEvidenceStage.Composition, SetupEvidenceCause.Rejected),
            missing ? SetupCode.Unavailable : SetupCode.InvalidEvidence);
        Assert.Empty(world.Files.ReadPaths);
        AssertNoMutation(world);
    }

    [Theory]
    [InlineData(SetupCode.InvalidEvidence, false)]
    [InlineData(SetupCode.InvalidEvidence, true)]
    [InlineData(SetupCode.Unavailable, false)]
    [InlineData(SetupCode.Unavailable, true)]
    public async Task AdmissionFailurePreservesEvidenceOrIdentifiesTheAdmissionBoundary(
        object resultCode, bool detailed)
    {
        var code = Assert.IsType<SetupCode>(resultCode);
        using var world = new RootFixture();
        var expected = new SetupRefusalEvidence(SetupEvidenceStage.Admission,
            detailed ? SetupEvidenceCause.ExpectedException : SetupEvidenceCause.Rejected);
        var error = new SetupIoException(code, detailed ? expected : null);
        var disk = new SetupDisk(world.Files, new FailingPaths(error), world.Files);
        var admission = new FileAdmissionSource(disk, world.Scope, new SyntheticGateCodec());
        AssertRefused(await RunAsync(world, SetupVerb.Install,
            world.Capabilities with { Admission = admission }), SetupVerb.Install, expected, code);
        AssertNoMutation(world);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnavailableIoRetainsTheExistingBoundaryAndHResult(
        bool outer, bool accessDenied)
    {
        using var world = new RootFixture();
        Exception error = accessDenied
            ? new UnauthorizedAccessException("sensitive access detail")
            : new IOException("sensitive I/O detail");
        var disk = new SetupDisk(world.Files, new FailingPaths(error), world.Files);
        IAdmissionSource admission = outer ? new ThrowingAdmission(error)
            : new FileAdmissionSource(disk, world.Scope, new SyntheticGateCodec());
        AssertRefused(await RunAsync(world, SetupVerb.Install,
            world.Capabilities with { Admission = admission }), SetupVerb.Install,
            new(outer ? SetupEvidenceStage.Entry : SetupEvidenceStage.Admission,
                SetupEvidenceCause.ExpectedException,
                ErrorKind: StartupEvidenceErrorKind.HResult, ErrorCode: error.HResult),
            SetupCode.Unavailable);
        AssertNoMutation(world);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    public async Task AdmissionSharingFailuresRemainBusyWithoutEvidence(int nativeCode)
    {
        using var world = new RootFixture();
        var error = new IOException("sensitive sharing detail",
            unchecked((int)(0x80070000u | (uint)nativeCode)));
        var disk = new SetupDisk(world.Files, new FailingPaths(error), world.Files);
        var admission = new FileAdmissionSource(disk, world.Scope, new SyntheticGateCodec());
        var result = await RunAsync(world, SetupVerb.Install,
            world.Capabilities with { Admission = admission });
        Assert.Equal(1618, result.ExitCode);
        Assert.Equal(SetupExecution.Refused(SetupVerb.Install, SetupCode.Busy).Report.ToJson() +
            Environment.NewLine, result.Output);
        Assert.Equal("Hermes Setup: Busy." + Environment.NewLine, result.Diagnostics);
        AssertNoMutation(world);
    }

    [Theory]
    [InlineData(SetupVerb.Install, false)]
    [InlineData(SetupVerb.Recover, false)]
    [InlineData(SetupVerb.Install, true)]
    [InlineData(SetupVerb.Recover, true)]
    public async Task ExpectedInspectionExceptionsExposeOnlyTheirNumericalCause(object operation, bool native)
    {
        var verb = Assert.IsType<SetupVerb>(operation);
        using var world = new RootFixture();
        Exception error = native
            ? new Win32Exception(87, "sensitive native message")
            : new IOException("sensitive managed message");
        world.Registry.Fail(error);
        var expected = new SetupRefusalEvidence(SetupEvidenceStage.InitialInventory,
            SetupEvidenceCause.ExpectedException,
            ErrorKind: native ? StartupEvidenceErrorKind.Win32 : StartupEvidenceErrorKind.HResult,
            ErrorCode: native ? 87 : error.HResult);
        AssertRefused(await RunAsync(world, verb), verb, expected);
        AssertNoMutation(world);
    }

    [Theory]
    [InlineData(SetupCode.InvalidEvidence, false)]
    [InlineData(SetupCode.InvalidEvidence, true)]
    [InlineData(SetupCode.Unavailable, false)]
    [InlineData(SetupCode.Unavailable, true)]
    public async Task EntryTypedExceptionsPreserveTheirOriginalCodeAndAvailableEvidence(
        object resultCode, bool detailed)
    {
        var code = Assert.IsType<SetupCode>(resultCode);
        using var world = new RootFixture();
        var expected = new SetupRefusalEvidence(
            detailed ? SetupEvidenceStage.FinalInventory : SetupEvidenceStage.Entry,
            SetupEvidenceCause.Rejected);
        var factory = new ThrowingFactory(
            new SetupIoException(code, detailed ? expected : null));
        AssertRefused(await RunAsync(factory, SetupVerb.Install, world.New.SourcePath),
            SetupVerb.Install, expected, code);
        AssertNoMutation(world);
    }

    [Theory]
    [InlineData(SetupCode.InvalidEvidence, WriterFault.Io, false)]
    [InlineData(SetupCode.InvalidEvidence, WriterFault.Io, true)]
    [InlineData(SetupCode.InvalidEvidence, WriterFault.AccessDenied, false)]
    [InlineData(SetupCode.InvalidEvidence, WriterFault.AccessDenied, true)]
    [InlineData(SetupCode.Unavailable, WriterFault.Io, false)]
    [InlineData(SetupCode.Unavailable, WriterFault.Io, true)]
    [InlineData(SetupCode.Unavailable, WriterFault.AccessDenied, false)]
    [InlineData(SetupCode.Unavailable, WriterFault.AccessDenied, true)]
    public async Task OptionalEvidenceWriteFailurePreservesOriginalRefusal(
        object resultCode, object fault, bool asynchronous)
    {
        var code = Assert.IsType<SetupCode>(resultCode);
        var factory = new ThrowingFactory(new SetupIoException(code));
        var original = await RunAsync(factory, SetupVerb.Recover, "");
        AssertRefused(original, SetupVerb.Recover,
            new(SetupEvidenceStage.Entry, SetupEvidenceCause.Rejected), code);
        var error = WriterError(fault);
        using var output = new FailingLineWriter(0, error, asynchronous);
        using var diagnostics = new FailingLineWriter(2, error, asynchronous);
        var exit = await SetupEntryPoint.RunAsync(
            ["recover", "--quiet"], true, factory, output, diagnostics, CancellationToken.None);
        Assert.Equal(original.ExitCode, exit);
        Assert.Equal(original.Output, output.ToString());
        Assert.Equal($"Hermes Setup: {code}." + Environment.NewLine, diagnostics.ToString());
        Assert.Equal(1, output.Writes);
        Assert.Equal(2, diagnostics.Writes);
    }

    [Theory]
    [InlineData(WriterFault.Cancelled, false)]
    [InlineData(WriterFault.Cancelled, true)]
    [InlineData(WriterFault.Programming, false)]
    [InlineData(WriterFault.Programming, true)]
    [InlineData(WriterFault.Disposed, false)]
    [InlineData(WriterFault.Disposed, true)]
    public async Task OptionalEvidenceWritePreservesCancellationAndProgrammingFaults(
        object fault, bool asynchronous)
    {
        var error = WriterError(fault);
        var factory = new ThrowingFactory(new SetupIoException(SetupCode.InvalidEvidence));
        using var output = new StringWriter();
        using var diagnostics = new FailingLineWriter(2, error, asynchronous);
        var actual = await Assert.ThrowsAsync(error.GetType(), () =>
            SetupEntryPoint.RunAsync(["recover", "--quiet"], true, factory,
                output, diagnostics, CancellationToken.None));
        Assert.Same(error, actual);
        Assert.Equal(InvalidEvidenceMarker + Environment.NewLine, diagnostics.ToString());
        Assert.Equal(2, diagnostics.Writes);
    }

    [Theory]
    [InlineData(WriterFault.Io, true, false)]
    [InlineData(WriterFault.Io, true, true)]
    [InlineData(WriterFault.Io, false, false)]
    [InlineData(WriterFault.Io, false, true)]
    [InlineData(WriterFault.AccessDenied, true, false)]
    [InlineData(WriterFault.AccessDenied, true, true)]
    [InlineData(WriterFault.AccessDenied, false, false)]
    [InlineData(WriterFault.AccessDenied, false, true)]
    public async Task MandatoryReportAndMarkerWriteFailuresStillPropagate(
        object fault, bool reportFails, bool asynchronous)
    {
        var error = WriterError(fault);
        var factory = new ThrowingFactory(new SetupIoException(SetupCode.InvalidEvidence));
        using var output = new FailingLineWriter(reportFails ? 1 : 0, error, asynchronous);
        using var diagnostics = new FailingLineWriter(reportFails ? 0 : 1, error, asynchronous);
        var actual = await Assert.ThrowsAsync(error.GetType(), () =>
            SetupEntryPoint.RunAsync(["recover", "--quiet"], true, factory,
                output, diagnostics, CancellationToken.None));
        Assert.Same(error, actual);
        Assert.Equal(1, output.Writes);
        Assert.Equal(reportFails ? 0 : 1, diagnostics.Writes);
        var expectedOutput = reportFails ? "" :
            SetupExecution.Refused(SetupVerb.Recover, SetupCode.InvalidEvidence).Report.ToJson()
                + Environment.NewLine;
        Assert.Equal(expectedOutput, output.ToString());
        Assert.Equal("", diagnostics.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EscapingClosedProcessingFaultIsTaggedButStillPropagates(bool detailed)
    {
        var expected = new SetupRefusalEvidence(
            detailed ? SetupEvidenceStage.CandidatePackage : SetupEvidenceStage.ClosedProcessing,
            SetupEvidenceCause.Rejected);
        var effects = new FakeEffects(Samples.Fresh(), new Scenario
        {
            OnExecute = () => throw new SetupIoException(
                SetupCode.InvalidEvidence, detailed ? expected : null)
        });
        var error = await Assert.ThrowsAsync<SetupIoException>(
            () => effects.RunAsync(Samples.Install(Samples.First)));
        Assert.Equal(SetupCode.InvalidEvidence, error.Code);
        Assert.Equal(expected, error.Evidence);
    }

    [Fact]
    public async Task TypedEvidenceSurvivesBothExistingEffectBoundaries()
    {
        var evidence = new SetupRefusalEvidence(
            SetupEvidenceStage.InitialInventory, SetupEvidenceCause.MsiObservation,
            ObservationFault.NativeQuery, StartupEvidenceErrorKind.Win32, 5);
        var first = await SetupIo.RunAsync<Unit>(
            () => throw new SetupIoException(SetupCode.InvalidEvidence, evidence),
            SetupCode.MediaInvalid, CancellationToken.None);
        var second = await AggregateResult.ReadAsync(
            () => AggregateResult.Require(first), SetupCode.MediaInvalid, CancellationToken.None);
        Assert.Equal(SetupCode.InvalidEvidence, first.FailureCode);
        Assert.Equal(SetupCode.InvalidEvidence, second.FailureCode);
        Assert.Equal(evidence, first.Evidence);
        Assert.Equal(evidence, second.Evidence);
    }

    [Fact]
    public void InspectionChecksPreserveSpecificCodesAndNeverCatchProgrammingFaults()
    {
        var evidence = new SetupRefusalEvidence(
            SetupEvidenceStage.Unspecified, SetupEvidenceCause.RegistryUnsupported);
        var error = Assert.Throws<SetupIoException>(() =>
            InspectionEvidence.Check(SetupEvidenceStage.Placement,
                () => SetupIo.Require(false, SetupCode.RootMismatch, evidence)));
        Assert.Equal(SetupCode.RootMismatch, error.Code);
        Assert.Equal(evidence with { Stage = SetupEvidenceStage.Placement }, error.Evidence);
        Assert.Throws<InvalidOperationException>(() =>
            InspectionEvidence.Check(SetupEvidenceStage.Placement,
                () => throw new InvalidOperationException("programming fault")));
    }

    [Fact]
    public void EvidenceHasAStableBoundedNumericalWireFormat()
    {
        var evidence = new SetupRefusalEvidence(SetupEvidenceStage.InitialInventory,
            SetupEvidenceCause.ShortcutObservation, ObservationFault.Unreadable,
            StartupEvidenceErrorKind.HResult, uint.MaxValue);
        Assert.Equal(
            "HERMES_SETUP_EVIDENCE v=1 stage=8 cause=4 fault=5 errorKind=2 error=4294967295",
            evidence.ToDiagnosticLine());
        Assert.InRange(evidence.ToDiagnosticLine().Length, 1,
            SetupRefusalEvidence.MaximumDiagnosticLength);
    }

    [Fact]
    public async Task FreshSuccessAndNoRecoveryPendingKeepTheirExistingDiagnostics()
    {
        using var world = new RootFixture();
        var installed = await RunAsync(world, SetupVerb.Install);
        Assert.Equal(0, installed.ExitCode);
        Assert.Equal("Hermes Setup: None." + Environment.NewLine, installed.Diagnostics);
        var recovery = await RunAsync(world, SetupVerb.Recover);
        Assert.Equal(1603, recovery.ExitCode);
        Assert.Equal("Hermes Setup: NoRecoveryPending." + Environment.NewLine, recovery.Diagnostics);
        Assert.Equal(1, world.Engine.Calls);
        Assert.Empty(world.Files.ActiveReadPaths);
        Assert.False(world.Admission.Held);
    }

    [Fact]
    public async Task CandidateGuardRefusalRetainsItsExistingTypedNegativeOutcome()
    {
        using var world = new RootFixture(badGuard: true);
        var result = await RunAsync(world, SetupVerb.Install);
        AssertRefused(result, SetupVerb.Install,
            new(SetupEvidenceStage.CandidatePackage, SetupEvidenceCause.CandidateGuardApprovedHash),
            SetupCode.MediaInvalid);
        using var report = JsonDocument.Parse(result.Output);
        Assert.Equal("Refused", report.RootElement.GetProperty("outcome").GetString());
        Assert.False(report.RootElement.GetProperty("ready").GetBoolean());
        AssertNoMutation(world);
    }

    private static RegistrationInventory Broken(
        RegistrationInventory value, SetupEvidenceCause cause) => cause switch
    {
        SetupEvidenceCause.MsiObservation => value with
            { Msi = ReadObservation<MsiRegistration>.Unknown(ObservationFault.NativeQuery, 5) },
        SetupEvidenceCause.ShortcutObservation => value with
            { Shortcut = ReadObservation<ShortcutFields>.Unknown(ObservationFault.Unreadable, 0x80070005) },
        SetupEvidenceCause.PendingUnknown => value with
            { Pending = new(PendingDisposition.Unknown, [], 87) },
        SetupEvidenceCause.PendingAlias => value with
            { Pending = new(PendingDisposition.Alias, []) },
        SetupEvidenceCause.RegistryUnsupported => value with
            { Run = new(RegistryAtomKind.Unsupported, "sensitive registry content", null, []) },
        _ => throw new ArgumentOutOfRangeException(nameof(cause))
    };

    private static SetupRefusalEvidence Expected(
        SetupEvidenceStage stage, SetupEvidenceCause cause) => cause switch
    {
        SetupEvidenceCause.MsiObservation =>
            new(stage, cause, ObservationFault.NativeQuery, StartupEvidenceErrorKind.Win32, 5),
        SetupEvidenceCause.ShortcutObservation =>
            new(stage, cause, ObservationFault.Unreadable, StartupEvidenceErrorKind.HResult, 0x80070005),
        SetupEvidenceCause.PendingUnknown =>
            new(stage, cause, ErrorKind: StartupEvidenceErrorKind.Win32, ErrorCode: 87),
        SetupEvidenceCause.PendingAlias =>
            new(stage, cause, ErrorKind: StartupEvidenceErrorKind.Win32),
        _ => new(stage, cause)
    };

    private static Task<Captured> RunAsync(
        RootFixture world, SetupVerb verb, SetupCapabilities? capabilities = null)
    {
        ISetupApplicationFactory factory = capabilities is { } value
            ? new Factory(value, world.Old.Authoring.Approved) : world.Factory;
        return RunAsync(factory, verb, world.New.SourcePath);
    }

    private static async Task<Captured> RunAsync(
        ISetupApplicationFactory factory, SetupVerb verb, string source)
    {
        var operation = verb.ToString().ToLowerInvariant();
        string[] arguments = verb == SetupVerb.Install
            ? [operation, "--package", source, "--quiet"] : [operation, "--quiet"];
        using var output = new StringWriter();
        using var diagnostics = new StringWriter();
        var exit = await SetupEntryPoint.RunAsync(arguments, true, factory,
            output, diagnostics, CancellationToken.None);
        return new(exit, output.ToString(), diagnostics.ToString());
    }

    private static void AssertRefused(
        Captured actual, SetupVerb verb, SetupRefusalEvidence expected,
        SetupCode code = SetupCode.InvalidEvidence)
    {
        Assert.Equal(1603, actual.ExitCode);
        using var document = JsonDocument.Parse(actual.Output);
        var report = document.RootElement;
        Assert.Equal(5, report.EnumerateObject().Count());
        Assert.Equal(1, report.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(verb.ToString(), report.GetProperty("operation").GetString());
        Assert.Equal("Refused", report.GetProperty("outcome").GetString());
        Assert.False(report.GetProperty("ready").GetBoolean());
        Assert.Equal(JsonValueKind.Null, report.GetProperty("msiExitCode").ValueKind);
        Assert.Equal($"Hermes Setup: {code}." + Environment.NewLine +
            expected.ToDiagnosticLine() + Environment.NewLine, actual.Diagnostics);
        Assert.DoesNotContain("sensitive", actual.Output + actual.Diagnostics);
    }

    private static void AssertNoMutation(RootFixture world)
    {
        Assert.Equal(0, world.Engine.Calls);
        Assert.Equal(0, world.Admission.CloseCalls);
        Assert.Equal(0, world.Admission.Publications);
        Assert.False(world.Admission.Held);
        Assert.Empty(world.Files.ActiveReadPaths);
    }

    private sealed record Captured(int ExitCode, string Output, string Diagnostics);

    private enum WriterFault { Io, AccessDenied, Cancelled, Programming, Disposed }

    private static Exception WriterError(object fault) => Assert.IsType<WriterFault>(fault) switch
    {
        WriterFault.Io => new IOException("sensitive I/O detail"),
        WriterFault.AccessDenied => new UnauthorizedAccessException("sensitive access detail"),
        WriterFault.Cancelled => new OperationCanceledException("sensitive cancellation detail"),
        WriterFault.Programming => new InvalidOperationException("sensitive programming detail"),
        WriterFault.Disposed => new ObjectDisposedException("sensitive writer"),
        _ => throw new ArgumentOutOfRangeException(nameof(fault))
    };

    private sealed class FailingLineWriter(
        int failingLine, Exception error, bool asynchronous) : StringWriter
    {
        private int _writes;

        internal int Writes => _writes;

        public override Task WriteLineAsync(string? value)
        {
            if (++_writes != failingLine)
                return base.WriteLineAsync(value);
            return asynchronous ? Task.FromException(error) : throw error;
        }
    }

    private sealed class Factory(
        SetupCapabilities capabilities, ApprovedGuardProfile approved) : ISetupApplicationFactory
    {
        public Effect<SetupApplication> Create() =>
            Effect<SetupApplication>.Ok(SetupComposition.Create(capabilities, approved));
    }

    private sealed class ThrowingFactory(Exception error) : ISetupApplicationFactory
    {
        public Effect<SetupApplication> Create() => throw error;
    }

    private sealed class ThrowingAdmission(Exception error) : IAdmissionSource
    {
        public Effect<ISetupLease> TryAcquire() => throw error;
    }

    private sealed class FailingPaths(Exception error) : ISetupPathSafety
    {
        public void RequireDirectory(string path) => throw error;
        public void RequireFile(string path) => throw error;
    }

    private sealed class ChangingInventory(
        IRegistrationInventory inner,
        Func<RegistrationInventory, RegistrationInventory> change) : IRegistrationInventory
    {
        private int _reads;

        public RegistrationInventory Read(IReadOnlyList<Guid> componentCodes)
        {
            var observed = inner.Read(componentCodes);
            return ++_reads == 2 ? change(observed) : observed;
        }
    }
}
