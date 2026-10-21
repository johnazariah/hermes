using System.Globalization;

namespace Hermes.Servicing.Acceptance;

internal interface IWriterProcessQuery
{
    (SafeProcessHandle Handle, int Error) Open(
        uint access, int inheritHandle, uint processId);

    Stamp Identity(int processId);

    (uint Status, int Error) Wait(SafeProcessHandle handle);
}

internal static class ReadOnlyWriterObservation
{
    // SYNCHRONIZE | PROCESS_QUERY_INFORMATION | PROCESS_VM_READ.
    internal const uint Access = 0x00100000 | 0x00000400 | 0x00000010;

    internal static (Stamp Identity, NativeWriterBinding Binding) Observe(
        IWriterProcessQuery query, NativeWriterTracker tracker,
        long cursor, NativeReporter reporter, ProcessKey claimed)
    {
        if (reporter.ProcessId is 0 or > int.MaxValue)
            throw new EvidenceGap("UnsupportedNativeReporter");
        var opened = query.Open(Access, 0, reporter.ProcessId);
        using var pin = opened.Handle;
        if (pin.IsInvalid || pin.IsClosed)
            throw Failure(opened.Error);
        var actual = query.Identity(checked((int)reporter.ProcessId));
        if (!IsLive(query.Wait(pin)))
            throw new EvidenceGap("DiagnosticWriterObservationLost");
        var observed = new ProcessKey(
            checked((uint)actual.ProcessId), actual.StartUtcFileTime);
        var binding = tracker.Observe(cursor, reporter, claimed,
            observed, DateTime.UtcNow.ToFileTimeUtc());
        return (actual, binding);
    }

    private static bool IsLive((uint Status, int Error) result) =>
        result.Status switch
        {
            258 => true, // WAIT_TIMEOUT: the process has not exited.
            0 => false, // WAIT_OBJECT_0: the process has exited.
            uint.MaxValue => throw Failure(result.Error),
            _ => throw Failure(13)
        };

    private static EvidenceGap Failure(int error) =>
        new("WriterProcessReadFailed:" +
            (error > 0 ? error : 13).ToString(CultureInfo.InvariantCulture));
}

internal sealed partial class WindowsWriterProcessQuery : IWriterProcessQuery
{
    public (SafeProcessHandle Handle, int Error) Open(
        uint access, int inheritHandle, uint processId)
    {
        var handle = OpenProcess(access, inheritHandle, processId);
        return (handle, Marshal.GetLastPInvokeError());
    }

    public Stamp Identity(int processId)
    {
        // The caller holds the kernel pin across these fresh PID-based queries.
        // Preserve Files.Identity's loaded-module path, creation time and hash.
        using var process = Process.GetProcessById(processId);
        return Files.Identity(process);
    }

    public (uint Status, int Error) Wait(SafeProcessHandle handle)
    {
        var status = WaitForSingleObject(handle, 0);
        return (status, Marshal.GetLastPInvokeError());
    }

    [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeProcessHandle OpenProcess(
        uint access, int inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "WaitForSingleObject", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaitForSingleObject(
        SafeProcessHandle handle, uint milliseconds);
}
