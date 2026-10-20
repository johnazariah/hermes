using System.Text.Json;
using Hermes.Installer.Control;
using Hermes.Servicing;
using Xunit;

namespace Hermes.Installer.Control.Tests;

public sealed class AuthorityTests
{
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
        Assert.Equal(ControlObservation.MsiMessageFilters, authority.ObservationFilters);
        authority.Begin();
        Confirm(authority, Request(authority, GuardPhase.Preflight));
        Confirm(authority, Request(authority, GuardPhase.Transaction));
        var receipt = authority.End(true, 0);
        Assert.False(receipt.ControlFaulted);
        Assert.Equal(2, receipt.Fences.Length);
        Assert.Equal(4, failures.Calls);
        Assert.Equal(sinkThrows ? 8 : 4, receipt.ObservationFaults);
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
