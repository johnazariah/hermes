using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace Hermes.Setup;

[SupportedOSPlatform("windows")]
internal sealed class WindowsShellLinkQuery : IShellLinkQuery
{
    public ReadObservation<ShortcutFields> Read(string knownPath) =>
        new ShellLinkReader(new WindowsPathIdentityQuery(),
            new PhysicalSetupFileSystem(), new WindowsShellLinkLoader()).Read(knownPath);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsShellLinkLoader : IShellLinkLoader
{
    public ShortcutFields Load(string path)
    {
        var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true)
            ?? throw new COMException("ShellLink activation is unavailable.");
        var instance = Activator.CreateInstance(type)
            ?? throw new COMException("ShellLink activation returned no object.");
        try { return ReadFields(instance, path); }
        finally { Marshal.FinalReleaseComObject(instance); }
    }

    private static ShortcutFields ReadFields(object instance, string path)
    {
        if (instance is not IWindowsShellLink link || instance is not IPersistFile file)
            throw new InvalidCastException("The ShellLink COM interfaces are unavailable.");
        file.Load(path, 0);
        var target = new StringBuilder(32_768);
        var arguments = new StringBuilder(32_768);
        var directory = new StringBuilder(32_768);
        link.GetPath(target, target.Capacity, 0, 4); // SLGP_RAWPATH; never Resolve.
        link.GetArguments(arguments, arguments.Capacity);
        link.GetWorkingDirectory(directory, directory.Capacity);
        return new(target.ToString(), arguments.ToString(), directory.ToString());
    }
}

// Complete IShellLinkW vtable. Only the three getters above are called.
[ComImport, Guid("000214F9-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IWindowsShellLink
{
    void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, nint findData, uint flags);
    void GetIDList([Out] nint[] list);
    void SetIDList(nint list);
    void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
    void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
    void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string text);
    void GetHotkey([Out] short[] hotkey);
    void SetHotkey(short hotkey);
    void GetShowCmd([Out] int[] command);
    void SetShowCmd(int command);
    void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, [Out] int[] index);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
    void Resolve(nint window, uint flags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
}
