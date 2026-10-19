using System.Threading.Channels;
using Hermes.Tray;
using Hermes.Tray.Smoke;
using Xunit;

namespace Hermes.Tray.Tests;

public sealed class OwnershipTests
{
    private const string CanonicalArtifact = "canonical-react";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Stable_owned_proof_and_graceful_stop_pass_with_the_actual_stopped_pid()
    {
        await using var rig = new Rig(new ManualClock());
        var oracle = Attach(rig);
        rig.Start();
        var pid = await rig.Service.Ready.WaitAsync(Deadline);
        oracle.Bind(rig.Service.Snapshot);
        Assert.Equal(CanonicalArtifact,
            await oracle.ProveAsync("webview:stable", _ => Task.FromResult(CanonicalArtifact), CancellationToken.None));
        await oracle.PrepareStopAsync(CancellationToken.None);
        await rig.StopAsync();
        var verdict = oracle.Evaluate(await rig.Service.Final, true);
        Assert.True(verdict.Passed, verdict.Failure);
        Assert.Equal(pid, verdict.Identity?.ProcessId);
        Assert.Equal(rig.RequireCurrent().Id, verdict.Stop?.Identity.ProcessId);
        Assert.Equal(pid, verdict.Stop?.Identity.ProcessId);
        Assert.Equal(new StopOutcome(true, false, true, 0), verdict.Stop?.Outcome);
        Assert.All(verdict.Checks, check => Assert.Equal(check.Before, check.After));
    }

    [Fact]
    public async Task Health_loss_then_foreign_same_artifact_cannot_pass_using_the_old_graceful_stop()
    {
        var clock = new ManualClock();
        await using var rig = new Rig(clock);
        var oracle = Attach(rig);
        rig.Start();
        var pid = await rig.Service.Ready.WaitAsync(Deadline);
        oracle.Bind(rig.Service.Snapshot);
        var entered = Signal();
        var servedByReplacement = Signal();
        var proof = oracle.ProveAsync("http:same-artifact",
            token => DelayedArtifactAsync(entered, servedByReplacement, token), CancellationToken.None);
        await entered.Task.WaitAsync(Deadline);
        var foreign = new FakeProcess(9876, true);
        await InstallForeignAfterHealthLossAsync(rig, clock, foreign);
        servedByReplacement.TrySetResult();
        await Assert.ThrowsAsync<LifecycleFault>(() => proof);
        AssertRejectedReplacement(rig, oracle.Evaluate(await rig.Service.Final, true), foreign, pid);
        Assert.Equal(pid, await rig.Service.Ready);
    }

    public enum InvalidObservation { Unhealthy, Foreign, Exited, NotRunning }

    [Theory]
    [InlineData(InvalidObservation.Unhealthy)]
    [InlineData(InvalidObservation.Foreign)]
    [InlineData(InvalidObservation.Exited)]
    [InlineData(InvalidObservation.NotRunning)]
    public async Task Ownership_checks_reject_invalid_current_child(InvalidObservation invalid)
    {
        await using var rig = new Rig(new ManualClock());
        rig.Start();
        var pid = await rig.Service.Ready.WaitAsync(Deadline);
        var identity = new OwnedIdentity(pid, rig.Service.Snapshot.Restart);
        await ApplyInvalidObservationAsync(rig, invalid);
        var error = await Assert.ThrowsAsync<LifecycleFault>(
            () => rig.Service.VerifyOwnershipAsync(identity, CancellationToken.None));
        var expected = invalid switch
        {
            InvalidObservation.Unhealthy => EventCode.HealthLost,
            InvalidObservation.Exited => EventCode.ChildExited,
            _ => EventCode.IdentityMismatch
        };
        Assert.Equal(expected, error.Code);
    }

    [Fact]
    public async Task Ownership_checks_and_supervision_share_the_serialized_health_probe()
    {
        var clock = new ManualClock();
        await using var rig = new Rig(clock);
        rig.Start();
        var pid = await rig.Service.Ready.WaitAsync(Deadline);
        var identity = new OwnedIdentity(pid, 0);
        var entered = Signal();
        var release = Signal();
        rig.Health.Override = token => BlockingHealthAsync(rig, entered, release, token);
        var first = rig.Service.VerifyOwnershipAsync(identity, CancellationToken.None);
        await entered.Task.WaitAsync(Deadline);
        var second = rig.Service.VerifyOwnershipAsync(identity, CancellationToken.None);
        await clock.AdvanceAsync();
        Assert.Equal(1, rig.Health.Active);
        release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(Deadline);
        await rig.StopAsync();
        Assert.Equal(1, rig.Health.MaximumConcurrent);
    }

