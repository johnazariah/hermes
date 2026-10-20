using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Hermes.Tray.Access;
using Xunit;

namespace Hermes.Tray.Tests;

public sealed class NativeAccessHostTests
{
    [Fact]
    public Task Real_host_dispatch_runs_ready_pairing_and_exchange_status_on_the_UI_thread() =>
        AccessSta.RunAsync(async () =>
        {
            await using var rig = new NativeHostFixture();
            rig.Owner.Status = async _ =>
            {
                await Task.Run(() => Thread.Sleep(10));
                return new OwnerResult<OwnerStatus>.Success(
                    new(AccessFixture.Instance(rig.Target), OwnerPhase.Ready, AccessCapabilities.EffectiveUi));
            };
            var ready = await rig.ReadyAsync();
            await rig.PairAsync(ready);
            Assert.False(rig.Host.Evidence.Paired);
            await rig.StatusAsync(ready);
            Assert.True(rig.Host.Evidence.Paired);
            Assert.Equal(AccessFixture.Id, rig.Host.Evidence.CurrentInstance);
            Assert.Equal((1, 1, 1), (rig.Owner.StatusCalls, rig.Owner.ResolveCalls, rig.Owner.ApproveCalls));
            Assert.Equal(3, rig.Public.Reads);
            Assert.Equal(0, rig.Surface.Cleared);
        });

    [Theory]
    [InlineData("iframe")]
    [InlineData("foreign")]
    [InlineData("old-navigation")]
    [InlineData("wrong-instance")]
    public Task Untrusted_engine_dispatch_does_no_pairing_owner_work(string change) =>
        AccessSta.RunAsync(async () =>
        {
            await using var rig = new NativeHostFixture();
            var ready = await rig.ReadyAsync();
            var notice = change switch
            {
                "old-navigation" => ready with { NavigationId = AccessFixture.OtherId },
                "wrong-instance" => ready with { InstanceId = AccessFixture.OtherId },
                _ => ready
            };
            rig.Host.Receive(change != "iframe",
                change == "foreign" ? "http://localhost:31991/" : rig.Surface.Source.AbsoluteUri,
                Encoding.UTF8.GetString(AccessFixture.PairingNotice(notice)));
            await rig.Host.DrainAsync(CancellationToken.None);
            Assert.Equal((0, 0), (rig.Owner.ResolveCalls, rig.Owner.ApproveCalls));
            Assert.False(rig.Host.Evidence.Paired);
        });

    [Theory]
    [InlineData("/api/access")]
    [InlineData("/api/settings")]
    [InlineData("/assets/application.js")]
    public Task Same_origin_non_application_documents_receive_no_ready_or_owner_work(string path) =>
        AccessSta.RunAsync(async () =>
        {
            await using var rig = new NativeHostFixture();
            rig.Surface.Navigate(new(rig.Target.ApiOrigin + path));
            await rig.CompleteAsync(1);
            Assert.Empty(rig.Surface.Messages);
            Assert.Equal(0, rig.Owner.StatusCalls);
            Assert.Equal(0, rig.Public.Reads);
        });

    [Fact]
    public Task Failed_or_obsolete_engine_completion_cannot_publish_ready() =>
        AccessSta.RunAsync(async () =>
        {
            await using var rig = new NativeHostFixture();
            rig.Host.NavigationStarting(1, rig.Surface.Source);
            rig.Host.NavigationCompleted(1, false);
            rig.Host.NavigationStarting(2, rig.Surface.Source);
            rig.Host.NavigationCompleted(1, true);
            await rig.Host.DrainAsync(CancellationToken.None);
            Assert.Empty(rig.Surface.Messages);
            Assert.Equal(0, rig.Owner.StatusCalls);
        });

    [Fact]
    public Task Missing_owner_principal_is_visible_unpaired_without_webview_failure() =>
        AccessSta.RunAsync(async () =>
        {
            await using var rig = new NativeHostFixture();
            rig.Owner.Status = _ => Task.FromResult<OwnerResult<OwnerStatus>>(
                new OwnerResult<OwnerStatus>.Failure(OwnerFailure.Unavailable));
            await rig.CompleteAsync(1);
            Assert.Empty(rig.Surface.Messages);
            Assert.True(rig.Surface.View.CanReconnect);
            Assert.Equal(NativeAccessFailure.OwnerUnavailable, rig.Surface.View.Failure);
            Assert.Equal(0, rig.Surface.Cleared);
            Assert.DoesNotContain(rig.Log.Entries, entry => entry.Code == EventCode.WindowFailed);
        });

