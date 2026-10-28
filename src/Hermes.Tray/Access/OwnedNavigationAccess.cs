namespace Hermes.Tray.Access;

internal enum AccessDocument { Other, CanonicalReactApplication }
internal enum NativeAccessPhase
{
    Unpaired, AwaitingPairing, Pairing, Approving, AwaitingExchange, Authenticated, Cancelling
}
internal enum NativeAccessFailure { None, OwnerUnavailable, IdentityChanged, PairingRejected, Expired, Cancelled }

internal sealed record NativeAccessView(NativeAccessPhase Phase, NativeAccessFailure Failure)
{
    public bool CanReconnect => Phase == NativeAccessPhase.Unpaired;
    public string Text => Phase switch
    {
        NativeAccessPhase.Authenticated => "Unlocked",
        NativeAccessPhase.Cancelling => "Locked. Cancelling unlock…",
        NativeAccessPhase.Pairing or NativeAccessPhase.Approving => "Unlocking…",
        NativeAccessPhase.AwaitingExchange => "Waiting for browser confirmation…",
        _ => "Locked. Reconnect to unlock."
    };
}

internal sealed record AccessEnvironmentSnapshot(
    IAccessProcessHandle Process, long RestartGeneration, ulong NativeNavigationId,
    Uri Source, bool Completed, AccessDocument Document);

internal interface IAccessEnvironment
{
    // The native interpreter supplies actual owned-handle and current top-level document state.
    AccessEnvironmentSnapshot? Read();
}

internal sealed record AccessDeadline(long Started, TimeSpan Lifetime)
{
    public bool IsLive(TimeProvider clock) => clock.GetElapsedTime(Started) < Lifetime;

    public AccessDeadline Earlier(AccessDeadline other, TimeProvider clock)
    {
        var now = clock.GetTimestamp();
        return Lifetime - clock.GetElapsedTime(Started, now) <=
               other.Lifetime - clock.GetElapsedTime(other.Started, now)
            ? this : other;
    }

    public static AccessDeadline Until(TimeProvider clock, DateTimeOffset expiresAt, TimeSpan maximum)
    {
        var remaining = expiresAt - clock.GetUtcNow();
        return new(clock.GetTimestamp(), remaining < maximum ? remaining : maximum);
    }
}

