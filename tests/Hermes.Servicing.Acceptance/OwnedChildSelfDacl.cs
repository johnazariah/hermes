using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Hermes.Servicing.Acceptance;

internal enum AcceptanceSelfTokenInformation
{
    User = 1,
    Owner = 4,
    PrimaryGroup = 5,
    DefaultDacl = 6,
    RestrictedSids = 11
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct AcceptanceSelfTokenDefaultDacl(nint defaultDacl)
{
    internal readonly nint DefaultDacl = defaultDacl;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct AcceptanceOwnedObjectAttributes(
    uint length, nint descriptor, int inheritHandles)
{
    internal readonly uint Length = length;
    internal readonly nint Descriptor = descriptor;
    internal readonly int InheritHandles = inheritHandles;
}

internal static class AcceptanceOwnedSelfDacl
{
    internal const uint GenericAll = 0x10000000;

    // Only for authoring NEW owned objects. Windows maps GENERIC_ALL separately
    // for the process and thread. This is not an offline generic mapping.
    internal static byte[] Author(
        SecurityIdentifier user, SecurityIdentifier owner, SecurityIdentifier group,
        byte[] defaultDacl, IReadOnlyList<SecurityIdentifier> restrictingSids)
    {
        RequireRestrictingPass(user, restrictingSids);
        var acl = ReadAcl(defaultDacl);
        AcceptanceDesktopDacl.RequireUnconditionalAccess(acl, true);
        var grant = new CommonAce(AceFlags.None, AceQualifier.AccessAllowed,
            checked((int)GenericAll), user, false, null);
        if (acl.Count == ushort.MaxValue || acl.BinaryLength > ushort.MaxValue - grant.BinaryLength)
            throw RestrictedOwnerToken.Failure(13);
        acl.InsertAce(acl.Count, grant);
        var descriptor = new RawSecurityDescriptor(
            ControlFlags.SelfRelative | ControlFlags.DiscretionaryAclPresent,
            owner, group, null, acl);
        if (descriptor.BinaryLength > AcceptanceDesktopDacl.DescriptorLimit)
            throw RestrictedOwnerToken.Failure(13);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static void RequireRestrictingPass(
        SecurityIdentifier user, IReadOnlyList<SecurityIdentifier> restrictingSids)
    {
        // Never guess a LUA restricting SID or grant it access to other users.
        if (restrictingSids.Count != 0 && !restrictingSids.Any(sid => sid.Equals(user)))
            throw RestrictedOwnerToken.Failure(50);
    }

    private static RawAcl ReadAcl(byte[] bytes)
    {
        RequireAclEnvelope(bytes);
        try { return new RawAcl(bytes, 0); }
        catch (Exception error) when (
            error is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            throw RestrictedOwnerToken.Failure(13);
        }
    }

    private static void RequireAclEnvelope(byte[] bytes)
    {
        if (bytes.Length is < 8 or > ushort.MaxValue || (bytes.Length & 3) != 0 ||
            bytes[0] is not (2 or 4) || bytes[1] != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)) != bytes.Length ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)) != 0)
            throw RestrictedOwnerToken.Failure(13);
    }

    // TOKEN_DEFAULT_DACL is one PACL. Copy only an ACL wholly inside the
    // bounded GetTokenInformation buffer; no pointer survives this call.
    internal static byte[] CopyDefaultDacl(nint buffer, int length)
    {
        if (buffer == 0 || length < nint.Size || length > AcceptanceDesktopDacl.DescriptorLimit)
            throw RestrictedOwnerToken.Failure(13);
        var acl = Marshal.PtrToStructure<AcceptanceSelfTokenDefaultDacl>(buffer).DefaultDacl;
        if (acl == 0)
            throw RestrictedOwnerToken.Failure(50);
        if (length < nint.Size + 8)
            throw RestrictedOwnerToken.Failure(13);
        var offset = unchecked((nuint)acl - (nuint)buffer);
        if (offset < (nuint)nint.Size || offset > (nuint)(length - 8) || (offset & 3) != 0)
            throw RestrictedOwnerToken.Failure(13);
        var size = unchecked((ushort)Marshal.ReadInt16(acl, 2));
        if (size < 8 || (size & 3) != 0 || (nuint)size > (nuint)length - offset)
            throw RestrictedOwnerToken.Failure(13);
        var bytes = new byte[size];
        Marshal.Copy(acl, bytes, 0, size);
        return bytes;
    }
}
