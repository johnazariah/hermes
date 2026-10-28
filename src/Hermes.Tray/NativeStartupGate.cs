using Hermes.Servicing;

namespace Hermes.Tray;

internal sealed record NativeStartupContext(Admission Evidence)
{
    internal string RequireService(string path) => Evidence switch
    {
        OutsideKnownInstallation => path,
        Admitted { Lease: IEnrolledServiceLease lease } => lease.SelectService(path) switch
        {
            ServiceSelected { ExecutablePath: { Length: > 0 } selected } => selected,
            _ => throw new InvalidOperationException("Hermes Service selection was refused.")
        },
        _ => throw new InvalidOperationException("Hermes Service selection requires startup admission.")
    };
}

// Linked into the BCL-only smoke helper; no WinForms or WebView dependency.
internal static class NativeStartupGate
{
    internal const int RefusedExitCode = 78;
    internal const string RefusalDiagnostic = "Hermes startup refused: servicing admission unavailable.";

    internal static IStartupAdmission ForCurrentProcess() =>
        OperatingSystem.IsWindows()
            ? WindowsStartupAdmission.ForCurrentUser()
            : throw new PlatformNotSupportedException("This entry requires Windows startup admission.");

    internal static int Run(
        Func<IStartupAdmission> create,
        Func<NativeStartupContext, int> dispatch,
        Action<string> diagnostic) =>
        Enter(create, diagnostic) is { } startup
            ? dispatch(startup) : RefusedExitCode;

    internal static async Task<int> RunAsync(
        Func<IStartupAdmission> create,
        Func<NativeStartupContext, Task<int>> dispatch,
        Action<string> diagnostic) =>
        Enter(create, diagnostic) is { } startup
            ? await dispatch(startup) : RefusedExitCode;

    internal static NativeStartupContext? Classify(Admission? admission) => admission switch
    {
        Admitted { Lease: not null } admitted => new(admitted),
        OutsideKnownInstallation outside => new(outside),
        _ => null
    };

    private static NativeStartupContext? Enter(
        Func<IStartupAdmission> create, Action<string> diagnostic)
    {
        NativeStartupContext? startup;
        try { startup = Classify(create().Enter(RuntimeEntry.Tray)); }
        catch (Exception) { startup = null; }
        if (startup is null)
            diagnostic(RefusalDiagnostic);
        return startup;
    }
}
