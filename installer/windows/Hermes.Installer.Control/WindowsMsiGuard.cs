using System.IO.Pipes;
using System.Runtime.Versioning;
using Hermes.Servicing;

namespace Hermes.Installer.Control;

[SupportedOSPlatform("windows")]
public static class WindowsMsiGuard
{
    public static uint Invoke(uint session, GuardPhase phase)
    {
        var call = new Invocation(session);
        var probe = new NativePreflightProbe(call.Observe, phase == GuardPhase.Preflight);
        uint result;
        try { result = MsiGuardFlow.Invoke(call, phase, probe); }
        catch { result = BoundaryRefuse(session, probe); }
        call.Complete(result);
        return result;
    }

    private static uint BoundaryRefuse(uint session, NativePreflightProbe probe)
    {
        // Only this unmanaged boundary contains arbitrary exceptions.
        // The managed flow preserves unexpected exception identity.
        try { probe.Reject(NativePreflightReason.BoundaryFault); }
        catch { } // An optional fault hint must itself never escape into MSI.
        return Refuse(session);
    }

    private static bool Exchange(uint session, SessionSnapshot snapshot,
        ControlEndpoint endpoint, NativePreflightProbe probe)
    {
        probe.Mark(NativePreflightPoint.EndpointValidation);
        RequireEndpoint(endpoint);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        probe.Mark(NativePreflightPoint.Connection);
        using var pipe = Connect(endpoint, deadline.Token);
        return GuardPrivateExchange.Run(
            new Channel(session, pipe, deadline.Token), snapshot, endpoint, probe);
    }

    private static NamedPipeClientStream Connect(
        ControlEndpoint endpoint, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", endpoint.PipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            pipe.ConnectAsync(cancellationToken).GetAwaiter().GetResult();
            if (WindowsPeers.Server(pipe) != endpoint.Coordinator)
                throw new InvalidDataException("Wrong coordinator process instance.");
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }

    private static bool Receive(NamedPipeClientStream pipe, GuardExchange exchange,
        ControlStep step, CancellationToken cancellationToken)
    {
        var reply = ControlPackets.ReadAsync(pipe, ControlJson.Default.ControlReply,
            cancellationToken).GetAwaiter().GetResult();
        return reply.Step == step && reply.RunId == exchange.RunId &&
            reply.Challenge == exchange.Challenge;
    }

    private static void RequireEndpoint(ControlEndpoint endpoint)
    {
        if (endpoint.Abi != HermesServicingProtocol.AdmissionAbi ||
            endpoint.RunId == Guid.Empty ||
            endpoint.Coordinator is not { ProcessId: > 0, CreationFileTime: > 0 } ||
            endpoint.PipeName != ControlNames.Pipe(endpoint.RunId) ||
            endpoint.UserSid != WindowsPeers.CurrentSid())
            throw new InvalidDataException("Invalid coordinator descriptor.");
    }

    private static uint Refuse(uint session)
    {
        try
        {
            MsiNative.Message(session, MsiNative.ErrorMessage,
                "Hermes servicing was refused. Use Hermes.Setup; no application was stopped.");
        }
        catch { }
        return 1603;
    }

    private sealed class Invocation(uint session) : IGuardInvocation
    {
        private GuardDiagnostic? _diagnostic;

        public SessionSnapshot ReadSession(GuardPhase phase) =>
            SessionSnapshot.Read(session, phase);

        public ControlEndpoint ReadEndpoint() =>
            ControlFiles.ReadEndpoint(ControlPaths.Current().EndpointPath);

        public bool Exchange(SessionSnapshot snapshot,
            ControlEndpoint endpoint, NativePreflightProbe probe)
        {
            _diagnostic = GuardDiagnostic.Start(snapshot, endpoint);
            return WindowsMsiGuard.Exchange(session, snapshot, endpoint, probe);
        }

        public uint Refuse() => WindowsMsiGuard.Refuse(session);

        internal void Observe(string text) =>
            MsiNative.TryMessage(session, MsiNative.InfoMessage, text);

        internal void Complete(uint result) => _diagnostic?.Complete(result);
    }

