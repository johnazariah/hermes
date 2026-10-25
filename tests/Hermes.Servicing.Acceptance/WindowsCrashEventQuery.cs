using System.Runtime.InteropServices;

namespace Hermes.Servicing.Acceptance;

// Uninvoked native adapter for the fixed portable policy.
// Provider/schema admission is an explicit caller precondition, not verified here.
// Native UInt64 server-filter precision remains UNPROVEN.
//
// Sources: official SDK winevt.h EVT_QUERY_FLAGS / EvtQuery / EvtNext / EvtClose;
// https://learn.microsoft.com/en-us/windows/win32/api/winevt/nf-winevt-evtquery
// https://learn.microsoft.com/en-us/windows/win32/api/winevt/nf-winevt-evtnext
// https://learn.microsoft.com/en-us/windows/win32/api/winevt/nf-winevt-evtclose
// https://learn.microsoft.com/en-us/windows/win32/wes/consuming-events
//
// A future invoker MUST run within a deadline-controlled worker in a private job.
// No Task abandonment, async continuation, subscription, or cancellation claim.
internal static class WindowsCrashEventQuery
{
    internal static CrashQueryResult EnumeratePresence(
        CrashQueryRequest request, CrashQueryAdmission admission,
        CrashEventTelemetry? telemetry = null)
    {
        if (!OperatingSystem.IsWindows())
            return CrashEventQuery.Failure(
                CrashEventFailureKind.Unavailable, CrashQueryReason.UnsupportedPlatform);
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return CrashEventQuery.Failure(
                CrashEventFailureKind.Refused, CrashQueryReason.UnsupportedAbi);
        return CrashEventQuery.EnumeratePresence(
            new PresenceApi(), request, admission, telemetry);
    }

    internal static CrashQueryResult EnumerateCompletedCandidate(
        CrashQueryRequest request, CrashQueryAdmission admission,
        CrashEventTelemetry? telemetry = null)
    {
        if (!OperatingSystem.IsWindows())
            return CrashEventQuery.Failure(
                CrashEventFailureKind.Unavailable, CrashQueryReason.UnsupportedPlatform);
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return CrashEventQuery.Failure(
                CrashEventFailureKind.Refused, CrashQueryReason.UnsupportedAbi);
        return CrashEventQuery.EnumerateCompletedCandidate(
            new Api(), request, admission, telemetry);
    }

    internal static CrashQueryResult EnumerateCandidate(
        CrashQueryRequest request, CrashQueryAdmission admission)
    {
        if (!OperatingSystem.IsWindows())
            return CrashEventQuery.Failure(
                CrashEventFailureKind.Unavailable, CrashQueryReason.UnsupportedPlatform);
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return CrashEventQuery.Failure(
                CrashEventFailureKind.Refused, CrashQueryReason.UnsupportedAbi);
        return CrashEventQuery.EnumerateCandidate(new Api(), request, admission);
    }

    private abstract class Transport : ICrashEventQueryTransport
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;

        public CrashQueryOpenCall Open(
            nint session, string channel, string expression, uint flags)
        {
            RequireThread();
            var handle = EvtQuery(session, channel, expression, flags);
            var error = handle == 0 ? Marshal.GetLastPInvokeError() : 0;
            return new(handle, error);
        }

        public CrashQueryNextCall Next(
            nint query, nint[] events, uint timeoutMilliseconds, uint flags)
        {
            RequireThread();
            var success = EvtNext(query, checked((uint)events.Length), events,
                timeoutMilliseconds, flags, out var returned);
            var error = success ? 0 : Marshal.GetLastPInvokeError();
            return new(success, error, returned);
        }

        public CrashQueryCloseCall Close(nint ownedHandle)
        {
            RequireThread();
            var success = EvtClose(ownedHandle);
            var error = success ? 0 : Marshal.GetLastPInvokeError();
            return new(success, error);
        }

        protected void RequireThread()
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("Crash-event query thread changed.");
        }
    }

    private sealed class Api : Transport, ICrashEventQueryApi
    {
        public CrashEventRead Read(nint borrowedEvent, CrashEventSubject expected)
        {
            RequireThread();
            // Both query and event remain owned by the portable lifetime.
            return WindowsCrashEventValues.Read(borrowedEvent, expected);
        }
    }

    private sealed class PresenceApi : Transport, ICrashEventPresenceQueryApi
    {
        public CrashIdentityRead ReadIdentity(
            nint borrowedEvent, CrashEventSubject expected)
        {
            RequireThread();
            // This adapter exposes no details-reader method.
            return WindowsCrashEventValues.ReadIdentity(borrowedEvent, expected);
        }
    }

    [DllImport("wevtapi.dll", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint EvtQuery(
        nint session, string path, string query, uint flags);

    [DllImport("wevtapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtNext(
        nint resultSet, uint eventsSize,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] nint[] events,
        uint timeout, uint flags, out uint returned);

    [DllImport("wevtapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtClose(nint handle);
}
