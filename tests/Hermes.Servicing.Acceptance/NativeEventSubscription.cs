namespace Hermes.Servicing.Acceptance;

internal sealed unsafe class NativeEventSubscription : IDisposable
{
    private readonly EventWaitHandle _signal = new(false, EventResetMode.ManualReset);
    private readonly nint _subscription;
    private readonly nint _publisher;
    private int _disposed;

    internal NativeEventSubscription()
    {
        try
        {
            _publisher = EvtOpenPublisherMetadata(0, "MsiInstaller", null, 0x409, 0);
            if (_publisher == 0)
                throw Error("PublisherMetadata");
            _subscription = Subscribe();
        }
        catch
        {
            if (_publisher != 0)
                EvtClose(_publisher);
            _signal.Dispose();
            throw;
        }
    }

    private nint Subscribe()
    {
        const string query =
            "*[System[Provider[@Name='MsiInstaller'] or " +
            "Provider[@Name='Hermes.Servicing.Evidence.Native']]]";
        var subscription = EvtSubscribe(0, _signal.SafeWaitHandle.DangerousGetHandle(),
            "Application", query, 0, 0, 0, 0x10001);
        return subscription != 0 ? subscription : throw Error("Subscribe");
    }

    internal ImmutableArray<WireEvent> Drain()
    {
        var result = ImmutableArray.CreateBuilder<WireEvent>();
        while (result.Count < 256 && Next() is { } handle)
        {
            try
            {
                var xml = Render(handle);
                var message = NeedsMessage(xml) ? Format(handle) : null;
                result.Add(EngineXml.Parse(xml, message));
            }
            finally { EvtClose(handle); }
        }
        _signal.Reset();
        return result.ToImmutable();
    }

    private nint? Next()
    {
        nint handle = 0;
        uint returned = 0;
        if (EvtNext(_subscription, 1, &handle, 0, 0, &returned) != 0)
        {
            if (returned != 1 || handle == 0)
                throw new EvidenceGap("InvalidNativeEventBatch");
            return handle;
        }
        var code = Marshal.GetLastWin32Error();
        return code is 259 or 1460 ? null : throw Error("Next", code);
    }

    private static bool NeedsMessage(string xml) =>
        xml.Contains("Name=\"MsiInstaller\"", StringComparison.Ordinal) ||
        xml.Contains("Name='MsiInstaller'", StringComparison.Ordinal);

    private static string Render(nint handle)
    {
        uint bytes = 0;
        uint properties = 0;
        EvtRender(0, handle, 1, 0, null, &bytes, &properties);
        if (Marshal.GetLastWin32Error() != 122 || bytes is 0 or > 131072)
            throw Error("RenderSize");
        var buffer = new char[checked((int)(bytes / 2 + 1))];
        fixed (char* pointer = buffer)
        {
            if (EvtRender(0, handle, 1, bytes, pointer, &bytes, &properties) == 0)
                throw Error("Render");
            return new(pointer);
        }
    }

    private string Format(nint handle)
    {
        uint length = 0;
        EvtFormatMessage(_publisher, handle, 0, 0, 0, 1, 0, null, &length);
        if (Marshal.GetLastWin32Error() != 122 || length is 0 or > 32768)
            throw Error("FormatSize");
        var buffer = new char[checked((int)length + 1)];
        fixed (char* pointer = buffer)
        {
            if (EvtFormatMessage(_publisher, handle, 0, 0, 0, 1,
                (uint)buffer.Length, pointer, &length) == 0)
                throw Error("Format");
            return new(pointer);
        }
    }

    private static EvidenceGap Error(string operation, int? code = null) =>
        new($"NativeEvent:{operation}:{code ?? Marshal.GetLastWin32Error()}");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        EvtClose(_subscription);
        EvtClose(_publisher);
        _signal.Dispose();
    }

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint EvtSubscribe(nint session, nint signal, string channel,
        string query, nint bookmark, nint context, nint callback, uint flags);

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint EvtOpenPublisherMetadata(
        nint session, string publisher, string? logFile, uint locale, uint flags);

    [DllImport("wevtapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int EvtNext(
        nint resultSet, uint count, nint* events, uint timeout, uint flags, uint* returned);

    [DllImport("wevtapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int EvtRender(nint context, nint fragment, uint flags,
        uint bufferBytes, char* buffer, uint* usedBytes, uint* propertyCount);

    [DllImport("wevtapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int EvtFormatMessage(nint publisher, nint evt, uint messageId,
        uint valueCount, nint values, uint flags, uint bufferChars, char* buffer, uint* usedChars);

    [DllImport("wevtapi.dll", ExactSpelling = true)]
    private static extern int EvtClose(nint handle);
}
