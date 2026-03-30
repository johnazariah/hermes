using Avalonia.Controls.ApplicationLifetimes;
using Hermes.App.Views;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hermes.App.ViewModels;

public sealed class TrayIconViewModel
{
    private readonly HermesServiceBridge _bridge;
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private ShellWindow? _shellWindow;

    public TrayIconViewModel(HermesServiceBridge bridge, IClassicDesktopStyleApplicationLifetime lifetime)
    {
        _bridge = bridge;
        _lifetime = lifetime;
    }

    public string StatusText => _bridge.StatusText;

    public UpdateChecker.UpdateInfo? UpdateAvailable { get; set; }

    public void OpenShellWindow()
    {
        if (_shellWindow is null || !_shellWindow.IsVisible)
        {
            _shellWindow = new ShellWindow(_bridge);
            _shellWindow.Show();
        }
        else
        {
            _shellWindow.Activate();
        }
    }

    public void OpenArchiveFolder()
    {
        var path = _bridge.ArchiveDir;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Process.Start("explorer.exe", path);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Process.Start("open", path);
    }

    public void OpenUpdatePage()
    {
        if (UpdateAvailable is null) return;
        var url = UpdateAvailable.DownloadUrl;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Process.Start("open", url);
    }

    public void TogglePause() => _bridge.TogglePause();
    public void RequestSync() => _bridge.RequestSync();

    public bool IsPaused => _bridge.IsPaused;

    public void Quit()
    {
        _shellWindow?.Close();
        _lifetime.Shutdown();
    }
}
