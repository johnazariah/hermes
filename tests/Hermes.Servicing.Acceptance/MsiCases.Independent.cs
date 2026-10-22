namespace Hermes.Servicing.Acceptance;

internal static partial class MsiCases
{
    private static async Task<CaseReport> ObservedCrashAsync(
        AcceptanceContext context, CrashCase item, IndependentMsiObserver observer,
        ImmutableArray<object>.Builder evidence, CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteObservedAsync(context, item, observer, evidence, cancellationToken);
        }
        catch (EvidenceGap)
        {
            evidence.Add(observer.Report());
            throw;
        }
    }

    private static async Task<CaseReport> ExecuteObservedAsync(
        AcceptanceContext context, CrashCase item, IndependentMsiObserver observer,
        ImmutableArray<object>.Builder evidence, CancellationToken cancellationToken)
    {
        var before = context.OwnerUnknown();
        using var old = await StartWorkerAsync(
            context, item.Operation, item.Hold, observer, cancellationToken);
        var oldEvents = await UntilPausedAsync(old, cancellationToken);
        ValidatePause(item, old, oldEvents);
        var expected = ExpectOld(context, item, observer, old.Identity, oldEvents);
        evidence.Add(new { oldCoordinator = old.Identity, oldEvents });
        await old.CrashAsync(cancellationToken);
        Files.RequireSame(before, context.OwnerUnknown());
        await observer.WaitOldAsync(expected, cancellationToken);
        return await RecoverObservedAsync(
            context, item, observer, expected, oldEvents[^1], before, evidence, cancellationToken);
    }

    private static async Task<CaseReport> RecoverObservedAsync(AcceptanceContext context,
        CrashCase item, IndependentMsiObserver observer, ExpectedOld old,
        RootEvent paused, ImmutableArray<FileEvidence> before,
        ImmutableArray<object>.Builder evidence, CancellationToken cancellationToken)
    {
        var attempts = await RecoverAsync(context, old.RunId, observer, cancellationToken);
        var completed = attempts[^1];
        ValidateRecovery(item, paused, completed);
        var fresh = ExpectFresh(observer, completed);
        var proof = await observer.CompleteAsync(old, fresh, cancellationToken);
        var after = context.OwnerUnknown();
        Files.RequireSame(before, after);
        evidence.Add(new { attempts, independent = observer.Report(), proof,
            ownerUnknownBefore = before, ownerUnknownAfter = after });
        return new(item.Name, "actual-msi-root-composition",
            CaseState.Passed, evidence.ToImmutable());
    }

    private static ExpectedOld ExpectOld(AcceptanceContext context, CrashCase item,
        IndependentMsiObserver observer, Stamp client, ImmutableArray<RootEvent> events)
    {
        var paused = events[^1];
        var boundary = item.Hold switch
        {
            HoldPoint.OwnPreflight => OldBoundary.BeforePreflight,
            HoldPoint.OwnTransaction => OldBoundary.BeforeTransaction,
            HoldPoint.AfterOwnFenceAction => OldBoundary.AfterParentFence,
            HoldPoint.ApiReturned => OldBoundary.Returned,
            _ => throw new EvidenceGap("UnsupportedCrashBoundary")
        };
        var host = OldHost(item, events);
        var returned = paused.Receipt is { MayEvaluateRecovery: true,
            MsiExitCode: 0, ObservationFaults: 0 };
        return new(IndependentMsiObserver.Key(client), paused.Journal.ActiveRunId,
            observer.Packages.ForCase(context, item), Operation(item.Operation),
            boundary, IndependentMsiObserver.Key(host), returned);
    }

    private static ProcessInstance OldHost(CrashCase item, ImmutableArray<RootEvent> events)
    {
        if (events[^1].Fence is { } pending)
            return pending.CaHost;
        if (item.Hold == HoldPoint.ApiReturned && events[^1].Receipt is { } receipt)
            return receipt.Fences.Single(fence =>
                fence.OwnSession && fence.Phase == GuardPhase.Transaction).CaHost;
        return events.Where(IsOwnTransactionAccepted)
            .Select(value => value.Fence ?? throw new EvidenceGap("MissingOldFence"))
            .Single().CaHost;
    }

    private static string Operation(string operation) => operation switch
    {
        "old" or "new" => "Install",
        "repair" => "Repair",
        "uninstall" => "Uninstall",
        _ => throw new EvidenceGap("UnsupportedOperation")
    };

    private static ImmutableArray<ExpectedFresh> ExpectFresh(
        IndependentMsiObserver observer, RecoveryAttempt completed)
    {
        var result = completed.Result;
        var raw = completed.Events.Where(value => value.Kind == "api-returned")
            .Select(value => value.Receipt ?? throw new EvidenceGap("MissingRawReceipt"))
            .ToArray();
        if (raw.Length != result.Operations.Length)
            throw new EvidenceGap("MissingStepOperationBinding");
        return raw.Select((receipt, index) =>
            ExpectFreshCall(observer, completed, receipt, result.Operations[index]))
            .ToImmutableArray();
    }

    private static ExpectedFresh ExpectFreshCall(IndependentMsiObserver observer,
        RecoveryAttempt completed, InstallerCallReceipt receipt, MaintenanceKind operation)
    {
        var preflight = receipt.Fences.Single(fence =>
            fence.OwnSession && fence.Phase == GuardPhase.Preflight);
        var transaction = receipt.Fences.Single(fence =>
            fence.OwnSession && fence.Phase == GuardPhase.Transaction);
        if (preflight.ProductCode != transaction.ProductCode)
            throw new EvidenceGap("MixedFreshProductIdentity");
        return new(IndependentMsiObserver.Key(completed.Process), receipt.RunId,
            observer.Packages.Product(transaction.ProductCode), operation.ToString(),
            IndependentMsiObserver.Key(preflight.CaHost),
            IndependentMsiObserver.Key(transaction.CaHost),
            receipt.MayEvaluateRecovery && receipt.ObservationFaults == 0,
            completed.ExitCode == 0 && completed.Result.DiagnosticCode == "None" &&
                completed.Result.Report is { Ready: true, Outcome: SetupOutcome.Completed });
    }
}
