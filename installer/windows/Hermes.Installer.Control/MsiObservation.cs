using System.Runtime.Versioning;

namespace Hermes.Installer.Control;

[SupportedOSPlatform("windows")]
internal static class MsiObservation
{
    internal static MsiObservedMessage? Read(uint message, uint record) =>
        (message & 0xff000000) switch
        {
            MsiNative.InstallStartMessage => new(MsiObservedKind.InstallStart,
                Guid.Parse(MsiNative.RecordField(record, 2)), null, null),
            MsiNative.InstallEndMessage => new(MsiObservedKind.InstallEnd,
                Guid.Parse(MsiNative.RecordField(record, 2)),
                MsiNative.RecordInteger(record, 3), null),
            MsiNative.ActionStartMessage => new(MsiObservedKind.ActionStart,
                null, null, Action(record)),
            _ => null
        };

    private static string Action(uint record)
    {
        var name = MsiNative.RecordField(record, 1);
        return name.Length is > 0 and <= 128 &&
            name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
                ? name : throw new InvalidDataException("Unsupported MSI action name.");
    }
}
