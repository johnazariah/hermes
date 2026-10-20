using System.Collections.Immutable;
using Hermes.Tray.Access;

namespace Hermes.Tray;

// Composition boundary only: private framing, principal validation and pairing policy
// remain in the approved client and owned-navigation adapter.
internal sealed class VerifiedOwnerControl(
    OwnerTarget target, IOwnerControlClient owner,
    IPublicAccessIdentity publicIdentity, IAccessEnvironment environment) : IOwnerControlClient
{
    public async Task<OwnerResult<OwnerStatus>> GetStatusAsync(
        OwnerProcessIdentity owned, string? expectedInstanceId, CancellationToken cancellationToken)
    {
        if (Capture(owned) is not { } capture)
            return Failed<OwnerStatus>(OwnerFailure.IdentityMismatch);
        var result = await owner.GetStatusAsync(owned, expectedInstanceId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Current(capture))
            return Failed<OwnerStatus>(OwnerFailure.IdentityMismatch);
        if (result is not OwnerResult<OwnerStatus>.Success { Value: { Phase: OwnerPhase.Ready } status })
            return result;
        return await ConfirmAsync(capture, status.Instance, cancellationToken) is { } failure
            ? Failed<OwnerStatus>(failure) : result;
    }

    public Task<OwnerResult<OwnerPairing>> ResolveAsync(
        OwnerInstance pinned, string userCode, CancellationToken cancellationToken) =>
        InvokeAsync(pinned, token => owner.ResolveAsync(pinned, userCode, token), cancellationToken);

    public Task<OwnerResult<OwnerApproval>> ApproveAsync(
        OwnerInstance pinned, OwnerPairing pairing,
        ImmutableArray<AccessCapability> capabilities, CancellationToken cancellationToken) =>
        InvokeAsync(pinned, token => owner.ApproveAsync(pinned, pairing, capabilities, token),
            cancellationToken);

    private async Task<OwnerResult<T>> InvokeAsync<T>(
        OwnerInstance pinned, Func<CancellationToken, Task<OwnerResult<T>>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Capture(pinned.Process) is not { } capture)
            return Failed<T>(OwnerFailure.IdentityMismatch);
        if (await ConfirmAsync(capture, pinned, cancellationToken) is { } failure)
            return Failed<T>(failure);
        var result = await operation(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Current(capture) ? result : Failed<T>(OwnerFailure.IdentityMismatch);
    }

    private async Task<OwnerFailure?> ConfirmAsync(
        AccessEnvironmentSnapshot capture, OwnerInstance pinned, CancellationToken cancellationToken)
    {
        if (!Current(capture) || pinned.Process != capture.Process.Identity ||
            pinned.ConfigKey != target.ConfigKey || pinned.Origin != target.ApiOrigin)
            return OwnerFailure.IdentityMismatch;
        var result = await publicIdentity.ReadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Current(capture))
            return OwnerFailure.IdentityMismatch;
        return result switch
        {
            OwnerResult<PublicAccessIdentity>.Success { Value: var value }
                when value.InstanceId == pinned.InstanceId && value.Process == pinned.Process &&
                    value.Origin == pinned.Origin => null,
            OwnerResult<PublicAccessIdentity>.Failure failure => failure.Code,
            _ => OwnerFailure.IdentityMismatch
        };
    }

    private AccessEnvironmentSnapshot? Capture(OwnerProcessIdentity owned) =>
        environment.Read() is { Completed: true, Document: AccessDocument.CanonicalReactApplication } capture &&
        capture.Process.IsAlive && capture.Process.Identity == owned ? capture : null;

    private bool Current(AccessEnvironmentSnapshot capture) =>
        environment.Read() is { Completed: true, Document: AccessDocument.CanonicalReactApplication } current &&
        current.Process.IsAlive && ReferenceEquals(current.Process, capture.Process) &&
        current.Process.Identity == capture.Process.Identity &&
        current.RestartGeneration == capture.RestartGeneration &&
        current.NativeNavigationId == capture.NativeNavigationId &&
        current.Source.AbsoluteUri == capture.Source.AbsoluteUri;

    private static OwnerResult<T> Failed<T>(OwnerFailure failure) =>
        new OwnerResult<T>.Failure(failure);
}
