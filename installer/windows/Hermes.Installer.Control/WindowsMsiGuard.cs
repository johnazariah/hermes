using System.IO.Pipes;
using System.Runtime.Versioning;
using Hermes.Servicing;

namespace Hermes.Installer.Control;

[SupportedOSPlatform("windows")]
public static class WindowsMsiGuard
{
    public static uint Invoke(uint session, GuardPhase phase)
    {
        GuardDiagnostic? diagnostic = null;
        uint result;
        try
        {
            var snapshot = SessionSnapshot.Read(session, phase);
            var endpoint = ControlFiles.ReadEndpoint(ControlPaths.Current().EndpointPath);
            diagnostic = GuardDiagnostic.Start(snapshot, endpoint);
            result = Exchange(session, snapshot, endpoint) ? 0u : Refuse(session);
        }
        catch { result = Refuse(session); }
        diagnostic?.Complete(result);
        return result;
    }

    private static bool Exchange(uint session, SessionSnapshot snapshot, ControlEndpoint endpoint)
    {
        RequireEndpoint(endpoint);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var pipe = Connect(endpoint, deadline.Token);
        var exchange = new GuardExchange(endpoint.RunId, endpoint.Abi,
            Guid.NewGuid(), snapshot.PropertyRun, snapshot.Request);
        ControlPackets.WriteAsync(pipe, exchange, ControlJson.Default.GuardExchange,
            deadline.Token).GetAwaiter().GetResult();
        if (!Receive(pipe, exchange, ControlStep.Prepared, deadline.Token))
            return false;
        MsiNative.Message(session, MsiNative.InfoMessage,
            new FenceSignal(endpoint.RunId, snapshot.Request.Phase, exchange.Challenge).Encode());
        return Receive(pipe, exchange, ControlStep.Permit, deadline.Token);
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
}

[SupportedOSPlatform("windows")]
internal sealed record SessionSnapshot(GuardRequest Request, Guid? PropertyRun)
{
    internal static SessionSnapshot Read(uint session, GuardPhase phase)
    {
        RequireContext(session, phase);
        var product = Guid.Parse(MsiNative.Property(session, "ProductCode"));
        var upgrading = OptionalGuid(MsiNative.Property(session, "UPGRADINGPRODUCTCODE"));
        var run = OptionalGuid(MsiNative.Property(session, HermesServicingProtocol.RunProperty));
        var abi = MsiNative.Property(session, HermesServicingProtocol.AbiProperty);
        if ((upgrading is null && (run is null || abi != "1")) ||
            (upgrading is not null && abi is not "" and not "1"))
            throw new InvalidDataException("Missing admission context.");
        if (Guid.Parse(MsiNative.Property(session, "UpgradeCode")) != HermesServicingProtocol.UpgradeCode)
            throw new InvalidDataException("Wrong upgrade identity.");
        var root = FixedInstallRoot.ForCurrentUser();
        return new(root.Bind(new GuardRequest(phase, product, upgrading, Operation(session),
            MsiNative.Property(session, "INSTALLFOLDER"))), run);
    }

    private static void RequireContext(uint session, GuardPhase phase)
    {
        if (MsiNative.Property(session, "ACTION") != "INSTALL" ||
            MsiNative.Property(session, "ALLUSERS") != "" ||
            MsiNative.Property(session, "MsiRunningElevated") == "1" ||
            MsiNative.Property(session, "PATCH") != "" ||
            MsiNative.Property(session, "RollbackDisabled") == "1" ||
            MsiNative.GetMode(session, 16) != 0 ||
            MsiNative.GetMode(session, 17) != 0 ||
            MsiNative.GetMode(session, 18) != 0 ||
            (phase == GuardPhase.Transaction && MsiNative.GetMode(session, 3) == 0))
            throw new InvalidDataException("Unsupported MSI execution context.");
    }

    private static MaintenanceKind Operation(uint session)
    {
        var remove = MsiNative.Property(session, "REMOVE");
        if (remove == "ALL")
            return MaintenanceKind.Uninstall;
        if (remove != "")
            throw new InvalidDataException("Partial removal is unsupported.");
        return (MsiNative.Property(session, "Installed"),
            MsiNative.Property(session, "REINSTALL")) switch
        {
            ("", "") => MaintenanceKind.Install,
            (_, "ALL") => MaintenanceKind.Repair,
            _ => throw new InvalidDataException("Unsupported maintenance operation.")
        };
    }

    private static Guid? OptionalGuid(string text) =>
        text.Length == 0 ? null : Guid.Parse(text);
}
