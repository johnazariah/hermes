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
    bool ProbeClosed);

internal readonly record struct AcceptanceAccessReply(
    int Succeeded, int Status, uint GrantedMask, int Error,
    uint PrivilegeBytes, uint PrivilegeCount);

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct AcceptanceDesktopGenericMapping(
    uint Read, uint Write, uint Execute, uint All);

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
        byte[] bytes, bool requireConcreteAccess = false)
    {
        RequireEnvelope(bytes);
        try
        {
            var descriptor = new RawSecurityDescriptor(bytes, 0);
            if (requireConcreteAccess && HasGenericAccess(descriptor.DiscretionaryAcl))
                throw RestrictedOwnerToken.Failure(50);
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
