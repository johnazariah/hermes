using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hermes.Tray;
using Xunit;
using Xunit.Abstractions;

namespace Hermes.Tray.Tests;

public sealed class HealthTransportTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly Uri HandlerOrigin = new("http://127.0.0.1:31991/");
    private const string TransportDetail = "synthetic-transport-detail";
    private const int ForeignPid = 1000001;

    [Theory]
    [InlineData(HttpRequestError.ConnectionError, SocketError.ConnectionRefused, false)]
    [InlineData(HttpRequestError.ConnectionError, SocketError.HostUnreachable, true)]
    [InlineData(HttpRequestError.ConnectionError, SocketError.AccessDenied, true)]
    [InlineData(HttpRequestError.InvalidResponse, SocketError.ConnectionRefused, true)]
    public async Task Only_explicit_connection_stage_refusal_is_free(
        HttpRequestError httpError, SocketError socketError, bool occupied)
    {
        var error = new HttpRequestException(httpError, TransportDetail,
            new IOException(TransportDetail, new SocketException((int)socketError)));
        using var probe = new HttpHealthProbe(new HttpClient(new FailureHandler(error)));
        Assert.Equal(new(occupied, false, null),
            await probe.ProbeAsync(HandlerOrigin, CancellationToken.None));
        var diagnostic = Assert.IsType<HealthProbeDiagnostic>(probe.LastDiagnostic);
        Assert.Equal(socketError, diagnostic.SocketError);
        Assert.Equal(httpError, diagnostic.HttpError);
        Assert.Equal(SafeFailure.Describe(error), diagnostic.Failure);
        Assert.Equal(probe.FirstDiagnostic, diagnostic);
        Assert.DoesNotContain(TransportDetail, JsonSerializer.Serialize(diagnostic));
    }

    [Fact]
    public async Task Timeout_stays_occupied_but_a_later_explicit_refusal_within_budget_is_free()
    {
        var shortHandler = new GatedRefusalHandler();
        using var shortProbe = new HttpHealthProbe(new HttpClient(shortHandler),
            new(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50)));
        Assert.Equal(new(true, false, null),
            await shortProbe.ProbeAsync(HandlerOrigin, CancellationToken.None));
        Assert.Equal(HealthProbeOutcome.Timeout, shortProbe.LastDiagnostic?.Outcome);
        var handler = new GatedRefusalHandler();
        using var probe = new HttpHealthProbe(new HttpClient(handler));
        var pending = probe.ProbeAsync(HandlerOrigin, CancellationToken.None);
        await handler.Entered.WaitAsync(Deadline);
        handler.Release();
        Assert.Equal(new(false, false, null), await pending.WaitAsync(Deadline));
        Assert.Equal(HealthProbeOutcome.ConnectionRefused, probe.LastDiagnostic?.Outcome);
    }

    [Fact]
    public void Invalid_budgets_are_rejected_before_creating_the_http_client()
    {
        HealthProbePolicy[] invalid =
        [
            new(TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            new(TimeSpan.FromSeconds(1), TimeSpan.Zero),
            new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2))
        ];
        foreach (var policy in invalid)
            Assert.Throws<ArgumentException>(() => new HttpHealthProbe(policy: policy));
    }

    [Fact]
    public async Task Real_exclusively_reserved_closed_loopback_measures_legacy_and_current_budgets()
    {
        await using var fixture = new LoopbackHealthFixture(ResponseMode.Healthy);
        using var deadline = new CancellationTokenSource(Deadline);
        using var legacy = new HttpHealthProbe(policy:
            new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)));
        var old = await legacy.ProbeAsync(fixture.Origin, deadline.Token);
        Report("legacy-closed-loopback", legacy);
        Assert.False(old.Healthy);
        using var current = new HttpHealthProbe();
        var observed = await current.ProbeAsync(fixture.Origin, deadline.Token);
        Report("current-closed-loopback", current);
        Assert.Equal(new(false, false, null), observed);
        Assert.Equal(HealthProbeOutcome.ConnectionRefused, current.LastDiagnostic?.Outcome);
        Assert.Equal(SocketError.ConnectionRefused, current.LastDiagnostic?.SocketError);
        output.WriteLine("LegacyTimeoutReproduced={0}",
            legacy.LastDiagnostic?.Outcome == HealthProbeOutcome.Timeout && !observed.Occupied);
    }

    public enum ResponseMode { Healthy, WrongService, HttpError, MalformedJson, Silent }

    [Theory]
    [InlineData(ResponseMode.Healthy, true)]
    [InlineData(ResponseMode.WrongService, false)]
    [InlineData(ResponseMode.HttpError, false)]
    [InlineData(ResponseMode.MalformedJson, false)]
    public async Task Real_http_listener_preserves_occupancy_and_wire_identity(ResponseMode mode, bool healthy)
    {
        await using var fixture = new LoopbackHealthFixture(mode);
        fixture.BeginListening();
        using var deadline = new CancellationTokenSource(Deadline);
        using var probe = new HttpHealthProbe();
        var observed = await probe.ProbeAsync(fixture.Origin, deadline.Token);
        Report($"real-http-{mode}", probe);
        Assert.True(observed.Occupied);
        Assert.Equal(healthy, observed.Healthy);
        Assert.Equal(mode is ResponseMode.Healthy or ResponseMode.WrongService
            ? Environment.ProcessId : (int?)null, observed.ProcessId);
        Assert.Equal(ExpectedOutcome(mode), probe.LastDiagnostic?.Outcome);
    }

    [Fact]
    public async Task Real_non_http_listener_is_occupied_after_the_bounded_timeout()
    {
        await using var fixture = new LoopbackHealthFixture(ResponseMode.Silent);
        fixture.BeginListening();
        using var deadline = new CancellationTokenSource(Deadline);
        using var probe = new HttpHealthProbe();
        var observed = await probe.ProbeAsync(fixture.Origin, deadline.Token);
        await fixture.Accepted.WaitAsync(deadline.Token);
        Report("real-connected-silent-listener", probe);
        Assert.Equal(new(true, false, null), observed);
        Assert.Equal(HealthProbeOutcome.Timeout, probe.LastDiagnostic?.Outcome);
        Assert.Equal(8000L, probe.LastDiagnostic?.RequestBudgetMilliseconds);
    }

    [Fact]
    public async Task Caller_cancellation_of_a_real_connected_probe_remains_cancellation()
    {
        await using var fixture = new LoopbackHealthFixture(ResponseMode.Silent);
        fixture.BeginListening();
        using var deadline = new CancellationTokenSource(Deadline);
        using var probe = new HttpHealthProbe();
        var pending = probe.ProbeAsync(fixture.Origin, deadline.Token);
        await fixture.Accepted.WaitAsync(deadline.Token);
        deadline.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(HealthProbeOutcome.Cancelled, probe.LastDiagnostic?.Outcome);
        Report("caller-cancelled-real-probe", probe);
    }

    [Fact]
    public async Task Real_healthy_foreign_listener_prevents_launch_and_adoption()
    {
        await using var fixture = new LoopbackHealthFixture(ResponseMode.Healthy, ForeignPid);
        fixture.BeginListening();
        using var deadline = new CancellationTokenSource(Deadline);
        using var probe = new HttpHealthProbe();
        Assert.True((await probe.ProbeAsync(fixture.Origin, deadline.Token)).Healthy);
        var launcher = new FakeLauncher();
        var log = new MemoryLog();
        using var service = CreateService(fixture.Origin, launcher, probe, log);
        var final = await RunToFailureAsync(service, EventCode.ForeignListener, deadline.Token);
        Assert.Equal(SupervisionDisposition.Failed, final.Disposition);
        Assert.Null(final.LastOwned);
        Assert.Empty(final.Stops);
        Assert.Empty(launcher.Processes);
        Assert.Equal(0, launcher.Starts);
        Assert.Contains(log.Entries, entry => entry.Code == EventCode.HealthProbe);
    }

    [Fact]
    public async Task Real_wrong_identity_after_launch_stops_only_the_owned_child()
    {
        await using var fixture = new LoopbackHealthFixture(ResponseMode.Healthy, ForeignPid);
        using var deadline = new CancellationTokenSource(Deadline);
        using var probe = new HttpHealthProbe();
        var owned = new FakeLauncher();
        var log = new MemoryLog();
        using var service = CreateService(fixture.Origin, new ListeningLauncher(owned, fixture), probe, log);
        var final = await RunToFailureAsync(service, EventCode.IdentityMismatch, deadline.Token);
        var child = Assert.Single(owned.Processes);
        Assert.Equal(child.Id, final.Stops.Single().Identity.ProcessId);
        Assert.Equal(new StopOutcome(true, false, true, 0), final.Stops.Single().Outcome);
        Assert.Equal(1, child.ShutdownRequests);
        Assert.Equal(0, child.Kills);
        Assert.True(child.Disposed);
        Assert.Equal(HealthProbeOutcome.ConnectionRefused, probe.FirstDiagnostic?.Outcome);
        Assert.Equal(new(true, true, ForeignPid),
            await probe.ProbeAsync(fixture.Origin, deadline.Token));
    }

    [Fact]
    public async Task Closed_fixture_releases_its_exclusive_reservation_on_dispose()
    {
        await using var fixture = new LoopbackHealthFixture(ResponseMode.Healthy);
        var port = fixture.Origin.Port;
        await fixture.DisposeAsync();
        using var rebound = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ExclusiveAddressUse = true
        };
        rebound.Bind(new IPEndPoint(IPAddress.Loopback, port));
        Assert.Equal(port, Assert.IsType<IPEndPoint>(rebound.LocalEndPoint).Port);
    }

    private void Report(string control, HttpHealthProbe probe) =>
        output.WriteLine(JsonSerializer.Serialize(new { Control = control, Diagnostic = probe.LastDiagnostic }));

    private static HealthProbeOutcome ExpectedOutcome(ResponseMode mode) => mode switch
    {
        ResponseMode.Healthy => HealthProbeOutcome.Healthy,
        ResponseMode.WrongService => HealthProbeOutcome.InvalidIdentity,
        ResponseMode.HttpError => HealthProbeOutcome.HttpStatus,
        _ => HealthProbeOutcome.InvalidJson
    };

    private static OwnedService CreateService(
        Uri origin, IProcessLauncher launcher, IHealthProbe probe, IServiceLog log) =>
        new(new TrayOptions(Path.Combine(Path.GetTempPath(), "hermes-transport-fixture"),
                origin.Port, @"C:\transport-fixture\unused-service.exe"),
            launcher, probe, new ServiceClock(), log, new TransportObserver(), LifecyclePolicy.Default);

    private static async Task<SupervisionResult> RunToFailureAsync(
        OwnedService service, EventCode code, CancellationToken cancellationToken)
    {
        var worker = service.RunAsync(cancellationToken);
        try
        {
            var error = await Assert.ThrowsAsync<LifecycleFault>(
                () => service.Ready.WaitAsync(cancellationToken));
            Assert.Equal(code, error.Code);
        }
        finally { await worker.WaitAsync(TimeSpan.FromSeconds(25)); }
        return await service.Final;
    }
}