    [Fact]
    public Task Public_identity_disagreement_prevents_private_resolve_and_approve() =>
        AccessSta.RunAsync(async () =>
        {
            await using var rig = new NativeHostFixture();
            var ready = await rig.ReadyAsync();
            rig.Public.Replace(AccessFixture.Instance(rig.Target) with { InstanceId = AccessFixture.OtherId });
            await rig.PairAsync(ready);
            Assert.Equal((0, 0), (rig.Owner.ResolveCalls, rig.Owner.ApproveCalls));
            Assert.False(rig.Host.Evidence.Paired);
            Assert.True(rig.Surface.View.CanReconnect);
        });

    [Theory]
    [InlineData("restart")]
    [InlineData("death")]
    public Task Actual_owned_state_loss_clears_ready_and_the_old_browser_document(string change) =>
        AccessSta.RunAsync(async () =>
        {
            await using var rig = new NativeHostFixture();
            var ready = await rig.ReadyAsync();
            await rig.PairAsync(ready);
            await rig.StatusAsync(ready);
            if (change == "death")
                rig.Process.Alive = false;
            else
                rig.Owned.Restart();
            rig.Host.Pulse();
            await rig.StatusAsync(ready);
            Assert.False(rig.Host.Evidence.Paired);
            Assert.Null(rig.Host.Evidence.CurrentInstance);
            Assert.Equal(1, rig.Surface.Cleared);
        });

    [Fact]
    public Task Queued_same_URL_notice_from_previous_navigation_does_no_owner_work() =>
        AccessSta.RunAsync(async () =>
        {
            await using var rig = new NativeHostFixture();
            var old = await rig.ReadyAsync();
            var current = await rig.ReadyAsync(2);
            Assert.NotEqual(old.NavigationId, current.NavigationId);
            await rig.PairAsync(old);
            Assert.Equal((0, 0), (rig.Owner.ResolveCalls, rig.Owner.ApproveCalls));
            await rig.PairAsync(current);
            Assert.Equal((1, 1), (rig.Owner.ResolveCalls, rig.Owner.ApproveCalls));
        });

    [Fact]
    public Task Clock_expiry_cannot_leave_native_authenticated_evidence() =>
        AccessSta.RunAsync(async () =>
        {
            await using var rig = new NativeHostFixture();
            var ready = await rig.ReadyAsync();
            await rig.PairAsync(ready);
            await rig.StatusAsync(ready);
            rig.Clock.Advance(TimeSpan.FromMinutes(31));
            rig.Host.Pulse();
            Assert.False(rig.Host.Evidence.Paired);
            Assert.True(rig.Surface.View.CanReconnect);
        });

    [Fact]
    public Task Shutdown_cancels_but_joins_late_successful_owner_work_before_disposal() =>
        AccessSta.RunAsync(async () =>
        {
            await using var rig = new NativeHostFixture();
            var pending = new TaskCompletionSource<OwnerResult<OwnerPairing>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var ownerToken = CancellationToken.None;
            rig.Owner.Resolve = token => { ownerToken = token; return pending.Task; };
            var ready = await rig.ReadyAsync();
            rig.ReceivePairing(ready);
            await UntilAsync(() => rig.Owner.ResolveCalls == 1, CancellationToken.None);
            var stop = rig.Host.StopAsync(CancellationToken.None);
            Assert.True(ownerToken.IsCancellationRequested);
            Assert.False(stop.IsCompleted);
            Assert.False(rig.Public.Disposed);
            pending.SetResult(new OwnerResult<OwnerPairing>.Success(AccessFixture.Pairing(rig.Target)));
            await stop;
            Assert.True(rig.Public.Disposed);
            Assert.Equal(0, rig.Owner.ApproveCalls);
            Assert.False(rig.Host.Evidence.Paired);
        });

    private static async Task UntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!predicate())
            await Task.Delay(1, timeout.Token);
    }
}

internal sealed class NativeHostFixture : IAsyncDisposable
{
    public OwnerTarget Target { get; } = AccessFixture.Target();
    public AccessTestClock Clock { get; } = new();
    public AccessTestProcess Process { get; } = new(AccessFixture.Process);
    public AccessTestOwner Owner { get; }
    public NativePublicIdentity Public { get; }
    public NativeOwnedSource Owned { get; }
    public NativeTestSurface Surface { get; }
    public MemoryLog Log { get; } = new();
    public NativeAccessHost Host { get; }

