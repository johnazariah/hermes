using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Hermes.Tray.Access;

namespace Hermes.Tray;

internal sealed class MainWindow : Form, INativeAccessSurface, INavigationDriver
{
    private readonly TrayOptions _options;
    private readonly BrowserNavigation _navigation;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly NativeAccessHost _access;
    private readonly int _uiThread = Environment.CurrentManagedThreadId;
    private readonly System.Windows.Forms.Timer _accessPulse = new() { Interval = 200 };
    private readonly ToolStripStatusLabel _accessText = new() { Text = "Locked. Reconnect to unlock." };
    private readonly ToolStripButton _reconnect = new("Reconnect");
    private readonly StatusStrip _accessStatus = new();
    private bool _initialized;
    private bool _shuttingDown;
    private bool _lockingDocument;

    public MainWindow(
        TrayOptions options, IExternalOpener external, IOwnedAccessSource ownership, IServiceLog log)
    {
        _options = options;
        _navigation = new(options, external);
        _access = NativeAccessHost.Create(options, ownership, _navigation, this, log);
        Text = "Hermes";
        Size = new(1280, 800);
        MinimumSize = new(800, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_web);
        _accessStatus.Items.AddRange([_accessText, _reconnect]);
        Controls.Add(_accessStatus);
        _reconnect.Click += OnReconnect;
        _accessPulse.Tick += OnAccessPulse;
    }

    public string RuntimeVersion => _web.CoreWebView2.Environment.BrowserVersionString;
    public NativeAccessEvidence AccessEvidence => _access.Evidence;
    public event Action<NativeHttpResponse>? ProtectedResponse;
    public event Action<NavigationStart>? NavigationStarted;
    public event Action<NavigationFinish>? NavigationFinished;

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
        AttachCoreEvents();
        _initialized = true;
        _accessPulse.Start();
    }

    private void AttachCoreEvents()
    {
        var core = _web.CoreWebView2;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.SourceChanged += OnSourceChanged;
        core.WebMessageReceived += OnWebMessageReceived;
        core.WebResourceResponseReceived += OnWebResponse;
        core.NewWindowRequested += OnNewWindow;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
    }

    public Task NavigateAsync(string path, CancellationToken cancellationToken) =>
        NavigationRequest.RunAsync(this, new Uri(_options.BaseAddress, path),
            _navigation.IsCanonical, cancellationToken);

    void INavigationDriver.Navigate(Uri target) =>
        _web.CoreWebView2.Navigate(target.AbsoluteUri);

    public Task<string> ExecuteScriptAsync(string script, CancellationToken cancellationToken) =>
        _web.CoreWebView2.ExecuteScriptAsync(script).WaitAsync(cancellationToken);

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        _access.NavigationStarting(args.NavigationId, ExternalAddress.Parse(args.Uri));
        NavigationStarted?.Invoke(new(
            args.NavigationId, ExternalAddress.Parse(args.Uri), args.IsRedirected));
        if (_lockingDocument && args.Uri == "about:blank")
        {
            _lockingDocument = false;
            return;
        }
        _navigation.NavigationStarting(args.Uri, () => args.Cancel = true);
        if (args.Cancel)
            _access.Suspend();
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        _access.NavigationCompleted(args.NavigationId, args.IsSuccess);
        NavigationFinished?.Invoke(new(
            args.NavigationId, args.IsSuccess, ExternalAddress.Parse(_web.CoreWebView2.Source)));
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs args)
    {
        if (!args.IsNewDocument)
            _access.HistoryChanged();
    }

    // CoreWebView2.WebMessageReceived is the engine's top-level document event.
    // Do not subscribe CoreWebView2Frame.WebMessageReceived or infer this bit from Source.
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args) =>
        _access.Receive(true, args.Source, args.WebMessageAsJson);

    private void OnWebResponse(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        if (ExternalAddress.Parse(args.Request.Uri) is not { } uri ||
            !_navigation.IsCanonical(uri) || uri.Query.Length != 0)
            return;
        if ((args.Request.Method, uri.AbsolutePath) is
            ("GET", "/api/settings") or ("PUT", "/api/preferences") or ("POST", "/api/sync"))
            ProtectedResponse?.Invoke(new(args.Request.Method, uri.AbsolutePath, args.Response.StatusCode));
    }

    private void OnAccessPulse(object? sender, EventArgs args) => _access.Pulse();

    private void OnReconnect(object? sender, EventArgs args)
    {
        if (_initialized && !_shuttingDown)
            _web.CoreWebView2.Navigate(_options.BaseAddress.AbsoluteUri);
    }

    Uri? INativeAccessSurface.Source =>
        _web.CoreWebView2 is { } core ? ExternalAddress.Parse(core.Source) : null;

    private void VerifyUi()
    {
        if (Environment.CurrentManagedThreadId != _uiThread)
            throw new InvalidOperationException("Native access requires the window UI thread.");
    }

    void INativeAccessSurface.VerifyAccess() => VerifyUi();

    void INativeAccessSurface.PostReady(string json)
    {
        VerifyUi();
        _web.CoreWebView2.PostWebMessageAsJson(json);
    }

    void INativeAccessSurface.RenderAccess(NativeAccessView view)
    {
        VerifyUi();
        _accessText.Text = view.Text;
        _reconnect.Enabled = _initialized && view.CanReconnect && !_shuttingDown;
    }

    void INativeAccessSurface.ClearAccessDocument()
    {
        VerifyUi();
        if (_web.CoreWebView2 is { } core && core.Source != "about:blank")
        {
            _lockingDocument = true;
            core.Navigate("about:blank");
        }
    }

    public Task StopAccessAsync(CancellationToken cancellationToken)
    {
        VerifyUi();
        _accessPulse.Stop();
        return _access.StopAsync(cancellationToken);
    }

    private void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs args) =>
        _navigation.NewWindowRequested(args.Uri, () => args.Handled = true);

    public void Shutdown()
    {
        if (!_access.Stopped)
            throw new InvalidOperationException("Native access must be joined before window disposal.");
        _shuttingDown = true;
        _accessPulse.Dispose();
        Close();
        Dispose();
    }

    protected override void OnFormClosing(FormClosingEventArgs args)
    {
        if (!_shuttingDown && args.CloseReason == CloseReason.UserClosing)
        {
            args.Cancel = true;
            _access.Suspend();
            Hide();
            return;
        }
        base.OnFormClosing(args);
    }
}
