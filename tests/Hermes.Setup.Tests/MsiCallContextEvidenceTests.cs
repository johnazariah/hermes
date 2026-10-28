using System.Text.Json;
using Hermes.Installer.Control;
using Hermes.Servicing;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed class MsiCallContextEvidenceTests
{
    private static MsiCallContextObservation High { get; } =
        new(MsiContextRead.Observed, 1, 1, 12288, 1,
            MsiContextRead.Absent, MsiContextRead.Absent, -1, -1);

    private static MsiCallContextObservation Medium { get; } =
        new(MsiContextRead.Observed, 0, 3, 8192, 0,
            MsiContextRead.Absent, MsiContextRead.Observed, 1, 1);

    private static MsiFailureEvidence Evidence(MsiCallContextObservation context) =>
        new(1399, 0, false, false, false) { CallContext = context };

    [Fact]
    public async Task Actual_Setup_root_emits_context_inside_the_MSI_frame_without_public_changes()
    {
        using var world = World();
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(1603, await SetupEntryPoint.RunAsync(
            ["install", "--package", world.New.SourcePath, "--quiet"],
            true, world.Factory, output, errors, CancellationToken.None));
        Assert.Equal("Hermes Setup: TransactionFenceMissing." + Environment.NewLine +
            Evidence(High).ToDiagnosticLine() + Environment.NewLine, errors.ToString());
        AssertPublicClosed(output.ToString());
        AssertClosed(world);
    }

    [Fact]
    public async Task Recovery_keeps_its_own_context_without_replacing_the_original()
    {
        using var world = World();
        var original = await world.RunAsync(world.Install);
        var originalLine = original.MsiFailure?.ToDiagnosticLine();
        world.Engine.Enqueue(new(1603, null, Fence: false,
            Failure: Evidence(Medium), OwnPreflight: false));
        var recovery = await world.RunAsync(new MaintenanceCommand(SetupVerb.Recover, true));
        Assert.Equal(High, original.MsiFailure?.CallContext);
        Assert.Equal(Medium, recovery.MsiFailure?.CallContext);
        Assert.Equal(originalLine, original.MsiFailure?.ToDiagnosticLine());
        Assert.Equal(SetupVerb.Install, original.Report.Operation);
        Assert.Equal(SetupVerb.Recover, recovery.Report.Operation);
        Assert.False(original.Report.Ready || recovery.Report.Ready);
        AssertClosed(world);
    }

    [Fact]
    public void Legacy_evidence_retains_its_exact_version_one_frame()
    {
        var legacy = Evidence(High) with { CallContext = null };
        Assert.Equal("HERMES_SETUP_MSI v=1 action=1399 error=0 readFailed=0 preflight=0 fence=0",
            legacy.ToDiagnosticLine());
        Assert.Equal("HERMES_SETUP_MSI v=2 action=1399 error=0 readFailed=0 preflight=0 fence=0" +
            High.ToDiagnosticSuffix(), Evidence(High).ToDiagnosticLine());
        Assert.True(Evidence(High).ToDiagnosticLine().Length <= 320);
    }

    private static RootFixture World()
    {
        var world = new RootFixture();
        world.Engine.Enqueue(new(1603, null, Fence: false,
            Failure: Evidence(High), OwnPreflight: false));
        return world;
    }

    private static void AssertPublicClosed(string text)
    {
        using var document = JsonDocument.Parse(text);
        var report = document.RootElement;
        Assert.Equal(5, report.EnumerateObject().Count());
        Assert.Equal(1, report.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("RecoveryRequired", report.GetProperty("outcome").GetString());
        Assert.False(report.GetProperty("ready").GetBoolean());
        Assert.Equal(1603, report.GetProperty("msiExitCode").GetInt32());
        Assert.DoesNotContain("context", text, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertClosed(RootFixture world)
    {
        Assert.Equal(GateDisposition.Closed, world.Admission.Snapshot.Disposition);
        Assert.Equal(0, world.Admission.Publications);
        Assert.False(world.Admission.Held);
        Assert.Empty(world.Files.ActiveReadPaths);
    }
}
