using System.Runtime.Versioning;
using Hermes.Installer.Control;
using Hermes.Servicing;
using Xunit;

namespace Hermes.Servicing.WindowsTests;

[SupportedOSPlatform("windows")]
public sealed class WindowsGuardInvalidHandleTests
{
    // Real msi.dll calls, not proof that the published NativeAOT guard DLL loaded.
    // No database, installation, registration, or successful fence is manufactured.
    [Fact]
    public void Invalid_real_MSI_session_is_refused_at_the_native_safe_boundary() =>
        Assert.Equal(1603u, WindowsMsiGuard.Invoke(0, GuardPhase.Preflight));
}
