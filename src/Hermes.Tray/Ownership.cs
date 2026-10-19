using System.Collections.Immutable;

namespace Hermes.Tray;

internal enum SupervisionDisposition { Cancelled, Failed }

internal sealed record OwnedIdentity(int ProcessId, int Restart);
internal sealed record OwnedStop(OwnedIdentity Identity, StopOutcome Outcome);
internal sealed record SupervisionResult(
    SupervisionDisposition Disposition, ServiceSnapshot Snapshot,
    OwnedIdentity? LastOwned, ImmutableArray<OwnedStop> Stops);

internal interface IOwnershipVerifier
{
    Task<OwnedIdentity> VerifyOwnershipAsync(
        OwnedIdentity expected, CancellationToken cancellationToken);
}
