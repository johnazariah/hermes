using System.ComponentModel;
using Hermes.Servicing.Acceptance;
using Xunit;

namespace Hermes.Servicing.Tests;

public sealed class RestrictedSourcePowerShellControlTests
{
    private enum Point
    {
        Source, Job, Create, Assign, Child, Inventory, Resume, Wait,
        CloseChild, FinalSource, CloseSource
    }

    private static readonly Point[] ExpectedSequence =
    [
        Point.Source, Point.Job, Point.Source, Point.Create, Point.Assign,
        Point.Child, Point.Source, Point.Inventory, Point.Source,
        Point.Resume, Point.Wait, Point.CloseChild, Point.FinalSource, Point.CloseSource
    ];

    private static readonly Point[] CleanupSequence =
        [Point.CloseChild, Point.FinalSource, Point.CloseSource];

    public static IEnumerable<object[]> FailurePoints =>
        ExpectedSequence.Select((point, index) => new object[]
        {
            (int)point,
            ExpectedSequence.Take(index + 1).Count(value => value == point)
        });

    public static IEnumerable<object[]> Combinations =>
        Enumerable.Range(0, 64).Select(mask => new object[] { mask });

    [Theory]
    [InlineData(0u)]
    [InlineData(37u)]
    [InlineData(3221225794u)]
    public void Real_exit_values_are_observations_not_acceptance_predicates(uint exit)
    {
        var scope = new FakeScope(exit);
        var factory = new FakeFactory(scope);
        var attempt = RestrictedSourcePowerShellControl.Observe(factory);
        var output = ClosedOutput(scope);
        AcceptanceSourceControlOrchestration.Complete(attempt, output, () => { });
        Assert.Equal(1, factory.Opens);
        Assert.Equal(ExpectedSequence, scope.Calls);
        Assert.Equal((uint?)exit, attempt.Observation.ExitCode);
        Assert.True(attempt.Observation.IsConclusive);
        Assert.True(attempt.Observation.CleanupCompleted);
        Assert.Equal(1, output.Writes);
        Assert.Contains($"exitObserved=1 exit={exit} ", attempt.Observation.Row);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(1314)]
    public void Clean_creation_denial_is_inconclusive_but_original_gate_still_fails(int code)
    {
        var denial = new AcceptanceSourceControlCreationDeniedException(code);
        var scope = new FakeScope().Fail(Point.Create, denial);
        var factory = new FakeFactory(scope);
        var attempt = RestrictedSourcePowerShellControl.Observe(factory);
        var gate = NewFault();
        var error = Record.Exception(() => AcceptanceSourceControlOrchestration.Complete(
            attempt, ClosedOutput(scope), () => throw gate));
        Assert.Same(gate, error);
        Assert.Null(attempt.Failure);
        Assert.Equal(code, attempt.Observation.Error);
        Assert.Null(attempt.Observation.ExitCode);
        Assert.False(attempt.Observation.IsConclusive);
        Assert.True(attempt.Observation.CleanupCompleted);
        Assert.Equal(1, factory.Opens);
        Assert.Equal(1, scope.Calls.Count(point => point == Point.Create));
        Assert.DoesNotContain(Point.Resume, scope.Calls);
        Assert.Equal(CleanupSequence, scope.Calls.TakeLast(3));
    }

