using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hermes.Installer.Control;

[SupportedOSPlatform("windows")]
internal sealed unsafe class ExternalUiScope : IDisposable
{
    private readonly CallAuthority _authority;
    private readonly GCHandle _context;
    private readonly uint _previousUi;
    private int _disposed;

    private ExternalUiScope(CallAuthority authority, GCHandle context, uint previousUi) =>
        (_authority, _context, _previousUi) = (authority, context, previousUi);

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

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Callback(nint context, uint message, uint record)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is not CallAuthority authority)
                return 0;
            if ((message & 0xff000000) == MsiNative.InfoMessage)
                return FenceSignal.Decode(MsiNative.RecordText(record)) is { } signal &&
                    authority.Observe(signal) ? 1 : 0;
            authority.ObserveMsi(message, record);
            return 0;
        }
        catch { return 0; }
    }

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
