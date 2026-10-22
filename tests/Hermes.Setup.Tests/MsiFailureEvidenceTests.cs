using System.Text.Json;
using Hermes.Servicing;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed class MsiFailureEvidenceTests
{
    private const string Prefix = "HERMES_SETUP_MSI";
    private static MsiFailureEvidence Original { get; } = new(1399, 1723, false, true, false);

    [Fact]
    public async Task Actual_root_failure_emits_only_numeric_observation_and_stays_closed()
    {
        using var world = FailedWorld(Original);
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var code = await RunAsync(world, output, errors);
        Assert.Equal(1603, code);
        AssertPublicFailure(output.ToString());
        Assert.Equal("Hermes Setup: TransactionFenceMissing." + Environment.NewLine +
            Original.ToDiagnosticLine() + Environment.NewLine, errors.ToString());
        Assert.Equal(2, world.Registry.Reads);
        AssertClosed(world);
    }

    [Fact]
    public async Task Recovery_observation_cannot_replace_original_failure()
    {
        using var world = FailedWorld(Original);
        var original = await world.RunAsync(world.Install);
        var recoveryEvidence = new MsiFailureEvidence(1501, 2896, false, true, false);
        world.Engine.Enqueue(new(1603, null, Fence: false, Failure: recoveryEvidence));
        var recovery = await world.RunAsync(new MaintenanceCommand(SetupVerb.Recover, true));
        Assert.Same(Original, original.MsiFailure);
        Assert.Same(recoveryEvidence, recovery.MsiFailure);
        Assert.Equal(SetupVerb.Install, original.Report.Operation);
        Assert.Equal(SetupVerb.Recover, recovery.Report.Operation);
        Assert.Equal(1603, original.Report.MsiExitCode);
        Assert.Equal(1603, recovery.Report.MsiExitCode);
        AssertClosed(world);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1603, 1603)]
    [InlineData(1605, 1603)]
    [InlineData(1618, 1618)]
    [InlineData(3010, 3010)]
    [InlineData(1641, 3010)]
    public async Task Other_native_outcomes_do_not_emit_missing_fence_metadata(int code, int exit)
    {
        using var world = new RootFixture();
        using var output = new StringWriter();
        using var errors = new StringWriter();
        world.Engine.Enqueue(new((uint)code, code == 0 ? world.New : null, Failure: Original));
        Assert.Equal(exit, await RunAsync(world, output, errors));
        using var report = JsonDocument.Parse(output.ToString());
        Assert.Equal(code, report.RootElement.GetProperty("msiExitCode").GetInt32());
        Assert.DoesNotContain(Prefix, errors.ToString());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1723, true)]
    public async Task Unknown_or_failed_record_read_is_not_a_success_shaped_fallback(
        int errorId, bool readFailed)
    {
        var evidence = new MsiFailureEvidence(0, errorId, readFailed, false, false);
        using var world = FailedWorld(evidence);
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(1603, await RunAsync(world, output, errors));
        AssertPublicFailure(output.ToString());
        Assert.Contains(evidence.ToDiagnosticLine(), errors.ToString());
        AssertClosed(world);
    }

    [Fact]
    public async Task Expected_optional_writer_failure_preserves_original_closed_result()
    {
        using var world = FailedWorld(Original);
        using var output = new StringWriter();
        using var errors = new EvidenceWriter(new IOException("SYNTHETIC_SECRET"));
        Assert.Equal(1603, await RunAsync(world, output, errors));
        AssertPublicFailure(output.ToString());
        Assert.DoesNotContain("SYNTHETIC_SECRET", output.ToString() + errors.ToString());
        AssertClosed(world);
    }

    [Fact]
    public async Task Programming_writer_failure_preserves_exception_identity()
    {
        using var world = FailedWorld(Original);
        using var output = new StringWriter();
        var expected = new InvalidOperationException("synthetic programming fault");
        using var errors = new EvidenceWriter(expected);
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunAsync(world, output, errors));
        Assert.Same(expected, actual);
        AssertPublicFailure(output.ToString());
        AssertClosed(world);
    }

    private static RootFixture FailedWorld(MsiFailureEvidence evidence)
    {
        var world = new RootFixture();
        world.Engine.Enqueue(new(1603, null, Fence: false, Failure: evidence));
        world.Files.Put(RootFixture.OwnerConfig, [7, 8, 9]);
        return world;
    }

    private static Task<int> RunAsync(
        RootFixture world, TextWriter output, TextWriter errors) =>
        SetupEntryPoint.RunAsync(
            ["install", "--package", world.New.SourcePath, "--quiet"],
            true, world.Factory, output, errors, CancellationToken.None);

    private static void AssertPublicFailure(string text)
    {
        using var document = JsonDocument.Parse(text);
        var report = document.RootElement;
        Assert.Equal(5, report.EnumerateObject().Count());
        Assert.Equal(1, report.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("RecoveryRequired", report.GetProperty("outcome").GetString());
        Assert.False(report.GetProperty("ready").GetBoolean());
        Assert.Equal(1603, report.GetProperty("msiExitCode").GetInt32());
    }

    private static void AssertClosed(RootFixture world)
    {
        Assert.Equal(GateDisposition.Closed, world.Admission.Snapshot.Disposition);
        Assert.Equal(0, world.Admission.Publications);
        Assert.False(world.Admission.Held);
        Assert.Empty(world.Files.ActiveReadPaths);
        Assert.Equal(new byte[] { 7, 8, 9 }, world.Files.RawBytes(RootFixture.OwnerConfig));
    }

    private sealed class EvidenceWriter(Exception failure) : StringWriter
    {
        public override Task WriteLineAsync(string? value) =>
            value?.StartsWith(Prefix, StringComparison.Ordinal) == true
                ? Task.FromException(failure)
                : base.WriteLineAsync(value);
    }
}
