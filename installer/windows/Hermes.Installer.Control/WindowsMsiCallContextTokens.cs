using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Hermes.Installer.Control;

[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WindowsMsiCallContextTokens : IMsiCallContextTokens
{
    private const uint QueryAccess = 0x0008;
    private const uint DuplicateAccess = 0x0002;
    private const int ElevationTypeInformation = 18;
    private const int LinkedTokenInformation = 19;
    private const int ElevationInformation = 20;
    private const int IntegrityInformation = 25;
    private const int NoToken = 1008;
    private const int NoSuchLogonSession = 1312;
    private const int InsufficientBuffer = 122;
    private const int InvalidData = 13;

    internal static WindowsMsiCallContextTokens Instance { get; } = new();

    public IMsiCallContextToken OpenProcess()
    {
        nint token = 0;
        if (OpenProcessToken(GetCurrentProcess(), QueryAccess | DuplicateAccess, &token) == 0)
            throw Error();
        return new Token(new SafeAccessTokenHandle(token));
    }

    public MsiContextRead ReadThreadToken()
    {
        nint token = 0;
        if (OpenThreadToken(GetCurrentThread(), QueryAccess, 1, &token) == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            return error == NoToken
                ? MsiContextRead.Absent : throw new Win32Exception(error);
        }
        using var owned = new SafeAccessTokenHandle(token);
        if (owned.IsInvalid)
            throw new Win32Exception(InvalidData);
        return MsiContextRead.Observed;
    }

    private sealed class Token(SafeAccessTokenHandle handle) : IMsiCallContextToken
    {
        public (int Elevated, int ElevationType, int Integrity, int Admin) ReadPrimary()
        {
            var elevated = ReadInt(handle, ElevationInformation);
            var type = ReadInt(handle, ElevationTypeInformation);
            var integrity = ReadIntegrity(handle);
            if (elevated is not (0 or 1) || type is < 1 or > 3 ||
                integrity is < 0 or > 65535)
                throw new Win32Exception(InvalidData);
            using var identity = new WindowsIdentity(handle.DangerousGetHandle());
            var admin = new WindowsPrincipal(identity)
                .IsInRole(WindowsBuiltInRole.Administrator);
            return (elevated, type, integrity, admin ? 1 : 0);
        }

        public (MsiContextRead State, int SameOwner, int Elevated) ReadLinked()
        {
            nint linked = 0;
            uint length = 0;
            if (GetTokenInformation(handle, LinkedTokenInformation,
                (nint)(&linked), (uint)sizeof(nint), &length) == 0)
                return MissingLinked(Marshal.GetLastPInvokeError());
            using var owned = new SafeAccessTokenHandle(linked);
            if (owned.IsInvalid || length != sizeof(nint))
                throw new Win32Exception(InvalidData);
            var elevated = ReadInt(owned, ElevationInformation);
            if (elevated is not (0 or 1))
                throw new Win32Exception(InvalidData);
            return (MsiContextRead.Observed, SameOwner(handle, owned), elevated);
        }

        public void Dispose() => handle.Dispose();
    }

    private static (MsiContextRead State, int SameOwner, int Elevated) MissingLinked(int error) =>
        error == NoSuchLogonSession
            ? (MsiContextRead.Absent, -1, -1)
            : throw new Win32Exception(error);

    private static int SameOwner(SafeAccessTokenHandle process, SafeAccessTokenHandle linked)
    {
        using var owner = new WindowsIdentity(process.DangerousGetHandle());
        using var other = new WindowsIdentity(linked.DangerousGetHandle());
        return (owner.User, other.User) switch
        {
            ({ } left, { } right) => left.Equals(right) ? 1 : 0,
            _ => throw new Win32Exception(InvalidData)
        };
    }

    private static int ReadInt(SafeAccessTokenHandle token, int information)
    {
        var value = 0;
        uint length = 0;
        if (GetTokenInformation(token, information,
            (nint)(&value), sizeof(int), &length) == 0)
            throw Error();
        return length == sizeof(int) ? value : throw new Win32Exception(InvalidData);
    }

    private static int ReadIntegrity(SafeAccessTokenHandle token)
    {
        uint length = 0;
        var result = GetTokenInformation(token, IntegrityInformation, 0, 0, &length);
        var error = Marshal.GetLastPInvokeError();
        if (result != 0 || error != InsufficientBuffer || length is < 12 or > 1024)
            throw new Win32Exception(result == 0 && error != InsufficientBuffer ? error : InvalidData);
        var buffer = Marshal.AllocHGlobal(checked((int)length));
        try
        {
            if (GetTokenInformation(token, IntegrityInformation, buffer, length, &length) == 0)
                throw Error();
            return IntegrityRid(buffer, length);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static int IntegrityRid(nint buffer, uint length)
    {
        var sid = Marshal.ReadIntPtr(buffer);
        var offset = sid.ToInt64() - buffer.ToInt64();
        if (length < 12 || offset < 0 || offset > (long)length - 12)
            throw new Win32Exception(InvalidData);
        // TOKEN_MANDATORY_LABEL uses S-1-16-RID. Inspect it in the owned
        // native buffer without constructing or emitting a SID string.
        if (Marshal.ReadByte(sid) != 1 || Marshal.ReadByte(sid, 1) != 1 ||
            Enumerable.Range(2, 5).Any(index => Marshal.ReadByte(sid, index) != 0) ||
            Marshal.ReadByte(sid, 7) != 16)
            throw new Win32Exception(InvalidData);
        return Marshal.ReadInt32(sid, 8);
    }

    private static Win32Exception Error() => new(Marshal.GetLastPInvokeError());

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int OpenProcessToken(nint process, uint access, nint* token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int OpenThreadToken(nint thread, uint access, int openAsSelf, nint* token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int GetTokenInformation(
        SafeAccessTokenHandle token, int information, nint buffer, uint length, uint* needed);
}
