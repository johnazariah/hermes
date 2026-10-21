using System.Buffers.Binary;
using System.Security.AccessControl;

namespace Hermes.Servicing.Acceptance;

internal enum AcceptanceDesktopObject
{
    Station = 1,
    Desktop = 2
}

internal interface IAcceptanceDesktopAccess
{
    AcceptanceDesktopAccessResult Read(IAcceptanceRestrictedToken token);
}

internal sealed record AcceptanceDaclObservation(
    int Observed, int Allowed, uint GrantedMask, int Error)
{
    internal static AcceptanceDaclObservation Unobserved(int error) =>
        new(0, -1, 0, error > 0 ? error : 13);
}

internal sealed record AcceptanceDesktopAccessResult(
    AcceptanceDaclObservation Station,
    AcceptanceDaclObservation Desktop,
    RestrictedOwnerTokenFacts ProbeFacts,
    int ImpersonationLevel,
    uint StationFlags,
    bool ProbeClosed)
{
    internal AcceptanceMandatoryLabelObservation StationLabel { get; init; } =
        AcceptanceMandatoryLabelObservation.Unknown;

    internal AcceptanceMandatoryLabelObservation DesktopLabel { get; init; } =
        AcceptanceMandatoryLabelObservation.Unknown;
}

internal readonly record struct AcceptanceAccessReply(
    int Succeeded, int Status, uint GrantedMask, int Error,
    uint PrivilegeBytes, uint PrivilegeCount);

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct AcceptanceDesktopGenericMapping(
    uint Read, uint Write, uint Execute, uint All);

internal enum AcceptanceMandatoryLabelState
{
    Unknown = 0,
    Present = 1,
    Missing = 2,
    Unsupported = 3,
    QueryFailed = 4,
    InvalidData = 5
}

// Present means one label ACE was returned, not that MIC granted access.
// ACE flags are retained: an inherit-only ACE is not an effective object label.
// Missing never manufactures a measured RID, policy, or effective-access result.
internal sealed record AcceptanceMandatoryLabelObservation(
    AcceptanceMandatoryLabelState State,
    long IntegrityRid,
    int Policy,
    int AceFlags,
    int Error)
{
    internal static AcceptanceMandatoryLabelObservation Unknown { get; } =
        new(AcceptanceMandatoryLabelState.Unknown, -1, -1, -1, 0);
}

internal readonly record struct AcceptanceLabelQueryReply(
    int Succeeded, uint Bytes, int Error);

// The implementation captures one borrowed object privately. No handle escapes.
internal interface IAcceptanceMandatoryLabelQuery
{
    AcceptanceLabelQueryReply Query(Span<byte> buffer);
}

internal readonly record struct AcceptanceDescriptorReply(
    byte[]? Bytes, int Error, bool Invalid);

// Shared bounded transport; decoding and descriptor scope remain caller-owned.
internal static class AcceptanceDescriptorQuery
{
    private static AcceptanceDescriptorReply Invalid => new(null, 13, true);

    internal static AcceptanceDescriptorReply Read(IAcceptanceMandatoryLabelQuery query)
    {
        var size = query.Query(Span<byte>.Empty);
        if (size.Succeeded != 0 || size.Error != 122)
            return size.Succeeded == 0 ? Failure(size.Error) : Invalid;
        if (size.Bytes is < 20 or > AcceptanceDesktopDacl.DescriptorLimit)
            return Invalid;
        var bytes = new byte[checked((int)size.Bytes)];
        var reply = query.Query(bytes);
        if (reply.Succeeded == 0)
            return Failure(reply.Error);
        return reply.Bytes == (uint)bytes.Length ? new(bytes, 0, false) : Invalid;
    }

    private static AcceptanceDescriptorReply Failure(int error) =>
        new(null, error > 0 ? error : 13, false);
}

internal static class AcceptanceDesktopMandatoryLabel
{
    // LABEL_SECURITY_INFORMATION requires READ_CONTROL, not a full-SACL query.
    internal const uint DescriptorScope = 0x00000010;

