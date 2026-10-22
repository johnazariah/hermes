using System.Collections.Immutable;
using System.Text.Json;

namespace Hermes.Tray.Access;

internal sealed class OwnerControlClient(OwnerTarget target, OwnerTransport transport)
    : IOwnerControlClient
{
    public Task<OwnerResult<OwnerStatus>> GetStatusAsync(
        OwnerProcessIdentity owned, string? expectedInstanceId, CancellationToken cancellationToken) =>
        SendAsync(owned, expectedInstanceId, "status", () => new { }, OwnerWire.Status, cancellationToken);

    public Task<OwnerResult<OwnerPairing>> ResolveAsync(
        OwnerInstance pinned, string userCode, CancellationToken cancellationToken) =>
        SendAsync(pinned.Process, pinned.InstanceId, "resolve",
            () => new { userCode = AccessJson.RequireCode(userCode) },
            (_, result) => OwnerWire.Pairing(result), cancellationToken);

    public Task<OwnerResult<OwnerApproval>> ApproveAsync(
        OwnerInstance pinned, OwnerPairing pairing,
        ImmutableArray<AccessCapability> capabilities, CancellationToken cancellationToken) =>
        SendAsync(pinned.Process, pinned.InstanceId, "approve", () => new
        {
            pairingId = AccessJson.RequireProof(pairing.PairingId, 16),
            userCode = AccessJson.RequireCode(pairing.UserCode),
            challenge = AccessJson.RequireProof(pairing.Challenge, 32),
            capabilities = GrantNames(pairing, capabilities)
        }, (_, result) => OwnerWire.Approval(result), cancellationToken);

    private static string[] GrantNames(
        OwnerPairing pairing, ImmutableArray<AccessCapability> capabilities)
    {
        if (capabilities.IsDefault ||
            capabilities.Any(capability => capability == AccessCapability.ArchiveRead ||
                !pairing.RequestedCapabilities.Contains(capability)))
            throw new AccessProtocolFault(OwnerFailure.InvalidRequest);
        var names = capabilities.Select(AccessCapabilities.Name).ToArray();
        if (!names.SequenceEqual(names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
            throw new AccessProtocolFault(OwnerFailure.InvalidRequest);
        return names;
    }

    private async Task<OwnerResult<T>> SendAsync<T>(
        OwnerProcessIdentity owned, string? instanceId, string operation, Func<object> payload,
        Func<OwnerInstance, JsonElement, T> readResult, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var requestId = AccessJson.NewIdentifier();
            var request = OwnerWire.Request(requestId, instanceId, operation, payload());
            var response = await transport.ExchangeAsync(target, request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return OwnerWire.Response(response, requestId, target, owned, instanceId, readResult);
        }
        catch (AccessProtocolFault error) { return new OwnerResult<T>.Failure(error.Failure); }
    }
}
