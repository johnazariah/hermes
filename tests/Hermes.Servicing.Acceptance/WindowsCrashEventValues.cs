using System.Runtime.InteropServices;

namespace Hermes.Servicing.Acceptance;

// Fixed Application Error 1000/v0 selective-values adapter only.
// Provider/schema admission and event-handle creation belong to a future caller.
// Sources: Microsoft winevt.h (EVT_VARIANT and render declarations), and:
// https://learn.microsoft.com/en-us/windows/win32/api/winevt/nf-winevt-evtcreaterendercontext
// https://learn.microsoft.com/en-us/windows/win32/api/winevt/nf-winevt-evtrender
// https://learn.microsoft.com/en-us/windows/win32/api/winevt/ns-winevt-evt_variant
// https://learn.microsoft.com/en-us/windows/win32/api/winevt/ne-winevt-evt_variant_type
// https://learn.microsoft.com/en-us/windows/win32/wes/rendering-events
internal static class WindowsCrashEventValues
{
    // Caller retains ownership of borrowedEvent and must keep it valid throughout.
    // No SafeHandle alias, EvtClose(event), foreign lookup or asynchronous work.
    internal static CrashEventRead Read(
        nint borrowedEvent, CrashEventSubject expected)
    {
        if (!OperatingSystem.IsWindows())
            return CrashEventValues.Failure(
                CrashEventFailureKind.Unavailable, CrashEventReason.UnsupportedPlatform);
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return CrashEventValues.Failure(
                CrashEventFailureKind.Refused, CrashEventReason.UnsupportedAbi);
        return CrashEventValues.Read(new Api(), borrowedEvent, expected);
    }

    [StructLayout(LayoutKind.Explicit)]
    private readonly struct VariantUnion
    {
        [FieldOffset(0)] internal readonly uint UInt32;
        [FieldOffset(0)] internal readonly ulong UInt64;
        [FieldOffset(0)] internal readonly nint String;

        internal VariantUnion(ulong value)
        {
            UInt32 = 0;
            String = 0;
            UInt64 = value;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Variant
    {
        internal readonly VariantUnion Value;
        internal readonly uint Count;
        internal readonly uint Type;

        internal Variant(VariantUnion value, uint count, uint type) =>
            (Value, Count, Type) = (value, count, type);
    }

    private sealed class Api : ICrashEventApi
    {
        public CrashEventAbi Abi => new(
            IntPtr.Size, Marshal.SizeOf<VariantUnion>(),
            Marshal.OffsetOf<Variant>(nameof(Variant.Count)).ToInt32(),
            Marshal.OffsetOf<Variant>(nameof(Variant.Type)).ToInt32(),
            Marshal.SizeOf<Variant>(), BitConverter.IsLittleEndian,
            Marshal.OffsetOf<Variant>(nameof(Variant.Value)) == IntPtr.Zero &&
            Marshal.OffsetOf<VariantUnion>(nameof(VariantUnion.UInt32)) == IntPtr.Zero &&
            Marshal.OffsetOf<VariantUnion>(nameof(VariantUnion.UInt64)) == IntPtr.Zero &&
            Marshal.OffsetOf<VariantUnion>(nameof(VariantUnion.String)) == IntPtr.Zero);

        public nint CreateContext(IReadOnlyList<string> valuePaths) =>
            EvtCreateRenderContext(checked((uint)valuePaths.Count), valuePaths.ToArray(), 0);

        public CrashEventRenderCall Render(
            nint context, nint borrowedEvent, ICrashEventAllocation? allocation)
        {
            // EvtRenderEventValues == 0. Never XML (1) or bookmark (2).
            var success = EvtRender(context, borrowedEvent, 0,
                checked((uint)(allocation?.Capacity ?? 0)),
                allocation is { } buffer ? unchecked((nint)buffer.Address) : 0,
                out var used, out var count);
            var error = success ? 0 : Marshal.GetLastPInvokeError();
            return new(success, error, used, count);
        }

        public ICrashEventAllocation Allocate(int capacity) => new Allocation(capacity);

        public bool Close(nint context) => EvtClose(context);
    }

    private sealed class Allocation(int capacity) : ICrashEventAllocation
    {
        private nint _pointer = Marshal.AllocHGlobal(capacity);
        private byte[] _snapshot = [];

        public ulong Address => unchecked((ulong)_pointer);
        public int Capacity { get; } = capacity;

        public ReadOnlyMemory<byte> Snapshot(int usedBytes)
        {
            ObjectDisposedException.ThrowIf(_pointer == 0, this);
            ArgumentOutOfRangeException.ThrowIfNegative(usedBytes);
            if (usedBytes > Capacity)
                throw CrashEventValues.Refuse(CrashEventReason.BufferSize);
            _snapshot = new byte[usedBytes];
            // The policy already validated this owned allocation and used range.
            // Selected string pointers are decoded from the snapshot, never read
            // directly or individually freed.
            Marshal.Copy(_pointer, _snapshot, 0, usedBytes);
            return _snapshot;
        }

        public void Dispose()
        {
            var pointer = _pointer;
            _pointer = 0;
            Array.Clear(_snapshot);
            _snapshot = [];
            if (pointer != 0)
                Marshal.FreeHGlobal(pointer);
        }
    }

    // EvtRenderContextValues == 0; both contexts always have three fixed paths.
    [DllImport("wevtapi.dll", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint EvtCreateRenderContext(
        uint valuePathsCount,
        [In, MarshalAs(UnmanagedType.LPArray,
            ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 0)]
        string[] valuePaths,
        uint flags);

    [DllImport("wevtapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtRender(
        nint context, nint fragment, uint flags, uint bufferSize, nint buffer,
        out uint bufferUsed, out uint propertyCount);

    [DllImport("wevtapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtClose(nint context);
}
