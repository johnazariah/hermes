using Hermes.Tray;
using Hermes.Tray.Smoke;
using Xunit;

namespace Hermes.Tray.Tests;

public sealed class OwnershipOracleTests
{
    private static readonly OwnedIdentity Owner = new(1001, 0);
    private static ServiceSnapshot Running => new(ServicePhase.Running, EventCode.Healthy, Owner.ProcessId);
    private static SupervisionResult StableFinal => new(
        SupervisionDisposition.Cancelled, new(ServicePhase.Stopped, EventCode.Stopped),
        Owner, [new(Owner, new(true, false, true, 0))]);

    public enum ObservedBreak { Backoff, Starting, Replacement, Stopping, Stopped, Failed }

    [Theory]
    [InlineData(ObservedBreak.Backoff)]
    [InlineData(ObservedBreak.Starting)]
    [InlineData(ObservedBreak.Replacement)]
    [InlineData(ObservedBreak.Stopping)]
    [InlineData(ObservedBreak.Stopped)]
    [InlineData(ObservedBreak.Failed)]
    public async Task Unexpected_transition_permanently_invalidates_the_bound_owner(ObservedBreak broken)
    {
        var oracle = Bound(new OracleVerifier());
        await ActualProofAsync(oracle);
        oracle.Changed(BrokenSnapshot(broken));
        oracle.Changed(Running);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => oracle.PrepareStopAsync(CancellationToken.None));
        Assert.False(oracle.Evaluate(StableFinal, true).Passed);
    }

    public enum ProofFailure { Before, During, After, WrongIdentity }

    [Theory]
    [InlineData(ProofFailure.Before)]
    [InlineData(ProofFailure.During)]
    [InlineData(ProofFailure.After)]
    [InlineData(ProofFailure.WrongIdentity)]
    public async Task Failed_operation_or_identity_check_cannot_be_recovered_as_success(ProofFailure failure)
    {
        var verifier = new OracleVerifier(failure);
        var oracle = Bound(verifier);
        await Assert.ThrowsAnyAsync<Exception>(() => oracle.ProveAsync("actual-operation",
            _ => failure == ProofFailure.During
                ? Task.FromException<bool>(new FileNotFoundException("private-proof-content"))
                : Task.FromResult(true), CancellationToken.None));
        oracle.Changed(Running);
        var verdict = oracle.Evaluate(StableFinal, true);
        Assert.False(verdict.Passed);
        Assert.Empty(verdict.Checks);
        Assert.DoesNotContain("private-proof-content", verdict.Failure ?? "");
    }

    public enum FinalFailure
    {
        MissingProof, UnfinishedProof, NotAuthorized, Terminal, WrongLastOwner, WrongStopOwner,
        Forced, NotGraceful, NotExited, NonZeroExit, WrongFinalPhase, WrongFinalReason, MissingStop
    }

    [Theory]
    [InlineData(FinalFailure.MissingProof)]
    [InlineData(FinalFailure.UnfinishedProof)]
    [InlineData(FinalFailure.NotAuthorized)]
    [InlineData(FinalFailure.Terminal)]
    [InlineData(FinalFailure.WrongLastOwner)]
    [InlineData(FinalFailure.WrongStopOwner)]
    [InlineData(FinalFailure.Forced)]
    [InlineData(FinalFailure.NotGraceful)]
    [InlineData(FinalFailure.NotExited)]
    [InlineData(FinalFailure.NonZeroExit)]
    [InlineData(FinalFailure.WrongFinalPhase)]
    [InlineData(FinalFailure.WrongFinalReason)]
    [InlineData(FinalFailure.MissingStop)]
    public async Task Final_oracle_requires_all_success_conditions_even_without_failure_notifications(FinalFailure failure)
    {
        var oracle = Bound(new OracleVerifier());
        if (failure != FinalFailure.MissingProof)
            await ActualProofAsync(oracle);
        if (failure != FinalFailure.NotAuthorized)
            await oracle.PrepareStopAsync(CancellationToken.None);
        var verdict = oracle.Evaluate(BrokenFinal(failure), failure != FinalFailure.UnfinishedProof);
        Assert.False(verdict.Passed);
        Assert.NotNull(verdict.Failure);
    }

    [Fact]
    public void Terminal_failure_before_binding_cannot_be_erased_by_a_running_snapshot()
    {
        var oracle = new SmokeOwnershipOracle(new OracleVerifier());
        oracle.Changed(new(ServicePhase.Starting, EventCode.Starting));
        oracle.Changed(new(ServicePhase.Failed, EventCode.ForeignListener));
        Assert.Throws<InvalidOperationException>(() => oracle.Bind(Running));
        Assert.False(oracle.Evaluate(StableFinal, true).Passed);
    }

    [Fact]
    public void Binding_is_one_shot_and_starting_before_binding_is_expected()
    {
        var oracle = new SmokeOwnershipOracle(new OracleVerifier());
        oracle.Changed(new(ServicePhase.Starting, EventCode.Starting));
        Assert.Equal(Owner, oracle.Bind(Running));
        Assert.Throws<InvalidOperationException>(() => oracle.Bind(Running));
    }

    private static SmokeOwnershipOracle Bound(OracleVerifier verifier)
    {
        var oracle = new SmokeOwnershipOracle(verifier);
        oracle.Bind(Running);
        return oracle;
    }

    private static Task ActualProofAsync(SmokeOwnershipOracle oracle) =>
        oracle.ProveAsync("actual-operation", _ => Task.FromResult(true), CancellationToken.None);

    private static ServiceSnapshot BrokenSnapshot(ObservedBreak broken) => broken switch
    {
        ObservedBreak.Backoff => new(ServicePhase.Backoff, EventCode.Retrying),
        ObservedBreak.Starting => new(ServicePhase.Starting, EventCode.Starting),
        ObservedBreak.Replacement => new(ServicePhase.Running, EventCode.Healthy, 1002, 1),
        ObservedBreak.Stopping => new(ServicePhase.Stopping, EventCode.Cancelled, Owner.ProcessId),
        ObservedBreak.Stopped => new(ServicePhase.Stopped, EventCode.Stopped),
        _ => new(ServicePhase.Failed, EventCode.ForeignListener)
    };

    private static SupervisionResult BrokenFinal(FinalFailure failure)
    {
        var final = StableFinal;
        var other = new OwnedIdentity(9876, 1);
        return failure switch
        {
            FinalFailure.Terminal => final with
            {
                Disposition = SupervisionDisposition.Failed,
                Snapshot = new(ServicePhase.Failed, EventCode.ForeignListener)
            },
            FinalFailure.WrongLastOwner => final with { LastOwned = other },
            FinalFailure.WrongStopOwner => final with { Stops = [new(other, final.Stops[0].Outcome)] },
            FinalFailure.WrongFinalPhase => final with { Snapshot = Running },
            FinalFailure.WrongFinalReason => final with { Snapshot = new(ServicePhase.Stopped, EventCode.StopFailed) },
            FinalFailure.MissingStop => final with { Stops = [] },
            _ => WithStopFailure(final, failure)
        };
    }

    private static SupervisionResult WithStopFailure(SupervisionResult final, FinalFailure failure) =>
        final with
        {
            Stops = [new(Owner, failure switch
            {
                FinalFailure.Forced => new(false, true, true, 0),
                FinalFailure.NotGraceful => new(false, false, true, 0),
                FinalFailure.NotExited => new(true, false, false, null),
                FinalFailure.NonZeroExit => new(true, false, true, 37),
                _ => final.Stops[0].Outcome
            })]
        };
}

internal sealed class OracleVerifier(OwnershipOracleTests.ProofFailure? failure = null) : IOwnershipVerifier
{
    private int _calls;

    public Task<OwnedIdentity> VerifyOwnershipAsync(
        OwnedIdentity expected, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var call = Interlocked.Increment(ref _calls);
        if ((failure == OwnershipOracleTests.ProofFailure.Before && call == 1) ||
            (failure == OwnershipOracleTests.ProofFailure.After && call == 2))
            throw new IOException("private-verifier-content");
        return Task.FromResult(failure == OwnershipOracleTests.ProofFailure.WrongIdentity
            ? expected with { ProcessId = expected.ProcessId + 1 } : expected);
    }
}
