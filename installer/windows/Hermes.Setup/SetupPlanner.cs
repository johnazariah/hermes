using System.Collections.Immutable;
using Hermes.Servicing;

namespace Hermes.Setup;

internal static class SetupPlanner
{
    internal static Effect<OperationPlan> Create(SetupCommand command, Inspection facts)
    {
        var refusal = Preconditions(command, facts);
        if (refusal != SetupCode.None)
            return Effect<OperationPlan>.Fail(refusal);
        var planned = command switch
        {
            InstallCommand install => Install(install, facts),
            MaintenanceCommand { Operation: SetupVerb.Recover } recovery =>
                Recover(recovery, facts),
            MaintenanceCommand maintenance => Maintain(maintenance, facts),
            _ => Effect<OperationPlan>.Fail(SetupCode.InvalidArguments)
        };
        return planned is Effect<OperationPlan>.Success(var plan)
            ? Validate(plan) : planned;
    }

    private static SetupCode Preconditions(SetupCommand command, Inspection facts) =>
        (command, facts) switch
        {
            (_, { Gate: GateDisposition.Missing, Observed: PresentTarget }) =>
                SetupCode.OwnershipRefused,
            (MaintenanceCommand { Operation: SetupVerb.Recover },
                { Gate: not GateDisposition.Closed }) => SetupCode.NoRecoveryPending,
            (MaintenanceCommand { Operation: SetupVerb.Recover },
                { Interrupted: null }) => SetupCode.JournalInvalid,
            (not MaintenanceCommand { Operation: SetupVerb.Recover },
                { Gate: GateDisposition.Closed }) => SetupCode.RecoveryRequired,
            _ => SetupCode.None
        };

    private static Effect<OperationPlan> Install(InstallCommand command, Inspection facts)
    {
        if (facts.Candidate is not { } candidate)
            return Effect<OperationPlan>.Fail(SetupCode.MediaInvalid);
        var chosen = InstallStep(facts.Observed, candidate, command.AllowRollback, command.Quiet);
        return chosen is Effect<EngineStep>.Success(var step)
            ? Build(SetupVerb.Install, facts, facts.Observed,
                new PresentTarget(candidate), [step])
            : Effect<OperationPlan>.Fail(chosen.FailureCode);
    }

    private static Effect<EngineStep> InstallStep(
        InstallationTarget observed, PackageEvidence package, bool allowRollback, bool quiet)
    {
        if (observed is PresentTarget previous)
        {
            if (SetupIdentity.SameContent(previous.Package, package))
                return Effect<EngineStep>.Ok(Step(MaintenanceKind.Repair, package, quiet));
            if (previous.Package.Identity.ProductCode == package.Identity.ProductCode)
                return Effect<EngineStep>.Fail(SetupCode.UnsupportedPackageChange);
            if (previous.Package.Identity.Version > package.Identity.Version && !allowRollback)
                return Effect<EngineStep>.Fail(SetupCode.RollbackNotAllowed);
        }
        ImmutableArray<Guid> nested = observed is PresentTarget old
            ? [old.Package.Identity.ProductCode] : [];
        return Effect<EngineStep>.Ok(new(
            MaintenanceKind.Install, package, nested, allowRollback, quiet));
    }

    private static Effect<OperationPlan> Maintain(
        MaintenanceCommand command, Inspection facts)
    {
        if (facts.Observed is not PresentTarget present)
            return Effect<OperationPlan>.Fail(SetupCode.NotInstalled);
        return command.Operation switch
        {
            SetupVerb.Repair => Build(command.Operation, facts, facts.Observed,
                facts.Observed, [Step(MaintenanceKind.Repair, present.Package, command.Quiet)]),
            SetupVerb.Uninstall => Build(command.Operation, facts, facts.Observed,
                AbsentTarget.Instance,
                [Step(MaintenanceKind.Uninstall, present.Package, command.Quiet)]),
            _ => Effect<OperationPlan>.Fail(SetupCode.InvalidArguments)
        };
    }

    private static Effect<OperationPlan> Recover(
        MaintenanceCommand command, Inspection facts)
    {
        if (facts.Interrupted is not { } interrupted ||
            interrupted.ActiveRunId == Guid.Empty || interrupted.RevokedRunIds.IsDefault ||
            interrupted.Packages.IsDefault)
            return Effect<OperationPlan>.Fail(SetupCode.JournalInvalid);
        if (facts.Observed is PresentTarget observed &&
            !interrupted.Packages.Any(package =>
                SetupIdentity.SameContent(package, observed.Package)))
            return Effect<OperationPlan>.Fail(SetupCode.OwnershipRefused);
        return interrupted.Desired switch
        {
            PresentTarget desired => RecoverPresent(command, facts, interrupted, desired),
            AbsentTarget => RecoverAbsent(command, facts, interrupted),
            _ => Effect<OperationPlan>.Fail(SetupCode.JournalInvalid)
        };
    }

