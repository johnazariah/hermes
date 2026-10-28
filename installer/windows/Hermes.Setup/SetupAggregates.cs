using System.Collections.Immutable;
using Hermes.Servicing;

namespace Hermes.Setup;

internal sealed class SetupStateProof(
    SetupDisk disk,
    UserInstallationScope scope,
    string programsDirectory,
    IRegistrationInventory registrations,
    IPathIdentityQuery paths)
{
    private static RegistryAtom Missing { get; } =
        new(RegistryAtomKind.Missing, null, null, []);

    internal void RequirePlacement(IReadOnlyList<string> coordinatorImages)
    {
        RequireDirectoryIdentity(scope.AppDirectory);
        RequireDirectoryIdentity(programsDirectory);
        SetupIo.Require(coordinatorImages.Count >= 2, SetupCode.InvalidEvidence);
        foreach (var image in coordinatorImages)
        {
            disk.Paths.RequireFile(image);
            SetupIo.Require(Stable(image) &&
                !WindowsEvidencePath.Intersects(image, scope.AppDirectory),
                SetupCode.RootMismatch);
        }
    }

    internal RegistrationInventory Read(IReadOnlyList<SetupPackage> packages)
    {
        RequireDirectoryIdentity(scope.AppDirectory);
        var codes = packages.SelectMany(package => package.Profile.Components)
            .Select(component => component.Code).Distinct().ToArray();
        var observed = registrations.Read(codes);
        var msi = Msi(observed);
        SetupIo.Require(!msi.RelatedProducts.IsDefault && msi.RelatedProducts.Length <= 1 &&
            msi.RelatedProducts.All(product => Eligible(product, msi.InvokingSid)),
            SetupCode.OwnershipRefused);
        RequireObservations(observed);
        return observed;
    }

    private static void RequireObservations(RegistrationInventory observed)
    {
        SetupIo.Require(observed.Shortcut.State != ObservationState.Unknown, SetupCode.InvalidEvidence,
            new(SetupEvidenceStage.Unspecified, SetupEvidenceCause.ShortcutObservation,
                observed.Shortcut.Fault, StartupEvidenceErrorKind.HResult, observed.Shortcut.NativeCode));
        SetupIo.Require(observed.Pending.Disposition is not
            (PendingDisposition.Unknown or PendingDisposition.Alias), SetupCode.InvalidEvidence,
            new(SetupEvidenceStage.Unspecified,
                observed.Pending.Disposition == PendingDisposition.Alias
                    ? SetupEvidenceCause.PendingAlias : SetupEvidenceCause.PendingUnknown,
                ErrorKind: StartupEvidenceErrorKind.Win32, ErrorCode: observed.Pending.NativeCode));
        SetupIo.Require(observed.ProductMetadata.Values.Concat(observed.ComponentMarkers.Values)
            .Append(observed.Run).All(value => value.Kind != RegistryAtomKind.Unsupported),
            SetupCode.InvalidEvidence,
            new(SetupEvidenceStage.Unspecified, SetupEvidenceCause.RegistryUnsupported));
    }

    internal static MsiRegistration Msi(RegistrationInventory inventory) =>
        inventory.Msi is { State: ObservationState.Known, Value: { } msi }
            ? msi : throw new SetupIoException(SetupCode.InvalidEvidence,
                new(SetupEvidenceStage.Unspecified, SetupEvidenceCause.MsiObservation,
                    inventory.Msi.Fault, StartupEvidenceErrorKind.Win32, inventory.Msi.NativeCode));

    internal bool Matches(
        SetupPackage? target, IReadOnlyList<SetupPackage> universe,
        RegistrationInventory observed) =>
        RegisteredOwnershipMatches(target, universe, observed) &&
        PayloadMatches(target, universe);

    // Registered ownership permits servicing damaged owned bytes, not publishing
    // Ready. Matches additionally requires the complete verified payload.
    internal bool RegisteredOwnershipMatches(
        SetupPackage? target, IReadOnlyList<SetupPackage> universe,
        RegistrationInventory observed) =>
        ProductMatches(target, Msi(observed)) &&
        Owners(target, universe, Msi(observed)) &&
        RegistryMatches(target, universe, observed) &&
        ShortcutMatches(target, observed.Shortcut) &&
        DirectoriesMatch(target, universe);

    internal bool Owners(
        SetupPackage? target, IReadOnlyList<SetupPackage> universe, MsiRegistration msi) =>
        universe.SelectMany(package => package.Profile.Components)
            .Select(component => component.Code).Distinct()
            .All(code => OwnerMatches(code, target, msi));

    internal static bool RegistrationMatches(
        RegisteredProduct registered, SetupCachedPackage cached) =>
        registered.Identity.ProductCode == cached.Profile.Identity.ProductCode &&
        registered.PackageCode == cached.Profile.Identity.PackageCode &&
        registered.Version == cached.Profile.Identity.Version;

    internal bool GateMatches(DiskGateSnapshot gate, SetupPackage? target)
    {
        if (gate.Disposition == GateDisposition.Closed)
            return gate.Ready is null;
        if (gate.Disposition == GateDisposition.Missing)
            return target is null && gate.Ready is null;
        var expected = SetupIdentity.ReadyState(scope, Target(target));
        return (gate.Ready?.Installation, expected.Installation) switch
        {
            (null, null) => gate.Ready is not null,
            ({ } actual, { } desired) => actual.ProductCode == desired.ProductCode &&
                actual.Binding.UpgradeCode == desired.Binding.UpgradeCode &&
                actual.Binding.AdmissionAbi == desired.Binding.AdmissionAbi &&
                SamePhysical(actual.Binding.AppDirectory, scope.AppDirectory) &&
                !actual.Images.IsDefault && actual.Images.SequenceEqual(desired.Images),
            _ => false
        };
    }

    internal static InstallationTarget Target(SetupPackage? package) =>
        package is { } present ? new PresentTarget(present.Evidence) : AbsentTarget.Instance;

    private bool Eligible(RegisteredProduct product, string sid) =>
        product.State == ProductRegistrationState.Installed &&
        product.Identity.Context == MsiContext.UserUnmanaged &&
        product.Identity.Sid == sid &&
        product.PackageCode is not null && product.Version is not null &&
        !string.IsNullOrWhiteSpace(product.LocalPackage) &&
        (string.IsNullOrEmpty(product.InstallLocation) ||
            SamePhysical(product.InstallLocation, scope.AppDirectory));

    private static bool ProductMatches(SetupPackage? target, MsiRegistration actual) =>
        target is null
            ? actual.RelatedProducts.IsEmpty
            : actual.RelatedProducts is [{ } product] &&
                product.Identity.ProductCode == target.Evidence.Identity.ProductCode &&
                product.PackageCode == target.Evidence.Identity.PackageCode &&
                product.Version == target.Evidence.Identity.Version;

    private static bool OwnerMatches(Guid code, SetupPackage? target, MsiRegistration msi)
    {
        SetupIo.Require(msi.ComponentClients.ContainsKey(code) &&
            !msi.ComponentClients[code].IsDefault, SetupCode.InvalidEvidence,
            new(SetupEvidenceStage.Unspecified, SetupEvidenceCause.ComponentClientsIncomplete));
        var clients = msi.ComponentClients[code];
        var keyPath = target?.Resources.ComponentKeyPaths.GetValueOrDefault(code);
        if (keyPath is null)
            return clients.IsEmpty;
        return target is { } present && clients is [{ } client] &&
            client.Client == new MsiPrincipal(present.Evidence.Identity.ProductCode,
                MsiContext.UserUnmanaged, msi.InvokingSid) &&
            client.KeyPath.State == ComponentPathState.Local &&
            string.Equals(client.KeyPath.Path, keyPath, StringComparison.OrdinalIgnoreCase);
    }

    private bool RegistryMatches(
        SetupPackage? target, IReadOnlyList<SetupPackage> universe, RegistrationInventory actual)
    {
        var desired = target?.Resources.Registry ?? [];
        var obsolete = universe.SelectMany(package => package.Resources.Registry)
            .DistinctBy(value => (value.Key, value.Name))
            .Where(value => !desired.Any(current =>
                current.Key == value.Key && current.Name == value.Name));
        return desired.All(value => ValueMatches(value, Actual(actual, value))) &&
            obsolete.All(value => Actual(actual, value).Kind == RegistryAtomKind.Missing) &&
            actual.ProductMetadata.Keys.All(name => desired.Any(value =>
                value.Key == SetupResources.InstallerKey && value.Name == name)) &&
            actual.ComponentMarkers.Keys.All(name => desired.Any(value =>
                value.Key == SetupResources.ComponentsKey && value.Name == name)) &&
            (target is not null || actual.Run.Kind == RegistryAtomKind.Missing);
    }

    private static RegistryAtom Actual(
        RegistrationInventory actual, RegistryExpectation expected) => expected.Key switch
    {
        SetupResources.InstallerKey => actual.ProductMetadata.GetValueOrDefault(expected.Name, Missing),
        SetupResources.ComponentsKey => actual.ComponentMarkers.GetValueOrDefault(expected.Name, Missing),
        SetupResources.RunKey when expected.Name == "Hermes" => actual.Run,
        _ => throw new SetupIoException(SetupCode.InvalidEvidence)
    };

    private bool ValueMatches(RegistryExpectation expected, RegistryAtom actual) =>
        (expected.Key, expected.Name, expected.Value, actual) switch
        {
            (SetupResources.RunKey, "Hermes", var value, _) => RunMatches(actual, value),
            (SetupResources.InstallerKey, "InstallDirectory",
                { Kind: RegistryAtomKind.Text, Text: { } wanted },
                { Kind: RegistryAtomKind.Text, Text: { } found }) => SamePhysical(found, wanted),
            (SetupResources.InstallerKey, "ProductCode",
                { Kind: RegistryAtomKind.Text, Text: { } wanted },
                { Kind: RegistryAtomKind.Text, Text: { } found }) =>
                string.Equals(wanted, found, StringComparison.OrdinalIgnoreCase),
            (_, _, { Kind: RegistryAtomKind.Integer, Integer: { } wanted },
                { Kind: RegistryAtomKind.Integer, Integer: { } found }) => wanted == found,
            (_, _, { Kind: RegistryAtomKind.Text, Text: { } wanted },
                { Kind: RegistryAtomKind.Text, Text: { } found }) => wanted == found,
            _ => false
        };

    private bool RunMatches(RegistryAtom actual, RegistryAtom expected) =>
        (RunCommandReader.Read(actual), RunCommandReader.Read(expected)) switch
        {
            ({ State: ObservationState.Known, Value: { } found },
             { State: ObservationState.Known, Value: { } wanted }) =>
                found.Arguments == wanted.Arguments &&
                SamePhysical(found.Executable, wanted.Executable),
            _ => false
        };

    private bool ShortcutMatches(
        SetupPackage? target, ReadObservation<ShortcutFields> actual) =>
        target is null
            ? actual.State == ObservationState.Missing
            : ShortcutClassifier.Compare(actual, target.Resources.Shortcut, paths) ==
                ShortcutComparison.Matched;

    private bool PayloadMatches(SetupPackage? target, IReadOnlyList<SetupPackage> universe)
    {
        var inventory = new OwnedPayloadInventory(disk, scope);
        var desired = (target?.Profile.Manifest.Files ?? [])
            .Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (target is null || OwnedPayloadInventory.Matches(
                target.Profile.Manifest, inventory.Read(target.Profile.Manifest))) &&
            inventory.RemovedFilesAbsent(
                universe.Select(package => package.Profile.Manifest), desired);
    }

    private bool DirectoriesMatch(SetupPackage? target, IReadOnlyList<SetupPackage> universe)
    {
        var desired = (target?.Resources.Directories.Values ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var obsolete = universe.SelectMany(package => package.Profile.DirectoryRemovals
            .Select(removal => package.Resources.Directories[removal.DirectoryId]))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(path => !desired.Contains(path));
        return desired.All(DirectoryPresent) && obsolete.All(RemovedOrNonempty);
    }

    private bool DirectoryPresent(string path)
    {
        RequireDirectoryIdentity(path);
        return disk.Files.Entry(path).Kind == DiskEntryKind.Directory;
    }

    private bool RemovedOrNonempty(string path)
    {
        RequireDirectoryIdentity(path);
        return disk.Files.Entry(path).Kind switch
        {
            DiskEntryKind.Missing => true,
            DiskEntryKind.Directory => disk.Files.Children(path).Count > 0,
            _ => false
        };
    }

    private void RequireDirectoryIdentity(string path)
    {
        disk.Paths.RequireDirectory(path);
        var entry = disk.Files.Entry(path);
        SetupIo.Require(!entry.ReparsePoint && entry.Kind != DiskEntryKind.File && Stable(path),
            SetupCode.RootMismatch);
    }

    private bool SamePhysical(string actual, string expected) =>
        WindowsEvidencePath.Same(actual, expected) && Stable(actual) && Stable(expected);

    private bool Stable(string path) =>
        paths.Read(path) is { State: ObservationState.Known, Value: { } identity } &&
        WindowsEvidencePath.Same(path, identity.Path);
}

internal sealed class InstallationInspection(
    SetupDisk disk,
    UserInstallationScope scope,
    ImmutableArray<string> coordinatorImages,
    SetupPackageCatalog packages,
    SetupStateProof proof,
    ISetupJournalReader journal) : IInstallationInspection
{
    public Task<Effect<Inspection>> InspectAsync(
        SetupCommand command, ISetupLease lease, CancellationToken cancellationToken) =>
        AggregateResult.ReadAsync(() => Inspect(command, lease),
            SetupCode.InvalidEvidence, cancellationToken);

    private Inspection Inspect(SetupCommand command, ISetupLease lease)
    {
        var gate = InspectionEvidence.Read(SetupEvidenceStage.Gate, () => Snapshot(lease));
        InspectionEvidence.Check(SetupEvidenceStage.Placement,
            () => proof.RequirePlacement(coordinatorImages));
        InspectionEvidence.Check(SetupEvidenceStage.Gate, () => RefuseWrongMode(command, gate));
        var record = InspectionEvidence.Read(SetupEvidenceStage.Journal, journal.Read);
        var recovery = InspectionEvidence.Read(SetupEvidenceStage.Recovery,
            () => Recovery(command, gate, record));
        var initial = InspectionEvidence.Read(SetupEvidenceStage.InitialInventory, () => proof.Read([]));
        var current = InspectionEvidence.Read(SetupEvidenceStage.CurrentPackage,
            () => Current(initial, record));
        var candidate = InspectionEvidence.Read(SetupEvidenceStage.CandidatePackage,
            () => command is InstallCommand install
                ? packages.Source(install.PackagePath, PackageAuthoringMode.CandidateSource) : null);
        return InspectCandidate(command, gate, initial, current, candidate, recovery);
    }

    private Inspection InspectCandidate(
        SetupCommand command, DiskGateSnapshot gate, RegistrationInventory initial,
        SetupPackage? current, SetupPackage? candidate, InterruptedOperation? recovery)
    {
        var universe = InspectionEvidence.Read(SetupEvidenceStage.RecoveryPackages,
            () => Combine(current, candidate, recovery));
        var actual = InspectionEvidence.Read(SetupEvidenceStage.FinalInventory, () => proof.Read(universe));
        InspectionEvidence.Check(SetupEvidenceStage.Ownership,
            () => RequireStableInventory(initial, actual));
        InspectionEvidence.Check(SetupEvidenceStage.Ownership,
            () => RequireCurrent(command, gate, current, actual));
        InspectionEvidence.Check(SetupEvidenceStage.Ownership,
            () => SetupIo.Require(proof.Owners(current, universe, SetupStateProof.Msi(actual)),
                SetupCode.OwnershipRefused));
        InspectionEvidence.Check(SetupEvidenceStage.Collisions, () => RequireCollisions(current, universe));
        return new(scope, gate.Disposition, SetupStateProof.Target(current),
            candidate?.Evidence, recovery);
    }

    private DiskGateSnapshot Snapshot(ISetupLease lease)
    {
        SetupIo.Require(SetupIdentity.SamePath(lease.GatePath, scope.GatePath),
            SetupCode.RootMismatch);
        return lease is IReadableSetupLease readable
            ? readable.ReadSnapshot()
            : throw new SetupIoException(SetupCode.InvalidEvidence);
    }

    private static void RefuseWrongMode(SetupCommand command, DiskGateSnapshot gate)
    {
        if (gate.Disposition == GateDisposition.Closed &&
            command is not MaintenanceCommand { Operation: SetupVerb.Recover })
            throw new SetupIoException(SetupCode.RecoveryRequired);
    }

    private SetupPackage? Current(RegistrationInventory actual, OperationJournal? record)
    {
        var product = SetupStateProof.Msi(actual).RelatedProducts.SingleOrDefault();
        if (product is null)
            return null;
        var cached = packages.Cached(product.LocalPackage
            ?? throw new SetupIoException(SetupCode.InvalidEvidence));
        SetupIo.Require(SetupStateProof.RegistrationMatches(product, cached),
            SetupCode.MediaIdentityMismatch);
        return packages.RegisteredSource(cached, record);
    }

    private static InterruptedOperation? Recovery(
        SetupCommand command, DiskGateSnapshot gate, OperationJournal? record) =>
        command is MaintenanceCommand { Operation: SetupVerb.Recover } &&
        gate.Disposition == GateDisposition.Closed
            ? record?.AsInterrupted() ?? throw new SetupIoException(SetupCode.JournalInvalid)
            : null;

    private SetupPackage[] Combine(
        SetupPackage? current, SetupPackage? candidate, InterruptedOperation? recovery) =>
        new[] { current, candidate }.OfType<SetupPackage>()
            .Concat((recovery?.Packages ?? []).Select(packages.Retained))
            .DistinctBy(package => package.Evidence.PackageSha256).ToArray();

    private static void RequireStableInventory(
        RegistrationInventory initial, RegistrationInventory actual) =>
        SetupIo.Require(SetupStateProof.Msi(initial).InvokingSid ==
                SetupStateProof.Msi(actual).InvokingSid &&
            SetupStateProof.Msi(initial).RelatedProducts.SequenceEqual(
                SetupStateProof.Msi(actual).RelatedProducts), SetupCode.InvalidEvidence,
            new(SetupEvidenceStage.Unspecified, SetupEvidenceCause.InventoryChanged));

    private void RequireCurrent(
        SetupCommand command, DiskGateSnapshot gate,
        SetupPackage? current, RegistrationInventory actual)
    {
        SetupIo.Require(proof.GateMatches(gate, current), SetupCode.OwnershipRefused);
        SetupIo.Require(actual.Pending.Disposition == PendingDisposition.None,
            SetupCode.RebootRequired);
        SetupPackage[] universe = current is { } present ? [present] : [];
        var matches = (command, gate.Disposition, current) switch
        {
            (InstallCommand or MaintenanceCommand
                { Operation: SetupVerb.Repair or SetupVerb.Uninstall },
                GateDisposition.Ready, not null) =>
                proof.RegisteredOwnershipMatches(current, universe, actual),
            _ => proof.Matches(current, universe, actual)
        };
        SetupIo.Require(matches, SetupCode.OwnershipRefused);
    }

    private void RequireCollisions(SetupPackage? current, IEnumerable<SetupPackage> universe)
    {
        var owned = (current?.Profile.Manifest.Files ?? [])
            .Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inventory = new OwnedPayloadInventory(disk, scope);
        foreach (var package in universe)
            inventory.RefuseCollisions(package.Profile.Manifest, owned);
    }
}

internal sealed class InstallationVerification(
    SetupPackageCatalog packages, SetupStateProof proof) : IInstallationVerification
{
    public Task<Effect<VerificationEvidence>> VerifyAsync(
        OperationJournal journal, InstallationTarget expected,
        CancellationToken cancellationToken) =>
        AggregateResult.ReadAsync(() => Verify(journal, expected),
            SetupCode.VerificationFailed, cancellationToken);

    private VerificationEvidence Verify(OperationJournal journal, InstallationTarget expected)
    {
        RequireCompletion(journal);
        var universe = journal.Plan.Packages.Select(packages.Retained).ToArray();
        var actual = proof.Read(universe);
        RequireActualCache(actual, universe);
        if (actual.Pending.Disposition == PendingDisposition.Relevant)
            return new(null, true);
        var desired = Resolve(expected, universe);
        if (proof.Matches(desired, universe, actual))
            return new(expected, false);
        var previous = Resolve(journal.Plan.Previous, universe);
        SetupIo.Require(!journal.Plan.IsRecovery &&
            proof.Matches(previous, universe, actual), SetupCode.VerificationFailed);
        return new(journal.Plan.Previous, false);
    }

    private static void RequireCompletion(OperationJournal journal)
    {
        SetupIo.Require(!journal.Receipts.IsDefaultOrEmpty &&
            journal.ActiveRunId != Guid.Empty &&
            journal.Receipts[^1].RunId == journal.ActiveRunId &&
            !journal.RevokedRunIds.Contains(journal.ActiveRunId) &&
            CompletionPolicy.BeforeVerification(journal.Receipts[^1],
                journal.Plan.IsRecovery) is null, SetupCode.VerificationFailed);
    }

    private void RequireActualCache(
        RegistrationInventory actual, IReadOnlyList<SetupPackage> universe)
    {
        if (SetupStateProof.Msi(actual).RelatedProducts.SingleOrDefault() is not { } product)
            return;
        var cached = packages.Cached(product.LocalPackage
            ?? throw new SetupIoException(SetupCode.InvalidEvidence));
        SetupIo.Require(SetupStateProof.RegistrationMatches(product, cached),
            SetupCode.MediaIdentityMismatch);
        var matching = universe.Where(package =>
            package.Evidence.Identity == cached.Profile.Identity &&
            package.Evidence.ManifestSha256 == cached.ManifestSha256).ToArray();
        SetupIo.Require(matching.Length == 1, SetupCode.VerificationFailed);
        SetupPackageCatalog.RequireCachedMatches(cached, matching[0]);
    }

    private static SetupPackage? Resolve(
        InstallationTarget target, IEnumerable<SetupPackage> universe) => target switch
    {
        AbsentTarget => null,
        PresentTarget present => universe.Single(package =>
            SetupIdentity.SameContent(package.Evidence, present.Package)),
        _ => throw new SetupIoException(SetupCode.InvalidEvidence)
    };
}
