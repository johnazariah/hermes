using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Hermes.App.ViewModels;
using Hermes.App.Views;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Hermes.App;

public class App : Application
{
    private CancellationTokenSource? _serviceCts;
    private HermesServiceBridge? _bridge;

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

            // Show first-run wizard if needed
            if (_bridge.IsFirstRun)
            {
                var wizard = new SetupWizard(_bridge);
                wizard.ShowDialog(null!);
                if (!wizard.Completed)
                {
                    desktop.Shutdown();
                    return;
                }
            }

            // Start the background service
            _serviceCts = new CancellationTokenSource();
            _ = Task.Run(() => _bridge.StartAsync(_serviceCts.Token));

            // Configure tray icon
            var trayViewModel = new TrayIconViewModel(_bridge, desktop);

            var trayIcon = new TrayIcon
            {
                ToolTipText = "Hermes — Document Intelligence",
                Menu = TrayMenuBuilder.Build(trayViewModel),
                IsVisible = true
            };

            trayIcon.Clicked += (_, _) => trayViewModel.OpenShellWindow();

            // Check for updates in the background
            _ = CheckForUpdateAsync(trayViewModel);

            desktop.Exit += (_, _) =>
            {
                _serviceCts?.Cancel();
                trayIcon.IsVisible = false;
                trayIcon.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
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
