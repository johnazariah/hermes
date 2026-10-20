using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Hermes.Tray;

internal enum ServicePhase { Starting, Running, Backoff, Failed, Stopping, Stopped }
internal enum OutputStream { StandardOutput, StandardError }
internal enum EventCode
{
    Configuration, Starting, Healthy, Retrying, Cancelled, ForeignListener,
    IdentityMismatch, StartupTimeout, HealthLost, ChildExited, LaunchFailed,
    RetryExhausted, ShutdownRequested, ShutdownTimedOut, StopFailed, Stopped,
    OutputDrained, PipeFailed, UnexpectedFailure, WindowFailed, BrowserFailed, HealthProbe, AccessFailed
}

internal sealed record ServiceSnapshot(
    ServicePhase Phase, EventCode Reason, int? ProcessId = null, int Restart = 0);
internal sealed record HealthObservation(bool Occupied, bool Healthy, int? ProcessId);
internal sealed record StopOutcome(bool Graceful, bool Forced, bool Exited, int? ExitCode);
internal sealed record LogEntry(
    EventCode Code, int? ProcessId = null, int? ExitCode = null,
    OutputStream? Stream = null, long Characters = 0, string? Detail = null);
internal sealed record AttemptFailure(EventCode Code, bool Retry);
internal sealed record LifecyclePolicy(
    TimeSpan StartupTimeout, TimeSpan PollInterval, TimeSpan StopTimeout,
    TimeSpan KillTimeout, int HealthFailureLimit, ImmutableArray<TimeSpan> RetryDelays)
{
    public static LifecyclePolicy Default { get; } = new(
        TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(3), 3,
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)]);
}

internal interface IServiceLog { void Write(LogEntry entry); }
internal interface IServiceObserver { void Changed(ServiceSnapshot snapshot); }
internal interface IServiceClock
{
    TimeSpan Elapsed { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}
internal interface IHealthProbe
{
    Task<HealthObservation> ProbeAsync(Uri origin, CancellationToken cancellationToken);
}
internal interface IProcessLauncher { IOwnedProcess Start(TrayOptions options); }
internal interface IOwnedProcess : IAsyncDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
    Task RequestShutdownAsync(CancellationToken cancellationToken);
    Task WaitForExitAsync(CancellationToken cancellationToken);
    Task DrainOutputAsync(CancellationToken cancellationToken);
    void KillTree();
}

internal sealed class ServiceClock : IServiceClock
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    public TimeSpan Elapsed => _watch.Elapsed;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

internal static class SafeFailure
{
    // Exception messages and child output can contain credentials or document content.
    public static string Describe(Exception error) => $"{error.GetType().FullName}:0x{error.HResult:X8}";
}

internal sealed class LifecycleFault(EventCode code, bool retry) : Exception(code.ToString())
{
    public EventCode Code { get; } = code;
    public bool Retry { get; } = retry;
}

