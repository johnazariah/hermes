using System.Buffers.Binary;
using System.ComponentModel;

namespace Hermes.Servicing.Acceptance;

internal sealed unsafe partial class WindowsRestrictedOwnerTokens
    : IAcceptanceDesktopAccess
{
    // Only existing parent objects are read. No reopening, ACL mutation,
    // impersonation assignment, privilege acquisition, or child launch.
    AcceptanceDesktopAccessResult IAcceptanceDesktopAccess.Read(
        IAcceptanceRestrictedToken token)
    {
        RequireDisposableJob();
        var probe = DuplicateDesktopProbe(FilteredHandle(token));
        return ReadOwnedDesktopProbe(probe);
    }

    private static SafeDesktopProbeToken DuplicateDesktopProbe(
        SafeAccessTokenHandle original)
    {
        nint raw = 0;
        // TOKEN_QUERY only; SecurityImpersonation; TokenImpersonation.
        // The duplicate is never assigned to a thread or used for creation.
        if (DuplicateDesktopProbeToken(original, 0x0008, 0, 2, 2, &raw) == 0)
            throw NativeFailure();
        var probe = new SafeDesktopProbeToken(raw);
        if (!probe.IsInvalid)
            return probe;
        probe.Dispose();
        throw RestrictedOwnerToken.Failure(13);
    }

    private static AcceptanceDesktopAccessResult ReadOwnedDesktopProbe(
        SafeDesktopProbeToken probe)
    {
        AcceptanceDesktopAccessResult result;
        try { result = ReadDesktopProbe(probe); }
        catch (Exception original)
        {
            try { CloseDesktopProbe(probe); }
            catch (Exception cleanup)
            {
                throw new AggregateException(
                    "Acceptance access probe failed; cleanup incomplete.", original, cleanup);
            }
            throw;
        }
        CloseDesktopProbe(probe);
        return result with { ProbeClosed = probe.IsClosed };
    }

    private static void CloseDesktopProbe(SafeDesktopProbeToken probe) =>
        AcceptanceChildCleanup.Complete(probe.CloseChecked);

    private static AcceptanceDesktopAccessResult ReadDesktopProbe(
        SafeDesktopProbeToken probe)
    {
        var facts = ReadDesktopProbeFacts(probe);
        var level = ReadInt(probe, 9);
        AcceptanceDesktopDacl.RequireProbe(facts, level);
        var station = GetProcessWindowStation();
        if (station == 0)
            throw NativeFailure();
        var desktop = GetThreadDesktop(GetCurrentThreadId());
        if (desktop == 0)
            throw NativeFailure();
        var flags = ReadDesktopStationFlags(station);
        return ObserveDesktopObjects(probe, station, desktop, facts, level, flags);
    }

    private static AcceptanceDesktopAccessResult ObserveDesktopObjects(
        SafeDesktopProbeToken probe, nint station, nint desktop,
        RestrictedOwnerTokenFacts facts, int level, uint flags)
    {
        var interactive = (flags & 1) != 0;
        return new(
            CheckDesktopObject(probe, station, AcceptanceDesktopObject.Station, interactive),
            CheckDesktopObject(probe, desktop, AcceptanceDesktopObject.Desktop, interactive),
            facts, level, flags, false)
        {
            StationLabel = AcceptanceDesktopMandatoryLabel.Read(
                new BorrowedMandatoryLabelQuery(station)),
            DesktopLabel = AcceptanceDesktopMandatoryLabel.Read(
                new BorrowedMandatoryLabelQuery(desktop))
        };
    }

    private static RestrictedOwnerTokenFacts ReadDesktopProbeFacts(
        SafeDesktopProbeToken probe)
    {
        using var source = Instance.OpenCurrentPrimary();
        var current = SourceHandle(source);
        var (enabled, denyOnly) = ReadAdministrator(probe);
        return new(ReadInt(probe, TypeInformation),
            ReadInt(probe, ElevationInformation), ReadIntegrity(probe),
            enabled, denyOnly, SameOwner(probe, current), SameSession(probe, current));
    }

    private static uint ReadDesktopStationFlags(nint borrowed)
    {
        var flags = default(DesktopUserObjectFlags);
        uint bytes = 0;
        if (GetUserObjectInformationW(borrowed, 1, (char*)(&flags),
            (uint)sizeof(DesktopUserObjectFlags), &bytes) == 0)
            throw NativeFailure();
        if (bytes != sizeof(DesktopUserObjectFlags) || (flags.Flags & ~1u) != 0)
            throw RestrictedOwnerToken.Failure(13);
        return flags.Flags;
    }

    private static AcceptanceDaclObservation CheckDesktopObject(
        SafeDesktopProbeToken probe, nint borrowed,
        AcceptanceDesktopObject target, bool interactive)
    {
        try
        {
            var descriptor = ReadDesktopDescriptor(borrowed);
            var concreteOnly = target == AcceptanceDesktopObject.Desktop && !interactive;
            var normalized = AcceptanceDesktopDacl.NormalizeDescriptor(
                descriptor, concreteOnly);
            return CheckDesktopDescriptor(probe, normalized, target,
                AcceptanceDesktopDacl.Mapping(target, interactive));
        }
        catch (Win32Exception error)
        {
            return AcceptanceDaclObservation.Unobserved(error.NativeErrorCode);
        }
    }

    private static byte[] ReadDesktopDescriptor(nint borrowed)
    {
        var bytes = new byte[RequiredDesktopDescriptorBytes(borrowed)];
        uint information = AcceptanceDesktopDacl.DescriptorScope;
        uint actual = 0;
        fixed (byte* address = bytes)
        {
            if (GetDesktopObjectSecurity(borrowed, &information,
                address, (uint)bytes.Length, &actual) == 0)
                throw NativeFailure();
        }
        if (actual != (uint)bytes.Length)
            throw RestrictedOwnerToken.Failure(13);
        return bytes;
    }

    private static int RequiredDesktopDescriptorBytes(nint borrowed)
    {
        uint information = AcceptanceDesktopDacl.DescriptorScope;
        uint bytes = 0;
        var result = GetDesktopObjectSecurity(borrowed, &information, null, 0, &bytes);
        var error = Marshal.GetLastPInvokeError();
        if (result != 0 || error != 122 ||
            bytes is < 20 or > AcceptanceDesktopDacl.DescriptorLimit)
            throw RestrictedOwnerToken.Failure(result == 0 && error != 122 ? error : 13);
        return checked((int)bytes);
    }

    private static AcceptanceDaclObservation CheckDesktopDescriptor(
        SafeDesktopProbeToken probe, byte[] descriptor,
        AcceptanceDesktopObject target, AcceptanceDesktopGenericMapping mapping)
    {
        Span<byte> privileges = stackalloc byte[AcceptanceDesktopDacl.PrivilegeCapacity];
        privileges.Clear();
        uint bytes = AcceptanceDesktopDacl.PrivilegeCapacity;
        uint granted = 0;
        var status = 0;
        fixed (byte* security = descriptor)
        fixed (byte* used = privileges)
        {
            var success = CheckDesktopDacl(security, probe,
                AcceptanceDesktopDacl.MaximumAllowed, &mapping, used, &bytes, &granted, &status);
            var error = Marshal.GetLastPInvokeError();
            var reply = new AcceptanceAccessReply(success, status, granted, error,
                bytes, BinaryPrimitives.ReadUInt32LittleEndian(privileges));
            return AcceptanceDesktopDacl.Decide(reply, AcceptanceDesktopDacl.ValidMask(target));
        }
    }

    // These remain existing parent USER objects, not child process/thread objects.
    // Query under the existing caller context; never assign the probe token.
    // The borrowed handle is neither reopened nor closed.
    private sealed class BorrowedMandatoryLabelQuery(nint borrowed)
        : IAcceptanceMandatoryLabelQuery
    {
        public AcceptanceLabelQueryReply Query(Span<byte> buffer)
        {
            Instance.RequireDisposableJob();
            uint information = AcceptanceDesktopMandatoryLabel.DescriptorScope;
            uint needed = 0;
            fixed (byte* address = buffer)
            {
                var succeeded = GetDesktopObjectSecurity(
                    borrowed, &information, address, (uint)buffer.Length, &needed);
                var error = Marshal.GetLastPInvokeError();
                return new(succeeded, needed, error);
            }
        }
    }

    private sealed class SafeDesktopProbeToken : SafeHandleZeroOrMinusOneIsInvalid
    {
        private int _closeError;

        internal SafeDesktopProbeToken(nint value) : base(ownsHandle: true) =>
            SetHandle(value);

        internal void CloseChecked()
        {
            Dispose();
            if (_closeError != 0)
                throw RestrictedOwnerToken.Failure(_closeError);
        }

        protected override bool ReleaseHandle()
        {
            if (CloseOwnedHandle(handle) != 0)
                return true;
            var error = Marshal.GetLastPInvokeError();
            _closeError = error > 0 ? error : 13;
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DesktopUserObjectFlags(
        int Inherit, int Reserved, uint Flags);

    [LibraryImport("advapi32.dll", EntryPoint = "DuplicateTokenEx", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DuplicateDesktopProbeToken(
        SafeAccessTokenHandle original, uint access, nint attributes,
        int impersonationLevel, int tokenType, nint* duplicate);

    [LibraryImport("user32.dll", EntryPoint = "GetUserObjectSecurity", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetDesktopObjectSecurity(
        nint borrowed, uint* information, byte* descriptor, uint length, uint* needed);

    [LibraryImport("advapi32.dll", EntryPoint = "AccessCheck", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CheckDesktopDacl(
        byte* descriptor, SafeDesktopProbeToken probe, uint desired,
        AcceptanceDesktopGenericMapping* mapping, byte* privileges,
        uint* privilegeLength, uint* granted, int* status);
}
