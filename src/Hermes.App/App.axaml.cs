using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Hermes.App.ViewModels;
using Hermes.App.Views;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hermes.App;

public class App : Application
{
    private CancellationTokenSource? _serviceCts;
    private HermesServiceBridge? _bridge;
    private TrayIconViewModel? _trayViewModel;
    private TrayIcon? _trayIcon;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _bridge = new HermesServiceBridge();

            // Post async startup to UI thread message queue so we can await dialogs
            _ = Dispatcher.UIThread.InvokeAsync(() => StartupAsync(desktop));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartupAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_bridge!.IsFirstRun)
        {
            var wizard = new SetupWizard(_bridge);
            wizard.Show();

            // Wait for wizard to close
            var tcs = new TaskCompletionSource<bool>();
            wizard.Closed += (_, _) => tcs.SetResult(wizard.Completed);
            var completed = await tcs.Task;

            if (!completed)
            {
                desktop.Shutdown();
                return;
            }
        }

        // Start the background service
        _serviceCts = new CancellationTokenSource();
        _ = Task.Run(() => _bridge.StartAsync(_serviceCts.Token));

        // Configure tray icon
        _trayViewModel = new TrayIconViewModel(_bridge, desktop);

        WindowIcon? trayIconImage = null;
        try
        {
            var stream = typeof(App).Assembly.GetManifestResourceStream("hermes.ico");
            if (stream is not null)
                trayIconImage = new WindowIcon(stream);
        }
        catch { /* icon loading is best-effort */ }

        _trayIcon = new TrayIcon
        {
            ToolTipText = "Hermes — Document Intelligence",
            Menu = TrayMenuBuilder.Build(_trayViewModel),
            IsVisible = true
        };
        if (trayIconImage is not null)
            _trayIcon.Icon = trayIconImage;

        _trayIcon.Clicked += (_, _) => _trayViewModel.OpenShellWindow();

        _ = CheckForUpdateAsync(_trayViewModel);

        desktop.Exit += (_, _) =>
        {
            _serviceCts?.Cancel();
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
        };
    }

    private static async Task CheckForUpdateAsync(TrayIconViewModel trayVm)
    {
        var update = await UpdateChecker.CheckAsync();
        if (update is { IsUpdateAvailable: true })
        {
            trayVm.UpdateAvailable = update;
        }
    }
}
