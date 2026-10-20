using System.Collections.Immutable;
using System.Text;
using Hermes.Tray.Access;

namespace Hermes.Tray;

internal sealed record NativeAccessEvidence(
    bool Paired, string? CurrentInstance, ImmutableArray<string> Capabilities);

internal sealed record NativeHttpResponse(string Method, string Path, int StatusCode);

internal sealed class NativeAccessHost
{
    private readonly NativeAccessEnvironment _environment;
    private readonly INativeAccessSurface _surface;
    private readonly IPublicAccessIdentity _publicIdentity;
    private readonly IServiceLog _log;
    private readonly OwnedNavigationAccess _access;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _work = [];
    private AccessReady? _published;
    private ulong _generation;
    private ulong _engineNavigation;
    private bool _successfulDocument;
    private bool _closing;
    private Task? _shutdown;

    public NativeAccessHost(
        OwnerTarget target, IOwnerControlClient owner, IPublicAccessIdentity publicIdentity,
        NativeAccessEnvironment environment, INativeAccessSurface surface,
        IServiceLog log, TimeProvider clock)
    {
        (_environment, _surface, _publicIdentity, _log) = (environment, surface, publicIdentity, log);
        _access = new(target, new VerifiedOwnerControl(target, owner, publicIdentity, environment),
            environment, clock);
    }

    public static NativeAccessHost Create(
        TrayOptions options, IOwnedAccessSource ownership, BrowserNavigation navigation,
        INativeAccessSurface surface, IServiceLog log)
    {
        var target = OwnerTarget.Create(options.ConfigDirectory, options.Port);
        var owner = new OwnerControlClient(target, new(new OwnerPipeConnectionFactory()));
        return new(target, owner, new HttpAccessIdentity(target),
            new(ownership, navigation, surface), surface, log, TimeProvider.System);
    }

    public bool Stopped => _shutdown is { IsCompletedSuccessfully: true };

    public NativeAccessEvidence Evidence
    {
        get
        {
            _surface.VerifyAccess();
            var paired = !_closing && _access.View.Phase == NativeAccessPhase.Authenticated;
            var ready = paired ? _access.CurrentReady : null;
            return new(ready is not null, ready?.InstanceId, ready is null ? [] :
                AccessCapabilities.EffectiveUi.Select(AccessCapabilities.Name).ToImmutableArray());
        }
    }

    public void NavigationStarting(ulong engineNavigation, Uri? source)
    {
        _surface.VerifyAccess();
        if (_closing)
            return;
        _engineNavigation = engineNavigation;
        _successfulDocument = false;
        BeginDocument(source);
    }

    private void BeginDocument(Uri? source)
    {
        _published = null;
        _generation = checked(_generation + 1);
        _environment.Starting(_generation, source);
        if (source is { IsAbsoluteUri: true })
            _access.NavigationStarting(_generation, source);
        else
            _access.Invalidate();
        Pulse();
    }

    public void NavigationCompleted(ulong engineNavigation, bool success)
    {
        _surface.VerifyAccess();
        if (_closing || engineNavigation != _engineNavigation)
            return;
        _environment.Complete(_generation, success);
        _successfulDocument = _environment.Completed;
        if (_successfulDocument)
            Dispatch(PublishAsync);
        else
            Pulse();
    }

    // History API changes retain the successfully completed engine document, but
    // receive a new native generation and exact current-source handshake.
    public void HistoryChanged()
    {
        _surface.VerifyAccess();
        if (_closing || !_successfulDocument ||
            _surface.Source?.AbsoluteUri == _environment.NavigationSource?.AbsoluteUri)
            return;
        BeginDocument(_surface.Source);
        _environment.Complete(_generation, true);
        if (_environment.Completed)
            Dispatch(PublishAsync);
    }

    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        var ready = await _access.NavigationCompletedAsync(_generation, cancellationToken);
        _surface.VerifyAccess();
        if (!_closing && ready is not null && _access.CurrentReady == ready)
        {
            _published = ready;
            _surface.PostReady(Encoding.UTF8.GetString(AccessBridge.Ready(ready)));
        }
    }

    public void Receive(bool isTopLevel, string source, string json)
    {
        _surface.VerifyAccess();
        if (_closing || !isTopLevel || json.Length > 8192 ||
            _environment.Read() is not { Completed: true } capture ||
            source != capture.Source.AbsoluteUri || _surface.Source?.AbsoluteUri != source)
            return;
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > 8192)
            return;
        var messageSource = new AccessMessageSource(capture.NativeNavigationId, capture.Source, true);
        Dispatch(token => _access.ReceiveAsync(messageSource, bytes, token));
    }

    public void Pulse()
    {
        _surface.VerifyAccess();
        if (_closing)
            return;
        var view = _access.View;
        if (_published is not null && _access.CurrentReady is null)
        {
            Suspend();
            return;
        }
        _surface.RenderAccess(view);
    }

    public void Suspend()
    {
        _surface.VerifyAccess();
        if (_closing)
            return;
        _published = null;
        _successfulDocument = false;
        _environment.Clear();
        _access.Invalidate();
        _surface.ClearAccessDocument();
        _surface.RenderAccess(_access.View);
    }

    private void Dispatch(Func<CancellationToken, Task> operation)
    {
        _work.RemoveAll(task => task.IsCompletedSuccessfully);
        _work.Add(GuardAsync(operation, _stop.Token));
    }

    private async Task GuardAsync(
        Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        try { await operation(cancellationToken); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            _access.Invalidate();
            _log.Write(new(EventCode.AccessFailed, Detail: SafeFailure.Describe(error)));
        }
        finally
        {
            if (!_closing)
                Pulse();
        }
    }

    public Task DrainAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(_work.ToArray()).WaitAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _surface.VerifyAccess();
        return _shutdown ??= StopCoreAsync(cancellationToken);
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        _closing = true;
        _published = null;
        _environment.Clear();
        _access.Invalidate();
        try
        {
            await _stop.CancelAsync();
            await Task.WhenAll(_work.ToArray());
        }
        finally
        {
            _access.Dispose();
            _publicIdentity.Dispose();
            _stop.Dispose();
        }
        // Cancellation may report after cleanup, never abandon the join.
        cancellationToken.ThrowIfCancellationRequested();
    }
}
