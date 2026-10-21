using System.Text.Json;
using System.Runtime.InteropServices;
using Hermes.Installer.Control;
using Hermes.Servicing;
using Xunit;

namespace Hermes.Installer.Control.Tests;

public sealed class AuthorityTests
{
    private const uint InfoMessage = 0x04000000;
    private const string DiagnosticSecret = "SYNTHETIC_CALLBACK_SECRET";
    private static readonly Guid Product = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Previous = new("22222222-2222-2222-2222-222222222222");
    private static readonly string Root = FixedRootFixture.Policy.AppDirectory;

    [Fact]
    public void Public_properties_do_not_complete_private_confirmation()
    {
        var authority = Create();
        authority.Begin();
        var pending = Assert.IsType<PendingFence>(
            authority.Prepare(Request(authority, GuardPhase.Preflight), SyntheticClient()));
        Assert.False(pending.Completion.Task.IsCompleted);
        Assert.False(authority.End(true, 0).MayEvaluateRecovery);
    }

    [Fact]
    public void Own_fence_and_returned_success_enable_only_outcome_evaluation()
    {
        var authority = Create();
        authority.Begin();
        Confirm(authority, Request(authority, GuardPhase.Preflight));
        Confirm(authority, Request(authority, GuardPhase.Transaction));
        var receipt = authority.End(true, 0);
        Assert.True(receipt.MayEvaluateRecovery);
        Assert.Equal(2, receipt.Fences.Length);
    }

    [Theory]
    [InlineData(1618u)]
    [InlineData(1605u)]
    [InlineData(3010u)]
    [InlineData(1603u)]
    public void Non_success_never_satisfies_recovery(uint code)
    {
        var authority = Create();
        authority.Begin();
        Confirm(authority, Request(authority, GuardPhase.Preflight));
        Confirm(authority, Request(authority, GuardPhase.Transaction));
        Assert.False(authority.End(true, code).MayEvaluateRecovery);
    }

    [Fact]
    public void Transaction_requires_own_preflight()
    {
        var authority = Create();
        authority.Begin();
        Assert.Null(authority.Prepare(Request(authority, GuardPhase.Transaction), SyntheticClient()));
    }

    [Fact]
    public void Nested_fences_do_not_substitute_for_own_fences()
    {
        var authority = Create();
        authority.Begin();
        Confirm(authority, Nested(authority, GuardPhase.Preflight));
        Confirm(authority, Nested(authority, GuardPhase.Transaction));
        var receipt = authority.End(true, 0);
        Assert.False(receipt.MayEvaluateRecovery);
        Assert.All(receipt.Fences, fence => Assert.False(fence.OwnSession));
    }

    [Fact]
    public void Wrong_property_run_is_refused()
    {
        var authority = Create();
        authority.Begin();
        var request = Request(authority, GuardPhase.Preflight) with { PropertyRun = Guid.NewGuid() };
        Assert.Null(authority.Prepare(request, SyntheticClient()));
    }

    [Fact]
    public void Wrong_root_and_unrecorded_nested_product_are_refused()
    {
        var authority = Create();
        authority.Begin();
        var request = Request(authority, GuardPhase.Preflight);
        Assert.Null(authority.Prepare(request with
        {
            Request = request.Request with { AppDirectory = Path.GetFullPath("other-root") }
        }, SyntheticClient()));
        var nested = Nested(authority, GuardPhase.Preflight);
        Assert.Null(authority.Prepare(nested with
        {
            Request = nested.Request with { ProductCode = Guid.NewGuid() }
        }, SyntheticClient()));
    }

    [Fact]
    public void Revoked_run_and_unknown_challenge_do_not_confirm()
    {
        var authority = Create();
        authority.Begin();
        var request = Request(authority, GuardPhase.Preflight);
        var pending = Assert.IsType<PendingFence>(authority.Prepare(request, SyntheticClient()));
        Assert.False(authority.Observe(new(Guid.NewGuid(), GuardPhase.Preflight, request.Challenge)));
        Assert.False(authority.Observe(new(authority.Endpoint.RunId, GuardPhase.Preflight, Guid.NewGuid())));
        Assert.False(pending.Completion.Task.IsCompleted);
    }

