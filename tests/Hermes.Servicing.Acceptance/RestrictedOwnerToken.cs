using System.ComponentModel;

namespace Hermes.Servicing.Acceptance;

[Flags]
internal enum AcceptanceTokenFilter : uint
{
    DisableMaxPrivilege = 0x1,
    LuaToken = 0x4
}

internal sealed record RestrictedOwnerTokenFacts(
    int TokenType,
    int Elevated,
    int Integrity,
    int AdminEnabled,
    int AdminDenyOnly,
    int SameOwner,
    int SameSession)
{
    internal static RestrictedOwnerTokenFacts Required { get; } =
        new(1, 0, 8192, 0, 1, 1, 1);

    // Numeric failed-predicate mask; no identity or handle enters diagnostics.
    internal int FailedPredicates =>
        (TokenType == 1 ? 0 : 1) |
        (Elevated == 0 ? 0 : 2) |
        (Integrity == 8192 ? 0 : 4) |
        (AdminEnabled == 0 ? 0 : 8) |
        (AdminDenyOnly == 1 ? 0 : 16) |
        (SameOwner == 1 ? 0 : 32) |
        (SameSession == 1 ? 0 : 64);

    internal void Require()
    {
        if (FailedPredicates != 0)
            throw new RestrictedOwnerTokenVerificationException(FailedPredicates);
    }
}

internal sealed class RestrictedOwnerTokenVerificationException(int failedPredicates)
    : Win32Exception(13, "Acceptance token verification refused.")
{
    internal int FailedPredicates { get; } = failedPredicates;
}

// Separate roles prevent the integrity setter from accepting the process token.
// Neither interface exposes a raw handle or impersonation capability.
internal interface IAcceptanceSourceToken : IDisposable
{
    bool IsClosed { get; }
}

internal interface IAcceptanceRestrictedToken : IDisposable
{
    bool IsClosed { get; }
}

internal interface IRestrictedOwnerTokens
{
    void RequireDisposableJob();
    MsiContextRead ReadThreadToken();
    IAcceptanceSourceToken OpenCurrentPrimary();
    IAcceptanceRestrictedToken Filter(
        IAcceptanceSourceToken source, AcceptanceTokenFilter flags);
    void SetMediumIntegrity(IAcceptanceRestrictedToken token);
    RestrictedOwnerTokenFacts ReadFacts(
        IAcceptanceRestrictedToken token, IAcceptanceSourceToken source);
}

internal sealed class RestrictedOwnerToken : IDisposable
{
    private readonly IRestrictedOwnerTokens _tokens;
    private readonly IAcceptanceRestrictedToken _token;

    private RestrictedOwnerToken(
        IRestrictedOwnerTokens tokens, IAcceptanceRestrictedToken token)
    {
        _tokens = tokens;
        _token = token;
    }

    internal bool IsClosed => _token.IsClosed;

    internal static RestrictedOwnerToken Create(IRestrictedOwnerTokens tokens)
    {
        tokens.RequireDisposableJob();
        RequireUnimpersonated(tokens);
        using var source = tokens.OpenCurrentPrimary();
        var token = tokens.Filter(source,
            AcceptanceTokenFilter.DisableMaxPrivilege | AcceptanceTokenFilter.LuaToken);
        try
        {
            tokens.SetMediumIntegrity(token);
            _ = ReadVerified(tokens, token, source);
            return new(tokens, token);
        }
        catch
        {
            token.Dispose();
            throw;
        }
    }

    internal RestrictedOwnerTokenFacts Verify()
    {
        if (_token.IsClosed)
            throw Failure(6);
        _tokens.RequireDisposableJob();
        RequireUnimpersonated(_tokens);
        using var source = _tokens.OpenCurrentPrimary();
        return ReadVerified(_tokens, _token, source);
    }

    // Test-only, synchronous borrow. No token or native handle is returned.
    internal void CreateSuspendedInertChild(IAcceptanceOwnedChild child)
    {
        _ = Verify();
        child.CreateSuspended(_token);
    }

