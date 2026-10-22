using System.Collections.Immutable;
using System.Text;
using Hermes.Servicing;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed class SetupRootTests
{
    [Fact]
    public async Task OrdinaryFreshInstallUsesRealAggregationAndPublishesItsActualBinding()
    {
        using var world = new RootFixture();
        var result = await world.RunAsync(world.Install);
        Assert.Equal(SetupExitCode.Success, result.ExitCode);
        Assert.Equal(SetupOutcome.Completed, result.Report.Outcome);
        Assert.True(result.Report.Ready);
        Assert.Equal(1, world.Engine.Calls);
        Assert.Equal(1, world.Admission.SnapshotReads);
        Assert.Equal(1, world.Admission.CloseCalls);
        var ready = Assert.IsType<ReadyInstallation>(world.Admission.Snapshot.Ready?.Installation);
        Assert.Equal(world.New.Manifest.ProductCode, ready.ProductCode);
        Assert.Equal(world.Scope.AppDirectory, ready.Binding.AppDirectory);
        Assert.Equal<ManagedImage>(world.Media.Read(world.New.SourcePath).Evidence.Images, ready.Images);
        Assert.Empty(world.Files.ActiveReadPaths);
        Assert.False(world.Admission.Held);
    }

    [Fact]
    public async Task OwnerDataAndUnknownNoncollidingFilesSurviveInstallAndUninstallUnread()
    {
        using var world = new RootFixture();
        var paths = new[] { RootFixture.OwnerConfig, RootFixture.OwnerArchive,
            Path.Combine(world.Scope.AppDirectory, "notes", "unknown.bin") };
        foreach (var path in paths)
            world.Files.Put(path, [7, 8, 9]);
        Assert.True((await world.RunAsync(world.Install)).Report.Ready);
        var removed = await world.RunAsync(new MaintenanceCommand(SetupVerb.Uninstall, true));
        Assert.Equal(SetupExitCode.Success, removed.ExitCode);
        Assert.True(removed.Report.Ready);
        Assert.Null(world.Admission.Snapshot.Ready?.Installation);
        foreach (var path in paths)
        {
            Assert.Equal(new byte[] { 7, 8, 9 }, world.Files.RawBytes(path));
            Assert.DoesNotContain(world.Files.ReadPaths,
                opened => WindowsEvidencePath.Same(opened, path));
        }
    }

    [Fact]
    public async Task AncestorCleanupPreservesUnknownNestedUserDataUnread()
    {
        using var world = new RootFixture(
            oldRelative: "tray/runtimes/win-x64/native/old.bin",
            newRelative: "tray/runtimes/win-x64/native/new.bin");
        var unknown = world.Owned("tray/runtimes/win-x64/native/owner.bin");
        world.Files.Put(unknown, [7, 8, 9]);
        await world.SeedAsync();
        Assert.True((await world.RunAsync(world.Install)).Report.Ready);
        var removed = await world.RunAsync(new MaintenanceCommand(SetupVerb.Uninstall, true));
        Assert.Equal(SetupExitCode.Success, removed.ExitCode);
        Assert.True(removed.Report.Ready);
        Assert.Null(world.Admission.Snapshot.Ready?.Installation);
        Assert.Equal(new byte[] { 7, 8, 9 }, world.Files.RawBytes(unknown));
        Assert.DoesNotContain(world.Files.ReadPaths,
            opened => WindowsEvidencePath.Same(opened, unknown));
    }

    [Fact]
    public async Task KnownUnregisteredNewComponentIsAllowedOnlyAtAnAbsentNewOwnedPath()
    {
        using var world = new RootFixture();
        await world.SeedAsync();
        var code = world.New.Manifest.Files.Single(file =>
            file.Path == RootFixture.NewRelative).ComponentCode;
        Assert.False(SetupStateProof.Msi(world.Registry.Current).ComponentClients.ContainsKey(code));
        var result = await world.RunAsync(world.Install);
        Assert.Equal(SetupExitCode.Success, result.ExitCode);
        Assert.True(result.Report.Ready);
        Assert.Equal(DiskEntryKind.Missing,
            world.Files.Entry(world.Owned(RootFixture.OldRelative)).Kind);
    }

    [Fact]
    public async Task NewUnownedCollisionRefusesBeforeCloseOrEngine()
    {
        using var world = new RootFixture();
        world.Files.Put(world.Owned(RootFixture.NewRelative), [42]);
        var result = await world.RunAsync(world.Install);
        Assert.Equal(SetupCode.Collision, result.Code);
        AssertBeforeEffects(world, result);
        Assert.Equal(new byte[] { 42 }, world.Files.RawBytes(world.Owned(RootFixture.NewRelative)));
    }

    [Theory]
    [InlineData(RootDefect.RegisteredRoot)]
    [InlineData(RootDefect.ManagedContext)]
    [InlineData(RootDefect.MachineContext)]
    [InlineData(RootDefect.ForeignSid)]
    [InlineData(RootDefect.Advertised)]
    [InlineData(RootDefect.Damaged)]
    [InlineData(RootDefect.MissingComponent)]
    [InlineData(RootDefect.ForeignClient)]
    [InlineData(RootDefect.WrongKeyPath)]
    [InlineData(RootDefect.UnknownRegistration)]
    [InlineData(RootDefect.MissingQueryEvidence)]
    [InlineData(RootDefect.WrongBinding)]
    public async Task IncompatibleOrIncompleteOwnershipRefusesBeforeClosure(object value)
    {
        var defect = Assert.IsType<RootDefect>(value);
        using var world = new RootFixture();
        await world.SeedAsync();
        world.Registry.Mutate(defect);
        AssertBeforeEffects(world, await world.RunAsync(world.Install));
        Assert.Equal(GateDisposition.Ready, world.Admission.Snapshot.Disposition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhysicalRootAliasOrUngatedRegistrationIsNotAdopted(bool alias)
    {
        using var world = new RootFixture();
        await world.SeedAsync();
        if (alias)
            world.Paths.Alias(world.Scope.AppDirectory, "//foreign/share/App");
        else
            world.Admission.Set(new(GateDisposition.Missing, null));
        AssertBeforeEffects(world, await world.RunAsync(world.Install));
    }

    [Fact]
    public async Task GateGenerationMustMatchActualValidatedCurrentPackage()
    {
        using var world = new RootFixture();
        await world.SeedAsync();
        var ready = SetupIdentity.ReadyState(world.Scope,
            new PresentTarget(world.Media.Read(world.New.SourcePath).Evidence));
        world.Admission.Set(new(GateDisposition.Ready, ready));
        AssertBeforeEffects(world, await world.RunAsync(world.Install));
    }

    [Fact]
    public async Task BusyAcquisitionHasNoInspectionJournalMediaOrEngineEffects()
    {
        using var world = new RootFixture();
        world.Admission.MakeBusy();
        var result = await world.RunAsync(world.Install);
        Assert.Equal(SetupExitCode.Busy, result.ExitCode);
        Assert.Equal(0, world.Registry.Reads);
        Assert.Equal(0, world.Admission.SnapshotReads);
        Assert.Empty(world.Files.ReadPaths);
        AssertBeforeEffects(world, result);
    }

    [Fact]
    public async Task UpgradeKeepsOwnedMenuDirectoryAndUninstallRemovesOwnedLink()
    {
        using var world = new RootFixture();
        await world.SeedAsync();
        var resources = world.Resources(world.New);
        var menu = resources.Directories["HermesMenuFolder"];
        Assert.Equal(DiskEntryKind.File, world.Files.Entry(resources.ShortcutPath).Kind);
        var upgraded = await world.RunAsync(world.Install);
        Assert.Equal(SetupExitCode.Success, upgraded.ExitCode);
        Assert.Equal(DiskEntryKind.File, world.Files.Entry(resources.ShortcutPath).Kind);
        Assert.Equal(DiskEntryKind.Directory, world.Files.Entry(menu).Kind);
        var removed = await world.RunAsync(new MaintenanceCommand(SetupVerb.Uninstall, true));
        Assert.Equal(SetupExitCode.Success, removed.ExitCode);
        Assert.Equal(DiskEntryKind.Missing, world.Files.Entry(resources.ShortcutPath).Kind);
        Assert.Equal(DiskEntryKind.Missing, world.Files.Entry(menu).Kind);
        Assert.Equal(2, world.Engine.Calls);
        Assert.Empty(world.Files.ActiveReadPaths);
    }

    [Fact]
    public async Task FailedMsiCanPublishOnlyTheFullyVerifiedRestoredPreviousState()
    {
        using var world = new RootFixture();
        await world.SeedAsync();
        world.Engine.Enqueue(new(1603, world.Old));
        var result = await world.RunAsync(world.Install);
        Assert.Equal(1, world.Engine.Calls);
        Assert.Equal(3, world.Registry.Reads);
        Assert.Equal(SetupExitCode.ServicingFailed, result.ExitCode);
        Assert.Equal(SetupOutcome.RolledBack, result.Report.Outcome);
        Assert.True(result.Report.Ready);
        Assert.Equal(world.Old.Manifest.ProductCode,
            world.Admission.Snapshot.Ready?.Installation?.ProductCode);
        Assert.Equal(DiskEntryKind.Missing,
            world.Files.Entry(world.Owned(RootFixture.NewRelative)).Kind);
        Assert.Equal(world.Old.Contents[RootFixture.OldRelative].ToArray(),
            world.Files.RawBytes(world.Owned(RootFixture.OldRelative)));
    }

    [Theory]
    [InlineData(RootDefect.ShortcutTarget)]
    [InlineData(RootDefect.ShortcutArguments)]
    [InlineData(RootDefect.ShortcutDirectory)]
    [InlineData(RootDefect.ShortcutUnreadable)]
    [InlineData(RootDefect.Run)]
    [InlineData(RootDefect.WrongBinding)]
    [InlineData(RootDefect.WrongKeyPath)]
    [InlineData(RootDefect.MissingComponent)]
    [InlineData(RootDefect.ObsoleteFile)]
    [InlineData(RootDefect.WrongHash)]
    [InlineData(RootDefect.UnknownRegistration)]
    [InlineData(RootDefect.PendingUnknown)]
    [InlineData(RootDefect.ReadFailure)]
    public async Task IncompletePoststateCannotPublishReady(object value)
    {
        var defect = Assert.IsType<RootDefect>(value);
        using var world = new RootFixture();
        await world.SeedAsync();
        world.Engine.Enqueue(new(0, world.New, defect));
        var result = await world.RunAsync(world.Install);
        Assert.Equal(1, world.Engine.Calls);
        Assert.Equal(3, world.Registry.Reads);
        Assert.False(result.Report.Ready);
        Assert.Equal(SetupExitCode.ServicingFailed, result.ExitCode);
        Assert.Equal(GateDisposition.Closed, world.Admission.Snapshot.Disposition);
        Assert.Equal(0, world.Admission.Publications);
        Assert.Empty(world.Files.ActiveReadPaths);
    }

    [Fact]
    public async Task HashMatchingManagedBytesWithWrongManifestMvidStillFailVerification()
    {
        using var world = new RootFixture(wrongMvid: true);
        var result = await world.RunAsync(world.Install);
        Assert.Equal(1, world.Engine.Calls);
        Assert.Equal(3, world.Registry.Reads);
        Assert.Equal(SetupCode.VerificationFailed, result.Code);
        Assert.False(result.Report.Ready);
        Assert.Equal(GateDisposition.Closed, world.Admission.Snapshot.Disposition);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3010)]
    public async Task RelevantPendingRenameOr3010NeverPublishesReady(int code)
    {
        using var world = new RootFixture();
        world.Engine.Enqueue(new((uint)code, world.New, RootDefect.Pending));
        var result = await world.RunAsync(world.Install);
        Assert.Equal(1, world.Engine.Calls);
        Assert.Equal(code == 3010 ? 2 : 3, world.Registry.Reads);
        Assert.Equal(SetupExitCode.RebootRequired, result.ExitCode);
        Assert.Equal(SetupOutcome.RebootRequired, result.Report.Outcome);
        Assert.False(result.Report.Ready);
        Assert.Equal(GateDisposition.Closed, world.Admission.Snapshot.Disposition);
    }

    [Fact]
    public async Task MissingJournalRecoveryPreservesActualClosedStateWithoutInventingWork()
    {
        using var world = new RootFixture();
        world.Admission.Set(new(GateDisposition.Closed, null));
        var result = await world.RunAsync(new MaintenanceCommand(SetupVerb.Recover, true));
        Assert.Equal(SetupCode.JournalInvalid, result.Code);
        Assert.False(result.Report.Ready);
        Assert.Equal(GateDisposition.Closed, world.Admission.Snapshot.Disposition);
        Assert.Equal(0, world.Admission.CloseCalls);
        Assert.Equal(0, world.Engine.Calls);
        Assert.Null(world.Journal.Read());
    }

    [Fact]
    public async Task RecoveryOfAbsenceUsesFreshInstallAndUninstallFencesAndPreservesRevocation()
    {
        using var world = new RootFixture();
        var interrupted = await world.InterruptAbsentAsync();
        var result = await world.RunAsync(new MaintenanceCommand(SetupVerb.Recover, true));
        Assert.Equal(SetupExitCode.Success, result.ExitCode);
        Assert.True(result.Report.Ready);
        Assert.Equal(new[] { MaintenanceKind.Install, MaintenanceKind.Uninstall },
            world.Engine.Requests.Select(request => request.Step.Operation));
        Assert.Equal(2, world.Engine.Calls);
        var record = Assert.IsType<OperationJournal>(world.Journal.Read());
        Assert.Contains(interrupted.Run, record.RevokedRunIds);
        Assert.Equal(interrupted.Operation, record.Plan.Interrupted?.OperationId);
        Assert.Equal(2, record.Receipts.Length);
        Assert.NotEqual(record.Receipts[0].RunId, record.Receipts[1].RunId);
        Assert.Contains(record.Receipts[0].RunId, record.RevokedRunIds);
        Assert.Equal(1, world.Admission.Publications);
    }

    [Theory]
    [InlineData(1605)]
    [InlineData(1618)]
    public async Task RecoveryWithoutACompletedBarrierCannotUnlock(int code)
    {
        using var world = new RootFixture();
        await world.InterruptAbsentAsync();
        world.Engine.Enqueue(new((uint)code, null));
        var result = await world.RunAsync(new MaintenanceCommand(SetupVerb.Recover, true));
        Assert.False(result.Report.Ready);
        Assert.Equal(GateDisposition.Closed, world.Admission.Snapshot.Disposition);
        Assert.Equal(1, world.Engine.Calls);
        Assert.Equal(0, world.Admission.Publications);
    }

    [Fact]
    public async Task MissingOwnFenceCannotReachVerificationOrReady()
    {
        using var world = new RootFixture();
        world.Engine.Enqueue(new(0, world.New, Fence: false));
        var result = await world.RunAsync(world.Install);
        Assert.Equal(1, world.Engine.Calls);
        Assert.Equal(2, world.Registry.Reads);
        Assert.Equal(SetupCode.TransactionFenceMissing, result.Code);
        Assert.False(result.Report.Ready);
        Assert.Equal(GateDisposition.Closed, world.Admission.Snapshot.Disposition);
    }

    [Fact]
    public async Task RetainedManifestDamageFailsBeforeClosureDespiteValidRegisteredCache()
    {
        using var world = new RootFixture();
        await world.SeedAsync();
        var evidence = world.Media.Read(world.Old.SourcePath).Evidence;
        world.Files.Put(MediaCachePaths.Manifest(world.Scope, evidence.PackageSha256), [1, 2]);
        AssertBeforeEffects(world, await world.RunAsync(world.Install));
    }

    [Fact]
    public async Task ExpectedIoIsFailedEvidenceButProgrammingFaultsPropagateAndReleaseOwnership()
    {
        using var io = new RootFixture();
        io.Registry.Fail(new IOException("sensitive synthetic diagnostic"));
        AssertBeforeEffects(io, await io.RunAsync(io.Install));
        using var bug = new RootFixture();
        bug.Registry.Fail(new InvalidOperationException("synthetic programming fault"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bug.RunAsync(bug.Install));
        Assert.False(bug.Admission.Held);
        Assert.Empty(bug.Files.ActiveReadPaths);
        Assert.Equal(0, bug.Admission.CloseCalls);
    }

    private static void AssertBeforeEffects(RootFixture world, SetupExecution result)
    {
        Assert.False(result.Report.Ready);
        Assert.NotEqual(SetupExitCode.Success, result.ExitCode);
        Assert.Equal(0, world.Admission.CloseCalls);
        Assert.Equal(0, world.Admission.Publications);
        Assert.Equal(0, world.Engine.Calls);
    }
}

internal enum RootDefect
{
    None, RegisteredRoot, ManagedContext, MachineContext, ForeignSid, Advertised, Damaged,
    MissingComponent, ForeignClient, WrongKeyPath, UnknownRegistration, MissingQueryEvidence,
    WrongBinding, ShortcutTarget, ShortcutArguments, ShortcutDirectory, ShortcutUnreadable,
    Run, ObsoleteFile, WrongHash, Pending, PendingUnknown, ReadFailure,
    MissingPayload, WrongLength, MissingBinding, MissingMarker
}

internal sealed record RootPackage(
    AuthoringFixture Authoring, PayloadManifest Manifest,
    ImmutableDictionary<string, ImmutableArray<byte>> Contents,
    string SourcePath, ImmutableArray<byte> SourceBytes)
{
    internal string CachedPath =>
        RootFixture.Prefix + "/Installer/" + Manifest.ProductCode.ToString("N") + ".msi";

    internal static RootPackage Create(
        string name, string extra, bool wrongMvid, bool badGuard, Version? version = null)
    {
        var initial = AggregateResult.Require(AuthoringFixture.Create().Validate()).Manifest;
        var files = initial.Files.Add(AuthoringFixture.File(extra));
        var contents = files.ToImmutableDictionary(file => file.Path,
            file => Bytes(file.Path, name));
        var manifest = initial with
        {
            ProductCode = Guid.NewGuid(), PackageCode = Guid.NewGuid(),
            Version = version ?? initial.Version,
            Files = files.Select(file => Describe(file, contents[file.Path], wrongMvid))
                .ToImmutableArray()
        };
        var authoring = AuthoringFixture.Create(manifest);
        return new(badGuard ? Unapproved(authoring) : authoring, manifest, contents,
            RootFixture.Prefix + "/media/" + name + ".msi",
            Encoding.UTF8.GetBytes(name + manifest.PackageCode.ToString("N")).ToImmutableArray());
    }

    private static ImmutableArray<byte> Bytes(string path, string name) =>
        path.EndsWith(".dll", StringComparison.Ordinal)
            ? File.ReadAllBytes(typeof(RootPackage).Assembly.Location).ToImmutableArray()
            : Encoding.UTF8.GetBytes(name + "/" + path).ToImmutableArray();

    private static PayloadFile Describe(
        PayloadFile file, ImmutableArray<byte> bytes, bool wrongMvid) => file with
    {
        Length = bytes.Length,
        Sha256 = PackageMediaReader.Hash(bytes.ToArray()),
        ModuleVersionId = file.ModuleVersionId is null ? null :
            wrongMvid ? Guid.NewGuid() : typeof(RootPackage).Module.ModuleVersionId
    };

    private static AuthoringFixture Unapproved(AuthoringFixture fixture)
    {
        var image = AuthoringFixture.NativeImage();
        image[0x200] ^= 1;
        return fixture.Guard(image, approveSyntheticImage: false);
    }
}

internal sealed class RootFixture : IDisposable
{
    internal const string WindowsPrefix = "//hermes/share";

    // Both spellings denote the same ordinary Windows UNC identity.
    // On Unix, "/\server/share" has only one leading host separator, so
    // GetDirectoryName/GetFullPath cannot collapse its Windows UNC introducer.
    // On Windows, use the host's canonical UNC spelling.
    internal static string Prefix { get; } = Path.GetFullPath(
        OperatingSystem.IsWindows() ? WindowsPrefix : "/\\" + WindowsPrefix[2..])
        .TrimEnd('\\', '/');

    internal static string LocalAppData { get; } = Path.GetFullPath(Prefix + "/Local");
    internal static string Programs { get; } = Path.GetFullPath(Prefix + "/Programs");
    internal static string OwnerConfig { get; } =
        Path.GetFullPath(Prefix + "/Local/Hermes/settings.json");
    internal static string OwnerArchive { get; } = Path.GetFullPath(Prefix + "/Archive/owner.bin");
    internal const string OldRelative = "service/obsolete.bin";
    internal const string NewRelative = "service/fresh.bin";
    internal const string ManagedRelative = "service/Hermes.Service.dll";
    internal const string ExecutableRelative = "service/Hermes.Service.exe";
    internal const string Sid = "S-1-5-21-100-200-300-1001";
    private RootPackage? _current;

    internal RootFixture(
        bool wrongMvid = false, bool badGuard = false, Version? newVersion = null,
        string? oldRelative = null, string? newRelative = null)
    {
        Files = new();
        Paths = new();
        Scope = UserInstallationScope.FromKnownFolder(LocalAppData);
        Disk = new(Files, Paths, Files);
        Old = RootPackage.Create("old", oldRelative ?? OldRelative, false, false);
        New = RootPackage.Create("new", newRelative ?? NewRelative,
            wrongMvid, badGuard, newVersion);
        Database = new(Disk, [Old, New]);
        Media = new(Disk, Scope, Database);
        Admission = new(Scope);
        Registry = new();
        Engine = new(this);
        Factory = new(this);
        SeedMedia();
    }

    internal RootFiles Files { get; }
    internal RootPaths Paths { get; }
    internal UserInstallationScope Scope { get; }
    internal SetupDisk Disk { get; }
    internal RootPackage Old { get; }
    internal RootPackage New { get; }
    internal RootDatabase Database { get; }
    internal PackageMediaReader Media { get; }
    internal RootAdmission Admission { get; }
    internal RootRegistry Registry { get; }
    internal RootEngine Engine { get; }
    internal RootFactory Factory { get; }
    internal FileOperationJournal Journal => new(Disk, Scope);
    internal InstallCommand Install => new(New.SourcePath, false, true);
    internal SetupCapabilities Capabilities => new(Scope, Programs,
        [Prefix + "/Setup/Hermes.Setup.exe", Prefix + "/Setup/Hermes.Setup.dll"],
        Disk, Database, Database, Registry, Paths, Admission, Engine);

    internal async Task<SetupExecution> RunAsync(SetupCommand command)
    {
        await using var application = AggregateResult.Require(Factory.Create());
        return await application.ExecuteAsync(command, CancellationToken.None);
    }

    internal Task SeedAsync() => SeedAsync(Old);

    internal async Task SeedAsync(RootPackage package)
    {
        Replace(package);
        using var retention = new FileMediaRetention(Disk, Scope, Media);
        AggregateResult.Require(await retention.RetainAsync(
            [Media.Read(package.SourcePath).Evidence], CancellationToken.None));
        Admission.Set(new(GateDisposition.Ready, SetupIdentity.ReadyState(
            Scope, new PresentTarget(Media.Read(package.SourcePath).Evidence))));
    }

    internal async Task<(Guid Operation, Guid Run)> InterruptAbsentAsync()
    {
        await SeedAsync();
        var evidence = Media.Read(Old.SourcePath).Evidence;
        var plan = new OperationPlan(SetupVerb.Uninstall, Scope, new PresentTarget(evidence),
            AbsentTarget.Instance, [new(MaintenanceKind.Uninstall, evidence, [], false, true)], null);
        var record = new OperationJournal(Guid.NewGuid(), Guid.NewGuid(), [], plan,
            [new(evidence, MediaCachePaths.Package(Scope, evidence.PackageSha256),
                MediaCachePaths.Manifest(Scope, evidence.PackageSha256))], []);
        AggregateResult.Require(await Journal.SaveAsync(record, CancellationToken.None));
        Replace(null);
        Admission.Set(new(GateDisposition.Closed, null));
        return (record.OperationId, record.ActiveRunId);
    }

    internal string Owned(string relative) =>
        Path.Combine(Scope.AppDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

    internal RootPackage? PackageFor(EngineRequest request) =>
        request.Step.Operation == MaintenanceKind.Uninstall ? null :
            new[] { Old, New }.Single(package =>
                package.Manifest.ProductCode == request.Step.Package.Identity.ProductCode);

    internal SetupResources Resources(RootPackage package) => SetupResources.Create(
        package.Authoring.Snapshot, AggregateResult.Require(package.Authoring.Validate()),
        Scope, Programs);

    internal void Replace(RootPackage? target)
    {
        var previous = _current;
        foreach (var file in previous?.Manifest.Files ?? [])
            Files.DeleteFile(Owned(file.Path));
        if (previous is { } removed)
            Files.DeleteFile(Resources(removed).ShortcutPath);
        if (target is { } present)
            InstallFiles(present);
        _current = target;
        Registry.Set(target is { } installed ? Observe(installed) : RootRegistry.Empty());
        if (previous is { } old)
            RemoveEmptyDirectories(old);
    }

    internal void Apply(RootMsiResult result)
    {
        Replace(result.Installed);
        Registry.Mutate(result.Defect);
        switch (result.Defect)
        {
            case RootDefect.ObsoleteFile:
                Files.Put(Owned(OldRelative), Old.Contents[OldRelative].AsSpan());
                break;
            case RootDefect.MissingPayload or RootDefect.WrongHash or RootDefect.WrongLength:
                DamageOwned(result.Defect);
                break;
            case RootDefect.ReadFailure:
                Files.FailRead(Owned(ManagedRelative));
                break;
        }
    }

    internal void DamageOwned(RootDefect defect)
    {
        var path = Owned(defect == RootDefect.MissingPayload
            ? ManagedRelative : ExecutableRelative);
        switch (defect)
        {
            case RootDefect.MissingPayload:
                Files.DeleteFile(path);
                break;
            case RootDefect.WrongHash:
                Files.Put(path, new byte[Files.RawBytes(path).Length]);
                break;
            case RootDefect.WrongLength:
                Files.Put(path, [0]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(defect));
        }
    }

    private void InstallFiles(RootPackage package)
    {
        var resources = Resources(package);
        foreach (var directory in resources.Directories.Values)
            Files.CreateDirectory(directory);
        foreach (var file in package.Contents)
            Files.Put(Owned(file.Key), file.Value.AsSpan());
        // The shell observer supplies synthetic fields, not native .lnk bytes.
        // Its owned filesystem entry must nevertheless exist for directory
        // lifetime checks, and Replace removes only that owned entry.
        Files.Put(resources.ShortcutPath, [0x4c, 0x4e, 0x4b]);
        Files.Put(package.CachedPath, package.SourceBytes.AsSpan());
    }

    private RegistrationInventory Observe(RootPackage package)
    {
        var resources = Resources(package);
        var principal = new MsiPrincipal(package.Manifest.ProductCode, MsiContext.UserUnmanaged, Sid);
        var product = new RegisteredProduct(principal, ProductRegistrationState.Installed,
            package.Manifest.PackageCode, package.Manifest.Version,
            package.CachedPath, Scope.AppDirectory);
        var clients = resources.ComponentKeyPaths.ToImmutableDictionary(pair => pair.Key,
            pair => ImmutableArray.Create(new ComponentRegistration(principal,
                new(ComponentPathState.Local, pair.Value))));
        return new(ReadObservation<MsiRegistration>.Known(new(Sid, [product], clients)),
            Values(resources, SetupResources.InstallerKey),
            Values(resources, SetupResources.ComponentsKey),
            resources.Registry.Single(value => value.Key == SetupResources.RunKey).Value,
            ReadObservation<ShortcutFields>.Known(resources.Shortcut),
            new(PendingDisposition.None, []));
    }

    private static ImmutableDictionary<string, RegistryAtom> Values(
        SetupResources resources, string key) => resources.Registry.Where(value => value.Key == key)
        .ToImmutableDictionary(value => value.Name, value => value.Value,
            StringComparer.OrdinalIgnoreCase);

    private void RemoveEmptyDirectories(RootPackage package)
    {
        var resources = Resources(package);
        var directories = package.Authoring.Validate() is Effect<ValidatedPackageProfile>.Success(var profile)
            ? profile.DirectoryRemovals.Select(removal => resources.Directories[removal.DirectoryId])
            : throw new InvalidOperationException("Invalid synthetic fixture.");
        foreach (var directory in directories.Distinct().OrderByDescending(
            path => WindowsEvidencePath.Normalize(path).Count(character => character == '/')))
            Files.DeleteEmptyDirectory(directory);
    }

    private void SeedMedia()
    {
        Files.CreateDirectory(Programs);
        Files.Put(Old.SourcePath, Old.SourceBytes.AsSpan());
        Files.Put(New.SourcePath, New.SourceBytes.AsSpan());
        Files.Put(Prefix + "/Setup/Hermes.Setup.exe", [1]);
        Files.Put(Prefix + "/Setup/Hermes.Setup.dll", [2]);
    }

    public void Dispose() => Files.Dispose();
}

// These MSI database observations are synthetic; source bytes are not real MSIs.
internal sealed class RootDatabase(
    SetupDisk disk, ImmutableArray<RootPackage> packages) : IPackageDatabase, IPackageAuthoringDatabase
{
    public PackageDatabaseImage Read(string packagePath) => Snapshot(packagePath).Package;
    public PackageAuthoringSnapshot ReadAuthoring(string packagePath) => Snapshot(packagePath);

    private PackageAuthoringSnapshot Snapshot(string path)
    {
        using var stream = disk.Read(path);
        var hash = PackageMediaReader.Hash(stream);
        var package = packages.SingleOrDefault(candidate =>
            PackageMediaReader.Hash(candidate.SourceBytes.ToArray()) == hash)
            ?? throw new SetupIoException(SetupCode.MediaInvalid);
        var snapshot = package.Authoring.Snapshot;
        return WindowsEvidencePath.Same(path, package.CachedPath)
            ? snapshot with { Package = snapshot.Package with
                { Streams = ImmutableDictionary<string, uint>.Empty } }
            : snapshot;
    }
}

internal sealed class RootPaths : IPathIdentityQuery, ISetupPathSafety
{
    private ImmutableDictionary<string, string> _aliases =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
    private ImmutableHashSet<string> _unknown =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);

    internal void Alias(string path, string target) =>
        _aliases = _aliases.SetItem(WindowsEvidencePath.Normalize(path), target);

    internal void Unknown(string path) =>
        _unknown = _unknown.Add(WindowsEvidencePath.Normalize(path));

    public ReadObservation<PathIdentity> Read(string path)
    {
        var normalized = WindowsEvidencePath.Normalize(path);
        if (_unknown.Contains(normalized))
            return ReadObservation<PathIdentity>.Unknown(ObservationFault.Identity);
        return ReadObservation<PathIdentity>.Known(new(
            _aliases.GetValueOrDefault(normalized, normalized)));
    }

    public void RequireDirectory(string path) => Require(path);
    public void RequireFile(string path) => Require(path);

    private void Require(string path) =>
        SetupIo.Require(Read(path) is { Value: { } identity } &&
            WindowsEvidencePath.Same(path, identity.Path), SetupCode.RootMismatch);
}

internal sealed class RootFiles : ISetupFileSystem, IAtomicFileCommit, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly PhysicalSetupFileSystem _physical = new();
    private readonly List<(string Path, FileStream Stream)> _streams = [];
    private readonly List<string> _reads = [];
    private ImmutableHashSet<string> _reparsePoints =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);
    private string? _failedRead;

    internal IReadOnlyList<string> ReadPaths => _reads;
    internal IReadOnlyList<string> ActiveReadPaths =>
        _streams.Where(item => item.Stream.CanRead).Select(item => item.Path).ToArray();

    public DiskEntry Entry(string path)
    {
        var entry = _physical.Entry(Map(path));
        return entry with { ReparsePoint = entry.ReparsePoint ||
            _reparsePoints.Contains(WindowsEvidencePath.Normalize(path)) };
    }
    public void CreateDirectory(string path) => Directory.CreateDirectory(Map(path));
    public void DeleteFile(string path) => File.Delete(Map(path));
    public void Flush(FileStream stream) => stream.Flush(flushToDisk: true);
    public IReadOnlyList<string> Children(string path) => Directory.GetFileSystemEntries(Map(path))
        .Select(child => Path.Combine(path, Path.GetFileName(child))).ToArray();

    public FileStream Open(string path, FileMode mode, FileAccess access, FileShare share)
    {
        if ((access & FileAccess.Read) != 0)
        {
            _reads.Add(path);
            if (_failedRead is { } failed && WindowsEvidencePath.Same(failed, path))
                throw new IOException("sensitive synthetic read failure");
        }
        var stream = new FileStream(Map(path), mode, access, share);
        _streams.Add((path, stream));
        return stream;
    }

    public void Commit(string temporary, string destination, bool overwrite) =>
        File.Move(Map(temporary), Map(destination), overwrite);

    internal void Put(string path, ReadOnlySpan<byte> bytes)
    {
        var mapped = Map(path);
        Directory.CreateDirectory(Path.GetDirectoryName(mapped)
            ?? throw new InvalidOperationException("Missing synthetic parent."));
        File.WriteAllBytes(mapped, bytes.ToArray());
    }

    internal byte[] RawBytes(string path) => File.ReadAllBytes(Map(path));
    internal void FailRead(string path) => _failedRead = path;

    internal void MarkReparse(string path) =>
        _reparsePoints = _reparsePoints.Add(WindowsEvidencePath.Normalize(path));

    internal void DeleteEmptyDirectory(string path)
    {
        if (Entry(path).Kind == DiskEntryKind.Directory && Children(path).Count == 0)
            Directory.Delete(Map(path));
    }

    private string Map(string path)
    {
        var normalized = WindowsEvidencePath.Normalize(path);
        SetupIo.Require(string.Equals(normalized, RootFixture.WindowsPrefix,
                StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(RootFixture.WindowsPrefix + "/", StringComparison.OrdinalIgnoreCase),
            SetupCode.RootMismatch);
        var suffix = normalized[RootFixture.WindowsPrefix.Length..].TrimStart('/');
        return Path.Combine(new[] { _root }.Concat(
            suffix.Length == 0 ? [] : suffix.Split('/')).ToArray());
    }

    public void Dispose()
    {
        foreach (var item in _streams)
            item.Stream.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}

internal sealed class RootRegistry : IRegistrationInventory
{
    private RegistrationInventory _current = Empty();
    private Exception? _failure;
    private bool _omitQueryEvidence;
    internal int Reads { get; private set; }
    internal RegistrationInventory Current => _current;

    internal static RegistrationInventory Empty() => new(
        ReadObservation<MsiRegistration>.Known(new(RootFixture.Sid, [],
            ImmutableDictionary<Guid, ImmutableArray<ComponentRegistration>>.Empty)),
        ImmutableDictionary<string, RegistryAtom>.Empty,
        ImmutableDictionary<string, RegistryAtom>.Empty,
        new(RegistryAtomKind.Missing, null, null, []),
        ReadObservation<ShortcutFields>.Missing(), new(PendingDisposition.None, []));

    internal void Set(RegistrationInventory value) => _current = value;
    internal void Fail(Exception failure) => _failure = failure;

    public RegistrationInventory Read(IReadOnlyList<Guid> componentCodes)
    {
        Reads++;
        if (_failure is { } failure)
            throw failure;
        if (_current.Msi is not { State: ObservationState.Known, Value: { } msi })
            return _current;
        var missing = AuthoringFixture.File(RootFixture.ManagedRelative).ComponentCode;
        var codes = componentCodes.Where(code => !_omitQueryEvidence || code != missing);
        var clients = codes.Distinct().ToImmutableDictionary(code => code,
            code => msi.ComponentClients.GetValueOrDefault(code, []));
        return _current with { Msi = ReadObservation<MsiRegistration>.Known(
            msi with { ComponentClients = clients }) };
    }

    internal void Mutate(RootDefect defect)
    {
        _omitQueryEvidence = defect == RootDefect.MissingQueryEvidence;
        _current = defect switch
        {
            RootDefect.UnknownRegistration => _current with
                { Msi = ReadObservation<MsiRegistration>.Unknown(ObservationFault.NativeQuery, 5) },
            RootDefect.WrongBinding => _current with { ProductMetadata =
                _current.ProductMetadata.SetItem("InstallDirectory", Text("//foreign/share/App")) },
            RootDefect.MissingBinding => _current with { ProductMetadata =
                _current.ProductMetadata.Remove("InstallDirectory") },
            RootDefect.MissingMarker => _current with { ComponentMarkers =
                _current.ComponentMarkers.Remove(
                    AuthoringFixture.File(RootFixture.ManagedRelative).ComponentId) },
            RootDefect.Run => _current with
                { Run = Text((_current.Run.Text ?? "") + " --foreign") },
            RootDefect.ShortcutUnreadable => _current with
                { Shortcut = ReadObservation<ShortcutFields>.Unknown(ObservationFault.Unreadable) },
            RootDefect.Pending => _current with { Pending = new(PendingDisposition.Relevant,
                [new(RootFixture.Prefix + "/Local/Hermes/App/tray/Hermes.Tray.exe", null)]) },
            RootDefect.PendingUnknown => _current with { Pending = new(PendingDisposition.Unknown, []) },
            _ => MutateStructured(defect)
        };
    }

    private RegistrationInventory MutateStructured(RootDefect defect) => defect switch
    {
        RootDefect.RegisteredRoot or RootDefect.ManagedContext or RootDefect.MachineContext or
        RootDefect.ForeignSid or RootDefect.Advertised or RootDefect.Damaged => ChangeProduct(defect),
        RootDefect.MissingComponent or RootDefect.ForeignClient or RootDefect.WrongKeyPath =>
            ChangeClient(defect),
        RootDefect.ShortcutTarget or RootDefect.ShortcutArguments or RootDefect.ShortcutDirectory =>
            ChangeShortcut(defect),
        _ => _current
    };

    private RegistrationInventory ChangeProduct(RootDefect defect)
    {
        var msi = SetupStateProof.Msi(_current);
        var product = msi.RelatedProducts.Single();
        var changed = defect switch
        {
            RootDefect.RegisteredRoot => product with { InstallLocation = "//foreign/share/App" },
            RootDefect.ManagedContext => product with { Identity = product.Identity with
                { Context = MsiContext.UserManaged } },
            RootDefect.MachineContext => product with { Identity = product.Identity with
                { Context = MsiContext.Machine, Sid = "" } },
            RootDefect.ForeignSid => product with { Identity = product.Identity with { Sid = "S-1-5-21-9" } },
            RootDefect.Advertised => product with { State = ProductRegistrationState.Advertised },
            RootDefect.Damaged => product with { State = ProductRegistrationState.Damaged },
            _ => product
        };
        return _current with { Msi = ReadObservation<MsiRegistration>.Known(
            msi with { RelatedProducts = [changed] }) };
    }

    private RegistrationInventory ChangeClient(RootDefect defect)
    {
        var msi = SetupStateProof.Msi(_current);
        var code = AuthoringFixture.File(RootFixture.ManagedRelative).ComponentCode;
        var client = msi.ComponentClients[code].Single();
        var changed = defect switch
        {
            RootDefect.ForeignClient => client with
                { Client = client.Client with { ProductCode = Guid.NewGuid() } },
            RootDefect.WrongKeyPath => client with
                { KeyPath = new(ComponentPathState.Local, @"01:\Software\Foreign\Value") },
            _ => client
        };
        var clients = msi.ComponentClients.SetItem(code,
            defect == RootDefect.MissingComponent ? [] : [changed]);
        return _current with { Msi = ReadObservation<MsiRegistration>.Known(
            msi with { ComponentClients = clients }) };
    }

    private RegistrationInventory ChangeShortcut(RootDefect defect)
    {
        var shortcut = _current.Shortcut.Value
            ?? throw new InvalidOperationException("Missing synthetic shortcut.");
        var changed = defect switch
        {
            RootDefect.ShortcutTarget => shortcut with { Target = "//foreign/share/Tray.exe" },
            RootDefect.ShortcutArguments => shortcut with { Arguments = "--foreign" },
            RootDefect.ShortcutDirectory => shortcut with { WorkingDirectory = "//foreign/share" },
            _ => shortcut
        };
        return _current with { Shortcut = ReadObservation<ShortcutFields>.Known(changed) };
    }

    private static RegistryAtom Text(string value) => new(RegistryAtomKind.Text, value, null, []);
}

internal sealed class RootAdmission(UserInstallationScope scope) : IAdmissionSource
{
    private bool _busy;
    internal DiskGateSnapshot Snapshot { get; private set; } = new(GateDisposition.Missing, null);
    internal bool Held { get; private set; }
    internal int CloseCalls { get; private set; }
    internal int Publications { get; private set; }
    internal int SnapshotReads { get; private set; }
    internal void Set(DiskGateSnapshot snapshot) => Snapshot = snapshot;
    internal void MakeBusy() => _busy = true;

    public Effect<ISetupLease> TryAcquire()
    {
        if (_busy)
            return Effect<ISetupLease>.Fail(SetupCode.Busy);
        Assert.False(Held);
        Held = true;
        return Effect<ISetupLease>.Ok(new Lease(this, scope.GatePath, CloseCalls));
    }

    private sealed class Lease(
        RootAdmission owner, string path, int initialClosures) : IReadableSetupLease
    {
        public string GatePath => path;
        public bool IsHeldAndClosed =>
            owner.Held && owner.Snapshot.Disposition == GateDisposition.Closed;

        public DiskGateSnapshot ReadSnapshot()
        {
            Assert.True(owner.Held);
            Assert.Equal(initialClosures, owner.CloseCalls);
            owner.SnapshotReads++;
            return owner.Snapshot;
        }

        public Task<Effect<Unit>> CloseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(owner.Held);
            owner.CloseCalls++;
            owner.Snapshot = new(GateDisposition.Closed, null);
            return Task.FromResult(Effect<Unit>.Ok(default));
        }

        public Task<Effect<Unit>> PublishReadyAsync(
            GateReadyState ready, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(IsHeldAndClosed);
            owner.Publications++;
            owner.Snapshot = new(GateDisposition.Ready, ready);
            return Task.FromResult(Effect<Unit>.Ok(default));
        }

        public void Dispose() => owner.Held = false;
    }
}

internal sealed record RootMsiResult(
    uint Code, RootPackage? Installed, RootDefect Defect = RootDefect.None,
    bool Fence = true, MsiFailureEvidence? Failure = null,
    NativePreflightEvidence? Preflight = null, bool OwnPreflight = true);

internal sealed class RootEngine(RootFixture world) : IGuardedEngine
{
    private ImmutableQueue<RootMsiResult> _results = ImmutableQueue<RootMsiResult>.Empty;
    private readonly List<EngineRequest> _requests = [];
    internal int Calls { get; private set; }
    internal IReadOnlyList<EngineRequest> Requests => _requests;
    internal void Enqueue(RootMsiResult result) => _results = _results.Enqueue(result);

    public Task<Effect<IPreparedMsiCall>> PrepareAsync(
        ISetupLease lease, EngineRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.True(lease.IsHeldAndClosed);
        _requests.Add(request);
        return Task.FromResult(Effect<IPreparedMsiCall>.Ok(
            new Call(this, lease, request, Guid.NewGuid())));
    }

    private EngineReceipt Execute(ISetupLease lease, EngineRequest request, Guid run)
    {
        Assert.True(lease.IsHeldAndClosed);
        Assert.True(world.Files.ActiveReadPaths.Count >= 3);
        Assert.DoesNotContain(world.Files.ActiveReadPaths, path =>
            WindowsEvidencePath.Same(path, world.Old.CachedPath) ||
            WindowsEvidencePath.Same(path, world.New.CachedPath));
        Assert.Equal(run, world.Journal.Read()?.ActiveRunId);
        Calls++;
        var result = _results.IsEmpty ? new RootMsiResult(0, world.PackageFor(request)) : _results.Peek();
        if (!_results.IsEmpty)
            _results = _results.Dequeue();
        if (result.Code is not (1605 or 1618) && result.Fence)
            world.Apply(result);
        return new(run, true, true, result.Code, false, result.OwnPreflight, result.Fence)
        {
            MsiFailure = result.Failure,
            NativePreflight = result.Preflight
        };
    }

    private sealed class Call(
        RootEngine engine, ISetupLease lease, EngineRequest request, Guid run) : IPreparedMsiCall
    {
        public Guid RunId => run;
        public Effect<EngineReceipt> Execute() =>
            Effect<EngineReceipt>.Ok(engine.Execute(lease, request, run));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class RootFactory(RootFixture world) : ISetupApplicationFactory
{
    private ImmutableArray<KeyValuePair<string, string?>> _metadata =
        [new(CompiledGuardTrust.MetadataName, string.Join(';', world.Old.Authoring.Approved.Sha256))];
    internal int Creates { get; private set; }
    internal void Trust(ImmutableArray<KeyValuePair<string, string?>> metadata) => _metadata = metadata;

    public Effect<SetupApplication> Create()
    {
        Creates++;
        var trust = CompiledGuardTrust.Read(_metadata);
        return trust is Effect<ApprovedGuardProfile>.Success(var approved)
            ? Effect<SetupApplication>.Ok(SetupComposition.Create(world.Capabilities, approved))
            : Effect<SetupApplication>.Fail(trust.FailureCode);
    }
}