    [Fact]
    public void Lost_gate_ownership_denies_existing_pending_request()
    {
        var gate = new FakeGate();
        var authority = Create(gate);
        authority.Begin();
        var request = Request(authority, GuardPhase.Preflight);
        Assert.NotNull(authority.Prepare(request, SyntheticClient()));
        gate.LoseOwnership();
        Assert.False(authority.Observe(
            new(authority.Endpoint.RunId, GuardPhase.Preflight, request.Challenge)));
        Assert.False(authority.End(true, 0).MayEvaluateRecovery);
    }

    [Fact]
    public void Completed_call_cannot_admit_a_later_session()
    {
        var authority = Create();
        authority.Begin();
        authority.End(true, 0);
        Assert.Null(authority.Prepare(Request(authority, GuardPhase.Preflight), SyntheticClient()));
    }

    [Fact]
    public void Native_signal_round_trips_and_rejects_foreign_messages()
    {
        var signal = new FenceSignal(Guid.NewGuid(), GuardPhase.Transaction, Guid.NewGuid());
        Assert.Equal(signal, FenceSignal.Decode(signal.Encode()));
        Assert.Null(FenceSignal.Decode("ordinary MSI information"));
        Assert.Null(FenceSignal.Decode("Hermes.A2.Fence/1/bad/Transaction/bad"));
    }

