namespace Hermes.Tray;

internal sealed record NavigationStart(ulong Id, Uri? Source, bool IsRedirect);
internal sealed record NavigationFinish(ulong Id, bool Success, Uri? Source);

internal interface INavigationDriver
{
    event Action<NavigationStart>? NavigationStarted;
    event Action<NavigationFinish>? NavigationFinished;
    void Navigate(Uri target);
}

internal static class NavigationRequest
{
    internal static async Task RunAsync(
        INavigationDriver driver, Uri target, Func<Uri, bool> canonical,
        CancellationToken cancellationToken)
    {
        if (!canonical(target))
            throw new ArgumentException("Native navigation must stay on the canonical loopback origin.");
        var pending = new PendingNavigation(target, canonical);
        driver.NavigationStarted += pending.Started;
        driver.NavigationFinished += pending.Finished;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            driver.Navigate(target);
            if (!await pending.Completion.WaitAsync(cancellationToken))
                throw new InvalidOperationException("Canonical WebView2 navigation failed.");
        }
        finally
        {
            driver.NavigationStarted -= pending.Started;
            driver.NavigationFinished -= pending.Finished;
        }
    }

    private sealed class PendingNavigation(Uri target, Func<Uri, bool> canonical)
    {
        private readonly TaskCompletionSource<bool> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ulong? _navigationId;
        internal Task<bool> Completion => _completion.Task;

        internal void Started(NavigationStart start)
        {
            if (_navigationId is null && !start.IsRedirect && start.Source == target)
                _navigationId = start.Id;
        }

        internal void Finished(NavigationFinish finish)
        {
            if (_navigationId is { } id && finish.Id == id)
                _completion.TrySetResult(
                    finish.Success && finish.Source is { } source && canonical(source));
        }
    }
}
