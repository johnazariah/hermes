using System.Collections.Immutable;
using System.Text.Json;
using Hermes.Servicing;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed class SetupBoundaryTests
{
    [Fact]
    public void FixturePathsPreserveWindowsIdentityThroughHostParentsAndCliNormalization()
    {
        using var world = new RootFixture();
        Assert.Equal(RootFixture.WindowsPrefix, WindowsEvidencePath.Normalize(RootFixture.Prefix));
        Assert.True(Path.IsPathFullyQualified(RootFixture.LocalAppData));
        var company = Path.GetDirectoryName(world.Scope.AppDirectory)
            ?? throw new InvalidOperationException("Missing fixture company directory.");
        var local = Path.GetDirectoryName(company)
            ?? throw new InvalidOperationException("Missing fixture known folder.");
        Assert.Equal(RootFixture.WindowsPrefix + "/Local", WindowsEvidencePath.Normalize(local));
        foreach (var directory in world.Resources(world.New).Directories.Values)
            Assert.StartsWith(RootFixture.WindowsPrefix + "/", WindowsEvidencePath.Normalize(directory));
        var parsed = Assert.IsType<ParsedCommand>(SetupCommandLine.Parse(
            ["install", "--package", world.New.SourcePath]));
        var command = Assert.IsType<InstallCommand>(parsed.Command);
        Assert.True(WindowsEvidencePath.Same(world.New.SourcePath, command.PackagePath));
        Assert.True(WindowsEvidencePath.Same(world.New.SourcePath, Path.GetFullPath(world.New.SourcePath)));
    }

    [Fact]
    public void VirtualFilesystemMapsEquivalentWindowsSpellingsToTheSameOwnedEntry()
    {
        using var world = new RootFixture();
        var hostSpelling = world.Owned("notes/fixture.bin");
        var windowsSpelling = WindowsEvidencePath.Normalize(hostSpelling);
        world.Files.Put(hostSpelling, [3, 4, 5]);
        Assert.Equal(new byte[] { 3, 4, 5 }, world.Files.RawBytes(windowsSpelling));
        Assert.Equal(new byte[] { 3, 4, 5 },
            world.Files.RawBytes(windowsSpelling.Replace('/', '\\')));
        Assert.Equal(DiskEntryKind.File, world.Files.Entry(hostSpelling).Kind);
        Assert.True(WindowsEvidencePath.Same(hostSpelling, windowsSpelling));
    }

    [Theory]
    [InlineData("/hermes/share/Local")]
    [InlineData("hermes/share/Local")]
    [InlineData(@"C:relative\file")]
    public void VirtualFilesystemDoesNotRepairLostUncPrefixesOrRelativePaths(string path)
    {
        using var files = new RootFiles();
        Assert.Throws<FormatException>(() => files.CreateDirectory(path));
    }

    [Theory]
    [InlineData("//hermes/shareOther/Local")]
    [InlineData("//hermes/share-other/Local")]
    [InlineData("//foreign/share/Local")]
    public void VirtualFilesystemRetainsComponentBoundedOwnership(string path)
    {
        using var files = new RootFiles();
        var error = Assert.Throws<SetupIoException>(() => files.CreateDirectory(path));
        Assert.Equal(SetupCode.RootMismatch, error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCDEF")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaA")]
    [InlineData(" aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;")]
    public void MalformedCompiledTrustIsNotUsable(string text)
    {
        var result = CompiledGuardTrust.Read([new(CompiledGuardTrust.MetadataName, text)]);
        Assert.Equal(SetupCode.InvalidEvidence,
            Assert.IsType<Effect<ApprovedGuardProfile>.Failure>(result).Code);
    }

    [Fact]
    public void MissingDuplicateAndNullMetadataAreExplicitFailures()
    {
        Assert.Equal(SetupCode.Unavailable,
            Assert.IsType<Effect<ApprovedGuardProfile>.Failure>(CompiledGuardTrust.Read([])).Code);
        var hash = new string('a', 64);
        Assert.IsType<Effect<ApprovedGuardProfile>.Failure>(CompiledGuardTrust.Read(
            [new(CompiledGuardTrust.MetadataName, null)]));
        Assert.IsType<Effect<ApprovedGuardProfile>.Failure>(CompiledGuardTrust.Read(
            [new(CompiledGuardTrust.MetadataName, hash), new(CompiledGuardTrust.MetadataName, hash)]));
        Assert.IsType<Effect<ApprovedGuardProfile>.Failure>(CompiledGuardTrust.Read(
            [new(CompiledGuardTrust.MetadataName, hash + ";" + hash)]));
    }

    [Fact]
    public void CompiledCompatibleHashSetReusesFrozenAbiAndRoot()
    {
        var first = new string('a', 64);
        var second = new string('b', 64);
        var result = AggregateResult.Require(CompiledGuardTrust.Read(
            [new("UnrelatedMetadata", "ignored"),
             new(CompiledGuardTrust.MetadataName, first + ";" + second)]));
        Assert.Equal(HermesServicingProtocol.AdmissionAbi, result.AdmissionAbi);
        Assert.Equal(PackageMediaReader.RootToken, result.Root);
        Assert.True(result.Sha256.SetEquals([first, second]));
    }

    [Fact]
    public async Task CandidateSelfHashDoesNotCreateMissingCompiledTrustOrApproveChangedGuard()
    {
        using var world = new RootFixture(badGuard: true);
        world.Factory.Trust([]);
        var missing = world.Factory.Create();
        Assert.Equal(SetupCode.Unavailable,
            Assert.IsType<Effect<SetupApplication>.Failure>(missing).Code);
        Assert.Empty(world.Files.ReadPaths);
        world.Factory.Trust([new(CompiledGuardTrust.MetadataName,
            string.Join(';', world.Old.Authoring.Approved.Sha256))]);
        var refused = await world.RunAsync(world.Install);
        Assert.Equal(SetupCode.MediaInvalid, refused.Code);
        Assert.Equal(0, world.Admission.CloseCalls);
        Assert.Equal(0, world.Engine.Calls);
    }

    [Theory]
    [InlineData("[SystemFolder]foreign.exe")]
    [InlineData("[%LOCALAPPDATA%]foreign.exe")]
    [InlineData("[UnknownProperty]foreign.exe")]
    public void CheckedResolverDoesNotExpandArbitraryMsiTokens(string target)
    {
        using var world = new RootFixture();
        var profile = AggregateResult.Require(world.New.Authoring.Validate());
        var changed = profile with { Shortcut = profile.Shortcut with { Target = target } };
        var error = Assert.Throws<SetupIoException>(() => SetupResources.Create(
            world.New.Authoring.Snapshot, changed, world.Scope, RootFixture.Programs));
        Assert.Equal(SetupCode.MediaInvalid, error.Code);
    }

    [Fact]
    public void ResourcePlanUsesKnownFoldersRegistryKeypathsAndOnlyApprovedProperties()
    {
        using var world = new RootFixture();
        var resources = world.Resources(world.New);
        Assert.True(WindowsEvidencePath.Same(
            world.Owned("tray/Hermes.Tray.exe"), resources.Shortcut.Target));
        Assert.True(WindowsEvidencePath.Same(
            world.Scope.AppDirectory, resources.Shortcut.WorkingDirectory));
        Assert.True(WindowsEvidencePath.Same(
            RootFixture.Programs + "/Hermes/Hermes.lnk", resources.ShortcutPath));
        Assert.Contains(resources.ComponentKeyPaths.Values,
            value => value == @"01:\Software\Hermes\Installer\Version");
        Assert.Contains(resources.Registry, value => value.Name == "ProductCode" &&
            value.Value.Text == world.New.Manifest.ProductCode.ToString("B"));
        Assert.Contains(resources.Registry, value => value.Value.Kind == RegistryAtomKind.Integer &&
            value.Value.Integer == 1);
    }

    [Fact]
    public async Task InvalidCliIncludingTrustOverrideIsRejectedBeforeFactoryConstruction()
    {
        using var world = new RootFixture();
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var code = await SetupEntryPoint.RunAsync(
            ["install", "--package", world.New.SourcePath, "--trusted-guard-sha256", "sensitive"],
            true, world.Factory, output, errors, CancellationToken.None);
        Assert.Equal((int)SetupExitCode.InvalidArguments, code);
        Assert.Equal(0, world.Factory.Creates);
        Assert.Equal("", output.ToString());
        Assert.DoesNotContain("sensitive", errors.ToString());
        Assert.Empty(world.Files.ReadPaths);
    }

    [Fact]
    public async Task NonWindowsRefusalHasPublicReportButConstructsNoEffects()
    {
        using var world = new RootFixture();
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var code = await SetupEntryPoint.RunAsync(
            ["repair"], false, world.Factory, output, errors, CancellationToken.None);
        Assert.Equal((int)SetupExitCode.ServicingFailed, code);
        Assert.Equal(0, world.Factory.Creates);
        using var report = JsonDocument.Parse(output.ToString());
        Assert.False(report.RootElement.GetProperty("ready").GetBoolean());
        Assert.Equal("Hermes Setup: Unavailable." + Environment.NewLine, errors.ToString());
        Assert.Empty(world.Files.ReadPaths);
    }

    [Fact]
    public async Task CliSuccessIsTheRealOrchestratorOutcomeAndReleasesSourcePins()
    {
        using var world = new RootFixture();
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var code = await SetupEntryPoint.RunAsync(
            ["install", "--package", world.New.SourcePath, "--quiet"],
            true, world.Factory, output, errors, CancellationToken.None);
        Assert.Equal((int)SetupExitCode.Success, code);
        Assert.Equal(1, world.Factory.Creates);
        Assert.Equal(1, world.Engine.Calls);
        using var report = JsonDocument.Parse(output.ToString());
        Assert.True(report.RootElement.GetProperty("ready").GetBoolean());
        Assert.Equal(0, report.RootElement.GetProperty("msiExitCode").GetInt32());
        Assert.Empty(world.Files.ActiveReadPaths);
        Assert.False(world.Admission.Held);
    }

    [Fact]
    public async Task CliExpectedErrorsAreScrubbedButUnrelatedProgrammingFaultsPropagate()
    {
        using var world = new RootFixture();
        using var output = new StringWriter();
        using var errors = new StringWriter();
        world.Registry.Fail(new IOException("sensitive diagnostic"));
        var code = await SetupEntryPoint.RunAsync(
            ["repair"], true, world.Factory, output, errors, CancellationToken.None);
        Assert.NotEqual((int)SetupExitCode.Success, code);
        Assert.DoesNotContain("sensitive", output.ToString() + errors.ToString());
        world.Registry.Fail(new InvalidOperationException("programming fault"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SetupEntryPoint.RunAsync(
            ["repair"], true, world.Factory, output, errors, CancellationToken.None));
        Assert.False(world.Admission.Held);
        Assert.Empty(world.Files.ActiveReadPaths);
    }

    [Fact]
    public async Task NativeTypedRefusalUsesTheSameScrubbedPublicReport()
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var code = await SetupEntryPoint.NativeRefusalAsync(["uninstall"], output, errors);
        Assert.Equal((int)SetupExitCode.ServicingFailed, code);
        using var report = JsonDocument.Parse(output.ToString());
        Assert.False(report.RootElement.GetProperty("ready").GetBoolean());
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await SetupEntryPoint.NativeRefusalAsync(["invalid"], output, errors));
    }

    [Fact]
    public async Task CancelledObservationDoesNotProduceEvidenceOrEffects()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await AggregateResult.ReadAsync<object>(
            () => throw new InvalidOperationException("must not execute"),
            SetupCode.InvalidEvidence, cancellation.Token);
        Assert.Equal(SetupCode.Cancelled,
            Assert.IsType<Effect<object>.Failure>(result).Code);
    }
}
