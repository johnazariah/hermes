using System.ComponentModel;
using System.Runtime.Versioning;
using Hermes.Installer.Control;
using Hermes.Servicing.Acceptance;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Hermes.Servicing.WindowsTests;

[SupportedOSPlatform("windows")]
[Collection(nameof(RestrictedOwnerNativeCollection))]
public sealed class RestrictedOwnerChildTests(ITestOutputHelper output)
{
    private static readonly ChildPoint[] CleanupPoints =
    [
        ChildPoint.CloseJob, ChildPoint.Stop, ChildPoint.Reap,
        ChildPoint.CloseThread, ChildPoint.CloseProcess
    ];

    [Fact]
    public void Owned_child_is_verified_before_one_resume_and_complete_cleanup()
    {
        var fake = new FakeChildren();
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        var result = RestrictedOwnerChild.RunInert(token, fake);
        Assert.Equal(RestrictedOwnerTokenFacts.Required, result.Facts);
        Assert.Equal(MsiContextRead.Absent, result.Thread);
        Assert.Equal(37u, result.ExitCode);
        Assert.True(result.CleanupCompleted && fake.Child.IsClosed);
        Assert.Equal(Enum.GetValues<ChildPoint>(), fake.Calls);
        Assert.Equal(1, fake.Resumes);
        fake.Child.Dispose();
        Assert.Equal(1, fake.Resumes);
        var calls = fake.Calls.Count;
        var error = Assert.Throws<Win32Exception>(() => fake.Child.Resume());
        Assert.Equal(6, error.NativeErrorCode);
        Assert.Equal(calls, fake.Calls.Count);
        Assert.False(token.IsClosed);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(1, -1)]
    [InlineData(2, 1)]
    [InlineData(2, -1)]
    [InlineData(3, 12288)]
    [InlineData(3, 4096)]
    [InlineData(3, -1)]
    [InlineData(4, 1)]
    [InlineData(4, -1)]
    [InlineData(5, 0)]
    [InlineData(5, -1)]
    [InlineData(6, 0)]
    [InlineData(6, -1)]
    [InlineData(7, 0)]
    [InlineData(7, -1)]
    public void Adverse_actual_child_facts_never_resume(int predicate, int value)
    {
        var fake = new FakeChildren(facts: Changed(predicate, value));
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        var error = Assert.Throws<RestrictedOwnerTokenVerificationException>(
            () => RestrictedOwnerChild.RunInert(token, fake));
        Assert.Equal(1 << (predicate - 1), error.FailedPredicates);
        Assert.Equal(0, fake.Resumes);
        AssertCompleteCleanup(fake);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Only_observed_child_thread_absence_allows_resume(int state)
    {
        var fake = new FakeChildren(thread: (MsiContextRead)state);
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        var error = Assert.Throws<RestrictedOwnerTokenVerificationException>(
            () => RestrictedOwnerChild.RunInert(token, fake));
        Assert.Equal(128, error.FailedPredicates);
        Assert.Equal(0, fake.Resumes);
        AssertCompleteCleanup(fake);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Capability_failures_have_no_launch_or_resume_fallback(int point)
    {
        var fake = new FakeChildren();
        var fault = new Win32Exception(1314, "Synthetic numeric failure.");
        fake.Fail((ChildPoint)point, fault);
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        var error = Assert.Throws<Win32Exception>(
            () => RestrictedOwnerChild.RunInert(token, fake));
        Assert.Same(fault, error);
        Assert.Equal(point >= 7 ? 1 : 0, fake.Resumes);
        Assert.Equal(point == 1 ? 0 : 5, CleanupCalls(fake).Count());
        if (point != 1)
            AssertCompleteCleanup(fake);
    }

    [Fact]
    public void Closed_token_is_refused_before_opening_a_child_scope()
    {
        var fake = new FakeChildren();
        var token = RestrictedOwnerToken.Create(fake.Tokens);
        token.Dispose();
        var error = Assert.Throws<Win32Exception>(
            () => RestrictedOwnerChild.RunInert(token, fake));
        Assert.Equal(6, error.NativeErrorCode);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void Token_closed_after_job_creation_is_refused_before_process_creation()
    {
        var fake = new FakeChildren();
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        fake.Observe(point =>
        {
            if (point == ChildPoint.Job)
                token.Dispose();
        });
        var error = Assert.Throws<Win32Exception>(
            () => RestrictedOwnerChild.RunInert(token, fake));
        Assert.Equal(6, error.NativeErrorCode);
        Assert.DoesNotContain(ChildPoint.Create, fake.Calls);
        Assert.Equal(0, fake.Resumes);
        AssertCompleteCleanup(fake);
    }

    [Fact]
    public void Final_parent_and_borrowed_token_reverification_precedes_resume()
    {
        var fake = new FakeChildren();
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        fake.Observe(point =>
        {
            if (point == ChildPoint.Thread)
                fake.Tokens.ChangeFacts(Changed(6, 0));
        });
        var error = Assert.Throws<RestrictedOwnerTokenVerificationException>(
            () => RestrictedOwnerChild.RunInert(token, fake));
        Assert.Equal(32, error.FailedPredicates);
        Assert.Equal(0, fake.Resumes);
        AssertCompleteCleanup(fake);
    }

    [Fact]
    public void Unexpected_programming_failure_is_preserved_after_cleanup()
    {
        var fake = new FakeChildren();
        var fault = new InvalidOperationException("Synthetic program fault.");
        fake.Fail(ChildPoint.Assign, fault);
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        var error = Assert.Throws<InvalidOperationException>(
            () => RestrictedOwnerChild.RunInert(token, fake));
        Assert.Same(fault, error);
        Assert.Equal(0, fake.Resumes);
        AssertCompleteCleanup(fake);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    public void Cancellation_cleans_up_every_acquired_scope(int point)
    {
        var fake = new FakeChildren();
        using var cancellation = new CancellationTokenSource();
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        fake.Observe(current =>
        {
            if ((int)current == point)
                cancellation.Cancel();
        });
        if (point == 0)
            cancellation.Cancel();
        var error = Assert.Throws<OperationCanceledException>(
            () => RestrictedOwnerChild.RunInert(token, fake, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(point >= 7 ? 1 : 0, fake.Resumes);
        Assert.Equal(point == 0 ? 0 : 5, CleanupCalls(fake).Count());
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void Each_cleanup_failure_is_visible_and_other_cleanup_is_attempted(int point)
    {
        var fake = new FakeChildren();
        var fault = new Win32Exception(123, "Synthetic numeric failure.");
        fake.Fail((ChildPoint)point, fault);
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        var error = Assert.Throws<AcceptanceChildCleanupException>(
            () => RestrictedOwnerChild.RunInert(token, fake));
        Assert.Same(fault, Assert.Single(error.InnerExceptions));
        Assert.Equal(CleanupPoints, CleanupCalls(fake));
        Assert.False(fake.Child.IsClosed);
        Assert.Equal(1, fake.Resumes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Original_and_cleanup_failures_are_both_preserved(bool cancellation)
    {
        var fake = new FakeChildren();
        Exception original = cancellation
            ? new OperationCanceledException()
            : new InvalidOperationException("Synthetic program fault.");
        var cleanup = new Win32Exception(123, "Synthetic numeric failure.");
        fake.Fail(ChildPoint.Wait, original);
        fake.Fail(ChildPoint.CloseJob, cleanup);
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        var error = Assert.Throws<AggregateException>(
            () => RestrictedOwnerChild.RunInert(token, fake));
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Same(original, error.InnerExceptions[0]);
        var incomplete = Assert.IsType<AcceptanceChildCleanupException>(error.InnerExceptions[1]);
        Assert.Same(cleanup, Assert.Single(incomplete.InnerExceptions));
        Assert.Equal(CleanupPoints, CleanupCalls(fake));
        Assert.False(fake.Child.IsClosed);
    }

    [Fact]
    public void Multiple_cleanup_failures_are_all_retained()
    {
        var fake = new FakeChildren();
        foreach (var point in CleanupPoints)
            fake.Fail(point, new Win32Exception(123, "Synthetic numeric failure."));
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        var error = Assert.Throws<AcceptanceChildCleanupException>(
            () => RestrictedOwnerChild.RunInert(token, fake));
        Assert.Equal(5, error.InnerExceptions.Count);
        Assert.Equal(CleanupPoints, CleanupCalls(fake));
        Assert.False(fake.Child.IsClosed);
    }

    [Fact]
    public void Actual_suspended_restricted_child_is_verified_then_exits_without_parent_changes()
    {
        try { RequireGenuineProof(); }
        catch (Exception error)
        {
            var leaves = Leaves(error).ToArray();
            var code = leaves.OfType<Win32Exception>()
                .Select(value => value.NativeErrorCode).FirstOrDefault(13);
            var failed = leaves.OfType<RestrictedOwnerTokenVerificationException>()
                .Aggregate(0, (mask, value) => mask | value.FailedPredicates);
            output.WriteLine(
                $"HERMES_RESTRICTED_CHILD_TEST v=1 completed=0 error={code} failed={failed} cleanupNotCompleted={(HasCleanupFailure(error) ? 1 : 0)}");
            throw;
        }
    }

    private void RequireGenuineProof()
    {
        var api = WindowsRestrictedOwnerTokens.Instance;
        api.RequireDisposableJob();
        var before = ReadParent();
        Assert.True(IsActualSource(before), "Required genuine source context absent.");
        var security = api.ReadParentSecurity();
        var children = new RecordingChildren(api);
        var proof = MeasureChild(api, children);
        var parent = before == ReadParent() &&
            security.SameFinalState(api.ReadParentSecurity());
        var closed = children.Child.IsClosed && ClosedUseRefused(children.Child);
        children.Child.Dispose();
        var verified = proof.Facts.FailedPredicates == 0 &&
            proof.Thread == MsiContextRead.Absent;
        output.WriteLine(
            $"HERMES_RESTRICTED_CHILD_TEST v=1 completed=1 observed={(verified ? 1 : 0)} failed={proof.Facts.FailedPredicates} resumed=1 exit={proof.ExitCode} parent={(parent ? 1 : 0)} disposed={(proof.CleanupCompleted ? 1 : 0)} closed={(closed ? 1 : 0)} error=0");
        Assert.True(verified && proof.ExitCode == 37 && parent &&
            proof.CleanupCompleted && closed, "Restricted inert-child proof incomplete.");
    }

    private static RestrictedOwnerChildResult MeasureChild(
        WindowsRestrictedOwnerTokens api, IAcceptanceChildFactory children)
    {
        using var token = RestrictedOwnerToken.Create(api);
        return RestrictedOwnerChild.RunInert(token, children);
    }

    private static bool ClosedUseRefused(IAcceptanceOwnedChild child)
    {
        var error = Assert.Throws<Win32Exception>(() => child.Resume());
        return error.NativeErrorCode == 6;
    }

    private static IEnumerable<Exception> Leaves(Exception error) => error switch
    {
        AggregateException aggregate => aggregate.InnerExceptions.SelectMany(Leaves),
        _ => [error]
    };

    private static bool HasCleanupFailure(Exception error) =>
        error is AcceptanceChildCleanupException ||
        error is AggregateException aggregate &&
        aggregate.InnerExceptions.Any(HasCleanupFailure);

    private static MsiCallContextObservation ReadParent() =>
        MsiCallContextObservation.Read(WindowsMsiCallContextTokens.Instance);

    private static bool IsActualSource(MsiCallContextObservation value) => value is
    {
        Process: MsiContextRead.Observed,
        Elevated: 1,
        ElevationType: 1,
        Integrity: 12288,
        Admin: 1,
        Thread: MsiContextRead.Absent,
        Linked: MsiContextRead.Absent,
        SameOwner: -1,
        LinkedElevated: -1
    };

    private static void AssertCompleteCleanup(FakeChildren fake)
    {
        Assert.Equal(CleanupPoints, CleanupCalls(fake));
        Assert.True(fake.Child.IsClosed);
    }

    private static IEnumerable<ChildPoint> CleanupCalls(FakeChildren fake) =>
        fake.Calls.Where(point => point >= ChildPoint.CloseJob);

    private static RestrictedOwnerTokenFacts Changed(int predicate, int value) =>
        predicate switch
        {
            1 => RestrictedOwnerTokenFacts.Required with { TokenType = value },
            2 => RestrictedOwnerTokenFacts.Required with { Elevated = value },
            3 => RestrictedOwnerTokenFacts.Required with { Integrity = value },
            4 => RestrictedOwnerTokenFacts.Required with { AdminEnabled = value },
            5 => RestrictedOwnerTokenFacts.Required with { AdminDenyOnly = value },
            6 => RestrictedOwnerTokenFacts.Required with { SameOwner = value },
            7 => RestrictedOwnerTokenFacts.Required with { SameSession = value },
            _ => throw new ArgumentOutOfRangeException(nameof(predicate))
        };

    private enum ChildPoint
    {
        Open = 1,
        Job,
        Create,
        Assign,
        Facts,
        Thread,
        Resume,
        Wait,
        CloseJob,
        Stop,
        Reap,
        CloseThread,
        CloseProcess
    }

    private sealed class RecordingChildren(IAcceptanceChildFactory native)
        : IAcceptanceChildFactory
    {
        private IAcceptanceOwnedChild? _child;
        internal IAcceptanceOwnedChild Child =>
            _child ?? throw new XunitException("Owned child scope absent.");

        public IAcceptanceOwnedChild Open()
        {
            var child = native.Open();
            _child = child;
            return child;
        }
    }

    private sealed class FakeChildren : IAcceptanceChildFactory
    {
        private readonly List<ChildPoint> _calls = [];
        private readonly Dictionary<ChildPoint, Exception> _faults = [];
        private readonly RestrictedOwnerTokenFacts _facts;
        private readonly MsiContextRead _thread;
        private Action<ChildPoint>? _observe;

        internal FakeChildren(
            RestrictedOwnerTokenFacts? facts = null,
            MsiContextRead thread = MsiContextRead.Absent)
        {
            _facts = facts ?? RestrictedOwnerTokenFacts.Required;
            _thread = thread;
            Child = new(this);
        }

        internal FakeTokens Tokens { get; } = new();
        internal FakeChild Child { get; }
        internal IReadOnlyList<ChildPoint> Calls => _calls;
        internal int Resumes => _calls.Count(point => point == ChildPoint.Resume);

        internal void Fail(ChildPoint point, Exception error) => _faults.Add(point, error);
        internal void Observe(Action<ChildPoint> observe) => _observe = observe;

        public IAcceptanceOwnedChild Open()
        {
            At(ChildPoint.Open);
            return Child;
        }

        private void At(ChildPoint point)
        {
            _calls.Add(point);
            _observe?.Invoke(point);
            if (_faults.TryGetValue(point, out var fault))
                throw fault;
        }

        internal sealed class FakeChild(FakeChildren owner) : IAcceptanceOwnedChild
        {
            private bool _disposed;
            private bool _completed;
            public bool IsClosed => _disposed && _completed;

            public void CreateKillOnCloseJob() => Call(ChildPoint.Job);

            public void CreateSuspended(IAcceptanceRestrictedToken token)
            {
                Call(ChildPoint.Create);
                Assert.Same(owner.Tokens.Candidate, token);
                Assert.False(token.IsClosed);
            }

            public void AssignJob() => Call(ChildPoint.Assign);

            public RestrictedOwnerTokenFacts ReadPrimaryFacts()
            {
                Call(ChildPoint.Facts);
                return owner._facts;
            }

            public MsiContextRead ReadThreadToken()
            {
                Call(ChildPoint.Thread);
                return owner._thread;
            }

            public void Resume() => Call(ChildPoint.Resume);

            public uint Wait(CancellationToken cancellationToken)
            {
                Call(ChildPoint.Wait);
                cancellationToken.ThrowIfCancellationRequested();
                return 37;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                AcceptanceChildCleanup.Complete(
                    () => owner.At(ChildPoint.CloseJob),
                    () => owner.At(ChildPoint.Stop),
                    () => owner.At(ChildPoint.Reap),
                    () => owner.At(ChildPoint.CloseThread),
                    () => owner.At(ChildPoint.CloseProcess));
                _completed = true;
            }

            private void Call(ChildPoint point)
            {
                if (_disposed)
                    throw RestrictedOwnerToken.Failure(6);
                owner.At(point);
            }
        }
    }

    private sealed class FakeSource : IAcceptanceSourceToken
    {
        private bool _closed;
        public bool IsClosed => _closed;
        public void Dispose() => _closed = true;
    }

    private sealed class FakeRestricted : IAcceptanceRestrictedToken
    {
        private bool _closed;
        public bool IsClosed => _closed;
        public void Dispose() => _closed = true;
    }

    private sealed class FakeTokens : IRestrictedOwnerTokens
    {
        private RestrictedOwnerTokenFacts _facts = RestrictedOwnerTokenFacts.Required;
        internal FakeRestricted Candidate { get; } = new();
        internal void ChangeFacts(RestrictedOwnerTokenFacts facts) => _facts = facts;

        public void RequireDisposableJob() { }
        public MsiContextRead ReadThreadToken() => MsiContextRead.Absent;
        public IAcceptanceSourceToken OpenCurrentPrimary() => new FakeSource();

        public IAcceptanceRestrictedToken Filter(
            IAcceptanceSourceToken source, AcceptanceTokenFilter flags) => Candidate;

        public void SetMediumIntegrity(IAcceptanceRestrictedToken token) =>
            Assert.Same(Candidate, token);

        public RestrictedOwnerTokenFacts ReadFacts(
            IAcceptanceRestrictedToken token, IAcceptanceSourceToken source)
        {
            Assert.Same(Candidate, token);
            Assert.False(token.IsClosed);
            Assert.False(source.IsClosed);
            return _facts;
        }
    }
}
