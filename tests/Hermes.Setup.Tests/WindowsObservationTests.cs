using System.Collections.Immutable;
using System.Text;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed class WindowsObservationTests
{
    private const string Sid = "S-1-5-21-100-200-300-1001";
    private const string OtherSid = "S-1-5-21-100-200-300-1002";
    private const string Root = "C:/Users/test/Local/Hermes/App";
    private const string Link = "C:/Users/test/Programs/Hermes/Hermes.lnk";
    private const string Exe = Root + "/tray/Hermes.Tray.exe";
    private static readonly Guid Product = new("21F6DD66-3729-4F16-8582-EC311B80D325");
    private static readonly Guid Component = new("CEB36AA6-F3BD-4AE4-AE55-1C4C338209F8");
    private static readonly ShortcutFields ExpectedLink = new(Exe, "", Root);

    [Theory]
    [InlineData(1607)]
    [InlineData(259)]
    public void NewUnregisteredComponentIsKnownEmptyButHasNoExpectedOwner(int code)
    {
        var native = Native() with
        {
            RelatedRows = [new(259, "")],
            ClientRows = [new((uint)code, "", 0, "")]
        };
        var result = new MsiRegistrationReader(native).Read([Component]);
        Assert.Equal(ObservationState.Known, result.State);
        var inventory = Assert.IsType<MsiRegistration>(result.Value);
        Assert.Empty(inventory.RelatedProducts);
        Assert.Empty(inventory.ComponentClients[Component]);
        Assert.DoesNotContain(inventory.ComponentClients[Component],
            client => client.Client.ProductCode == Product);
    }

    [Theory]
    [InlineData(1, Sid)]
    [InlineData(2, Sid)]
    [InlineData(4, "")]
    [InlineData(2, OtherSid)]
    public void ActualContextPrincipalAndComponentKeyPathAreRetained(int context, string principal)
    {
        var row = Identity((uint)context, principal);
        var native = Native() with
        {
            ProductRows = [row, End()],
            ClientRows = [row, End()]
        };
        var result = new MsiRegistrationReader(native).Read([Component]);
        var inventory = Assert.IsType<MsiRegistration>(result.Value);
        Assert.Equal(Sid, inventory.InvokingSid);
        var product = Assert.Single(inventory.RelatedProducts);
        Assert.Equal((MsiContext)context, product.Identity.Context);
        Assert.Equal(principal, product.Identity.Sid);
        var client = Assert.Single(inventory.ComponentClients[Component]);
        Assert.Equal(product.Identity, client.Client);
        Assert.Equal(new MsiComponentPath(ComponentPathState.Local, Exe), client.KeyPath);
    }

    [Theory]
    [InlineData("1", 0, ProductRegistrationState.Advertised)]
    [InlineData("5", 0, ProductRegistrationState.Installed)]
    [InlineData("", 1610, ProductRegistrationState.Damaged)]
    [InlineData("unexpected", 0, ProductRegistrationState.Unknown)]
    public void ProductStateIsNotCollapsedIntoInstalled(
        string state, int code, object value)
    {
        var expected = Assert.IsType<ProductRegistrationState>(value);
        var native = Native();
        native = native with { Info = native.Info.SetItem("State", new((uint)code, state)) };
        var result = new MsiRegistrationReader(native).Read([]);
        var product = Assert.Single(Assert.IsType<MsiRegistration>(result.Value).RelatedProducts);
        Assert.Equal(expected, product.State);
        Assert.Equal(expected == ProductRegistrationState.Installed, product.PackageCode is not null);
    }

    [Theory]
    [InlineData(-6)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ComponentPathNativeStateIsNotRewrittenAsOwnership(int state)
    {
        var native = Native() with
        {
            ClientRows = [Identity(2, Sid), End()],
            KeyPath = new((ComponentPathState)state, Exe)
        };
        var inventory = Assert.IsType<MsiRegistration>(
            new MsiRegistrationReader(native).Read([Component]).Value);
        Assert.Equal((ComponentPathState)state,
            Assert.Single(inventory.ComponentClients[Component]).KeyPath.State);
    }

    [Fact]
    public void MultipleProductContextsAreExplicitlyAmbiguous()
    {
        var native = Native() with { ProductRows = [Identity(2, Sid), Identity(4, "")] };
        var result = new MsiRegistrationReader(native).Read([]);
        Assert.Equal(ObservationState.Unknown, result.State);
        Assert.Equal(ObservationFault.Ambiguous, result.Fault);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(1607, true)]
    [InlineData(1610, true)]
    public void FailedClientQueriesNeverReturnEmptyOrPartialSuccess(int code, bool partial)
    {
        var failure = new MsiIdentityResult((uint)code, "", 0, "");
        var native = Native() with
        {
            ClientRows = partial ? [Identity(2, Sid), failure] : [failure]
        };
        var result = new MsiRegistrationReader(native).Read([Component]);
        Assert.Equal(ObservationState.Unknown, result.State);
        Assert.Equal((uint)code, result.NativeCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public void ARelatedProductQueryFailureDiscardsThePartialInventory()
    {
        var native = Native() with { RelatedRows = [new(0, Product.ToString("B")), new(5, "")] };
        var result = new MsiRegistrationReader(native).Read([]);
        Assert.Equal(ObservationState.Unknown, result.State);
        Assert.Equal(5u, result.NativeCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public void MissingProductInformationIsNotProductAbsence()
    {
        var native = Native();
        native = native with { Info = native.Info.SetItem("PackageCode", new(1605, "")) };
        var result = new MsiRegistrationReader(native).Read([]);
        Assert.Equal(ObservationState.Unknown, result.State);
        Assert.Equal(1605u, result.NativeCode);
    }

    [Fact]
    public void DuplicateClientsAndUnsupportedContextsAreUnknown()
    {
        var duplicate = Native() with { ClientRows = [Identity(2, Sid), Identity(2, Sid)] };
        var unsupported = Native() with { ProductRows = [Identity(3, Sid), End()] };
        Assert.Equal(ObservationState.Unknown,
            new MsiRegistrationReader(duplicate).Read([Component]).State);
        Assert.Equal(ObservationState.Unknown,
            new MsiRegistrationReader(unsupported).Read([]).State);
    }

    [Theory]
    [InlineData(Exe, "", Root, ShortcutComparison.Matched)]
    [InlineData("C:/Elsewhere/foreign.exe", "", Root, ShortcutComparison.WrongTarget)]
    [InlineData(Exe, "--foreign", Root, ShortcutComparison.WrongArguments)]
    [InlineData(Exe, "", "C:/Elsewhere", ShortcutComparison.WrongWorkingDirectory)]
    public void ActualShortcutFieldsHaveDistinctComparisons(
        string target, string arguments, string directory, object value)
    {
        var expected = Assert.IsType<ShortcutComparison>(value);
        var actual = ReadObservation<ShortcutFields>.Known(new(target, arguments, directory));
        Assert.Equal(expected, ShortcutClassifier.Compare(actual, ExpectedLink, Paths(Exe, Root)));
    }

    [Fact]
    public void ShortcutMissingUnreadableAndAliasedAreDistinct()
    {
        Assert.Equal(ShortcutComparison.Missing, ShortcutClassifier.Compare(
            ReadObservation<ShortcutFields>.Missing(), ExpectedLink, Paths()));
        Assert.Equal(ShortcutComparison.Unreadable, ShortcutClassifier.Compare(
            ReadObservation<ShortcutFields>.Unknown(ObservationFault.Unreadable), ExpectedLink, Paths()));
        var paths = Paths(Exe, Root);
        paths = paths with { Values = paths.Values.SetItem(Exe,
            ReadObservation<PathIdentity>.Known(new("C:/Foreign/Tray.exe"))) };
        Assert.Equal(ShortcutComparison.Uncertain, ShortcutClassifier.Compare(
            ReadObservation<ShortcutFields>.Known(ExpectedLink), ExpectedLink, paths));
    }

    [Fact]
    public void RunParsingPreservesActualArgumentsAndRequiresUnambiguousQuoting()
    {
        var parsed = RunCommandReader.Read(Text($"\"{Exe}\" --first \"two words\""));
        Assert.Equal(new RunCommand(Exe, "--first \"two words\""), parsed.Value);
        Assert.Equal(ObservationState.Unknown, RunCommandReader.Read(Text(Exe + " --first")).State);
        Assert.Equal(ObservationState.Unknown, RunCommandReader.Read(Text($"\"{Exe}\"suffix")).State);
        Assert.Equal(ObservationState.Missing, RunCommandReader.Read(
            new(RegistryAtomKind.Missing, null, null, [])).State);
    }

    [Theory]
    [InlineData("C:/Elsewhere/a", "C:/Elsewhere/b", PendingDisposition.None)]
    [InlineData(Root + "Other/a", "C:/Elsewhere/b", PendingDisposition.None)]
    [InlineData(Root + "/a", "", PendingDisposition.Relevant)]
    [InlineData("C:/Elsewhere/a", Root + "/b", PendingDisposition.Relevant)]
    [InlineData("C:/Users/test/Local/Hermes", "", PendingDisposition.Relevant)]
    [InlineData("C:/Elsewhere/a", Link, PendingDisposition.Relevant)]
    public void PendingOperationsUseBothEndpointsAndComponentBoundaries(
        string source, string destination, object value)
    {
        var expected = Assert.IsType<PendingDisposition>(value);
        var paths = destination.Length == 0 ? Paths(source) : Paths(source, destination);
        var result = PendingFileClassifier.Read([Raw(source, destination), MissingRaw()],
            [Root, Link], paths);
        Assert.Equal(expected, result.Disposition);
        Assert.Single(result.Operations);
    }

    [Fact]
    public void NtPrefixReplacementAndDeletionAreDecodedWithoutExecutingAnything()
    {
        var outside = "C:/Elsewhere/a";
        var destination = Root + "/b";
        var rename = PendingFileClassifier.Read(
            [Raw(@"\??\C:\Elsewhere\a", @"!\??\C:\Users\test\Local\Hermes\App\b"), MissingRaw()],
            [Root], Paths(outside, destination));
        Assert.Equal(PendingDisposition.Relevant, rename.Disposition);
        Assert.Equal(destination, Assert.Single(rename.Operations).Destination);
        var deletion = PendingFileClassifier.Read(
            [Raw(@"\??\C:\Users\test\Local\Hermes\App\b", ""), MissingRaw()],
            [Root], Paths(destination));
        Assert.Null(Assert.Single(deletion.Operations).Destination);
        Assert.Equal(PendingDisposition.Relevant, deletion.Disposition);
    }

    [Fact]
    public void PhysicalAliasDanglingAndMissingFakeEvidenceCannotProveOutside()
    {
        const string outside = "C:/Elsewhere/a";
        var alias = Paths(outside);
        alias = alias with { Values = alias.Values.SetItem(outside,
            ReadObservation<PathIdentity>.Known(new(Root + "/a"))) };
        var dangling = Paths(outside);
        dangling = dangling with { Values = dangling.Values.SetItem(outside,
            ReadObservation<PathIdentity>.Unknown(ObservationFault.Identity)) };
        Assert.Equal(PendingDisposition.Alias, Pending(outside, alias).Disposition);
        Assert.Equal(PendingDisposition.Unknown, Pending(outside, dangling).Disposition);
        Assert.Equal(PendingDisposition.Unknown, Pending(outside, Paths()).Disposition);
    }

    [Fact]
    public void MalformedUnsupportedAndFailedPendingQueriesAreNotNone()
    {
        var odd = Encoding.Unicode.GetBytes("C:/a\0C:/b\0C:/c\0").ToImmutableArray();
        ReadObservation<RegistryBytes>[] invalid =
        [
            ReadObservation<RegistryBytes>.Known(new(7, odd)),
            ReadObservation<RegistryBytes>.Known(new(7, [0, 0, 0])),
            ReadObservation<RegistryBytes>.Known(new(1, Encoding.Unicode.GetBytes("\0\0").ToImmutableArray())),
            ReadObservation<RegistryBytes>.Unknown(ObservationFault.NativeQuery, 5),
            Raw(@"\Device\HarddiskVolume1\a", "")
        ];
        foreach (var value in invalid)
            Assert.Equal(PendingDisposition.Unknown, PendingFileClassifier.Read(
                [value, MissingRaw()], [Root], Paths()).Disposition);
    }

    [Fact]
    public void KnownMissingOrEmptyPendingValuesAreNone()
    {
        var empty = ReadObservation<RegistryBytes>.Known(new(7,
            Encoding.Unicode.GetBytes("\0\0").ToImmutableArray()));
        Assert.Equal(PendingDisposition.None, PendingFileClassifier.Read(
            [empty, MissingRaw()], [Root], Paths()).Disposition);
    }

    private static RegistryAtom Text(string value) => new(RegistryAtomKind.Text, value, null, []);
    private static MsiIdentityResult Identity(uint context, string sid) => new(0, Product.ToString("B"), context, sid);
    private static MsiIdentityResult End() => new(259, "", 0, "");
    private static ReadObservation<RegistryBytes> MissingRaw() => ReadObservation<RegistryBytes>.Missing();
    private static ReadObservation<RegistryBytes> Raw(params string[] parts) =>
        ReadObservation<RegistryBytes>.Known(new(7,
            Encoding.Unicode.GetBytes(string.Join('\0', parts) + "\0\0").ToImmutableArray()));

    private static PendingEvidence Pending(string source, IPathIdentityQuery paths) =>
        PendingFileClassifier.Read([Raw(source, ""), MissingRaw()], [Root], paths);

    private static IdentityFake Paths(params string[] paths) => new(
        paths.Concat([Root, Link]).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(path => path,
                path => ReadObservation<PathIdentity>.Known(new(path)),
                StringComparer.OrdinalIgnoreCase));

    private static MsiFake Native() => new(
        [new(0, Product.ToString("B")), new(259, "")],
        [Identity(2, Sid), End()], [new(1607, "", 0, "")],
        new Dictionary<string, MsiTextResult>
        {
            ["State"] = new(0, "5"),
            ["PackageCode"] = new(0, "{4DDF7445-9913-48E7-9B4F-DC233AFBB499}"),
            ["VersionString"] = new(0, "1.2.3"),
            ["LocalPackage"] = new(0, "C:/Windows/Installer/example.msi"),
            ["InstallLocation"] = new(0, Root)
        }.ToImmutableDictionary(), new(ComponentPathState.Local, Exe));

    private sealed record IdentityFake(
        ImmutableDictionary<string, ReadObservation<PathIdentity>> Values) : IPathIdentityQuery
    {
        public ReadObservation<PathIdentity> Read(string path) => Values.GetValueOrDefault(
            path, ReadObservation<PathIdentity>.Unknown(ObservationFault.Identity));
    }

    private sealed record MsiFake(
        ImmutableArray<MsiClientResult> RelatedRows,
        ImmutableArray<MsiIdentityResult> ProductRows,
        ImmutableArray<MsiIdentityResult> ClientRows,
        ImmutableDictionary<string, MsiTextResult> Info,
        MsiComponentPath KeyPath) : IMsiRegistrationQueries
    {
        public string CurrentSid() => Sid;
        public MsiClientResult Related(uint index) => RelatedRows[checked((int)index)];
        public MsiIdentityResult Product(Guid product, uint index) => ProductRows[checked((int)index)];
        public MsiIdentityResult Client(Guid component, uint index) => ClientRows[checked((int)index)];
        public MsiTextResult ProductInfo(MsiPrincipal principal, string property) => Info[property];
        public MsiComponentPath ComponentPath(Guid component, MsiPrincipal principal) => KeyPath;
    }
}
