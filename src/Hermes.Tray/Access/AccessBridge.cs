using System.Collections.Immutable;
using System.Text.Json;

namespace Hermes.Tray.Access;

internal sealed record AccessReady(string NavigationId, string InstanceId);
internal sealed record AccessMessageSource(ulong NativeNavigationId, Uri Source, bool IsTopLevel);

internal abstract record AccessBrowserNotice(string NavigationId, string InstanceId);
internal sealed record AccessPairingNotice(
    string NavigationId, string InstanceId, string PairingId, string UserCode, string Challenge)
    : AccessBrowserNotice(NavigationId, InstanceId)
{
    public override string ToString() => "AccessPairingNotice { Challenge = [redacted] }";
}
internal sealed record AccessAuthenticatedNotice(
    string NavigationId, string InstanceId, ImmutableArray<AccessCapability> Capabilities,
    DateTimeOffset ExpiresAt) : AccessBrowserNotice(NavigationId, InstanceId);

internal static class AccessBridge
{
    public static byte[] Ready(AccessReady ready) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        version = 1, type = "hermes.access.ready",
        navigationId = ready.NavigationId, instanceId = ready.InstanceId
    });

    public static AccessBrowserNotice Read(ReadOnlyMemory<byte> bytes) =>
        AccessJson.Read(bytes, ReadNotice);

    private static AccessBrowserNotice ReadNotice(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            throw new AccessProtocolFault(OwnerFailure.Protocol);
        return type.GetString() switch
        {
            "hermes.access.pairing" => Pairing(root),
            "hermes.access.status" => Status(root),
            _ => throw new AccessProtocolFault(OwnerFailure.Protocol)
        };
    }

    private static void Version(JsonElement root)
    {
        if (AccessJson.Integer(root, "version") != 1)
            throw new AccessProtocolFault(OwnerFailure.Protocol);
    }

    private static AccessPairingNotice Pairing(JsonElement root)
    {
        AccessJson.Object(root, "version", "type", "navigationId", "instanceId",
            "pairingId", "userCode", "challenge");
        Version(root);
        return new(AccessJson.Identifier(root, "navigationId"),
            AccessJson.Identifier(root, "instanceId"), AccessJson.Identifier(root, "pairingId"),
            AccessJson.Code(root, "userCode"), AccessJson.Challenge(root, "challenge"));
    }

    private static AccessAuthenticatedNotice Status(JsonElement root)
    {
        AccessJson.Object(root, "version", "type", "navigationId", "instanceId",
            "state", "capabilities", "expiresAt");
        Version(root);
        if (AccessJson.Text(root, "state") != "authenticated")
            throw new AccessProtocolFault(OwnerFailure.Protocol);
        return new(AccessJson.Identifier(root, "navigationId"),
            AccessJson.Identifier(root, "instanceId"),
            AccessJson.Capabilities(root, "capabilities", true),
            AccessJson.Timestamp(root, "expiresAt"));
    }
}
