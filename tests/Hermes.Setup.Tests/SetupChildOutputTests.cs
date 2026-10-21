using Hermes.Installer.Control;
using Hermes.Servicing;
using Hermes.Servicing.Acceptance;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed partial class SetupChildTransportTests
{
    private const string RefusalSummary = "Hermes Setup: InvalidEvidence.\n";
    private const string FenceSummary = "Hermes Setup: TransactionFenceMissing.\n";
    private const string RefusalLine =
        "HERMES_SETUP_EVIDENCE v=1 stage=1 cause=10 fault=0 errorKind=0 error=0";
    private const string MsiLine =
        "HERMES_SETUP_MSI v=1 action=1399 error=1723 readFailed=0 preflight=0 fence=0";
    private const string NativeLine =
        "HERMES_NATIVE_PREFLIGHT v=1 source=1 observed=1 point=2 reason=11";
    private static SetupReport RefusedReport { get; } =
        new(SetupVerb.Install, SetupOutcome.Refused, false, null);
    private static SetupReport ClosedReport { get; } =
        new(SetupVerb.Install, SetupOutcome.RecoveryRequired, false, 1603);

    public static IEnumerable<object[]> BadReports => new[]
    {
        "null",
        "[]",
        "{}",
        SuccessJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":0"),
        SuccessJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":true"),
        SuccessJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":\"1\""),
        SuccessJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":1.0"),
        SuccessJson.Replace(",\"schemaVersion\":1", "").Replace("\"schemaVersion\":1,", ""),
        SuccessJson.Insert(1, "\"schemaVersion\":1,"),
        SuccessJson.Insert(1, "\"extra\":1,"),
        SuccessJson.Replace("\"operation\":\"Install\"", "\"operation\":\"Repair\""),
        SuccessJson.Replace("\"outcome\":\"Completed\"", "\"outcome\":0"),
        SuccessJson.Replace("\"ready\":true", "\"ready\":1"),
        SuccessJson.Replace("\"msiExitCode\":0", "\"msiExitCode\":-1"),
        SuccessJson.Replace("\"msiExitCode\":0", "\"msiExitCode\":2147483648"),
        SuccessJson + "\n" + SuccessJson,
        SuccessJson + " ",
        "noise\n" + SuccessJson,
        "\uFEFF" + SuccessJson
    }.Select(text => new object[] { text });

    [Theory]
    [MemberData(nameof(BadReports))]
    public async Task Entire_canonical_incoming_report_is_required(string text)
    {
        using var child = new Child(Bytes(text + "\n"), Bytes(SuccessSummary));
        await FaultAsync(ConsumeAsync(child), SetupChildFault.InvalidReport);
        Assert.Equal(0, child.Kills);
    }

    [Fact]
    public async Task Operation_is_bound_to_the_requested_command()
    {
        using var child = new Child(Bytes(SuccessJson + "\n"), Bytes(SuccessSummary));
        await FaultAsync(ConsumeAsync(child, SetupVerb.Repair), SetupChildFault.InvalidReport);
    }

    public static IEnumerable<object[]> BadDiagnostics =>
        new (SetupReport Report, string Diagnostics)[]
        {
            (SuccessReport, SuccessSummary + "unexpected output\n"),
            (SuccessReport, SuccessSummary + SuccessSummary),
            (SuccessReport, SuccessSummary + "\n"),
            (SuccessReport, "Hermes Setup: Invented.\n"),
            (SuccessReport, "hermes Setup: None.\n"),
            (SuccessReport, SuccessSummary.Replace("\n", "\r\n")),
            (SuccessReport, SuccessSummary + RefusalLine + "\n"),
            (SuccessReport, SuccessSummary + MsiLine + "\n"),
            (RefusedReport, RefusalSummary + RefusalLine + "\n" + RefusalLine + "\n"),
            (RefusedReport, RefusalSummary + RefusalLine.Replace("stage=1", "stage=10") + "\n"),
            (RefusedReport, RefusalSummary + RefusalLine.Replace("stage=1", "stage=17") + "\n"),
            (RefusedReport, RefusalSummary + RefusalLine.Replace("fault=0", "fault=1") + "\n"),
            (RefusedReport, RefusalSummary + RefusalLine.Replace("cause=10", "cause=74") + "\n"),
            (RefusedReport, RefusalSummary + RefusalLine.Replace("v=1", "v=01") + "\n"),
            (RefusedReport, RefusalSummary + RefusalLine.Replace("error=0", "error=4294967296") + "\n"),
            (RefusedReport, RefusalSummary + RefusalLine.Replace("error=0", "error=999999999999999999999") + "\n"),
            (RefusedReport, "Hermes Setup: MediaInvalid.\n" +
                RefusalLine.Replace("stage=1 cause=10", "stage=10 cause=1") + "\n"),
            (ClosedReport, FenceSummary + MsiLine.Replace("action=1399", "action=1398") + "\n"),
            (ClosedReport, FenceSummary + MsiLine.Replace("v=1", "v=2") + "\n"),
            (ClosedReport, FenceSummary + MsiLine.Replace("error=1723", "error=65536") + "\n"),
            (ClosedReport, FenceSummary + MsiLine.Replace("readFailed=0", "readFailed=2") + "\n"),
            (ClosedReport, FenceSummary + MsiLine.Replace("fence=0", "fence=1") + "\n"),
            (ClosedReport, FenceSummary + MsiLine + "\n" + MsiLine + "\n"),
            (ClosedReport, FenceSummary + RefusalLine + "\n"),
            (ClosedReport, FenceSummary + NativeLine + "\n" + MsiLine + "\n"),
            (ClosedReport, FenceSummary + MsiLine.Replace("preflight=0", "preflight=1") +
                "\n" + NativeLine + "\n"),
            (ClosedReport, FenceSummary + NativeLine.Replace("observed=1", "observed=0") + "\n"),
            (ClosedReport, FenceSummary + NativeLine.Replace("source=1", "source=2") + "\n"),
            (ClosedReport, FenceSummary + NativeLine.Replace("point=2", "point=6") + "\n"),
            (ClosedReport, FenceSummary + ContextLine.Replace("process=0", "process=3") + "\n"),
            (ClosedReport, FenceSummary + ContextLine.Replace("integrity=-1", "integrity=1") + "\n")
        }.Select(row => new object[] { row.Report.ToJson(), row.Diagnostics });

    private static string ContextLine =>
        new MsiFailureEvidence(1399, 1723, false, false, false)
        {
            CallContext = MsiCallContextObservation.Missing
        }.ToDiagnosticLine();

    [Theory]
    [MemberData(nameof(BadDiagnostics))]
    public async Task Every_diagnostic_record_is_consumed_and_semantically_bound(
        string report, string diagnostics)
    {
        using var child = new Child(
            Bytes(report + "\n"), Bytes(diagnostics),
            exitCode: report == SuccessJson ? 0 : 1603);
        await FaultAsync(ConsumeAsync(child), SetupChildFault.InvalidDiagnostics);
    }

    [Theory]
    [InlineData(WriterCase.Success, 0, SetupOutcome.Completed)]
    [InlineData(WriterCase.Refused, 1603, SetupOutcome.Refused)]
    [InlineData(WriterCase.MediaInvalid, 1603, SetupOutcome.Refused)]
    [InlineData(WriterCase.Msi, 1603, SetupOutcome.RecoveryRequired)]
    [InlineData(WriterCase.Context, 1603, SetupOutcome.RecoveryRequired)]
    [InlineData(WriterCase.Native, 1603, SetupOutcome.RecoveryRequired)]
    [InlineData(WriterCase.NativeOnly, 1603, SetupOutcome.RecoveryRequired)]
    [InlineData(WriterCase.RolledBack, 1603, SetupOutcome.RolledBack)]
    [InlineData(WriterCase.Reboot, 3010, SetupOutcome.RebootRequired)]
    [InlineData(WriterCase.Reboot1641, 3010, SetupOutcome.RebootRequired)]
    [InlineData(WriterCase.VerificationRebootZero, 3010, SetupOutcome.RebootRequired)]
    [InlineData(WriterCase.VerificationRebootFailure, 3010, SetupOutcome.RebootRequired)]
    [InlineData(WriterCase.Busy, 1618, SetupOutcome.RecoveryRequired)]
    [InlineData(WriterCase.PreCallBusy, 1618, SetupOutcome.Refused)]
    [InlineData(WriterCase.UnknownProduct, 1603, SetupOutcome.RecoveryRequired)]
    public async Task Real_writer_frames_pass_through_real_consumer(
        WriterCase kind, int expectedExit, SetupOutcome outcome)
    {
        using var world = await WriterWorldAsync(kind);
        using var output = new StringWriter();
        using var diagnostics = new OptionalWriter(kind == WriterCase.NativeOnly);
        var exit = await SetupEntryPoint.RunAsync(
            ["install", "--package", world.New.SourcePath, "--quiet"],
            true, world.Factory, output, diagnostics, CancellationToken.None);
        using var child = new Child(Bytes(output.ToString()),
            Bytes(diagnostics.ToString()), exitCode: exit);
        var result = await ConsumeAsync(child);
        Assert.Equal(expectedExit, result.ExitCode);
        Assert.Equal(outcome, result.Report.Outcome);
        Assert.Equal(0, child.Kills);
    }

    [Theory]
    [InlineData(SetupVerb.Repair)]
    [InlineData(SetupVerb.Uninstall)]
    [InlineData(SetupVerb.Recover)]
    public async Task Real_maintenance_writer_uses_the_requested_operation(SetupVerb operation)
    {
        using var world = new RootFixture();
        if (operation is SetupVerb.Repair or SetupVerb.Uninstall)
            await world.SeedAsync();
        using var output = new StringWriter();
        using var diagnostics = new StringWriter();
        var exit = await SetupEntryPoint.RunAsync(
            [operation.ToString().ToLowerInvariant(), "--quiet"], true, world.Factory,
            output, diagnostics, CancellationToken.None);
        using var child = new Child(Bytes(output.ToString()),
            Bytes(diagnostics.ToString()), exitCode: exit);
        Assert.Equal(operation, (await ConsumeAsync(child, operation)).Report.Operation);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task A_complete_consistent_writer_newline_layout_is_accepted(string newline)
    {
        using var child = new Child(
            Bytes(SuccessJson + newline), Bytes(SuccessSummary.Replace("\n", newline)));
        Assert.Equal(SuccessReport, (await ConsumeAsync(child)).Report);
    }

    [Fact]
    public async Task Truncated_optional_diagnostics_are_not_silently_dropped()
    {
        using var child = new Child(
            Bytes(RefusedReport.ToJson() + "\n"),
            Bytes(RefusalSummary + RefusalLine), exitCode: 1603);
        await FaultAsync(ConsumeAsync(child), SetupChildFault.PrematureEof);
    }

    [Fact]
    public async Task Defined_but_inconsistent_summary_is_not_accepted()
    {
        using var child = new Child(
            Bytes(SuccessJson + "\n"), Bytes(RefusalSummary));
        await FaultAsync(ConsumeAsync(child), SetupChildFault.ExitMismatch);
    }

    [Fact]
    public async Task Native_excluded_msi_status_cannot_claim_a_missing_transaction_fence()
    {
        var report = ClosedReport with { MsiExitCode = 1605 };
        using var child = new Child(
            Bytes(report.ToJson() + "\n"), Bytes(FenceSummary), exitCode: 1603);
        await FaultAsync(ConsumeAsync(child), SetupChildFault.ExitMismatch);
    }

    public static IEnumerable<object[]> ContradictoryStatuses =>
        new (SetupOutcome Outcome, int? Msi, SetupCode Code, int Exit)[]
        {
            (SetupOutcome.RebootRequired, 1618, SetupCode.RebootRequired, 3010),
            (SetupOutcome.RebootRequired, 1605, SetupCode.RebootRequired, 3010),
            (SetupOutcome.RebootRequired, null, SetupCode.RebootRequired, 3010),
            (SetupOutcome.RecoveryRequired, 0, SetupCode.Busy, 1618),
            (SetupOutcome.RecoveryRequired, 1603, SetupCode.Busy, 1618),
            (SetupOutcome.RecoveryRequired, 1605, SetupCode.Busy, 1618),
            (SetupOutcome.RecoveryRequired, 3010, SetupCode.Busy, 1618),
            (SetupOutcome.RecoveryRequired, 1641, SetupCode.Busy, 1618),
            (SetupOutcome.RecoveryRequired, 1618, SetupCode.VerificationFailed, 1603),
            (SetupOutcome.RecoveryRequired, 1605, SetupCode.VerificationFailed, 1603),
            (SetupOutcome.RecoveryRequired, 3010, SetupCode.VerificationFailed, 1603),
            (SetupOutcome.RecoveryRequired, 1641, SetupCode.VerificationFailed, 1603),
            (SetupOutcome.RecoveryRequired, null, SetupCode.TransactionFenceMissing, 1603)
        }.Select(row => new object[]
        {
            new SetupReport(SetupVerb.Install, row.Outcome, false, row.Msi).ToJson(),
            $"Hermes Setup: {row.Code}.\n", row.Exit
        });

    [Theory]
    [MemberData(nameof(ContradictoryStatuses))]
    public async Task Receipt_summary_and_outcome_must_obey_producer_ordering(
        string report, string diagnostics, int exit)
    {
        using var child = new Child(Bytes(report + "\n"), Bytes(diagnostics), exitCode: exit);
        await FaultAsync(ConsumeAsync(child), SetupChildFault.ExitMismatch);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1603)]
    [InlineData(1605)]
    [InlineData(1618)]
    [InlineData(3010)]
    [InlineData(1641)]
    public async Task Incomplete_api_receipts_keep_their_real_policy_failure(int msiExit)
    {
        var receipt = new EngineReceipt(
            Guid.NewGuid(), true, false, (uint)msiExit, false, true, true);
        var decision = Assert.IsType<LeaveClosed>(
            CompletionPolicy.BeforeVerification(receipt, false));
        Assert.Equal(SetupCode.MsiDidNotComplete, decision.Code);
        var execution = SetupExecution.Closed(SetupVerb.Install, decision, receipt);
        using var child = new Child(Bytes(execution.Report.ToJson() + "\n"),
            Bytes($"Hermes Setup: {execution.Code}.\n"), exitCode: (int)execution.ExitCode);
        Assert.Equal(execution.Report, (await ConsumeAsync(child)).Report);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_pre_call_closed_failure_does_not_invent_an_msi_receipt(bool busy)
    {
        var decision = new LeaveClosed(
            busy ? SetupCode.Busy : SetupCode.Unavailable,
            busy ? SetupExitCode.Busy : SetupExitCode.ServicingFailed);
        var execution = SetupExecution.Closed(SetupVerb.Install, decision);
        using var child = new Child(Bytes(execution.Report.ToJson() + "\n"),
            Bytes($"Hermes Setup: {execution.Code}.\n"), exitCode: (int)execution.ExitCode);
        var result = await ConsumeAsync(child);
        Assert.Equal(execution.Report, result.Report);
        Assert.Null(result.Report.MsiExitCode);
    }

    public enum WriterCase
    {
        Success, Refused, MediaInvalid, Msi, Context, Native, NativeOnly,
        RolledBack, Reboot, Busy, Reboot1641, VerificationRebootZero,
        VerificationRebootFailure, PreCallBusy, UnknownProduct
    }

    private static async Task<RootFixture> WriterWorldAsync(WriterCase kind)
    {
        var world = new RootFixture(badGuard: kind == WriterCase.MediaInvalid);
        if (kind == WriterCase.Refused)
            world.Factory.Trust([]);
        if (kind == WriterCase.PreCallBusy)
            world.Admission.MakeBusy();
        if (kind == WriterCase.RolledBack)
            await world.SeedAsync();
        if (WriterReceipt(kind, world) is { } queued)
            world.Engine.Enqueue(queued);
        return world;
    }

    private static RootMsiResult? WriterReceipt(WriterCase kind, RootFixture world) =>
        kind switch
        {
            WriterCase.RolledBack => new(1603, world.Old),
            WriterCase.Reboot => new(3010, world.New),
            WriterCase.Reboot1641 => new(1641, world.New),
            WriterCase.VerificationRebootZero => new(0, world.New, RootDefect.Pending),
            WriterCase.VerificationRebootFailure => new(1603, world.New, RootDefect.Pending),
            WriterCase.Busy => new(1618, null),
            WriterCase.UnknownProduct => new(1605, null),
            WriterCase.Msi or WriterCase.Context or WriterCase.Native or WriterCase.NativeOnly =>
                MissingFence(kind),
            _ => null
        };

    private static RootMsiResult MissingFence(WriterCase kind) =>
        new(1603, null, Fence: false, OwnPreflight: false,
            Failure: new MsiFailureEvidence(1399, 1723, false, false, false)
            {
                CallContext = kind == WriterCase.Context
                    ? MsiCallContextObservation.Missing : null
            },
            Preflight: kind is WriterCase.Native or WriterCase.NativeOnly
                ? NativePreflightEvidence.FromValues(2, 11) : null);

    private sealed class OptionalWriter(bool omitMsi) : StringWriter
    {
        public override Task WriteLineAsync(string? value) =>
            omitMsi && value?.StartsWith("HERMES_SETUP_MSI", StringComparison.Ordinal) == true
                ? Task.FromException(new IOException("synthetic optional write failure"))
                : base.WriteLineAsync(value);
    }
}