// Native hooks must await their calls; invalidate first, join outstanding calls, then dispose.
// No WebView, HTTP transport, credential, verifier, or Core dependency lives here.
internal sealed class OwnedNavigationAccess(
    OwnerTarget target, IOwnerControlClient owner, IAccessEnvironment environment, TimeProvider clock)
    : IDisposable
{
    private readonly object _gate = new();
    private Navigation? _navigation;
    private Session? _session;
    private AccessPairingNotice? _notice;
    private OwnerPairing? _pairing;
    private OwnerApproval? _approval;
    private AccessAuthenticatedNotice? _earlyStatus;
    private AccessDeadline? _deadline;
    private bool _completionStarted;
    private PairingOperation? _pairingWork;
    private Task _pairingCancellation = Task.CompletedTask;
    private bool _disposed;
    private NativeAccessView _view = new(NativeAccessPhase.Unpaired, NativeAccessFailure.None);

    public NativeAccessView View
    {
        get
        {
            lock (_gate)
            {
                if (_session is { } session)
                    _ = Current(session);
                Expire();
                return _view;
            }
        }
    }

    public AccessReady? CurrentReady
    {
        get
        {
            lock (_gate)
                return _session is { } session && Current(session)
                    ? new(session.NavigationId, session.Instance.InstanceId) : null;
        }
    }

    public void NavigationStarting(ulong nativeNavigationId, Uri source)
    {
        Navigation? previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _navigation;
            Clear(NativeAccessFailure.None);
            _navigation = new(nativeNavigationId, source.AbsoluteUri, new());
            _completionStarted = false;
        }
        Cancel(previous);
    }

    public void Invalidate()
    {
        Navigation? previous;
        lock (_gate)
        {
            previous = _navigation;
            _navigation = null;
            Clear(NativeAccessFailure.IdentityChanged);
        }
        Cancel(previous);
    }

    public async Task<AccessReady?> NavigationCompletedAsync(
        ulong nativeNavigationId, CancellationToken cancellationToken)
    {
        using var operation = BeginCompletion(nativeNavigationId, cancellationToken);
        if (operation is null)
            return null;
        try
        {
            var status = await owner.GetStatusAsync(operation.Capture.Process.Identity, null, operation.Token);
            return FinishCompletion(operation, status);
        }
        catch (OperationCanceledException)
        {
            FailNavigation(operation.Navigation, NativeAccessFailure.Cancelled);
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private NavigationOperation? BeginCompletion(ulong id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed || _completionStarted || _navigation is not { } navigation ||
                navigation.NativeId != id || environment.Read() is not { } capture ||
                !Trusted(capture, navigation))
                return null;
            _completionStarted = true;
            return new(navigation, capture,
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, navigation.Stop.Token));
        }
    }

    private AccessReady? FinishCompletion(
        NavigationOperation operation, OwnerResult<OwnerStatus> result)
    {
        lock (_gate)
        {
            if (!Current(operation))
                return null;
            if (result is not OwnerResult<OwnerStatus>.Success { Value: { Phase: OwnerPhase.Ready } status } ||
                !MatchesOwned(status.Instance, operation.Capture))
            {
                Clear(NativeAccessFailure.OwnerUnavailable);
                return null;
            }
            _session = new(operation.Navigation, operation.Capture, status.Instance, AccessJson.NewIdentifier());
            _view = new(NativeAccessPhase.AwaitingPairing, NativeAccessFailure.None);
            return new(_session.NavigationId, status.Instance.InstanceId);
        }
    }

    public async Task ReceiveAsync(
        AccessMessageSource source, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AccessBrowserNotice notice;
        try { notice = AccessBridge.Read(bytes); }
        catch (AccessProtocolFault) { return; }
        switch (notice)
        {
            case AccessPairingNotice pairing:
                await PairAsync(source, pairing, cancellationToken);
                break;
            case AccessAuthenticatedNotice status:
                ReceiveStatus(source, status);
                break;
        }
    }

    private async Task PairAsync(
        AccessMessageSource source, AccessPairingNotice notice, CancellationToken cancellationToken)
    {
        var work = BeginPairing(source, notice, cancellationToken);
        if (work is null)
            return;
        try { await RunPairingAsync(work, cancellationToken); }
        finally { await FinishPairingAsync(work); }
    }

    private async Task RunPairingAsync(PairingOperation work, CancellationToken cancellationToken)
    {
        try
        {
            if (await ResolveAsync(work) is { } pairing)
                await ApproveAsync(work, pairing);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            FailPairing(work, NativeAccessFailure.Cancelled);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    // Settlement cannot be abandoned: retain the gate through work and cancellation callbacks.
    private async Task FinishPairingAsync(PairingOperation work)
    {
        Task cancellation;
        lock (_gate)
        {
            CancelPairing();
            cancellation = _pairingCancellation;
        }
        try { await cancellation; }
        finally { EndPairing(work); }
    }

    private void EndPairing(PairingOperation work)
    {
        lock (_gate)
        {
            work.Dispose();
            _pairingWork = null;
            _pairingCancellation = Task.CompletedTask;
            if (_view.Phase == NativeAccessPhase.Cancelling)
                _view = new(NativeAccessPhase.Unpaired, _view.Failure);
        }
    }

    private PairingOperation? BeginPairing(
        AccessMessageSource source, AccessPairingNotice notice, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Expire();
            if (_session is not { } session || !Accepts(session, source, notice) || _pairingWork is not null ||
                _view.Phase is not (NativeAccessPhase.AwaitingPairing or NativeAccessPhase.Unpaired) ||
                _notice?.PairingId == notice.PairingId)
                return null;
            ResetPairing();
            _notice = notice;
            _view = new(NativeAccessPhase.Pairing, NativeAccessFailure.None);
            return _pairingWork = new(session, notice, CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, session.Navigation.Stop.Token));
        }
    }

    private async Task<OwnerPairing?> ResolveAsync(PairingOperation work)
    {
        lock (_gate)
            if (!Current(work))
                return null;
        var result = await owner.ResolveAsync(work.Session.Instance, work.Notice.UserCode, work.Token);
        lock (_gate)
        {
            if (!Current(work))
                return null;
            if (result is not OwnerResult<OwnerPairing>.Success success ||
                !MatchesNotice(success.Value, work.Notice))
                return RejectPairing(NativeAccessFailure.PairingRejected);
            _pairing = success.Value;
            _deadline = AccessDeadline.Until(clock, _pairing.ExpiresAt, TimeSpan.FromSeconds(180));
            return _deadline.IsLive(clock) ? _pairing : RejectPairing(NativeAccessFailure.Expired);
        }
    }

    private async Task ApproveAsync(PairingOperation work, OwnerPairing pairing)
    {
        lock (_gate)
        {
            Expire();
            if (!Current(work) || !ReferenceEquals(_pairing, pairing))
                return;
            _view = new(NativeAccessPhase.Approving, NativeAccessFailure.None);
        }
        var result = await owner.ApproveAsync(
            work.Session.Instance, pairing, AccessCapabilities.Ui, work.Token);
        lock (_gate)
        {
            Expire();
            if (!Current(work) || !ReferenceEquals(_pairing, pairing))
                return;
            FinishApproval(result, pairing);
        }
    }

    private void FinishApproval(OwnerResult<OwnerApproval> result, OwnerPairing pairing)
    {
        if (_deadline is not { } originalDeadline ||
            result is not OwnerResult<OwnerApproval>.Success success ||
            success.Value.PairingId != pairing.PairingId ||
            success.Value.ExpiresAt > pairing.ExpiresAt ||
            !AccessCapabilities.Equal(success.Value.Capabilities, AccessCapabilities.Ui))
        {
            _ = RejectPairing(NativeAccessFailure.PairingRejected);
            return;
        }
        _approval = success.Value;
        _deadline = originalDeadline.Earlier(
            AccessDeadline.Until(clock, _approval.ExpiresAt, TimeSpan.FromSeconds(60)), clock);
        _view = new(NativeAccessPhase.AwaitingExchange, NativeAccessFailure.None);
        Expire();
        if (_earlyStatus is { } status)
            Authenticate(status);
    }

    private void ReceiveStatus(AccessMessageSource source, AccessAuthenticatedNotice status)
    {
        lock (_gate)
        {
            Expire();
            if (_session is not { } session || !Accepts(session, source, status) ||
                _notice is null ||
                !AccessCapabilities.Equal(status.Capabilities, AccessCapabilities.EffectiveUi))
                return;
            if (_view.Phase == NativeAccessPhase.Approving)
                _earlyStatus ??= status;
            else if (_view.Phase == NativeAccessPhase.AwaitingExchange)
                Authenticate(status);
        }
    }

    private void Authenticate(AccessAuthenticatedNotice status)
    {
        if (_approval is null || _deadline is not { } approvalDeadline ||
            !approvalDeadline.IsLive(clock))
            return;
        var deadline = AccessDeadline.Until(clock, status.ExpiresAt, TimeSpan.FromMinutes(30));
        if (!deadline.IsLive(clock))
        {
            _ = RejectPairing(NativeAccessFailure.Expired);
            return;
        }
        _deadline = deadline;
        _earlyStatus = null;
        _view = new(NativeAccessPhase.Authenticated, NativeAccessFailure.None);
    }

    private bool MatchesNotice(OwnerPairing pairing, AccessPairingNotice notice) =>
        pairing.PairingId == notice.PairingId && pairing.UserCode == notice.UserCode &&
        pairing.Challenge == notice.Challenge && pairing.ApiOrigin == target.ApiOrigin &&
        pairing.BrowserOrigin == target.ApiOrigin &&
        pairing.CreatedAt < pairing.ExpiresAt &&
        AccessCapabilities.Equal(pairing.RequestedCapabilities, AccessCapabilities.Ui);

    private bool Accepts(
        Session session, AccessMessageSource source, AccessBrowserNotice notice) =>
        Current(session) && source.IsTopLevel &&
        source.NativeNavigationId == session.Navigation.NativeId &&
        source.Source.AbsoluteUri == session.Navigation.Source &&
        notice.NavigationId == session.NavigationId &&
        notice.InstanceId == session.Instance.InstanceId;

    private bool Current(NavigationOperation operation) =>
        !operation.Token.IsCancellationRequested && ReferenceEquals(_navigation, operation.Navigation) &&
        SameEnvironment(operation.Capture, operation.Navigation);

    private bool Current(PairingOperation operation) =>
        !operation.Token.IsCancellationRequested && ReferenceEquals(_notice, operation.Notice) &&
        ReferenceEquals(_session, operation.Session) && Current(operation.Session);

    private bool Current(Session session)
    {
        if (!_disposed && ReferenceEquals(_session, session) &&
            ReferenceEquals(_navigation, session.Navigation) &&
            SameEnvironment(session.Capture, session.Navigation))
            return true;
        if (ReferenceEquals(_session, session))
            Clear(NativeAccessFailure.IdentityChanged);
        return false;
    }

    private bool SameEnvironment(AccessEnvironmentSnapshot capture, Navigation navigation) =>
        environment.Read() is { } current && Trusted(current, navigation) &&
        ReferenceEquals(current.Process, capture.Process) &&
        current.Process.Identity == capture.Process.Identity &&
        current.RestartGeneration == capture.RestartGeneration;

    private bool Trusted(AccessEnvironmentSnapshot capture, Navigation navigation) =>
        capture.Completed && capture.Document == AccessDocument.CanonicalReactApplication &&
        capture.Process.IsAlive && capture.NativeNavigationId == navigation.NativeId &&
        capture.Source.AbsoluteUri == navigation.Source && Canonical(capture.Source);

    private bool Canonical(Uri source) =>
        source.IsAbsoluteUri && source.Scheme == Uri.UriSchemeHttp &&
        string.IsNullOrEmpty(source.UserInfo) &&
        source.GetLeftPart(UriPartial.Authority) == target.ApiOrigin;

    private bool MatchesOwned(OwnerInstance instance, AccessEnvironmentSnapshot capture) =>
        instance.Process == capture.Process.Identity && instance.ConfigKey == target.ConfigKey &&
        instance.Origin == target.ApiOrigin;

    private void Expire()
    {
        if (_deadline is { } deadline && !deadline.IsLive(clock))
            _ = RejectPairing(NativeAccessFailure.Expired);
    }

    private OwnerPairing? RejectPairing(NativeAccessFailure failure)
    {
        ResetPairing();
        _view = new(_pairingWork is null ? NativeAccessPhase.Unpaired : NativeAccessPhase.Cancelling, failure);
        return null;
    }

    private void ResetPairing()
    {
        CancelPairing();
        _notice = null;
        _pairing = null;
        _approval = null;
        _earlyStatus = null;
        _deadline = null;
    }

    private void CancelPairing()
    {
        if (_pairingWork is { } work && !work.Token.IsCancellationRequested)
            _pairingCancellation = work.Cancellation.CancelAsync();
    }

    private void Clear(NativeAccessFailure failure)
    {
        _session = null;
        _ = RejectPairing(failure);
    }

    private void FailNavigation(Navigation navigation, NativeAccessFailure failure)
    {
        lock (_gate)
            if (ReferenceEquals(_navigation, navigation))
                Clear(failure);
    }

    private void FailPairing(PairingOperation work, NativeAccessFailure failure)
    {
        lock (_gate)
            if (ReferenceEquals(_session, work.Session) && ReferenceEquals(_notice, work.Notice))
                _ = RejectPairing(failure);
    }

    private static void Cancel(Navigation? navigation)
    {
        if (navigation is null)
            return;
        navigation.Stop.Cancel();
        navigation.Stop.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        Invalidate();
    }

    private sealed record Navigation(ulong NativeId, string Source, CancellationTokenSource Stop);
    private sealed record Session(
        Navigation Navigation, AccessEnvironmentSnapshot Capture, OwnerInstance Instance, string NavigationId);
    private sealed record NavigationOperation(
        Navigation Navigation, AccessEnvironmentSnapshot Capture, CancellationTokenSource Cancellation)
        : IDisposable
    {
        public CancellationToken Token => Cancellation.Token;
        public void Dispose() => Cancellation.Dispose();
    }
    private sealed record PairingOperation(
        Session Session, AccessPairingNotice Notice, CancellationTokenSource Cancellation)
        : IDisposable
    {
        public CancellationToken Token => Cancellation.Token;
        public void Dispose() => Cancellation.Dispose();
    }
}