    public NativeHostFixture()
    {
        Owner = new(Target);
        Public = new(AccessFixture.Instance(Target));
        Owned = new(new(Process, 0, Process.Identity.ProcessId));
        Surface = new(new(Target.ApiOrigin + "/"));
        var options = new TrayOptions(Target.CanonicalDirectory, Target.Port, @"C:\synthetic\service.exe");
        var navigation = new BrowserNavigation(options, new NativeNoExternal());
        Host = new(Target, Owner, Public, new(Owned, navigation, Surface), Surface, Log, Clock);
    }

    public async Task CompleteAsync(ulong engineNavigation, CancellationToken cancellationToken = default)
    {
        Host.NavigationStarting(engineNavigation, Surface.Source);
        Host.NavigationCompleted(engineNavigation, true);
        await Host.DrainAsync(cancellationToken);
    }

    public async Task<AccessReady> ReadyAsync(
        ulong engineNavigation = 1, CancellationToken cancellationToken = default)
    {
        await CompleteAsync(engineNavigation, cancellationToken);
        return Surface.Messages[^1];
    }

    public void ReceivePairing(AccessReady ready) =>
        Host.Receive(true, Surface.Source.AbsoluteUri,
            Encoding.UTF8.GetString(AccessFixture.PairingNotice(ready)));

    public async Task PairAsync(AccessReady ready, CancellationToken cancellationToken = default)
    {
        ReceivePairing(ready);
        await Host.DrainAsync(cancellationToken);
    }

    public async Task StatusAsync(AccessReady ready, CancellationToken cancellationToken = default)
    {
        Host.Receive(true, Surface.Source.AbsoluteUri,
            Encoding.UTF8.GetString(AccessFixture.StatusNotice(ready, AccessFixture.Now.AddMinutes(30))));
        await Host.DrainAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync() => await Host.StopAsync(CancellationToken.None);
}

internal sealed class NativePublicIdentity(OwnerInstance instance) : IPublicAccessIdentity
{
    private OwnerInstance _instance = instance;
    private int _reads;
    private bool _disposed;
    public int Reads => _reads;
    public bool Disposed => _disposed;
    public void Replace(OwnerInstance value) => _instance = value;

    public async Task<OwnerResult<PublicAccessIdentity>> ReadAsync(CancellationToken cancellationToken)
    {
        _reads++;
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return new OwnerResult<PublicAccessIdentity>.Success(
            new(_instance.InstanceId, _instance.Process, _instance.Origin));
    }

    public void Dispose() => _disposed = true;
}

internal sealed class NativeOwnedSource(OwnedAccessState initial) : IOwnedAccessSource
{
    private OwnedAccessState _state = initial;
    public OwnedAccessState? ReadAccess() => _state.Process.IsAlive ? _state : null;
    public void Restart() => _state = _state with { RestartGeneration = _state.RestartGeneration + 1 };
}

internal sealed class NativeTestSurface(Uri source) : INativeAccessSurface
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private Uri _source = source;
    private ImmutableArray<AccessReady> _messages = [];
    private NativeAccessView _view = new(NativeAccessPhase.Unpaired, NativeAccessFailure.None);
    private int _cleared;
    public Uri Source => _source;
    public ImmutableArray<AccessReady> Messages => _messages;
    public NativeAccessView View => _view;
    public int Cleared => _cleared;

    public void VerifyAccess() => Assert.Equal(_thread, Environment.CurrentManagedThreadId);
    public void Navigate(Uri value) { VerifyAccess(); _source = value; }
    public void RenderAccess(NativeAccessView view) { VerifyAccess(); _view = view; }

    public void PostReady(string json)
    {
        VerifyAccess();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        AccessJson.Object(root, "version", "type", "navigationId", "instanceId");
        Assert.Equal(1, AccessJson.Integer(root, "version"));
        Assert.Equal("hermes.access.ready", AccessJson.Text(root, "type"));
        _messages = _messages.Add(new(
            AccessJson.Identifier(root, "navigationId"), AccessJson.Identifier(root, "instanceId")));
    }

    public void ClearAccessDocument()
    {
        VerifyAccess();
        _cleared++;
        _source = new("about:blank");
    }
}

internal sealed class NativeNoExternal : IExternalOpener
{
    public void Open(string address) => throw new InvalidOperationException("Unexpected external navigation.");
}

internal static class AccessSta
{
    public static Task RunAsync(Func<Task> exercise, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => Execute(exercise, completion)) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(cancellationToken);
    }

    private static void Execute(Func<Task> exercise, TaskCompletionSource completion)
    {
        try
        {
            using var control = new Control();
            _ = control.Handle;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var work = exercise();
            while (!work.IsCompleted)
            {
                Application.DoEvents();
                Thread.Sleep(1);
            }
            work.GetAwaiter().GetResult();
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }
}
