using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using Hermes.Servicing;

namespace Hermes.Installer.Control;

internal sealed class PendingFence(
    GuardExchange exchange, ProcessInstance client, bool ownSession)
{
    internal GuardExchange Exchange { get; } = exchange;
    internal ProcessInstance Client { get; } = client;
    internal bool OwnSession { get; } = ownSession;
    internal TaskCompletionSource<FenceReceipt> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class CallAuthority(
    ControlEndpoint endpoint, ControlPlan plan, IClosedGateOwnership gate,
    FixedInstallRoot root, IControlCallObserver? observer = null,
    IObservationFailureSink? observationFailures = null)
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, PendingFence> _pending = [];
    private readonly List<FenceReceipt> _receipts = [];
    private readonly MsiFailureTrace _msiFailure = new();
    private NativePreflightObservation _nativePreflight = NativePreflightObservation.Missing;
    private ExceptionDispatchInfo? _msiFailureFault;
    private MsiCallContextObservation _callContext = MsiCallContextObservation.Missing;
    private ExceptionDispatchInfo? _firstObservationFault;
    private bool _contextCaptured;
    private bool _active;
    private bool _finished;
    private bool _faulted;
    private int _observationFaults;

    internal ControlEndpoint Endpoint { get; } = endpoint;
    internal ControlPlan Plan { get; } = root.Bind(plan);
    internal uint ObservationFilters =>
        MsiFailureTrace.Filters |
        (observer is IMsiMessageObserver ? ControlObservation.MsiMessageFilters : 0);

    internal void ObserveNativePreflight(string text)
    {
        lock (_sync)
        {
            if (!_active || _finished ||
                _nativePreflight.Reason != NativePreflightReason.None ||
                _receipts.Any(receipt => receipt.OwnSession &&
                    receipt.Phase == GuardPhase.Preflight))
                return;
            if (NativePreflightObservation.Decode(text) is { } value)
                _nativePreflight = value;
        }
    }

    internal void ObserveMsiFailure(
        uint message, uint record, IMsiFailureRecords records)
    {
        lock (_sync)
        {
            if (!_active || _finished || _msiFailureFault is not null)
                return;
            try { _msiFailure.Observe(message, record, records); }
            catch (Exception error)
            {
                // Stop subsequent callback reads, but retain the first programming
                // fault across caller-context capture and MSI callbacks.
                _msiFailureFault = ExceptionDispatchInfo.Capture(error);
                _firstObservationFault ??= _msiFailureFault;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    internal void ObserveMsi(uint message, uint record)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Native MSI records require Windows.");
        if (observer is not IMsiMessageObserver messages)
            return;
        try
        {
            if (MsiObservation.Read(message, record) is not { } value)
                return;
            DiagnosticLog.Call(Endpoint, value.ProductCode ?? Plan.ProductCode,
                value.Kind.ToString(), value.Status, value.Action);
            ControlObservation.EmitMsi(messages, value, ObservationFailed);
        }
        catch { ObservationFailed("MsiRecord"); }
    }

    private void ObservationFailed(string point)
    {
        Interlocked.Increment(ref _observationFaults);
        try { observationFailures?.Record(point); }
        catch { Interlocked.Increment(ref _observationFaults); }
    }

    private bool Live => _active && !_finished && !_faulted && gate.IsHeldAndClosed;

    internal uint InvokeObserved(IMsiCallContextTokens tokens, Func<uint> invoke)
    {
        CaptureCallContext(tokens);
        return invoke();
    }

    private void CaptureCallContext(IMsiCallContextTokens tokens)
    {
        lock (_sync)
        {
            if (!_active || _finished || _contextCaptured)
                return;
            _contextCaptured = true;
            try { _callContext = MsiCallContextObservation.Read(tokens); }
            catch (Exception error)
            {
                // Optional observation cannot prevent the native call. Programming
                // faults retain identity and are handed off after its receipt.
                _firstObservationFault ??= ExceptionDispatchInfo.Capture(error);
            }
        }
    }

    internal InstallerCallReceipt Execute(
        Func<IDisposable> openUi, Func<uint> invoke,
        Action<InstallerCallReceipt> publish)
    {
        Begin();
        var entered = false;
        uint? code = null;
        try
        {
            using var ui = openUi();
            entered = true;
            code = invoke();
        }
        catch { Fault(); }
        var receipt = End(entered, code);
        publish(receipt);
        if (!receipt.ControlFaulted)
            ThrowMsiFailure();
        return receipt;
    }

    private void ThrowMsiFailure()
    {
        lock (_sync)
        {
            _firstObservationFault?.Throw();
        }
    }

    internal void Begin()
    {
        lock (_sync)
        {
            if (_active || _finished || !gate.IsHeldAndClosed)
                throw new InvalidOperationException("The guarded call cannot start.");
            _active = true;
        }
    }

    internal PendingFence? Prepare(GuardExchange exchange, ProcessInstance client)
    {
        lock (_sync)
        {
            var role = Classify(exchange);
            if (!Live || role is null || exchange.Challenge == Guid.Empty ||
                _pending.ContainsKey(exchange.Challenge) || !Ordered(exchange, role.Value))
                return null;
            var pending = new PendingFence(exchange, client, role.Value);
            _pending.Add(exchange.Challenge, pending);
            return pending;
        }
    }

    private bool? Classify(GuardExchange exchange)
    {
        if (exchange.RunId != Endpoint.RunId ||
            exchange.Abi != HermesServicingProtocol.AdmissionAbi ||
            exchange.Request is not { } request ||
            !root.IsAppDirectory(request.AppDirectory))
            return null;
        if (request.ProductCode == Plan.ProductCode && request.UpgradingProductCode is null &&
            exchange.PropertyRun == Endpoint.RunId && request.Operation == Plan.Operation)
            return true;
        return request.Operation == MaintenanceKind.Uninstall &&
            request.UpgradingProductCode == Plan.ProductCode &&
            Plan.NestedRemovalProducts.Contains(request.ProductCode) &&
            (exchange.PropertyRun is null || exchange.PropertyRun == Endpoint.RunId)
                ? false : null;
    }

    private bool Ordered(GuardExchange exchange, bool ownSession) =>
        exchange.Request.Phase switch
        {
            GuardPhase.Preflight => true,
            GuardPhase.Transaction => _receipts.Any(receipt =>
                receipt.OwnSession == ownSession &&
                receipt.ProductCode == exchange.Request.ProductCode &&
                receipt.Phase == GuardPhase.Preflight),
            _ => false
        };

    internal bool Observe(FenceSignal signal)
    {
        var description = Describe(signal);
        if (description is { } before)
            ControlObservation.Emit(observer,
                ControlObservationPoint.BeforeCanonicalObservation, before, ObservationFailed);
        var accepted = AcceptObservation(signal);
        if (accepted && description is { } after)
            ControlObservation.Emit(observer,
                ControlObservationPoint.CanonicalObservationAccepted, after, ObservationFailed);
        return accepted;
    }

    private ControlFenceObservation? Describe(FenceSignal signal)
    {
        lock (_sync)
        {
            return signal.RunId == Endpoint.RunId &&
                _pending.TryGetValue(signal.Challenge, out var pending) &&
                pending.Exchange.Request.Phase == signal.Phase
                    ? new(Endpoint.RunId, signal.Phase,
                        pending.Exchange.Request.ProductCode,
                        pending.OwnSession, pending.Client)
                    : null;
        }
    }

    private bool AcceptObservation(FenceSignal signal)
    {
        lock (_sync)
        {
            if (!Live || signal.RunId != Endpoint.RunId ||
                !_pending.TryGetValue(signal.Challenge, out var pending) ||
                pending.Completion.Task.IsCompleted ||
                pending.Exchange.Request.Phase != signal.Phase)
                return false;
            var receipt = new FenceReceipt(Endpoint.RunId, signal.Challenge, signal.Phase,
                pending.Exchange.Request.ProductCode, pending.OwnSession, pending.Client);
            _receipts.Add(receipt);
            return pending.Completion.TrySetResult(receipt);
        }
    }

    internal void Release(Guid challenge)
    {
        lock (_sync)
            _pending.Remove(challenge);
    }

    internal void Fault()
    {
        lock (_sync)
            _faulted = true;
    }

    internal InstallerCallReceipt End(bool entered, uint? code)
    {
        lock (_sync)
        {
            _active = false;
            _finished = true;
            foreach (var pending in _pending.Values)
                pending.Completion.TrySetCanceled();
            return new(Endpoint.RunId, entered, code.HasValue, code, _faulted,
                _receipts.ToImmutableArray())
            {
                ObservationFaults = Volatile.Read(ref _observationFaults),
                MsiFailure = _msiFailure.Snapshot,
                NativePreflight = _nativePreflight,
                CallContext = _callContext
            };
        }
    }

}
