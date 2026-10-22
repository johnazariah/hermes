using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Hermes.Installer.Control;
using Hermes.Servicing;

namespace Hermes.Setup;

[SupportedOSPlatform("windows")]
internal sealed class WindowsMsiRegistrationQueries : IMsiRegistrationQueries
{
    private const string Library = "msi.dll";
    private const int Capacity = 32_768;

    public string CurrentSid() => WindowsPeers.CurrentSid();

    public MsiClientResult Related(uint index)
    {
        var product = new StringBuilder(39);
        var code = EnumRelated(HermesServicingProtocol.UpgradeCode.ToString("B"), 0, index, product);
        return new(code, product.ToString());
    }

    public MsiIdentityResult Product(Guid product, uint index) => Enumerate(product, index, false);
    public MsiIdentityResult Client(Guid component, uint index) => Enumerate(component, index, true);

    private static MsiIdentityResult Enumerate(Guid identity, uint index, bool client)
    {
        var product = new StringBuilder(39);
        var sid = new StringBuilder(256);
        uint[] context = [0];
        uint[] length = [(uint)sid.Capacity];
        // Null SID means the invoking user, not S-1-1-0/all users.
        var code = client
            ? EnumClients(identity.ToString("B"), null, 7, index, product, context, sid, length)
            : EnumProducts(identity.ToString("B"), null, 7, index, product, context, sid, length);
        return new(code, product.ToString(), context[0], sid.ToString());
    }

    public MsiTextResult ProductInfo(MsiPrincipal principal, string property)
    {
        var text = new StringBuilder(Capacity);
        uint[] length = [(uint)text.Capacity];
        var code = GetProductInfo(principal.ProductCode.ToString("B"), Sid(principal),
            (uint)principal.Context, property, text, length);
        return new(code, text.ToString());
    }

    public MsiComponentPath ComponentPath(Guid component, MsiPrincipal principal)
    {
        var text = new StringBuilder(Capacity);
        uint[] length = [(uint)text.Capacity];
        var state = GetComponentPath(principal.ProductCode.ToString("B"), component.ToString("B"),
            Sid(principal), (uint)principal.Context, text, length);
        return new((ComponentPathState)state, text.ToString());
    }

    private static string? Sid(MsiPrincipal principal) =>
        principal.Context == MsiContext.Machine ? null : principal.Sid;

    [DllImport(Library, EntryPoint = "MsiEnumRelatedProductsW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint EnumRelated(string upgrade, uint reserved, uint index, [Out] StringBuilder product);

    [DllImport(Library, EntryPoint = "MsiEnumProductsExW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint EnumProducts(string product, string? user, uint contexts, uint index,
        [Out] StringBuilder installedProduct, [Out] uint[] context, [Out] StringBuilder sid, [In, Out] uint[] sidLength);

    [DllImport(Library, EntryPoint = "MsiEnumClientsExW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint EnumClients(string component, string? user, uint contexts, uint index,
        [Out] StringBuilder product, [Out] uint[] context, [Out] StringBuilder sid, [In, Out] uint[] sidLength);

    [DllImport(Library, EntryPoint = "MsiGetProductInfoExW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetProductInfo(string product, string? sid, uint context, string property,
        [Out] StringBuilder value, [In, Out] uint[] length);

    [DllImport(Library, EntryPoint = "MsiGetComponentPathExW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetComponentPath(string product, string component, string? sid, uint context,
        [Out] StringBuilder path, [In, Out] uint[] length);
}
