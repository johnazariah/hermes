using System.ComponentModel;
using System.Text.Json;
using Hermes.Servicing;

namespace Hermes.Installer.Control;

internal interface IGuardInvocation
{
    SessionSnapshot ReadSession(GuardPhase phase);
    ControlEndpoint ReadEndpoint();
    bool Exchange(
        SessionSnapshot snapshot, ControlEndpoint endpoint, NativePreflightProbe probe);
    uint Refuse();
}

internal interface IGuardChannel
{
    void Write(GuardExchange exchange);
    bool Receive(GuardExchange exchange, ControlStep step);
    void Signal(FenceSignal signal);
}

internal interface IGuardSession
{
    string Property(string name);
    int Mode(int mode);
    FixedInstallRoot ReadRoot();
}

internal static class MsiGuardFlow
{
    internal static uint Invoke(
        IGuardInvocation call, GuardPhase phase, NativePreflightProbe probe)
    {
        try { return Execute(call, phase, probe) ? 0u : call.Refuse(); }
        catch (GuardRefusalException error)
        {
            probe.Reject(error.Reason);
            return call.Refuse();
        }
        catch (InvalidDataException)
        {
            probe.Reject(NativePreflightReason.Refused);
            return call.Refuse();
        }
        catch (Exception error) when (Expected(error))
        {
            probe.Reject(NativePreflightReason.OperationFailed);
            return call.Refuse();
        }
    }

    private static bool Execute(
        IGuardInvocation call, GuardPhase phase, NativePreflightProbe probe)
    {
        probe.Mark(NativePreflightPoint.Entry);
        probe.Mark(NativePreflightPoint.Session);
        var snapshot = call.ReadSession(phase);
        probe.Mark(NativePreflightPoint.Endpoint);
        var endpoint = call.ReadEndpoint();
        return call.Exchange(snapshot, endpoint, probe);
    }

    private static bool Expected(Exception error) =>
        error is IOException or UnauthorizedAccessException or Win32Exception or
            FormatException or JsonException or OperationCanceledException or TimeoutException;
}

internal static class GuardPrivateExchange
{
    internal static bool Run(IGuardChannel channel, SessionSnapshot snapshot,
        ControlEndpoint endpoint, NativePreflightProbe probe)
    {
        var exchange = new GuardExchange(endpoint.RunId, endpoint.Abi,
            Guid.NewGuid(), snapshot.PropertyRun, snapshot.Request);
        probe.Mark(NativePreflightPoint.Prepare);
        channel.Write(exchange);
        if (!channel.Receive(exchange, ControlStep.Prepared))
            return Reject(probe);
        probe.Mark(NativePreflightPoint.Signal);
        channel.Signal(new(endpoint.RunId, snapshot.Request.Phase, exchange.Challenge));
        probe.Mark(NativePreflightPoint.Permit);
        if (!channel.Receive(exchange, ControlStep.Permit))
            return Reject(probe);
        probe.Mark(NativePreflightPoint.Permitted);
        return true;
    }

    private static bool Reject(NativePreflightProbe probe)
    {
        probe.Reject(NativePreflightReason.Refused);
        return false;
    }
}
