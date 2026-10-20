using Hermes.Installer.Control;
using Hermes.Servicing;
using Xunit;

namespace Hermes.Installer.Control.Tests;

internal static class FixedRootFixture
{
    internal static string LocalAppData { get; } =
        Path.GetFullPath("synthetic-local-app-data");

    internal static FixedInstallRoot Policy { get; } =
        FixedInstallRoot.FromKnownFolder(new KnownFolder(LocalAppData));

    internal sealed record KnownFolder(string? Value) : ILocalAppDataKnownFolder
    {
        public string? Read() => Value;
    }
}

public sealed class FixedRootTests
{
    private static readonly Guid Product = new("11111111-1111-1111-1111-111111111111");
    private static FixedInstallRoot Policy => FixedRootFixture.Policy;

    [Fact]
    public void Root_is_derived_from_known_folder_not_product_metadata() =>
        Assert.Equal(Path.Combine(FixedRootFixture.LocalAppData, "Hermes", "App"),
            Policy.AppDirectory);

    [Fact]
    public void Equivalent_supported_spellings_bind_to_derived_spelling()
    {
        var root = Policy.AppDirectory;
        var candidates = new[]
        {
            root,
            root.ToUpperInvariant(),
            root.Replace('\\', '/'),
            root.Replace('/', '\\'),
            root + "\\",
            root + "/"
        };
        foreach (var candidate in candidates)
            Assert.Equal(root, Policy.Require(candidate));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("%LOCALAPPDATA%\\Hermes\\App")]
    public void Missing_relative_and_unexpanded_values_are_rejected(string? candidate) =>
        Assert.Throws<InvalidDataException>(() => Policy.Require(candidate));

    [Theory]
    [InlineData(".")]
    [InlineData(" ")]
    [InlineData(@"\.")]
    [InlineData(@"\..")]
    [InlineData(@"\..\App")]
    [InlineData(@"\child")]
    [InlineData(@"\child\..")]
    [InlineData(":stream")]
    [InlineData("::$DATA")]
    [InlineData(@"\\")]
    public void Aliases_and_descendants_are_not_normalized_into_admission(string suffix) =>
        Assert.Throws<InvalidDataException>(() =>
            Policy.Require(Policy.AppDirectory + suffix));

    [Theory]
    [InlineData("Hermes.")]
    [InlineData("Hermes ")]
    public void Interior_win32_aliases_are_rejected(string company)
    {
        var candidate = Path.Combine(FixedRootFixture.LocalAppData, company, "App");
        Assert.Throws<InvalidDataException>(() => Policy.Require(candidate));
    }

    [Fact]
    public void Another_profile_and_device_spelling_are_rejected()
    {
        var otherProfile = Path.Combine(Path.GetFullPath("other-local-app-data"), "Hermes", "App");
        Assert.Throws<InvalidDataException>(() => Policy.Require(otherProfile));
        Assert.Throws<InvalidDataException>(() =>
            Policy.Require(@"\\?\" + Policy.AppDirectory.Replace('/', '\\')));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative-local-app-data")]
    public void Unresolved_known_folder_is_not_replaced_with_a_fallback(string? value) =>
        Assert.Throws<InvalidOperationException>(() =>
            FixedInstallRoot.FromKnownFolder(new FixedRootFixture.KnownFolder(value)));

    [Fact]
    public void Matching_custom_plan_and_request_do_not_establish_root_authority()
    {
        var gate = new CountingGate();
        var custom = Path.GetFullPath("relocated-hermes-app");
        Assert.Throws<InvalidDataException>(() => Create(custom, gate));
        Assert.Equal(0, gate.Probes);
    }

    [Fact]
    public void Valid_plan_is_canonicalized_before_authority_uses_it()
    {
        var authority = Create(Policy.AppDirectory.ToUpperInvariant() + "\\", new CountingGate());
        Assert.Equal(Policy.AppDirectory, authority.Plan.AppDirectory);
    }

    [Theory]
    [InlineData(GuardPhase.Preflight)]
    [InlineData(GuardPhase.Transaction)]
    public void Guard_binding_rejects_custom_root_before_downstream_effect(GuardPhase phase)
    {
        var effects = new FakeEffects();
        var request = new GuardRequest(phase, Product, null, MaintenanceKind.Install,
            Path.GetFullPath("relocated-hermes-app"));
        Assert.Throws<InvalidDataException>(() => effects.Accept(Policy.Bind(request)));
        Assert.Equal(0, effects.Calls);
    }

    [Theory]
    [InlineData(GuardPhase.Preflight)]
    [InlineData(GuardPhase.Transaction)]
    public void Guard_binding_uses_derived_root_for_both_phases(GuardPhase phase)
    {
        var request = new GuardRequest(phase, Product, null, MaintenanceKind.Install,
            Policy.AppDirectory.ToUpperInvariant() + "/");
        var bound = Policy.Bind(request);
        Assert.Equal(Policy.AppDirectory, bound.AppDirectory);
        Assert.Equal(phase, bound.Phase);
    }

    [Theory]
    [InlineData(GuardPhase.Preflight)]
    [InlineData(GuardPhase.Transaction)]
    public void Server_refuses_custom_root_even_with_current_run_and_product(GuardPhase phase)
    {
        var authority = Create(Policy.AppDirectory, new CountingGate());
        authority.Begin();
        if (phase == GuardPhase.Transaction)
            ConfirmPreflight(authority);
        var exchange = Exchange(authority, phase,
            Path.GetFullPath("relocated-hermes-app"));
        Assert.Null(authority.Prepare(exchange, new ProcessInstance(300, 400)));
        Assert.False(authority.End(false, null).MayEvaluateRecovery);
    }

    private static CallAuthority Create(string appDirectory, CountingGate gate)
    {
        var run = Guid.NewGuid();
        var endpoint = new ControlEndpoint(1, run, new(100, 200),
            "synthetic-test-principal", ControlNames.Pipe(run));
        var plan = new ControlPlan(Product, MaintenanceKind.Install, appDirectory,
            [], Path.GetFullPath("candidate.msi"), false, true);
        return new(endpoint, plan, gate, Policy);
    }

    private static GuardExchange Exchange(
        CallAuthority authority, GuardPhase phase, string appDirectory) =>
        new(authority.Endpoint.RunId, 1, Guid.NewGuid(), authority.Endpoint.RunId,
            new(phase, Product, null, MaintenanceKind.Install, appDirectory));

    private static void ConfirmPreflight(CallAuthority authority)
    {
        var exchange = Exchange(authority, GuardPhase.Preflight, Policy.AppDirectory);
        Assert.NotNull(authority.Prepare(exchange, new ProcessInstance(300, 400)));
        Assert.True(authority.Observe(
            new(exchange.RunId, GuardPhase.Preflight, exchange.Challenge)));
        authority.Release(exchange.Challenge);
    }

    private sealed class CountingGate : IClosedGateOwnership
    {
        private int _probes;
        internal int Probes => _probes;
        public string GatePath => Path.GetFullPath("synthetic-admission.bin");
        public bool IsHeldAndClosed
        {
            get
            {
                Interlocked.Increment(ref _probes);
                return true;
            }
        }
    }

    private sealed class FakeEffects
    {
        private int _calls;
        internal int Calls => _calls;
        internal void Accept(GuardRequest request) => _calls++;
    }
}
