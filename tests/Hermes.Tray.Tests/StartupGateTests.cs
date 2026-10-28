using System.Collections.Immutable;
using System.Text.Json;
using Hermes.Servicing;
using Xunit;
using SmokeEntry = Hermes.Tray.Smoke.SmokeEntry;

namespace Hermes.Tray.Tests;

public sealed class StartupGateTests
{
    [Theory]
    [InlineData("normal")]
    [InlineData("help")]
    [InlineData("diagnostic")]
    [InlineData("malformed-diagnostic")]
    public void Every_real_tray_dispatch_refuses_before_dependencies(string route)
    {
        var order = new List<string>();
        var admission = new FixedAdmission(new Refused(RefusalReason.Closed), order);
        var exit = Program.Run(TrayArguments(route), () => admission,
            (_, _) => Effect(order, "normal"), (_, _) => Effect(order, "diagnostic"),
            message => order.Add(message));
        Assert.Equal(NativeStartupGate.RefusedExitCode, exit);
        Assert.Equal(["enter", NativeStartupGate.RefusalDiagnostic], order);
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("diagnostic")]
    public void Actual_tray_dispatch_keeps_the_retained_nonreleasable_lease(string route)
    {
        var rig = new RetainedAdmission();
        var order = new List<string>();
        int Dispatch(NativeStartupContext startup)
        {
            Assert.Equal(1, rig.Retention.Count);
            Assert.False(rig.Lease.Disposed);
            Assert.Same(rig.Retention.Token, Assert.IsType<Admitted>(startup.Evidence).Lease);
            return Effect(order, route);
        }
        var exit = Program.Run(TrayArguments(route), () => rig.Admission,
            (_, startup) => Dispatch(startup), (_, startup) => Dispatch(startup), order.Add);
        Assert.Equal(7, exit);
        Assert.Equal([route], order);
        Assert.False(rig.Lease.Disposed);
        Assert.False(rig.Retention.Token is IDisposable);
    }

    [Theory]
    [InlineData("fixture")]
    [InlineData("smoke")]
    [InlineData("help")]
    public async Task Actual_helper_refuses_before_fixture_console_or_harness_effects(string route)
    {
        var order = new List<string>();
        var admission = new FixedAdmission(new Refused(RefusalReason.Busy), order);
        var exit = await SmokeEntry.RunAsync(HelperArguments(route), () => admission,
            (_, _) => Effect(order, "fixture"),
            _ => Task.FromResult(Effect(order, "harness")), order.Add);
        Assert.Equal(NativeStartupGate.RefusedExitCode, exit);
        Assert.Equal(["enter", NativeStartupGate.RefusalDiagnostic], order);
    }

    [Theory]
    [InlineData("fixture")]
    [InlineData("smoke")]
    [InlineData("help")]
    public async Task Helper_dispatch_requires_positive_outside_evidence(string route)
    {
        var order = new List<string>();
        var admission = new FixedAdmission(new OutsideKnownInstallation(), order);
        var exit = await SmokeEntry.RunAsync(HelperArguments(route), () => admission,
            (receipt, archive) => Effect(order, $"{receipt}:{archive}"),
            args => EffectAsync(order, string.Join(":", args)), order.Add);
        Assert.Equal(7, exit);
        Assert.Equal(["enter", route switch
        {
            "fixture" => "receipt:archive",
            "help" => "--help",
            _ => "--installed-tray-path:tray"
        }], order);
    }

    [Fact]
    public void Null_unknown_and_null_lease_evidence_are_not_outside()
    {
        Assert.Null(NativeStartupGate.Classify(null));
        Assert.Null(NativeStartupGate.Classify(new UnknownAdmission()));
        Assert.Null(NativeStartupGate.Classify(
            JsonSerializer.Deserialize<Admitted>("""{"Lease":null}""")));
        Assert.Null(NativeStartupGate.Classify(new Refused(RefusalReason.Unavailable)));
    }