    [Fact]
    public async Task Cancelled_proof_check_does_not_turn_expected_worker_quit_into_failure()
    {
        await using var rig = new Rig(new ManualClock());
        rig.Start();
        var pid = await rig.Service.Ready.WaitAsync(Deadline);
        var entered = Signal();
        var release = Signal();
        rig.Health.Override = token => BlockingHealthAsync(rig, entered, release, token);
        using var cancellation = new CancellationTokenSource();
        var check = rig.Service.VerifyOwnershipAsync(new(pid, 0), cancellation.Token);
        await entered.Task.WaitAsync(Deadline);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        await rig.StopAsync();
        var final = await rig.Service.Final;
        Assert.Equal(SupervisionDisposition.Cancelled, final.Disposition);
        Assert.Equal(new StopOutcome(true, false, true, 0), final.Stops.Single().Outcome);
    }

    private static SmokeOwnershipOracle Attach(Rig rig)
    {
        var oracle = new SmokeOwnershipOracle(rig.Service);
        rig.ObserveWith(oracle);
        return oracle;
    }

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<string> DelayedArtifactAsync(
        TaskCompletionSource entered, TaskCompletionSource release, CancellationToken cancellationToken)
    {
        entered.TrySetResult();
        await release.Task.WaitAsync(cancellationToken);
        return CanonicalArtifact;
    }

    private static async Task InstallForeignAfterHealthLossAsync(Rig rig, ManualClock clock, FakeProcess foreign)
    {
        rig.Health.Healthy = false;
        foreach (var _ in Enumerable.Range(0, 3))
            await clock.AdvanceAsync();
        await rig.WaitAsync(ServicePhase.Backoff);
        Assert.True(rig.RequireCurrent().HasExited && rig.RequireCurrent().Disposed);
        rig.Health.Foreign = new(true, true, foreign.Id);
        await clock.AdvanceAsync();
        await rig.Worker.WaitAsync(Deadline);
    }

    private static void AssertRejectedReplacement(
        Rig rig, SmokeOwnershipVerdict verdict, FakeProcess foreign, int originalPid)
    {
        Assert.False(verdict.Passed);
        Assert.Equal(ServicePhase.Failed, rig.Service.Snapshot.Phase);
        Assert.Equal(EventCode.ForeignListener, rig.Service.Snapshot.Reason);
        Assert.Equal(1, rig.Launcher.Starts);
        Assert.Equal(originalPid, verdict.Stop?.Identity.ProcessId);
        Assert.Equal(new StopOutcome(true, false, true, 0), rig.Service.LastStop);
        Assert.Equal(1, rig.RequireCurrent().ShutdownRequests);
        Assert.Equal(0, foreign.ShutdownRequests);
        Assert.Equal(0, foreign.Kills);
        Assert.False(foreign.HasExited || foreign.Disposed);
    }

    private static async Task ApplyInvalidObservationAsync(Rig rig, InvalidObservation invalid)
    {
        switch (invalid)
        {
            case InvalidObservation.Unhealthy: rig.Health.Healthy = false; break;
            case InvalidObservation.Foreign: rig.Health.Foreign = new(true, true, 9876); break;
            case InvalidObservation.Exited: rig.RequireCurrent().Exit(37); break;
            case InvalidObservation.NotRunning: await rig.StopAsync(); break;
        }
    }

    private static async Task<HealthObservation> BlockingHealthAsync(
        Rig rig, TaskCompletionSource entered, TaskCompletionSource release,
        CancellationToken cancellationToken)
    {
        entered.TrySetResult();
        await release.Task.WaitAsync(cancellationToken);
        return new(true, true, rig.RequireCurrent().Id);
    }
}

internal sealed class ManualClock : IServiceClock
{
    private sealed record PendingDelay(TimeSpan Delay, TaskCompletionSource Completion);
    private readonly Channel<PendingDelay> _pending = Channel.CreateUnbounded<PendingDelay>();
    private long _ticks;
    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _pending.Writer.WriteAsync(new(delay, completion), cancellationToken);
        await completion.Task.WaitAsync(cancellationToken);
    }

    public async Task AdvanceAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = await _pending.Reader.ReadAsync(deadline.Token);
        Interlocked.Add(ref _ticks, pending.Delay.Ticks);
        pending.Completion.TrySetResult();
    }
}
