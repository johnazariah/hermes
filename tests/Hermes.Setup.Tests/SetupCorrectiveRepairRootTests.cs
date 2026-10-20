using Hermes.Servicing;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed class SetupCorrectiveRepairRootTests
{
    private static MaintenanceCommand Repair { get; } = new(SetupVerb.Repair, true);

    [Theory]
    [InlineData(RootDefect.MissingPayload, false)]
    [InlineData(RootDefect.WrongHash, false)]
    [InlineData(RootDefect.MissingPayload, true)]
    [InlineData(RootDefect.WrongHash, true)]
    public async Task RegisteredDamageReachesRepairAndRestoresTheCompleteArtifact(
        object value, bool samePackage)
    {
        var damage = Assert.IsType<RootDefect>(value);
        using var world = new RootFixture();
        await world.SeedAsync();
        world.DamageOwned(damage);
        AssertDamage(world, damage);
        var result = await world.RunAsync(Command(world, samePackage));
        AssertReachedMsi(world);
        Assert.Equal(MaintenanceKind.Repair, Assert.Single(world.Engine.Requests).Step.Operation);
        Assert.Equal(SetupExitCode.Success, result.ExitCode);
        Assert.True(result.Report.Ready);
        AssertReady(world, world.Old);
    }

    [Theory]
    [InlineData(RootDefect.MissingComponent, 2, false)]
    [InlineData(RootDefect.MissingComponent, 2, true)]
    [InlineData(RootDefect.WrongKeyPath, 2, false)]
    [InlineData(RootDefect.ForeignClient, 2, false)]
    [InlineData(RootDefect.MissingBinding, 2, false)]
    [InlineData(RootDefect.MissingBinding, 2, true)]
    [InlineData(RootDefect.MissingMarker, 2, false)]
    [InlineData(RootDefect.WrongBinding, 2, false)]
    [InlineData(RootDefect.ManagedContext, 1, false)]
    [InlineData(RootDefect.MachineContext, 1, false)]
    [InlineData(RootDefect.ForeignSid, 1, false)]
    [InlineData(RootDefect.Damaged, 1, false)]
    [InlineData(RootDefect.UnknownRegistration, 1, false)]
    [InlineData(RootDefect.MissingQueryEvidence, 2, false)]
    public async Task MissingOwnedPayloadDoesNotExcuseMissingOwnership(
        object value, int reads, bool samePackage)
    {
        var defect = Assert.IsType<RootDefect>(value);
        using var world = new RootFixture();
        await world.SeedAsync();
        world.DamageOwned(RootDefect.MissingPayload);
        world.Registry.Mutate(defect);
        var result = await world.RunAsync(Command(world, samePackage));
        AssertBeforeEffects(world, result, reads);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OwnedFileAndAncestorSafetyRemainRequired(
        bool ancestor, bool reparse)
    {
        using var world = new RootFixture();
        await world.SeedAsync();
        world.DamageOwned(RootDefect.WrongHash);
        var path = world.Owned(ancestor ? "service" : RootFixture.ExecutableRelative);
        if (reparse)
            world.Files.MarkReparse(path);
        else
            world.Paths.Alias(path, "//foreign/share/redirected");
        var result = await world.RunAsync(Repair);
        AssertBeforeEffects(world, result, 2);
    }

    [Fact]
    public async Task UnknownOwnedFileIdentityCannotUseTheCorrectiveRoute()
    {
        using var world = new RootFixture();
        await world.SeedAsync();
        world.DamageOwned(RootDefect.WrongHash);
        world.Paths.Unknown(world.Owned(RootFixture.ExecutableRelative));
        var result = await world.RunAsync(Repair);
        AssertBeforeEffects(world, result, 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorrectiveServicingStillRequiresTheCurrentReadyGeneration(bool missing)
    {
        using var world = new RootFixture();
        await world.SeedAsync();
        world.DamageOwned(RootDefect.MissingPayload);
        world.Admission.Set(missing
            ? new(GateDisposition.Missing, null)
            : new(GateDisposition.Ready, SetupIdentity.ReadyState(world.Scope,
                new PresentTarget(world.Media.Read(world.New.SourcePath).Evidence))));
        var result = await world.RunAsync(Repair);
        Assert.Equal(0, world.Engine.Calls);
        Assert.Equal(2, world.Registry.Reads);
        Assert.Equal(SetupCode.OwnershipRefused, result.Code);
        Assert.Equal(0, world.Admission.CloseCalls);
        Assert.Equal(0, world.Admission.Publications);
        Assert.False(result.Report.Ready);
    }

    [Fact]
    public async Task SameBytesAtAnUnownedNewPathAreNeitherAdoptedNorRead()
    {
        using var world = new RootFixture();
        await world.SeedAsync();
        world.DamageOwned(RootDefect.WrongHash);
        var path = world.Owned(RootFixture.NewRelative);
        var bytes = world.New.Contents[RootFixture.NewRelative];
        world.Files.Put(path, bytes.AsSpan());
        world.Files.FailRead(path);
        var result = await world.RunAsync(world.Install);
        AssertBeforeEffects(world, result, 2);
        Assert.Equal(SetupCode.Collision, result.Code);
        Assert.Equal(bytes.ToArray(), world.Files.RawBytes(path));
        Assert.DoesNotContain(world.Files.ReadPaths,
            opened => WindowsEvidencePath.Same(opened, path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PayloadDamageDoesNotExcuseInvalidRetainedAuthority(bool manifest)
    {
        using var world = new RootFixture();
        await world.SeedAsync();
        world.DamageOwned(RootDefect.MissingPayload);
        var evidence = world.Media.Read(world.Old.SourcePath).Evidence;
        var path = manifest
            ? MediaCachePaths.Manifest(world.Scope, evidence.PackageSha256)
            : MediaCachePaths.Package(world.Scope, evidence.PackageSha256);
        world.Files.Put(path, [1, 2]);
        var result = await world.RunAsync(Repair);
        AssertBeforeEffects(world, result, 1);
    }

    [Theory]
    [InlineData(RootDefect.MissingPayload)]
    [InlineData(RootDefect.WrongHash)]
    [InlineData(RootDefect.WrongLength)]
    [InlineData(RootDefect.WrongBinding)]
    [InlineData(RootDefect.MissingBinding)]
    [InlineData(RootDefect.MissingMarker)]
    [InlineData(RootDefect.MissingComponent)]
    [InlineData(RootDefect.WrongKeyPath)]
    [InlineData(RootDefect.ShortcutTarget)]
    [InlineData(RootDefect.ShortcutArguments)]
    [InlineData(RootDefect.ShortcutDirectory)]
    [InlineData(RootDefect.ShortcutUnreadable)]
    [InlineData(RootDefect.Run)]
    [InlineData(RootDefect.UnknownRegistration)]
    [InlineData(RootDefect.PendingUnknown)]
    [InlineData(RootDefect.ReadFailure)]
    public async Task IncompleteRepairPoststateCannotPublishReady(object value)
    {
        var defect = Assert.IsType<RootDefect>(value);
        using var world = new RootFixture();
        await world.SeedAsync();
        world.DamageOwned(RootDefect.WrongHash);
        world.Engine.Enqueue(new(0, world.Old, defect));
        var result = await world.RunAsync(Repair);
        AssertReachedMsi(world);
        Assert.Equal(MaintenanceKind.Repair, Assert.Single(world.Engine.Requests).Step.Operation);
        AssertClosed(world, result);
    }

    [Theory]
    [InlineData(RootDefect.MissingPayload)]
    [InlineData(RootDefect.WrongHash)]
    public async Task FailedRepairRestoringTheDamagedPrestateRemainsClosed(object value)
    {
        var damage = Assert.IsType<RootDefect>(value);
        using var world = new RootFixture();
        await world.SeedAsync();
        world.DamageOwned(damage);
        AssertDamage(world, damage);
        world.Engine.Enqueue(new(1603, world.Old, damage));
        var result = await world.RunAsync(Repair);
        AssertReachedMsi(world);
        Assert.Equal(SetupCode.VerificationFailed, result.Code);
        AssertDamage(world, damage);
        AssertClosed(world, result);
    }

    [Fact]
    public async Task FailedRepairCanReopenOnlyAfterTheFullPreviousArtifactIsRestored()
    {
        using var world = new RootFixture();
        await world.SeedAsync();
        world.DamageOwned(RootDefect.WrongHash);
        world.Engine.Enqueue(new(1603, world.Old));
        var result = await world.RunAsync(Repair);
        AssertReachedMsi(world);
        Assert.Equal(SetupExitCode.ServicingFailed, result.ExitCode);
        Assert.Equal(SetupOutcome.RolledBack, result.Report.Outcome);
        Assert.True(result.Report.Ready);
        AssertReady(world, world.Old);
    }

    [Fact]
    public async Task HashMatchingRepairWithWrongExpectedMvidRemainsClosed()
    {
        using var world = new RootFixture(wrongMvid: true);
        await world.SeedAsync(world.New);
        world.DamageOwned(RootDefect.MissingPayload);
        var result = await world.RunAsync(Repair);
        AssertReachedMsi(world);
        Assert.Equal(MaintenanceKind.Repair, Assert.Single(world.Engine.Requests).Step.Operation);
        Assert.All(world.New.Manifest.Files, file =>
            Assert.Equal(file.Sha256,
                PackageMediaReader.Hash(world.Files.RawBytes(world.Owned(file.Path)))));
        Assert.Equal(SetupCode.VerificationFailed, result.Code);
        AssertClosed(world, result);
    }

    [Theory]
    [InlineData(0, false, false, 2)]
    [InlineData(0, true, true, 3)]
    [InlineData(3010, true, true, 2)]
    public async Task CorrectiveRepairStillRequiresItsOwnFenceAndNoPendingWork(
        int code, bool fence, bool pending, int reads)
    {
        using var world = new RootFixture();
        await world.SeedAsync();
        world.DamageOwned(RootDefect.WrongHash);
        world.Engine.Enqueue(new((uint)code, world.Old,
            pending ? RootDefect.Pending : RootDefect.None, Fence: fence));
        var result = await world.RunAsync(Repair);
        AssertReachedMsi(world, reads);
        Assert.Equal(fence ? SetupCode.RebootRequired : SetupCode.TransactionFenceMissing,
            result.Code);
        AssertClosed(world, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpgradeAndExplicitDowngradeCanServiceRegisteredPayloadDamage(bool rollback)
    {
        using var world = new RootFixture(newVersion: new Version(1, 2, 4));
        Assert.True(world.New.Manifest.Version > world.Old.Manifest.Version);
        var current = rollback ? world.New : world.Old;
        var target = rollback ? world.Old : world.New;
        await world.SeedAsync(current);
        world.DamageOwned(RootDefect.WrongHash);
        var result = await world.RunAsync(new InstallCommand(target.SourcePath, rollback, true));
        AssertReachedMsi(world);
        var step = Assert.Single(world.Engine.Requests).Step;
        Assert.Equal(MaintenanceKind.Install, step.Operation);
        Assert.Contains(current.Manifest.ProductCode, step.NestedRemovalProducts);
        Assert.Equal(SetupExitCode.Success, result.ExitCode);
        Assert.True(result.Report.Ready);
        AssertReady(world, target);
    }

    [Theory]
    [InlineData(RootDefect.MissingPayload)]
    [InlineData(RootDefect.WrongHash)]
    public async Task UninstallDoesNotRequireAlreadyRepairedOwnedBytes(object value)
    {
        var damage = Assert.IsType<RootDefect>(value);
        using var world = new RootFixture();
        await world.SeedAsync();
        world.DamageOwned(damage);
        var result = await world.RunAsync(new MaintenanceCommand(SetupVerb.Uninstall, true));
        AssertReachedMsi(world);
        Assert.Equal(MaintenanceKind.Uninstall, Assert.Single(world.Engine.Requests).Step.Operation);
        Assert.Equal(SetupExitCode.Success, result.ExitCode);
        Assert.True(result.Report.Ready);
        Assert.Null(world.Admission.Snapshot.Ready?.Installation);
        Assert.Equal(1, world.Admission.Publications);
        Assert.All(world.Old.Manifest.Files, file =>
            Assert.Equal(DiskEntryKind.Missing, world.Files.Entry(world.Owned(file.Path)).Kind));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRelatedServicingCannotPublishTheDamagedPreviousArtifact(bool rollback)
    {
        using var world = new RootFixture(newVersion: new Version(1, 2, 4));
        var current = rollback ? world.New : world.Old;
        var target = rollback ? world.Old : world.New;
        await world.SeedAsync(current);
        world.DamageOwned(RootDefect.WrongHash);
        world.Engine.Enqueue(new(1603, current, RootDefect.WrongHash));
        var result = await world.RunAsync(new InstallCommand(target.SourcePath, rollback, true));
        AssertReachedMsi(world);
        Assert.Equal(MaintenanceKind.Install, Assert.Single(world.Engine.Requests).Step.Operation);
        Assert.Equal(SetupCode.VerificationFailed, result.Code);
        AssertClosed(world, result);
    }

    private static SetupCommand Command(RootFixture world, bool samePackage) =>
        samePackage ? new InstallCommand(world.Old.SourcePath, false, true) : Repair;

    private static void AssertDamage(RootFixture world, RootDefect damage)
    {
        switch (damage)
        {
            case RootDefect.MissingPayload:
                Assert.Equal(DiskEntryKind.Missing,
                    world.Files.Entry(world.Owned(RootFixture.ManagedRelative)).Kind);
                break;
            case RootDefect.WrongHash:
                Assert.Equal(new byte[world.Old.Contents[RootFixture.ExecutableRelative].Length],
                    world.Files.RawBytes(world.Owned(RootFixture.ExecutableRelative)));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(damage));
        }
    }

    private static void AssertReachedMsi(RootFixture world, int registryReads = 3)
    {
        Assert.Equal(1, world.Engine.Calls);
        Assert.Equal(registryReads, world.Registry.Reads);
        Assert.Single(world.Engine.Requests);
        Assert.Equal(1, world.Admission.CloseCalls);
        Assert.Equal(1, world.Admission.SnapshotReads);
        Assert.False(world.Admission.Held);
        Assert.Empty(world.Files.ActiveReadPaths);
    }

    private static void AssertBeforeEffects(
        RootFixture world, SetupExecution result, int registryReads)
    {
        Assert.Equal(0, world.Engine.Calls);
        Assert.Equal(registryReads, world.Registry.Reads);
        Assert.Empty(world.Engine.Requests);
        Assert.Equal(0, world.Admission.CloseCalls);
        Assert.Equal(0, world.Admission.Publications);
        Assert.Equal(GateDisposition.Ready, world.Admission.Snapshot.Disposition);
        Assert.False(result.Report.Ready);
        Assert.NotEqual(SetupExitCode.Success, result.ExitCode);
        Assert.False(world.Admission.Held);
        Assert.Empty(world.Files.ActiveReadPaths);
    }

    private static void AssertClosed(RootFixture world, SetupExecution result)
    {
        Assert.False(result.Report.Ready);
        Assert.NotEqual(SetupExitCode.Success, result.ExitCode);
        Assert.Equal(SetupAdmissionState.Closed, result.Admission);
        Assert.Equal(GateDisposition.Closed, world.Admission.Snapshot.Disposition);
        Assert.Equal(0, world.Admission.Publications);
    }

    private static void AssertReady(RootFixture world, RootPackage package)
    {
        Assert.Equal(GateDisposition.Ready, world.Admission.Snapshot.Disposition);
        var ready = Assert.IsType<ReadyInstallation>(world.Admission.Snapshot.Ready?.Installation);
        Assert.Equal(package.Manifest.ProductCode, ready.ProductCode);
        Assert.Equal(world.Scope.AppDirectory, ready.Binding.AppDirectory);
        Assert.Equal(HermesServicingProtocol.UpgradeCode, ready.Binding.UpgradeCode);
        Assert.Equal(HermesServicingProtocol.AdmissionAbi, ready.Binding.AdmissionAbi);
        Assert.Equal<ManagedImage>(world.Media.Read(package.SourcePath).Evidence.Images, ready.Images);
        Assert.True(OwnedPayloadInventory.Matches(package.Manifest,
            new OwnedPayloadInventory(world.Disk, world.Scope).Read(package.Manifest)));
        Assert.Equal(1, world.Admission.Publications);
        Assert.False(world.Admission.Held);
        Assert.Empty(world.Files.ActiveReadPaths);
    }
}