    private static AcceptanceMandatoryLabelObservation Invalid { get; } =
        new(AcceptanceMandatoryLabelState.InvalidData, -1, -1, -1, 13);

    private static AcceptanceMandatoryLabelObservation Missing { get; } =
        new(AcceptanceMandatoryLabelState.Missing, -1, -1, -1, 0);

    internal static AcceptanceMandatoryLabelObservation Read(
        IAcceptanceMandatoryLabelQuery query)
    {
        return AcceptanceDescriptorQuery.Read(query) switch
        {
            { Bytes: { } bytes } => Decode(bytes),
            { Invalid: true } => Invalid,
            var failure => Failure(failure.Error)
        };
    }

    internal static AcceptanceMandatoryLabelObservation Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 20 or > AcceptanceDesktopDacl.DescriptorLimit ||
            bytes[0] != 1 ||
            (BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]) & 0x8000) == 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]) != 0)
            return Invalid;
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        if (offset == 0)
            return Missing;
        if ((bytes[2] & 0x10) == 0 || offset < 20 || (offset & 3) != 0 ||
            offset > (uint)(bytes.Length - 8))
            return Invalid;
        return DecodeAcl(bytes[checked((int)offset)..]);
    }

    private static AcceptanceMandatoryLabelObservation DecodeAcl(ReadOnlySpan<byte> acl)
    {
        var length = BinaryPrimitives.ReadUInt16LittleEndian(acl[2..]);
        if (acl[0] is not (2 or 4) || acl[1] != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(acl[6..]) != 0 ||
            length < 8 || (length & 3) != 0 || length > acl.Length)
            return Invalid;
        return BinaryPrimitives.ReadUInt16LittleEndian(acl[4..]) switch
        {
            0 => Missing,
            1 when length >= 28 => DecodeAce(acl.Slice(8, length - 8)),
            _ => Invalid
        };
    }

    private static AcceptanceMandatoryLabelObservation DecodeAce(ReadOnlySpan<byte> ace)
    {
        if (ace[0] != 0x11 || (ace[1] & ~0x1f) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(ace[2..]) != 20 ||
            BinaryPrimitives.ReadUInt32LittleEndian(ace[4..]) > 7 ||
            !ace.Slice(8, 8).SequenceEqual(
                new byte[] { 1, 1, 0, 0, 0, 0, 0, 16 }))
            return Invalid;
        return new(AcceptanceMandatoryLabelState.Present,
            BinaryPrimitives.ReadUInt32LittleEndian(ace[16..]),
            checked((int)BinaryPrimitives.ReadUInt32LittleEndian(ace[4..])),
            ace[1], 0);
    }

    private static AcceptanceMandatoryLabelObservation Failure(int error)
    {
        var code = error > 0 ? error : 13;
        var state = code is 50 or 120
            ? AcceptanceMandatoryLabelState.Unsupported
            : AcceptanceMandatoryLabelState.QueryFailed;
        return new(state, -1, -1, -1, code);
    }
}

internal static class AcceptanceDesktopDacl
{
    internal const uint DescriptorScope = 0x00000007;
    internal const uint MaximumAllowed = 0x02000000;
    internal const int DescriptorLimit = 65536;
    internal const int PrivilegeCapacity = 4096;

    internal static AcceptanceDesktopGenericMapping Mapping(
        AcceptanceDesktopObject target, bool interactive) =>
        (target, interactive) switch
        {
            (AcceptanceDesktopObject.Station, true) =>
                new(0x00020303, 0x0002001c, 0x00020060, 0x000f037f),
            (AcceptanceDesktopObject.Station, false) =>
                new(0x00020103, 0x0002000c, 0x00020060, 0x000f016f),
            (AcceptanceDesktopObject.Desktop, _) =>
                new(0x00020041, 0x000200be, 0x00020100, 0x000f01ff),
            _ => throw RestrictedOwnerToken.Failure(13)
        };

