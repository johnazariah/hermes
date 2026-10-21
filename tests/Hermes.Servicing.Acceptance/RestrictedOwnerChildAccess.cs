using System.ComponentModel;

namespace Hermes.Servicing.Acceptance;

internal enum AcceptanceChildObject
{
    Unknown = 0,
    Process = 1,
    Thread = 2
}

internal enum AcceptanceChildSubject
{
    Unknown = 0,
    ActualTokenVerified = 1
}

internal enum AcceptanceAuthzState
{
    Unknown = 0,
    Granted = 1,
    Denied = 2,
    Unobserved = 3
}

internal sealed record AcceptanceAuthzObservation(
    AcceptanceAuthzState State, int Error, long ReplyError, uint GrantedMask)
{
    internal static AcceptanceAuthzObservation Unknown { get; } =
        new(AcceptanceAuthzState.Unknown, 0, -1, 0);

    internal static AcceptanceAuthzObservation Unobserved(int error) =>
        new(AcceptanceAuthzState.Unobserved, error > 0 ? error : 13, -1, 0);
}

internal sealed record AcceptanceChildObjectAccess(
    AcceptanceChildObject Object,
    AcceptanceChildSubject Subject,
    AcceptanceAuthzObservation Authz,
    AcceptanceMandatoryLabelObservation Label)
{
    internal static AcceptanceChildObjectAccess Unknown(AcceptanceChildObject target) =>
        new(target, AcceptanceChildSubject.Unknown,
            AcceptanceAuthzObservation.Unknown, AcceptanceMandatoryLabelObservation.Unknown);
}

internal sealed record AcceptanceChildAccessResult(
    AcceptanceChildObjectAccess Process,
    AcceptanceChildObjectAccess Thread,
    bool ResourcesClosed)
{
    internal static AcceptanceChildAccessResult Unknown { get; } =
        new(AcceptanceChildObjectAccess.Unknown(AcceptanceChildObject.Process),
            AcceptanceChildObjectAccess.Unknown(AcceptanceChildObject.Thread), false);
}

internal readonly record struct AcceptanceAuthzReply(
    int Succeeded, int Error, uint ReplyError, uint GrantedMask);

// Native implementation privately captures the held, verified child primary
// token and retained owned process/thread handles. No handle or SID escapes.
internal interface IAcceptanceChildAccessScope : IDisposable
{
    bool IsClosed { get; }
    void InitializeResourceManager(uint flags);
    void InitializeContext(uint flags);
    AcceptanceLabelQueryReply Query(
        AcceptanceChildObject target, uint information, Span<byte> buffer);
    AcceptanceAuthzReply Check(uint flags, uint desired, byte[] descriptor);
}

internal static class AcceptanceChildAccess
{
    internal const uint NoAudit = 1;

    // This is an offline OWNER/GROUP/DACL inventory, not a kernel/MIC verdict.
    // No generic mapping, token duplication, group reconstruction, or callback.
    internal static AcceptanceChildAccessResult Read(IAcceptanceChildAccessScope scope)
    {
        AcceptanceChildAccessResult result;
        try { result = ReadObjects(scope); }
        catch (Exception original)
        {
            try { scope.Dispose(); }
            catch (Exception cleanup)
            {
                throw new AggregateException(
                    "Acceptance child inventory failed; cleanup incomplete.", original, cleanup);
            }
            throw;
        }
        scope.Dispose();
        if (!scope.IsClosed)
            throw new AcceptanceChildCleanupException([RestrictedOwnerToken.Failure(6)]);
        return result with { ResourcesClosed = true };
    }

    private static AcceptanceChildAccessResult ReadObjects(IAcceptanceChildAccessScope scope)
    {
        var contextError = Initialize(scope);
        return new(
            Observe(scope, AcceptanceChildObject.Process, contextError),
            Observe(scope, AcceptanceChildObject.Thread, contextError), false);
    }

    private static int Initialize(IAcceptanceChildAccessScope scope)
    {
        try
        {
            scope.InitializeResourceManager(NoAudit);
            scope.InitializeContext(0);
            return 0;
        }
        catch (Win32Exception error)
        {
            return error.NativeErrorCode > 0 ? error.NativeErrorCode : 13;
        }
    }

    private static AcceptanceChildObjectAccess Observe(
        IAcceptanceChildAccessScope scope, AcceptanceChildObject target, int contextError) =>
        new(target, AcceptanceChildSubject.ActualTokenVerified,
            ReadDacl(scope, target, contextError),
            AcceptanceDesktopMandatoryLabel.Read(new ScopedQuery(
                scope, target, AcceptanceDesktopMandatoryLabel.DescriptorScope)));

    private static AcceptanceAuthzObservation ReadDacl(
        IAcceptanceChildAccessScope scope, AcceptanceChildObject target, int contextError)
    {
        try
        {
            var read = AcceptanceDescriptorQuery.Read(
                new ScopedQuery(scope, target, AcceptanceDesktopDacl.DescriptorScope));
            if (read.Bytes is not { } bytes)
                return AcceptanceAuthzObservation.Unobserved(read.Error);
            var normalized = AcceptanceDesktopDacl.NormalizeDescriptor(bytes, true, true);
            return contextError != 0 ? AcceptanceAuthzObservation.Unobserved(contextError)
                : Decide(scope.Check(0, AcceptanceDesktopDacl.MaximumAllowed, normalized));
        }
        catch (Win32Exception error)
        {
            return AcceptanceAuthzObservation.Unobserved(error.NativeErrorCode);
        }
    }

    internal static AcceptanceAuthzObservation Decide(AcceptanceAuthzReply reply)
    {
        if (reply.Succeeded == 0)
            return AcceptanceAuthzObservation.Unobserved(reply.Error);
        return (reply.ReplyError, reply.GrantedMask) switch
        {
            (0, 0) => Unexpected(reply, 13),
            (0, var mask) when (mask & 0xf2000000) != 0 => Unexpected(reply, 50),
            (0, var mask) => new(AcceptanceAuthzState.Granted, 0, 0, mask),
            (5, 0) => new(AcceptanceAuthzState.Denied, 0, 5, 0),
            (5, _) => Unexpected(reply, 13),
            _ => Unexpected(reply, reply.ReplyError is > 0 and <= int.MaxValue
                ? checked((int)reply.ReplyError) : 13)
        };
    }

    private static AcceptanceAuthzObservation Unexpected(AcceptanceAuthzReply reply, int error) =>
        new(AcceptanceAuthzState.Unobserved, error, reply.ReplyError, 0);

    private sealed class ScopedQuery(
        IAcceptanceChildAccessScope scope, AcceptanceChildObject target, uint information)
        : IAcceptanceMandatoryLabelQuery
    {
        public AcceptanceLabelQueryReply Query(Span<byte> buffer) =>
            scope.Query(target, information, buffer);
    }
}
