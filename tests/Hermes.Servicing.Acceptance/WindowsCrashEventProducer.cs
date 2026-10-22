namespace Hermes.Servicing.Acceptance;

internal static partial class WindowsCrashControl
{
    internal static CrashEventChildCommandEffects EventChildEffects => new(
        RequireEventContext, RunEventChildAsync);

    private static void StartEvent(
        Action<CrashOwnedChild> adopt, CancellationToken cancellationToken) =>
        StartWithInfo(CreateEventStartInfo, adopt, cancellationToken);

    private static ProcessStartInfo CreateEventStartInfo(string image) =>
        CrashEventLaunch.CreateStartInfo(new(
            RequireEventContext,
            () => CrashEventLaunch.ReadContext(
                OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable),
            () => CreateStartInfo(image)));

    private static async Task<int> RunEventChildAsync(CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, cancellationToken);
        using var input = new SafeFileHandle(GetStdHandle(-10), ownsHandle: false);
        using var output = Console.OpenStandardOutput();
        if (input.IsInvalid)
            return CrashControl.TransportRefused;
        return await CrashEventChild.RunAsync(new(
            new(memory => ReadPipe(input, memory)),
            token => CrashWire.WriteReadyAsync(output, token),
            WerSetFlags, ReadWerFlags, RaiseFailFastException), linked.Token);
    }

    // Event-only controlled producer, not proof of the managed FailFast route.
    // .NET 9 managed FailFast best-effort ClrReportEvent uses .NET Runtime 1025:
    // https://raw.githubusercontent.com/dotnet/runtime/v9.0.0/src/coreclr/vm/eventreporter.cpp
    // This Win32 API invokes WER if enabled; it does not guarantee AppError 1000.
    // Delivery/precision remain unproven until the unchanged exact/adjacent query
    // succeeds. No producer-success fact or alternate acceptance is introduced.
    // ABI: VOID WINAPI (PEXCEPTION_RECORD optional, PCONTEXT optional, DWORD).
    // https://learn.microsoft.com/en-us/windows/win32/api/errhandlingapi/nf-errhandlingapi-raisefailfastexception
    [LibraryImport("kernel32.dll", EntryPoint = "RaiseFailFastException")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void RaiseFailFastException(
        nint exceptionRecord, nint context, uint flags);
}
