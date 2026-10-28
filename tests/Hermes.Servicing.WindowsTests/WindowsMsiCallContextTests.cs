using System.Runtime.Versioning;
using Hermes.Installer.Control;
using Hermes.Servicing;
using Xunit;

namespace Hermes.Servicing.WindowsTests;

[SupportedOSPlatform("windows")]
public sealed class WindowsMsiCallContextTests
{
    // Real current-process/current-thread token queries. The native-call delegate
    // is deliberately inert: this test installs nothing and manufactures no fence.
    [Fact]
    public void Calling_thread_context_is_numeric_and_cannot_open_a_closed_result()
    {
        var authority = Create();
        var receipt = authority.Execute(() => new Lifetime(),
            () => authority.InvokeObserved(WindowsMsiCallContextTokens.Instance,
                static () => 1603), static _ => { });
        AssertObserved(receipt.CallContext);
        Assert.True(receipt.ApiEntered && receipt.ApiReturned);
        Assert.Equal(1603u, receipt.MsiExitCode);
        Assert.False(receipt.ControlFaulted || receipt.MayEvaluateRecovery);
        Assert.Empty(receipt.Fences);
    }

    private static void AssertObserved(MsiCallContextObservation value)
    {
        value.RequireValid();
        Assert.Equal(MsiContextRead.Observed, value.Process);
        Assert.Contains(value.Elevated, new[] { 0, 1 });
        Assert.InRange(value.ElevationType, 1, 3);
        Assert.InRange(value.Integrity, 0, 65535);
        Assert.Contains(value.Admin, new[] { 0, 1 });
        Assert.Contains(value.Thread, new[] { MsiContextRead.Observed, MsiContextRead.Absent });
        Assert.Contains(value.Linked, new[] { MsiContextRead.Observed, MsiContextRead.Absent });
        Assert.True(value.ToDiagnosticSuffix().Length <= 220);
    }

    private static CallAuthority Create()
    {
        var run = Guid.NewGuid();
        var root = FixedInstallRoot.ForCurrentUser();
        var endpoint = new ControlEndpoint(1, run, WindowsPeers.Current(),
            WindowsPeers.CurrentSid(), ControlNames.Pipe(run));
        var plan = new ControlPlan(Guid.NewGuid(), MaintenanceKind.Install,
            root.AppDirectory, [], Path.GetFullPath("never-installed.msi"), false, true);
        return new(endpoint, plan, new Gate(), root);
    }

    private sealed class Gate : IClosedGateOwnership
    {
        public string GatePath => Path.GetFullPath("synthetic-context-admission.bin");
        public bool IsHeldAndClosed => true;
    }

    private sealed class Lifetime : IDisposable
    {
        public void Dispose() { }
    }
}
