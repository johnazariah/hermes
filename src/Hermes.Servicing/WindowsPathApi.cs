using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Hermes.Servicing;

internal sealed record NativePathOpen(SafeFileHandle Handle, int Error);
internal sealed record NativePathInformation(bool Succeeded, byte[] Information, int Error);
internal sealed record NativePathFinal(uint Length, string Path, int Error);

internal interface IWindowsPathApi
{
    NativePathOpen Open(string path, uint flags);
    NativePathInformation Information(SafeFileHandle handle);
    NativePathFinal FinalPath(SafeFileHandle handle);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsPathApi : IWindowsPathApi
{
    internal const int MaximumPathCharacters = 32_768;
    private const string Kernel32 = "kernel32.dll";
    private const uint BackupSemantics = 0x02000000;

    public NativePathOpen Open(string path, uint flags)
    {
        var handle = OpenPath(path, 0, FileShare.ReadWrite | FileShare.Delete,
            0, FileMode.Open, BackupSemantics | flags, 0);
        var error = Marshal.GetLastPInvokeError();
        return new(handle, handle.IsInvalid ? error : 0);
    }

    public NativePathInformation Information(SafeFileHandle handle)
    {
        var information = new byte[24];
        var succeeded = ReadInformation(handle, 1, information, (uint)information.Length);
        var error = Marshal.GetLastPInvokeError();
        return new(succeeded, information, succeeded ? 0 : error);
    }

    public NativePathFinal FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(MaximumPathCharacters);
        var length = ReadFinalPath(handle, buffer, (uint)buffer.Capacity, 0);
        var error = Marshal.GetLastPInvokeError();
        return new(length, buffer.ToString(), length == 0 ? error : 0);
    }

    [DllImport(Kernel32, EntryPoint = "CreateFileW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle OpenPath(
        string path, uint desiredAccess, FileShare shareMode, nint securityAttributes,
        FileMode creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport(Kernel32, EntryPoint = "GetFileInformationByHandleEx",
        ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadInformation(
        SafeFileHandle handle, int informationClass, [Out] byte[] information, uint length);

    [DllImport(Kernel32, EntryPoint = "GetFinalPathNameByHandleW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint ReadFinalPath(
        SafeFileHandle handle, [Out] StringBuilder path, uint length, uint flags);
}
