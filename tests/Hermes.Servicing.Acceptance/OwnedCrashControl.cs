using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Hermes.Servicing.Acceptance;

// These capabilities borrow only the child created by the fixed native launch.
// Start must publish ownership before binding, identity or pipe observations.
internal sealed record CrashOwnedChild(
    CrashPipe Output,
    CrashPipe Error,
    Func<CancellationToken, Task> WriteArm,
    Action CloseInput,
    Action CloseOutput,
    Action CloseError,
    Func<CrashIdentity> ReadIdentity,
    Func<CancellationToken, Task<uint>> Wait,
    Action Stop,
    Action Dispose);

// Cooperative operation and reap budgets, not synchronous-call interruption.
// Native execution requires an outer fixed-helper process deadline and disposable
// job containment. After cancellation, actual operations are joined before their
// tokens/handles are disposed, even if a noncooperative operation overruns a budget.
internal sealed record CrashLimits(TimeSpan Operation, TimeSpan Cleanup)
{
    internal static CrashLimits Default { get; } =
        new(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(5));
}

// Carries cleanup facts without replacing the initiating failure with success.
// An unadopted start failure also receives Ownership: absence of a returned child
// is not proof that no process was created or that an internal reap succeeded.
internal sealed class CrashStartException(CrashCleanup cleanupError, Exception cause)
    : IOException("Crash-control startup cleanup failed.", cause)
{
    internal CrashCleanup CleanupError { get; } = cleanupError;
}

// The scope and underlying handles remain valid until the actual callback task
// and all child operations have settled, including after callback cancellation.
internal sealed class CrashObservation(
    CrashIdentity identity, uint exit, Func<CrashIdentity> read) : IDisposable
{
    private bool _active = true;

    internal CrashIdentity Identity => _active
        ? identity : throw new ObjectDisposedException(nameof(CrashObservation));

    internal CrashIdentity Recheck()
    {
        if (!_active)
            throw new ObjectDisposedException(nameof(CrashObservation));
        var current = read();
        current.RequireExit(identity, exit);
        return current;
    }

    public void Dispose() => _active = false;
}