    private static RestrictedOwnerTokenFacts ReadVerified(
        IRestrictedOwnerTokens tokens, IAcceptanceRestrictedToken token,
        IAcceptanceSourceToken source)
    {
        if (token.IsClosed)
            throw Failure(6);
        var facts = tokens.ReadFacts(token, source);
        facts.Require();
        RequireUnimpersonated(tokens);
        return facts;
    }

    private static void RequireUnimpersonated(IRestrictedOwnerTokens tokens)
    {
        if (tokens.ReadThreadToken() != MsiContextRead.Absent)
            throw Failure(13);
    }

    internal static Win32Exception Failure(int code) =>
        new(code > 0 ? code : 13, "Acceptance token operation refused.");

    public void Dispose() => _token.Dispose();
}

// Deliberately test-only. This file only creates and verifies tokens.
// The separate inert-child partial contains the bounded process-launch API.
// Neither partial acquires privileges, impersonates, or changes accounts,
// profiles, desktop ACLs, or host policy.
internal sealed unsafe partial class WindowsRestrictedOwnerTokens : IRestrictedOwnerTokens
{
    private const uint SourceAccess = 0x0001 | 0x0002 | 0x0008 | 0x0080;
    private const int UserInformation = 1;
    private const int GroupsInformation = 2;
    private const int TypeInformation = 8;
    private const int StatisticsInformation = 10;
    private const int SessionInformation = 12;
    private const int ElevationInformation = 20;
    private const int IntegrityInformation = 25;

    internal static WindowsRestrictedOwnerTokens Instance { get; } = new();

    private WindowsRestrictedOwnerTokens() { }

    private static ReadOnlySpan<byte> AdministratorSid =>
        [1, 2, 0, 0, 0, 0, 0, 5, 32, 0, 0, 0, 32, 2, 0, 0];

    private static ReadOnlySpan<byte> MediumSid =>
        [1, 1, 0, 0, 0, 0, 0, 16, 0, 32, 0, 0];

    public void RequireDisposableJob()
    {
        if (!OperatingSystem.IsWindows() ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_OS") != "Windows" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" ||
            Environment.GetEnvironmentVariable("GITHUB_JOB") != "installer")
            throw RestrictedOwnerToken.Failure(5);
    }

    public MsiContextRead ReadThreadToken()
    {
        RequireDisposableJob();
        return WindowsMsiCallContextTokens.Instance.ReadThreadToken();
    }

