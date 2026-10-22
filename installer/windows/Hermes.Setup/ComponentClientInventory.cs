using System.Collections.Immutable;
using System.ComponentModel;

namespace Hermes.Setup;

internal sealed record MsiClientResult(uint Code, string Product);

internal interface IMsiComponentClients
{
    MsiClientResult Enumerate(Guid component, uint index);
}

internal static class ComponentClientInventory
{
    private const uint NoMoreItems = 259;
    private const uint UnknownComponent = 1607;

    // Empty means native enumeration established no clients, not that an
    // expected product owns the component. Ownership still requires a match.
    internal static ImmutableArray<Guid> Read(Guid component, IMsiComponentClients native)
    {
        var clients = ImmutableArray.CreateBuilder<Guid>();
        for (uint index = 0; ; index++)
        {
            var result = native.Enumerate(component, index);
            if (result.Code == NoMoreItems ||
                (result.Code == UnknownComponent && index == 0))
                return clients.ToImmutable();
            if (result.Code != 0)
                throw new Win32Exception(unchecked((int)result.Code));
            var product = Product(result.Product);
            SetupIo.Require(index < 10_000 && !clients.Contains(product),
                SetupCode.OwnershipRefused);
            clients.Add(product);
        }
    }

    private static Guid Product(string text) =>
        Guid.TryParse(text, out var product) && product != Guid.Empty
            ? product : throw new SetupIoException(SetupCode.OwnershipRefused);
}
