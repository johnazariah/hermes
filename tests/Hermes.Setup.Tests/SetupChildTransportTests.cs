using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Text;
using Hermes.Servicing;
using Hermes.Servicing.Acceptance;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed partial class SetupChildTransportTests
{
    private const string SuccessSummary = "Hermes Setup: None.\n";
    private static SetupReport SuccessReport { get; } =
        new(SetupVerb.Install, SetupOutcome.Completed, true, 0);
    private static string SuccessJson => SuccessReport.ToJson();
    private static SetupChildBudget Budget { get; } =
        new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), TimeProvider.System);

    [Fact]
    public async Task Both_pipes_are_drained_concurrently()
    {
        var stderrStarted = Source();
        using var child = new Child(
            Bytes(SuccessJson + "\n", (_, token) => stderrStarted.Task.WaitAsync(token)),
            Bytes(SuccessSummary, (_, _) =>
            {
                stderrStarted.TrySetResult(true);
                return Task.CompletedTask;
            }));
        Assert.Equal(SuccessReport, (await ConsumeAsync(child)).Report);
        Assert.Equal(0, child.Kills);
        Assert.False(child.StandardOutput.CanRead);
        Assert.False(child.StandardError.CanRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pipe_limits_stop_a_live_child_without_waiting_for_exit(bool stderr)
    {
        var tooLong = new Pipe(new byte[MsiCases.SetupOutputBytes + 1]);
        using var child = new Child(
            stderr ? Bytes(SuccessJson + "\n") : tooLong,
            stderr ? tooLong : Bytes(SuccessSummary), exited: false);
        await FaultAsync(ConsumeAsync(child),
            stderr ? SetupChildFault.StderrLimit : SetupChildFault.StdoutLimit);
        Assert.Equal(1, child.Kills);
        Assert.True(child.HasExited);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_utf8_is_not_replaced_or_accepted(bool stderr)
    {
        var malformed = new Pipe([0xc3, 0x28]);
        using var child = new Child(
            stderr ? Bytes(SuccessJson + "\n") : malformed,
            stderr ? malformed : Bytes(SuccessSummary));
        await FaultAsync(ConsumeAsync(child),
            stderr ? SetupChildFault.InvalidDiagnostics : SetupChildFault.InvalidReport);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unterminated")]
    public async Task Missing_or_unterminated_stdout_is_explicit_eof(string text)
    {
        using var child = new Child(Bytes(text), Bytes(SuccessSummary));
        await FaultAsync(ConsumeAsync(child), SetupChildFault.PrematureEof);
        Assert.Equal(0, child.Kills);
    }

    [Fact]
    public async Task Missing_stderr_is_not_a_success_fallback()
    {
        using var child = new Child(Bytes(SuccessJson + "\n"), Bytes(""));
        await FaultAsync(ConsumeAsync(child), SetupChildFault.PrematureEof);
    }

    [Fact]
    public async Task Unexpected_os_exit_is_distinct_from_a_report_mismatch()
    {
        using var child = new Child(
            Bytes(SuccessJson + "\n"), Bytes(SuccessSummary), exitCode: 42);
        await FaultAsync(ConsumeAsync(child), SetupChildFault.UnexpectedExit);
    }

    [Theory]
    [InlineData(1603)]
    [InlineData(1618)]
    [InlineData(3010)]
    public async Task Actual_exit_must_agree_with_completed_report(int exit)
    {
        using var child = new Child(
            Bytes(SuccessJson + "\n"), Bytes(SuccessSummary), exitCode: exit);
        await FaultAsync(ConsumeAsync(child), SetupChildFault.ExitMismatch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expected_pipe_or_wait_failures_keep_an_os_failure_state(bool wait)
    {
        var error = new Win32Exception(5);
        using var child = new Child(
            wait ? Bytes(SuccessJson + "\n") : new Pipe([], failure: new IOException()),
            Bytes(SuccessSummary), exited: false, waitFailure: wait ? error : null);
        await FaultAsync(ConsumeAsync(child), SetupChildFault.OsFailure);
        Assert.Equal(1, child.Kills);
    }

    [Fact]
    public async Task Programming_fault_is_rethrown_after_checked_cleanup()
    {
        var expected = new InvalidOperationException("synthetic programming fault");
        using var child = new Child(
            new Pipe([], failure: expected), Bytes(SuccessSummary), exited: false);
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConsumeAsync(child)));
        Assert.Equal(1, child.Kills);
        Assert.True(child.HasExited);
    }

    [Fact]
    public async Task Caller_cancellation_does_not_cancel_owned_cleanup()
    {
        using var caller = new CancellationTokenSource();
        using var output = new CleanupPipe();
        using var errors = new CleanupPipe();
        using var child = new Child(output, errors, exited: false);
        var result = ConsumeAsync(child, cancellationToken: caller.Token);
        await Task.WhenAll(output.Started.Task, errors.Started.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await FaultAsync(result, SetupChildFault.Cancelled);
        AssertClosedAndSettled(output);
        AssertClosedAndSettled(errors);
        Assert.Equal(1, child.Kills);
        Assert.True(child.HasExited);
    }

    [Theory]
    [InlineData(KillMode.Complete)]
    [InlineData(KillMode.ExitRace)]
    public async Task Deadline_is_finite_and_a_natural_exit_race_is_checked(KillMode mode)
    {
        var clock = new ManualClock();
        using var child = new Child(
            Bytes(SuccessJson + "\n"), Bytes(SuccessSummary), exited: false, mode: mode);
        var result = ConsumeAsync(child, budget: Budget with { Clock = clock });
        await child.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Fire();
        await FaultAsync(result, SetupChildFault.Timeout);
        Assert.Equal(1, child.Kills);
        Assert.True(child.HasExited);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_exited_child_with_an_open_pipe_does_not_complete_early(bool stderr)
    {
        var clock = new ManualClock();
        using var pending = new CleanupPipe();
        using var child = new Child(
            stderr ? Bytes(SuccessJson + "\n") : pending,
            stderr ? pending : Bytes(SuccessSummary));
        var result = ConsumeAsync(child, budget: Budget with { Clock = clock });
        await pending.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.IsCompleted);
        clock.Fire();
        await FaultAsync(result, SetupChildFault.Timeout);
        AssertClosedAndSettled(pending);
        Assert.Equal(0, child.Kills);
    }

    [Fact]
    public async Task Late_trailing_stdout_is_consumed_and_rejected()
    {
        var atTail = Source();
        var release = Source();
        using var child = new Child(Bytes(SuccessJson + "\ntrailing\n", (position, _) =>
            Hold(position >= SuccessJson.Length + 1, atTail, release)), Bytes(SuccessSummary));
        var result = ConsumeAsync(child);
        await atTail.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.IsCompleted);
        release.TrySetResult(true);
        await FaultAsync(result, SetupChildFault.InvalidReport);
    }

    [Theory]
    [InlineData(false, KillMode.Complete, 0)]
    [InlineData(true, KillMode.Fail, 1)]
    public async Task Unowned_or_unterminable_child_is_a_checked_cleanup_failure(
        bool owned, KillMode mode, int kills)
    {
        var clock = new ManualClock();
        using var child = new Child(Bytes(SuccessJson + "\n"), Bytes(SuccessSummary),
            exited: false, owned: owned, mode: mode);
        var result = ConsumeAsync(child, budget: Budget with { Clock = clock });
        await child.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Fire();
        var error = await FaultAsync(result, SetupChildFault.CleanupFailed);
        Assert.IsType<AggregateException>(error.InnerException);
        Assert.Equal(kills, child.Kills);
    }

    [Fact]
    public async Task Cleanup_wait_has_its_own_independent_finite_deadline()
    {
        var clock = new ManualClock();
        using var child = new Child(Bytes(SuccessJson + "\n"), Bytes(SuccessSummary),
            exited: false, mode: KillMode.Stall);
        var result = ConsumeAsync(child, budget: Budget with { Clock = clock });
        await child.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Fire();
        await clock.CleanupArmed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Fire();
        var error = await FaultAsync(result, SetupChildFault.CleanupFailed);
        var failures = Assert.IsType<AggregateException>(error.InnerException);
        Assert.Equal(SetupChildFault.Timeout,
            Assert.IsType<SetupChildException>(failures.InnerExceptions[0]).Fault);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pipe_failure_closes_and_settles_the_other_pending_read(bool stderr)
    {
        using var pending = new CleanupPipe();
        using var failed = new Pipe([], failure: new IOException());
        using var child = new Child(
            stderr ? pending : failed, stderr ? failed : pending, exited: false);
        await FaultAsync(ConsumeAsync(child), SetupChildFault.OsFailure);
        AssertClosedAndSettled(pending);
        Assert.Equal(1, child.Kills);
        Assert.True(child.HasExited);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ignoring_cancellation_and_close_cannot_claim_cleanup_success(bool stderr)
    {
        using var output = new CleanupPipe(stderr ? PipeCloseMode.Release : PipeCloseMode.Ignore);
        using var errors = new CleanupPipe(stderr ? PipeCloseMode.Ignore : PipeCloseMode.Release);
        using var child = new Child(output, errors);
        try
        {
            InitialTimeout(await CleanupFailureAsync(child, output, errors, true));
            var pending = stderr ? errors : output;
            Assert.True(pending.Closed);
            Assert.False(pending.PendingRead.IsCompleted);
            Assert.Equal(0, child.Kills);
        }
        finally { await ReleasePipesAsync(output, errors); }
    }

    [Theory]
    [InlineData(false, PipeCloseMode.Fail)]
    [InlineData(true, PipeCloseMode.Fail)]
    [InlineData(false, PipeCloseMode.Stall)]
    [InlineData(true, PipeCloseMode.Stall)]
    public async Task Pipe_close_faults_and_stalls_are_checked_within_cleanup_budget(
        bool stderr, PipeCloseMode mode)
    {
        var expected = new IOException("synthetic close failure");
        using var output = new CleanupPipe(stderr ? PipeCloseMode.Release : mode, expected);
        using var errors = new CleanupPipe(stderr ? mode : PipeCloseMode.Release, expected);
        using var child = new Child(output, errors);
        try
        {
            var failure = await CleanupFailureAsync(
                child, output, errors, mode == PipeCloseMode.Stall);
            var causes = InitialTimeout(failure);
            if (mode == PipeCloseMode.Fail)
                Assert.Contains(expected, causes.Flatten().InnerExceptions);
            Assert.Equal(0, child.Kills);
        }
        finally { await ReleasePipesAsync(output, errors); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_programming_fault_from_a_cancelled_drain_is_not_discarded(bool stderr)
    {
        var expected = new InvalidOperationException("synthetic late read fault");
        using var output = new CleanupPipe(readFailure: stderr ? null : expected);
        using var errors = new CleanupPipe(readFailure: stderr ? expected : null);
        using var child = new Child(output, errors);
        var causes = InitialTimeout(await CleanupFailureAsync(child, output, errors, false));
        Assert.Contains(expected, causes.Flatten().InnerExceptions);
        AssertClosedAndSettled(output);
        AssertClosedAndSettled(errors);
    }

    private static async Task<SetupChildException> CleanupFailureAsync(
        Child child, CleanupPipe output, CleanupPipe errors, bool expireCleanup)
    {
        var clock = new ManualClock();
        var result = ConsumeAsync(child, budget: Budget with { Clock = clock });
        await Task.WhenAll(output.Started.Task, errors.Started.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        clock.Fire();
        await Task.WhenAll(output.CloseStarted.Task, errors.CloseStarted.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        if (expireCleanup)
            clock.Fire();
        return await FaultAsync(result, SetupChildFault.CleanupFailed);
    }

    private static AggregateException InitialTimeout(SetupChildException error)
    {
        var causes = Assert.IsType<AggregateException>(error.InnerException);
        Assert.Equal(SetupChildFault.Timeout,
            Assert.IsType<SetupChildException>(causes.InnerExceptions[0]).Fault);
        return causes;
    }

    private static void AssertClosedAndSettled(CleanupPipe pipe)
    {
        Assert.True(pipe.Closed);
        Assert.True(pipe.CloseFinished.Task.IsCompletedSuccessfully);
        Assert.True(pipe.PendingRead.IsCompleted);
    }

    private static Task ReleasePipesAsync(CleanupPipe output, CleanupPipe errors)
    {
        foreach (var pipe in new[] { output, errors })
        {
            pipe.ReleaseClose();
            pipe.ReleaseRead();
        }
        return Task.WhenAll(output.CloseFinished.Task, errors.CloseFinished.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static Task Hold(
        bool hold, TaskCompletionSource<bool> reached, TaskCompletionSource<bool> release)
    {
        if (!hold)
            return Task.CompletedTask;
        reached.TrySetResult(true);
        return release.Task;
    }

    private static Task<SetupChildResult> ConsumeAsync(
        Child child, SetupVerb operation = SetupVerb.Install,
        SetupChildBudget? budget = null, CancellationToken cancellationToken = default) =>
        MsiCases.MainAsync(child, operation, budget ?? Budget, cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5));

    private static async Task<SetupChildException> FaultAsync(
        Task<SetupChildResult> task, SetupChildFault fault)
    {
        var error = await Assert.ThrowsAsync<SetupChildException>(() => task);
        Assert.Equal(fault, error.Fault);
        Assert.Equal(unchecked((int)0x8004A100) | (int)fault, error.HResult);
        Assert.Equal("Setup child transport failed.", error.Message);
        return error;
    }

    private static TaskCompletionSource<bool> Source() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Pipe Bytes(
        string text, Func<long, CancellationToken, Task>? before = null) =>
        new(Encoding.UTF8.GetBytes(text), before);

    public enum KillMode { Complete, Fail, Stall, ExitRace }
    public enum PipeCloseMode { Release, Ignore, Fail, Stall }

    private sealed class Pipe(
        byte[] bytes, Func<long, CancellationToken, Task>? before = null,
        Exception? failure = null) : MemoryStream(bytes, writable: false)
    {
        internal TaskCompletionSource<bool> Started { get; } = Source();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            if (failure is { } error)
                ExceptionDispatchInfo.Capture(error).Throw();
            if (before is { } step)
                await step(Position, cancellationToken);
            return await base.ReadAsync(
                buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
        }
    }

    private sealed class CleanupPipe(
        PipeCloseMode mode = PipeCloseMode.Release,
        Exception? closeFailure = null, Exception? readFailure = null) : MemoryStream()
    {
        private readonly TaskCompletionSource<int> _read =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _closeRelease = Source();
        private int _closing;
        private int _closed;
        internal TaskCompletionSource<bool> Started { get; } = Source();
        internal TaskCompletionSource<bool> CloseStarted { get; } = Source();
        internal TaskCompletionSource<bool> CloseFinished { get; } = Source();
        internal Task<int> PendingRead => _read.Task;
        internal bool Closed => Volatile.Read(ref _closed) != 0;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            return new(_read.Task);
        }

        internal void ReleaseRead(Exception? error = null)
        {
            if (error is { } failure)
                _read.TrySetException(failure);
            else
                _read.TrySetResult(0);
        }

        internal void ReleaseClose() => _closeRelease.TrySetResult(true);

        protected override void Dispose(bool disposing)
        {
            if (!disposing || Interlocked.Exchange(ref _closing, 1) != 0)
                return;
            CloseStarted.TrySetResult(true);
            try
            {
                if (mode != PipeCloseMode.Ignore)
                    ReleaseRead(readFailure ?? new ObjectDisposedException(nameof(CleanupPipe)));
                if (mode == PipeCloseMode.Stall)
                    _closeRelease.Task.GetAwaiter().GetResult();
                if (mode == PipeCloseMode.Fail)
                    throw closeFailure ?? new IOException("synthetic close failure");
                base.Dispose(disposing);
                Interlocked.Exchange(ref _closed, 1);
            }
            finally { CloseFinished.TrySetResult(true); }
        }
    }

    private sealed class Child : ISetupChild, IDisposable
    {
        private readonly TaskCompletionSource<int> _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly KillMode _mode;
        private readonly Exception? _waitFailure;
        private int _waits;
        private int _kills;
        public Stream StandardOutput { get; }
        public Stream StandardError { get; }
        public bool IsOwned { get; }
        public bool HasExited => _exit.Task.IsCompletedSuccessfully;
        internal int Kills => _kills;
        internal TaskCompletionSource<bool> WaitStarted { get; } = Source();

        internal Child(Stream output, Stream errors, bool exited = true, int exitCode = 0,
            bool owned = true, KillMode mode = KillMode.Complete, Exception? waitFailure = null)
        {
            StandardOutput = output;
            StandardError = errors;
            IsOwned = owned;
            _mode = mode;
            _waitFailure = waitFailure;
            if (exited)
                _exit.SetResult(exitCode);
        }

        public void CloseStandardOutput() => StandardOutput.Dispose();
        public void CloseStandardError() => StandardError.Dispose();

        public Task<int> WaitAsync(CancellationToken cancellationToken)
        {
            _waits++;
            WaitStarted.TrySetResult(true);
            return _waits == 1 && _waitFailure is { } error
                ? Task.FromException<int>(error)
                : _exit.Task.WaitAsync(cancellationToken);
        }

        public void Kill()
        {
            _kills++;
            switch (_mode)
            {
                case KillMode.Fail:
                    throw new Win32Exception(5);
                case KillMode.Stall:
                    return;
                case KillMode.ExitRace:
                    _exit.TrySetResult(-1);
                    throw new InvalidOperationException();
                default:
                    _exit.TrySetResult(-1);
                    return;
            }
        }

        public void Dispose()
        {
            StandardOutput.Dispose();
            StandardError.Dispose();
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        internal TaskCompletionSource<bool> CleanupArmed { get; } = Source();

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(() => callback(state));
            lock (_timers)
            {
                _timers.Add(timer);
                if (_timers.Count == 2)
                    CleanupArmed.TrySetResult(true);
            }
            return timer;
        }

        internal void Fire()
        {
            ManualTimer[] timers;
            lock (_timers)
                timers = _timers.ToArray();
            foreach (var timer in timers)
                timer.Fire();
        }

        private sealed class ManualTimer(Action callback) : ITimer
        {
            private int _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) =>
                Volatile.Read(ref _disposed) == 0;
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            internal void Fire()
            {
                if (Volatile.Read(ref _disposed) == 0)
                    callback();
            }
        }
    }
}
