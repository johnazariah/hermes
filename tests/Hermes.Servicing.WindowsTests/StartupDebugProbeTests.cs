using System.ComponentModel;
using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text;
using Hermes.Installer.Control;
using Hermes.Servicing.Acceptance;
using Xunit;

namespace Hermes.Servicing.WindowsTests;

[SupportedOSPlatform("windows")]
[Collection(nameof(RestrictedOwnerNativeCollection))]
public sealed class StartupDebugProbeTests
{
    // Count handoff only, not an execution/native-proof claim:
    // retain the original 18 intents and add exactly 40 finite fake cases.
    // StartupDebugProbeTests: 18 + 40 = 58. Overall Windows cases: 461 + 40 = 501.
    // No CI, PowerShell controller, genuine exit-37, or MSI gate is changed here.
    private const string CreatedLine =
        "HERMES_STARTUP_CREATED v=2 phase=created helper=11 pid=22 born=123\n";

    private static StartupDebugIdentity TargetIdentity { get; } =
        new(22, 123, true, true);

    private static StartupDebugOutcome Completed { get; } = new(37, true, 0, 0);
    private static StartupDebugOutcome Unavailable { get; } = new(0, true, 13, 0);

    private static IReadOnlyList<string> ExpectedRows =>
    [
        "HERMES_STARTUP v=2 stage=1 helper=11 pid=22 born=123 image=1 debug=1 context=1 flags=134742020 resumed=0 exit=0 parent=0 cleanup=0 fault=0 cleanupFault=0\n",
        "HERMES_STARTUP v=2 stage=2 helper=11 pid=22 born=123 image=1 debug=1 context=1 flags=134742020 resumed=1 exit=0 parent=0 cleanup=0 fault=0 cleanupFault=0\n",
        "HERMES_STARTUP v=2 stage=3 helper=11 pid=22 born=123 image=1 debug=1 context=1 flags=134742020 resumed=1 exit=37 parent=1 cleanup=1 fault=0 cleanupFault=0\n"
    ];

    [Theory]
    [InlineData("")]
    [InlineData("run")]
    [InlineData("Startup-debug-probe")]
    [InlineData("startup-debug-probe x")]
    [InlineData("startup-debug-probe --pid 7")]
    [InlineData("startup-debug-probe --command anything")]
    [InlineData("startup-debug-probe startup-debug-probe")]
    [InlineData("startup-debug-probe x y")]
    public void Finite_arity_refuses_before_any_capability(string text)
    {
        var host = new FakeHost();
        var arguments = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, StartupDebugProbe.Dispatch(arguments, host));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void Hosted_high_debugger_gate_precedes_the_only_fixed_execution()
    {
        var host = new FakeHost();
        Assert.Equal(0, StartupDebugProbe.Dispatch(["startup-debug-probe"], host));
        Assert.Equal(["gate", "fixed-probe"], host.Calls);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(13)]
    [InlineData(1314)]
    public void Refused_context_never_executes(int code)
    {
        var host = new FakeHost(gateError: new Win32Exception(code));
        Assert.Equal(1, StartupDebugProbe.Dispatch(["startup-debug-probe"], host));
        Assert.Equal(["gate"], host.Calls);
    }

