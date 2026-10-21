using System.Security.AccessControl;
using System.Security.Principal;

namespace Hermes.Servicing.Acceptance;

internal sealed unsafe partial class WindowsRestrictedOwnerTokens
{
    internal static void RequireStartupDebugger()
    {
        Instance.RequireDisposableJob();
        if (StartupIsDebuggerPresent() != 1)
            throw RestrictedOwnerToken.Failure(5);
    }

    internal static string StartupTraceRoot()
    {
        Instance.RequireDisposableJob();
        var temporary = Environment.GetEnvironmentVariable("RUNNER_TEMP")
            ?? throw RestrictedOwnerToken.Failure(5);
        var root = RequirePowerShellProbePath(
            Path.Combine(RequirePowerShellProbePath(temporary),
                "hermes-native-startup-trace"));
        var security = new DirectoryInfo(root).GetAccessControl();
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        if (identity.User is not { } owner || !security.AreAccessRulesProtected ||
            !Equals(security.GetOwner(typeof(SecurityIdentifier)), owner))
            throw RestrictedOwnerToken.Failure(5);
        return root;
    }

    internal static void RequireStartupFile(string path) =>
        _ = RequirePowerShellProbePath(path);

    private sealed partial class OwnedInertChild : IAcceptanceStartupChild
    {
        public StartupDebugIdentity ReadStartupIdentity()
        {
            Require(ChildState.AccessObserved);
            if (!powerShellProbe || ownedSelfControl ||
                powerShellConsole != AcceptancePowerShellProbeConsole.NoWindow ||
                desktop != AcceptanceDesktopSelection.Automatic)
                throw RestrictedOwnerToken.Failure(13);
            var process = Handle(_process);
            var pid = StartupGetProcessId(process);
            long born = 0, exited = 0, kernel = 0, user = 0;
            var debugged = 0;
            if (pid == 0 ||
                StartupGetProcessTimes(process, &born, &exited, &kernel, &user) == 0 ||
                StartupCheckRemoteDebuggerPresent(process, &debugged) == 0)
                throw NativeFailure();
            return new(pid, born, _powerShellImageMatched, debugged == 1);
        }

        public uint WaitForStartupTrace(CancellationToken cancellationToken)
        {
            Require(ChildState.Running);
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(120))
            {
                cancellationToken.ThrowIfCancellationRequested();
                _powerShellIo?.Check();
                var result = WaitForOwnedObject(Handle(_process), 100);
                if (result == 0)
                    return ReadExit();
                if (result != 258)
                    throw WaitFailure(result);
            }
            throw RestrictedOwnerToken.Failure(1460);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "IsDebuggerPresent")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int StartupIsDebuggerPresent();

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessId", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint StartupGetProcessId(SafeChildHandle process);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessTimes", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int StartupGetProcessTimes(
        SafeChildHandle process, long* created, long* exited, long* kernel, long* user);

    [LibraryImport("kernel32.dll", EntryPoint = "CheckRemoteDebuggerPresent",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int StartupCheckRemoteDebuggerPresent(
        SafeChildHandle process, int* present);
}
