using System.Collections.Immutable;
using Hermes.Tray;

namespace Hermes.Tray.Smoke;

internal sealed record SmokeReport(
    bool Passed, string? Failure, int? ProcessId, string? WebViewRuntime,
    StopOutcome? Stop, ImmutableArray<AssetProof> Assets, ImmutableArray<RouteProof> Routes,
    bool Cleaned = false, string? RetainedSyntheticDirectory = null)
{
    public bool InstalledTrayLaunched { get; init; }
    public string? TrayExecutablePath { get; init; }
    public int? TrayProcessId { get; init; }
    public long? TrayStartUtcTicks { get; init; }
    public long? ServiceStartUtcTicks { get; init; }
    public string? InstalledProductCode { get; init; }
    public string? InstalledVersion { get; init; }
    public Guid? TrayModuleVersionId { get; init; }
    public ImmutableArray<InstalledFileProof> InstalledFiles { get; init; } = [];
    public bool InstalledSourceUnchanged { get; init; }
    public bool ReadyReached { get; init; }
    public bool Continued { get; init; }
    public SupervisionResult? Supervision { get; init; }
    public HealthProbeDiagnostic? FirstHealthProbe { get; init; }
    public HealthProbeDiagnostic? LastHealthProbe { get; init; }
    public ImmutableArray<OwnershipProof> OwnershipChecks { get; init; } = [];
    public NativeAuthenticationProof? Authentication { get; init; }
    public static SmokeReport Empty { get; } = new(false, "Native smoke did not complete.", null, null, null, [], []);
}

internal sealed class SmokeContext : ApplicationContext, IServiceObserver, IBrowserFailureNotification
{
    private readonly TrayOptions _options;
    private readonly CancellationTokenSource _deadline;
    private readonly HttpHealthProbe _health = new();
    private readonly MainWindow _window;
    private readonly OwnedService _service;
    private readonly SmokeOwnershipOracle _oracle;
    private readonly StableProcessVerifier _identity;
    private readonly Func<SmokeReport, CancellationToken, Task>? _hold;
    private readonly Action<SmokeReport>? _checkpoint;
    private Task _worker = Task.CompletedTask;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public SmokeReport Report { get; private set; } = SmokeReport.Empty;

    public SmokeContext(
        TrayOptions options, TimeSpan timeout,
        Func<SmokeReport, CancellationToken, Task>? hold = null,
        Action<SmokeReport>? checkpoint = null)
    {
        _options = options;
        _hold = hold;
        _checkpoint = checkpoint;
        _deadline = new(timeout);
        var log = new JsonServiceLog(options);
        var external = new GuardedExternalOpener(options, new ShellExternalLauncher(), log, this);
        _service = new(options, new NativeProcessLauncher(log), _health,
            new ServiceClock(), log, this, LifecyclePolicy.Default);
        _window = new(options, external, _service, log);
        _identity = new(_service, options.ServicePath, new WindowsProcessIdentitySource());
        _oracle = new(_identity);
        Application.Idle += Start;
    }

    public void Changed(ServiceSnapshot snapshot) => _oracle.Changed(snapshot);

    public void BrowserFailed()
    {
        const string failure = "External browser launch failed; native smoke cannot use a browser fallback.";
        _oracle.Invalidate(failure);
        Report = Report with { Passed = false, Failure = failure };
    }

    private void Start(object? sender, EventArgs args)
    {
        Application.Idle -= Start;
        _ = _window.Handle;
        Completion = RunAsync();
    }

    private async Task RunAsync()
    {
        try { await MeasureAsync(); }
        catch (Exception error)
        {
            Report = Report with { Passed = false, Failure = SafeFailure.Describe(error) };
            Checkpoint();
        }
        finally
        {
            try
            {
                Checkpoint();
                await FinishAsync();
            }
            finally { ExitThread(); }
        }
    }

    private void Checkpoint()
    {
        try { _checkpoint?.Invoke(Report); }
        catch (Exception error)
        {
            Report = Report with { Passed = false, Failure = SafeFailure.Describe(error) };
        }
    }

    private async Task MeasureAsync()
    {
        _worker = _service.RunAsync(_deadline.Token);
        var pid = await _service.Ready.WaitAsync(_deadline.Token);
        var identity = _oracle.Bind(_service.Snapshot);
        if (identity.ProcessId != pid)
            throw new InvalidOperationException("Initial readiness no longer belongs to the smoke child.");
        Report = Report with { ProcessId = identity.ProcessId };
        var proof = await ReactProof.RunAsync(_options, _window, _oracle, _deadline.Token);
        Report = Report with
        {
            Failure = null, WebViewRuntime = proof.Runtime,
            Assets = proof.Assets, Routes = proof.Routes,
            Authentication = proof.Authentication,
            ServiceStartUtcTicks = _identity.Bound?.StartUtcTicks
        };
        await HoldAsync(_deadline.Token);
    }

    private async Task HoldAsync(CancellationToken cancellationToken)
    {
        if (_hold is not { } hold)
            return;
        await _oracle.ProveAsync("installed:servicing-hold",
            token => hold(Report, token), cancellationToken);
        Report = Report with { ReadyReached = true, Continued = true };
    }

    private async Task FinishAsync()
    {
        await PrepareStopAsync();
        await StopNativeAccessAsync();
        _deadline.Cancel();
        await StopWorkerAsync();
        var final = await _service.Final;
        var verdict = _oracle.Evaluate(final, Report.Failure is null);
        Report = Report with
        {
            Passed = verdict.Passed, Failure = Report.Failure ?? verdict.Failure,
            ProcessId = verdict.Identity?.ProcessId, Stop = verdict.Stop?.Outcome,
            Supervision = final, OwnershipChecks = verdict.Checks,
            FirstHealthProbe = _health.FirstDiagnostic, LastHealthProbe = _health.LastDiagnostic
            , ServiceStartUtcTicks = _identity.Bound?.StartUtcTicks
        };
        ReleaseNativeResources();
    }

    private async Task PrepareStopAsync()
    {
        try
        {
            if (Report.Failure is null)
                await _oracle.PrepareStopAsync(_deadline.Token);
        }
        catch (Exception error)
        {
            Report = Report with { Passed = false, Failure = SafeFailure.Describe(error) };
        }
    }

    private async Task StopWorkerAsync()
    {
        try { await _worker; }
        catch (Exception error)
        {
            Report = Report with { Passed = false, Failure = SafeFailure.Describe(error) };
        }
    }

    private async Task StopNativeAccessAsync()
    {
        try { await _window.StopAccessAsync(CancellationToken.None); }
        catch (Exception error)
        {
            Report = Report with { Passed = false, Failure = SafeFailure.Describe(error) };
        }
    }

    private void ReleaseNativeResources()
    {
        try { _window.Shutdown(); }
        catch (Exception error)
        {
            Report = Report with { Passed = false, Failure = SafeFailure.Describe(error) };
        }
        finally
        {
            _service.Dispose();
            _health.Dispose();
            _deadline.Dispose();
        }
    }
}
