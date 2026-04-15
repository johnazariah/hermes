using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Http;
using System.Windows.Forms;

namespace Hermes.Tray;

/// <summary>
/// System tray application that manages the Hermes service lifecycle
/// and provides a WebView2-based UI window.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string ServiceUrl = "http://localhost:21741";
    private const int HealthCheckIntervalMs = 5_000;

    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _healthTimer;
    private readonly HttpClient _httpClient;
    private Process? _serviceProcess;
    private bool _windowOpened;
    private bool _disposed;
    private MainWindow? _mainWindow;
    private bool _webViewAvailable = true;

    private readonly Icon _iconHealthy;
    private readonly Icon _iconUnhealthy;

    public TrayApplicationContext()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        _iconHealthy = CreateStatusIcon(Color.FromArgb(34, 139, 34));   // forest green
        _iconUnhealthy = CreateStatusIcon(Color.FromArgb(220, 20, 60)); // crimson

        _notifyIcon = new NotifyIcon
        {
            Icon = _iconUnhealthy,
            Text = "Hermes — Starting\u2026",
            Visible = true,
            ContextMenuStrip = BuildContextMenu(),
        };
        _notifyIcon.DoubleClick += OnOpenHermes;

        _healthTimer = new System.Windows.Forms.Timer { Interval = HealthCheckIntervalMs };
        _healthTimer.Tick += OnHealthCheck;

        StartServiceProcess();
        _healthTimer.Start();
    }

    // ── Context menu ────────────────────────────────────────────────

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Hermes", null, OnOpenHermes);
        menu.Items.Add("View Logs", null, OnViewLogs);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Hermes", null, OnQuit);
        return menu;
    }

    // ── Service process management ──────────────────────────────────

    private void StartServiceProcess()
    {
        var exeDir = AppContext.BaseDirectory;

        // Published layout: service/Hermes.Service.exe beside the tray app
        var serviceExe = Path.Combine(exeDir, "service", "Hermes.Service.exe");

        // Source layout fallback: navigate up from bin to src/Hermes.Service
        var sourceProject = Path.GetFullPath(
            Path.Combine(exeDir, "..", "..", "..", "..", "Hermes.Service", "Hermes.Service.fsproj"));

        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "hermes");
        var logDir = Path.Combine(configDir, "logs");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(logDir);

        ProcessStartInfo psi;

        if (File.Exists(serviceExe))
        {
            psi = new ProcessStartInfo
            {
                FileName = serviceExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
        }
        else if (File.Exists(sourceProject))
        {
            psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{sourceProject}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        }
        else
        {
            _notifyIcon.ShowBalloonTip(
                5_000,
                "Hermes",
                $"Service not found:\n{serviceExe}\n{sourceProject}",
                ToolTipIcon.Error);
            return;
        }

        psi.Environment["HERMES_CONFIG_DIR"] = configDir;

        try
        {
            _serviceProcess = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _notifyIcon.ShowBalloonTip(
                5_000,
                "Hermes",
                $"Failed to start service: {ex.Message}",
                ToolTipIcon.Error);
        }
    }

    private void StopServiceProcess()
    {
        if (_serviceProcess is null || _serviceProcess.HasExited)
            return;

        try
        {
            if (!_serviceProcess.CloseMainWindow())
                _serviceProcess.Kill(entireProcessTree: true);

            _serviceProcess.WaitForExit(5_000);
        }
        catch
        {
            try { _serviceProcess.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        }
    }

    // ── Health check ────────────────────────────────────────────────

    private async void OnHealthCheck(object? sender, EventArgs e)
    {
        bool healthy;
        try
        {
            using var response = await _httpClient.GetAsync($"{ServiceUrl}/health");
            healthy = response.IsSuccessStatusCode;
        }
        catch
        {
            healthy = false;
        }

        if (healthy)
        {
            _notifyIcon.Icon = _iconHealthy;
            _notifyIcon.Text = "Hermes — Running";

            if (!_windowOpened)
            {
                _windowOpened = true;
                ShowMainWindow();
            }
        }
        else
        {
            _notifyIcon.Icon = _iconUnhealthy;
            _notifyIcon.Text = "Hermes — Not responding";
        }
    }

    // ── Menu handlers ───────────────────────────────────────────────

    private void OnOpenHermes(object? sender, EventArgs e)
        => ShowMainWindow();

    private void OnViewLogs(object? sender, EventArgs e)
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "hermes", "logs");
        Directory.CreateDirectory(logDir);

        Process.Start(new ProcessStartInfo
        {
            FileName = logDir,
            UseShellExecute = true,
        });
    }

    private void OnQuit(object? sender, EventArgs e)
    {
        _healthTimer.Stop();
        StopServiceProcess();
        _notifyIcon.Visible = false;

        if (_mainWindow is not null)
        {
            _mainWindow.PrepareForShutdown();
            _mainWindow.Close();
        }

        Application.Exit();
    }

    // ── Window management ───────────────────────────────────────────

    private async void ShowMainWindow()
    {
        if (!_webViewAvailable)
        {
            OpenFallbackBrowser(ServiceUrl);
            return;
        }

        try
        {
            if (_mainWindow is null || _mainWindow.IsDisposed)
            {
                _mainWindow = new MainWindow();
                _mainWindow.NavigateTo(ServiceUrl);
                await _mainWindow.EnsureInitializedAsync();
            }

            _mainWindow.Show();
            _mainWindow.BringToFront();

            if (_mainWindow.WindowState == FormWindowState.Minimized)
                _mainWindow.WindowState = FormWindowState.Normal;
        }
        catch (Exception ex) when (
            ex.Message.Contains("WebView2", StringComparison.OrdinalIgnoreCase) ||
            ex is System.ComponentModel.Win32Exception ||
            ex is FileNotFoundException)
        {
            _webViewAvailable = false;
            _notifyIcon.ShowBalloonTip(
                5_000,
                "Hermes",
                "WebView2 runtime not found \u2014 opening in default browser.",
                ToolTipIcon.Warning);
            OpenFallbackBrowser(ServiceUrl);
        }
        catch (Exception ex)
        {
            _webViewAvailable = false;
            _notifyIcon.ShowBalloonTip(
                5_000,
                "Hermes",
                $"Could not open window: {ex.Message}\nFalling back to browser.",
                ToolTipIcon.Warning);
            OpenFallbackBrowser(ServiceUrl);
        }
    }

    private static void OpenFallbackBrowser(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// Generates a 16x16 icon with a filled circle in the given colour,
    /// used to indicate service health status in the system tray.
    /// </summary>
    private static Icon CreateStatusIcon(Color color)
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 1, 1, 14, 14);
        return Icon.FromHandle(bmp.GetHicon());
    }

    // ── Dispose ─────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            _healthTimer.Dispose();
            StopServiceProcess();
            _serviceProcess?.Dispose();
            _notifyIcon.Dispose();
            _httpClient.Dispose();
            _iconHealthy.Dispose();
            _iconUnhealthy.Dispose();

            if (_mainWindow is not null)
            {
                _mainWindow.PrepareForShutdown();
                _mainWindow.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