    private static Effect<OperationPlan> RecoverPresent(
        MaintenanceCommand command, Inspection facts,
        InterruptedOperation interrupted, PresentTarget desired)
    {
        var chosen = InstallStep(facts.Observed, desired.Package, true, command.Quiet);
        return chosen is Effect<EngineStep>.Success(var step)
            ? Build(SetupVerb.Recover, facts, interrupted.Previous, desired, [step])
            : Effect<OperationPlan>.Fail(chosen.FailureCode);
    }

    private static Effect<OperationPlan> RecoverAbsent(
        MaintenanceCommand command, Inspection facts, InterruptedOperation interrupted)
    {
        if (facts.Observed is PresentTarget current)
            return Build(SetupVerb.Recover, facts, interrupted.Previous, AbsentTarget.Instance,
                [Step(MaintenanceKind.Uninstall, current.Package, command.Quiet)]);
        var barrier = interrupted.Previous is PresentTarget previous
            ? previous.Package : interrupted.Packages.FirstOrDefault();
        if (barrier is null)
            return Effect<OperationPlan>.Fail(SetupCode.RecoveryMediaUnavailable);
        return Build(SetupVerb.Recover, facts, interrupted.Previous, AbsentTarget.Instance,
            [new(MaintenanceKind.Install, barrier, [], true, command.Quiet),
             Step(MaintenanceKind.Uninstall, barrier, command.Quiet)]);
    }

    private static EngineStep Step(
        MaintenanceKind operation, PackageEvidence package, bool quiet) =>
        new(operation, package, [], false, quiet);

    private static Effect<OperationPlan> Build(
        SetupVerb verb, Inspection facts, InstallationTarget previous,
        InstallationTarget desired, ImmutableArray<EngineStep> steps) =>
        Effect<OperationPlan>.Ok(new(verb, facts.Scope, previous, desired, steps,
            verb == SetupVerb.Recover ? facts.Interrupted : null));

    private static Effect<OperationPlan> Validate(OperationPlan plan)
    {
        if (plan.Steps.IsDefaultOrEmpty)
            return Effect<OperationPlan>.Fail(SetupCode.InvalidEvidence);
        if (plan.Packages.Any(package =>
            !SetupIdentity.SamePath(package.AppDirectory, plan.Scope.AppDirectory)))
            return Effect<OperationPlan>.Fail(SetupCode.RootMismatch);
        return plan.Packages.All(ProtocolCompatible)
            ? Effect<OperationPlan>.Ok(plan)
            : Effect<OperationPlan>.Fail(SetupCode.IncompatibleInstallation);
    }

    private static bool ProtocolCompatible(PackageEvidence package) =>
        package.Identity.UpgradeCode == HermesServicingProtocol.UpgradeCode &&
        package.Identity.AdmissionAbi == HermesServicingProtocol.AdmissionAbi &&
        package.Identity.Scope == PackageScope.PerUser &&
        package.Identity.ProductCode != Guid.Empty &&
        package.Identity.PackageCode != Guid.Empty &&
        !package.Images.IsDefaultOrEmpty;
}

internal static class CompletionPolicy
{
    internal static LeaveClosed? BeforeVerification(
        EngineReceipt receipt, bool recovery) => receipt switch
        {
            { CompletedApi: false } => new(SetupCode.MsiDidNotComplete),
            { NativeCode: null } => new(SetupCode.InvalidEvidence),
            { MsiExitCode: 3010 or 1641 } => Reboot(),
            { MsiExitCode: 1618 } => new(SetupCode.Busy, SetupExitCode.Busy),
            { MsiExitCode: 1605 } => new(SetupCode.MsiCallFailed),
            { OwnPreflightOccurred: false } or { OwnTransactionFenceOccurred: false } =>
                new(SetupCode.TransactionFenceMissing),
            { MsiExitCode: not 0 } when recovery => new(SetupCode.MsiCallFailed),
            _ => null
        };

    internal static CompletionDecision Final(
        OperationPlan plan, EngineReceipt receipt, VerificationEvidence evidence)
    {
        if (BeforeVerification(receipt, plan.IsRecovery) is { } blocked)
            return blocked;
        if (evidence.RelevantPendingMutations)
            return Reboot();
        if (receipt.MsiExitCode == 0 &&
            SetupIdentity.SameTarget(evidence.ProvenTarget, plan.Desired))
            return new PublishInstallation(plan.Desired,
                SetupOutcome.Completed, SetupExitCode.Success, SetupCode.None);
        if (!plan.IsRecovery && receipt.MsiExitCode != 0 &&
            SetupIdentity.SameTarget(evidence.ProvenTarget, plan.Previous))
            return new PublishInstallation(plan.Previous, SetupOutcome.RolledBack,
                SetupExitCode.ServicingFailed, SetupCode.MsiCallFailed);
        return new LeaveClosed(receipt.MsiExitCode == 0
            ? SetupCode.VerificationFailed : SetupCode.MsiCallFailed);
    }

    internal static LeaveClosed Reboot() => new(
        SetupCode.RebootRequired, SetupExitCode.RebootRequired, SetupOutcome.RebootRequired);
}
