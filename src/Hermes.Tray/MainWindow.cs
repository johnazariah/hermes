using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Hermes.Tray;

/// <summary>
/// Native window hosting the Hermes UI via WebView2.
/// Closing hides to tray; actual disposal is driven by the tray app quit flow.
/// </summary>
internal sealed class MainWindow : Form
{
    private readonly WebView2 _webView;
    private Task? _initTask;
    private string? _pendingUrl;
    private bool _isShuttingDown;

    public MainWindow()
    {
        Text = "Hermes";
        Size = new System.Drawing.Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new System.Drawing.Size(800, 600);

        _webView = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_webView);
    }

    /// <summary>
    /// Initialise WebView2 with an explicit user-data folder under LOCALAPPDATA.
    /// Returns the cached init task so callers can await readiness.
    /// </summary>
    public Task EnsureInitializedAsync()
    {
        _initTask ??= InitCoreAsync();
        return _initTask;
    }

    private async Task InitCoreAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hermes", "webview2");
        Directory.CreateDirectory(userDataFolder);

        var env = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder);

        await _webView.EnsureCoreWebView2Async(env);

        // Open external links in the default browser
        _webView.CoreWebView2.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            Process.Start(new ProcessStartInfo
            {
                FileName = args.Uri,
                UseShellExecute = true,
            });
        };

        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;

        if (_pendingUrl is not null)
        {
            _webView.CoreWebView2.Navigate(_pendingUrl);
            _pendingUrl = null;
        }
    }

    /// <summary>
    /// Navigate to the given URL. If WebView2 is not yet initialised,
    /// the URL is queued and navigated once init completes.
    /// </summary>
    public void NavigateTo(string url)
    {
        if (_webView.CoreWebView2 is not null)
        {
            _webView.CoreWebView2.Navigate(url);
        }
        else
        {
            _pendingUrl = url;
        }
    }

    /// <summary>
    /// Call before disposing to allow the form to actually close.
    /// </summary>
    public void PrepareForShutdown() => _isShuttingDown = true;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_isShuttingDown && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _webView.Dispose();
        }

        base.Dispose(disposing);
    }
}
