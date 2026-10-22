using System.ComponentModel;
using Hermes.Servicing.Acceptance;
using Xunit;

namespace Hermes.Servicing.Tests;

public sealed class StepJobContainmentTests
{
    private enum Call
    {
        Guard, Create, Configure, Read, Assign, Clear, Release, Abort, Close
    }

    private enum NativeCall
    {
        CurrentClose, JobClose, JobTerminate, CurrentTerminate
    }

    private const int NativeCloseError = 6;
    private const int NativeTerminationError = 5;

    private static StepJobContext Admitted => new(
        true, "true", "Windows", "github-hosted", "installer",
        "https://github.com", "johnazariah/hermes");

    private static StepJobReadback Empty =>
        new(StepJobLimits.KillOnClose, false, 0, 0, 0);

    private static StepJobReadback Assigned =>
        Empty with { CurrentIsMember = true, ActiveProcesses = 1 };

    private static StepJobReadback Released =>
        Assigned with { Limits = StepJobLimits.None };

    private static IReadOnlyList<StepJobReadback> NormalReads =>
        [Empty, Assigned, Assigned, Released];

    [Fact]
    public void Begin_and_normal_release_require_actual_ordered_readbacks()
    {
        var fixture = new Fixture();
        var scope = fixture.Bootstrap().Begin();
        Assert.Equal<Call>(
            [Call.Guard, Call.Create, Call.Configure, Call.Read, Call.Assign, Call.Read],
            fixture.Calls);
        scope.CompleteAfterChildrenExited();
        scope.Dispose();
        scope.Dispose();
        Assert.Equal<Call>(
            [Call.Guard, Call.Create, Call.Configure, Call.Read, Call.Assign,
                Call.Read, Call.Read, Call.Clear, Call.Read, Call.Release],
            fixture.Calls);
        Assert.Throws<ObjectDisposedException>(scope.CompleteAfterChildrenExited);
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
    public void Every_guard_field_is_required_before_resource_creation(int defect)
    {
        var fixture = new Fixture { Context = RejectedContext(defect) };
        Assert.Throws<InvalidOperationException>(() => fixture.Bootstrap().Begin());
        Assert.Equal<Call>([Call.Guard], fixture.Calls);
    }

    private static StepJobContext RejectedContext(int defect) => defect switch
    {
        0 => Admitted with { Windows = false },
        1 => Admitted with { Actions = null },
        2 => Admitted with { RunnerOs = "Linux" },
        3 => Admitted with { RunnerEnvironment = "self-hosted" },
        4 => Admitted with { Job = "other" },
        5 => Admitted with { Server = "https://example.invalid" },
        6 => Admitted with { Repository = "other/hermes" },
        7 => Admitted with { Repository = "Johnazariah/hermes" },
        _ => throw new ArgumentOutOfRangeException(nameof(defect))
    };

    public static IEnumerable<object[]> ReadbackDefects() =>
        Enumerable.Range(0, 4).SelectMany(stage =>
            Enumerable.Range(0, 9).Select(defect => new object[] { stage, defect }));

    [Theory]
    [MemberData(nameof(ReadbackDefects))]
    public void Invalid_actual_readbacks_never_report_clean_release(int stage, int defect)
    {
        var fixture = new Fixture
        {
            Reads = NormalReads.Select((facts, index) =>
                index == stage ? Corrupt(facts, defect) : facts).ToArray()
        };
        AssertReadbackFailure(fixture, stage);
        Assert.Equal(stage == 0 ? Call.Close : Call.Abort, fixture.Calls[^1]);
        Assert.DoesNotContain(Call.Release, fixture.Calls);
        Assert.Equal(stage == 3, fixture.Calls.Contains(Call.Clear));
    }

    private static StepJobReadback Corrupt(StepJobReadback facts, int defect) =>
        defect switch
        {
            0 => facts with { Limits = facts.Limits ^ StepJobLimits.KillOnClose },
            1 => facts with { Limits = facts.Limits | StepJobLimits.Breakaway },
            2 => facts with { Limits = facts.Limits | StepJobLimits.SilentBreakaway },
            3 => facts with { Limits = facts.Limits | (StepJobLimits)1 },
            4 => facts with { UiRestrictions = 1 },
            5 => facts with { HandleFlags = 1 },
            6 => facts with { HandleFlags = 2 },
            7 => facts with { CurrentIsMember = !facts.CurrentIsMember },
            8 => facts with { ActiveProcesses = facts.ActiveProcesses + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };

    private static void AssertReadbackFailure(Fixture fixture, int stage)
    {
        var bootstrap = fixture.Bootstrap();
        if (stage < 2)
        {
            Assert.Throws<InvalidDataException>(() => bootstrap.Begin());
            return;
        }
        var scope = bootstrap.Begin();
        Assert.Throws<InvalidDataException>(scope.CompleteAfterChildrenExited);
        scope.Dispose();
        Assert.Throws<ObjectDisposedException>(scope.CompleteAfterChildrenExited);
    }

    [Theory]
    [InlineData((int)Call.Guard)]
    [InlineData((int)Call.Create)]
    [InlineData((int)Call.Configure)]
    [InlineData((int)Call.Read)]
    [InlineData((int)Call.Assign)]
    public void Bootstrap_resource_errors_close_only_the_unassigned_empty_job(int failure)
    {
        var fixture = new Fixture { Failure = (Call)failure };
        Assert.Throws<IOException>(() => fixture.Bootstrap().Begin());
        Assert.DoesNotContain(Call.Abort, fixture.Calls);
        Assert.DoesNotContain(Call.Release, fixture.Calls);
        Assert.Equal(failure >= (int)Call.Configure ? Call.Close : (Call)failure,
            fixture.Calls[^1]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Query_errors_use_ownership_state_not_assumed_configuration(int read)
    {
        var fixture = new Fixture { FailedRead = read };
        var bootstrap = fixture.Bootstrap();
        if (read <= 2)
            Assert.Throws<IOException>(() => bootstrap.Begin());
        else
        {
            var scope = bootstrap.Begin();
            Assert.Throws<IOException>(scope.CompleteAfterChildrenExited);
            scope.Dispose();
        }
        Assert.Equal(read == 1 ? Call.Close : Call.Abort, fixture.Calls[^1]);
        Assert.DoesNotContain(Call.Release, fixture.Calls);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(uint.MaxValue)]
    public void Normal_release_requires_exactly_one_actual_active_process(uint active)
    {
        var fixture = new Fixture
        {
            Reads = [Empty, Assigned, Assigned with { ActiveProcesses = active }]
        };
        var scope = fixture.Bootstrap().Begin();
        Assert.Throws<InvalidDataException>(scope.CompleteAfterChildrenExited);
        Assert.DoesNotContain(Call.Clear, fixture.Calls);
        Assert.DoesNotContain(Call.Release, fixture.Calls);
        Assert.Equal(Call.Abort, fixture.Calls[^1]);
    }

    [Theory]
    [InlineData((int)Call.Clear)]
    [InlineData((int)Call.Release)]
    public void Completion_policy_errors_request_assigned_abort(int failure)
    {
        var fixture = new Fixture { Failure = (Call)failure };
        var scope = fixture.Bootstrap().Begin();
        Assert.Throws<IOException>(scope.CompleteAfterChildrenExited);
        Assert.Equal(Call.Abort, fixture.Calls[^1]);
        Assert.Throws<ObjectDisposedException>(scope.CompleteAfterChildrenExited);
        scope.Dispose();
        Assert.Equal(1, fixture.Calls.Count(call => call == Call.Abort));
    }

    [Fact]
    public void Disposal_without_completion_aborts_without_clearing_limits()
    {
        var fixture = new Fixture();
        var scope = fixture.Bootstrap().Begin();
        Assert.Throws<InvalidOperationException>(scope.Dispose);
        scope.Dispose();
        Assert.Equal(Call.Abort, fixture.Calls[^1]);
        Assert.DoesNotContain(Call.Clear, fixture.Calls);
        Assert.DoesNotContain(Call.Release, fixture.Calls);
        Assert.Throws<ObjectDisposedException>(scope.CompleteAfterChildrenExited);
    }

    [Fact]
    public void Assigned_abort_errors_are_not_success_defaults()
    {
        var fixture = new Fixture { Failure = Call.Abort };
        var scope = fixture.Bootstrap().Begin();
        Assert.Throws<IOException>(scope.Dispose);
        Assert.DoesNotContain(Call.Clear, fixture.Calls);
        Assert.DoesNotContain(Call.Release, fixture.Calls);
        Assert.Throws<ObjectDisposedException>(scope.CompleteAfterChildrenExited);
    }

    [Fact]
    public void Empty_job_close_errors_are_not_success_defaults()
    {
        var fixture = new Fixture
        {
            Failure = Call.Close,
            Reads = [Empty with { HandleFlags = 1 }]
        };
        Assert.Throws<IOException>(() => fixture.Bootstrap().Begin());
        Assert.DoesNotContain(Call.Assign, fixture.Calls);
        Assert.DoesNotContain(Call.Abort, fixture.Calls);
    }

    [Fact]
    public void Successful_release_does_not_allow_another_job_in_the_same_step()
    {
        var fixture = new Fixture();
        var bootstrap = fixture.Bootstrap();
        var scope = bootstrap.Begin();
        scope.CompleteAfterChildrenExited();
        Assert.Throws<InvalidOperationException>(() => bootstrap.Begin());
        Assert.Equal(1, fixture.Calls.Count(call => call == Call.Create));
    }

    [Fact]
    public void Resource_failure_does_not_allow_a_second_bootstrap_attempt()
    {
        var fixture = new Fixture { Failure = Call.Create };
        var bootstrap = fixture.Bootstrap();
        Assert.Throws<IOException>(() => bootstrap.Begin());
        Assert.Throws<InvalidOperationException>(() => bootstrap.Begin());
        Assert.Equal(1, fixture.Calls.Count(call => call == Call.Create));
    }

    [Fact]
    public void Native_owner_quiescent_release_does_not_terminate_or_reuse_handles()
    {
        var fixture = new NativeOwnerFixture();
        var scope = fixture.Open();
        scope.CompleteAfterChildrenExited();
        scope.Dispose();
        fixture.Handles.ReleaseVerified();
        fixture.Handles.Abandon();
        Assert.Equal<NativeCall>(
            [NativeCall.CurrentClose, NativeCall.JobClose], fixture.Calls);
        Assert.Throws<ObjectDisposedException>(fixture.Job.ArmTermination);
        Assert.Throws<ObjectDisposedException>(fixture.Current.ArmTermination);
    }

    [Theory]
    [InlineData((int)NativeCall.CurrentClose)]
    [InlineData((int)NativeCall.JobClose)]
    public void Native_owner_close_failure_keeps_job_armed_for_scope_abort(int failure)
    {
        var fixture = FailingNative((NativeCall)failure, NativeCloseError);
        var scope = fixture.Open();
        var error = Assert.Throws<Win32Exception>(scope.CompleteAfterChildrenExited);
        Assert.Equal(NativeCloseError, error.NativeErrorCode);
        Assert.Contains("handle close failed", error.Message);
        scope.Dispose();
        fixture.Handles.Abandon();
        NativeCall[] expected = (NativeCall)failure == NativeCall.CurrentClose
            ? [NativeCall.CurrentClose, NativeCall.JobTerminate,
                NativeCall.JobClose, NativeCall.CurrentClose]
            : [NativeCall.CurrentClose, NativeCall.JobClose,
                NativeCall.JobTerminate, NativeCall.JobClose];
        Assert.Equal<NativeCall>(expected, fixture.Calls);
    }

    [Fact]
    public void Native_owner_assigned_abandonment_terminates_before_closing_handles()
    {
        var fixture = new NativeOwnerFixture();
        var scope = fixture.Open();
        Assert.Throws<InvalidOperationException>(scope.Dispose);
        scope.Dispose();
        fixture.Handles.Abandon();
        Assert.Equal<NativeCall>(
            [NativeCall.JobTerminate, NativeCall.JobClose, NativeCall.CurrentClose],
            fixture.Calls);
    }

    [Fact]
    public void Native_owner_termination_error_retains_job_without_attempting_close()
    {
        var fixture = FailingNative(NativeCall.JobTerminate, NativeTerminationError);
        var scope = fixture.Open();
        var error = Assert.Throws<Win32Exception>(scope.Dispose);
        Assert.Equal(NativeTerminationError, error.NativeErrorCode);
        Assert.Contains("termination failed", error.Message);
        Assert.Equal<NativeCall>(
            [NativeCall.JobTerminate, NativeCall.CurrentClose], fixture.Calls);
        scope.Dispose();
        Assert.Null(fixture.Job.Abandon());
        fixture.Handles.Abandon();
        Assert.Equal<NativeCall>(
            [NativeCall.JobTerminate, NativeCall.CurrentClose,
                NativeCall.JobTerminate, NativeCall.JobClose], fixture.Calls);
    }

    [Fact]
    public void Native_owner_reports_both_termination_and_process_close_errors()
    {
        var fixture = new NativeOwnerFixture
        {
            Results = new Dictionary<NativeCall, IReadOnlyList<int>>
            {
                [NativeCall.JobTerminate] = [NativeTerminationError],
                [NativeCall.CurrentClose] = [NativeCloseError]
            }
        };
        var scope = fixture.Open();
        var errors = Assert.Throws<AggregateException>(scope.Dispose).InnerExceptions;
        Assert.Collection(errors,
            error => Assert.Equal(NativeTerminationError,
                Assert.IsType<Win32Exception>(error).NativeErrorCode),
            error => Assert.Equal(NativeCloseError,
                Assert.IsType<Win32Exception>(error).NativeErrorCode));
        fixture.Handles.Abandon();
        Assert.Equal<NativeCall>(
            [NativeCall.JobTerminate, NativeCall.CurrentClose,
                NativeCall.JobTerminate, NativeCall.JobClose, NativeCall.CurrentClose],
            fixture.Calls);
    }

    [Theory]
    [InlineData((int)NativeCall.CurrentClose)]
    [InlineData((int)NativeCall.JobClose)]
    public void Native_owner_persistent_close_error_does_not_consume_ownership(int failure)
    {
        var fixture = FailingNative(
            (NativeCall)failure, NativeCloseError, NativeCloseError);
        var scope = fixture.Open();
        var error = Assert.Throws<Win32Exception>(scope.CompleteAfterChildrenExited);
        Assert.Equal(NativeCloseError, error.NativeErrorCode);
        fixture.Handles.Abandon();
        fixture.Handles.Abandon();
        NativeCall[] expected = (NativeCall)failure == NativeCall.CurrentClose
            ? [NativeCall.CurrentClose, NativeCall.JobTerminate, NativeCall.JobClose,
                NativeCall.CurrentClose, NativeCall.CurrentClose]
            : [NativeCall.CurrentClose, NativeCall.JobClose, NativeCall.JobTerminate,
                NativeCall.JobClose, NativeCall.JobTerminate, NativeCall.JobClose];
        Assert.Equal<NativeCall>(expected, fixture.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Native_owner_empty_cleanup_never_terminates(bool retainCurrent)
    {
        var fixture = new NativeOwnerFixture(retainCurrent);
        fixture.Handles.Abandon();
        fixture.Handles.Abandon();
        NativeCall[] expected = retainCurrent
            ? [NativeCall.JobClose, NativeCall.CurrentClose]
            : [NativeCall.JobClose];
        Assert.Equal<NativeCall>(expected, fixture.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Native_owner_empty_close_error_retains_only_failed_handle(bool retainCurrent)
    {
        var fixture = new NativeOwnerFixture(retainCurrent)
        {
            Results = new Dictionary<NativeCall, IReadOnlyList<int>>
            {
                [NativeCall.JobClose] = [NativeCloseError]
            }
        };
        var error = Assert.Throws<Win32Exception>(fixture.Handles.Abandon);
        Assert.Equal(NativeCloseError, error.NativeErrorCode);
        fixture.Handles.Abandon();
        fixture.Handles.Abandon();
        NativeCall[] expected = retainCurrent
            ? [NativeCall.JobClose, NativeCall.CurrentClose, NativeCall.JobClose]
            : [NativeCall.JobClose, NativeCall.JobClose];
        Assert.Equal<NativeCall>(expected, fixture.Calls);
    }

    [Theory]
    [InlineData((int)NativeCall.JobClose)]
    [InlineData((int)NativeCall.CurrentClose)]
    public void Finalization_of_unarmed_persistent_close_failure_never_terminates(int failure)
    {
        var call = (NativeCall)failure;
        var fixture = FailingNative(call, NativeCloseError, NativeCloseError);
        var owner = call == NativeCall.JobClose ? fixture.Job : fixture.Current;
        var expected = new StepJobHandleFailure(
            StepJobHandleOperation.Close, NativeCloseError);
        Assert.Equal(expected, owner.Abandon());
        AssertFinalized(owner, owner.FinalizeOwnership(), expected);
        Assert.Equal<NativeCall>([call, call], fixture.Calls);
    }

    [Theory]
    [InlineData((int)NativeCall.JobClose)]
    [InlineData((int)NativeCall.JobTerminate)]
    public void Assigned_finalization_failure_is_terminal_without_claiming_release(int failure)
    {
        var call = (NativeCall)failure;
        var error = call == NativeCall.JobTerminate ? NativeTerminationError : NativeCloseError;
        var operation = call == NativeCall.JobTerminate
            ? StepJobHandleOperation.Terminate : StepJobHandleOperation.Close;
        var expected = new StepJobHandleFailure(operation, error);
        var fixture = FailingNative(call, error, error);
        fixture.Job.ArmTermination();
        Assert.Equal(expected, fixture.Job.Abandon());
        AssertFinalized(fixture.Job, fixture.Job.FinalizeOwnership(), expected);
        NativeCall[] expectedCalls = call == NativeCall.JobTerminate
            ? [NativeCall.JobTerminate, NativeCall.JobTerminate]
            : [NativeCall.JobTerminate, NativeCall.JobClose,
                NativeCall.JobTerminate, NativeCall.JobClose];
        Assert.Equal<NativeCall>(expectedCalls, fixture.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Successful_finalization_is_idempotent_and_uses_assignment_state(bool assigned)
    {
        var fixture = new NativeOwnerFixture();
        if (assigned)
            fixture.Job.ArmTermination();
        AssertFinalized(fixture.Job, fixture.Job.FinalizeOwnership(), null);
        AssertFinalized(fixture.Current, fixture.Current.FinalizeOwnership(), null);
        fixture.Handles.ReleaseVerified();
        fixture.Handles.Abandon();
        NativeCall[] expected = assigned
            ? [NativeCall.JobTerminate, NativeCall.JobClose, NativeCall.CurrentClose]
            : [NativeCall.JobClose, NativeCall.CurrentClose];
        Assert.Equal<NativeCall>(expected, fixture.Calls);
    }

    [Theory]
    [InlineData((int)NativeCall.CurrentClose, false)]
    [InlineData((int)NativeCall.CurrentClose, true)]
    [InlineData((int)NativeCall.JobClose, false)]
    [InlineData((int)NativeCall.JobClose, true)]
    public void Finalization_after_partial_release_preserves_failure_and_termination_intent(
        int failure, bool persistent)
    {
        var call = (NativeCall)failure;
        var fixture = FailingNative(call, NativeCloseError, persistent ? NativeCloseError : 0);
        fixture.Job.ArmTermination();
        var error = Assert.Throws<Win32Exception>(fixture.Handles.ReleaseVerified);
        Assert.Equal(NativeCloseError, error.NativeErrorCode);
        var job = fixture.Job.FinalizeOwnership();
        var current = fixture.Current.FinalizeOwnership();
        StepJobHandleFailure? expected = persistent
            ? new(StepJobHandleOperation.Close, NativeCloseError) : null;
        AssertFinalized(fixture.Job, job, call == NativeCall.JobClose ? expected : null);
        AssertFinalized(fixture.Current, current, call == NativeCall.CurrentClose ? expected : null);
        Assert.Equal<NativeCall>(PartialReleaseFinalizationCalls(call), fixture.Calls);
    }

    [Fact]
    public void Finalization_termination_failure_preserves_partial_cleanup_and_explicit_error()
    {
        var fixture = FailingNative(
            NativeCall.JobTerminate, NativeTerminationError, NativeTerminationError);
        var scope = fixture.Open();
        Assert.Throws<Win32Exception>(scope.Dispose);
        scope.Dispose();
        AssertFinalized(fixture.Job, fixture.Job.FinalizeOwnership(),
            new(StepJobHandleOperation.Terminate, NativeTerminationError));
        AssertFinalized(fixture.Current, fixture.Current.FinalizeOwnership(), null);
        var error = Assert.Throws<Win32Exception>(fixture.Handles.Abandon);
        Assert.Equal(NativeTerminationError, error.NativeErrorCode);
        Assert.Equal<NativeCall>(
            [NativeCall.JobTerminate, NativeCall.CurrentClose, NativeCall.JobTerminate],
            fixture.Calls);
    }

    [Fact]
    public void Finalization_after_verified_release_does_not_terminate_or_close_again()
    {
        var fixture = new NativeOwnerFixture();
        var scope = fixture.Open();
        scope.CompleteAfterChildrenExited();
        scope.Dispose();
        AssertFinalized(fixture.Job, fixture.Job.FinalizeOwnership(), null);
        AssertFinalized(fixture.Current, fixture.Current.FinalizeOwnership(), null);
        Assert.Equal<NativeCall>(
            [NativeCall.CurrentClose, NativeCall.JobClose], fixture.Calls);
    }

    private static void AssertFinalized(
        StepJobHandleOwner owner,
        StepJobHandleFinalization decision,
        StepJobHandleFailure? expected)
    {
        Assert.Equal(expected, decision.Failure);
        Assert.Equal(expected is null, decision.Released);
        Assert.Same(decision, owner.FinalizeOwnership());
        Assert.Equal(expected, owner.ReleaseVerified());
        Assert.Equal(expected, owner.Abandon());
        Assert.Throws<ObjectDisposedException>(owner.ArmTermination);
    }

    private static IReadOnlyList<NativeCall> PartialReleaseFinalizationCalls(NativeCall failure) =>
        failure == NativeCall.CurrentClose
            ? [NativeCall.CurrentClose, NativeCall.JobTerminate,
                NativeCall.JobClose, NativeCall.CurrentClose]
            : [NativeCall.CurrentClose, NativeCall.JobClose,
                NativeCall.JobTerminate, NativeCall.JobClose];

    private static NativeOwnerFixture FailingNative(
        NativeCall call, params int[] errors) => new()
    {
        Results = new Dictionary<NativeCall, IReadOnlyList<int>> { [call] = errors }
    };

    private sealed class NativeOwnerFixture
    {
        private readonly List<NativeCall> _calls = [];
        private int _read;

        internal NativeOwnerFixture(bool retainCurrent = true)
        {
            Job = new(new(
                () => Invoke(NativeCall.JobClose),
                () => Invoke(NativeCall.JobTerminate)));
            Current = new(new(
                () => Invoke(NativeCall.CurrentClose),
                () => Invoke(NativeCall.CurrentTerminate)));
            Handles = new(Job, retainCurrent ? Current : null);
        }

        internal IReadOnlyDictionary<NativeCall, IReadOnlyList<int>> Results { get; init; } =
            new Dictionary<NativeCall, IReadOnlyList<int>>();
        internal StepJobHandleOwner Job { get; }
        internal StepJobHandleOwner Current { get; }
        internal StepJobOwnedHandles Handles { get; }
        internal IReadOnlyList<NativeCall> Calls => _calls;

        internal StepJobScope Open() => StepJobScope.Open(new(
            () => { },
            Job.ArmTermination,
            () => NormalReads[_read++],
            () => { },
            Handles.Abandon,
            Handles.Abandon,
            Handles.ReleaseVerified));

        private int Invoke(NativeCall call)
        {
            var index = _calls.Count(value => value == call);
            _calls.Add(call);
            var results = Results.GetValueOrDefault(call, []);
            return index < results.Count ? results[index] : 0;
        }
    }

    private sealed class Fixture
    {
        private readonly List<Call> _calls = [];
        private int _read;

        internal StepJobContext Context { get; init; } = Admitted;
        internal IReadOnlyList<StepJobReadback> Reads { get; init; } = NormalReads;
        internal Call? Failure { get; init; }
        internal int FailedRead { get; init; }
        internal IReadOnlyList<Call> Calls => _calls;

        internal StepJobBootstrap Bootstrap() => new(new(ReadContext, Create));

        private StepJobContext ReadContext()
        {
            Touch(Call.Guard);
            return Context;
        }

        private StepJob Create()
        {
            Touch(Call.Create);
            return new(
                () => Touch(Call.Configure),
                () => Touch(Call.Assign),
                Read,
                () => Touch(Call.Clear),
                () => Touch(Call.Close),
                () => Touch(Call.Abort),
                () => Touch(Call.Release));
        }

        private StepJobReadback Read()
        {
            Touch(Call.Read);
            _read++;
            if (_read == FailedRead)
                throw new IOException("Synthetic job-query failure.");
            return Reads[_read - 1];
        }

        private void Touch(Call call)
        {
            _calls.Add(call);
            if (Failure == call)
                throw new IOException("Synthetic job-resource failure.");
        }
    }
}
