using Xunit;

namespace Hermes.Tray.Tests;

public sealed class NavigationRequestTests
{
    private static readonly Uri Target = new("http://127.0.0.1:31991/settings");
    private static bool Canonical(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == "http" && uri.Host == "127.0.0.1" && uri.Port == 31991;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Old_same_url_completion_cannot_settle_a_new_request(bool oldSuccess)
    {
        var driver = new TestNavigation();
        var pending = NavigationRequest.RunAsync(driver, Target, Canonical, CancellationToken.None);
        driver.Start(new(20, Target, false));
        driver.Finish(new(19, oldSuccess, Target));
        Assert.False(pending.IsCompleted);
        Assert.Equal((1, 1), driver.Subscriptions);
        driver.Finish(new(20, true, Target));
        await pending;
        Assert.Equal((0, 0), driver.Subscriptions);
        Assert.Equal([Target], driver.Targets);
    }

    [Fact]
    public async Task Completion_before_the_initiated_start_is_ignored()
    {
        var driver = new TestNavigation();
        var pending = NavigationRequest.RunAsync(driver, Target, Canonical, CancellationToken.None);
        driver.Finish(new(19, true, Target));
        driver.Start(new(19, Target, true));
        driver.Finish(new(19, false, Target));
        Assert.False(pending.IsCompleted);
        driver.Start(new(20, Target, false));
        driver.Finish(new(20, true, Target));
        await pending;
        Assert.Equal((0, 0), driver.Subscriptions);
    }

    [Fact]
    public async Task Current_same_id_canonical_redirect_completes_the_request()
    {
        var driver = new TestNavigation();
        var redirected = new Uri(Target, "/onboarding");
        var pending = NavigationRequest.RunAsync(driver, Target, Canonical, CancellationToken.None);
        driver.Start(new(20, Target, false));
        driver.Start(new(20, redirected, true));
        driver.Finish(new(19, true, Target));
        Assert.False(pending.IsCompleted);
        driver.Finish(new(20, true, redirected));
        await pending;
        Assert.Equal((0, 0), driver.Subscriptions);
    }

    [Fact]
    public async Task Current_failure_is_not_masked_by_old_success()
    {
        var driver = new TestNavigation();
        var pending = NavigationRequest.RunAsync(driver, Target, Canonical, CancellationToken.None);
        driver.Start(new(20, Target, false));
        driver.Finish(new(19, true, Target));
        Assert.False(pending.IsCompleted);
        driver.Finish(new(20, false, Target));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Equal("Canonical WebView2 navigation failed.", error.Message);
        Assert.Equal((0, 0), driver.Subscriptions);
    }

    [Fact]
    public async Task Successful_external_completion_is_still_failure()
    {
        var driver = new TestNavigation();
        var pending = NavigationRequest.RunAsync(driver, Target, Canonical, CancellationToken.None);
        driver.Start(new(20, Target, false));
        driver.Finish(new(20, true, new("https://example.invalid/")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Equal((0, 0), driver.Subscriptions);
    }

    [Fact]
    public async Task Cancellation_detaches_both_handlers_and_late_completion_is_inert()
    {
        var driver = new TestNavigation();
        using var cancellation = new CancellationTokenSource();
        var pending = NavigationRequest.RunAsync(driver, Target, Canonical, cancellation.Token);
        driver.Start(new(20, Target, false));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal((0, 0), driver.Subscriptions);
        driver.Finish(new(20, true, Target));
        Assert.True(pending.IsCanceled);
    }

    [Fact]
    public async Task Already_cancelled_request_never_initiates_navigation()
    {
        var driver = new TestNavigation();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NavigationRequest.RunAsync(driver, Target, Canonical, new CancellationToken(true)));
        Assert.Empty(driver.Targets);
        Assert.Equal((0, 0), driver.Subscriptions);
    }

    [Fact]
    public async Task Synchronous_navigation_failure_also_detaches_handlers()
    {
        var driver = new TestNavigation { FailNavigate = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NavigationRequest.RunAsync(driver, Target, Canonical, CancellationToken.None));
        Assert.Equal([Target], driver.Targets);
        Assert.Equal((0, 0), driver.Subscriptions);
    }

    [Fact]
    public async Task Noncanonical_target_is_rejected_without_subscriptions_or_navigation()
    {
        var driver = new TestNavigation();
        await Assert.ThrowsAsync<ArgumentException>(() => NavigationRequest.RunAsync(
            driver, new("https://example.invalid/"), Canonical, CancellationToken.None));
        Assert.Empty(driver.Targets);
        Assert.Equal((0, 0), driver.Subscriptions);
    }

    [Fact]
    public async Task Unrelated_start_cannot_bind_the_waiter()
    {
        var driver = new TestNavigation();
        var pending = NavigationRequest.RunAsync(driver, Target, Canonical, CancellationToken.None);
        driver.Start(new(19, new Uri(Target, "/documents"), false));
        driver.Finish(new(19, true, Target));
        Assert.False(pending.IsCompleted);
        driver.Start(new(20, Target, false));
        driver.Finish(new(20, true, Target));
        await pending;
        Assert.Equal((0, 0), driver.Subscriptions);
    }

    private sealed class TestNavigation : INavigationDriver
    {
        private Action<NavigationStart>? _started;
        private Action<NavigationFinish>? _finished;
        private readonly List<Uri> _targets = [];
        internal IReadOnlyList<Uri> Targets => _targets;
        internal bool FailNavigate { get; init; }
        internal (int Started, int Finished) Subscriptions =>
            (_started?.GetInvocationList().Length ?? 0, _finished?.GetInvocationList().Length ?? 0);

        public event Action<NavigationStart>? NavigationStarted
        {
            add => _started += value;
            remove => _started -= value;
        }

        public event Action<NavigationFinish>? NavigationFinished
        {
            add => _finished += value;
            remove => _finished -= value;
        }

        public void Navigate(Uri target)
        {
            _targets.Add(target);
            if (FailNavigate)
                throw new InvalidOperationException("Synthetic navigation failure.");
        }

        internal void Start(NavigationStart start) => _started?.Invoke(start);
        internal void Finish(NavigationFinish finish) => _finished?.Invoke(finish);
    }
}
