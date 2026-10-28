using System.Collections.Concurrent;
using System.ComponentModel;
using System.Threading.Channels;
using Hermes.Tray;
using Xunit;

namespace Hermes.Tray.Tests;

public sealed class LifecycleTests
{
    [Fact]
    public async Task Startup_requires_owned_identity_and_quit_is_graceful()
    {
        await using var rig = new Rig();
        rig.Start();
        var pid = await rig.Service.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(rig.Launcher.Current?.Id, pid);
        await rig.StopAsync();
        Assert.Equal(ServicePhase.Stopped, rig.Service.Snapshot.Phase);
        Assert.True(rig.Service.LastStop is { Graceful: true, Forced: false, Exited: true });
        Assert.All(rig.Launcher.Processes, process => Assert.True(process.Disposed));
        Assert.Equal(1, rig.Health.MaximumConcurrent);
    }

    [Fact]
    public async Task Foreign_listener_is_not_adopted_launched_or_killed()
    {
        await using var rig = new Rig();
        rig.Health.Foreign = new(true, true, 9876);
        rig.Start();
        var error = await Assert.ThrowsAsync<LifecycleFault>(() => rig.Service.Ready);
        await rig.Worker;
        Assert.Equal(EventCode.ForeignListener, error.Code);
        Assert.Equal(0, rig.Launcher.Starts);
        Assert.Empty(rig.Launcher.Processes);
    }

    [Fact]
    public async Task Foreign_identity_after_launch_stops_only_the_owned_child_without_retry()
    {
        await using var rig = new Rig();
        rig.Health.Override = token => Task.FromResult(
            rig.Launcher.Current is null ? new HealthObservation(false, false, null) : new(true, true, 9876));
        rig.Start();
        var error = await Assert.ThrowsAsync<LifecycleFault>(() => rig.Service.Ready);
        await rig.Worker;
        Assert.Equal(EventCode.IdentityMismatch, error.Code);
        Assert.Equal(1, rig.Launcher.Starts);
        Assert.All(rig.Launcher.Processes, process => Assert.Equal(1, process.ShutdownRequests));
    }

    [Fact]
    public async Task Launch_failures_are_logged_and_retry_budget_is_finite()
    {
        await using var rig = new Rig();
        rig.Launcher.FailLaunch = true;
        rig.Start();
        await Assert.ThrowsAsync<LifecycleFault>(() => rig.Service.Ready);
        await rig.Worker.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, rig.Launcher.Starts);
        Assert.Equal(EventCode.RetryExhausted, rig.Service.Snapshot.Reason);
        Assert.Equal(4, rig.Log.Entries.Count(entry => entry.Code == EventCode.LaunchFailed));
    }

    [Fact]
    public async Task Startup_deadline_stops_each_failed_child_before_retry()
    {
        await using var rig = new Rig();
        rig.Health.Healthy = false;
        rig.Start();
        await Assert.ThrowsAsync<LifecycleFault>(() => rig.Service.Ready);
        await rig.Worker.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, rig.Launcher.Starts);
        Assert.All(rig.Launcher.Processes, process => Assert.True(process.HasExited && process.Disposed));
        Assert.Contains(rig.Log.Entries, entry => entry.Code == EventCode.StartupTimeout);
    }

    [Fact]
    public async Task Repeated_crashes_restart_three_times_then_visibly_exhaust()
    {
        await using var rig = new Rig();
        rig.Start();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await rig.WaitAsync(ServicePhase.Running);
            Assert.Equal(attempt + 1, rig.Launcher.Starts);
            rig.RequireCurrent().Exit(37);
        }
        await rig.Worker.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(EventCode.RetryExhausted, rig.Service.Snapshot.Reason);
        Assert.Equal(4, rig.Launcher.Starts);
        Assert.Contains(rig.Log.Entries, entry => entry.Code == EventCode.ChildExited && entry.ExitCode == 37);
    }

    [Fact]
    public async Task Sustained_health_loss_stops_old_child_before_replacement()
    {
        await using var rig = new Rig();
        rig.Start();
        await rig.WaitAsync(ServicePhase.Running);
        var original = rig.RequireCurrent();
        rig.Health.Healthy = false;
        await rig.WaitAsync(ServicePhase.Backoff);
        Assert.True(original.HasExited && original.Disposed);
        rig.Health.Healthy = true;
        await rig.WaitAsync(ServicePhase.Running);
        Assert.Equal(2, rig.Launcher.Starts);
    }

    [Fact]
    public async Task Shutdown_timeout_falls_back_to_only_the_owned_process()
    {
        await using var rig = new Rig();
        rig.Launcher.ShutdownWorks = false;
        rig.Start();
        await rig.Service.Ready;
        await rig.StopAsync();
        var process = rig.RequireCurrent();
        Assert.Equal(1, process.ShutdownRequests);
        Assert.Equal(1, process.Kills);
        Assert.True(rig.Service.LastStop is { Graceful: false, Forced: true, Exited: true });
    }

    [Fact]
    public async Task Cancellation_waits_for_inflight_health_and_then_stops_child()
    {
        await using var rig = new Rig();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Health.Override = async token =>
        {
            if (rig.Launcher.Current is null)
                return new(false, false, null);
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The cancelled health operation returned.");
        };
        rig.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await rig.StopAsync();
        Assert.Equal(ServicePhase.Stopped, rig.Service.Snapshot.Phase);
        Assert.True(rig.RequireCurrent().Disposed);
        Assert.Equal(1, rig.Health.MaximumConcurrent);
        Assert.Equal(0, rig.Health.Active);
    }

    [Fact]
    public async Task A_second_worker_is_rejected()
    {
        await using var rig = new Rig();
        rig.Start();
        await rig.Service.Ready;
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.RunAsync(CancellationToken.None));
        Assert.Equal(1, rig.Launcher.Starts);
        Assert.Equal(1, rig.Health.MaximumConcurrent);
    }
}

