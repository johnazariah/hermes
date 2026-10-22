using System.Security.Principal;

namespace Hermes.Servicing.Acceptance;

internal sealed unsafe partial class WindowsRestrictedOwnerTokens
{
    internal IAcceptanceChildFactory OwnedSelfControlForObservation { get; } =
        new OwnedSelfControlChildren();

    private sealed class OwnedSelfControlChildren : IAcceptanceChildFactory
    {
        public IAcceptanceOwnedChild Open()
        {
            Instance.RequireDisposableJob();
            return new OwnedInertChild(ownedSelfControl: true);
        }
    }

    // The parameter cannot accept the source-token role. Nothing is set on
    // either token: the returned private SD is used only at object creation.
    private static byte[] ReadOwnedSelfDescriptor(IAcceptanceRestrictedToken token)
    {
        Instance.RequireDisposableJob();
        var candidate = FilteredHandle(token);
        var before = ReadSelfTokenStamp(candidate);
        if (before.Identity == Instance.ReadCurrentStamp().Identity)
            throw RestrictedOwnerToken.Failure(13);
        var descriptor = AuthorOwnedSelfDescriptor(candidate);
        if (before != ReadSelfTokenStamp(candidate))
            throw RestrictedOwnerToken.Failure(13);
        return descriptor;
    }

    private static byte[] AuthorOwnedSelfDescriptor(SafeAccessTokenHandle candidate)
    {
        using var user = TokenBuffer.Read(candidate, (int)AcceptanceSelfTokenInformation.User);
        using var owner = TokenBuffer.Read(candidate, (int)AcceptanceSelfTokenInformation.Owner);
        using var group = TokenBuffer.Read(candidate, (int)AcceptanceSelfTokenInformation.PrimaryGroup);
        using var dacl = TokenBuffer.Read(candidate, (int)AcceptanceSelfTokenInformation.DefaultDacl);
        using var restricted = TokenBuffer.Read(candidate, (int)AcceptanceSelfTokenInformation.RestrictedSids);
        var restrictingSids = ReadGroups(restricted)
            .Select(entry => CopySelfSid(restricted, entry.Sid)).ToArray();
        return AcceptanceOwnedSelfDacl.Author(
            ReadSelfUser(user), CopySelfSid(owner, owner.ReadSid(0)),
            CopySelfSid(group, group.ReadSid(0)),
            AcceptanceOwnedSelfDacl.CopyDefaultDacl(dacl.Pointer, dacl.Length),
            restrictingSids);
    }

    private static SecurityIdentifier ReadSelfUser(TokenBuffer data)
    {
        if (data.Length < sizeof(SidAndAttributes))
            throw RestrictedOwnerToken.Failure(13);
        if ((Marshal.ReadInt32(data.Pointer, nint.Size) & 0x10) != 0)
            throw RestrictedOwnerToken.Failure(50);
        return CopySelfSid(data, data.ReadSid(0));
    }

    private static SecurityIdentifier CopySelfSid(TokenBuffer data, nint address)
    {
        var sid = data.RequireSid(address);
        var bytes = new byte[8 + 4 * Marshal.ReadByte(sid, 1)];
        Marshal.Copy(sid, bytes, 0, bytes.Length);
        return new SecurityIdentifier(bytes, 0);
    }

    private static (long Identity, long Modification) ReadSelfTokenStamp(SafeHandle token)
    {
        using var data = TokenBuffer.Read(token, StatisticsInformation);
        if (data.Length != 56)
            throw RestrictedOwnerToken.Failure(13);
        return (Marshal.ReadInt64(data.Pointer), Marshal.ReadInt64(data.Pointer, 48));
    }
}
