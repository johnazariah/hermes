using System.Text.Json;
using Hermes.Servicing;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed class NativePreflightEvidenceTests
{
    private const string Prefix = "HERMES_NATIVE_PREFLIGHT";
    private static MsiFailureEvidence MissingFences { get; } = new(1399, 0, false, false, false);
    private static NativePreflightEvidence Original { get; } =
        NativePreflightEvidence.FromValues(2, 11);

    [Fact]
    public async Task Actual_root_emits_the_separate_closed_frame_and_keeps_the_public_report()
    {
        using var world = World();
        using var output = new StringWriter();
        using var diagnostics = new StringWriter();
        Assert.Equal(1603, await RunAsync(world, output, diagnostics));
        AssertPublicClosed(output.ToString());
        Assert.Equal("Hermes Setup: TransactionFenceMissing." + Environment.NewLine +
            MissingFences.ToDiagnosticLine() + Environment.NewLine +
            Original.ToDiagnosticLine() + Environment.NewLine, diagnostics.ToString());
        AssertClosed(world);
    }

    [Fact]
    public async Task Recovery_cannot_replace_the_original_observation()
    {
        using var world = World();
        var original = await world.RunAsync(world.Install);
        var saved = original.NativePreflight?.ToDiagnosticLine();
        var recoveryHint = NativePreflightEvidence.FromValues(8, 2);
        world.Engine.Enqueue(new(1603, null, Fence: false, Failure: MissingFences,
            Preflight: recoveryHint, OwnPreflight: false));
        var recovery = await world.RunAsync(new MaintenanceCommand(SetupVerb.Recover, true));
        Assert.Same(Original, original.NativePreflight);
        Assert.Equal(saved, original.NativePreflight?.ToDiagnosticLine());
        Assert.Same(recoveryHint, recovery.NativePreflight);
        Assert.Equal(SetupVerb.Install, original.Report.Operation);
        Assert.Equal(SetupVerb.Recover, recovery.Report.Operation);
        Assert.False(original.Report.Ready || recovery.Report.Ready);
        AssertClosed(world);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expected_optional_writer_failure_preserves_the_closed_native_result(bool denied)
    {
        using var world = World();
        using var output = new StringWriter();
        using var diagnostics = new BrokenWriter(denied
            ? new UnauthorizedAccessException("PRIVATE_SECRET")
            : new IOException("PRIVATE_SECRET"));
        Assert.Equal(1603, await RunAsync(world, output, diagnostics));
        AssertPublicClosed(output.ToString());
        Assert.Contains(MissingFences.ToDiagnosticLine(), diagnostics.ToString());
        Assert.DoesNotContain("PRIVATE_SECRET", output.ToString() + diagnostics.ToString());
        AssertClosed(world);
    }

    [Fact]
    public async Task Programming_writer_failure_is_not_converted_to_a_negative_fallback()
    {
        using var world = World();
        using var output = new StringWriter();
        var expected = new InvalidOperationException("PRIVATE_SECRET");
        using var diagnostics = new BrokenWriter(expected);
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunAsync(world, output, diagnostics)));
        AssertPublicClosed(output.ToString());
        AssertClosed(world);
    }

    [Fact]
    public async Task An_own_preflight_receipt_suppresses_this_before_preflight_diagnostic()
    {
        using var world = World(ownPreflight: true);
        using var output = new StringWriter();
        using var diagnostics = new StringWriter();
        Assert.Equal(1603, await RunAsync(world, output, diagnostics));
        Assert.DoesNotContain(Prefix, diagnostics.ToString());
        AssertPublicClosed(output.ToString());
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(2, 11, 1)]
    [InlineData(2, 15, 1)]
    [InlineData(2, 16, 1)]
    [InlineData(2, 17, 1)]
    [InlineData(6, 2, 1)]
    [InlineData(7, 4, 1)]
    [InlineData(8, 2, 1)]
    public void Numeric_conversion_preserves_only_the_closed_values(int point, int reason, int observed)
    {
        var value = NativePreflightEvidence.FromValues(point, reason);
        Assert.Equal($"HERMES_NATIVE_PREFLIGHT v=1 source=1 observed={observed} point={point} reason={reason}",
            value.ToDiagnosticLine());
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(10, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(2, 23)]
    [InlineData(6, 11)]
    [InlineData(2, int.MaxValue)]
    public void Invalid_conversion_is_a_programming_fault_not_coerced_evidence(int point, int reason) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativePreflightEvidence.FromValues(point, reason));

    private static RootFixture World(bool ownPreflight = false)
    {
        var world = new RootFixture();
        world.Engine.Enqueue(new(1603, null, Fence: false,
            Failure: MissingFences with { OwnPreflight = ownPreflight },
            Preflight: Original, OwnPreflight: ownPreflight));
        world.Files.Put(RootFixture.OwnerConfig, [7, 8, 9]);
        return world;
    }

    private static Task<int> RunAsync(RootFixture world, TextWriter output, TextWriter diagnostics) =>
        SetupEntryPoint.RunAsync(["install", "--package", world.New.SourcePath, "--quiet"],
            true, world.Factory, output, diagnostics, CancellationToken.None);

    private static void AssertPublicClosed(string text)
    {
        using var document = JsonDocument.Parse(text);
        var report = document.RootElement;
        Assert.Equal(5, report.EnumerateObject().Count());
        Assert.Equal(1, report.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("RecoveryRequired", report.GetProperty("outcome").GetString());
        Assert.False(report.GetProperty("ready").GetBoolean());
        Assert.Equal(1603, report.GetProperty("msiExitCode").GetInt32());
        Assert.DoesNotContain(Prefix, text);
    }

    private static void AssertClosed(RootFixture world)
    {
        Assert.Equal(GateDisposition.Closed, world.Admission.Snapshot.Disposition);
        Assert.Equal(0, world.Admission.Publications);
        Assert.False(world.Admission.Held);
        Assert.Empty(world.Files.ActiveReadPaths);
        Assert.Equal(new byte[] { 7, 8, 9 }, world.Files.RawBytes(RootFixture.OwnerConfig));
    }

    private sealed class BrokenWriter(Exception failure) : StringWriter
    {
        public override Task WriteLineAsync(string? value) =>
            value?.StartsWith(Prefix, StringComparison.Ordinal) == true
                ? Task.FromException(failure)
                : base.WriteLineAsync(value);
    }
}