internal sealed class Rig : IAsyncDisposable, IServiceObserver
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<ServiceSnapshot> _states = Channel.CreateUnbounded<ServiceSnapshot>();
    private IServiceObserver? _additionalObserver;
    public FakeLauncher Launcher { get; } = new();
    public MemoryLog Log { get; } = new();
    public FakeHealth Health { get; }
    public OwnedService Service { get; }
    public Task Worker { get; private set; } = Task.CompletedTask;

    public Rig(IServiceClock? clock = null)
    {
        Health = new(Launcher);
        var options = new TrayOptions(Path.Combine(Path.GetTempPath(), "hermes-fake"), 31991, @"C:\fake\Hermes.Service.exe");
        var policy = new LifecyclePolicy(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(250), 3,
            [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(30)]);
        Service = new(options, Launcher, Health, clock ?? new FakeClock(), Log, this, policy);
    }

    public void Start() => Worker = Service.RunAsync(_stop.Token);
    public void ObserveWith(IServiceObserver observer) => _additionalObserver = observer;
    public void Changed(ServiceSnapshot snapshot)
    {
        _additionalObserver?.Changed(snapshot);
        _states.Writer.TryWrite(snapshot);
    }
    public FakeProcess RequireCurrent() => Launcher.Current ?? throw new InvalidOperationException("No fake child.");

    public async Task WaitAsync(ServicePhase phase)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await _states.Reader.ReadAsync(deadline.Token)).Phase != phase) { }
    }

    public async Task StopAsync()
    {
        _stop.Cancel();
        await Worker.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Service.Dispose();
        _stop.Dispose();
    }
}

internal sealed class MemoryLog : IServiceLog
{
    public ConcurrentQueue<LogEntry> Entries { get; } = new();
    public void Write(LogEntry entry) => Entries.Enqueue(entry);
}

internal sealed class FakeClock : IServiceClock
{
    private long _ticks;
    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Interlocked.Add(ref _ticks, delay.Ticks);
        return Task.Delay(1, cancellationToken);
    }
}

internal sealed class FakeLauncher : IProcessLauncher
{
    public List<FakeProcess> Processes { get; } = [];
    public FakeProcess? Current => Processes.LastOrDefault();
    public int Starts { get; private set; }
    public bool FailLaunch { get; set; }
    public bool ShutdownWorks { get; set; } = true;

    public IOwnedProcess Start(TrayOptions options)
    {
        Starts++;
        if (FailLaunch)
            throw new Win32Exception(2);
        if (Current is { HasExited: false })
            throw new InvalidOperationException("Overlapping owned children.");
        var child = new FakeProcess(1000 + Starts, ShutdownWorks);
        Processes.Add(child);
        return child;
    }
}

internal sealed class FakeProcess(int id, bool shutdownWorks)
    : IOwnedProcess, Hermes.Tray.Access.IAccessProcessHandle
{
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Id { get; } = id;
    public Hermes.Tray.Access.OwnerProcessIdentity Identity { get; } =
        new(id, AccessFixture.Now.AddMinutes(-1));
    public bool IsAlive => !HasExited && !Disposed;
    public bool HasExited => _exit.Task.IsCompleted;
    public int? ExitCode { get; private set; }
    public int ShutdownRequests { get; private set; }
    public int Kills { get; private set; }
    public bool Disposed { get; private set; }

    public void Exit(int code)
    {
        ExitCode = code;
        _exit.TrySetResult();
    }

    public Task RequestShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ShutdownRequests++;
        if (shutdownWorks)
            Exit(0);
        return Task.CompletedTask;
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);
    public Task DrainOutputAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public void KillTree() { Kills++; Exit(-1); }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

internal sealed class FakeHealth(FakeLauncher launcher) : IHealthProbe
{
    private int _active;
    private int _maximumConcurrent;
    public bool Healthy { get; set; } = true;
    public HealthObservation? Foreign { get; set; }
    public Func<CancellationToken, Task<HealthObservation>>? Override { get; set; }
    public int Active => Volatile.Read(ref _active);
    public int MaximumConcurrent => Volatile.Read(ref _maximumConcurrent);

    public async Task<HealthObservation> ProbeAsync(Uri origin, CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref _active);
        Interlocked.Exchange(ref _maximumConcurrent, Math.Max(active, MaximumConcurrent));
        try
        {
            if (Override is { } probe)
                return await probe(cancellationToken);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return Foreign ?? (launcher.Current is { HasExited: false } process
                ? new(true, Healthy, process.Id) : new(false, false, null));
        }
        finally { Interlocked.Decrement(ref _active); }
    }
}