    public IAcceptanceSourceToken OpenCurrentPrimary()
    {
        RequireDisposableJob();
        nint raw = 0;
        if (OpenProcessToken(GetCurrentProcess(), SourceAccess, &raw) == 0)
            throw NativeFailure();
        var handle = Own(raw);
        try
        {
            if (ReadInt(handle, TypeInformation) != 1)
                throw RestrictedOwnerToken.Failure(13);
            return new SourceToken(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public IAcceptanceRestrictedToken Filter(
        IAcceptanceSourceToken source, AcceptanceTokenFilter flags)
    {
        RequireDisposableJob();
        if (flags != (AcceptanceTokenFilter.DisableMaxPrivilege | AcceptanceTokenFilter.LuaToken))
            throw RestrictedOwnerToken.Failure(13);
        nint raw = 0;
        fixed (byte* administrator = AdministratorSid)
        {
            var disabled = new SidAndAttributes((nint)administrator, 0);
            if (CreateRestrictedToken(SourceHandle(source), (uint)flags,
                1, &disabled, 0, 0, 0, 0, &raw) == 0)
                throw NativeFailure();
        }
        return new FilteredToken(Own(raw));
    }

    public void SetMediumIntegrity(IAcceptanceRestrictedToken token)
    {
        RequireDisposableJob();
        var sid = MediumSid;
        var size = checked(sizeof(SidAndAttributes) + sid.Length);
        byte* buffer = stackalloc byte[size];
        var address = buffer + sizeof(SidAndAttributes);
        sid.CopyTo(new Span<byte>(address, sid.Length));
        *(SidAndAttributes*)buffer = new((nint)address, 0x20);
        if (SetTokenInformation(FilteredHandle(token), IntegrityInformation,
            (nint)buffer, (uint)size) == 0)
            throw NativeFailure();
    }

    public RestrictedOwnerTokenFacts ReadFacts(
        IAcceptanceRestrictedToken token, IAcceptanceSourceToken source)
    {
        RequireDisposableJob();
        var candidate = FilteredHandle(token);
        var current = SourceHandle(source);
        var (enabled, denyOnly) = ReadAdministrator(candidate);
        return new(ReadInt(candidate, TypeInformation),
            ReadInt(candidate, ElevationInformation), ReadIntegrity(candidate),
            enabled, denyOnly, SameOwner(candidate, current),
            SameSession(candidate, current));
    }

    // Read-only TOKEN_STATISTICS comparison for the genuine parent-unchanged
    // test. These values stay internal and are never formatted or asserted raw.
    internal (long Identity, long Modification) ReadCurrentStamp()
    {
        RequireDisposableJob();
        using var source = OpenCurrentPrimary();
        using var data = TokenBuffer.Read(SourceHandle(source), StatisticsInformation);
        if (data.Length != 56)
            throw RestrictedOwnerToken.Failure(13);
        return (Marshal.ReadInt64(data.Pointer), Marshal.ReadInt64(data.Pointer, 48));
    }

    private static SafeAccessTokenHandle Own(nint raw)
    {
        var handle = new SafeAccessTokenHandle(raw);
        if (!handle.IsInvalid)
            return handle;
        handle.Dispose();
        throw RestrictedOwnerToken.Failure(13);
    }

    private static SafeAccessTokenHandle SourceHandle(IAcceptanceSourceToken token) =>
        token switch
        {
            SourceToken { Handle: { IsClosed: false, IsInvalid: false } handle } => handle,
            _ => throw RestrictedOwnerToken.Failure(6)
        };

    private static SafeAccessTokenHandle FilteredHandle(IAcceptanceRestrictedToken token) =>
        token switch
        {
            FilteredToken { Handle: { IsClosed: false, IsInvalid: false } handle } => handle,
            _ => throw RestrictedOwnerToken.Failure(6)
        };

    private static int ReadInt(SafeAccessTokenHandle token, int information)
    {
        var value = 0;
        uint length = 0;
        if (GetTokenInformation(token, information, (nint)(&value),
            sizeof(int), &length) == 0)
            throw NativeFailure();
        return length == sizeof(int)
            ? value : throw RestrictedOwnerToken.Failure(13);
    }

    private static int ReadIntegrity(SafeAccessTokenHandle token)
    {
        using var data = TokenBuffer.Read(token, IntegrityInformation);
        var sid = data.ReadSid(0);
        if (Marshal.ReadByte(sid, 1) != 1 ||
            Enumerable.Range(2, 5).Any(index => Marshal.ReadByte(sid, index) != 0) ||
            Marshal.ReadByte(sid, 7) != 16)
            throw RestrictedOwnerToken.Failure(13);
        return Marshal.ReadInt32(sid, 8);
    }

    private static (int Enabled, int DenyOnly) ReadAdministrator(
        SafeAccessTokenHandle token)
    {
        using var data = TokenBuffer.Read(token, GroupsInformation);
        var groups = ReadGroups(data);
        fixed (byte* administrator = AdministratorSid)
        {
            var expected = (nint)administrator;
            var matches = groups.Where(group => EqualSid(group.Sid, expected) != 0).ToArray();
            return matches switch
            {
                [] => (0, 0),
                [var group] => ((group.Attributes & 0x4) == 0 ? 0 : 1,
                    (group.Attributes & 0x16) == 0x10 ? 1 : 0),
                _ => throw RestrictedOwnerToken.Failure(13)
            };
        }
    }

    private static ImmutableArray<SidAndAttributes> ReadGroups(TokenBuffer data)
    {
        var count = Marshal.ReadInt32(data.Pointer);
        // TOKEN_GROUPS aligns its SID_AND_ATTRIBUTES array to pointer alignment.
        var offset = sizeof(nint);
        if (count < 0 || count > (data.Length - offset) / sizeof(SidAndAttributes))
            throw RestrictedOwnerToken.Failure(13);
        return Enumerable.Range(0, count)
            .Select(index => ReadGroup(data, offset + index * sizeof(SidAndAttributes)))
            .ToImmutableArray();
    }

    private static SidAndAttributes ReadGroup(TokenBuffer data, int offset)
    {
        var group = *(SidAndAttributes*)(data.Pointer + offset);
        _ = data.RequireSid(group.Sid);
        return group;
    }

    private static int SameOwner(
        SafeAccessTokenHandle candidate, SafeAccessTokenHandle source)
    {
        using var left = TokenBuffer.Read(candidate, UserInformation);
        using var right = TokenBuffer.Read(source, UserInformation);
        return EqualSid(left.ReadSid(0), right.ReadSid(0)) != 0 ? 1 : 0;
    }

    private static int SameSession(
        SafeAccessTokenHandle candidate, SafeAccessTokenHandle source)
    {
        var current = ReadInt(source, SessionInformation);
        return current >= 0 && current == ReadInt(candidate, SessionInformation) ? 1 : 0;
    }

    private static Win32Exception NativeFailure() =>
        RestrictedOwnerToken.Failure(Marshal.GetLastPInvokeError());

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct SidAndAttributes(nint Sid, uint Attributes);

    private sealed class SourceToken(SafeAccessTokenHandle handle) : IAcceptanceSourceToken
    {
        internal SafeAccessTokenHandle Handle { get; } = handle;
        public bool IsClosed => Handle.IsClosed;
        public void Dispose() => Handle.Dispose();
    }

    private sealed class FilteredToken(SafeAccessTokenHandle handle)
        : IAcceptanceRestrictedToken
    {
        internal SafeAccessTokenHandle Handle { get; } = handle;
        public bool IsClosed => Handle.IsClosed;
        public void Dispose() => Handle.Dispose();
    }

    private sealed class TokenBuffer : SafeBuffer
    {
        internal int Length { get; }
        internal nint Pointer => handle;

        private TokenBuffer(int length) : base(ownsHandle: true)
        {
            Length = length;
            SetHandle(Marshal.AllocHGlobal(length));
            Initialize((ulong)length);
        }

        internal static TokenBuffer Read(SafeAccessTokenHandle token, int information)
        {
            var data = new TokenBuffer(RequiredSize(token, information));
            try
            {
                uint actual = 0;
                if (GetTokenInformation(token, information, data.Pointer,
                    (uint)data.Length, &actual) == 0)
                    throw NativeFailure();
                if (actual != (uint)data.Length)
                    throw RestrictedOwnerToken.Failure(13);
                return data;
            }
            catch
            {
                data.Dispose();
                throw;
            }
        }

        private static int RequiredSize(SafeAccessTokenHandle token, int information)
        {
            uint length = 0;
            var result = GetTokenInformation(token, information, 0, 0, &length);
            var error = Marshal.GetLastPInvokeError();
            if (result != 0 || error != 122 || length < sizeof(nint) || length > 65536)
                throw RestrictedOwnerToken.Failure(result == 0 && error != 122 ? error : 13);
            return checked((int)length);
        }

        internal nint ReadSid(int offset)
        {
            if (offset < 0 || offset > Length - sizeof(nint))
                throw RestrictedOwnerToken.Failure(13);
            return RequireSid(Marshal.ReadIntPtr(Pointer, offset));
        }

        internal nint RequireSid(nint sid)
        {
            var offset = sid.ToInt64() - Pointer.ToInt64();
            if (offset < 0 || offset > Length - 8)
                throw RestrictedOwnerToken.Failure(13);
            var count = Marshal.ReadByte(sid, 1);
            if (Marshal.ReadByte(sid) != 1 || count > 15 ||
                offset > (long)Length - 8 - 4 * count)
                throw RestrictedOwnerToken.Failure(13);
            return sid;
        }

        protected override bool ReleaseHandle()
        {
            Marshal.FreeHGlobal(handle);
            return true;
        }
    }

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int OpenProcessToken(nint process, uint access, nint* token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CreateRestrictedToken(
        SafeAccessTokenHandle source, uint flags, uint disabledCount,
        SidAndAttributes* disabled, uint deletedCount, nint deleted,
        uint restrictedCount, nint restricted, nint* token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetTokenInformation(
        SafeAccessTokenHandle token, int information, nint buffer, uint length);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetTokenInformation(
        SafeAccessTokenHandle token, int information,
        nint buffer, uint length, uint* needed);

    [LibraryImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int EqualSid(nint left, nint right);
}