    [Fact]
    public void Missing_control_descriptor_members_are_rejected()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize("{}"u8, ControlJson.Default.ControlEndpoint));
    }

    [Fact]
    public async Task Oversized_packet_is_rejected_before_body_allocation()
    {
        using var stream = new MemoryStream([0xff, 0xff, 0xff, 0x7f]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ControlPackets.ReadAsync(stream, ControlJson.Default.ControlReply, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Observer_and_sink_failures_are_recorded_without_faulting_authority(bool sinkThrows)
    {
        var failures = new FailureSink(sinkThrows);
        var authority = Create(observer: new BrokenObserver(), failures: failures);
        Assert.Equal(ControlObservation.MsiMessageFilters | MsiFailureTrace.Filters,
            authority.ObservationFilters);
        authority.Begin();
        Confirm(authority, Request(authority, GuardPhase.Preflight));
        Confirm(authority, Request(authority, GuardPhase.Transaction));
        var receipt = authority.End(true, 0);
        Assert.False(receipt.ControlFaulted);
        Assert.Equal(2, receipt.Fences.Length);
        Assert.Equal(4, failures.Calls);
        Assert.Equal(sinkThrows ? 8 : 4, receipt.ObservationFaults);
    }

    [Fact]
    public void Msi_failure_capture_is_live_bounded_and_never_establishes_a_fence()
    {
        var authority = Create();
        var records = new FailureRecords();
        authority.ObserveMsiFailure(MsiFailureTrace.ActionStartMessage, 1, records);
        Assert.Equal(0, records.Reads);
        authority.Begin();
        CaptureFailure(authority, records);
        var receipt = authority.End(true, 1603);
        authority.ObserveMsiFailure(MsiFailureTrace.ErrorMessage, 1, records);
        Assert.Equal(2, records.Reads);
        Assert.Equal(new(MsiFailureAction.HermesPreflight, 1723, false), receipt.MsiFailure);
        Assert.Empty(receipt.Fences);
        Assert.False(receipt.MayEvaluateRecovery);
        Assert.False(receipt.ControlFaulted);
        Assert.Equal(1603u, receipt.MsiExitCode);
    }

    [Fact]
    public void First_numeric_error_freezes_action_and_does_not_read_later_records()
    {
        var authority = Create();
        authority.Begin();
        CaptureFailure(authority, new FailureRecords());
        var later = new FailureRecords(failure: new InvalidOperationException("must not read"));
        CaptureFailure(authority, later);
        Assert.Equal(0, later.Reads);
        Assert.Equal(new(MsiFailureAction.HermesPreflight, 1723, false),
            authority.End(true, 1603).MsiFailure);
    }

    [Fact]
    public void Unrelated_messages_do_not_read_records_or_change_private_confirmation()
    {
        var authority = Create();
        Assert.Equal(MsiFailureTrace.Filters, authority.ObservationFilters);
        authority.Begin();
        var request = Request(authority, GuardPhase.Preflight);
        var pending = Assert.IsType<PendingFence>(authority.Prepare(request, SyntheticClient()));
        var records = new FailureRecords(failure: new InvalidOperationException("must not read"));
        authority.ObserveMsiFailure(0x04000000, 1, records);
        Assert.Equal(0, records.Reads);
        Assert.False(pending.Completion.Task.IsCompleted);
        Assert.Empty(authority.End(true, 1603).Fences);
    }

    [Theory]
    [InlineData("1399")]
    [InlineData("hermespreflight")]
    [InlineData("HermesPreflight ")]
    [InlineData("SYNTHETIC_SECRET C:\\owner\\token")]
    public void Only_exact_source_action_names_are_retained(string name)
    {
        var authority = Create();
        authority.Begin();
        CaptureFailure(authority, new FailureRecords(name));
        var observed = Assert.IsType<MsiFailureObservation>(authority.End(true, 1603).MsiFailure);
        Assert.Equal(MsiFailureAction.Unknown, observed.Action);
        Assert.Equal(1723, observed.ErrorId);
        Assert.DoesNotContain("SYNTHETIC_SECRET", JsonSerializer.Serialize(observed));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    [InlineData(65536, true)]
    public void Missing_or_out_of_bound_error_is_explicit_not_native_success(int? code, bool failed)
    {
        var authority = Create();
        authority.Begin();
        CaptureFailure(authority, new FailureRecords(errorId: code));
        var receipt = authority.End(true, 1603);
        Assert.Equal(new(MsiFailureAction.HermesPreflight, null, failed), receipt.MsiFailure);
        Assert.Equal(1603u, receipt.MsiExitCode);
        Assert.False(receipt.MayEvaluateRecovery);
    }

    [Fact]
    public void Expected_record_failure_is_visible_without_changing_original_result()
    {
        var authority = Create();
        authority.Begin();
        CaptureFailure(authority,
            new FailureRecords(failure: new System.ComponentModel.Win32Exception(5, "SECRET")));
        var receipt = authority.End(true, 1603);
        Assert.Equal(new(MsiFailureAction.Unknown, null, true), receipt.MsiFailure);
        Assert.False(receipt.ControlFaulted);
        Assert.False(receipt.MayEvaluateRecovery);
        Assert.Equal(1603u, receipt.MsiExitCode);
    }

    [Theory]
    [InlineData(MsiFailureTrace.ActionStartMessage)]
    [InlineData(MsiFailureTrace.ErrorMessage)]
    public void Callback_programming_fault_is_handed_off_after_native_completion(uint message)
    {
        var authority = Create();
        var expected = new InvalidOperationException(DiagnosticSecret);
        var records = new FailureRecords(failure: expected);
        var later = new FailureRecords(failure: new ArgumentException("later fault"));
        PendingFence? pending = null;
        InstallerCallReceipt? receipt = null;
        using var call = new CallbackCall(() =>
            pending = FaultDuringCallback(authority, message, records, later));
        var actual = Assert.Throws<InvalidOperationException>(() =>
            authority.Execute(() => call, call.Invoke, value => receipt = value));
        AssertFaultHandoff(expected, actual, call, pending,
            Assert.IsType<InstallerCallReceipt>(receipt));
        Assert.Equal(1, records.Reads);
        Assert.Equal(0, later.Reads);
        AssertCallbackClosed(authority, records, pending);
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(1603u, false)]
    [InlineData(0u, true)]
    [InlineData(1603u, true)]
    public void Callback_Win32_read_failure_preserves_native_exit_and_real_fences(
        uint code, bool fences)
    {
        var authority = Create();
        var records = new FailureRecords(
            failure: new System.ComponentModel.Win32Exception(5, DiagnosticSecret));
        using var call = new CallbackCall(
            () => CaptureAndConfirmCallback(authority, records, fences), code);
        var receipt = authority.Execute(() => call, call.Invoke, static _ => { });
        Assert.Equal(new(MsiFailureAction.Unknown, null, true), receipt.MsiFailure);
        Assert.Equal(code, receipt.MsiExitCode);
        Assert.Equal(fences ? 2 : 0, receipt.Fences.Length);
        Assert.Equal(code == 0 && fences, receipt.MayEvaluateRecovery);
        Assert.False(receipt.ControlFaulted);
        Assert.True(receipt.ApiEntered && receipt.ApiReturned);
        Assert.True(call.Returned && call.Disposed);
        AssertCallbackClosed(authority, records);
    }

    [Fact]
    public void Callback_live_window_and_INFO_confirmation_are_unchanged()
    {
        var authority = Create();
        var outside = new FailureRecords(failure: new InvalidOperationException("outside"));
        AssertCallbackClosed(authority, outside);
        var records = new FailureRecords(errorId: null);
        using var call = new CallbackCall(
            () => CaptureAndConfirmCallback(authority, records, true), 0);
        var receipt = authority.Execute(() => call, call.Invoke, static _ => { });
        Assert.Equal(0, outside.Reads);
        Assert.Equal(2, records.Reads);
        Assert.Equal(new(MsiFailureAction.HermesPreflight, null, false), receipt.MsiFailure);
        Assert.Equal(2, receipt.Fences.Length);
        Assert.True(receipt.MayEvaluateRecovery);
        AssertCallbackClosed(authority, records);
        AssertCallbackClosed(authority, outside);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Invocation_and_disposal_failures_preserve_faulted_receipts(
        bool diagnosticFault, bool duringDisposal)
    {
        var authority = Create();
        var records = new FailureRecords(failure:
            diagnosticFault ? new InvalidOperationException(DiagnosticSecret) : null);
        var later = new FailureRecords();
        var expected = new IOException("synthetic native failure");
        PendingFence? pending = null;
        InstallerCallReceipt? published = null;
        using var call = new CallbackCall(() =>
            pending = FaultDuringCallback(
                authority, MsiFailureTrace.ErrorMessage, records, later),
            code: 0, failure: duringDisposal ? null : expected,
            disposalFailure: duringDisposal ? expected : null);
        var receipt = authority.Execute(
            () => call, call.Invoke, value => published = value);
        Assert.Same(receipt, published);
        AssertFaultedCall(receipt, call, pending, duringDisposal);
        AssertCallbackClosed(authority, records, pending);
    }

    [Fact]
    public void Open_failure_still_ends_and_publishes_faulted_receipt()
    {
        var authority = Create();
        var invoked = false;
        InstallerCallReceipt? published = null;
        var receipt = authority.Execute(
            () => throw new IOException("synthetic UI open failure"),
            () => { invoked = true; return 0; },
            value => published = value);
        Assert.Same(receipt, published);
        Assert.False(invoked);
        Assert.False(receipt.ApiEntered);
        Assert.False(receipt.ApiReturned);
        Assert.Null(receipt.MsiExitCode);
        Assert.True(receipt.ControlFaulted);
        Assert.False(receipt.MayEvaluateRecovery);
        Assert.Empty(receipt.Fences);
        AssertCallbackClosed(authority, new FailureRecords());
    }

    [Fact]
    public void Existing_control_fault_is_not_replaced_by_deferred_callback_fault()
    {
        var authority = Create();
        var records = new FailureRecords(failure: new InvalidOperationException(DiagnosticSecret));
        using var call = new CallbackCall(() =>
        {
            Assert.Equal(0, DispatchCallback(authority, MsiFailureTrace.ErrorMessage, records));
            authority.Fault();
        }, 0);
        InstallerCallReceipt? published = null;
        var receipt = authority.Execute(
            () => call, call.Invoke, value => published = value);
        Assert.Same(receipt, published);
        Assert.True(receipt.ApiEntered && receipt.ApiReturned);
        Assert.True(receipt.ControlFaulted);
        Assert.Equal(0u, receipt.MsiExitCode);
        Assert.False(receipt.MayEvaluateRecovery);
        Assert.True(call.Returned && call.Disposed);
    }

    [Theory]
    [InlineData(InfoMessage)]
    [InlineData(MsiFailureTrace.ActionStartMessage)]
    public void Existing_callback_catch_still_returns_zero_without_new_handoff(uint message)
    {
        var authority = Create();
        var records = new FailureRecords();
        var legacy = new InvalidOperationException("legacy callback failure");
        using var call = new CallbackCall(() =>
            Assert.Equal(0, DispatchCallback(authority, message, records,
                _ => throw legacy, (_, _, _) => throw legacy)));
        var receipt = authority.Execute(() => call, call.Invoke, static _ => { });
        Assert.True(receipt.ApiReturned);
        Assert.False(receipt.ControlFaulted);
        Assert.Equal(1603u, receipt.MsiExitCode);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1603u)]
    public void Diagnostic_error_does_not_replace_native_result_or_canonical_fences(uint code)
    {
        var authority = Create();
        authority.Begin();
        CaptureFailure(authority, new FailureRecords());
        Confirm(authority, Request(authority, GuardPhase.Preflight));
        Confirm(authority, Request(authority, GuardPhase.Transaction));
        var receipt = authority.End(true, code);
        Assert.Equal(code, receipt.MsiExitCode);
        Assert.Equal(2, receipt.Fences.Length);
        Assert.Equal(code == 0, receipt.MayEvaluateRecovery);
    }

    private static int DispatchCallback(
        CallAuthority authority, uint message, IMsiFailureRecords records,
        Func<uint, string>? readText = null,
        Action<CallAuthority, uint, uint>? observeMsi = null)
    {
        var context = GCHandle.Alloc(authority);
        try
        {
            return ExternalUiScope.Dispatch(GCHandle.ToIntPtr(context), message, 1, records,
                readText ?? (static _ => ""),
                observeMsi ?? (static (_, _, _) => { }));
        }
        finally { context.Free(); }
    }

    private static void CaptureAndConfirmCallback(
        CallAuthority authority, IMsiFailureRecords records, bool fences)
    {
        Assert.Equal(0, DispatchCallback(authority, MsiFailureTrace.ActionStartMessage, records));
        Assert.Equal(0, DispatchCallback(authority, MsiFailureTrace.ErrorMessage, records));
        if (!fences)
            return;
        ConfirmCallback(authority, GuardPhase.Preflight);
        ConfirmCallback(authority, GuardPhase.Transaction);
    }

    private static void ConfirmCallback(CallAuthority authority, GuardPhase phase)
    {
        var exchange = Request(authority, phase);
        var pending = Assert.IsType<PendingFence>(authority.Prepare(exchange, SyntheticClient()));
        var signal = new FenceSignal(exchange.RunId, phase, exchange.Challenge);
        var records = new FailureRecords(failure: new InvalidOperationException("must not read"));
        Assert.Equal(0, DispatchCallback(authority, InfoMessage, records));
        Assert.False(pending.Completion.Task.IsCompleted);
        Assert.Equal(1, DispatchCallback(
            authority, InfoMessage | 1u, records, _ => signal.Encode()));
        Assert.Equal(0, DispatchCallback(authority, InfoMessage, records, _ => signal.Encode()));
        Assert.Equal(0, records.Reads);
        Assert.True(pending.Completion.Task.IsCompletedSuccessfully);
        authority.Release(exchange.Challenge);
    }

    private static PendingFence FaultDuringCallback(
        CallAuthority authority, uint message,
        IMsiFailureRecords records, IMsiFailureRecords later)
    {
        Assert.Equal(0, DispatchCallback(authority, message, records));
        ConfirmCallback(authority, GuardPhase.Preflight);
        var pending = Assert.IsType<PendingFence>(
            authority.Prepare(Request(authority, GuardPhase.Transaction), SyntheticClient()));
        Assert.Equal(0, DispatchCallback(authority, message, later));
        Assert.False(pending.Completion.Task.IsCompleted);
        return pending;
    }

    private static void AssertFaultedCall(
        InstallerCallReceipt receipt, CallbackCall call,
        PendingFence? pending, bool returned)
    {
        Assert.True(receipt.ApiEntered);
        Assert.Equal(returned, receipt.ApiReturned);
        Assert.Equal(returned ? 0u : (uint?)null, receipt.MsiExitCode);
        Assert.True(receipt.ControlFaulted);
        Assert.False(receipt.MayEvaluateRecovery);
        Assert.Equal(GuardPhase.Preflight, Assert.Single(receipt.Fences).Phase);
        Assert.Equal(returned, call.Returned);
        Assert.True(call.Disposed);
        Assert.True(pending is { Completion.Task.IsCanceled: true });
        Assert.DoesNotContain(DiagnosticSecret,
            JsonSerializer.Serialize(receipt, ControlJson.Default.InstallerCallReceipt));
    }

    private static void AssertFaultHandoff(
        Exception expected, Exception actual, CallbackCall call,
        PendingFence? pending, InstallerCallReceipt receipt)
    {
        Assert.Same(expected, actual);
        Assert.Contains(nameof(FailureRecords), Assert.IsType<string>(actual.StackTrace));
        Assert.True(call.Returned && call.Disposed);
        Assert.True(pending is { Completion.Task.IsCanceled: true });
        Assert.True(receipt.ApiEntered && receipt.ApiReturned);
        Assert.Equal(1603u, receipt.MsiExitCode);
        Assert.Equal(GuardPhase.Preflight, Assert.Single(receipt.Fences).Phase);
        Assert.False(receipt.ControlFaulted);
        Assert.False(receipt.MayEvaluateRecovery);
        Assert.DoesNotContain(DiagnosticSecret,
            JsonSerializer.Serialize(receipt, ControlJson.Default.InstallerCallReceipt));
    }

    private static void AssertCallbackClosed(
        CallAuthority authority, FailureRecords records, PendingFence? pending = null)
    {
        var reads = records.Reads;
        Assert.Equal(0, DispatchCallback(authority, MsiFailureTrace.ActionStartMessage, records));
        Assert.Equal(0, DispatchCallback(authority, MsiFailureTrace.ErrorMessage, records));
        Assert.Equal(reads, records.Reads);
        Assert.Null(authority.Prepare(Request(authority, GuardPhase.Preflight), SyntheticClient()));
        if (pending is not { Exchange: var exchange })
            return;
        var signal = new FenceSignal(exchange.RunId, exchange.Request.Phase, exchange.Challenge);
        Assert.Equal(0, DispatchCallback(authority, InfoMessage, records, _ => signal.Encode()));
    }

    private sealed class CallbackCall(
        Action invoke, uint code = 1603, Exception? failure = null,
        Exception? disposalFailure = null) : IDisposable
    {
        private bool _returned;
        private bool _disposed;
        internal bool Returned => _returned;
        internal bool Disposed => _disposed;

        internal uint Invoke()
        {
            invoke();
            if (failure is { } error)
                throw error;
            _returned = true;
            return code;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (disposalFailure is { } error)
                throw error;
        }
    }

    private static void CaptureFailure(CallAuthority authority, IMsiFailureRecords records)
    {
        authority.ObserveMsiFailure(MsiFailureTrace.ActionStartMessage, 1, records);
        authority.ObserveMsiFailure(MsiFailureTrace.ErrorMessage, 1, records);
    }

    private sealed class FailureRecords(
        string action = "HermesPreflight", int? errorId = 1723,
        Exception? failure = null) : IMsiFailureRecords
    {
        private int _reads;
        internal int Reads => _reads;

        public string ReadAction(uint record)
        {
            _reads++;
            if (failure is { } error)
                throw error;
            return action;
        }

        public int? ReadErrorId(uint record)
        {
            _reads++;
            if (failure is { } error)
                throw error;
            return errorId;
        }
    }

    private sealed class BrokenObserver : IMsiMessageObserver
    {
        public void Observe(ControlObservationPoint point, ControlFenceObservation observation) =>
            throw new InvalidOperationException("Synthetic observer failure.");

        public void ObserveMsi(MsiObservedMessage message) =>
            throw new InvalidOperationException("Synthetic message observer failure.");
    }

    private sealed class FailureSink(bool fail) : IObservationFailureSink
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);

        public void Record(string point)
        {
            Interlocked.Increment(ref _calls);
            if (fail)
                throw new InvalidOperationException("Synthetic sink failure.");
        }
    }

    private static CallAuthority Create(FakeGate? gate = null,
        IControlCallObserver? observer = null, IObservationFailureSink? failures = null)
    {
        var run = Guid.NewGuid();
        var endpoint = new ControlEndpoint(1, run, new(100, 200),
            "synthetic-test-principal", ControlNames.Pipe(run));
        var plan = new ControlPlan(Product, MaintenanceKind.Install, Root,
            [Previous], Path.GetFullPath("candidate.msi"), false, true);
        return new(endpoint, plan, gate ?? new FakeGate(),
            FixedRootFixture.Policy, observer, failures);
    }

    private static GuardExchange Request(CallAuthority authority, GuardPhase phase) =>
        new(authority.Endpoint.RunId, 1, Guid.NewGuid(), authority.Endpoint.RunId,
            new(phase, Product, null, MaintenanceKind.Install, Root));

    private static GuardExchange Nested(CallAuthority authority, GuardPhase phase) =>
        new(authority.Endpoint.RunId, 1, Guid.NewGuid(), null,
            new(phase, Previous, Product, MaintenanceKind.Uninstall, Root));

    private static ProcessInstance SyntheticClient() => new(300, 400);

    private static void Confirm(CallAuthority authority, GuardExchange exchange)
    {
        var pending = Assert.IsType<PendingFence>(authority.Prepare(exchange, SyntheticClient()));
        Assert.True(authority.Observe(
            new(exchange.RunId, exchange.Request.Phase, exchange.Challenge)));
        Assert.True(pending.Completion.Task.IsCompletedSuccessfully);
        authority.Release(exchange.Challenge);
    }

    private sealed class FakeGate : IClosedGateOwnership
    {
        private bool _held = true;
        public string GatePath => Path.GetFullPath("synthetic-admission.bin");
        public bool IsHeldAndClosed => _held;
        internal void LoseOwnership() => _held = false;
    }
}