    [Theory]
    [MemberData(nameof(FailurePoints))]
    public void Every_operation_fault_is_retained_and_cleanup_is_not_retried(
        int value, int occurrence)
    {
        var point = (Point)value;
        var fault = NewFault();
        var scope = new FakeScope().Fail(point, fault, occurrence);
        var attempt = RestrictedSourcePowerShellControl.Observe(new FakeFactory(scope));
        var error = Record.Exception(() => AcceptanceSourceControlOrchestration.Complete(
            attempt, new FakeOutput(), () => { }));
        Assert.Same(fault, error);
        Assert.False(attempt.Observation.IsConclusive);
        Assert.Equal(CleanupSequence, scope.Calls.TakeLast(3));
        Assert.Equal(1, scope.Calls.Count(item => item == Point.CloseSource));
        var count = scope.Calls.Count;
        scope.Dispose();
        Assert.Equal(count, scope.Calls.Count);
        Assert.True(scope.IsDisposed);
        Assert.True(scope.Calls.Count(item => item == Point.Resume) <= 1);
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void Orchestration_retains_gate_execution_multiple_cleanup_source_and_output_faults(
        int mask)
    {
        var scenario = CreateScenario(mask);
        var attempt = RestrictedSourcePowerShellControl.Observe(
            new FakeFactory(scenario.Scope));
        var gates = 0;
        var output = new FakeOutput(
            _ => Assert.Equal(CleanupSequence, scenario.Scope.Calls.TakeLast(3)),
            scenario.Reporting);
        var error = Record.Exception(() => AcceptanceSourceControlOrchestration.Complete(
            attempt, output, () =>
            {
                gates++;
                AcceptanceSourceControlOrchestration.Throw(scenario.Gate);
            }));
        Assert.Equal(1, gates);
        Assert.Equal(1, output.Writes);
        AssertFailures(scenario.Expected, error);
        Assert.Equal(ExpectedSequence, scenario.Scope.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throwing_output_sees_completed_cleanup_and_cannot_abandon_the_subject(
        bool gateFails)
    {
        var scope = new FakeScope();
        var attempt = RestrictedSourcePowerShellControl.Observe(new FakeFactory(scope));
        var reporting = NewFault();
        var gate = gateFails ? NewFault() : null;
        var output = new FakeOutput(_ =>
        {
            Assert.True(scope.IsClosed);
            Assert.True(scope.ChildClosed);
            Assert.True(scope.SourceClosed);
        }, reporting);
        var error = Record.Exception(() => AcceptanceSourceControlOrchestration.Complete(
            attempt, output, () => AcceptanceSourceControlOrchestration.Throw(gate)));
        AssertFailures(new Exception?[] { gate, reporting }.OfType<Exception>().ToArray(), error);
        Assert.Equal(ExpectedSequence, scope.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Reporting_failure_does_not_skip_original_verification_or_reached_predicates(
        int failedGate)
    {
        var scope = new FakeScope();
        var attempt = RestrictedSourcePowerShellControl.Observe(new FakeFactory(scope));
        var reporting = NewFault();
        var original = NewFault();
        List<int> reached = [];
        var error = Record.Exception(() => AcceptanceSourceControlOrchestration.Complete(
            attempt, ClosedOutput(scope, reporting), () =>
            {
                foreach (var gate in Enumerable.Range(0, 6))
                {
                    reached.Add(gate);
                    if (gate == failedGate)
                        throw original;
                }
            }));
        Assert.Equal(Enumerable.Range(0, failedGate + 1), reached);
        AssertFailures([original, reporting], error);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(1314)]
    public void Creation_denial_with_multiple_cleanup_and_final_source_faults_is_unsafe(int code)
    {
        var denial = new AcceptanceSourceControlCreationDeniedException(code);
        var child = NewFault();
        var source = NewFault();
        var close = NewFault();
        var gate = NewFault();
        var scope = new FakeScope().Fail(Point.Create, denial)
            .Fail(Point.CloseChild, child).Fail(Point.FinalSource, source)
            .Fail(Point.CloseSource, close);
        var attempt = RestrictedSourcePowerShellControl.Observe(new FakeFactory(scope));
        var error = Record.Exception(() => AcceptanceSourceControlOrchestration.Complete(
            attempt, new FakeOutput(), () => throw gate));
        AssertFailures([gate, denial, child, source, close], error);
        Assert.False(attempt.Observation.CleanupCompleted);
        Assert.False(attempt.Observation.IsConclusive);
        Assert.Equal(CleanupSequence, scope.Calls.TakeLast(3));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void A_denial_shape_outside_the_create_call_is_not_inconclusive(int point)
    {
        var denial = new AcceptanceSourceControlCreationDeniedException(1314);
        var scope = new FakeScope();
        if (point >= 0)
            scope.Fail((Point)point, denial);
        var factory = new FakeFactory(scope, point < 0 ? denial : null);
        var attempt = RestrictedSourcePowerShellControl.Observe(factory);
        Assert.Same(denial, attempt.Failure);
        Assert.False(attempt.Observation.IsConclusive);
        Assert.Equal(1, factory.Opens);
    }

    [Fact]
    public void Failure_to_open_is_preserved_without_an_adopted_scope_or_fallback()
    {
        var fault = NewFault();
        var scope = new FakeScope();
        var factory = new FakeFactory(scope, fault);
        var attempt = RestrictedSourcePowerShellControl.Observe(factory);
        Assert.Same(fault, attempt.Failure);
        Assert.Equal(1, factory.Opens);
        Assert.Empty(scope.Calls);
        Assert.False(scope.SourceOpened);
    }

    [Fact]
    public void Cancellation_before_open_acquires_nothing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var scope = new FakeScope();
        var factory = new FakeFactory(scope);
        var attempt = RestrictedSourcePowerShellControl.Observe(factory, cancellation.Token);
        Assert.Equal(0, factory.Opens);
        Assert.Empty(scope.Calls);
        var error = Assert.IsType<OperationCanceledException>(attempt.Failure);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1223, attempt.Observation.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Cancellation_after_acquisition_retains_the_held_source_through_cleanup(int value)
    {
        using var cancellation = new CancellationTokenSource();
        var scope = new FakeScope(onPoint: point =>
        {
            if (point == (Point)value)
                cancellation.Cancel();
        });
        var attempt = RestrictedSourcePowerShellControl.Observe(
            new FakeFactory(scope), cancellation.Token);
        var error = Assert.IsType<OperationCanceledException>(attempt.Failure);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(scope.IsClosed);
        Assert.Equal(CleanupSequence, scope.Calls.TakeLast(3));
        Assert.True(scope.Calls.Count(point => point == Point.Resume) <= 1);
    }

    [Fact]
    public void Evidence_getter_failures_do_not_replace_execution_or_cleanup_faults()
    {
        var execution = NewFault();
        var cleanup = NewFault();
        var evidence = NewFault();
        var state = NewFault();
        var gate = NewFault();
        var scope = new FakeScope(project: _ => throw evidence, stateFault: state)
            .Fail(Point.Wait, execution).Fail(Point.FinalSource, cleanup);
        var attempt = RestrictedSourcePowerShellControl.Observe(new FakeFactory(scope));
        var error = Record.Exception(() => AcceptanceSourceControlOrchestration.Complete(
            attempt, new FakeOutput(), () => throw gate));
        AssertFailures([gate, execution, cleanup, evidence, state], error);
        Assert.Equal(CleanupSequence, scope.Calls.TakeLast(3));
        Assert.True(scope.SourceClosed);
        Assert.False(attempt.Observation.IsConclusive);
    }

    [Fact]
    public void Unproved_resource_closure_is_unsafe_even_when_dispose_returns()
    {
        var scope = new FakeScope(resourcesClosed: false);
        var attempt = RestrictedSourcePowerShellControl.Observe(new FakeFactory(scope));
        Assert.IsType<InvalidOperationException>(attempt.Failure);
        Assert.False(attempt.Observation.CleanupCompleted);
        Assert.False(attempt.Observation.IsConclusive);
        Assert.Equal(CleanupSequence, scope.Calls.TakeLast(3));
    }

    [Fact]
    public void Existing_aggregate_identity_and_depth_first_leaf_order_are_preserved()
    {
        var first = NewFault();
        var second = NewFault();
        var gate = new AggregateException(first, second);
        var supplemental = NewFault();
        var observation = ObserveClean();
        var attempt = observation with { Failure = supplemental };
        var error = Assert.Throws<AggregateException>(() =>
            AcceptanceSourceControlOrchestration.Complete(
                attempt, new FakeOutput(), () => throw gate));
        Assert.Same(gate, error.InnerExceptions[0]);
        Assert.Same(supplemental, error.InnerExceptions[1]);
        AssertFailures([first, second, supplemental], error);
    }

    [Fact]
    public void Repeated_reporting_root_is_skipped_without_rewriting_existing_cleanup_graph()
    {
        var gate = NewFault();
        var shared = NewFault();
        var source = NewFault();
        var scope = new FakeScope().Fail(Point.Wait, shared)
            .Fail(Point.CloseChild, shared).Fail(Point.FinalSource, source);
        var attempt = RestrictedSourcePowerShellControl.Observe(new FakeFactory(scope));
        var error = Assert.Throws<AggregateException>(() =>
            AcceptanceSourceControlOrchestration.Complete(
                attempt, new FakeOutput(failure: shared), () => throw gate));
        var execution = Assert.IsType<AggregateException>(attempt.Failure);
        var cleanup = Assert.IsType<AggregateException>(execution.InnerExceptions[1]);
        Assert.Collection(error.InnerExceptions,
            first => Assert.Same(gate, first), second => Assert.Same(execution, second));
        Assert.Collection(execution.InnerExceptions,
            first => Assert.Same(shared, first), second => Assert.Same(cleanup, second));
        Assert.Collection(cleanup.InnerExceptions,
            first => Assert.Same(shared, first), second => Assert.Same(source, second));
        AssertFailures([gate, shared, shared, source], error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Original_aggregate_with_repetitions_is_thrown_unchanged(bool nested)
    {
        var shared = NewFault();
        Exception child = nested ? new AggregateException(shared, shared) : shared;
        var gate = new AggregateException(child, child);
        var error = Record.Exception(() =>
            AcceptanceSourceControlOrchestration.Complete(
                ObserveClean(), new FakeOutput(), () => throw gate));
        Assert.Same(gate, error);
        Assert.Same(gate, AcceptanceSourceControlOrchestration.Combine(
            null, gate, gate, shared));
        Assert.Collection(gate.InnerExceptions,
            first => Assert.Same(child, first), second => Assert.Same(child, second));
        AssertFailures(
            nested ? [shared, shared, shared, shared] : [shared, shared], error);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Ordinary_inner_ownership_skips_reachable_roots_and_retains_new_reporting(
        bool nested, bool newReporting)
    {
        var shared = NewFault();
        var inner = new InvalidOperationException("Synthetic inner failure.", shared);
        var gate = nested
            ? new InvalidOperationException("Synthetic outer failure.", inner) : inner;
        var reporting = newReporting ? NewFault() : shared;
        var attempt = ObserveClean() with { Failure = shared };
        var error = Record.Exception(() =>
            AcceptanceSourceControlOrchestration.Complete(
                attempt, new FakeOutput(failure: reporting), () => throw gate));
        AssertFailures(newReporting ? [gate, reporting] : [gate], error);
        Assert.Same(gate, AcceptanceSourceControlOrchestration.Combine(gate, shared, inner));
        Assert.Same(shared, inner.InnerException);
        Assert.Same(nested ? inner : shared, gate.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Genuine_shared_aggregate_graphs_keep_their_envelopes_and_repetitions(
        bool hasNewFault)
    {
        var shared = NewFault();
        var fresh = hasNewFault ? NewFault() : shared;
        var original = new AggregateException(shared, shared);
        var foreign = new AggregateException(shared, fresh, shared);
        var error = Assert.IsType<AggregateException>(
            AcceptanceSourceControlOrchestration.Combine(
                original, foreign, shared, fresh, foreign));
        Assert.Collection(error.InnerExceptions,
            first => Assert.Same(original, first), second => Assert.Same(foreign, second));
        Assert.Collection(foreign.InnerExceptions,
            first => Assert.Same(shared, first), second => Assert.Same(fresh, second),
            third => Assert.Same(shared, third));
        AssertFailures([shared, shared, shared, fresh, shared], error);
    }

    [Fact]
    public void Later_enclosing_source_graph_is_preserved_and_owns_reachable_reporting()
    {
        var original = NewFault();
        var reporting = NewFault();
        var nested = new AggregateException(original, reporting);
        var source = new InvalidOperationException("Synthetic source failure.", nested);
        var attempt = ObserveClean() with { Failure = source };
        var error = Assert.Throws<AggregateException>(() =>
            AcceptanceSourceControlOrchestration.Complete(
                attempt, new FakeOutput(failure: reporting), () => throw original));
        Assert.Collection(error.InnerExceptions,
            first => Assert.Same(original, first), second => Assert.Same(source, second));
        Assert.Same(nested, source.InnerException);
        Assert.Collection(nested.InnerExceptions,
            first => Assert.Same(original, first), second => Assert.Same(reporting, second));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    [InlineData(3, 8192)]
    [InlineData(4, 0)]
    [InlineData(5, 1)]
    [InlineData(6, 0)]
    [InlineData(7, 0)]
    public void Every_source_high_predicate_is_required(int predicate, int value)
    {
        AcceptanceSourceHighFacts.Required.Require();
        var facts = ChangeHigh(predicate, value);
        Assert.Equal(13, Assert.Throws<Win32Exception>(facts.Require).NativeErrorCode);
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
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public void Every_required_progress_and_io_predicate_fences_conclusive_status(int predicate)
    {
        var scope = new FakeScope(project: progress => ChangeProgress(progress, predicate));
        var attempt = RestrictedSourcePowerShellControl.Observe(new FakeFactory(scope));
        Assert.Equal((uint?)37u, attempt.Observation.ExitCode);
        Assert.False(attempt.Observation.IsConclusive);
        Assert.IsType<InvalidOperationException>(attempt.Failure);
    }

    [Fact]
    public void Row_is_separate_numeric_only_and_unobserved_zero_is_explicit()
    {
        var clean = ObserveClean().Observation;
        var denied = RestrictedSourcePowerShellControl.Observe(new FakeFactory(
            new FakeScope().Fail(Point.Create,
                new AcceptanceSourceControlCreationDeniedException(5)))).Observation;
        Assert.Equal(0x08080004u, AcceptanceSourceControlObservation.CreationFlags);
        Assert.Matches(
            "^HERMES_RESTRICTED_SOURCE_CONTROL v=1(?: [A-Za-z]+=[0-9]+)+$", clean.Row);
        Assert.Contains("role=1 ", clean.Row);
        Assert.Contains("state=1 ", clean.Row);
        Assert.Contains("closed=1 created=1 imageMatch=1 sourceBytes=8 ", clean.Row);
        Assert.Contains("state=0 ", denied.Row);
        Assert.Contains("exitObserved=0 exit=0 ", denied.Row);
        Assert.DoesNotContain("arm=", clean.Row);
        Assert.DoesNotContain('\r', clean.Row);
        Assert.DoesNotContain('\n', clean.Row);
    }

    [Fact]
    public void Nine_byte_crlf_source_evidence_is_also_conclusive()
    {
        var scope = new FakeScope(project: progress => progress with
        {
            Io = progress.Io with { SourceBytes = 9 }
        });
        var attempt = RestrictedSourcePowerShellControl.Observe(new FakeFactory(scope));
        Assert.True(attempt.Observation.IsConclusive);
        Assert.Null(attempt.Failure);
    }

    private sealed record Scenario(
        FakeScope Scope,
        Exception? Gate,
        Exception? Reporting,
        IReadOnlyList<Exception> Expected);

    private static Scenario CreateScenario(int mask)
    {
        var errors = Enumerable.Range(0, 6)
            .Select(index => (mask & (1 << index)) != 0 ? NewFault() : null).ToArray();
        var scope = new FakeScope().Fail(Point.Wait, errors[1])
            .Fail(Point.CloseChild, errors[2]).Fail(Point.FinalSource, errors[3])
            .Fail(Point.CloseSource, errors[4]);
        return new(scope, errors[0], errors[5], errors.OfType<Exception>().ToArray());
    }

    private static AcceptanceSourceControlAttempt ObserveClean() =>
        RestrictedSourcePowerShellControl.Observe(new FakeFactory(new FakeScope()));

    private static InvalidOperationException NewFault() =>
        new("Synthetic control failure.");

    private static FakeOutput ClosedOutput(FakeScope scope, Exception? failure = null) =>
        new(_ => Assert.True(scope.IsClosed), failure);

    private static IEnumerable<Exception> Leaves(Exception error) =>
        error is AggregateException aggregate
            ? aggregate.InnerExceptions.SelectMany(Leaves)
            : [error];

    private static void AssertFailures(
        IReadOnlyList<Exception> expected, Exception? error)
    {
        if (expected.Count == 0)
        {
            Assert.Null(error);
            return;
        }
        var actual = Assert.IsAssignableFrom<Exception>(error);
        var leaves = Leaves(actual).ToArray();
        Assert.Equal(expected.Count, leaves.Length);
        foreach (var pair in expected.Zip(leaves))
            Assert.Same(pair.First, pair.Second);
        if (expected.Count == 1)
            Assert.Same(expected[0], actual);
    }

    private static AcceptanceSourceHighFacts ChangeHigh(int predicate, int value) =>
        predicate switch
        {
            0 => AcceptanceSourceHighFacts.Required with { TokenType = value },
            1 => AcceptanceSourceHighFacts.Required with { ElevationType = value },
            2 => AcceptanceSourceHighFacts.Required with { Elevated = value },
            3 => AcceptanceSourceHighFacts.Required with { Integrity = value },
            4 => AcceptanceSourceHighFacts.Required with { AdminEnabled = value },
            5 => AcceptanceSourceHighFacts.Required with { AdminDenyOnly = value },
            6 => AcceptanceSourceHighFacts.Required with { SameUser = value },
            7 => AcceptanceSourceHighFacts.Required with { SameSession = value },
            _ => throw new ArgumentOutOfRangeException(nameof(predicate))
        };

    private static AcceptanceSourceControlProgress ChangeProgress(
        AcceptanceSourceControlProgress progress, int predicate) => predicate switch
    {
        0 => progress with { SourceHigh = false },
        1 => progress with { ChildHigh = false },
        2 => progress with { Resumed = false },
        _ => progress with { Io = ChangeIo(progress.Io, predicate - 3) }
    };

    private static AcceptanceSourceControlIo ChangeIo(
        AcceptanceSourceControlIo io, int predicate) => predicate switch
    {
        0 => io with { Created = false },
        1 => io with { ImageMatched = false },
        2 => io with { SourceBytes = 0 },
        3 => io with { StdinEof = false },
        4 => io with { PipesClosed = false },
        5 => io with { DrainsClosed = false },
        6 => io with { OutputBytes = -1 },
        7 => io with { ErrorBytes = -1 },
        8 => io with { OutputState = 0 },
        9 => io with { ErrorState = 0 },
        10 => io with { OutputError = 13 },
        11 => io with { ErrorError = 13 },
        _ => throw new ArgumentOutOfRangeException(nameof(predicate))
    };

    private sealed class FakeOutput(
        Action<string>? inspect = null, Exception? failure = null)
        : IAcceptanceSourceControlOutput
    {
        private int _writes;
        internal int Writes => _writes;

        public void WriteLine(string row)
        {
            _writes++;
            inspect?.Invoke(row);
            AcceptanceSourceControlOrchestration.Throw(failure);
        }
    }

    private sealed class FakeFactory(FakeScope scope, Exception? failure = null)
        : IAcceptanceSourceControlFactory
    {
        private int _opens;
        internal int Opens => _opens;

        public IAcceptanceSourceControlScope OpenSourceControl()
        {
            _opens++;
            AcceptanceSourceControlOrchestration.Throw(failure);
            scope.OpenSource();
            return scope;
        }
    }

    private sealed record Fault(Point Point, int Occurrence, Exception Error);

    private sealed class FakeScope : IAcceptanceSourceControlScope
    {
        private readonly uint _exit;
        private readonly Func<AcceptanceSourceControlProgress,
            AcceptanceSourceControlProgress> _project;
        private readonly Action<Point>? _onPoint;
        private readonly Exception? _stateFault;
        private readonly AcceptanceSourceControlLifetime _lifetime;
        private readonly List<Point> _calls = [];
        private readonly List<Fault> _faults = [];
        private AcceptanceSourceControlProgress _progress =
            AcceptanceSourceControlProgress.Empty;
        private bool _sourceOpened;
        private bool _sourceClosed;
        private bool _childClosed;

        internal FakeScope(
            uint exit = 37,
            Func<AcceptanceSourceControlProgress,
                AcceptanceSourceControlProgress>? project = null,
            Action<Point>? onPoint = null,
            Exception? stateFault = null,
            bool resourcesClosed = true)
        {
            _exit = exit;
            _project = project ?? (progress => progress);
            _onPoint = onPoint;
            _stateFault = stateFault;
            _lifetime = new(CloseChild, VerifyFinalSource, CloseSource,
                () => resourcesClosed && _childClosed && _sourceClosed);
        }

        internal IReadOnlyList<Point> Calls => _calls;
        internal bool SourceOpened => _sourceOpened;
        internal bool SourceClosed => _sourceClosed;
        internal bool ChildClosed => _childClosed;
        internal bool IsDisposed => _lifetime.IsDisposed;
        public bool IsClosed => _stateFault is { } error
            ? throw error : _lifetime.IsClosed;
        public AcceptanceSourceControlProgress Progress => _project(_progress);

        internal void OpenSource() => _sourceOpened = true;

        internal FakeScope Fail(Point point, Exception? error, int occurrence = 1)
        {
            if (error is { } fault)
                _faults.Add(new(point, occurrence, fault));
            return this;
        }

        public void VerifySourceHighSnapshot()
        {
            At(Point.Source);
            _progress = _progress with { SourceHigh = true };
        }

        public void CreateKillOnCloseJob() => At(Point.Job);

        public void CreateSuspended()
        {
            At(Point.Create);
            _progress = _progress with
            {
                Io = new(true, true, 8, true, false, false, 0, 0, 1, 1, 0, 0)
            };
        }

        public void AssignJob() => At(Point.Assign);

        public void VerifyChildHighSnapshotAndThreadAbsence()
        {
            At(Point.Child);
            _progress = _progress with { ChildHigh = true };
        }

        public void ReadObjectAccess() => At(Point.Inventory);

        public void Resume()
        {
            At(Point.Resume);
            _progress = _progress with { Resumed = true };
        }

        public uint Wait(CancellationToken cancellationToken)
        {
            At(Point.Wait);
            cancellationToken.ThrowIfCancellationRequested();
            return _exit;
        }

        public void Dispose() => _lifetime.Dispose();

        private void CloseChild()
        {
            At(Point.CloseChild);
            _childClosed = true;
            _progress = _progress with
            {
                Io = _progress.Io with { PipesClosed = true, DrainsClosed = true }
            };
        }

        private void VerifyFinalSource() => At(Point.FinalSource);

        private void CloseSource()
        {
            At(Point.CloseSource);
            _sourceClosed = true;
        }

        private void At(Point point)
        {
            Assert.True(_sourceOpened && !_sourceClosed);
            _calls.Add(point);
            _onPoint?.Invoke(point);
            var occurrence = _calls.Count(value => value == point);
            if (_faults.FirstOrDefault(fault =>
                fault.Point == point && fault.Occurrence == occurrence) is { } planned)
                throw planned.Error;
        }
    }
}