internal sealed class OwnedService(
    TrayOptions options, IProcessLauncher launcher, IHealthProbe health,
    IServiceClock clock, IServiceLog log, IServiceObserver observer, LifecyclePolicy policy)
    : IOwnershipVerifier, IOwnedAccessSource, IDisposable
{
    private readonly TaskCompletionSource<int> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<SupervisionResult> _final =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _healthGate = new(1, 1);
    private ServiceSnapshot _snapshot = new(ServicePhase.Stopped, EventCode.Stopped);
    private ImmutableArray<OwnedStop> _stops = [];
    private OwnedIdentity? _lastOwned;
    private IOwnedProcess? _owned;
    private OwnedAccessState? _accessState;
    private int _started;
    private int _restart;

    public Task<int> Ready => _ready.Task;
    public Task<SupervisionResult> Final => _final.Task;
    public ServiceSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public StopOutcome? LastStop => _stops.IsEmpty ? null : _stops[^1].Outcome;

    public OwnedAccessState? ReadAccess()
    {
        var current = Volatile.Read(ref _accessState);
        return (Snapshot, current) switch
        {
            ({ Phase: ServicePhase.Running, ProcessId: { } pid } snapshot, { } owned)
                when owned.Process.IsAlive && owned.Process.Identity.ProcessId == pid &&
                    owned.ReadyHealthProcessId == pid &&
                    owned.RestartGeneration == snapshot.Restart => owned,
            _ => null
        };
    }

    private void PublishAccess(IOwnedProcess process, HealthObservation observation) =>
        Volatile.Write(ref _accessState,
            process is Hermes.Tray.Access.IAccessProcessHandle handle &&
            observation is { Healthy: true, ProcessId: { } pid } && pid == process.Id
                ? new(handle, _restart, pid) : null);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("The owned-service worker may only be started once.");
        try { await RunGuardedAsync(cancellationToken); }
        finally
        {
            var snapshot = Snapshot;
            var disposition = snapshot.Phase == ServicePhase.Stopped
                ? SupervisionDisposition.Cancelled : SupervisionDisposition.Failed;
            _final.TrySetResult(new(disposition, snapshot, _lastOwned, _stops));
        }
    }

    private async Task RunGuardedAsync(CancellationToken cancellationToken)
    {
        try { await SuperviseAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ready.TrySetCanceled(cancellationToken);
            Change(ServicePhase.Stopped, EventCode.Stopped);
        }
        catch (Exception error)
        {
            log.Write(new(EventCode.UnexpectedFailure, Detail: SafeFailure.Describe(error)));
            Fail(error is LifecycleFault fault ? fault.Code : EventCode.UnexpectedFailure);
        }
    }

    private async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        for (_restart = 0; _restart <= policy.RetryDelays.Length; _restart++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Change(ServicePhase.Starting, EventCode.Starting);
            var failure = await AttemptAsync(cancellationToken);
            if (!failure.Retry || _restart == policy.RetryDelays.Length)
            {
                Fail(failure.Retry ? EventCode.RetryExhausted : failure.Code);
                return;
            }
            Change(ServicePhase.Backoff, EventCode.Retrying);
            await clock.DelayAsync(policy.RetryDelays[_restart], cancellationToken);
        }
    }

    private async Task<AttemptFailure> AttemptAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAttemptAsync(cancellationToken);
            throw new LifecycleFault(EventCode.UnexpectedFailure, false);
        }
        catch (LifecycleFault fault)
        {
            log.Write(new(fault.Code, _owned?.Id, _owned?.ExitCode));
            return new(fault.Code, fault.Retry);
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
        {
            log.Write(new(EventCode.LaunchFailed, Detail: SafeFailure.Describe(error)));
            return new(EventCode.LaunchFailed, true);
        }
        finally { await FinishAttemptAsync(cancellationToken); }
    }

    private async Task ExecuteAttemptAsync(CancellationToken cancellationToken)
    {
        if ((await ProbeSerializedAsync(cancellationToken)).Occupied)
            throw new LifecycleFault(EventCode.ForeignListener, false);
        cancellationToken.ThrowIfCancellationRequested();
        var process = launcher.Start(options);
        _owned = process;
        _lastOwned = new(process.Id, _restart);
        await AwaitHealthyAsync(process, cancellationToken);
        Change(ServicePhase.Running, EventCode.Healthy);
        _ready.TrySetResult(process.Id);
        await MonitorAsync(process, cancellationToken);
    }

    private async Task AwaitHealthyAsync(IOwnedProcess process, CancellationToken cancellationToken)
    {
        var deadline = clock.Elapsed + policy.StartupTimeout;
        while (clock.Elapsed < deadline)
        {
            EnsureAlive(process);
            var observation = await ProbeSerializedAsync(cancellationToken);
            EnsureIdentity(process, observation);
            EnsureAlive(process);
            if (observation.Healthy && observation.ProcessId == process.Id)
            {
                PublishAccess(process, observation);
                return;
            }
            await clock.DelayAsync(policy.PollInterval, cancellationToken);
        }
        throw new LifecycleFault(EventCode.StartupTimeout, true);
    }

    private async Task MonitorAsync(IOwnedProcess process, CancellationToken cancellationToken)
    {
        var failures = 0;
        while (true)
        {
            await clock.DelayAsync(policy.PollInterval, cancellationToken);
            EnsureAlive(process);
            var observation = await ProbeSerializedAsync(cancellationToken);
            EnsureIdentity(process, observation);
            EnsureAlive(process);
            PublishAccess(process, observation);
            failures = observation.Healthy && observation.ProcessId == process.Id ? 0 : failures + 1;
            if (failures >= policy.HealthFailureLimit)
                throw new LifecycleFault(EventCode.HealthLost, true);
        }
    }

    private static void EnsureAlive(IOwnedProcess process)
    {
        if (process.HasExited)
            throw new LifecycleFault(EventCode.ChildExited, true);
    }

    private static void EnsureIdentity(IOwnedProcess process, HealthObservation observation)
    {
        if (observation.ProcessId is { } actual && actual != process.Id)
            throw new LifecycleFault(EventCode.IdentityMismatch, false);
    }

    private async Task FinishAttemptAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            Change(ServicePhase.Stopping, EventCode.Cancelled);
        await _healthGate.WaitAsync(CancellationToken.None);
        try { await ReleaseOwnedProcessAsync(); }
        finally { _healthGate.Release(); }
    }

    private async Task ReleaseOwnedProcessAsync()
    {
        Volatile.Write(ref _accessState, null);
        if (_owned is not { } process)
            return;
        var identity = new OwnedIdentity(process.Id, _restart);
        _owned = null;
        try
        {
            var stop = new OwnedStop(identity, await StopAsync(process));
            _stops = _stops.Add(stop);
            log.Write(new(EventCode.Stopped, identity.ProcessId, stop.Outcome.ExitCode));
            if (!stop.Outcome.Exited)
                throw new LifecycleFault(EventCode.StopFailed, false);
        }
        finally { await process.DisposeAsync(); }
    }

    private async Task<HealthObservation> ProbeSerializedAsync(CancellationToken cancellationToken)
    {
        await _healthGate.WaitAsync(cancellationToken);
        try { return await ProbeAndLogAsync(cancellationToken); }
        finally { _healthGate.Release(); }
    }

    private async Task<HealthObservation> ProbeAndLogAsync(CancellationToken cancellationToken)
    {
        try { return await health.ProbeAsync(options.BaseAddress, cancellationToken); }
        finally
        {
            if (health is IHealthDiagnostics { LastDiagnostic: { } diagnostic })
                log.Write(new(EventCode.HealthProbe, Detail: JsonSerializer.Serialize(diagnostic)));
        }
    }

    public async Task<OwnedIdentity> VerifyOwnershipAsync(
        OwnedIdentity expected, CancellationToken cancellationToken)
    {
        await _healthGate.WaitAsync(cancellationToken);
        try
        {
            var process = RequireOwned(expected);
            EnsureAlive(process);
            var observation = await ProbeAndLogAsync(cancellationToken);
            EnsureIdentity(process, observation);
            EnsureAlive(process);
            if (!observation.Healthy || observation.ProcessId != expected.ProcessId)
                throw new LifecycleFault(EventCode.HealthLost, false);
            if (!ReferenceEquals(process, RequireOwned(expected)))
                throw new LifecycleFault(EventCode.IdentityMismatch, false);
            return expected;
        }
        finally { _healthGate.Release(); }
    }

    private IOwnedProcess RequireOwned(OwnedIdentity expected) => (Snapshot, _owned) switch
    {
        ({ Phase: ServicePhase.Running } snapshot, { } process)
            when snapshot.ProcessId == expected.ProcessId && snapshot.Restart == expected.Restart
                && process.Id == expected.ProcessId => process,
        _ => throw new LifecycleFault(EventCode.IdentityMismatch, false)
    };

    // Call only after the worker and all ownership checks have been joined.
    public void Dispose() => _healthGate.Dispose();

    private async Task<StopOutcome> StopAsync(IOwnedProcess process)
    {
        var requested = !process.HasExited;
        using var timeout = new CancellationTokenSource(policy.StopTimeout);
        try
        {
            if (requested)
            {
                log.Write(new(EventCode.ShutdownRequested, process.Id));
                await process.RequestShutdownAsync(timeout.Token);
            }
            await process.WaitForExitAsync(timeout.Token);
            await process.DrainOutputAsync(timeout.Token);
            return new(requested, false, true, process.ExitCode);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException)
        {
            log.Write(new(EventCode.ShutdownTimedOut, process.Id, Detail: SafeFailure.Describe(error)));
        }
        return await ForceStopAsync(process);
    }

    private async Task<StopOutcome> ForceStopAsync(IOwnedProcess process)
    {
        using var timeout = new CancellationTokenSource(policy.KillTimeout);
        try
        {
            process.KillTree();
            await process.WaitForExitAsync(timeout.Token);
            await process.DrainOutputAsync(timeout.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or Win32Exception or InvalidOperationException or IOException)
        {
            log.Write(new(EventCode.StopFailed, process.Id, Detail: SafeFailure.Describe(error)));
        }
        return new(false, true, process.HasExited, process.ExitCode);
    }

    private void Fail(EventCode code)
    {
        Change(ServicePhase.Failed, code);
        _ready.TrySetException(new LifecycleFault(code, false));
    }

    private void Change(ServicePhase phase, EventCode reason)
    {
        var snapshot = new ServiceSnapshot(phase, reason, _owned?.Id, _restart);
        Volatile.Write(ref _snapshot, snapshot);
        log.Write(new(reason, snapshot.ProcessId));
        observer.Changed(snapshot);
    }
}
