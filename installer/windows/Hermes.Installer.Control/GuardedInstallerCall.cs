using System.Runtime.Versioning;
using Hermes.Servicing;

namespace Hermes.Installer.Control;

[SupportedOSPlatform("windows")]
internal sealed class GuardedInstallerCall : IAsyncDisposable
{
    private readonly CallAuthority _authority;
    private readonly ControlServer _server;
    private readonly ControlPaths _paths;

    private GuardedInstallerCall(
        CallAuthority authority, ControlServer server, ControlPaths paths) =>
        (_authority, _server, _paths) = (authority, server, paths);

    internal Guid RunId => _authority.Endpoint.RunId;

    internal static Task<GuardedInstallerCall> StartAsync(
        IClosedGateOwnership gate, ControlPlan plan,
        CancellationToken cancellationToken) =>
        StartAsync(gate, plan, null, cancellationToken);

    internal static async Task<GuardedInstallerCall> StartAsync(
        IClosedGateOwnership gate, ControlPlan plan,
        IControlCallObserver? observer, CancellationToken cancellationToken)
    {
        var (admittedPlan, root) = RequirePlan(plan);
        var paths = ControlPaths.Current();
        if (!gate.IsHeldAndClosed || !SamePath(gate.GatePath, paths.GatePath))
            throw new InvalidOperationException("Exclusive closed admission is required.");
        var endpoint = NewEndpoint();
        var authority = new CallAuthority(endpoint, admittedPlan, gate, root, observer,
            new WindowsObservationFailureSink(endpoint, admittedPlan.ProductCode));
        var server = await ControlServer.StartAsync(authority, cancellationToken);
        return await PublishAsync(authority, server, paths, cancellationToken);
    }

    private static ControlEndpoint NewEndpoint()
    {
        var run = Guid.NewGuid();
        return new(HermesServicingProtocol.AdmissionAbi, run,
            WindowsPeers.Current(), WindowsPeers.CurrentSid(), ControlNames.Pipe(run));
    }

    private static async Task<GuardedInstallerCall> PublishAsync(
        CallAuthority authority, ControlServer server, ControlPaths paths,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ControlFiles.PublishEndpoint(paths.EndpointPath, authority.Endpoint);
            return new(authority, server, paths);
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    internal InstallerCallReceipt Execute() => _authority.Execute(
        () => ExternalUiScope.Open(_authority, _authority.Plan.Quiet),
        () => Invoke(_authority.Plan, RunId, _authority),
        receipt => ControlFiles.PublishReceipt(_paths.ReceiptPath, receipt));

    private static uint Invoke(ControlPlan plan, Guid run, CallAuthority authority)
    {
        var properties = Properties(plan, run);
        Func<uint> invoke = plan.Operation switch
        {
            MaintenanceKind.Install when plan.PackagePath is { } package =>
                () => MsiNative.InstallProduct(package, properties),
            MaintenanceKind.Repair => () => MsiNative.ConfigureProduct(
                plan.ProductCode.ToString("B"), 0, 5,
                $"{properties} REINSTALL=ALL REINSTALLMODE=amus"),
            MaintenanceKind.Uninstall => () => MsiNative.ConfigureProduct(
                plan.ProductCode.ToString("B"), 0, 2, properties),
            _ => throw new InvalidOperationException("Unsupported installer call.")
        };
        return authority.InvokeObserved(WindowsMsiCallContextTokens.Instance, invoke);
    }

    private static string Properties(ControlPlan plan, Guid run) =>
        $"{HermesServicingProtocol.RunProperty}={run:D} " +
        $"{HermesServicingProtocol.AbiProperty}=1 " +
        $"INSTALLFOLDER=\"{plan.AppDirectory}\" " +
        "REBOOT=ReallySuppress MSIRESTARTMANAGERCONTROL=Disable" +
        (plan.AllowRollback ? $" {HermesServicingProtocol.RollbackProperty}=1" : "");

    private static (ControlPlan Plan, FixedInstallRoot Root) RequirePlan(ControlPlan plan)
    {
        var root = FixedInstallRoot.ForCurrentUser();
        plan = root.Bind(plan);
        if (plan.ProductCode == Guid.Empty ||
            plan.NestedRemovalProducts.IsDefault ||
            plan.NestedRemovalProducts.Any(product => product == Guid.Empty || product == plan.ProductCode) ||
            plan.NestedRemovalProducts.Distinct().Count() != plan.NestedRemovalProducts.Length ||
            (plan.Operation != MaintenanceKind.Install &&
                (plan.PackagePath is not null || plan.AllowRollback ||
                    !plan.NestedRemovalProducts.IsEmpty)))
            throw new InvalidDataException("Invalid installer plan.");
        if (plan.Operation == MaintenanceKind.Install &&
            (plan.PackagePath is not { } package || !Path.IsPathFullyQualified(package)))
            throw new InvalidDataException("An absolute MSI source is required.");
        return (plan, root);
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}
