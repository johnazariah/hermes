using System.ComponentModel;
using System.Diagnostics;
using Hermes.Servicing.Acceptance;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed class OwnedChildStartupTests
{
    [Fact]
    public void A_loaded_module_does_not_add_a_fixed_startup_delay()
    {
        var clock = new Clock();
        var started = clock.GetTimestamp();
        var image = new Image(clock, _ => true);
        OwnedChildStartup.RequireImage(image, clock);
        Assert.Equal(1, image.Reads);
        Assert.Empty(image.Waits);
        Assert.Equal(TimeSpan.Zero, clock.GetElapsedTime(started));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    public void Null_modules_are_observed_again_until_loading_completes(int emptyReads)
    {
        var clock = new Clock();
        var image = new Image(clock, reads => reads > emptyReads);
        OwnedChildStartup.RequireImage(image, clock);
        Assert.Equal(emptyReads + 1, image.Reads);
        Assert.Equal(Enumerable.Repeat(10, emptyReads), image.Waits);
    }

    [Fact]
    public void A_live_child_without_a_module_has_a_finite_monotonic_deadline()
    {
        var clock = new Clock();
        var started = clock.GetTimestamp();
        var image = new Image(clock, _ => false);
        Assert.Throws<TimeoutException>(() => OwnedChildStartup.RequireImage(image, clock));
        Assert.Equal(TimeSpan.FromSeconds(5), clock.GetElapsedTime(started));
        Assert.Equal(500, image.Reads);
        Assert.Equal(Enumerable.Repeat(10, 500), image.Waits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Query_time_counts_and_a_late_ready_result_is_not_success(bool ready)
    {
        var clock = new Clock();
        var image = new Image(clock, _ =>
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            return ready;
        });
        Assert.Throws<TimeoutException>(() => OwnedChildStartup.RequireImage(image, clock));
        Assert.Equal(1, image.Reads);
        Assert.Empty(image.Waits);
    }

    [Theory]
    [InlineData(4997, 3)]
    [InlineData(4999, 1)]
    public void The_last_wait_is_clipped_to_the_remaining_budget(
        int queryMilliseconds, int waitMilliseconds)
    {
        var clock = new Clock();
        var image = new Image(clock, _ =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(queryMilliseconds));
            return false;
        });
        Assert.Throws<TimeoutException>(() => OwnedChildStartup.RequireImage(image, clock));
        Assert.Equal(1, image.Reads);
        Assert.Equal(waitMilliseconds, Assert.Single(image.Waits));
    }

    [Fact]
    public void An_already_exited_child_is_not_queried_or_waited()
    {
        var clock = new Clock();
        var image = new Image(clock, _ => true) { Exited = () => true };
        Assert.Throws<InvalidOperationException>(
            () => OwnedChildStartup.RequireImage(image, clock));
        Assert.Equal(0, image.Reads);
        Assert.Empty(image.Waits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exit_during_a_module_query_is_never_readiness(bool ready)
    {
        var clock = new Clock();
        var exited = false;
        var image = new Image(clock, _ =>
        {
            exited = true;
            return ready;
        }) { Exited = () => exited };
        Assert.Throws<InvalidOperationException>(
            () => OwnedChildStartup.RequireImage(image, clock));
        Assert.Equal(1, image.Reads);
        Assert.Empty(image.Waits);
    }

    [Fact]
    public void A_signaled_exit_wait_is_failure_without_another_property_query()
    {
        var clock = new Clock();
        var image = new Image(clock, _ => false) { Waited = () => true };
        Assert.Throws<InvalidOperationException>(
            () => OwnedChildStartup.RequireImage(image, clock));
        Assert.Equal(1, image.Reads);
        Assert.Equal(10, Assert.Single(image.Waits));
    }

    public enum QueryPoint { Exit, Module, Wait }

    [Theory]
    [InlineData(QueryPoint.Exit, 5)]
    [InlineData(QueryPoint.Exit, 6)]
    [InlineData(QueryPoint.Exit, 299)]
    [InlineData(QueryPoint.Module, 5)]
    [InlineData(QueryPoint.Module, 6)]
    [InlineData(QueryPoint.Module, 299)]
    [InlineData(QueryPoint.Wait, 5)]
    [InlineData(QueryPoint.Wait, 6)]
    [InlineData(QueryPoint.Wait, 299)]
    public void Native_query_failures_are_not_retried_or_reclassified(
        QueryPoint boundary, int code)
    {
        var clock = new Clock();
        var expected = new Win32Exception(code);
        var image = new Image(clock, _ =>
            boundary == QueryPoint.Module ? throw expected : false)
        {
            Exited = () => boundary == QueryPoint.Exit ? throw expected : false,
            Waited = () => boundary == QueryPoint.Wait ? throw expected : false
        };
        Assert.Same(expected, Assert.Throws<Win32Exception>(
            () => OwnedChildStartup.RequireImage(image, clock)));
        Assert.Equal(boundary == QueryPoint.Exit ? 0 : 1, image.Reads);
        Assert.Equal(Enumerable.Repeat(10, boundary == QueryPoint.Wait ? 1 : 0),
            image.Waits);
    }

    [Fact]
    public void An_invalid_process_exception_after_null_is_not_another_loading_state()
    {
        var clock = new Clock();
        var expected = new InvalidOperationException("Synthetic exited process.");
        var image = new Image(clock, reads => reads == 1 ? false : throw expected);
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(
            () => OwnedChildStartup.RequireImage(image, clock)));
        Assert.Equal(2, image.Reads);
        Assert.Equal(10, Assert.Single(image.Waits));
    }

    [Fact]
    public void No_process_association_is_an_error_at_each_real_adapter_boundary()
    {
        using var process = new Process();
        var image = new StartedChildImage(process);
        Assert.Throws<InvalidOperationException>(() => { _ = image.HasExited; });
        Assert.Throws<InvalidOperationException>(() => { _ = image.HasMainModule; });
        Assert.Throws<InvalidOperationException>(() => image.WaitForExit(1));
        Assert.Throws<InvalidOperationException>(image.Kill);
        image.Dispose();
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks = TimeSpan.TicksPerDay;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }

    private sealed class Image(Clock clock, Func<int, bool> read) : IOwnedChildImage
    {
        private int _reads;
        private readonly List<int> _waits = [];

        internal int Reads => _reads;
        internal IReadOnlyList<int> Waits => _waits;
        internal Func<bool> Exited { get; init; } = static () => false;
        internal Func<bool> Waited { get; init; } = static () => false;

        public bool HasExited => Exited();
        public bool HasMainModule => read(++_reads);

        public bool WaitForExit(int milliseconds)
        {
            _waits.Add(milliseconds);
            clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
            return Waited();
        }
    }
}
