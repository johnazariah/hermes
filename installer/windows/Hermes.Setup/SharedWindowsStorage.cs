using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hermes.Installer.Control;
using Hermes.Servicing;

namespace Hermes.Setup;

[SupportedOSPlatform("windows")]
internal static class WindowsSetupScope
{
    internal static UserInstallationScope ForCurrentUser()
    {
        var scope = UserInstallationScope.FromKnownFolder(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        FixedInstallRoot.ForCurrentUser().Require(scope.AppDirectory);
        return scope;
    }
}

internal sealed class SharedSetupGateCodec : ISetupGateCodec
{
    public DiskGateSnapshot Read(FileStream stream)
    {
        if (stream.Length == 0)
            return new(GateDisposition.Missing, null);
        try { return new(GateDisposition.Ready, GateCodec.ReadReady(stream)); }
        catch (AdmissionException error) when (error.Reason == RefusalReason.Closed)
        { return new(GateDisposition.Closed, null); }
        catch (AdmissionException)
        { throw new SetupIoException(SetupCode.InvalidEvidence); }
    }

    public void Close(FileStream stream) => GateCodec.Close(stream);

    public void Publish(FileStream stream, GateReadyState ready) =>
        GateCodec.PublishReady(stream, ready);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsSetupPathSafety : ISetupPathSafety
{
    private readonly WindowsPhysicalPaths _physical = new();

    public void RequireDirectory(string path) =>
        RequireSame(RuntimePaths.Canonical(path), _physical.Directory(path));

    public void RequireFile(string path) =>
        RequireSame(RuntimePaths.Canonical(path), _physical.Image(path));

    private static void RequireSame(string expected, string actual) =>
        SetupIo.Require(string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase),
            SetupCode.OwnershipRefused);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsAtomicFileCommit : IAtomicFileCommit
{
    public void Commit(string temporary, string destination, bool overwrite)
    {
        if (!Move(temporary, destination, 8u | (overwrite ? 1u : 0u)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Move(string existing, string replacement, uint flags);
}
