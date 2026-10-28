using System.Collections.Immutable;
using System.Text.Json;

namespace Hermes.Tray.Access;

internal static class OwnerWire
{
    public static byte[] Request<T>(
        string requestId, string? expectedInstanceId, string operation, T payload)
    {
        AccessJson.RequireProof(requestId, 16);
        if (expectedInstanceId is { } instance)
            AccessJson.RequireProof(instance, 16);
        if (operation is not ("status" or "resolve" or "approve") ||
            (operation != "status" && expectedInstanceId is null))
            throw new AccessProtocolFault(OwnerFailure.Protocol);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1, requestId, expectedInstanceId, operation, payload
        });
        return bytes.Length <= 8192 ? bytes : throw new AccessProtocolFault(OwnerFailure.Protocol);
    }

    public static OwnerResult<T> Response<T>(
        ReadOnlyMemory<byte> bytes, string requestId, OwnerTarget target,
        OwnerProcessIdentity owned, string? expectedInstanceId,
        Func<OwnerInstance, JsonElement, T> readResult) =>
        AccessJson.Read(bytes, root =>
            ReadResponse(root, requestId, target, owned, expectedInstanceId, readResult));

    private static OwnerResult<T> ReadResponse<T>(
        JsonElement root, string requestId, OwnerTarget target,
        OwnerProcessIdentity owned, string? expectedInstanceId,
        Func<OwnerInstance, JsonElement, T> readResult)
    {
        AccessJson.Object(root, "version", "requestId", "instance", "ok", "result", "error");
        if (AccessJson.Integer(root, "version") != 1 ||
            AccessJson.Identifier(root, "requestId") != requestId)
            throw new AccessProtocolFault(OwnerFailure.Protocol);
        var instance = Instance(root.GetProperty("instance"));
        ValidateIdentity(instance, target, owned, expectedInstanceId);
        return AccessJson.Boolean(root, "ok")
            ? Success(root, instance, readResult) : Failure<T>(root);
    }

    private static OwnerResult<T> Success<T>(
        JsonElement root, OwnerInstance instance, Func<OwnerInstance, JsonElement, T> readResult)
    {
        if (root.GetProperty("error").ValueKind != JsonValueKind.Null ||
            root.GetProperty("result").ValueKind != JsonValueKind.Object)
            throw new AccessProtocolFault(OwnerFailure.Protocol);
        return new OwnerResult<T>.Success(readResult(instance, root.GetProperty("result")));
    }

    private static OwnerResult<T> Failure<T>(JsonElement root)
    {
        if (root.GetProperty("result").ValueKind != JsonValueKind.Null)
            throw new AccessProtocolFault(OwnerFailure.Protocol);
        var error = root.GetProperty("error");
        AccessJson.Object(error, "code", "message");
        // The ADR fixes codes, but does not enumerate pipe message text. Never surface peer text.
        _ = AccessJson.Text(error, "message", 1024);
        return new OwnerResult<T>.Failure(ErrorCode(AccessJson.Text(error, "code")));
    }

    public static OwnerInstance Instance(JsonElement value)
    {
        AccessJson.Object(value, "instanceId", "processId", "processStartTimeUtc", "origin", "configKey");
        var processId = AccessJson.Integer(value, "processId");
        var key = AccessJson.Text(value, "configKey", 64);
        if (processId <= 0 || key.Length != 64 ||
            key.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new AccessProtocolFault(OwnerFailure.Protocol);
        return new(AccessJson.Identifier(value, "instanceId"), processId,
            AccessJson.Timestamp(value, "processStartTimeUtc"),
            AccessJson.Text(value, "origin"), key);
    }

    public static void ValidateIdentity(
        OwnerInstance instance, OwnerTarget target, OwnerProcessIdentity owned, string? expectedInstanceId)
    {
        if (instance.ConfigKey != target.ConfigKey || instance.Origin != target.ApiOrigin ||
            instance.Process != owned ||
            (expectedInstanceId is { } expected && instance.InstanceId != expected))
            throw new AccessProtocolFault(OwnerFailure.IdentityMismatch);
    }

    public static OwnerStatus Status(OwnerInstance instance, JsonElement value)
    {
        AccessJson.Object(value, "phase", "capabilities");
        var phase = AccessJson.Text(value, "phase") switch
        {
            "starting" => OwnerPhase.Starting,
            "ready" => OwnerPhase.Ready,
            "stopping" => OwnerPhase.Stopping,
            _ => throw new AccessProtocolFault(OwnerFailure.Protocol)
        };
        return new(instance, phase, AccessJson.Capabilities(value, "capabilities", true));
    }

    public static OwnerPairing Pairing(JsonElement value)
    {
        AccessJson.Object(value, "pairingId", "userCode", "challenge", "browserOrigin", "apiOrigin",
            "requestedCapabilities", "createdAt", "expiresAt", "state");
        var state = AccessJson.Text(value, "state") switch
        {
            "pending" => PairingState.Pending,
            "approved" => PairingState.Approved,
            _ => throw new AccessProtocolFault(OwnerFailure.Protocol)
        };
        var pairing = new OwnerPairing(AccessJson.Identifier(value, "pairingId"),
            AccessJson.Code(value, "userCode"), AccessJson.Challenge(value, "challenge"),
            AccessJson.Text(value, "browserOrigin"), AccessJson.Text(value, "apiOrigin"),
            AccessJson.Capabilities(value, "requestedCapabilities", false),
            AccessJson.Timestamp(value, "createdAt"), AccessJson.Timestamp(value, "expiresAt"), state);
        return pairing.CreatedAt < pairing.ExpiresAt
            ? pairing : throw new AccessProtocolFault(OwnerFailure.Protocol);
    }

    public static OwnerApproval Approval(JsonElement value)
    {
        AccessJson.Object(value, "pairingId", "state", "capabilities", "expiresAt");
        if (AccessJson.Text(value, "state") != "approved")
            throw new AccessProtocolFault(OwnerFailure.Protocol);
        return new(AccessJson.Identifier(value, "pairingId"),
            AccessJson.Capabilities(value, "capabilities", false),
            AccessJson.Timestamp(value, "expiresAt"));
    }

    public static OwnerFailure ErrorCode(string value) => value switch
    {
        "invalid_request" => OwnerFailure.InvalidRequest,
        "unsupported_version" => OwnerFailure.UnsupportedVersion,
        "wrong_instance" => OwnerFailure.WrongInstance,
        "instance_not_ready" => OwnerFailure.InstanceNotReady,
        "pairing_not_found" => OwnerFailure.PairingNotFound,
        "pairing_expired" => OwnerFailure.PairingExpired,
        "pairing_consumed" => OwnerFailure.PairingConsumed,
        "pairing_conflict" => OwnerFailure.PairingConflict,
        "insufficient_capability" => OwnerFailure.InsufficientCapability,
        "access_capacity_reached" => OwnerFailure.AccessCapacityReached,
        "internal_error" => OwnerFailure.InternalError,
        _ => throw new AccessProtocolFault(OwnerFailure.Protocol)
    };
}