    private sealed class Channel(
        uint session, NamedPipeClientStream pipe, CancellationToken cancellationToken)
        : IGuardChannel
    {
        public void Write(GuardExchange exchange) =>
            ControlPackets.WriteAsync(pipe, exchange, ControlJson.Default.GuardExchange,
                cancellationToken).GetAwaiter().GetResult();

        public bool Receive(GuardExchange exchange, ControlStep step) =>
            WindowsMsiGuard.Receive(pipe, exchange, step, cancellationToken);

        public void Signal(FenceSignal signal) =>
            MsiNative.Message(session, MsiNative.InfoMessage, signal.Encode());
    }
}

internal sealed record SessionSnapshot(GuardRequest Request, Guid? PropertyRun)
{
    [SupportedOSPlatform("windows")]
    internal static SessionSnapshot Read(uint session, GuardPhase phase)
        => Read(new WindowsSession(session), phase);

    internal static SessionSnapshot Read(IGuardSession session, GuardPhase phase)
    {
        RequireContext(session, phase);
        var (product, upgrading, run) = Identity(session);
        var root = session.ReadRoot();
        var request = new GuardRequest(phase, product, upgrading, Operation(session),
            session.Property("INSTALLFOLDER"));
        Require(root.IsAppDirectory(request.AppDirectory), NativePreflightReason.Root);
        return new(root.Bind(request), run);
    }

    private static (Guid Product, Guid? Upgrading, Guid? Run) Identity(IGuardSession session)
    {
        var product = Guid.Parse(session.Property("ProductCode"));
        var upgrading = OptionalGuid(session.Property("UPGRADINGPRODUCTCODE"));
        var run = OptionalGuid(session.Property(HermesServicingProtocol.RunProperty));
        var abi = session.Property(HermesServicingProtocol.AbiProperty);
        if ((upgrading is null && (run is null || abi != "1")) ||
            (upgrading is not null && abi is not "" and not "1"))
            throw new GuardRefusalException(NativePreflightReason.Admission);
        Require(Guid.Parse(session.Property("UpgradeCode")) == HermesServicingProtocol.UpgradeCode,
            NativePreflightReason.UpgradeIdentity);
        return (product, upgrading, run);
    }

    private static void RequireContext(IGuardSession session, GuardPhase phase)
    {
        Require(session.Property("ACTION") == "INSTALL", NativePreflightReason.Action);
        Require(session.Property("ALLUSERS") == "", NativePreflightReason.Scope);
        Require(session.Property("MsiRunningElevated") != "1", NativePreflightReason.Elevated);
        Require(session.Property("PATCH") == "", NativePreflightReason.Patch);
        Require(session.Property("RollbackDisabled") != "1", NativePreflightReason.RollbackDisabled);
        Require(session.Mode(16) == 0 && session.Mode(17) == 0,
            NativePreflightReason.ScheduledOrRollbackMode);
        Require(session.Mode(18) == 0, NativePreflightReason.CommitMode);
        Require(phase != GuardPhase.Transaction || session.Mode(3) != 0,
            NativePreflightReason.RollbackNotEnabled);
    }

    private static MaintenanceKind Operation(IGuardSession session)
    {
        var remove = session.Property("REMOVE");
        if (remove == "ALL")
            return MaintenanceKind.Uninstall;
        if (remove != "")
            throw new GuardRefusalException(NativePreflightReason.PartialRemoval);
        return (session.Property("Installed"), session.Property("REINSTALL")) switch
        {
            ("", "") => MaintenanceKind.Install,
            (_, "ALL") => MaintenanceKind.Repair,
            _ => throw new GuardRefusalException(NativePreflightReason.Maintenance)
        };
    }

    private static void Require(bool condition, NativePreflightReason reason)
    {
        if (!condition)
            throw new GuardRefusalException(reason);
    }

    private static Guid? OptionalGuid(string text) =>
        text.Length == 0 ? null : Guid.Parse(text);

    [SupportedOSPlatform("windows")]
    private sealed class WindowsSession(uint session) : IGuardSession
    {
        public string Property(string name) => MsiNative.Property(session, name);
        public int Mode(int mode) => MsiNative.GetMode(session, mode);
        public FixedInstallRoot ReadRoot() => FixedInstallRoot.ForCurrentUser();
    }
}
