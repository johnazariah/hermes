using System.Collections.Immutable;
using System.ComponentModel;
using System.Security;

namespace Hermes.Setup;

internal enum MsiContext : uint { UserManaged = 1, UserUnmanaged = 2, Machine = 4 }
internal enum ProductRegistrationState { Unknown, Advertised, Installed, Damaged }
internal enum ComponentPathState
{
    BadConfiguration = -6, Incomplete = -5, MoreData = -3, InvalidArgument = -2,
    Unknown = -1, Broken = 0, Advertised = 1, Absent = 2, Local = 3, Source = 4
}

internal sealed record MsiPrincipal(Guid ProductCode, MsiContext Context, string Sid);
internal sealed record MsiIdentityResult(uint Code, string Product, uint Context, string Sid);
internal sealed record MsiTextResult(uint Code, string Text);
internal sealed record MsiComponentPath(ComponentPathState State, string Path);
internal sealed record ComponentRegistration(MsiPrincipal Client, MsiComponentPath KeyPath);

internal sealed record RegisteredProduct(
    MsiPrincipal Identity,
    ProductRegistrationState State,
    Guid? PackageCode,
    Version? Version,
    string? LocalPackage,
    string? InstallLocation);

internal sealed record MsiRegistration(
    string InvokingSid,
    ImmutableArray<RegisteredProduct> RelatedProducts,
    ImmutableDictionary<Guid, ImmutableArray<ComponentRegistration>> ComponentClients);

internal interface IMsiRegistrationQueries
{
    string CurrentSid();
    MsiClientResult Related(uint index);
    MsiIdentityResult Product(Guid product, uint index);
    MsiIdentityResult Client(Guid component, uint index);
    MsiTextResult ProductInfo(MsiPrincipal principal, string property);
    MsiComponentPath ComponentPath(Guid component, MsiPrincipal principal);
}

internal sealed class MsiRegistrationReader(IMsiRegistrationQueries native)
{
    internal ReadObservation<MsiRegistration> Read(IReadOnlyList<Guid> components)
    {
        try
        {
            var sid = native.CurrentSid();
            RequireSid(sid);
            return ReadObservation<MsiRegistration>.Known(new(sid, Products(),
                components.Distinct().ToImmutableDictionary(code => code, Clients)));
        }
        catch (Win32Exception error)
        { return ReadObservation<MsiRegistration>.Unknown(ObservationFault.NativeQuery, unchecked((uint)error.NativeErrorCode)); }
        catch (SetupIoException)
        { return ReadObservation<MsiRegistration>.Unknown(ObservationFault.Ambiguous); }
        catch (FormatException)
        { return ReadObservation<MsiRegistration>.Unknown(ObservationFault.Malformed); }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException or SecurityException)
        { return ReadObservation<MsiRegistration>.Unknown(ObservationFault.Identity); }
    }

    private ImmutableArray<RegisteredProduct> Products()
    {
        var products = ImmutableArray.CreateBuilder<RegisteredProduct>();
        for (uint index = 0; ; index++)
        {
            var row = native.Related(index);
            if (row.Code == 259)
                return products.ToImmutable();
            Check(row.Code);
            var code = ProductCode(row.Product);
            SetupIo.Require(index < 10_000 &&
                !products.Any(product => product.Identity.ProductCode == code),
                SetupCode.OwnershipRefused);
            products.Add(ProductContext(code));
        }
    }

    private RegisteredProduct ProductContext(Guid product)
    {
        var row = native.Product(product, 0);
        Check(row.Code);
        var identity = Identity(row);
        SetupIo.Require(identity.ProductCode == product, SetupCode.OwnershipRefused);
        var next = native.Product(product, 1);
        SetupIo.Require(next.Code != 0, SetupCode.OwnershipRefused);
        if (next.Code != 259)
            Check(next.Code);
        return Product(identity);
    }

    private RegisteredProduct Product(MsiPrincipal identity)
    {
        var state = native.ProductInfo(identity, "State");
        if (state.Code == 1610)
            return new(identity, ProductRegistrationState.Damaged, null, null, null, null);
        Check(state.Code);
        return state.Text switch
        {
            "1" => new(identity, ProductRegistrationState.Advertised, null, null, null, null),
            "5" => Installed(identity),
            _ => new(identity, ProductRegistrationState.Unknown, null, null, null, null)
        };
    }

    private RegisteredProduct Installed(MsiPrincipal identity) => new(
        identity, ProductRegistrationState.Installed,
        ProductCode(Info(identity, "PackageCode")), Version.Parse(Info(identity, "VersionString")),
        Info(identity, "LocalPackage"), Info(identity, "InstallLocation"));

    private string Info(MsiPrincipal identity, string property)
    {
        var result = native.ProductInfo(identity, property);
        Check(result.Code);
        return result.Text;
    }

    private ImmutableArray<ComponentRegistration> Clients(Guid component)
    {
        SetupIo.Require(component != Guid.Empty, SetupCode.OwnershipRefused);
        var adapter = new ContextClients(native);
        return ComponentClientInventory.Read(component, adapter).Select(product =>
        {
            var principal = adapter.Identities[product];
            return new ComponentRegistration(principal, native.ComponentPath(component, principal));
        }).ToImmutableArray();
    }

    private sealed class ContextClients(IMsiRegistrationQueries queries) : IMsiComponentClients
    {
        internal Dictionary<Guid, MsiPrincipal> Identities { get; } = [];

        public MsiClientResult Enumerate(Guid component, uint index)
        {
            var row = queries.Client(component, index);
            if (row.Code != 0)
                return new(row.Code, row.Product);
            var identity = Identity(row);
            Identities.TryAdd(identity.ProductCode, identity);
            return new(0, identity.ProductCode.ToString("B"));
        }
    }

    private static MsiPrincipal Identity(MsiIdentityResult row)
    {
        var context = (MsiContext)row.Context;
        SetupIo.Require(context is MsiContext.UserManaged or MsiContext.UserUnmanaged or MsiContext.Machine,
            SetupCode.OwnershipRefused);
        if (context == MsiContext.Machine)
            SetupIo.Require(row.Sid.Length == 0, SetupCode.OwnershipRefused);
        else
            RequireSid(row.Sid);
        return new(ProductCode(row.Product), context, row.Sid);
    }

    private static void RequireSid(string sid) =>
        SetupIo.Require(sid.StartsWith("S-1-", StringComparison.Ordinal) &&
            sid.Split('-').Length >= 4 &&
            sid.Split('-').Skip(2).All(part => part.Length > 0 && part.All(char.IsAsciiDigit)),
            SetupCode.OwnershipRefused);

    private static Guid ProductCode(string text)
    {
        var code = Guid.Parse(text);
        SetupIo.Require(code != Guid.Empty, SetupCode.OwnershipRefused);
        return code;
    }

    private static void Check(uint code)
    {
        if (code != 0)
            throw new Win32Exception(unchecked((int)code));
    }
}