    internal static uint ValidMask(AcceptanceDesktopObject target) => target switch
    {
        AcceptanceDesktopObject.Station => 0x000f037f,
        AcceptanceDesktopObject.Desktop => 0x000f01ff,
        _ => throw RestrictedOwnerToken.Failure(13)
    };

    internal static AcceptanceDaclObservation Decide(
        AcceptanceAccessReply reply, uint validMask)
    {
        if (reply.Succeeded == 0)
            return AcceptanceDaclObservation.Unobserved(reply.Error);
        if (reply.PrivilegeBytes is < 8 or > PrivilegeCapacity ||
            reply.PrivilegeCount > (reply.PrivilegeBytes - 8) / 12)
            return AcceptanceDaclObservation.Unobserved(13);
        return (reply.Status != 0, reply.GrantedMask) switch
        {
            (false, 0) => new(1, 0, 0, reply.Error),
            (true, var granted) when (granted & ~validMask) == 0 =>
                new(1, 1, granted, 0),
            _ => AcceptanceDaclObservation.Unobserved(13)
        };
    }

    internal static void RequireProbe(
        RestrictedOwnerTokenFacts facts, int impersonationLevel)
    {
        // This is an impersonation token, NOT a verified primary token.
        var failed = (facts.FailedPredicates & ~1) |
            (facts.TokenType == 2 ? 0 : 1) |
            (impersonationLevel == 2 ? 0 : 256);
        if (failed != 0)
            throw new RestrictedOwnerTokenVerificationException(failed);
    }

    internal static byte[] NormalizeDescriptor(
        byte[] bytes, bool requireConcreteAccess = false,
        bool requireUnconditionalAccess = false)
    {
        RequireEnvelope(bytes);
        try
        {
            var descriptor = new RawSecurityDescriptor(bytes, 0);
            if (requireConcreteAccess && HasGenericAccess(descriptor.DiscretionaryAcl))
                throw RestrictedOwnerToken.Failure(50);
            RequireUnconditionalAccess(descriptor.DiscretionaryAcl, requireUnconditionalAccess);
            return CopyDaclDescriptor(descriptor);
        }
        catch (Exception error) when (
            error is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            throw RestrictedOwnerToken.Failure(13);
        }
    }

    private static void RequireEnvelope(byte[] bytes)
    {
        if (bytes.Length is < 20 or > DescriptorLimit || bytes[0] != 1 ||
            (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)) & 0x8000) == 0)
            throw RestrictedOwnerToken.Failure(13);
    }

    private static bool HasGenericAccess(RawAcl? acl) =>
        acl is { } present && present.Cast<GenericAce>().Any(ace =>
            ace is not KnownAce known ||
            (unchecked((uint)known.AccessMask) & 0xf0000000) != 0);

    private static void RequireUnconditionalAccess(RawAcl? acl, bool required)
    {
        if (required && acl is { } present && present.Cast<GenericAce>().Any(ace =>
            ace is not CommonAce
            {
                IsCallback: false,
                AceQualifier: AceQualifier.AccessAllowed or AceQualifier.AccessDenied
            }))
            throw RestrictedOwnerToken.Failure(50);
    }

    private static byte[] CopyDaclDescriptor(RawSecurityDescriptor descriptor)
    {
        if (descriptor.Owner is not { } owner ||
            descriptor.Group is not { } group || descriptor.SystemAcl is not null)
            throw RestrictedOwnerToken.Failure(13);
        var copy = new RawSecurityDescriptor(
            descriptor.ControlFlags & ~ControlFlags.SystemAclPresent,
            owner, group, null, descriptor.DiscretionaryAcl);
        if (copy.BinaryLength is < 20 or > DescriptorLimit)
            throw RestrictedOwnerToken.Failure(13);
        var bytes = new byte[copy.BinaryLength];
        copy.GetBinaryForm(bytes, 0);
        return bytes;
    }
}
