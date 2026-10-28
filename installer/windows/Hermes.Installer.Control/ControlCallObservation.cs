using Hermes.Servicing;

namespace Hermes.Installer.Control;

internal enum ControlObservationPoint
{
    BeforeCanonicalObservation,
    CanonicalObservationAccepted
}

// A copied description, not a capability or permission response.
internal sealed record ControlFenceObservation(
    Guid RunId,
    GuardPhase Phase,
    Guid ProductCode,
    bool OwnSession,
    ProcessInstance CaHost);

internal interface IControlCallObserver
{
    void Observe(ControlObservationPoint point, ControlFenceObservation observation);
}

internal enum MsiObservedKind { InstallStart, InstallEnd, ActionStart }

internal sealed record MsiObservedMessage(
    MsiObservedKind Kind,
    Guid? ProductCode,
    int? Status,
    string? Action);

internal interface IMsiMessageObserver : IControlCallObserver
{
    void ObserveMsi(MsiObservedMessage message);
}

internal interface IObservationFailureSink
{
    void Record(string point);
}

internal static class ControlObservation
{
    internal const uint MsiMessageFilters = (1u << 8) | (1u << 26) | (1u << 27);

    internal static void Emit(
        IControlCallObserver? observer,
        ControlObservationPoint point,
        ControlFenceObservation observation,
        Action<string> failed)
    {
        try { observer?.Observe(point, observation); }
        catch { failed(point.ToString()); }
    }

    internal static void EmitMsi(
        IMsiMessageObserver observer,
        MsiObservedMessage message,
        Action<string> failed)
    {
        try { observer.ObserveMsi(message); }
        catch { failed(message.Kind.ToString()); }
    }
}
