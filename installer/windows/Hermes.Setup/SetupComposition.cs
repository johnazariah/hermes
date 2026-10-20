using System.Collections.Immutable;
using Hermes.Servicing;

namespace Hermes.Setup;

internal static class CompiledGuardTrust
{
    internal const string MetadataName = "HermesApprovedGuardSha256";

    internal static Effect<ApprovedGuardProfile> Read(
        IEnumerable<KeyValuePair<string, string?>> metadata)
    {
        var values = metadata.Where(pair => pair.Key == MetadataName).ToArray();
        if (values.Length == 0)
            return Effect<ApprovedGuardProfile>.Fail(SetupCode.Unavailable);
        if (values is not [{ Value: { Length: > 0 } text }])
            return Effect<ApprovedGuardProfile>.Fail(SetupCode.InvalidEvidence);
        var hashes = text.Split(';');
        return hashes.All(SetupPackageCatalog.CanonicalHash) &&
            hashes.Distinct(StringComparer.Ordinal).Count() == hashes.Length
            ? Effect<ApprovedGuardProfile>.Ok(new(HermesServicingProtocol.AdmissionAbi,
                PackageMediaReader.RootToken, hashes.ToImmutableHashSet(StringComparer.Ordinal)))
            : Effect<ApprovedGuardProfile>.Fail(SetupCode.InvalidEvidence);
    }
}

internal sealed record SetupCapabilities(
    UserInstallationScope Scope,
    string ProgramsDirectory,
    ImmutableArray<string> CoordinatorImages,
    SetupDisk Disk,
    IPackageDatabase Database,
    IPackageAuthoringDatabase Authoring,
    IRegistrationInventory Registry,
    IPathIdentityQuery Paths,
    IAdmissionSource Admission,
    IGuardedEngine Engine);

internal sealed class SetupApplication(
    SetupOrchestrator orchestrator,
    FileMediaRetention retention,
    SetupPackageCatalog packages) : IAsyncDisposable
{
    internal Task<SetupExecution> ExecuteAsync(
        SetupCommand command, CancellationToken cancellationToken) =>
        orchestrator.ExecuteAsync(command, cancellationToken);

    public ValueTask DisposeAsync()
    {
        try { retention.Dispose(); }
        finally { packages.Dispose(); }
        return ValueTask.CompletedTask;
    }
}

internal interface ISetupApplicationFactory
{
    Effect<SetupApplication> Create();
}

internal static class SetupComposition
{
    internal static SetupApplication Create(
        SetupCapabilities capabilities, ApprovedGuardProfile approved)
    {
        var media = new PackageMediaReader(
            capabilities.Disk, capabilities.Scope, capabilities.Database);
        var packages = new SetupPackageCatalog(capabilities.Disk, capabilities.Scope,
            capabilities.ProgramsDirectory, media, capabilities.Authoring, approved);
        var proof = new SetupStateProof(capabilities.Disk, capabilities.Scope,
            capabilities.ProgramsDirectory, capabilities.Registry, capabilities.Paths);
        var journal = new FileOperationJournal(capabilities.Disk, capabilities.Scope);
        var retention = new FileMediaRetention(capabilities.Disk, capabilities.Scope, media);
        var inspection = new InstallationInspection(capabilities.Disk, capabilities.Scope,
            capabilities.CoordinatorImages, packages, proof, journal);
        return new(new(capabilities.Admission, inspection, retention, journal,
            capabilities.Engine, new InstallationVerification(packages, proof)),
            retention, packages);
    }
}
