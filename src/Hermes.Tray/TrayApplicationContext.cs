using System.Diagnostics;
using System.Drawing;

namespace Hermes.Tray;

internal sealed class TrayApplicationContext : ApplicationContext, IServiceObserver, IBrowserFailureNotification
{
    private readonly TrayOptions _options;
    private readonly IServiceLog _log;
    private readonly IExternalOpener _external;
    private readonly HttpHealthProbe _health = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly MainWindow _window;
    private readonly OwnedService _service;
    private readonly Icon _healthy = (Icon)SystemIcons.Information.Clone();
    private readonly Icon _unhealthy = (Icon)SystemIcons.Error.Clone();
    private readonly NotifyIcon _notify;
    private Task _worker = Task.CompletedTask;
    private Task _open = Task.CompletedTask;
    private Task? _quit;
    private bool _browserFallback;
    private bool _cleaned;

    public TrayApplicationContext(TrayOptions options)
    {
        _options = options;
        _log = new JsonServiceLog(options);
        _external = new GuardedExternalOpener(options, new ShellExternalLauncher(), _log, this);
        _window = new(options, _external);
        _service = new(options, new NativeProcessLauncher(_log), _health,
            new ServiceClock(), _log, this, LifecyclePolicy.Default);
        _notify = new NotifyIcon
        {
            Icon = _unhealthy, Text = "Hermes — Starting", Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _notify.DoubleClick += OnOpen;
        Application.Idle += Start;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Hermes", null, OnOpen);
        menu.Items.Add("View Logs", null, OnLogs);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Hermes", null, OnQuit);
        return menu;
    }

    private void Start(object? sender, EventArgs args)
    {
        Application.Idle -= Start;
        // Establish the UI synchronization context before starting the async worker.
        _ = _window.Handle;
        _worker = _service.RunAsync(_lifetime.Token);
        _open = OpenWhenReadyAsync();
    }

    public void Changed(ServiceSnapshot snapshot)
    {
        if (_cleaned)
            return;
        _notify.Icon = snapshot.Phase == ServicePhase.Running ? _healthy : _unhealthy;
        _notify.Text = $"Hermes — {snapshot.Phase}";
        if (snapshot.Phase == ServicePhase.Failed)
            _notify.ShowBalloonTip(5000, "Hermes service failed",
                $"{snapshot.Reason}. See {_options.LogDirectory}", ToolTipIcon.Error);
        if (snapshot.Phase == ServicePhase.Backoff)
            _notify.ShowBalloonTip(3000, "Hermes service restarting",
                $"Bounded restart {snapshot.Restart + 1} of 3.", ToolTipIcon.Warning);
    }

    private async Task OpenWhenReadyAsync()
    {
        try
        {
            await _service.Ready.WaitAsync(_lifetime.Token);
            await OpenWindowAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (LifecycleFault) { /* The lifecycle observer already displays the terminal failure. */ }
    }

    private void OnOpen(object? sender, EventArgs args)
    {
        if (_service.Snapshot.Phase != ServicePhase.Running)
        {
            _notify.ShowBalloonTip(3000, "Hermes", "The owned service is not ready. See View Logs.", ToolTipIcon.Warning);
            return;
        }
        if (_open.IsCompleted && !_lifetime.IsCancellationRequested)
            _open = OpenWindowAsync();
    }

    private async Task OpenWindowAsync()
    {
        try
        {
            if (_browserFallback)
                OpenBrowser();
            else
                await _window.OpenAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            _log.Write(new(EventCode.WindowFailed, Detail: SafeFailure.Describe(error)));
            _browserFallback = true;
            _window.Hide();
            _notify.ShowBalloonTip(5000, "Hermes",
                $"Native window unavailable; opening your browser. Details: {_options.LogDirectory}", ToolTipIcon.Warning);
            OpenBrowser();
        }
    }

    private void OpenBrowser() => _external.Open(_options.BaseAddress.AbsoluteUri);

    public void BrowserFailed() =>
        _notify.ShowBalloonTip(5000, "Hermes", "Could not open the browser. See View Logs.", ToolTipIcon.Error);

    private void OnLogs(object? sender, EventArgs args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(_options.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            _log.Write(new(EventCode.WindowFailed, Detail: SafeFailure.Describe(error)));
            _notify.ShowBalloonTip(5000, "Hermes", $"Logs: {_options.LogDirectory}", ToolTipIcon.Error);
        }
    }

    private void OnQuit(object? sender, EventArgs args) => _quit ??= QuitAsync();

    private async Task QuitAsync()
    {
        Application.Idle -= Start;
        _lifetime.Cancel();
        try { await Task.WhenAll(_worker, _open); }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            _log.Write(new(EventCode.UnexpectedFailure, Detail: SafeFailure.Describe(error)));
        }
        finally
        {
            if (_service.Snapshot.Reason == EventCode.StopFailed)
                Environment.ExitCode = 1;
            Cleanup();
            ExitThread();
        }
    }

    private void Cleanup()
    {
        if (_cleaned)
            return;
        _cleaned = true;
        _notify.Visible = false;
        _notify.ContextMenuStrip?.Dispose();
        _notify.Dispose();
        _window.Shutdown();
        _healthy.Dispose();
        _unhealthy.Dispose();
        _service.Dispose();
        _health.Dispose();
        _lifetime.Dispose();
    }
}
