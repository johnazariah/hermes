using System.Collections.Immutable;
using Hermes.Servicing;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed class OrchestratorTests
{
    [Fact]
    public async Task FirstInstallPublishesOnlyAfterDurableCallAndVerification()
    {
        var effects = new FakeEffects(Samples.Fresh());
        var result = await effects.RunAsync(Samples.Install(Samples.First));
        Assert.True(result.Report.Ready);
        Assert.Equal(SetupExitCode.Success, result.ExitCode);
        Assert.Equal(SetupAdmissionState.Ready, result.Admission);
        Assert.Equal(GateDisposition.Ready, effects.Lease.State);
        Assert.False(effects.Lease.IsHeld);
        Assert.Equal(new[]
        {
            EffectEvent.Acquire, EffectEvent.Inspect, EffectEvent.Close,
            EffectEvent.Retain, EffectEvent.Prepare, EffectEvent.SaveJournal,
            EffectEvent.Execute, EffectEvent.SaveJournal, EffectEvent.DisposeCall,
            EffectEvent.Verify, EffectEvent.PublishReady, EffectEvent.DisposeLease
        }, effects.Events);
        Assert.Equal(Samples.First.Identity.ProductCode,
            effects.Lease.Published?.Installation?.ProductCode);
    }

    [Fact]
    public async Task ChangedPayloadUpgradeRestrictsNestedRemovalToExactOldProduct()
    {
        var effects = new FakeEffects(Samples.Installed(Samples.First, Samples.Second));
        var result = await effects.RunAsync(Samples.Install(Samples.Second));
        Assert.True(result.Report.Ready);
        var request = Assert.Single(effects.Requests);
        Assert.Equal(MaintenanceKind.Install, request.Step.Operation);
        Assert.Equal(new[] { Samples.First.Identity.ProductCode },
            request.Step.NestedRemovalProducts);
        Assert.Equal(Samples.Second.Identity.ProductCode,
            effects.Lease.Published?.Installation?.ProductCode);
    }

    [Fact]
    public async Task DowngradeWithoutExplicitPermissionRefusesBeforeClosure()
    {
        var effects = new FakeEffects(Samples.Installed(Samples.Second, Samples.First));
        var result = await effects.RunAsync(Samples.Install(Samples.First));
        Assert.Equal(SetupCode.RollbackNotAllowed, result.Code);
        Assert.False(result.Report.Ready);
        Assert.DoesNotContain(EffectEvent.Close, effects.Events);
        Assert.Empty(effects.Requests);
    }

    [Fact]
    public async Task ExplicitRollbackUsesStandardInstallAndOldProductParticipant()
    {
        var effects = new FakeEffects(Samples.Installed(Samples.Second, Samples.First));
        var result = await effects.RunAsync(Samples.Install(Samples.First, rollback: true));
        Assert.True(result.Report.Ready);
        var request = Assert.Single(effects.Requests);
        Assert.True(request.Step.AllowRollback);
        Assert.Equal(new[] { Samples.Second.Identity.ProductCode },
            request.Step.NestedRemovalProducts);
    }

    [Fact]
    public async Task SamePackageInstallBecomesGuardedRepair()
    {
        var effects = new FakeEffects(Samples.Installed(Samples.First, Samples.First));
        var result = await effects.RunAsync(Samples.Install(Samples.First));
        Assert.True(result.Report.Ready);
        Assert.Equal(MaintenanceKind.Repair, Assert.Single(effects.Requests).Step.Operation);
        Assert.Empty(effects.Requests[0].Step.NestedRemovalProducts);
    }

    [Fact]
    public async Task SameProductCodeWithDifferentPackageIsNotInventedAsMinorUpgrade()
    {
        var changed = Samples.Second with
        {
            Identity = Samples.Second.Identity with
            { ProductCode = Samples.First.Identity.ProductCode }
        };
        var effects = new FakeEffects(Samples.Installed(Samples.First, changed));
        var result = await effects.RunAsync(Samples.Install(changed));
        Assert.Equal(SetupCode.UnsupportedPackageChange, result.Code);
        Assert.DoesNotContain(EffectEvent.Close, effects.Events);
    }

    [Theory]
    [InlineData(SetupVerb.Repair, MaintenanceKind.Repair)]
    [InlineData(SetupVerb.Uninstall, MaintenanceKind.Uninstall)]
    public async Task MaintenanceUsesOwnedProduct(SetupVerb verb, MaintenanceKind operation)
    {
        var effects = new FakeEffects(Samples.Installed(Samples.First));
        var result = await effects.RunAsync(Samples.Maintenance(verb));
        Assert.True(result.Report.Ready);
        Assert.Equal(operation, Assert.Single(effects.Requests).Step.Operation);
        Assert.Equal(Samples.First.Identity.ProductCode,
            effects.Requests[0].Step.Package.Identity.ProductCode);
        Assert.Equal(verb == SetupVerb.Uninstall,
            effects.Lease.Published?.Installation is null);
    }

    [Fact]
    public async Task FailedCallWithVerifiedRestorationPublishesPreviousButReturnsFailure()
    {
        var previous = new PresentTarget(Samples.First);
        var scenario = new Scenario
        {
            MsiCodes = [1603],
            Verification = _ => new(previous, false)
        };
        var effects = new FakeEffects(Samples.Installed(Samples.First, Samples.Second), scenario);
        var result = await effects.RunAsync(Samples.Install(Samples.Second));
        Assert.True(result.Report.Ready);
        Assert.Equal(SetupOutcome.RolledBack, result.Report.Outcome);
        Assert.Equal(SetupExitCode.ServicingFailed, result.ExitCode);
        Assert.Equal(1603, result.Report.MsiExitCode);
        Assert.Equal(Samples.First.Identity.ProductCode,
            effects.Lease.Published?.Installation?.ProductCode);
    }

    [Fact]
    public async Task FailedFirstInstallCanPublishVerifiedRestoredAbsence()
    {
        var effects = new FakeEffects(Samples.Fresh(), new()
        {
            MsiCodes = [1603],
            Verification = _ => new(AbsentTarget.Instance, false)
        });
        var result = await effects.RunAsync(Samples.Install(Samples.First));
        Assert.True(result.Report.Ready);
        Assert.Equal(SetupOutcome.RolledBack, result.Report.Outcome);
        Assert.NotNull(effects.Lease.Published);
        Assert.Null(effects.Lease.Published?.Installation);
        Assert.Equal(SetupExitCode.ServicingFailed, result.ExitCode);
    }

    [Fact]
    public async Task UnknownRollbackRemainsClosed()
    {
        var effects = new FakeEffects(Samples.Installed(Samples.First, Samples.Second), new()
        {
            MsiCodes = [1603],
            Verification = _ => new(null, false)
        });
        var result = await effects.RunAsync(Samples.Install(Samples.Second));
        Assert.False(result.Report.Ready);
        Assert.True(effects.Lease.IsClosed);
        Assert.DoesNotContain(EffectEvent.PublishReady, effects.Events);
    }

    [Fact]
    public async Task BusyRefusesWithoutAnyFurtherEffect()
    {
        var effects = new FakeEffects(Samples.Fresh(), new() { AcquireError = SetupCode.Busy });
        var result = await effects.RunAsync(Samples.Install(Samples.First));
        Assert.Equal(SetupExitCode.Busy, result.ExitCode);
        Assert.False(result.Report.Ready);
        Assert.Equal(new[] { EffectEvent.Acquire }, effects.Events);
        Assert.False(effects.Lease.IsClosed);
    }

    [Theory]
    [InlineData(SetupCode.Collision)]
    [InlineData(SetupCode.OwnershipRefused)]
    [InlineData(SetupCode.IncompatibleInstallation)]
    [InlineData(SetupCode.RootMismatch)]
    [InlineData(SetupCode.MediaInvalid)]
    public async Task InspectionRefusalsDoNotCloseOrInvoke(object value)
    {
        var error = Assert.IsType<SetupCode>(value);
        var effects = new FakeEffects(Samples.Fresh(), new() { InspectionError = error });
        var result = await effects.RunAsync(Samples.Install(Samples.First));
        Assert.Equal(error, result.Code);
        Assert.Equal(new[]
        {
            EffectEvent.Acquire, EffectEvent.Inspect, EffectEvent.DisposeLease
        }, effects.Events);
        Assert.False(effects.Lease.IsClosed);
    }

    [Fact]
    public async Task FailedClosedPublicationNeverRetainsMediaOrInvokes()
    {
        var effects = new FakeEffects(Samples.Fresh(), new() { CloseFails = true });
        var result = await effects.RunAsync(Samples.Install(Samples.First));
        Assert.Equal(SetupCode.ClosedPublicationFailed, result.Code);
        Assert.False(result.Report.Ready);
        Assert.Equal(SetupAdmissionState.Unknown, result.Admission);
        Assert.Equal(GateDisposition.Missing, effects.Lease.State);
        Assert.DoesNotContain(EffectEvent.Retain, effects.Events);
        Assert.DoesNotContain(EffectEvent.Prepare, effects.Events);
    }

    [Fact]
    public async Task RetentionFailureLeavesClosedWithoutInvoking()
    {
        var effects = new FakeEffects(Samples.Fresh(),
            new() { RetentionError = SetupCode.MediaInvalid });
        var result = await effects.RunAsync(Samples.Install(Samples.First));
        Assert.Equal(SetupCode.MediaInvalid, result.Code);
        Assert.True(effects.Lease.IsClosed);
        Assert.Empty(effects.Requests);
    }

    [Fact]
    public async Task CacheIdentityChangeLeavesClosedWithoutInvoking()
    {
        var effects = new FakeEffects(Samples.Fresh(), new() { TamperRetainedManifest = true });
        var result = await effects.RunAsync(Samples.Install(Samples.First));
        Assert.Equal(SetupCode.MediaIdentityMismatch, result.Code);
        Assert.True(effects.Lease.IsClosed);
        Assert.DoesNotContain(EffectEvent.Execute, effects.Events);
    }

    [Fact]
    public async Task JournalMustBeDurableBeforeEngineEntry()
    {
        var effects = new FakeEffects(Samples.Fresh(), new() { JournalFailureAt = 1 });
        var result = await effects.RunAsync(Samples.Install(Samples.First));
        Assert.Equal(SetupCode.JournalPublicationFailed, result.Code);
        Assert.DoesNotContain(EffectEvent.Execute, effects.Events);
        Assert.Contains(EffectEvent.DisposeCall, effects.Events);
        Assert.True(effects.Lease.IsClosed);
    }

    [Fact]
    public async Task ReceiptPublicationFailureCannotPublishReady()
    {
        var effects = new FakeEffects(Samples.Fresh(), new() { JournalFailureAt = 2 });
        var result = await effects.RunAsync(Samples.Install(Samples.First));
        Assert.Equal(SetupCode.JournalPublicationFailed, result.Code);
        Assert.Contains(EffectEvent.Execute, effects.Events);
        Assert.DoesNotContain(EffectEvent.Verify, effects.Events);
        Assert.DoesNotContain(EffectEvent.PublishReady, effects.Events);
    }

    [Fact]
    public async Task PreparationFailureDoesNotEnterEngine()
    {
        var effects = new FakeEffects(Samples.Fresh(),
            new() { PreparationError = SetupCode.MsiPreparationFailed });
        var result = await effects.RunAsync(Samples.Install(Samples.First));
        Assert.Equal(SetupCode.MsiPreparationFailed, result.Code);
        Assert.DoesNotContain(EffectEvent.Execute, effects.Events);
        Assert.True(effects.Lease.IsClosed);
    }

    [Fact]
    public async Task InterruptedExecutionHasDurableContextAndDoesNotPublishReady()
    {
        var effects = new FakeEffects(Samples.Fresh(), new()
        {
            OnExecute = () => throw new SimulatedInterruptionException()
        });
        await Assert.ThrowsAsync<SimulatedInterruptionException>(
            () => effects.RunAsync(Samples.Install(Samples.First)));
        Assert.Single(effects.Records);
        Assert.NotEqual(Guid.Empty, effects.Records[0].ActiveRunId);
        Assert.True(effects.Lease.IsClosed);
        Assert.False(effects.Lease.IsHeld);
        Assert.DoesNotContain(EffectEvent.PublishReady, effects.Events);
    }

    [Fact]
    public async Task MissingOwnTransactionFenceCannotPublishReady()
    {
        var effects = new FakeEffects(Samples.Fresh(), new() { MissingOwnFence = true });
        var result = await effects.RunAsync(Samples.Install(Samples.First));
        Assert.Equal(SetupCode.TransactionFenceMissing, result.Code);
        Assert.True(effects.Lease.IsClosed);
        Assert.DoesNotContain(EffectEvent.Verify, effects.Events);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task IncompleteOrFaultedCallCannotPublishReady(bool incomplete, bool faulted)
    {
        var effects = new FakeEffects(Samples.Fresh(), new()
        {
            ApiDoesNotReturn = incomplete,
            ControlFaulted = faulted
        });
        var result = await effects.RunAsync(Samples.Install(Samples.First));
        Assert.Equal(SetupCode.MsiDidNotComplete, result.Code);
        Assert.True(effects.Lease.IsClosed);
        Assert.DoesNotContain(EffectEvent.Verify, effects.Events);
    }

    [Theory]
    [InlineData(3010u)]
    [InlineData(1641u)]
    public async Task RebootResultNeverPublishesReady(uint code)
    {
        var effects = new FakeEffects(Samples.Fresh(), new() { MsiCodes = [code] });
        var result = await effects.RunAsync(Samples.Install(Samples.First));
        Assert.Equal(SetupExitCode.RebootRequired, result.ExitCode);
        Assert.Equal(SetupOutcome.RebootRequired, result.Report.Outcome);
        Assert.False(result.Report.Ready);
        Assert.True(effects.Lease.IsClosed);
    }

    [Fact]
    public async Task RelevantPendingMutationsPreventReadyDespiteMatchingInventory()
    {
        var effects = new FakeEffects(Samples.Fresh(), new()
        {
            Verification = expected => new(expected, true)
        });
        var result = await effects.RunAsync(Samples.Install(Samples.First));
        Assert.Equal(SetupExitCode.RebootRequired, result.ExitCode);
        Assert.True(effects.Lease.IsClosed);
        Assert.DoesNotContain(EffectEvent.PublishReady, effects.Events);
    }

    [Fact]
    public async Task FailedReadyPublicationAttemptsReclosureAndReportsFailure()
    {
        var effects = new FakeEffects(Samples.Fresh(), new() { ReadyFails = true });
        var result = await effects.RunAsync(Samples.Install(Samples.First));
        Assert.Equal(SetupCode.ReadyPublicationFailed, result.Code);
        Assert.False(result.Report.Ready);
        Assert.Equal(2, effects.Events.Count(item => item == EffectEvent.Close));
        Assert.True(effects.Lease.IsClosed);
    }

    [Theory]
    [InlineData(LeaseFault.Failure)]
    [InlineData(LeaseFault.IoException)]
    [InlineData(LeaseFault.AccessDenied)]
    [InlineData(LeaseFault.Cancelled)]
    public async Task PublicationFaultAfterReadyReclosesBeforeDisposal(object value)
    {
        var fault = Assert.IsType<LeaseFault>(value);
        var effects = new FakeEffects(Samples.Fresh(), new() { ReadyFault = fault });
        var result = await effects.RunAsync(Samples.Install(Samples.First));
        AssertPublicationFailure(effects, result,
            SetupCode.ReadyPublicationFailed, SetupAdmissionState.Closed);
        Assert.Equal(GateDisposition.Closed, effects.Lease.State);
    }

    [Theory]
    [InlineData(LeaseFault.Failure, LeaseFault.Failure)]
    [InlineData(LeaseFault.Failure, LeaseFault.IoException)]
    [InlineData(LeaseFault.Failure, LeaseFault.AccessDenied)]
    [InlineData(LeaseFault.Failure, LeaseFault.Cancelled)]
    [InlineData(LeaseFault.Failure, LeaseFault.UnconfirmedSuccess)]
    [InlineData(LeaseFault.Failure, LeaseFault.FailureAfterChange)]
    [InlineData(LeaseFault.IoException, LeaseFault.Failure)]
    [InlineData(LeaseFault.IoException, LeaseFault.IoException)]
    [InlineData(LeaseFault.IoException, LeaseFault.AccessDenied)]
    [InlineData(LeaseFault.IoException, LeaseFault.Cancelled)]
    [InlineData(LeaseFault.IoException, LeaseFault.UnconfirmedSuccess)]
    [InlineData(LeaseFault.IoException, LeaseFault.FailureAfterChange)]
    public async Task FailedReclosureReportsUnknownWithoutClaimingDenial(
        object publicationValue, object reclosureValue)
    {
        var publication = Assert.IsType<LeaseFault>(publicationValue);
        var reclosure = Assert.IsType<LeaseFault>(reclosureValue);
        var effects = new FakeEffects(Samples.Fresh(), new()
        {
            ReadyFault = publication, ReclosureFault = reclosure
        });
        var result = await effects.RunAsync(Samples.Install(Samples.First));
        AssertPublicationFailure(effects, result,
            SetupCode.ClosedPublicationFailed, SetupAdmissionState.Unknown);
        Assert.Equal(reclosure == LeaseFault.FailureAfterChange
            ? GateDisposition.Closed : GateDisposition.Ready, effects.Lease.State);
    }

    [Fact]
    public async Task PublicationFailureWaitsForUncancelledReclosureBeforeDisposal()
    {
        using var cancellation = new CancellationTokenSource();
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var effects = new FakeEffects(Samples.Fresh(), new()
        {
            ReadyFault = LeaseFault.IoException,
            ReclosureWait = resume.Task, OnExecute = cancellation.Cancel
        });
        var execution = effects.RunAsync(Samples.Install(Samples.First), cancellation.Token);
        try { AssertReclosurePending(effects, execution); }
        finally { resume.SetResult(); }
        var result = await execution;
        Assert.True(cancellation.IsCancellationRequested);
        AssertPublicationFailure(effects, result,
            SetupCode.ReadyPublicationFailed, SetupAdmissionState.Closed);
        Assert.Equal(GateDisposition.Closed, effects.Lease.State);
    }

    private static void AssertReclosurePending(
        FakeEffects effects, Task<SetupExecution> execution)
    {
        Assert.False(execution.IsCompleted);
        Assert.True(effects.Lease.IsHeld);
        Assert.False(effects.Lease.IsHeldAndClosed);
        Assert.Equal(GateDisposition.Ready, effects.Lease.State);
        Assert.Equal(EffectEvent.Close, effects.Events[^1]);
        Assert.DoesNotContain(EffectEvent.DisposeLease, effects.Events);
    }

    private static void AssertPublicationFailure(
        FakeEffects effects, SetupExecution result,
        SetupCode code, SetupAdmissionState admission)
    {
        Assert.Equal(code, result.Code);
        Assert.Equal(admission, result.Admission);
        Assert.Equal(SetupExitCode.ServicingFailed, result.ExitCode);
        Assert.Equal(SetupOutcome.RecoveryRequired, result.Report.Outcome);
        Assert.False(result.Report.Ready);
        Assert.Equal(0, result.Report.MsiExitCode);
        Assert.NotNull(effects.Lease.Published);
        Assert.False(effects.Lease.IsHeld);
        Assert.Single(effects.Events, item => item == EffectEvent.PublishReady);
        Assert.Single(effects.Events, item => item == EffectEvent.DisposeLease);
        Assert.Equal(2, effects.Events.Count(item => item == EffectEvent.Close));
        Assert.Equal(new[]
        {
            EffectEvent.Verify, EffectEvent.PublishReady,
            EffectEvent.Close, EffectEvent.DisposeLease
        }, effects.Events.TakeLast(4));
    }

    [Fact]
    public async Task CancellationDuringActualCallDoesNotAbandonCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        var effects = new FakeEffects(Samples.Fresh(), new()
        {
            OnExecute = cancellation.Cancel
        });
        var result = await effects.RunAsync(Samples.Install(Samples.First), cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(result.Report.Ready);
        Assert.Contains(EffectEvent.Verify, effects.Events);
    }

    [Fact]
    public async Task CancellationBeforeAcquisitionHasNoEffects()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var effects = new FakeEffects(Samples.Fresh());
        var result = await effects.RunAsync(Samples.Install(Samples.First), cancellation.Token);
        Assert.Equal(SetupCode.Cancelled, result.Code);
        Assert.Empty(effects.Events);
    }

    [Fact]
    public async Task ClosedStateRejectsNormalServicingWithoutAnotherClosure()
    {
        var facts = Samples.Interrupted(AbsentTarget.Instance, AbsentTarget.Instance,
            new PresentTarget(Samples.First), [Samples.First]);
        var effects = new FakeEffects(facts);
        var result = await effects.RunAsync(Samples.Install(Samples.First));
        Assert.Equal(SetupCode.RecoveryRequired, result.Code);
        Assert.DoesNotContain(EffectEvent.Close, effects.Events);
        Assert.Empty(effects.Requests);
    }

    [Fact]
    public async Task RecoveryRecordsRevokedOldRunBeforeInvokingNewRun()
    {
        var facts = Samples.Interrupted(new PresentTarget(Samples.First),
            new PresentTarget(Samples.First), new PresentTarget(Samples.Second),
            [Samples.First, Samples.Second]);
        var old = facts.Interrupted ?? throw new InvalidOperationException();
        var effects = new FakeEffects(facts);
        var result = await effects.RunAsync(Samples.Maintenance(SetupVerb.Recover));
        Assert.True(result.Report.Ready);
        var started = effects.Records[0];
        Assert.NotEqual(old.ActiveRunId, started.ActiveRunId);
        Assert.Contains(old.ActiveRunId, started.RevokedRunIds);
        Assert.Equal(old, started.Plan.Interrupted);
        Assert.Empty(started.Receipts);
    }

    [Fact]
    public async Task RecoveryCannotReuseRevokedActiveRun()
    {
        var facts = Samples.Interrupted(AbsentTarget.Instance, AbsentTarget.Instance,
            new PresentTarget(Samples.First), [Samples.First]);
        var old = facts.Interrupted ?? throw new InvalidOperationException();
        var effects = new FakeEffects(facts, new() { ReusedRunId = old.ActiveRunId });
        var result = await effects.RunAsync(Samples.Maintenance(SetupVerb.Recover));
        Assert.Equal(SetupCode.JournalInvalid, result.Code);
        Assert.DoesNotContain(EffectEvent.Execute, effects.Events);
        Assert.True(effects.Lease.IsClosed);
    }

    [Theory]
    [InlineData(1618u)]
    [InlineData(1605u)]
    public async Task RecoveryContentionOrAbsentResultDoesNotProveBarrier(uint code)
    {
        var facts = Samples.Interrupted(new PresentTarget(Samples.First),
            new PresentTarget(Samples.First), AbsentTarget.Instance, [Samples.First]);
        var effects = new FakeEffects(facts, new() { MsiCodes = [code] });
        var result = await effects.RunAsync(Samples.Maintenance(SetupVerb.Recover));
        Assert.False(result.Report.Ready);
        Assert.True(effects.Lease.IsClosed);
        Assert.DoesNotContain(EffectEvent.PublishReady, effects.Events);
    }

    [Fact]
    public async Task RecoveryRequiresItsOwnFenceEvenWhenDesiredBytesVerify()
    {
        var facts = Samples.Interrupted(new PresentTarget(Samples.First),
            new PresentTarget(Samples.First), new PresentTarget(Samples.First),
            [Samples.First]);
        var effects = new FakeEffects(facts, new() { MissingOwnFence = true });
        var result = await effects.RunAsync(Samples.Maintenance(SetupVerb.Recover));
        Assert.Equal(SetupCode.TransactionFenceMissing, result.Code);
        Assert.False(result.Report.Ready);
        Assert.DoesNotContain(EffectEvent.Verify, effects.Events);
    }

    [Fact]
    public async Task AbsentRecoveryPerformsGuardedInstallThenGuardedUninstall()
    {
        var facts = Samples.Interrupted(AbsentTarget.Instance,
            new PresentTarget(Samples.First), AbsentTarget.Instance, [Samples.First]);
        var effects = new FakeEffects(facts, new() { MsiCodes = [0, 0] });
        var result = await effects.RunAsync(Samples.Maintenance(SetupVerb.Recover));
        Assert.True(result.Report.Ready);
        Assert.Equal(new[] { MaintenanceKind.Install, MaintenanceKind.Uninstall },
            effects.Requests.Select(request => request.Step.Operation));
        Assert.Equal(2, effects.Events.Count(item => item == EffectEvent.Verify));
        Assert.Single(effects.Events, item => item == EffectEvent.PublishReady);
        Assert.Null(effects.Lease.Published?.Installation);
        Assert.Contains(effects.Records[0].ActiveRunId, effects.Records[2].RevokedRunIds);
    }

    [Fact]
    public async Task FailedBootstrapDoesNotContinueToUninstallOrPublish()
    {
        var facts = Samples.Interrupted(AbsentTarget.Instance,
            new PresentTarget(Samples.First), AbsentTarget.Instance, [Samples.First]);
        var effects = new FakeEffects(facts, new() { MsiCodes = [1603] });
        var result = await effects.RunAsync(Samples.Maintenance(SetupVerb.Recover));
        Assert.False(result.Report.Ready);
        Assert.Single(effects.Requests);
        Assert.True(effects.Lease.IsClosed);
        Assert.DoesNotContain(EffectEvent.PublishReady, effects.Events);
    }

    [Fact]
    public async Task BootstrapVerificationFailureDoesNotContinueToUninstall()
    {
        var facts = Samples.Interrupted(AbsentTarget.Instance,
            new PresentTarget(Samples.First), AbsentTarget.Instance, [Samples.First]);
        var effects = new FakeEffects(facts, new()
        {
            MsiCodes = [0],
            Verification = _ => new(null, false)
        });
        var result = await effects.RunAsync(Samples.Maintenance(SetupVerb.Recover));
        Assert.Equal(SetupCode.VerificationFailed, result.Code);
        Assert.Single(effects.Requests);
        Assert.True(effects.Lease.IsClosed);
    }

    [Fact]
    public async Task FailedRecoveryCannotReopenMerelyBecausePreviousStateIsSafe()
    {
        var previous = new PresentTarget(Samples.First);
        var facts = Samples.Interrupted(previous, previous,
            new PresentTarget(Samples.Second), [Samples.First, Samples.Second]);
        var effects = new FakeEffects(facts, new()
        {
            MsiCodes = [1603],
            Verification = _ => new(previous, false)
        });
        var result = await effects.RunAsync(Samples.Maintenance(SetupVerb.Recover));
        Assert.False(result.Report.Ready);
        Assert.True(effects.Lease.IsClosed);
        Assert.DoesNotContain(EffectEvent.PublishReady, effects.Events);
    }

    [Fact]
    public async Task ReadyInstallationDoesNotRequireOrPermitSecondRecoveryPass()
    {
        var effects = new FakeEffects(Samples.Installed(Samples.First));
        var result = await effects.RunAsync(Samples.Maintenance(SetupVerb.Recover));
        Assert.Equal(SetupCode.NoRecoveryPending, result.Code);
        Assert.DoesNotContain(EffectEvent.Close, effects.Events);
        Assert.Empty(effects.Requests);
    }

    [Fact]
    public async Task RecoveryRefusesUnrecordedCurrentOwner()
    {
        var facts = Samples.Interrupted(new PresentTarget(Samples.Second),
            new PresentTarget(Samples.First), AbsentTarget.Instance, [Samples.First]);
        var effects = new FakeEffects(facts);
        var result = await effects.RunAsync(Samples.Maintenance(SetupVerb.Recover));
        Assert.Equal(SetupCode.OwnershipRefused, result.Code);
        Assert.DoesNotContain(EffectEvent.Close, effects.Events);
        Assert.Empty(effects.Requests);
    }

    [Fact]
    public async Task DifferentPackageRootIsRefusedBeforeClosure()
    {
        var relocated = Samples.First with { AppDirectory = Path.GetFullPath("/other/App") };
        var effects = new FakeEffects(Samples.Fresh(relocated));
        var result = await effects.RunAsync(Samples.Install(relocated));
        Assert.Equal(SetupCode.RootMismatch, result.Code);
        Assert.DoesNotContain(EffectEvent.Close, effects.Events);
    }

    [Fact]
    public async Task MachineScopePackageIsRefusedBeforeClosure()
    {
        var machine = Samples.First with
        {
            Identity = Samples.First.Identity with { Scope = PackageScope.Machine }
        };
        var effects = new FakeEffects(Samples.Fresh(machine));
        var result = await effects.RunAsync(Samples.Install(machine));
        Assert.Equal(SetupCode.IncompatibleInstallation, result.Code);
        Assert.DoesNotContain(EffectEvent.Close, effects.Events);
    }

    [Fact]
    public async Task RetainedSourcesAreOutsideApplicationAndUseExactManifestEvidence()
    {
        var effects = new FakeEffects(Samples.Fresh());
        await effects.RunAsync(Samples.Install(Samples.First));
        var media = Assert.Single(effects.Records[0].Media);
        Assert.Equal(Samples.First.ManifestSha256, media.Evidence.ManifestSha256);
        Assert.StartsWith(Samples.Scope.PackagesDirectory, media.PackagePath);
        Assert.StartsWith(Samples.Scope.PackagesDirectory, media.ManifestPath);
        Assert.False(media.PackagePath.StartsWith(
            Samples.Scope.AppDirectory, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExistingParserPreservesAbsolutePackagePathContainingSpaces()
    {
        var parsed = Assert.IsType<ParsedCommand>(SetupCommandLine.Parse(
            ["install", "--package", Samples.First.SourcePath, "--quiet", "--allow-rollback"]));
        var command = Assert.IsType<InstallCommand>(parsed.Command);
        Assert.Equal(Samples.First.SourcePath, command.PackagePath);
        Assert.True(command.AllowRollback);
        Assert.True(command.Quiet);
    }

    [Theory]
    [InlineData("INSTALLFOLDER=/other")]
    [InlineData("installfolder=/other")]
    [InlineData("LOCATION=/other")]
    [InlineData("--installfolder")]
    [InlineData("ALLUSERS=1")]
    public void ExistingParserRejectsRootAndMsiPropertyInjection(string property) =>
        Assert.IsType<InvalidCommand>(SetupCommandLine.Parse(
            ["install", "--package", Samples.First.SourcePath, property]));

    [Fact]
    public async Task SharedPublicReportContainsNoMediaOrPrivateControlFields()
    {
        var effects = new FakeEffects(Samples.Fresh());
        var result = await effects.RunAsync(Samples.Install(Samples.First));
        var json = result.Report.ToJson();
        Assert.DoesNotContain(Samples.First.SourcePath, json);
        Assert.DoesNotContain(Samples.Scope.AppDirectory, json);
        Assert.DoesNotContain("challenge", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pipe", json, StringComparison.OrdinalIgnoreCase);
    }
}
