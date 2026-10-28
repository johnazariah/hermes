using System.Collections.Immutable;

namespace Hermes.Setup;

internal sealed record RegistrationInventory(
    ReadObservation<MsiRegistration> Msi,
    ImmutableDictionary<string, RegistryAtom> ProductMetadata,
    ImmutableDictionary<string, RegistryAtom> ComponentMarkers,
    RegistryAtom Run,
    ReadObservation<ShortcutFields> Shortcut,
    PendingEvidence Pending);

internal interface IRegistrationInventory
{
    RegistrationInventory Read(IReadOnlyList<Guid> componentCodes);
}
