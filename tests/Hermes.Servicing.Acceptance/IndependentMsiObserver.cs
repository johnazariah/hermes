namespace Hermes.Servicing.Acceptance;

internal sealed record IndependentObserverReport(
    string SchemaProfile,
    Guid SubscriptionId,
    ObservationSnapshot Snapshot,
    ImmutableArray<EnginePackageBinding> Packages,
    ImmutableArray<Stamp> Writers,
    NativeWriterBinding? ReadyWriter);

internal sealed class IndependentMsiObserver : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly NativeEventSubscription _subscription;
    private readonly EnginePackageBinder _packages;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<uint, Stamp> _clients = [];
    private readonly Dictionary<(uint, string), LogFact> _begins = [];
    private readonly Dictionary<ProcessKey, Stamp> _writers = [];
    private readonly NativeWriterTracker _writerTracker = new();
    private readonly List<LogFact> _facts = [];
    private readonly List<string> _faults = [];
    private readonly List<EnginePackageBinding> _bindings = [];
    private readonly Task _loop;
    private long _lastCursor;
    private long _readyCursor;
    private NativeWriterBinding? _readyWriter;

    internal Guid SubscriptionId { get; } = Guid.NewGuid();
    internal EnginePackageBinder Packages => _packages;

    private IndependentMsiObserver(AcceptanceContext context)
    {
        _packages = new(context);
        try { _subscription = new(); }
        catch { _packages.Dispose(); throw; }
        _loop = LoopAsync(_stop.Token);
    }

    internal static async Task<IndependentMsiObserver> StartAsync(
        AcceptanceContext context, CancellationToken cancellationToken)
    {
        var observer = new IndependentMsiObserver(context);
        try
        {
            if (!DiagnosticLog.Checkpoint(observer.SubscriptionId))
                throw new EvidenceGap("ObserverReadyWriteFailed");
            await observer._ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return observer;
        }
        catch
        {
            await observer.DisposeAsync();
            throw;
        }
    }

    internal void Arm(Stamp client)
    {
        using var process = Process.GetProcessById(client.ProcessId);
        Require.That(Files.Identity(process) == client);
        lock (_sync)
        {
            if (!_clients.TryAdd(checked((uint)client.ProcessId), client))
                throw new EvidenceGap("ClientPidReusedWithinSubscription");
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (var value in _subscription.Drain())
                    Accept(value);
                await Task.Delay(25, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            var code = error is EvidenceGap gap ? gap.Code : ProofFiles.Failure(error);
            lock (_sync) { _faults.Add(code); }
            _ready.TrySetException(new EvidenceGap(code));
        }
    }

    private void Accept(WireEvent value)
    {
        lock (_sync)
        {
            if (value.Cursor <= _lastCursor)
                throw new EvidenceGap("NativeCursorReplayOrReset");
            _lastCursor = value.Cursor;
            if (value.Diagnostic is { Kind: "ObserverReady" } checkpoint &&
                checkpoint.Invocation == SubscriptionId)
            {
                Ready(value, checkpoint);
                return;
            }
            if (_readyCursor == 0 || value.Kind == "Ignore")
                return;
            if (_facts.Count >= 1024)
                throw new EvidenceGap("IndependentRecordLimit");
            AcceptOwned(value);
        }
    }

    private void Ready(WireEvent wire, DiagnosticValue value)
    {
        var current = Key(WindowsPeers.Current());
        if (value.Coordinator != current || value.Writer != current ||
            value.RunId != Guid.Empty || value.Failures != 0 || _readyCursor != 0)
            throw new EvidenceGap("InvalidObserverReady");
        _readyWriter = VerifyWriter(wire, value);
        _readyCursor = wire.Cursor;
        _ready.TrySetResult();
    }

    private void AcceptOwned(WireEvent value)
    {
        switch (value.Kind)
        {
            case "Begin": Begin(value); break;
            case "End": End(value); break;
            case "Outcome": Outcome(value); break;
            case "Diagnostic": Diagnostic(value); break;
            default: throw new EvidenceGap("UnknownWireKind");
        }
    }

    private void Begin(WireEvent value)
    {
        var pid = value.ClientPid ?? throw new EvidenceGap("MissingClientPid");
        var client = _clients.GetValueOrDefault(pid);
        if (client is null)
        {
            if (_begins.Count != 0)
                throw new EvidenceGap("CompetingUnownedTransaction");
            return;
        }
        using var process = Process.GetProcessById(checked((int)pid));
        if (Files.Identity(process) != client)
            throw new EvidenceGap("BeginningClientInstanceMismatch");
        var binding = _packages.Bind(value.PackagePath ?? throw new EvidenceGap("MissingPackagePath"));
        var key = (pid, binding.EnginePath);
        if (_begins.Count != 0 || _begins.ContainsKey(key))
            throw new EvidenceGap("OverlappingTransactionCandidates");
        var fact = new LogFact(value.Cursor, FactKind.Begin, Key(client),
            binding.Source, binding.EnginePath, null, null);
        _begins.Add(key, fact);
        _bindings.Add(binding);
        _facts.Add(fact);
    }

    private void End(WireEvent value)
    {
        var pid = value.ClientPid ?? throw new EvidenceGap("MissingClientPid");
        if (!_clients.ContainsKey(pid))
            return;
        var path = Path.GetFullPath(value.PackagePath ?? throw new EvidenceGap("MissingPackagePath"));
        var key = (pid, path);
        var begin = _begins.GetValueOrDefault(key)
            ?? throw new EvidenceGap("EndWithoutBoundBegin");
        _begins.Remove(key);
        _facts.Add(new(value.Cursor, FactKind.End, begin.Client,
            begin.Package, begin.PackagePath, null, null));
    }

    private void Outcome(WireEvent value)
    {
        var outcome = value.Outcome ?? throw new EvidenceGap("MissingOutcome");
        if (!_packages.Sources.Any(package => package.ProductCode == outcome.ProductCode))
            return;
        _facts.Add(new(value.Cursor, FactKind.Outcome,
            null, null, null, null, outcome));
    }

    private void Diagnostic(WireEvent wire)
    {
        var value = wire.Diagnostic ?? throw new EvidenceGap("MissingDiagnostic");
        var client = _clients.GetValueOrDefault(value.Coordinator.ProcessId);
        if (client is null)
            return;
        if (Key(client) != value.Coordinator)
            throw new EvidenceGap("DiagnosticClientInstanceMismatch");
        var binding = VerifyWriter(wire, value);
        _facts.Add(new(wire.Cursor, FactKind.Diagnostic,
            value.Coordinator, null, null, value, null, binding));
    }

    private NativeWriterBinding VerifyWriter(WireEvent wire, DiagnosticValue value)
    {
        var reporter = wire.Reporter ?? throw new EvidenceGap("MissingNativeReporter");
        if (reporter.ProcessId is 0 or > int.MaxValue)
            throw new EvidenceGap("UnsupportedNativeReporter");
        using var process = Process.GetProcessById(checked((int)reporter.ProcessId));
        _ = process.SafeHandle;
        var actual = Files.Identity(process);
        if (process.HasExited)
            throw new EvidenceGap("DiagnosticWriterObservationLost");
        var binding = _writerTracker.Observe(wire.Cursor, reporter,
            value.Writer, Key(actual), DateTime.UtcNow.ToFileTimeUtc());
        _writers.TryAdd(Key(actual), actual);
        return binding;
    }

    internal IndependentObserverReport Report()
    {
        lock (_sync)
        {
            return new(NativeWriterProof.SchemaProfile, SubscriptionId,
                new(_readyCursor, _facts.ToImmutableArray(), _faults.ToImmutableArray()),
                _bindings.ToImmutableArray(), _writers.Values.ToImmutableArray(), _readyWriter);
        }
    }

    internal async Task WaitOldAsync(
        ExpectedOld expected, CancellationToken cancellationToken)
    {
        await WaitAsync(snapshot =>
        {
            _ = BarrierCorrelation.Old(snapshot, expected);
            return true;
        }, cancellationToken);
    }

    internal async Task<BarrierProof> CompleteAsync(ExpectedOld old,
        ImmutableArray<ExpectedFresh> fresh, CancellationToken cancellationToken)
    {
        BarrierProof? proof = null;
        await WaitAsync(snapshot =>
        {
            proof = BarrierCorrelation.Complete(snapshot, old, fresh);
            return true;
        }, cancellationToken);
        return proof ?? throw new EvidenceGap("MissingCompletedProof");
    }

    private async Task WaitAsync(
        Func<ObservationSnapshot, bool> predicate, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        string missing = "IndependentEvidenceMissing";
        try
        {
            while (true)
            {
                var snapshot = Report().Snapshot;
                if (!snapshot.Faults.IsEmpty)
                    throw new EvidenceGap(snapshot.Faults[0]);
                try { if (predicate(snapshot)) return; }
                catch (EvidenceGap gap) { missing = gap.Code; }
                await Task.Delay(25, budget.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new EvidenceGap(missing); }
    }

    internal static ProcessKey Key(Stamp value) =>
        new(checked((uint)value.ProcessId), value.StartUtcFileTime);

    internal static ProcessKey Key(ProcessInstance value) =>
        new(value.ProcessId, value.CreationFileTime);

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _loop;
        _subscription.Dispose();
        _packages.Dispose();
        _stop.Dispose();
    }
}
