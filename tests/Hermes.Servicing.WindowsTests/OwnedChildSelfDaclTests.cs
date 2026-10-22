using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Hermes.Servicing.Acceptance;
using Xunit;

namespace Hermes.Servicing.WindowsTests;

[SupportedOSPlatform("windows")]
public sealed class OwnedChildSelfDaclTests
{
    private static SecurityIdentifier User =>
        new("S-1-5-21-111-222-333-1001");

    private static SecurityIdentifier System =>
        new(WellKnownSidType.LocalSystemSid, null);

    [Fact]
    public void Author_appends_only_the_actual_user_without_changing_existing_ACEs_or_input()
    {
        var original = DefaultAcl();
        var before = original.ToArray();
        var descriptor = new RawSecurityDescriptor(Author(original), 0);
        var acl = RequireDacl(descriptor);
        var source = new RawAcl(original, 0);
        Assert.Equal(before, original);
        Assert.Equal(User, descriptor.Owner);
        Assert.Equal(System, descriptor.Group);
        Assert.Null(descriptor.SystemAcl);
        Assert.Equal(source.Count + 1, acl.Count);
        Assert.All(Enumerable.Range(0, source.Count),
            index => Assert.Equal(AceBytes(source[index]), AceBytes(acl[index])));
        RequireUserGrant(acl[acl.Count - 1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Restricting_pass_uses_the_supplied_actual_profile_without_guessing_SIDs(int profile)
    {
        SecurityIdentifier[] restricting = profile switch
        {
            0 => [],
            1 => [User],
            2 => [System, User],
            3 => [System],
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };
        if (profile == 3)
        {
            Assert.Equal(50, Assert.Throws<Win32Exception>(
                () => Author(DefaultAcl(), restricting)).NativeErrorCode);
            return;
        }
        var acl = RequireDacl(new RawSecurityDescriptor(Author(DefaultAcl(), restricting), 0));
        RequireUserGrant(acl[acl.Count - 1]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 7)]
    [InlineData(3, 255)]
    [InlineData(4, 255)]
    [InlineData(6, 1)]
    [InlineData(10, 0)]
    [InlineData(10, 3)]
    public void Malformed_default_ACLs_are_refused_without_authoring_a_descriptor(int offset, int value)
    {
        var bytes = DefaultAcl();
        bytes[offset] = checked((byte)value);
        Assert.Equal(13, Assert.Throws<Win32Exception>(
            () => Author(bytes)).NativeErrorCode);
    }

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(0x10000000u, 0)]
    [InlineData(0x00040000u, 16)]
    [InlineData(0x001fffffu, 0)]
    public void Existing_user_denials_and_their_flags_are_not_removed_or_reordered(uint mask, int flags)
    {
        var original = AclBytes(new CommonAce((AceFlags)flags, AceQualifier.AccessDenied,
            unchecked((int)mask), User, false, null));
        var source = new RawAcl(original, 0);
        var result = RequireDacl(new RawSecurityDescriptor(Author(original), 0));
        Assert.Equal(2, result.Count);
        Assert.Equal(AceBytes(source[0]), AceBytes(result[0]));
        RequireUserGrant(result[1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Callback_object_and_unknown_ACEs_are_not_reinterpreted(int kind)
    {
        GenericAce ace = kind switch
        {
            0 => new CommonAce(AceFlags.None, AceQualifier.AccessAllowed,
                1, User, true, new byte[4]),
            1 => new ObjectAce(AceFlags.None, AceQualifier.AccessAllowed,
                1, User, ObjectAceFlags.ObjectAceTypePresent,
                Guid.Empty, Guid.Empty, false, null),
            2 => new CustomAce((AceType)0x7f, AceFlags.None, new byte[4]),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        Assert.Equal(50, Assert.Throws<Win32Exception>(
            () => Author(AclBytes(ace))).NativeErrorCode);
    }

    [Fact]
    public void Token_information_and_creation_attribute_ABIs_match_the_native_contract()
    {
        Assert.Equal(new[] { 1, 4, 5, 6, 11 },
            Enum.GetValues<AcceptanceSelfTokenInformation>().Select(value => (int)value));
        Assert.Equal(nint.Size, Marshal.SizeOf<AcceptanceSelfTokenDefaultDacl>());
        Assert.Equal(nint.Size == 8 ? 24 : 12, Marshal.SizeOf<AcceptanceOwnedObjectAttributes>());
        Assert.Equal(0, Marshal.OffsetOf<AcceptanceOwnedObjectAttributes>(
            nameof(AcceptanceOwnedObjectAttributes.Length)).ToInt32());
        Assert.Equal(nint.Size, Marshal.OffsetOf<AcceptanceOwnedObjectAttributes>(
            nameof(AcceptanceOwnedObjectAttributes.Descriptor)).ToInt32());
        Assert.Equal(2 * nint.Size, Marshal.OffsetOf<AcceptanceOwnedObjectAttributes>(
            nameof(AcceptanceOwnedObjectAttributes.InheritHandles)).ToInt32());
        Assert.Equal(0x10000000u, AcceptanceOwnedSelfDacl.GenericAll);
    }

    [Fact]
    public void Default_DACL_copy_owns_its_bytes_instead_of_retaining_the_native_pointer()
    {
        WithTokenDaclBuffer((buffer, length) =>
        {
            var copied = AcceptanceOwnedSelfDacl.CopyDefaultDacl(buffer, length);
            Assert.Equal(DefaultAcl(), copied);
            Marshal.WriteByte(buffer + nint.Size, 0);
            Assert.Equal(DefaultAcl(), copied);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Default_DACL_pointer_and_length_failures_never_escape_the_token_buffer(int kind)
    {
        WithTokenDaclBuffer((buffer, length) =>
        {
            PoisonTokenDacl(buffer, length, kind);
            var supplied = kind == 7 ? nint.Size - 1 : length;
            var error = Assert.Throws<Win32Exception>(
                () => AcceptanceOwnedSelfDacl.CopyDefaultDacl(buffer, supplied));
            Assert.Equal(kind == 0 ? 50 : 13, error.NativeErrorCode);
        });
    }

    private static byte[] Author(
        byte[] acl, IReadOnlyList<SecurityIdentifier>? restricting = null) =>
        AcceptanceOwnedSelfDacl.Author(User, User, System, acl, restricting ?? []);

    private static RawAcl RequireDacl(RawSecurityDescriptor descriptor) =>
        descriptor.DiscretionaryAcl is { } acl
            ? acl : throw new InvalidOperationException("Synthetic DACL absent.");

    private static void RequireUserGrant(GenericAce value)
    {
        var grant = Assert.IsType<CommonAce>(value);
        Assert.Equal(AceQualifier.AccessAllowed, grant.AceQualifier);
        Assert.Equal(AceFlags.None, grant.AceFlags);
        Assert.False(grant.IsCallback);
        Assert.Equal(User, grant.SecurityIdentifier);
        Assert.Equal(0x10000000u, unchecked((uint)grant.AccessMask));
    }

    private static byte[] DefaultAcl() => AclBytes(
        new CommonAce(AceFlags.None, AceQualifier.AccessDenied, 0x00040000, System, false, null),
        new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x00020001, User, false, null));

    private static byte[] AclBytes(params GenericAce[] aces)
    {
        var acl = aces.Aggregate(new RawAcl(4, aces.Length), Append);
        var bytes = new byte[acl.BinaryLength];
        acl.GetBinaryForm(bytes, 0);
        return bytes;

        static RawAcl Append(RawAcl target, GenericAce ace)
        {
            target.InsertAce(target.Count, ace);
            return target;
        }
    }

    private static byte[] AceBytes(GenericAce ace)
    {
        var bytes = new byte[ace.BinaryLength];
        ace.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static void WithTokenDaclBuffer(Action<nint, int> action)
    {
        var acl = DefaultAcl();
        var length = nint.Size + acl.Length;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.WriteIntPtr(buffer, buffer + nint.Size);
            Marshal.Copy(acl, 0, buffer + nint.Size, acl.Length);
            action(buffer, length);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void PoisonTokenDacl(nint buffer, int length, int kind)
    {
        var acl = buffer + nint.Size;
        switch (kind)
        {
            case 0: Marshal.WriteIntPtr(buffer, 0); break;
            case 1: Marshal.WriteIntPtr(buffer, buffer); break;
            case 2: Marshal.WriteIntPtr(buffer, buffer - 4); break;
            case 3: Marshal.WriteIntPtr(buffer, buffer + length); break;
            case 4: Marshal.WriteIntPtr(buffer, acl + 1); break;
            case 5: Marshal.WriteInt16(acl, 2, 7); break;
            case 6: Marshal.WriteInt16(acl, 2, checked((short)(length - nint.Size + 4))); break;
            case 7: break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
}
