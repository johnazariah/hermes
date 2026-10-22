using System.ComponentModel;
using System.Diagnostics;
using Hermes.Servicing.Acceptance;
using Xunit;

namespace Hermes.Setup.Tests;

// Source-linked production Bind controls, not Windows process-identity proof.
public sealed class OwnedChildBindingTests
{
    public enum FailurePoint { Exit, Module, Poll, Constructor }
    public enum CleanupPoint { Kill, Wait, Dispose, All }
    public enum DeadlinePoint { Kill, Wait, Dispose }
    private enum Step { Identity, Kill, Wait, Dispose }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Successful_binding_returns_the_factory_result_without_cleanup(bool capture)
    {
        var child = new Child(capture);
        Assert.Same(child.Owner, Bind(child));
        Assert.Equal<Step>([Step.Identity], child.Events);
        Assert.Empty(child.Polls);
        Assert.Empty(child.CleanupWaits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Null_module_timeout_cleans_up_before_any_identity_factory(bool capture)
    {
        var child = new Child(capture) { Image = static () => false };
        Assert.Throws<TimeoutException>(() => Bind(child));
        Assert.DoesNotContain(Step.Identity, child.Events);
        Assert.Equal(500, child.Polls.Count);
        Assert.Equal(5000, Assert.Single(child.CleanupWaits));
        AssertCleanup(child);
    }

    [Theory]
    [InlineData(false, FailurePoint.Exit)]
    [InlineData(true, FailurePoint.Exit)]
    [InlineData(false, FailurePoint.Module)]
    [InlineData(true, FailurePoint.Module)]
    [InlineData(false, FailurePoint.Poll)]
    [InlineData(true, FailurePoint.Poll)]
    [InlineData(false, FailurePoint.Constructor)]
    [InlineData(true, FailurePoint.Constructor)]
    public void Gate_and_constructor_exceptions_survive_complete_cleanup(
        bool capture, FailurePoint point)
    {
        var expected = new Win32Exception(5);
        var child = FaultingChild(capture, point, expected);
        Assert.Same(expected, Assert.Throws<Win32Exception>(() => Bind(child)));
        Assert.Equal(point == FailurePoint.Constructor,
            child.Events.Contains(Step.Identity));
        AssertCleanup(child);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Only_a_positively_reaped_exit_race_can_clear_a_known_kill_error(
        bool capture, bool nativeError)
    {
        Exception race = nativeError
            ? new Win32Exception(5) : new InvalidOperationException();
        var child = new Child(capture)
        {
            Exited = static () => true,
            Stop = () => throw race
        };
        var failure = Assert.Throws<InvalidOperationException>(() => Bind(child));
        Assert.NotSame(race, failure);
        Assert.DoesNotContain(Step.Identity, child.Events);
        AssertCleanup(child);
    }

    [Theory]
    [InlineData(false, CleanupPoint.Kill)]
    [InlineData(true, CleanupPoint.Kill)]
    [InlineData(false, CleanupPoint.Wait)]
    [InlineData(true, CleanupPoint.Wait)]
    [InlineData(false, CleanupPoint.Dispose)]
    [InlineData(true, CleanupPoint.Dispose)]
    [InlineData(false, CleanupPoint.All)]
    [InlineData(true, CleanupPoint.All)]
    public void Cleanup_faults_are_aggregated_without_skipping_later_steps(
        bool capture, CleanupPoint point)
    {
        var errors = new Errors(new Win32Exception(299), new IOException("kill"),
            new Win32Exception(6), new IOException("dispose"));
        var child = FaultyCleanupChild(capture, point, errors);
        IReadOnlyList<Exception> cleanup = point switch
        {
            CleanupPoint.Kill => [errors.Kill],
            CleanupPoint.Wait => [errors.Wait],
            CleanupPoint.Dispose => [errors.Dispose],
            _ => [errors.Kill, errors.Wait, errors.Dispose]
        };
        Assert.Equal<Exception>([errors.Original, .. cleanup],
            CleanupFailure(child, errors.Original).InnerExceptions);
        Assert.DoesNotContain(Step.Identity, child.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_unsignaled_wait_retains_the_kill_error_and_disposes(bool capture)
    {
        var original = new InvalidDataException();
        var kill = new Win32Exception(5);
        var clock = new Clock();
        var child = new Child(capture, clock)
        {
            ConstructionFailure = original,
            Stop = () => throw kill,
            Reaped = milliseconds =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
                return false;
            }
        };
        var causes = CleanupFailure(child, original).InnerExceptions;
        Assert.Equal(3, causes.Count);
        Assert.Same(kill, causes[1]);
        Assert.IsType<TimeoutException>(causes[2]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Kill_time_is_deducted_from_the_finite_reap_wait(bool capture)
    {
        var original = new InvalidDataException();
        var clock = new Clock();
        var child = new Child(capture, clock)
        {
            ConstructionFailure = original,
            Stop = () => clock.Advance(TimeSpan.FromMilliseconds(4997))
        };
        Assert.Same(original, Assert.Throws<InvalidDataException>(() => Bind(child)));
        Assert.Equal(3, Assert.Single(child.CleanupWaits));
        AssertCleanup(child);
    }

    [Theory]
    [InlineData(false, DeadlinePoint.Kill)]
    [InlineData(true, DeadlinePoint.Kill)]
    [InlineData(false, DeadlinePoint.Wait)]
    [InlineData(true, DeadlinePoint.Wait)]
    [InlineData(false, DeadlinePoint.Dispose)]
    [InlineData(true, DeadlinePoint.Dispose)]
    public void Cleanup_cannot_succeed_with_an_exhausted_budget_or_late_result(
        bool capture, DeadlinePoint point)
    {
        var original = new InvalidDataException();
        var clock = new Clock();
        var child = OverrunningChild(capture, point, clock, original);
        var causes = CleanupFailure(child, original).InnerExceptions;
        Assert.Equal(2, causes.Count);
        Assert.IsType<TimeoutException>(causes[1]);
        Assert.Equal(point == DeadlinePoint.Kill ? 0 : 5000,
            Assert.Single(child.CleanupWaits));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_late_ready_image_never_reaches_the_identity_factory(bool capture)
    {
        var clock = new Clock();
        var child = new Child(capture, clock)
        {
            Image = () =>
            {
                clock.Advance(TimeSpan.FromSeconds(5));
                return true;
            }
        };
        Assert.Throws<TimeoutException>(() => Bind(child));
        Assert.DoesNotContain(Step.Identity, child.Events);
        AssertCleanup(child);
    }

    private static object Bind(Child child)
    {
        Assert.Equal(child.CaptureStandardError,
            child.StartInfo.RedirectStandardError);
        Assert.True(child.StartInfo.RedirectStandardInput);
        Assert.True(child.StartInfo.RedirectStandardOutput);
        Assert.False(child.StartInfo.UseShellExecute);
        return OwnedChild.Bind(child, child.Clock, child.Construct);
    }

    private static void AssertCleanup(Child child)
    {
        Assert.Equal<Step>([Step.Kill, Step.Wait, Step.Dispose],
            child.Events.Where(step => step != Step.Identity));
        Assert.Single(child.CleanupWaits);
    }

    private static AggregateException CleanupFailure(Child child, Exception original)
    {
        var failure = Assert.Throws<SetupChildException>(() => Bind(child));
        Assert.Equal(SetupChildFault.CleanupFailed, failure.Fault);
        var aggregate = Assert.IsType<AggregateException>(failure.InnerException);
        Assert.Same(original, aggregate.InnerExceptions[0]);
        AssertCleanup(child);
        return aggregate;
    }

    private static Child FaultingChild(
        bool capture, FailurePoint point, Exception failure) =>
        new(capture)
        {
            Exited = () => point == FailurePoint.Exit ? throw failure : false,
            Image = () => point switch
            {
                FailurePoint.Module => throw failure,
                FailurePoint.Poll => false,
                _ => true
            },
            Polled = () => throw failure,
            ConstructionFailure = point == FailurePoint.Constructor ? failure : null
        };

    private static Child FaultyCleanupChild(
        bool capture, CleanupPoint point, Errors errors) =>
        new(capture)
        {
            Image = () => throw errors.Original,
            Stop = () =>
            {
                if (point is CleanupPoint.Kill or CleanupPoint.All)
                    throw errors.Kill;
            },
            Reaped = _ => point is CleanupPoint.Wait or CleanupPoint.All
                ? throw errors.Wait : true,
            Close = () =>
            {
                if (point is CleanupPoint.Dispose or CleanupPoint.All)
                    throw errors.Dispose;
            }
        };

    private static Child OverrunningChild(
        bool capture, DeadlinePoint point, Clock clock, Exception original) =>
        new(capture, clock)
        {
            ConstructionFailure = original,
            Stop = () => clock.Advance(point == DeadlinePoint.Kill
                ? TimeSpan.FromSeconds(5) : TimeSpan.Zero),
            Reaped = _ =>
            {
                clock.Advance(point == DeadlinePoint.Wait
                    ? TimeSpan.FromSeconds(5) : TimeSpan.Zero);
                return true;
            },
            Close = () => clock.Advance(point == DeadlinePoint.Dispose
                ? TimeSpan.FromSeconds(5) : TimeSpan.Zero)
        };

    private sealed record Errors(
        Exception Original, Exception Kill, Exception Wait, Exception Dispose);

    private sealed class Clock : TimeProvider
    {
        private long _ticks = TimeSpan.TicksPerDay;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }

    private sealed class Child(bool captureStandardError, Clock? clock = null)
        : IOwnedChildBinding
    {
        private bool _cleanup;
        private readonly List<Step> _events = [];
        private readonly List<int> _polls = [];
        private readonly List<int> _cleanupWaits = [];

        internal bool CaptureStandardError => captureStandardError;
        internal Clock Clock { get; } = clock ?? new();
        internal object Owner { get; } = new();
        internal ProcessStartInfo StartInfo { get; } = OwnedChild.CreateStartInfo(
            Path.Combine(Path.GetTempPath(), "owned.exe"), ["literal"],
            Path.GetTempPath(), captureStandardError);
        internal IReadOnlyList<Step> Events => _events;
        internal IReadOnlyList<int> Polls => _polls;
        internal IReadOnlyList<int> CleanupWaits => _cleanupWaits;
        internal Func<bool> Exited { get; init; } = static () => false;
        internal Func<bool> Image { get; init; } = static () => true;
        internal Func<bool> Polled { get; init; } = static () => false;
        internal Action Stop { get; init; } = static () => { };
        internal Func<int, bool> Reaped { get; init; } = static _ => true;
        internal Action Close { get; init; } = static () => { };
        internal Exception? ConstructionFailure { get; init; }

        public bool HasExited => Exited();
        public bool HasMainModule => Image();

        internal object Construct()
        {
            _events.Add(Step.Identity);
            if (ConstructionFailure is { } failure)
                throw failure;
            return Owner;
        }

        public bool WaitForExit(int milliseconds)
        {
            if (_cleanup)
            {
                _events.Add(Step.Wait);
                _cleanupWaits.Add(milliseconds);
                return Reaped(milliseconds);
            }
            _polls.Add(milliseconds);
            Clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
            return Polled();
        }

        public void Kill()
        {
            _cleanup = true;
            _events.Add(Step.Kill);
            Stop();
        }

        public void Dispose()
        {
            _events.Add(Step.Dispose);
            Close();
        }
    }
}
