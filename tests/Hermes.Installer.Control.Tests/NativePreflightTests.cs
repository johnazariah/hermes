using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Hermes.Installer.Control;
using Hermes.Servicing;
using Xunit;

namespace Hermes.Installer.Control.Tests;

public sealed class NativePreflightTests
{
    private static readonly Guid Product = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Run = new("33333333-3333-3333-3333-333333333333");
    private static FixedInstallRoot Root => FixedRootFixture.Policy;

    [Theory]
    [InlineData("7701399/1/02/11 ")]
    [InlineData(" 7701399/1/02/11")]
    [InlineData("7701399/2/02/11")]
    [InlineData("7701399/1/2/11")]
    [InlineData("7701399/1/00/00")]
    [InlineData("7701399/1/99/00")]
    [InlineData("7701399/1/02/99")]
    [InlineData("7701399/1/06/11")]
    [InlineData("7701399/1/02/03")]
    [InlineData("SECRET C:\\owner\\token")]
    public void Only_closed_exact_numeric_wire_is_retained(string text) =>
        Assert.Null(NativePreflightObservation.Decode(text));

    [Fact]
    public void Wire_is_bounded_numeric_separate_from_fences_and_round_trips()
    {
        var value = new NativePreflightObservation(
            NativePreflightPoint.Session, NativePreflightReason.Scope);
        var wire = value.Encode();
        Assert.Equal(15, wire.Length);
        Assert.All(wire, character => Assert.True(character == '/' || character is >= '0' and <= '9'));
        Assert.Equal(value, NativePreflightObservation.Decode(wire));
        Assert.Null(FenceSignal.Decode(wire));
        Assert.Null(NativePreflightObservation.Decode(new string('7', 8192)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativePreflightObservation.Missing.Encode());
    }

    [Theory]
    [InlineData("ACTION", "ADMIN", 10)]
    [InlineData("ALLUSERS", "1", 11)]
    [InlineData("ALLUSERS", "2", 11)]
    [InlineData("MsiRunningElevated", "1", 12)]
    [InlineData("PATCH", "SECRET", 13)]
    [InlineData("RollbackDisabled", "1", 14)]
    [InlineData("HERMES_SERVICING_ABI", "2", 18)]
    [InlineData("UpgradeCode", "{00000000-0000-0000-0000-000000000000}", 19)]
    [InlineData("INSTALLFOLDER", "SECRET", 20)]
    [InlineData("REMOVE", "Partial", 21)]
    [InlineData("Installed", "1", 22)]
    public void Actual_snapshot_refusals_are_numeric_and_do_not_reach_private_exchange(
        string property, string value, int reason)
    {
        var session = Session() with { Values = Session().Values.SetItem(property, value) };
        var channel = new Channel();
        var call = new Invocation(session, channel);
        var wire = new List<string>();
        Assert.Equal(1603u, MsiGuardFlow.Invoke(call, GuardPhase.Preflight, Probe(wire.Add)));
        Assert.Equal(new(NativePreflightPoint.Session, (NativePreflightReason)reason), Last(wire));
        Assert.Equal(1, call.Refusals);
        Assert.Equal(0, channel.Writes);
        Assert.Equal(0, channel.Signals);
        Assert.DoesNotContain(value == "SECRET" ? value : "PRIVATE_SECRET", Last(wire).Encode());
        Assert.Equal(value, session.Property(property));
    }

    [Fact]
    public void Empty_per_user_scope_is_accepted_by_the_actual_snapshot_reader()
    {
        var snapshot = SessionSnapshot.Read(Session(), GuardPhase.Preflight);
        Assert.Equal(Product, snapshot.Request.ProductCode);
        Assert.Equal(Run, snapshot.PropertyRun);
        Assert.Equal(Root.AppDirectory, snapshot.Request.AppDirectory);
        Assert.Equal(MaintenanceKind.Install, snapshot.Request.Operation);
    }

    [Theory]
    [InlineData(16, 15)]
    [InlineData(17, 15)]
    [InlineData(18, 16)]
    public void Native_modes_remain_refused(int mode, int reason)
    {
        var session = Session() with { ActiveMode = mode };
        var error = Assert.Throws<GuardRefusalException>(() =>
            SessionSnapshot.Read(session, GuardPhase.Preflight));
        Assert.Equal((NativePreflightReason)reason, error.Reason);
        var rollback = Assert.Throws<GuardRefusalException>(() =>
            SessionSnapshot.Read(Session(), GuardPhase.Transaction));
        Assert.Equal(NativePreflightReason.RollbackNotEnabled, rollback.Reason);
    }

    [Fact]
    public void Entry_is_observed_before_the_first_property_read_can_fail()
    {
        var wire = new List<string>();
        var call = new Invocation(Session() with { ReadFailure = new Win32Exception(5) }, new Channel());
        Assert.Equal(1603u, MsiGuardFlow.Invoke(call, GuardPhase.Preflight, Probe(wire.Add)));
        Assert.Equal(NativePreflightPoint.Entry, Last([wire[0]]).Point);
        Assert.Equal(new(NativePreflightPoint.Session, NativePreflightReason.OperationFailed), Last(wire));
        Assert.Equal(1, call.Refusals);
    }

    [Theory]
    [InlineData(false, true, 6, 0)]
    [InlineData(true, false, 8, 1)]
    public void Actual_private_exchange_distinguishes_prepare_and_permit_refusal(
        bool prepared, bool permit, int point, int signals)
    {
        var wire = new List<string>();
        var channel = new Channel(prepared, permit);
        var call = new Invocation(Session(), channel);
        Assert.Equal(1603u, MsiGuardFlow.Invoke(call, GuardPhase.Preflight, Probe(wire.Add)));
        Assert.Equal(new((NativePreflightPoint)point, NativePreflightReason.Refused), Last(wire));
        Assert.Equal(signals, channel.Signals);
        Assert.Equal(1, call.Refusals);
    }

    [Fact]
    public void Original_signal_exception_is_preserved_without_reading_a_permit()
    {
        var wire = new List<string>();
        var expected = new InvalidOperationException("PRIVATE_SECRET");
        var channel = new Channel(signalFailure: expected);
        var call = new Invocation(Session(), channel);
        var actual = Assert.Throws<InvalidOperationException>(() =>
            MsiGuardFlow.Invoke(call, GuardPhase.Preflight, Probe(wire.Add)));
        Assert.Same(expected, actual);
        Assert.Equal(new(NativePreflightPoint.Signal, NativePreflightReason.None), Last(wire));
        Assert.Equal(0, channel.PermitReads);
        Assert.Equal(0, call.Refusals);
    }

    [Fact]
    public void Managed_programming_fault_is_not_converted_to_a_refusal()
    {
        var expected = new InvalidOperationException("PRIVATE_SECRET");
        var call = new Invocation(Session(), new Channel(signalFailure: expected));
        var actual = Assert.Throws<InvalidOperationException>(() =>
            MsiGuardFlow.Invoke(call, GuardPhase.Preflight, Probe(static _ => { })));
        Assert.Same(expected, actual);
        Assert.Equal(0, call.Refusals);
    }

    [Fact]
    public void Expected_optional_probe_failure_preserves_the_original_refusal()
    {
        var call = new Invocation(Session() with
            { Values = Session().Values.SetItem("ALLUSERS", "1") }, new Channel());
        Assert.Equal(1603u, MsiGuardFlow.Invoke(call, GuardPhase.Preflight,
            Probe(static _ => throw new IOException("PRIVATE_SECRET"))));
        Assert.Equal(1, call.Refusals);
    }

    [Fact]
    public void Diagnostic_INFO_is_live_only_never_acknowledged_and_never_completes_a_fence()
    {
        var authority = Authority();
        var value = new NativePreflightObservation(
            NativePreflightPoint.Session, NativePreflightReason.Scope);
        Assert.Equal(0, Dispatch(authority, value.Encode()));
        authority.Begin();
        var exchange = new GuardExchange(Run, 1, Guid.NewGuid(), Run,
            new(GuardPhase.Preflight, Product, null, MaintenanceKind.Install, Root.AppDirectory));
        var pending = Assert.IsType<PendingFence>(authority.Prepare(exchange, new(300, 400)));
        Assert.Equal(0, Dispatch(authority, value.Encode()));
        Assert.False(pending.Completion.Task.IsCompleted);
        var receipt = authority.End(true, 1603);
        Assert.Equal(value, receipt.NativePreflight);
        Assert.Empty(receipt.Fences);
        Assert.False(receipt.MayEvaluateRecovery);
        Assert.Equal(0, Dispatch(authority, value.Encode()));
        Assert.DoesNotContain("nativePreflight",
            JsonSerializer.Serialize(receipt, ControlJson.Default.InstallerCallReceipt));
    }

    [Fact]
    public void Prior_and_later_callbacks_cannot_supply_a_new_calls_observation()
    {
        var authority = Authority();
        var value = new NativePreflightObservation(
            NativePreflightPoint.Session, NativePreflightReason.Scope);
        Dispatch(authority, value.Encode());
        authority.Begin();
        var receipt = authority.End(true, 1603);
        Dispatch(authority, value.Encode());
        Assert.Equal(NativePreflightObservation.Missing, receipt.NativePreflight);
        Assert.Equal(NativePreflightObservation.Missing, Authority().End(false, null).NativePreflight);
    }

    private static NativePreflightProbe Probe(Action<string> write) => new(write, true);

    private static NativePreflightObservation Last(IReadOnlyList<string> wire) =>
        Assert.IsType<NativePreflightObservation>(NativePreflightObservation.Decode(wire[^1]));

    private static SessionData Session() => new(new Dictionary<string, string>
    {
        ["ACTION"] = "INSTALL",
        ["ProductCode"] = Product.ToString("B"),
        ["UpgradeCode"] = HermesServicingProtocol.UpgradeCode.ToString("B"),
        [HermesServicingProtocol.RunProperty] = Run.ToString("D"),
        [HermesServicingProtocol.AbiProperty] = "1",
        ["INSTALLFOLDER"] = Root.AppDirectory
    }.ToImmutableDictionary());

    private static CallAuthority Authority() => new(
        new(1, Run, new(100, 200), "synthetic-principal", ControlNames.Pipe(Run)),
        new(Product, MaintenanceKind.Install, Root.AppDirectory, [],
            Path.GetFullPath("candidate.msi"), false, true), new Gate(), Root);

    private static int Dispatch(CallAuthority authority, string text)
    {
        var context = GCHandle.Alloc(authority);
        try
        {
            return ExternalUiScope.Dispatch(GCHandle.ToIntPtr(context), 0x04000000, 1,
                new NoRecords(), _ => text, static (_, _, _) => { });
        }
        finally { context.Free(); }
    }

    private sealed record SessionData(
        ImmutableDictionary<string, string> Values,
        int ActiveMode = 0,
        Exception? ReadFailure = null) : IGuardSession
    {
        public string Property(string name)
        {
            if (ReadFailure is { } error)
                throw error;
            return Values.GetValueOrDefault(name, "");
        }

        public int Mode(int mode) => mode == ActiveMode ? 1 : 0;
        public FixedInstallRoot ReadRoot() => Root;
    }

    private sealed class Invocation(IGuardSession session, Channel channel) : IGuardInvocation
    {
        private int _refusals;
        internal int Refusals => _refusals;

        public SessionSnapshot ReadSession(GuardPhase phase) => SessionSnapshot.Read(session, phase);

        public ControlEndpoint ReadEndpoint() =>
            new(1, Run, new(100, 200), "synthetic-principal", ControlNames.Pipe(Run));

        public bool Exchange(SessionSnapshot snapshot,
            ControlEndpoint endpoint, NativePreflightProbe probe) =>
            GuardPrivateExchange.Run(channel, snapshot, endpoint, probe);

        public uint Refuse()
        {
            _refusals++;
            return 1603;
        }
    }

    private sealed class Channel(
        bool prepared = true, bool permit = true, Exception? signalFailure = null) : IGuardChannel
    {
        private int _writes;
        private int _signals;
        private int _permitReads;
        internal int Writes => _writes;
        internal int Signals => _signals;
        internal int PermitReads => _permitReads;

        public void Write(GuardExchange exchange) => _writes++;

        public bool Receive(GuardExchange exchange, ControlStep step)
        {
            if (step == ControlStep.Prepared)
                return prepared;
            _permitReads++;
            return permit;
        }

        public void Signal(FenceSignal signal)
        {
            _signals++;
            if (signalFailure is { } error)
                throw error;
        }
    }

    private sealed class Gate : IClosedGateOwnership
    {
        public string GatePath => Path.GetFullPath("synthetic-admission.bin");
        public bool IsHeldAndClosed => true;
    }

    private sealed class NoRecords : IMsiFailureRecords
    {
        public string ReadAction(uint record) => throw new InvalidOperationException("must not read");
        public int? ReadErrorId(uint record) => throw new InvalidOperationException("must not read");
    }

}