internal sealed class FailureHandler(Exception error) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(error);
}

internal sealed class GatedRefusalHandler : HttpMessageHandler
{
    private readonly TaskCompletionSource _entered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Entered => _entered.Task;
    public void Release() => _release.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _entered.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);
        throw new HttpRequestException(HttpRequestError.ConnectionError, "synthetic-gated-refusal",
            new SocketException((int)SocketError.ConnectionRefused));
    }
}

internal sealed class TransportObserver : IServiceObserver
{
    public void Changed(ServiceSnapshot snapshot) { }
}

internal sealed class ListeningLauncher(
    FakeLauncher owned, LoopbackHealthFixture fixture) : IProcessLauncher
{
    public IOwnedProcess Start(TrayOptions options)
    {
        fixture.BeginListening();
        return owned.Start(options);
    }
}

// Port 0 is exclusively bound for the fixture's entire lifetime.
// Before BeginListening it is reserved but genuinely has no TCP listener.
internal sealed class LoopbackHealthFixture(
    HealthTransportTests.ResponseMode mode, int? processId = null) : IAsyncDisposable
{
    private readonly Socket _listener = BindSocket();
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
    private readonly TaskCompletionSource _accepted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _processId = processId ?? Environment.ProcessId;
    private Task _server = Task.CompletedTask;
    private int _listening;
    private int _disposed;

    public Task Accepted => _accepted.Task;
    public Uri Origin => _listener.LocalEndPoint is IPEndPoint endpoint
        ? new($"http://127.0.0.1:{endpoint.Port}/")
        : throw new InvalidOperationException("The loopback fixture has no bound endpoint.");

    private static Socket BindSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ExclusiveAddressUse = true
        };
        try { socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); return socket; }
        catch { socket.Dispose(); throw; }
    }

    public void BeginListening()
    {
        if (Interlocked.Exchange(ref _listening, 1) != 0)
            throw new InvalidOperationException("The loopback fixture may only listen once.");
        _listener.Listen(4);
        _server = ServeAsync();
    }

    private async Task ServeAsync()
    {
        try
        {
            while (true)
            {
                using var connection = await _listener.AcceptAsync(_stop.Token);
                _accepted.TrySetResult();
                await ServeConnectionAsync(connection, _stop.Token);
            }
        }
        catch (Exception error) when (_stop.IsCancellationRequested &&
            (error is OperationCanceledException or SocketException or ObjectDisposedException or IOException))
        {
            // Expected cancellation is joined by DisposeAsync; other failures escape.
        }
    }

    private async Task ServeConnectionAsync(Socket connection, CancellationToken cancellationToken)
    {
        using var stream = new NetworkStream(connection, ownsSocket: false);
        if (mode == HealthTransportTests.ResponseMode.Silent)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return;
        }
        var request = new byte[1024];
        if (await stream.ReadAsync(request.AsMemory(), cancellationToken) == 0)
            return;
        await stream.WriteAsync(ResponseBytes(), cancellationToken);
    }

    private byte[] ResponseBytes()
    {
        var body = mode == HealthTransportTests.ResponseMode.MalformedJson
            ? "synthetic-not-json"
            : JsonSerializer.Serialize(new
            {
                status = "healthy",
                service = mode == HealthTransportTests.ResponseMode.WrongService
                    ? "synthetic-not-hermes" : "hermes",
                processId = _processId
            });
        var status = mode == HealthTransportTests.ResponseMode.HttpError ? 404 : 200;
        return Encoding.UTF8.GetBytes($"HTTP/1.1 {status} Fixture\r\nContent-Type: application/json\r\n" +
            $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Cancel();
        _listener.Dispose();
        try { await _server.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { _stop.Dispose(); }
    }
}
