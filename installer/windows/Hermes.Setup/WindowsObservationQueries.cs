using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hermes.Servicing;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Hermes.Setup;

[SupportedOSPlatform("windows")]
internal sealed class WindowsPathIdentityQuery : IPathIdentityQuery
{
    private readonly WindowsPhysicalPaths _physical = new();

    public ReadObservation<PathIdentity> Read(string path)
    {
        try
        {
            var canonical = RuntimePaths.Canonical(path);
            return ReadObservation<PathIdentity>.Known(new(Resolve(canonical)));
        }
        catch (AdmissionException)
        { return ReadObservation<PathIdentity>.Unknown(ObservationFault.Identity); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception)
        { return ReadObservation<PathIdentity>.Unknown(ObservationFault.Identity, unchecked((uint)error.HResult)); }
    }

    private string Resolve(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.Directory)
                ? _physical.Directory(path) : _physical.Image(path);
        }
        catch (FileNotFoundException) { return _physical.Directory(path); }
        catch (DirectoryNotFoundException) { return _physical.Directory(path); }
    }
}

[SupportedOSPlatform("windows")]
internal static class WindowsPendingValueQuery
{
    internal static ReadObservation<RegistryBytes> Read(RegistryKey? key, string name)
    {
        if (key is null)
            return ReadObservation<RegistryBytes>.Unknown(ObservationFault.NativeQuery, 2);
        var bytes = new byte[1_048_576];
        uint[] type = [0];
        uint[] length = [(uint)bytes.Length];
        var code = Query(key.Handle, name, 0, type, bytes, length);
        if (code == 2)
            return ReadObservation<RegistryBytes>.Missing();
        if (code != 0 || length[0] > (uint)bytes.Length)
            return ReadObservation<RegistryBytes>.Unknown(ObservationFault.NativeQuery, code == 0 ? 234u : code);
        return ReadObservation<RegistryBytes>.Known(new(type[0],
            bytes.AsSpan(0, checked((int)length[0])).ToArray().ToImmutableArray()));
    }

    [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint Query(SafeRegistryHandle key, string name, nint reserved,
        [Out] uint[] type, [Out] byte[] bytes, [In, Out] uint[] length);
}
