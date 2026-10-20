using System.Collections.Immutable;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed class DirectoryRemovalAuthoringTests
{
    private const string Removals = "RemoveFile";
    private const string Runtimes = "Dir_1c090cc2f18ea645eaa16f70eb2e57c9";
    private const string WindowsX64 = "Dir_ce27e8bcc87217462629bca44b9340ed";
    private const string FirstPath = "tray/runtimes/win-x64/native/first.dll";
    private const string DirectPath = "tray/runtimes/zz-direct.bin";
    private const string SecondPath = "tray/runtimes/win-arm64/native/second.dll";
    private const string PrefixPath = "tray/runtimes-extra/foreign.dll";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FirstDescendantOwnsAllManifestAncestorsInEveryMode(int mode)
    {
        var profile = Assert.IsType<Effect<ValidatedPackageProfile>.Success>(
            Nested().Validate((PackageAuthoringMode)mode)).Value;
        Assert.Equal(11, profile.DirectoryRemovals.Length);
        var directories = new[] { Runtimes, WindowsX64, AuthoringFixture.Dir("tray"),
            AuthoringFixture.Dir("tray/runtimes/win-x64/native") };
        foreach (var directory in directories)
            Assert.Contains(profile.DirectoryRemovals, removal =>
                removal.Id == RemovalId(directory) && removal.DirectoryId == directory &&
                removal.ComponentId == AuthoringFixture.File(FirstPath).ComponentId);
        Assert.Equal(Runtimes, AuthoringFixture.Dir("tray/runtimes"));
        Assert.Equal(WindowsX64, AuthoringFixture.Dir("tray/runtimes/win-x64"));
    }

    [Fact]
    public void OwnershipUsesPreservedManifestOrderWithoutResorting()
    {
        var manifest = AggregateResult.Require(Nested().Validate()).Manifest;
        var fixture = AuthoringFixture.Create(manifest with
        {
            Files = manifest.Files.Reverse().ToImmutableArray()
        });
        var profile = Assert.IsType<Effect<ValidatedPackageProfile>.Success>(
            fixture.Validate()).Value;
        Assert.Equal(AuthoringFixture.File(SecondPath).ComponentId,
            Assert.Single(profile.DirectoryRemovals,
                removal => removal.DirectoryId == Runtimes).ComponentId);
        Refused(Change(fixture, 1, AuthoringFixture.File(FirstPath).ComponentId));
    }

    [Theory]
    [InlineData(DirectPath)]
    [InlineData(SecondPath)]
    [InlineData(PrefixPath)]
    [InlineData("tray/Hermes.Tray.exe")]
    [InlineData("service/Hermes.Service.exe")]
    public void OtherDirectDescendantSiblingOrAncestorOwnersAreRejected(string path) =>
        Refused(Change(Nested(), 1, AuthoringFixture.File(path).ComponentId));

    [Theory]
    [InlineData("AutoStart")]
    [InlineData("StartMenuShortcut")]
    [InlineData("ForeignComponent")]
    public void NonpayloadAndUnknownOwnersAreRejected(string component) =>
        Refused(Change(Nested(), 1, component));

    [Fact]
    public void ExistingFileParentAlsoRequiresItsFirstManifestOwner()
    {
        var fixture = AuthoringFixture.Create();
        var index = Index(fixture, RemovalId(AuthoringFixture.Dir("service")));
        Refused(fixture.Cell(Removals, index, 1,
            AuthoringFixture.File("service/Hermes.Service.exe").ComponentId));
    }

    [Theory]
    [InlineData(0, "ForeignRemoval")]
    [InlineData(2, "*")]
    [InlineData(2, "*.json")]
    [InlineData(2, "owner.json")]
    [InlineData(3, WindowsX64)]
    [InlineData(3, "TARGETDIR")]
    [InlineData(3, "LocalAppDataFolder")]
    [InlineData(3, "ProgramMenuFolder")]
    [InlineData(3, "INSTALLFOLDER")]
    [InlineData(3, "HERMES_COMPANY")]
    [InlineData(4, "0")]
    [InlineData(4, "1")]
    [InlineData(4, "3")]
    public void AncestorCleanupCannotBroadenItsIdentityTargetOrMode(int column, string value) =>
        Refused(Change(Nested(), column, value));

    [Theory]
    [InlineData(Runtimes)]
    [InlineData(WindowsX64)]
    public void EveryAncestorRemovalIsRequired(string directory)
    {
        var fixture = Nested();
        Refused(fixture.Rows(Removals,
            fixture.Snapshot.RequireTable(Removals).RemoveAt(Index(fixture, RemovalId(directory)))));
    }

    [Fact]
    public void DuplicateAndAdditionalRemovalRowsRemainRejected()
    {
        var fixture = Nested();
        var rows = fixture.Snapshot.RequireTable(Removals);
        var row = rows[Index(fixture, RemovalId(Runtimes))];
        Refused(fixture.Rows(Removals, rows.Add(row)));
        Refused(fixture.Append(Removals, "AdditionalRemoval", row[1], "", Runtimes, "2"));
    }

    [Theory]
    [InlineData("RemoveMenuFolder", "AutoStart")]
    [InlineData("RemoveApplicationFolder", "StartMenuShortcut")]
    [InlineData("RemoveCompanyFolder", "StartMenuShortcut")]
    public void SpecialRemovalOwnershipRemainsExact(string id, string component)
    {
        var fixture = Nested();
        Refused(fixture.Cell(Removals, Index(fixture, id), 1, component));
    }

    private static AuthoringFixture Nested()
    {
        var manifest = AggregateResult.Require(AuthoringFixture.Create().Validate()).Manifest;
        return AuthoringFixture.Create(manifest with
        {
            Files = new[] { FirstPath, DirectPath, SecondPath, PrefixPath }
                .Select(AuthoringFixture.File).Concat(manifest.Files).ToImmutableArray()
        });
    }

    private static string RemovalId(string directory) => "Remove_" + directory;

    private static int Index(AuthoringFixture fixture, string id) =>
        fixture.Snapshot.RequireTable(Removals)
            .Select((row, index) => (Id: row[0], Index: index))
            .Single(entry => entry.Id == id).Index;

    private static AuthoringFixture Change(AuthoringFixture fixture, int column, string value) =>
        fixture.Cell(Removals, Index(fixture, RemovalId(Runtimes)), column, value);

    private static void Refused(AuthoringFixture fixture) =>
        Assert.Equal(SetupCode.MediaInvalid,
            Assert.IsType<Effect<ValidatedPackageProfile>.Failure>(fixture.Validate()).Code);
}