    [Fact]
    public async Task Factory_failure_is_bounded_in_both_entry_adapters()
    {
        var diagnostics = new List<string>();
        IStartupAdmission Fail() => throw new InvalidOperationException("not diagnostic content");
        Assert.Equal(78, NativeStartupGate.Run(Fail,
            _ => throw new InvalidOperationException("dispatched"), diagnostics.Add));
        Assert.Equal(78, await NativeStartupGate.RunAsync(Fail,
            _ => throw new InvalidOperationException("dispatched"), diagnostics.Add));
        Assert.Equal(Enumerable.Repeat(NativeStartupGate.RefusalDiagnostic, 2), diagnostics);
    }

    [Fact]
    public void Diagnostic_checks_child_before_ci_request_or_workspace_effects()
    {
        var paths = new List<string>();
        var lease = new SelectingLease(path =>
        {
            paths.Add(path);
            return new ServiceRefused(RefusalReason.WrongGeneration);
        });
        var workspace = Path.Combine(Path.GetTempPath(), $"not-created-{Guid.NewGuid():N}");
        Assert.Equal(1, Smoke.InstalledDiagnostic.Run(workspace, new(new Admitted(lease))));
        Assert.Single(paths);
        Assert.Equal(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "service", ServiceWire.Executable)), paths[0]);
        Assert.False(Directory.Exists(workspace));
    }

    private static string[] TrayArguments(string route) => route switch
    {
        "diagnostic" => ["--installed-smoke", "workspace"],
        "malformed-diagnostic" => ["--installed-smoke"],
        "help" => ["--help"],
        _ => []
    };

    private static string[] HelperArguments(string route) => route switch
    {
        "fixture" => ["--create-owner-fixture", "receipt", "--archive-directory", "archive"],
        "help" => ["--help"],
        _ => ["--installed-tray-path", "tray"]
    };

    private static int Effect(List<string> order, string value)
    {
        order.Add(value);
        return 7;
    }

    private static Task<int> EffectAsync(List<string> order, string value) =>
        Task.FromResult(Effect(order, value));

    private sealed record UnknownAdmission : Admission;

    private sealed record FixedAdmission(Admission Result, List<string> Order) : IStartupAdmission
    {
        public Admission Enter(RuntimeEntry entry)
        {
            Assert.Equal(RuntimeEntry.Tray, entry);
            Order.Add("enter");
            return Result;
        }
    }

    private sealed class RetainedAdmission
    {
        private static readonly string Root = Path.GetFullPath("synthetic-native-entry");
        private static readonly string Entry = Path.Combine(Root, "tray", "Hermes.Tray.dll");
        private static readonly Guid Mvid = new("11111111-1111-1111-1111-111111111111");
        internal TestReadLease Lease { get; } = new(new(new(
            new(HermesServicingProtocol.UpgradeCode, Root, HermesServicingProtocol.AdmissionAbi),
            new Guid("22222222-2222-2222-2222-222222222222"),
            [new("tray/Hermes.Tray.dll", Mvid)])));
        internal TestRetention Retention { get; } = new();
        internal IStartupAdmission Admission => new StartupAdmission(
            new TestGate(Lease), new TestLoaded(new(Entry, Mvid, [new(Entry, Mvid)])), Retention);
    }

    private sealed record TestGate(IRuntimeReadLease Lease) : IRuntimeGate
    {
        public GateOpen TryOpen() => new GateOpened(Lease);
    }

    private sealed record TestLoaded(LoadedCode Code) : ILoadedCode
    {
        public LoadedCode Read() => Code;
    }

    private sealed class TestReadLease(GateReadyState state) : IRuntimeReadLease
    {
        private bool _disposed;
        internal bool Disposed => _disposed;
        public GateReadyState ReadReady() => state;
        public void Dispose() => _disposed = true;
    }

    private sealed class TestRetention : ILeaseLifetime
    {
        private ImmutableArray<IRuntimeReadLease> _leases = [];
        internal int Count => _leases.Length;
        internal IProcessLease Token { get; } = new TestToken();
        public IProcessLease Retain(IRuntimeReadLease lease)
        {
            _leases = _leases.Add(lease);
            return Token;
        }
    }

    private sealed record TestToken : IProcessLease;
}

internal sealed class SelectingLease(Func<string, ServiceSelection> select) : IEnrolledServiceLease
{
    public ServiceSelection SelectService(string executablePath) => select(executablePath);
}
