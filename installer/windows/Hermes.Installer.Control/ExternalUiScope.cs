using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hermes.Installer.Control;

internal sealed unsafe class ExternalUiScope : IDisposable
{
    private const uint InfoMessage = 0x04000000;
    private readonly CallAuthority _authority;
    private readonly GCHandle _context;
    private readonly uint _previousUi;
    private int _disposed;

    [SupportedOSPlatform("windows")]
    private ExternalUiScope(CallAuthority authority, GCHandle context, uint previousUi) =>
        (_authority, _context, _previousUi) = (authority, context, previousUi);

    [SupportedOSPlatform("windows")]
    internal static ExternalUiScope Open(CallAuthority authority, bool quiet)
    {
        var context = GCHandle.Alloc(authority);
        var previousUi = MsiNative.SetInternalUi(quiet ? 2u : 3u, null);
        nint previousHandler = 0;
        var handler = (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, int>)&Callback;
        var result = MsiNative.SetExternalUi(handler,
            MsiNative.InfoFilter | authority.ObservationFilters,
            GCHandle.ToIntPtr(context), &previousHandler);
        if (result == 0 && previousHandler == 0)
            return new(authority, context, previousUi);
        authority.Fault();
        if (result != 0 || MsiNative.SetExternalUi(0, 0, 0, null) == 0)
            context.Free();
        MsiNative.SetInternalUi(previousUi, null);
        throw new Win32Exception(checked((int)(result == 0 ? 1603u : result)));
    }

    [SupportedOSPlatform("windows")]
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Callback(nint context, uint message, uint record)
    {
        try
        {
            return Dispatch(context, message, record,
                WindowsMsiFailureRecords.Instance, MsiNative.RecordText,
                static (authority, kind, value) => authority.ObserveMsi(kind, value));
        }
        catch { return 0; }
    }

    // Shared with boundary tests: context resolution, dispatch and catch are identical.
    internal static int Dispatch(
        nint context, uint message, uint record, IMsiFailureRecords records,
        Func<uint, string> readText,
        Action<CallAuthority, uint, uint> observeMsi)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is not CallAuthority authority)
                return 0;
            authority.ObserveMsiFailure(message, record, records);
            if ((message & 0xff000000) == InfoMessage)
                return FenceSignal.Decode(readText(record)) is { } signal &&
                    authority.Observe(signal) ? 1 : 0;
            observeMsi(authority, message, record);
            return 0;
        }
        catch { return 0; }
    }

    [SupportedOSPlatform("windows")]
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (MsiNative.SetExternalUi(0, 0, 0, null) == 0)
            _context.Free();
        else
            _authority.Fault();
        MsiNative.SetInternalUi(_previousUi, null);
    }
}
