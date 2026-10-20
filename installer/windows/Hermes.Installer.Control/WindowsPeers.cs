using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Hermes.Installer.Control;

[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsPeers
{
    internal static ProcessInstance Current()
    {
        using var process = Process.GetCurrentProcess();
        return Identity(process);
    }

    internal static ProcessInstance Server(NamedPipeClientStream pipe)
    {
        uint id = 0;
        if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, &id) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return Read(id);
    }

    internal static ProcessInstance Client(NamedPipeServerStream pipe)
    {
        uint id = 0;
        if (GetNamedPipeClientProcessId(pipe.SafePipeHandle, &id) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return Read(id);
    }

    internal static string CurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User is { } user
            ? user.Value : throw new InvalidOperationException("The user SID is unavailable.");
    }

    internal static string ClientSid(NamedPipeServerStream pipe)
    {
        string? sid = null;
        pipe.RunAsClient(() => sid = CurrentSid());
        return sid ?? throw new InvalidOperationException("The pipe principal is unavailable.");
    }

    private static ProcessInstance Read(uint id)
    {
        using var process = Process.GetProcessById(checked((int)id));
        return Identity(process);
    }

    private static ProcessInstance Identity(Process process) =>
        new(checked((uint)process.Id), process.StartTime.ToUniversalTime().ToFileTimeUtc());

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetNamedPipeServerProcessId(SafePipeHandle pipe, uint* id);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetNamedPipeClientProcessId(SafePipeHandle pipe, uint* id);
}
