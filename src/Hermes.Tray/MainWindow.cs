using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Hermes.Tray;

internal sealed class MainWindow : Form
{
    private readonly TrayOptions _options;
    private readonly BrowserNavigation _navigation;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private bool _initialized;
    private bool _shuttingDown;

    public MainWindow(TrayOptions options, IExternalOpener external)
    {
        _options = options;
        _navigation = new(options, external);
        Text = "Hermes";
        Size = new(1280, 800);
        MinimumSize = new(800, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_web);
    }

    public string RuntimeVersion => _web.CoreWebView2.Environment.BrowserVersionString;

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        Show();
        await InitializeAsync(cancellationToken);
        await NavigateAsync("/", cancellationToken);
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        BringToFront();
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;
        Directory.CreateDirectory(_options.WebViewDirectory);
        var environment = await CoreWebView2Environment.CreateAsync(
            userDataFolder: _options.WebViewDirectory).WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await _web.EnsureCoreWebView2Async(environment).WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _web.CoreWebView2.NavigationStarting += OnNavigationStarting;
        _web.CoreWebView2.NewWindowRequested += OnNewWindow;
        _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
        _initialized = true;
    }

    public async Task NavigateAsync(string path, CancellationToken cancellationToken)
    {
        var target = new Uri(_options.BaseAddress, path);
        if (!_navigation.IsCanonical(target))
            throw new ArgumentException("Native navigation must stay on the canonical loopback origin.");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs args) =>
            completion.TrySetResult(args.IsSuccess && _web.Source == target);
        _web.CoreWebView2.NavigationCompleted += Completed;
        try
        {
            _web.CoreWebView2.Navigate(target.AbsoluteUri);
            if (!await completion.Task.WaitAsync(cancellationToken))
                throw new InvalidOperationException("Canonical WebView2 navigation failed.");
        }
        finally { _web.CoreWebView2.NavigationCompleted -= Completed; }
    }

    public Task<string> ExecuteScriptAsync(string script, CancellationToken cancellationToken) =>
        _web.CoreWebView2.ExecuteScriptAsync(script).WaitAsync(cancellationToken);

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args) =>
        _navigation.NavigationStarting(args.Uri, () => args.Cancel = true);

    private void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs args) =>
        _navigation.NewWindowRequested(args.Uri, () => args.Handled = true);

    public void Shutdown()
    {
        _shuttingDown = true;
        Close();
        Dispose();
    }

    protected override void OnFormClosing(FormClosingEventArgs args)
    {
        if (!_shuttingDown && args.CloseReason == CloseReason.UserClosing)
        {
            args.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(args);
    }
}
