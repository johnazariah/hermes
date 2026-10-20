using System.Collections.Immutable;
using Hermes.Servicing;

namespace Hermes.Setup;

internal sealed class SetupOrchestrator(
    IAdmissionSource admission,
    IInstallationInspection inspection,
    IMediaRetention retention,
    IOperationJournal journalStore,
    IGuardedEngine engine,
    IInstallationVerification verification)
{
    internal async Task<SetupExecution> ExecuteAsync(
        SetupCommand command, CancellationToken cancellationToken)
    {
        var verb = Verb(command);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var acquired = admission.TryAcquire();
            return acquired is Effect<ISetupLease>.Success(var lease)
                ? await ExecuteLeasedAsync(command, lease, cancellationToken)
                : SetupExecution.Refused(verb, acquired.FailureCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return SetupExecution.Refused(verb, SetupCode.Cancelled); }
        catch (IOException) { return SetupExecution.Refused(verb, SetupCode.Unavailable); }
        catch (UnauthorizedAccessException)
        { return SetupExecution.Refused(verb, SetupCode.Unavailable); }
    }

    private async Task<SetupExecution> ExecuteLeasedAsync(
        SetupCommand command, ISetupLease lease, CancellationToken cancellationToken)
    {
        using (lease)
        {
            var inspected = await inspection.InspectAsync(command, lease, cancellationToken);
            if (inspected is not Effect<Inspection>.Success(var facts))
                return SetupExecution.Refused(Verb(command), inspected.FailureCode);
            var planned = SetupPlanner.Create(command, facts);
            return planned is Effect<OperationPlan>.Success(var plan)
                ? await ExecuteClosedAsync(plan, lease, cancellationToken)
                : SetupExecution.Refused(Verb(command), planned.FailureCode);
        }
    }

    private async Task<SetupExecution> ExecuteClosedAsync(
        OperationPlan plan, ISetupLease lease, CancellationToken cancellationToken)
    {
        try { return await CloseAndRetainAsync(plan, lease, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return ClosedOrUnknown(plan, lease, SetupCode.Cancelled); }
        catch (IOException) { return ClosedOrUnknown(plan, lease, SetupCode.Unavailable); }
        catch (UnauthorizedAccessException)
        { return ClosedOrUnknown(plan, lease, SetupCode.Unavailable); }
    }

    private async Task<SetupExecution> CloseAndRetainAsync(
        OperationPlan plan, ISetupLease lease, CancellationToken cancellationToken)
    {
        var closed = await lease.CloseAsync(cancellationToken);
        if (closed is not Effect<Unit>.Success || !lease.IsHeldAndClosed)
            return SetupExecution.Unknown(plan.Verb, SetupCode.ClosedPublicationFailed);
        var retained = await retention.RetainAsync(plan.Packages, cancellationToken);
        if (retained is not Effect<ImmutableArray<RetainedMedia>>.Success(var media))
            return Closed(plan, retained.FailureCode);
        if (!MediaMatches(plan.Packages, media))
            return Closed(plan, SetupCode.MediaIdentityMismatch);
        var record = new OperationJournal(Guid.NewGuid(),
            plan.Interrupted?.ActiveRunId ?? Guid.Empty,
            plan.Interrupted?.RevokedRunIds ?? [], plan, media, []);
        return await RunStepAsync(record, 0, lease, cancellationToken);
    }

    private async Task<SetupExecution> RunStepAsync(
        OperationJournal record, int index, ISetupLease lease,
        CancellationToken cancellationToken)
    {
        var step = record.Plan.Steps[index];
        var media = record.Media.Single(item => SetupIdentity.SameContent(item.Evidence, step.Package));
        var invoked = await InvokeAsync(record, new(record.Plan.Scope, step, media),
            lease, cancellationToken);
        if (invoked is not Effect<OperationJournal>.Success(var completed))
            return Closed(record.Plan, invoked.FailureCode);
        var receipt = completed.Receipts[^1];
        if (CompletionPolicy.BeforeVerification(receipt, record.Plan.IsRecovery) is { } blocked)
            return SetupExecution.Closed(record.Plan.Verb, blocked, receipt);
        return await VerifyStepAsync(completed, index, lease, CancellationToken.None);
    }

    private async Task<Effect<OperationJournal>> InvokeAsync(
        OperationJournal record, EngineRequest request, ISetupLease lease,
        CancellationToken cancellationToken)
    {
        if (!lease.IsHeldAndClosed)
            return Effect<OperationJournal>.Fail(SetupCode.ClosedPublicationFailed);
        var prepared = await engine.PrepareAsync(lease, request, cancellationToken);
        if (prepared is not Effect<IPreparedMsiCall>.Success(var call))
            return Effect<OperationJournal>.Fail(prepared.FailureCode);
        await using var ownedCall = call;
        if (!record.CanStart(call.RunId))
            return Effect<OperationJournal>.Fail(SetupCode.JournalInvalid);
        var started = record.ForRun(call.RunId);
        var saved = await journalStore.SaveAsync(started, cancellationToken);
        return saved is Effect<Unit>.Success
            ? await ExecuteRecordedAsync(started, call, lease, CancellationToken.None)
            : Effect<OperationJournal>.Fail(SetupCode.JournalPublicationFailed);
    }

    private async Task<Effect<OperationJournal>> ExecuteRecordedAsync(
        OperationJournal record, IPreparedMsiCall call, ISetupLease lease,
        CancellationToken cancellationToken)
    {
        if (!lease.IsHeldAndClosed)
            return Effect<OperationJournal>.Fail(SetupCode.ClosedPublicationFailed);
        var executed = call.Execute();
        if (executed is not Effect<EngineReceipt>.Success(var receipt))
            return Effect<OperationJournal>.Fail(executed.FailureCode);
        if (receipt.RunId != record.ActiveRunId)
            return Effect<OperationJournal>.Fail(SetupCode.InvalidEvidence);
        var completed = record with { Receipts = record.Receipts.Add(receipt) };
        var saved = await journalStore.SaveAsync(completed, cancellationToken);
        return saved is Effect<Unit>.Success
            ? Effect<OperationJournal>.Ok(completed)
            : Effect<OperationJournal>.Fail(SetupCode.JournalPublicationFailed);
    }

    private async Task<SetupExecution> VerifyStepAsync(
        OperationJournal record, int index, ISetupLease lease,
        CancellationToken cancellationToken)
    {
        var expected = record.Plan.Steps[index].Expected;
        var verified = await verification.VerifyAsync(record, expected, cancellationToken);
        if (verified is not Effect<VerificationEvidence>.Success(var evidence))
            return Closed(record.Plan, verified.FailureCode, record.Receipts[^1]);
        if (index == record.Plan.Steps.Length - 1)
            return await FinishAsync(record, evidence, lease, cancellationToken);
        if (evidence.RelevantPendingMutations)
            return SetupExecution.Closed(record.Plan.Verb, CompletionPolicy.Reboot(), record.Receipts[^1]);
        if (!SetupIdentity.SameTarget(evidence.ProvenTarget, expected))
            return Closed(record.Plan, SetupCode.VerificationFailed, record.Receipts[^1]);
        return await RunStepAsync(record, index + 1, lease, cancellationToken);
    }

    private async Task<SetupExecution> FinishAsync(
        OperationJournal record, VerificationEvidence evidence, ISetupLease lease,
        CancellationToken cancellationToken)
    {
        var receipt = record.Receipts[^1];
        var decision = CompletionPolicy.Final(record.Plan, receipt, evidence);
        if (decision is LeaveClosed closed)
            return SetupExecution.Closed(record.Plan.Verb, closed, receipt);
        if (decision is not PublishInstallation publish || !lease.IsHeldAndClosed)
            return Closed(record.Plan, SetupCode.InvalidEvidence, receipt);
        return await PublishAsync(record.Plan, publish, receipt, lease, cancellationToken);
    }

    private static async Task<SetupExecution> PublishAsync(
        OperationPlan plan, PublishInstallation publish, EngineReceipt receipt,
        ISetupLease lease, CancellationToken cancellationToken)
    {
        try
        {
            var ready = SetupIdentity.ReadyState(plan.Scope, publish.Target);
            var result = await lease.PublishReadyAsync(ready, cancellationToken);
            if (result is Effect<Unit>.Success)
                return SetupExecution.Published(plan.Verb, publish, receipt);
        }
        catch (Exception error) when (PublicationFailure(error)) { }
        return await RestoreClosedAsync(plan, lease, receipt, CancellationToken.None);
    }

    private static async Task<SetupExecution> RestoreClosedAsync(
        OperationPlan plan, ISetupLease lease, EngineReceipt receipt,
        CancellationToken cancellationToken)
    {
        try
        {
            var restored = await lease.CloseAsync(cancellationToken);
            if (restored is Effect<Unit>.Success && lease.IsHeldAndClosed)
                return Closed(plan, SetupCode.ReadyPublicationFailed, receipt);
        }
        catch (Exception error) when (PublicationFailure(error)) { }
        return SetupExecution.Unknown(plan.Verb, SetupCode.ClosedPublicationFailed, receipt);
    }

    private static bool PublicationFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or OperationCanceledException;

    private static bool MediaMatches(
        ImmutableArray<PackageEvidence> required, ImmutableArray<RetainedMedia> retained) =>
        !retained.IsDefault && required.Length == retained.Length &&
        required.All(package => retained.Count(item =>
            SetupIdentity.SameContent(package, item.Evidence)) == 1);

    private static SetupExecution ClosedOrUnknown(
        OperationPlan plan, ISetupLease lease, SetupCode code) =>
        lease.IsHeldAndClosed
            ? Closed(plan, code)
            : SetupExecution.Unknown(plan.Verb, code);

    private static SetupExecution Closed(
        OperationPlan plan, SetupCode code, EngineReceipt? receipt = null) =>
        SetupExecution.Closed(plan.Verb, new(code), receipt);

    private static SetupVerb Verb(SetupCommand command) => command switch
    {
        InstallCommand => SetupVerb.Install,
        MaintenanceCommand maintenance => maintenance.Operation,
        _ => throw new ArgumentException("Unsupported setup command.", nameof(command))
    };
}
