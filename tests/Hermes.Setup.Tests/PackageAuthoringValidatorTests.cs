using System.Collections.Immutable;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed class PackageAuthoringValidatorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CurrentProfileProducesUsefulResourceExpectations(int value)
    {
        var mode = (PackageAuthoringMode)value;
        var success = Assert.IsType<Effect<ValidatedPackageProfile>.Success>(
            AuthoringFixture.Create().Validate(mode));
        var profile = success.Value;
        Assert.Equal(4, profile.Manifest.Files.Length);
        Assert.Equal(6, profile.Components.Length);
        Assert.Equal(8, profile.Registry.Length);
        Assert.Equal(5, profile.DirectoryRemovals.Length);
        Assert.Equal(mode != PackageAuthoringMode.RegisteredCache, profile.HasCompleteSource);
        Assert.Equal(@"[INSTALLFOLDER]tray\Hermes.Tray.exe", profile.Shortcut.Target);
        Assert.Equal("", profile.Shortcut.Arguments);
        Assert.Equal(2, profile.Guard.Exports.Length);
        Assert.All(profile.Components, component =>
            Assert.Contains(profile.Registry, value => value.Id == component.RegistryKeyPath));
    }

    [Fact]
    public void CachedCabinetStrippingNeverCertifiesFullSource()
    {
        var fixture = AuthoringFixture.Create();
        fixture = fixture with
        {
            Snapshot = fixture.Snapshot with
            {
                Package = fixture.Snapshot.Package with
                { Streams = ImmutableDictionary<string, uint>.Empty }
            }
        };
        var cached = Assert.IsType<Effect<ValidatedPackageProfile>.Success>(
            fixture.Validate(PackageAuthoringMode.RegisteredCache));
        Assert.False(cached.Value.HasCompleteSource);
        Refused(fixture);
        Refused(fixture, PackageAuthoringMode.RetainedSource);
    }

    [Theory]
    [InlineData("HermesPreflight")]
    [InlineData("HermesTransactionFence")]
    public void EveryUnsupportedCustomActionTypeFails(string action)
    {
        var fixture = AuthoringFixture.Create();
        var index = fixture.Snapshot.RequireTable("CustomAction")
            .Select((row, position) => (row, position)).Single(pair => pair.row[0] == action).position;
        foreach (var type in new[] { "0", "2", "17", "65", "129", "193", "257", "1025", "2049", "4097" })
            Refused(fixture.Cell("CustomAction", index, 1, type));
    }

    [Theory]
    [InlineData("CustomAction", 0, 0, "ForeignAction")]
    [InlineData("CustomAction", 0, 2, "ForeignBinary")]
    [InlineData("CustomAction", 0, 3, "WrongExport")]
    [InlineData("CustomAction", 1, 2, "HermesPayloadManifest")]
    [InlineData("CustomAction", 1, 3, "HermesPreflight")]
    [InlineData("InstallExecuteSequence", 7, 1, "NOT Installed")]
    [InlineData("InstallExecuteSequence", 7, 2, "1401")]
    [InlineData("InstallExecuteSequence", 8, 2, "1398")]
    [InlineData("InstallExecuteSequence", 9, 2, "1499")]
    [InlineData("InstallExecuteSequence", 10, 1, "REMOVE=\"ALL\"")]
    [InlineData("InstallExecuteSequence", 10, 2, "1503")]
    [InlineData("InstallExecuteSequence", 11, 1, "NOT Installed")]
    [InlineData("InstallExecuteSequence", 11, 2, "1499")]
    [InlineData("InstallExecuteSequence", 25, 2, "6599")]
    [InlineData("AdminExecuteSequence", 0, 1, "NOT Installed")]
    [InlineData("AdminExecuteSequence", 0, 2, "1501")]
    [InlineData("AdvtExecuteSequence", 0, 1, "Installed")]
    [InlineData("AdvtExecuteSequence", 0, 2, "1501")]
    public void ActionAndSequenceChangesFail(string table, int row, int column, string value) =>
        Refused(AuthoringFixture.Create().Cell(table, row, column, value));

    [Theory]
    [InlineData("InstallExecuteSequence", "ForceReboot", "", "6500")]
    [InlineData("InstallExecuteSequence", "ScheduleReboot", "", "6500")]
    [InlineData("InstallExecuteSequence", "SelfRegModules", "", "5600")]
    [InlineData("InstallExecuteSequence", "MoveFiles", "", "3800")]
    [InlineData("InstallUISequence", "InstallFiles", "", "4000")]
    [InlineData("AdminExecuteSequence", "ForeignAction", "", "1300")]
    [InlineData("AdvtExecuteSequence", "ForeignAction", "", "1300")]
    public void NoAdditionalExecutableSequenceActionIsIgnored(
        string table, string action, string condition, string sequence) =>
        Refused(AuthoringFixture.Create().Append(table, action, condition, sequence));

    [Fact]
    public void ExtraCustomActionFailsEvenWhenUnscheduled() =>
        Refused(AuthoringFixture.Create().Append(
            "CustomAction", "Unscheduled", "1", "HermesServicingGuard", "HermesPreflight"));

    [Theory]
    [InlineData("InstallExecuteSequence", 7)]
    [InlineData("InstallExecuteSequence", 10)]
    [InlineData("AdminExecuteSequence", 0)]
    [InlineData("AdvtExecuteSequence", 0)]
    public void MissingGuardsFail(string table, int index)
    {
        var fixture = AuthoringFixture.Create();
        Refused(fixture.Rows(table, fixture.Snapshot.RequireTable(table).RemoveAt(index)));
    }

    [Fact]
    public void ExtendedCustomActionFlagsCannotHideOutsideTheOriginalQuery()
    {
        var fixture = AuthoringFixture.Create();
        var definitions = fixture.Snapshot.ColumnDefinitions.Add(
            ImmutableArray.Create("CustomAction", "5", "ExtendedType", "5378"));
        fixture = fixture with { Snapshot = fixture.Snapshot with { ColumnDefinitions = definitions } };
        fixture = fixture.Rows("CustomAction", fixture.Snapshot.RequireTable("CustomAction")
            .Select(row => row.Add("0")).ToImmutableArray());
        Assert.IsType<Effect<ValidatedPackageProfile>.Success>(fixture.Validate());
        Refused(fixture.Cell("CustomAction", 1, 4, "1"));
    }

    [Theory]
    [InlineData("HermesServicingGuard")]
    [InlineData("HermesPayloadManifest")]
    public void RequiredBinaryMustExist(string name)
    {
        var fixture = AuthoringFixture.Create();
        Refused(fixture with
        {
            Snapshot = fixture.Snapshot with { Binaries = fixture.Snapshot.Binaries.Remove(name) }
        });
    }

    [Fact]
    public void CandidateCannotNominateItsOwnGuardTrust()
    {
        var fixture = AuthoringFixture.Create();
        var bytes = AuthoringFixture.NativeImage();
        bytes[0x205] = 0x90;
        Refused(fixture.Guard(bytes, approveSyntheticImage: false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void StructuralNativeExportFailuresAreActuallyInspected(int mutation)
    {
        var bytes = AuthoringFixture.NativeImage();
        switch (mutation)
        {
            case 0: AuthoringFixture.Put16(bytes, 0x84, 0x14C); break;
            case 1: AuthoringFixture.Put16(bytes, 0x96, 0x22); break;
            case 2: AuthoringFixture.Put32(bytes, 0x318, 1); break;
            case 3: bytes[0x360] = (byte)'X'; break;
            case 4: AuthoringFixture.Put32(bytes, 0x340, 0x1160); break;
            case 5: AuthoringFixture.Put16(bytes, 0x352, 0); break;
        }
        // Synthetic trust is deliberately updated to reach PE structural checks.
        Refused(AuthoringFixture.Create().Guard(bytes, approveSyntheticImage: true));
    }

    [Theory]
    [InlineData("Component", 0, 3, "256")]
    [InlineData("Component", 0, 3, "268")]
    [InlineData("Component", 0, 3, "516")]
    [InlineData("Component", 0, 4, "NOT Installed")]
    [InlineData("Component", 0, 5, "ForeignRegistryKeyPath")]
    [InlineData("Component", 0, 1, "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}")]
    [InlineData("File", 0, 1, "AutoStart")]
    [InlineData("File", 0, 4, "File_Companion")]
    [InlineData("File", 0, 7, "2")]
    [InlineData("FeatureComponents", 0, 0, "ForeignFeature")]
    [InlineData("FeatureComponents", 0, 1, "ForeignComponent")]
    [InlineData("Feature", 0, 5, "0")]
    [InlineData("Feature", 0, 7, "1")]
    [InlineData("Registry", 0, 1, "2")]
    [InlineData("Registry", 0, 2, @"Software\Foreign")]
    [InlineData("Registry", 0, 4, "1")]
    [InlineData("Registry", 5, 4, @"C:\Elsewhere")]
    [InlineData("Registry", 7, 4, "\"[INSTALLFOLDER]tray\\Hermes.Tray.exe\" --foreign")]
    [InlineData("Shortcut", 0, 4, @"[INSTALLFOLDER]service\Hermes.Service.exe")]
    [InlineData("Shortcut", 0, 5, "--foreign")]
    [InlineData("Shortcut", 0, 11, "TARGETDIR")]
    [InlineData("RemoveFile", 0, 2, "*")]
    [InlineData("RemoveFile", 0, 2, "*.json")]
    [InlineData("RemoveFile", 0, 3, "LocalAppDataFolder")]
    [InlineData("RemoveFile", 0, 4, "3")]
    public void ResourceTopologyAndOwnedFormatsAreEnforced(
        string table, int row, int column, string value) =>
        Refused(AuthoringFixture.Create().Cell(table, row, column, value));

    [Fact]
    public void RegistryRowsOutsideTheProfileAreNotIgnored() =>
        Refused(AuthoringFixture.Create().Append("Registry",
            "Foreign", "1", @"Software\Foreign", "Value", "data", "AutoStart"));

    [Theory]
    [InlineData("Upgrade", 0, 1, "1.0.0")]
    [InlineData("Upgrade", 0, 2, "2.0.0")]
    [InlineData("Upgrade", 0, 4, "257")]
    [InlineData("Upgrade", 0, 5, "ProductFeature")]
    [InlineData("Upgrade", 0, 6, "WIX_UPGRADE_DETECTED")]
    [InlineData("Upgrade", 1, 1, "0.0.0")]
    [InlineData("Upgrade", 1, 4, "258")]
    [InlineData("Upgrade", 1, 6, "HERMES_RELATED_PRODUCTS")]
    [InlineData("LaunchCondition", 0, 0, "1")]
    [InlineData("Property", 6, 1, "Force")]
    [InlineData("Property", 7, 1, "DisableShutdown")]
    [InlineData("Property", 8, 1, "HERMES_SERVICING_RUN;HERMES_SERVICING_ABI")]
    [InlineData("AppSearch", 0, 0, "ALLUSERS")]
    [InlineData("RegLocator", 0, 1, "2")]
    [InlineData("RegLocator", 0, 4, "1")]
    public void UpgradeAndPropertyAuthoringIsNotAClaimOfPermission(
        string table, int row, int column, string value) =>
        Refused(AuthoringFixture.Create().Cell(table, row, column, value));

    [Theory]
    [InlineData("HERMES_SERVICING_RUN", "11111111-2222-3333-4444-555555555555")]
    [InlineData("HERMES_SERVICING_ABI", "1")]
    [InlineData("HERMES_ALLOW_ROLLBACK", "1")]
    [InlineData("DISABLEROLLBACK", "1")]
    [InlineData("PATCH", "patch.msp")]
    public void AuthoredPublicPropertyDefaultsDoNotGrantPermission(string name, string value) =>
        Refused(AuthoringFixture.Create().Append("Property", name, value));

    [Fact]
    public void ChangedApplicationRootIsClassifiedAsRootMismatch() =>
        Assert.Equal(SetupCode.RootMismatch,
            Assert.IsType<Effect<ValidatedPackageProfile>.Failure>(
                AuthoringFixture.Create().Cell("Directory", 4, 2, "Other").Validate()).Code);

    [Theory]
    [InlineData("ALLUSERS", "1")]
    [InlineData("INSTALLFOLDER", @"C:\Elsewhere")]
    public void AuthoredRootOrScopeOverridesCannotGrantPermission(string name, string value) =>
        Assert.Equal(SetupCode.RootMismatch,
            Assert.IsType<Effect<ValidatedPackageProfile>.Failure>(
                AuthoringFixture.Create().Append("Property", name, value).Validate()).Code);

    [Theory]
    [InlineData("MoveFiles")]
    [InlineData("DuplicateFile")]
    [InlineData("Environment")]
    [InlineData("SelfReg")]
    [InlineData("ServiceInstall")]
    [InlineData("MsiAssembly")]
    [InlineData("Patch")]
    [InlineData("ControlEvent")]
    public void UnsupportedMutationTablesFailEvenWhenEmpty(string name)
    {
        var fixture = AuthoringFixture.Create();
        Refused(fixture with
        {
            Snapshot = fixture.Snapshot with { TableNames = fixture.Snapshot.TableNames.Add(name) }
        });
    }

    [Fact]
    public void InnocuousMetadataDoesNotBecomeAnUnsupportedMutationTable()
    {
        var fixture = AuthoringFixture.Create();
        var definitions = PackageAuthoringSchema.Columns["MsiFileHash"]
            .Select((column, index) => ImmutableArray.Create(
                "MsiFileHash", (index + 1).ToString(), column, "7496"));
        fixture = fixture with
        {
            Snapshot = fixture.Snapshot with
            {
                TableNames = fixture.Snapshot.TableNames.Add("MsiFileHash"),
                ColumnDefinitions = fixture.Snapshot.ColumnDefinitions.AddRange(definitions)
            }
        };
        Assert.IsType<Effect<ValidatedPackageProfile>.Success>(fixture.Validate());
    }

    [Fact]
    public void FailedPresentQueryIsNotReclassifiedAsOptionalAbsence()
    {
        var fixture = AuthoringFixture.Create();
        Refused(fixture with
        {
            Snapshot = fixture.Snapshot with { Tables = fixture.Snapshot.Tables.Remove("Condition") }
        });
        var absent = fixture with
        {
            Snapshot = fixture.Snapshot with
            {
                TableNames = fixture.Snapshot.TableNames.Remove("Condition"),
                Tables = fixture.Snapshot.Tables.Remove("Condition"),
                ColumnDefinitions = fixture.Snapshot.ColumnDefinitions
                    .Where(row => row[0] != "Condition").ToImmutableArray()
            }
        };
        Assert.Empty(absent.Snapshot.OptionalTable("Condition"));
        Assert.IsType<Effect<ValidatedPackageProfile>.Success>(absent.Validate());
    }

    [Fact]
    public void DuplicateAndMalformedRowsAreNotSilentlyDiscarded()
    {
        var fixture = AuthoringFixture.Create();
        Refused(fixture.Rows("CustomAction",
            fixture.Snapshot.RequireTable("CustomAction").Add(
                fixture.Snapshot.RequireTable("CustomAction")[0])));
        Refused(fixture.Rows("Registry", AuthoringFixture.R(["Malformed"])));
        Refused(fixture with
        {
            Snapshot = fixture.Snapshot with
            {
                ColumnDefinitions = fixture.Snapshot.ColumnDefinitions.Add(
                    fixture.Snapshot.ColumnDefinitions[0])
            }
        });
    }

    [Fact]
    public void UnknownMutationColumnCannotHideBehindAKnownTable()
    {
        var fixture = AuthoringFixture.Create();
        Refused(fixture with
        {
            Snapshot = fixture.Snapshot with
            {
                ColumnDefinitions = fixture.Snapshot.ColumnDefinitions.Add(
                    ImmutableArray.Create("Component", "7", "ForeignPolicy", "5378"))
            }
        });
    }

    [Fact]
    public void InvalidCallerContractIsNotReportedAsAnApprovedPackage()
    {
        var fixture = AuthoringFixture.Create();
        var result = PackageAuthoringValidator.Validate(fixture.Snapshot,
            PackageAuthoringMode.CandidateSource, fixture.Approved with { AdmissionAbi = 2 });
        Assert.Equal(SetupCode.InvalidEvidence,
            Assert.IsType<Effect<ValidatedPackageProfile>.Failure>(result).Code);
    }

    private static void Refused(
        AuthoringFixture fixture,
        PackageAuthoringMode mode = PackageAuthoringMode.CandidateSource) =>
        Assert.Equal(SetupCode.MediaInvalid,
            Assert.IsType<Effect<ValidatedPackageProfile>.Failure>(fixture.Validate(mode)).Code);
}
