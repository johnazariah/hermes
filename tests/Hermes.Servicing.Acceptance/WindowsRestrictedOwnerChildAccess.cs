namespace Hermes.Servicing.Acceptance;

internal sealed unsafe partial class WindowsRestrictedOwnerTokens
{
    // Borrowed handles remain owned by OwnedInertChild. Only Authz handles
    // are owned here. The actual primary has TOKEN_QUERY | READ_CONTROL only.
    private sealed class ChildAuthzScope(
        SafeChildHandle primary, SafeChildHandle process, SafeChildHandle thread)
        : IAcceptanceChildAccessScope
    {
        private SafeChildAuthzHandle? _resource;
        private SafeChildAuthzHandle? _context;
        private bool _disposed;
        private bool _completed;

        public bool IsClosed => _disposed && _completed;

        public void InitializeResourceManager(uint flags)
        {
            RequireOpen();
            if (_resource is not null)
                throw RestrictedOwnerToken.Failure(13);
            nint raw = 0;
            if (AuthzInitializeResourceManager(flags, 0, 0, 0, 0, &raw) == 0)
                throw NativeFailure();
            _resource = OwnAuthz(raw, false);
        }

        public void InitializeContext(uint flags)
        {
            RequireOpen();
            if (_context is not null)
                throw RestrictedOwnerToken.Failure(13);
            nint raw = 0;
            // Directly initialize from the verified actual child primary token.
            // No parent/launch substitute, SID-built context, or skipped groups.
            if (AuthzInitializeContextFromToken(
                flags, primary, AuthzHandle(_resource), 0, default, 0, &raw) == 0)
                throw NativeFailure();
            _context = OwnAuthz(raw, true);
        }

        public AcceptanceLabelQueryReply Query(
            AcceptanceChildObject target, uint information, Span<byte> buffer)
        {
            RequireOpen();
            var owned = target switch
            {
                AcceptanceChildObject.Process => process,
                AcceptanceChildObject.Thread => thread,
                AcceptanceChildObject.Token => primary,
                _ => throw RestrictedOwnerToken.Failure(13)
            };
            uint needed = 0;
            fixed (byte* address = buffer)
            {
                var succeeded = GetKernelObjectSecurity(
                    owned, information, address, (uint)buffer.Length, &needed);
                return new(succeeded, needed, Marshal.GetLastPInvokeError());
            }
        }

        public AcceptanceAuthzReply Check(uint flags, uint desired, byte[] descriptor)
        {
            RequireOpen();
            uint granted = 0;
            uint sacl = 0;
            uint status = uint.MaxValue;
            var request = new ChildAuthzRequest(desired, 0, 0, 0, 0);
            var reply = new ChildAuthzNativeReply(
                1, (nint)(&granted), (nint)(&sacl), (nint)(&status));
            fixed (byte* security = descriptor)
            {
                var succeeded = AuthzAccessCheck(flags, AuthzHandle(_context),
                    &request, 0, security, 0, 0, &reply, 0);
                var error = Marshal.GetLastPInvokeError();
                return succeeded == 0 ? new(0, error, 0, 0)
                    : new(succeeded, 0, status, granted);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            AcceptanceChildCleanup.Complete(
                () => _context?.CloseChecked(),
                () => _resource?.CloseChecked());
            _completed = true;
        }

        private void RequireOpen()
        {
            if (_disposed)
                throw RestrictedOwnerToken.Failure(6);
            Instance.RequireDisposableJob();
        }
    }

    private static SafeChildAuthzHandle OwnAuthz(nint raw, bool context)
    {
        var owned = new SafeChildAuthzHandle(raw, context);
        owned.Initialize();
        if (!owned.IsInvalid)
            return owned;
        owned.CloseChecked();
        throw RestrictedOwnerToken.Failure(13);
    }

    private static SafeChildAuthzHandle AuthzHandle(SafeChildAuthzHandle? handle) =>
        handle is { IsClosed: false, IsInvalid: false } owned
            ? owned : throw RestrictedOwnerToken.Failure(6);

    private sealed class SafeChildAuthzHandle(nint value, bool context)
        : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        private int _closeError;

        internal void Initialize() => SetHandle(value);

        internal void CloseChecked()
        {
            Dispose();
            if (_closeError != 0)
                throw RestrictedOwnerToken.Failure(_closeError);
        }

        protected override bool ReleaseHandle()
        {
            var succeeded = context
                ? AuthzFreeContext(handle) : AuthzFreeResourceManager(handle);
            if (succeeded != 0)
                return true;
            var error = Marshal.GetLastPInvokeError();
            _closeError = error > 0 ? error : 13;
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ChildAuthzLuid(uint Low, int High);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ChildAuthzRequest(
        uint DesiredAccess, nint PrincipalSelf, nint ObjectTypes,
        uint ObjectTypeCount, nint Arguments);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ChildAuthzNativeReply(
        uint Count, nint Granted, nint Sacl, nint Error);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetKernelObjectSecurity(
        SafeChildHandle owned, uint information, byte* descriptor, uint length, uint* needed);

    [LibraryImport("authz.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int AuthzInitializeResourceManager(
        uint flags, nint accessCallback, nint groupsCallback,
        nint freeGroupsCallback, nint name, nint* resource);

    [LibraryImport("authz.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int AuthzInitializeContextFromToken(
        uint flags, SafeChildHandle token, SafeChildAuthzHandle resource,
        nint expiration, ChildAuthzLuid identifier, nint arguments, nint* context);

    [LibraryImport("authz.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int AuthzAccessCheck(
        uint flags, SafeChildAuthzHandle context, ChildAuthzRequest* request,
        nint audit, byte* descriptor, nint optionalDescriptors,
        uint optionalCount, ChildAuthzNativeReply* reply, nint cachedResults);

    [LibraryImport("authz.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int AuthzFreeContext(nint context);

    [LibraryImport("authz.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int AuthzFreeResourceManager(nint resource);
}
