using System.Collections.Immutable;
using Hermes.Servicing;

namespace Hermes.Setup;

// Nonblocking exclusive access to the stable admission.bin file object.
// Refusal must not modify the gate, application, running processes or user data.
internal interface IAdmissionSource
{
    Effect<ISetupLease> TryAcquire();
}

internal interface ISetupLease : IDisposable
{
    string GatePath { get; }
    // True only with live ownership and known durable Closed publication.
    bool IsHeldAndClosed { get; }

    // Successful closure means the Closed header has been flushed to disk.
    Task<Effect<Unit>> CloseAsync(CancellationToken cancellationToken);

    // Ready may become visible before a returned failure or exception.
    // The orchestrator awaits uncancelled reclosure before releasing the lease.
    // Failed or unconfirmed reclosure leaves durable admission unknown.
    Task<Effect<Unit>> PublishReadyAsync(
        GateReadyState ready, CancellationToken cancellationToken);
}

internal interface IInstallationInspection
{
    // Read-only under the exclusive lease. Establish invoking-user known-folder
    // identity, fixed physical root, gate/registration/component ownership,
    // related products, package/database/manifest identity, complete owned
    // inventory and collision safety, including recovery bootstrap candidates.
    // Unknown files are not adopted. Owner settings/archive are not inputs.
    // A detected ungated/foreign registration is a refusal, not an uninstall.
    Task<Effect<Inspection>> InspectAsync(
        SetupCommand command,
        ISetupLease lease,
        CancellationToken cancellationToken);
}

internal interface IMediaRetention
{
    // Called only after durable closure. Retain and flush complete, usable
    // source MSI media and its exact validated manifest in the fixed servicing
    // cache. Verify copied identity/content. LocalPackage alone is insufficient.
    // This capability must never copy application payload into AppRoot.
    Task<Effect<ImmutableArray<RetainedMedia>>> RetainAsync(
        IReadOnlyList<PackageEvidence> packages,
        CancellationToken cancellationToken);
}

internal interface IOperationJournal
{
    // Publish and flush the complete operation record before reporting success.
    // Interrupted context and revoked/new run IDs must survive a process crash.
    Task<Effect<Unit>> SaveAsync(
        OperationJournal journal, CancellationToken cancellationToken);
}

internal interface ISetupJournalReader
{
    OperationJournal? Read();
}

internal interface IGuardedEngine
{
    // Prepare the existing private control server, minting its real RunId.
    // No MSI operation may start in PrepareAsync.
    Task<Effect<IPreparedMsiCall>> PrepareAsync(
        ISetupLease lease,
        EngineRequest request,
        CancellationToken cancellationToken);
}

internal interface IPreparedMsiCall : IAsyncDisposable
{
    Guid RunId { get; }

    // Synchronous by design: do not return, dispose ownership, or honor
    // cancellation while the actual standard MSI API/rollback is outstanding.
    Effect<EngineReceipt> Execute();
}

internal interface IInstallationVerification
{
    // Called only after synchronous MSI return and durable receipt recording.
    // Prove full product/installation/component/payload inventory, hashes/MVIDs,
    // registration, shortcuts, Run state and relevant pending file operations.
    // Compare both expected and recorded previous targets where applicable.
    // Do not read settings/archive or treat unknown files as owned payload.
    Task<Effect<VerificationEvidence>> VerifyAsync(
        OperationJournal journal,
        InstallationTarget expected,
        CancellationToken cancellationToken);
}