    [Fact]
    public void Actual_child_verification_and_created_precede_one_resume_and_receipt()
    {
        var fixture = Create();
        using var token = RestrictedOwnerToken.Create(fixture.Tokens);
        var result = RestrictedOwnerChild.RunInert(token, fixture.Children);
        Assert.Equal(37u, result.ExitCode);
        Assert.True(result.CleanupCompleted);
        Assert.Equal(new[]
        {
            "job", "create", "assign", "facts", "thread", "access",
            "identity", "created", "identity", "resume", "resumed-row",
            "trace-wait", "dispose"
        }, fixture.Child.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_cancelled_created_never_resumes(bool cancellation)
    {
        Exception error = cancellation
            ? new OperationCanceledException()
            : new Win32Exception(13, "Synthetic wrong CREATED identity.");
        var fixture = Create(createdError: error);
        using var token = RestrictedOwnerToken.Create(fixture.Tokens);
        var actual = Record.Exception(() =>
            RestrictedOwnerChild.RunInert(token, fixture.Children));
        Assert.Same(error, actual);
        Assert.DoesNotContain("resume", fixture.Child.Calls);
        Assert.True(fixture.Child.IsClosed);
    }

    [Fact]
    public void Timeout_and_cleanup_fault_remain_distinct()
    {
        var original = new Win32Exception(1460);
        var cleanup = new Win32Exception(6);
        var fixture = Create(waitError: original, cleanupError: cleanup);
        using var token = RestrictedOwnerToken.Create(fixture.Tokens);
        var error = Assert.Throws<AggregateException>(() =>
            RestrictedOwnerChild.RunInert(token, fixture.Children));
        Assert.Same(original, error.InnerExceptions[0]);
        Assert.Equal((1460, 6), StartupDebugProbe.SplitFailure(error));
        Assert.False(fixture.Child.IsClosed);
    }

    [Fact]
    public void Cleanup_only_fault_is_not_relabelled_as_execution_failure()
    {
        var fixture = Create(cleanupError: new Win32Exception(6));
        using var token = RestrictedOwnerToken.Create(fixture.Tokens);
        var error = Assert.Throws<AcceptanceChildCleanupException>(() =>
            RestrictedOwnerChild.RunInert(token, fixture.Children));
        Assert.Equal((0, 6), StartupDebugProbe.SplitFailure(error));
        Assert.DoesNotContain("normal-wait", fixture.Child.Calls);
        Assert.False(fixture.Child.IsClosed);
    }

    [Fact]
    public void Dispatch_does_not_propagate_arbitrary_failure_payload()
    {
        var host = new FakeHost(runError:
            new InvalidOperationException("Synthetic private path and command payload."));
        Assert.Equal(1, StartupDebugProbe.Dispatch(["startup-debug-probe"], host));
        Assert.Equal(["gate", "fixed-probe"], host.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Identity_is_revalidated_after_created_before_resume(int mutation)
    {
        var fixture = Create(identityChange: mutation);
        var error = Assert.Throws<Win32Exception>(() => RunFixture(fixture));
        Assert.Equal(13, error.NativeErrorCode);
        Assert.Equal(2, fixture.Child.Calls.Count(call => call == "identity"));
        Assert.Contains("created", fixture.Child.Calls);
        Assert.DoesNotContain("resume", fixture.Child.Calls);
        Assert.DoesNotContain("resumed-row", fixture.Child.Calls);
        Assert.DoesNotContain("trace-wait", fixture.Child.Calls);
        Assert.Equal(TargetIdentity, Assert.IsType<StartupDebugChild>(
            fixture.Children.Child).Identity);
        Assert.True(fixture.Child.IsClosed);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(13)]
    public void Resume_failure_does_not_publish_resumed_or_wait(int code)
    {
        var failure = new Win32Exception(code);
        var fixture = Create(resumeError: failure);
        Assert.Same(failure, Record.Exception(() => RunFixture(fixture)));
        Assert.Equal(1, fixture.Child.Calls.Count(call => call == "resume"));
        Assert.DoesNotContain("resumed-row", fixture.Child.Calls);
        Assert.DoesNotContain("trace-wait", fixture.Child.Calls);
        Assert.True(fixture.Child.IsClosed);
    }

    [Fact]
    public void Resumed_publication_failure_prevents_wait_and_preserves_cleanup_fault()
    {
        var failure = new IOException("Synthetic receipt publication failure.");
        var fixture = Create(resumedError: failure, cleanupError: new Win32Exception(6));
        var error = Assert.Throws<AggregateException>(() => RunFixture(fixture));
        Assert.Same(failure, error.InnerExceptions[0]);
        Assert.Equal((13, 6), StartupDebugProbe.SplitFailure(error));
        Assert.Equal(1, fixture.Child.Calls.Count(call => call == "resume"));
        Assert.DoesNotContain("trace-wait", fixture.Child.Calls);
        Assert.False(fixture.Child.IsClosed);
    }

    [Fact]
    public void A_second_resume_never_reaches_the_owned_child()
    {
        var fixture = Create();
        Assert.Equal(37u, RunFixture(fixture));
        var child = Assert.IsType<StartupDebugChild>(fixture.Children.Child);
        Assert.Throws<Win32Exception>(child.Resume);
        Assert.Equal(1, fixture.Child.Calls.Count(call => call == "resume"));
        Assert.Equal(1, fixture.Child.Calls.Count(call => call == "resumed-row"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Wait_requires_a_successful_resume_receipt_and_is_not_replayed(bool completed)
    {
        var fixture = Create();
        if (completed)
            Assert.Equal(37u, RunFixture(fixture));
        using var child = completed
            ? Assert.IsType<StartupDebugChild>(fixture.Children.Child)
            : Assert.IsType<StartupDebugChild>(fixture.Children.Open());
        Assert.Throws<Win32Exception>(() => child.Wait(CancellationToken.None));
        Assert.Equal(completed ? 1 : 0,
            fixture.Child.Calls.Count(call => call == "trace-wait"));
    }

    [Fact]
    public void Real_control_emits_exact_ready_resumed_final_rows()
    {
        using var input = new CreatedStream(Encoding.ASCII.GetBytes(CreatedLine),
            CreatedLine.Length);
        var storage = new FakeStorage
            { CreatedBytes = StartupDebugProtocol.ReadCreated(input) };
        using var control = new FileStartupDebugControl(storage);
        var fixture = Create(control: control);
        Assert.Equal(37u, RunFixture(fixture));
        control.Finish(Assert.IsType<StartupDebugChild>(fixture.Children.Child).Identity,
            Completed, true);
        Assert.Equal(1, input.Reads);
        Assert.Equal(ExpectedRows,
            storage.Rows.Select(row => row.ToWire(storage.HelperPid)).ToArray());
        Assert.True(fixture.Child.IsClosed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Created_ack_is_exact_and_never_accepts_old_arm_authority(int variant)
    {
        var storage = new FakeStorage { CreatedBytes = Bytes(MalformedCreated(variant)) };
        using var control = new FileStartupDebugControl(storage);
        var fixture = Create(control: control);
        Assert.Equal(13, Assert.Throws<Win32Exception>(
            () => RunFixture(fixture)).NativeErrorCode);
        control.Finish(TargetIdentity, Unavailable, true);
        Assert.Equal([StartupDebugStage.Ready, StartupDebugStage.Final],
            storage.Rows.Select(row => row.Stage).ToArray());
        Assert.All(storage.Rows, row => Assert.False(row.Resumed));
        Assert.DoesNotContain("resume", fixture.Child.Calls);
        Assert.True(fixture.Child.IsClosed);
    }

    [Fact]
    public void Missing_created_is_bounded_at_45_seconds()
    {
        var storage = new FakeStorage
            { CreatedBytes = [], Step = TimeSpan.FromSeconds(45) };
        using var control = new FileStartupDebugControl(storage);
        var fixture = Create(control: control);
        Assert.Equal(1460, Assert.Throws<Win32Exception>(
            () => RunFixture(fixture)).NativeErrorCode);
        control.Finish(TargetIdentity, new(0, true, 1460, 0), true);
        Assert.Equal(1, storage.Reads);
        Assert.Equal(1, storage.Pauses);
        Assert.Equal(TimeSpan.FromSeconds(45), storage.Elapsed);
        Assert.All(storage.Rows, row => Assert.False(row.Resumed));
        Assert.DoesNotContain("resume", fixture.Child.Calls);
        Assert.True(fixture.Child.IsClosed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Cancellation_never_manufactures_a_resume_receipt(int phase)
    {
        using var cancellation = new CancellationTokenSource();
        var storage = new FakeStorage
        {
            CreatedBytes = phase == 1 ? [] : Bytes(CreatedLine),
            OnPause = phase == 1 ? cancellation.Cancel : null,
            OnRead = phase == 2 ? cancellation.Cancel : null
        };
        using var control = new FileStartupDebugControl(storage);
        if (phase == 0)
            cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            control.AwaitCreated(TargetIdentity, cancellation.Token));
        control.Finish(TargetIdentity, new(0, true, 1223, 0), true);
        Assert.Equal(phase == 0 ? 1 : 2, storage.Rows.Count);
        Assert.All(storage.Rows, row => Assert.False(row.Resumed));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Control_replay_and_wrong_phase_are_refused(int variant)
    {
        var storage = new FakeStorage();
        using var control = new FileStartupDebugControl(storage);
        PrepareReplay(control, variant);
        var before = storage.Rows.Count;
        Action replay = variant switch
        {
            0 or 4 or 6 => () => control.AwaitCreated(TargetIdentity, CancellationToken.None),
            1 or 3 or 5 => () => control.Resumed(TargetIdentity),
            2 => () => control.Finish(TargetIdentity, Completed, true),
            _ => throw new InvalidOperationException()
        };
        Assert.Equal(13, Assert.Throws<Win32Exception>(replay).NativeErrorCode);
        Assert.Equal(before, storage.Rows.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Control_io_failures_preserve_phase_facts_without_fake_resumed_rows(int point)
    {
        var fault = (StorageFault)point;
        var storage = new FakeStorage
        {
            Fault = fault,
            CreatedBytes = fault == StorageFault.Pause ? [] : Bytes(CreatedLine)
        };
        using var control = new FileStartupDebugControl(storage);
        Assert.Same(storage.Failure, ExerciseStorageFault(control, fault));
        if (fault is not (StorageFault.Final or StorageFault.Dispose))
        {
            control.Finish(TargetIdentity, Unavailable, true);
            Assert.Equal(fault == StorageFault.Resumed, storage.Rows[^1].Resumed);
        }
        Assert.Equal(fault == StorageFault.Final ? 1 : 0,
            storage.Rows.Count(row => row.Stage == StartupDebugStage.Resumed));
        Assert.Equal(fault == StorageFault.Dispose ? 1 : 0, storage.Disposals);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(129)]
    public void Created_file_length_is_checked_before_reading(int length)
    {
        using var stream = new CreatedStream([1], length);
        Assert.Equal(13, Assert.Throws<Win32Exception>(
            () => StartupDebugProtocol.ReadCreated(stream)).NativeErrorCode);
        Assert.Equal(0, stream.Reads);
    }

    [Fact]
    public void Created_reader_rejects_truncation()
    {
        using var stream = new CreatedStream([1], 8);
        Assert.Throws<EndOfStreamException>(() => StartupDebugProtocol.ReadCreated(stream));
        Assert.InRange(stream.Reads, 1, 2);
    }

    [Fact]
    public void Only_matching_identity_can_be_resumed_or_finalised()
    {
        var storage = new FakeStorage();
        using var control = new FileStartupDebugControl(storage);
        control.AwaitCreated(TargetIdentity, CancellationToken.None);
        var wrong = TargetIdentity with { Born = 124 };
        Assert.Throws<Win32Exception>(() => control.Resumed(wrong));
        Assert.Throws<Win32Exception>(() => control.Finish(wrong, Completed, true));
        Assert.Single(storage.Rows);
        control.Resumed(TargetIdentity);
        control.Finish(TargetIdentity, Completed, true);
        Assert.Equal(3, storage.Rows.Count);
        AssertInvalidRows(storage.Rows[0]);
    }

    private static ImmutableArray<byte> Bytes(string text) =>
        [.. Encoding.ASCII.GetBytes(text)];

    private static uint RunFixture(Fixture fixture)
    {
        using var token = RestrictedOwnerToken.Create(fixture.Tokens);
        return RestrictedOwnerChild.RunInert(token, fixture.Children).ExitCode;
    }

    private static string MalformedCreated(int variant) => variant switch
    {
        0 => "HERMES_STARTUP_ARM v=1 pid=22 born=123\n",
        1 => CreatedLine.Replace("v=2", "v=1", StringComparison.Ordinal),
        2 => CreatedLine.Replace("phase=created", "phase=armed", StringComparison.Ordinal),
        3 => CreatedLine.Replace("pid=22", "pid=23", StringComparison.Ordinal),
        4 => CreatedLine.Replace("born=123", "born=124", StringComparison.Ordinal),
        5 => CreatedLine.Replace("helper=11", "helper=12", StringComparison.Ordinal),
        6 => CreatedLine.Replace("\n", "\r\n", StringComparison.Ordinal),
        7 => CreatedLine + "g\n",
        _ => throw new InvalidOperationException()
    };

    private static void PrepareReplay(FileStartupDebugControl control, int variant)
    {
        if (variant is 0 or 1 or 2 or 5)
            control.AwaitCreated(TargetIdentity, CancellationToken.None);
        if (variant is 1 or 2)
            control.Resumed(TargetIdentity);
        if (variant is 2 or 4 or 5)
            control.Finish(TargetIdentity, variant == 2 ? Completed : Unavailable, true);
        if (variant == 6)
            control.Dispose();
    }

    private static Exception? ExerciseStorageFault(
        FileStartupDebugControl control, StorageFault fault) => Record.Exception(() =>
    {
        if (fault != StorageFault.Dispose)
            control.AwaitCreated(TargetIdentity, CancellationToken.None);
        if (fault is StorageFault.Resumed or StorageFault.Final)
            control.Resumed(TargetIdentity);
        if (fault == StorageFault.Final)
            control.Finish(TargetIdentity, Completed, true);
        if (fault == StorageFault.Dispose)
            control.Dispose();
    });

    private static void AssertInvalidRows(StartupDebugControlRow ready)
    {
        StartupDebugControlRow[] invalid =
        [
            ready with { Stage = (StartupDebugStage)0 },
            ready with { Resumed = true },
            ready with { Parent = true },
            ready with { Outcome = new(0, false, -1, 0) }
        ];
        Assert.All(invalid, row =>
            Assert.Throws<Win32Exception>(() => row.ToWire(11)));
        Assert.Throws<Win32Exception>(() => ready.ToWire(0));
    }

    private static Fixture Create(
        Exception? createdError = null, Exception? waitError = null,
        Exception? cleanupError = null, Exception? resumedError = null,
        Exception? resumeError = null, int identityChange = -1,
        IStartupDebugControl? control = null)
    {
        var child = new FakeChild(waitError, cleanupError, resumeError, identityChange);
        return new(new FakeTokens(), child,
            new(new SingleChild(child),
                control ?? new FakeControl(child.Calls, createdError, resumedError),
                CancellationToken.None));
    }

    private sealed record Fixture(
        FakeTokens Tokens, FakeChild Child, StartupDebugChildren Children);

    private sealed class FakeHost(
        Exception? gateError = null, Exception? runError = null) : IStartupDebugHost
    {
        internal readonly List<string> Calls = [];

        public void RequireContext()
        {
            Calls.Add("gate");
            if (gateError is { } error)
                throw error;
        }

        public int RunFixedProbe()
        {
            Calls.Add("fixed-probe");
            if (runError is { } error)
                throw error;
            return 0;
        }
    }

    private sealed class SingleChild(FakeChild child) : IAcceptanceChildFactory
    {
        public IAcceptanceOwnedChild Open() => child;
    }

    private sealed class FakeControl(
        List<string> calls, Exception? failure, Exception? resumedFailure)
        : IStartupDebugControl
    {
        public void AwaitCreated(
            StartupDebugIdentity identity, CancellationToken cancellationToken)
        {
            Assert.Equal(TargetIdentity, identity);
            cancellationToken.ThrowIfCancellationRequested();
            calls.Add("created");
            if (failure is { } error)
                throw error;
        }

        public void Resumed(StartupDebugIdentity identity)
        {
            Assert.Equal(TargetIdentity, identity);
            calls.Add("resumed-row");
            if (resumedFailure is { } error)
                throw error;
        }
    }

    private sealed class FakeChild(
        Exception? waitError, Exception? cleanupError,
        Exception? resumeError, int identityChange)
        : IAcceptanceOwnedChild, IAcceptanceStartupChild
    {
        internal readonly List<string> Calls = [];
        private bool _closed;
        private int _identityReads;
        public bool IsClosed => _closed;
        public void CreateKillOnCloseJob() => Calls.Add("job");
        public void CreateSuspended(IAcceptanceRestrictedToken token) => Calls.Add("create");
        public void AssignJob() => Calls.Add("assign");
        public void Resume()
        {
            Calls.Add("resume");
            if (resumeError is { } error)
                throw error;
        }

        public RestrictedOwnerTokenFacts ReadPrimaryFacts()
        {
            Calls.Add("facts");
            return RestrictedOwnerTokenFacts.Required;
        }

        public MsiContextRead ReadThreadToken()
        {
            Calls.Add("thread");
            return MsiContextRead.Absent;
        }

        public AcceptanceChildAccessResult ReadObjectAccess()
        {
            Calls.Add("access");
            return AcceptanceChildAccessResult.Unknown;
        }

        public StartupDebugIdentity ReadStartupIdentity()
        {
            Calls.Add("identity");
            return _identityReads++ == 0 ? TargetIdentity : identityChange switch
            {
                0 => TargetIdentity with { Pid = 23 },
                1 => TargetIdentity with { Born = 124 },
                2 => TargetIdentity with { ImageMatched = false },
                3 => TargetIdentity with { DebuggerPresent = false },
                _ => TargetIdentity
            };
        }

        public uint Wait(CancellationToken cancellationToken)
        {
            Calls.Add("normal-wait");
            throw new InvalidOperationException("Ordinary ten-second wait must not change.");
        }

        public uint WaitForStartupTrace(CancellationToken cancellationToken)
        {
            Calls.Add("trace-wait");
            cancellationToken.ThrowIfCancellationRequested();
            if (waitError is { } error)
                throw error;
            return 37;
        }

        public void Dispose()
        {
            if (_closed)
                return;
            Calls.Add("dispose");
            if (cleanupError is { } error)
                throw new AcceptanceChildCleanupException([error]);
            _closed = true;
        }
    }

    private enum StorageFault
    {
        None,
        Ready,
        Read,
        Pause,
        Resumed,
        Final,
        Dispose
    }

    private sealed class FakeStorage : IStartupDebugControlStorage
    {
        private ImmutableArray<StartupDebugControlRow> _rows = [];
        private TimeSpan _elapsed;

        internal StorageFault Fault { get; init; }
        internal ImmutableArray<byte> CreatedBytes { get; init; } = Bytes(CreatedLine);
        internal TimeSpan Step { get; init; } = TimeSpan.FromMilliseconds(25);
        internal Action? OnRead { get; init; }
        internal Action? OnPause { get; init; }
        internal IOException Failure { get; } = new("Synthetic control I/O failure.");
        internal IReadOnlyList<StartupDebugControlRow> Rows => _rows;
        internal int Reads;
        internal int Pauses;
        internal int Disposals;

        public int HelperPid => 11;
        public TimeSpan Elapsed => _elapsed;

        public void Publish(StartupDebugControlRow row)
        {
            Throw(row.Stage switch
            {
                StartupDebugStage.Ready => StorageFault.Ready,
                StartupDebugStage.Resumed => StorageFault.Resumed,
                StartupDebugStage.Final => StorageFault.Final,
                _ => throw new InvalidOperationException()
            });
            _rows = _rows.Add(row);
        }

        public ImmutableArray<byte> ReadCreated()
        {
            Reads++;
            Throw(StorageFault.Read);
            OnRead?.Invoke();
            return CreatedBytes;
        }

        public void Pause(CancellationToken cancellationToken)
        {
            Pauses++;
            Throw(StorageFault.Pause);
            OnPause?.Invoke();
            _elapsed += Step;
            cancellationToken.ThrowIfCancellationRequested();
        }

        public void Dispose()
        {
            Disposals++;
            Throw(StorageFault.Dispose);
        }

        private void Throw(StorageFault operation)
        {
            if (Fault == operation)
                throw Failure;
        }
    }

    private sealed class CreatedStream(byte[] bytes, long length)
        : MemoryStream(bytes, writable: false)
    {
        internal int Reads;
        public override long Length => length;

        public override int Read(Span<byte> buffer)
        {
            Reads++;
            return base.Read(buffer);
        }
    }

    private sealed class FakeTokens : IRestrictedOwnerTokens
    {
        public void RequireDisposableJob() { }
        public MsiContextRead ReadThreadToken() => MsiContextRead.Absent;
        public IAcceptanceSourceToken OpenCurrentPrimary() => new Source();
        public void SetMediumIntegrity(IAcceptanceRestrictedToken token) { }

        public IAcceptanceRestrictedToken Filter(
            IAcceptanceSourceToken source, AcceptanceTokenFilter flags)
        {
            Assert.Equal(AcceptanceTokenFilter.DisableMaxPrivilege |
                AcceptanceTokenFilter.LuaToken, flags);
            return new Filtered();
        }

        public RestrictedOwnerTokenFacts ReadFacts(
            IAcceptanceRestrictedToken token, IAcceptanceSourceToken source) =>
            RestrictedOwnerTokenFacts.Required;
    }

    private sealed class Source : IAcceptanceSourceToken
    {
        private bool _closed;
        public bool IsClosed => _closed;
        public void Dispose() => _closed = true;
    }

    private sealed class Filtered : IAcceptanceRestrictedToken
    {
        private bool _closed;
        public bool IsClosed => _closed;
        public void Dispose() => _closed = true;
    }
}