internal static class OwnedCrashControl
{
    internal static async Task<CrashResult> RunAsync(
        Action<Action<CrashOwnedChild>, CancellationToken> start,
        Func<CrashObservation, CancellationToken, Task> observe,
        CrashLimits limits, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(limits.Operation);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, cancellationToken);
        using var waitStop = new CancellationTokenSource();
        var session = CreateSession(limits, deadline, run, waitStop);
        try { await session.ObserveAsync(start, observe); }
        catch (Exception error)
        {
            session.Fail(error, deadline.IsCancellationRequested,
                cancellationToken.IsCancellationRequested);
        }
        finally { await session.CleanupAsync(limits.Cleanup); }
        return session.Finish(deadline.IsCancellationRequested,
            cancellationToken.IsCancellationRequested);
    }

    private static Session CreateSession(CrashLimits limits,
        CancellationTokenSource deadline, CancellationTokenSource run,
        CancellationTokenSource waitStop)
    {
        var clock = Stopwatch.StartNew();
        return new(run, waitStop, Checkpoint);

        void Checkpoint()
        {
            if (clock.Elapsed >= limits.Operation)
                deadline.Cancel();
            run.Token.ThrowIfCancellationRequested();
        }
    }

    private sealed class Session(
        CancellationTokenSource run, CancellationTokenSource waitStop,
        Action checkpoint)
    {
        private readonly TaskCompletionSource<bool> _ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CrashOwnedChild? _child;
        private CrashIdentity? _identity;
        private CrashObservation? _scope;
        private Task<CrashFault>? _output;
        private Task<CrashFault>? _error;
        private Task<uint>? _wait;
        private Task? _write;
        private Task? _callback;
        private Exception? _unexpected;
        private CrashResult _result = new();
        private CrashFault _stage = CrashFault.Start;
        private bool _inputClosed;

        internal async Task ObserveAsync(
            Action<Action<CrashOwnedChild>, CancellationToken> start,
            Func<CrashObservation, CancellationToken, Task> observe)
        {
            var child = Start(start);
            StartPumps(child);
            await RequireReadyAsync();
            await BindAndArmAsync(child);
            await ObserveExitAsync(child, observe);
        }

        private CrashOwnedChild Start(
            Action<Action<CrashOwnedChild>, CancellationToken> start)
        {
            checkpoint();
            // Deliberately inline: no unobserved late Start on a worker task.
            start(Adopt, run.Token);
            checkpoint();
            return _child
                ?? throw new InvalidOperationException("Crash-control child not adopted.");
        }

        private void Adopt(CrashOwnedChild child)
        {
            if (_child is { })
                throw new InvalidOperationException("Crash-control duplicate adoption.");
            _child = child;
            _result = _result with { Started = true };
        }

        private void StartPumps(CrashOwnedChild child)
        {
            _output = WatchAsync(() => CrashWire.ReadOutputAsync(
                child.Output, () => _ready.TrySetResult(true), run.Token), CrashFault.Stdout);
            _error = WatchAsync(() => CrashWire.DrainErrorAsync(
                child.Error, run.Token), CrashFault.Stderr);
            _wait = child.Wait(waitStop.Token);
        }

        private async Task RequireReadyAsync()
        {
            _stage = CrashFault.Ready;
            if (!await _ready.Task.WaitAsync(run.Token))
                throw new InvalidDataException("Crash-control ready proof missing.");
            checkpoint();
            _result = _result with { Ready = true };
        }

        private async Task<CrashFault> WatchAsync(Func<Task> operation, CrashFault fault)
        {
            try { await operation(); return CrashFault.None; }
            catch (Exception error)
            {
                RememberUnexpected(error);
                var observed = run.IsCancellationRequested ? CrashFault.Cancelled : fault;
                _ready.TrySetResult(false);
                run.Cancel();
                return observed;
            }
        }

        private void Bind(CrashOwnedChild child)
        {
            _stage = CrashFault.Binding;
            checkpoint();
            var identity = child.ReadIdentity();
            identity.RequireLive();
            checkpoint();
            var current = child.ReadIdentity();
            current.RequireLive();
            if (!current.SameInstance(identity))
                throw new InvalidDataException("Crash-control pre-arm binding changed.");
            _identity = identity;
            _result = _result with { Identity = true,
                ImageSha256 = identity.ImageSha256, SourceSha256 = identity.SourceSha256 };
            checkpoint();
        }

        private async Task BindAndArmAsync(CrashOwnedChild child)
        {
            Bind(child);
            _stage = CrashFault.Arm;
            _result = _result with { ArmAttempted = true };
            _write = child.WriteArm(run.Token);
            await _write.WaitAsync(run.Token);
            checkpoint();
            CloseInput();
            _result = _result with { Armed = true };
        }

        private async Task<CrashObservation> PrepareObservationAsync(CrashOwnedChild child)
        {
            _stage = CrashFault.Exit;
            if (_wait is not { } wait || _identity is not { } identity ||
                _output is not { } output || _error is not { } error)
                throw new InvalidOperationException("Crash-control lifetime incomplete.");
            var exit = await wait.WaitAsync(run.Token);
            _result = _result with { Exited = true, Exit = exit };
            await Task.WhenAll(output, error).WaitAsync(run.Token);
            checkpoint();
            var scope = new CrashObservation(identity, exit, child.ReadIdentity);
            _scope = scope;
            _ = scope.Recheck();
            checkpoint();
            return scope;
        }

        private async Task ObserveExitAsync(CrashOwnedChild child,
            Func<CrashObservation, CancellationToken, Task> observe)
        {
            var scope = await PrepareObservationAsync(child);
            _stage = CrashFault.Callback;
            _callback = observe(scope, run.Token);
            await _callback.WaitAsync(run.Token);
            _ = scope.Recheck();
            checkpoint();
            _result = _result with { Held = true };
            _stage = CrashFault.Exit;
            if (!CrashControl.IsCrashStatus(_result.Exit))
                throw new InvalidDataException("Crash-control exit was not a supported crash.");
        }

        internal void Fail(Exception error, bool deadline, bool cancelled)
        {
            RememberUnexpected(error);
            if (_stage == CrashFault.Start && _child is null)
                AddCleanup(CrashCleanup.Ownership);
            if (error is CrashStartException startup)
                AddCleanup(startup.CleanupError);
            var fault = error is OperationCanceledException
                ? deadline ? CrashFault.Deadline :
                    cancelled ? CrashFault.Cancelled : _stage
                : _stage;
            _result = _result with { Error = fault };
        }

        private void RememberUnexpected(Exception error)
        {
            if (!CrashControl.Operational(error))
                Interlocked.CompareExchange(ref _unexpected, error, null);
        }

        private void CloseInput()
        {
            if (_inputClosed || _child is not { } child)
                return;
            _inputClosed = true;
            try { child.CloseInput(); }
            catch (Exception error)
            {
                AddCleanup(CrashCleanup.Input);
                RememberUnexpected(error);
                throw;
            }
        }

        internal async Task CleanupAsync(TimeSpan timeout)
        {
            if (_child is not { } child)
            {
                _result = _result with { Cleanup = _result.CleanupError == CrashCleanup.None };
                return;
            }
            using var deadline = new CancellationTokenSource(timeout);
            var clock = Stopwatch.StartNew();
            run.Cancel();
            await CleanupOwnedAsync(child, deadline.Token);
            FinishCleanup(child, deadline.IsCancellationRequested || clock.Elapsed >= timeout);
        }

        private async Task CleanupOwnedAsync(CrashOwnedChild child, CancellationToken deadline)
        {
            CleanupStep(child.Stop, CrashCleanup.Kill);
            await JoinOperationAsync(_write, CrashFault.Arm);
            CleanupStep(CloseInput, CrashCleanup.Input);
            await ReapAsync(child, deadline);
            await JoinDrainsAsync();
            CleanupStep(child.CloseOutput, CrashCleanup.Output);
            CleanupStep(child.CloseError, CrashCleanup.Error);
            await JoinOperationAsync(_callback, CrashFault.Callback);
        }

        private void FinishCleanup(CrashOwnedChild child, bool expired)
        {
            _scope?.Dispose();
            _scope = null;
            CleanupStep(child.Dispose, CrashCleanup.Dispose);
            if (expired)
                AddCleanup(CrashCleanup.Deadline);
            _result = _result with { Cleanup = _result.CleanupError == CrashCleanup.None };
        }

        private async Task JoinOperationAsync(Task? operation, CrashFault fault)
        {
            if (operation is null)
                return;
            try { await operation; }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                RememberUnexpected(error);
                if (_result.Error is CrashFault.None or CrashFault.Cancelled or CrashFault.Deadline)
                    _result = _result with { Error = fault };
            }
        }

        private async Task ReapAsync(CrashOwnedChild child, CancellationToken deadline)
        {
            try
            {
                _wait ??= child.Wait(waitStop.Token);
                var exit = await _wait.WaitAsync(deadline);
                _result = _result with { Exited = true, Exit = exit };
            }
            catch (Exception error)
            {
                RememberUnexpected(error);
                AddCleanup(CrashCleanup.Reap);
            }
            finally { waitStop.Cancel(); }
            await JoinWaitAsync();
        }

        private async Task JoinWaitAsync()
        {
            if (_wait is not { } wait)
                return;
            try
            {
                var exit = await wait;
                _result = _result with { Exited = true, Exit = exit };
            }
            catch (Exception error)
            {
                RememberUnexpected(error);
                AddCleanup(CrashCleanup.Reap);
            }
            _result = _result with { WaitSettled = wait.IsCompleted };
        }

        private async Task JoinDrainsAsync()
        {
            await Task.WhenAll(new[] { _output, _error }.OfType<Task<CrashFault>>());
            _result = _result with
            {
                DrainsSettled = _output?.IsCompleted != false && _error?.IsCompleted != false,
                StdoutComplete = _output is { IsCompletedSuccessfully: true } output &&
                    output.Result == CrashFault.None,
                StderrComplete = _error is { IsCompletedSuccessfully: true } error &&
                    error.Result == CrashFault.None
            };
        }

        private void CleanupStep(Action action, CrashCleanup fault)
        {
            try { action(); }
            catch (Exception error)
            {
                RememberUnexpected(error);
                AddCleanup(fault);
            }
        }

        private void AddCleanup(CrashCleanup fault) =>
            _result = _result with { CleanupError = _result.CleanupError | fault };

        internal CrashResult Finish(bool deadline, bool cancelled)
        {
            if (_unexpected is { } unexpected)
                ExceptionDispatchInfo.Capture(unexpected).Throw();
            var faults = new[] { PumpFault(_output), PumpFault(_error) };
            var transport = faults.FirstOrDefault(
                fault => fault is not (CrashFault.None or CrashFault.Cancelled));
            var error = transport != CrashFault.None ? transport : _result.Error;
            error = error != CrashFault.None ? error :
                deadline ? CrashFault.Deadline :
                cancelled ? CrashFault.Cancelled : CrashFault.None;
            return _result with { Error = error, Deadline = deadline, Cancelled = cancelled,
                WriteSettled = _write?.IsCompleted != false,
                CallbackSettled = _callback?.IsCompleted != false };
        }

        private static CrashFault PumpFault(Task<CrashFault>? task) => task switch
        {
            { IsCompletedSuccessfully: true } completed => completed.Result,
            null => CrashFault.None,
            _ => throw new InvalidOperationException("Crash-control drain was not joined.")
        };
    }
}
