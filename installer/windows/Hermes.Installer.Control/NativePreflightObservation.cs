using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;

namespace Hermes.Installer.Control;

internal enum NativePreflightPoint
{
    NotObserved = 0,
    Entry = 1,
    Session = 2,
    Endpoint = 3,
    EndpointValidation = 4,
    Connection = 5,
    Prepare = 6,
    Signal = 7,
    Permit = 8,
    Permitted = 9
}

internal enum NativePreflightReason
{
    None = 0,
    OperationFailed = 1,
    Refused = 2,
    BoundaryFault = 4,
    Action = 10,
    Scope = 11,
    Elevated = 12,
    Patch = 13,
    RollbackDisabled = 14,
    ScheduledOrRollbackMode = 15,
    CommitMode = 16,
    RollbackNotEnabled = 17,
    Admission = 18,
    UpgradeIdentity = 19,
    Root = 20,
    PartialRemoval = 21,
    Maintenance = 22
}

// An untrusted hint from the active MSI callback, never native provenance or authority.
internal sealed record NativePreflightObservation(
    NativePreflightPoint Point, NativePreflightReason Reason)
{
    internal static NativePreflightObservation Missing { get; } =
        new(NativePreflightPoint.NotObserved, NativePreflightReason.None);

    private static readonly ImmutableDictionary<string, NativePreflightObservation> Wire =
        Enum.GetValues<NativePreflightPoint>()
            .SelectMany(point => Enum.GetValues<NativePreflightReason>()
                .Select(reason => new NativePreflightObservation(point, reason)))
            .Where(Valid)
            .ToImmutableDictionary(Format, value => value, StringComparer.Ordinal);

    internal string Encode() => Valid(this)
        ? Format(this)
        : throw new ArgumentOutOfRangeException(nameof(Point));

    internal static NativePreflightObservation? Decode(string text) =>
        text.Length == 15 ? Wire.GetValueOrDefault(text) : null;

    private static string Format(NativePreflightObservation value) =>
        string.Create(CultureInfo.InvariantCulture,
            $"7701399/1/{(int)value.Point:D2}/{(int)value.Reason:D2}");

    private static bool Valid(NativePreflightObservation value) =>
        (int)value.Point is >= 1 and <= 9 && (value.Reason switch
        {
            NativePreflightReason.None or NativePreflightReason.OperationFailed or
                NativePreflightReason.BoundaryFault => true,
            NativePreflightReason.Refused => value.Point is not NativePreflightPoint.Entry,
            _ => (int)value.Reason is >= 10 and <= 22 &&
                value.Point == NativePreflightPoint.Session
        });
}

internal sealed class NativePreflightProbe(Action<string> write, bool enabled)
{
    private NativePreflightObservation _current =
        new(NativePreflightPoint.Entry, NativePreflightReason.None);

    internal void Mark(NativePreflightPoint point)
    {
        _current = new(point, NativePreflightReason.None);
        Emit();
    }

    internal void Reject(NativePreflightReason reason)
    {
        _current = _current with { Reason = reason };
        Emit();
    }

    private void Emit()
    {
        if (!enabled)
            return;
        var text = _current.Encode();
        try { write(text); }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            // Native status failures are optional; programming faults are not hidden here.
        }
    }
}

internal sealed class GuardRefusalException(NativePreflightReason reason) : IOException
{
    internal NativePreflightReason Reason { get; } = reason;
}
