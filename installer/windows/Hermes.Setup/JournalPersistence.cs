using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hermes.Servicing;

namespace Hermes.Setup;

internal static class MediaCachePaths
{
    internal static string Directory(UserInstallationScope scope, string hash)
    {
        SetupIo.Require(hash.Length == 64 && hash.All(Uri.IsHexDigit),
            SetupCode.MediaIdentityMismatch);
        return Path.Combine(scope.PackagesDirectory, hash.ToLowerInvariant());
    }

    internal static string Package(UserInstallationScope scope, string hash) =>
        Path.Combine(Directory(scope, hash), "package.msi");

    internal static string Manifest(UserInstallationScope scope, string hash) =>
        Path.Combine(Directory(scope, hash), "manifest.json");
}

internal sealed record StoredInterrupted(
    Guid OperationId,
    Guid ActiveRunId,
    ImmutableArray<Guid> RevokedRunIds,
    PackageEvidence? Previous,
    PackageEvidence? Desired,
    ImmutableArray<PackageEvidence> Packages);

internal sealed record JournalDocument(
    int SchemaVersion,
    string AppDirectory,
    Guid OperationId,
    Guid ActiveRunId,
    ImmutableArray<Guid> RevokedRunIds,
    SetupVerb Verb,
    PackageEvidence? Previous,
    PackageEvidence? Desired,
    ImmutableArray<EngineStep> Steps,
    StoredInterrupted? Interrupted,
    ImmutableArray<RetainedMedia> Media,
    ImmutableArray<EngineReceipt> Receipts);

internal static class JournalDocuments
{
    internal static JournalDocument Encode(
        OperationJournal journal, UserInstallationScope scope)
    {
        ValidateHeader(journal.OperationId, journal.ActiveRunId, journal.RevokedRunIds);
        RequireMedia(journal.Media, scope);
        PackageEvidence Bind(PackageEvidence package) => Rebind(package, journal.Media, scope);
        return new(1, scope.AppDirectory, journal.OperationId, journal.ActiveRunId,
            journal.RevokedRunIds, journal.Plan.Verb,
            Package(journal.Plan.Previous, Bind), Package(journal.Plan.Desired, Bind),
            journal.Plan.Steps.Select(step => step with { Package = Bind(step.Package) })
                .ToImmutableArray(),
            StoreInterrupted(journal.Plan.Interrupted, Bind),
            journal.Media.Select(media => media with { Evidence = Bind(media.Evidence) })
                .ToImmutableArray(), journal.Receipts);
    }

    internal static OperationJournal Decode(
        JournalDocument document, UserInstallationScope scope)
    {
        SetupIo.Require(document.SchemaVersion == 1 &&
            SetupIdentity.SamePath(document.AppDirectory, scope.AppDirectory) &&
            Enum.IsDefined(document.Verb) && !document.Steps.IsDefaultOrEmpty &&
            !document.Receipts.IsDefault, SetupCode.JournalInvalid);
        ValidateHeader(document.OperationId, document.ActiveRunId, document.RevokedRunIds);
        RequireMedia(document.Media, scope);
        PackageEvidence Bind(PackageEvidence package) => Rebind(package, document.Media, scope);
        var plan = new OperationPlan(document.Verb, scope,
            Target(document.Previous, Bind), Target(document.Desired, Bind),
            document.Steps.Select(step => step with { Package = Bind(step.Package) })
                .ToImmutableArray(), RestoreInterrupted(document.Interrupted, Bind));
        return new(document.OperationId, document.ActiveRunId, document.RevokedRunIds,
            plan, document.Media.Select(media => media with { Evidence = Bind(media.Evidence) })
                .ToImmutableArray(), document.Receipts);
    }

    private static void ValidateHeader(
        Guid operation, Guid active, ImmutableArray<Guid> revoked) =>
        SetupIo.Require(operation != Guid.Empty && active != Guid.Empty &&
            !revoked.IsDefault && revoked.All(run => run != Guid.Empty && run != active) &&
            revoked.Distinct().Count() == revoked.Length, SetupCode.JournalInvalid);

    private static void RequireMedia(
        ImmutableArray<RetainedMedia> media, UserInstallationScope scope)
    {
        SetupIo.Require(!media.IsDefaultOrEmpty &&
            media.Select(item => item.Evidence.PackageSha256)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() == media.Length,
            SetupCode.JournalInvalid);
        SetupIo.Require(media.All(item =>
            SetupIdentity.SamePath(item.PackagePath,
                MediaCachePaths.Package(scope, item.Evidence.PackageSha256)) &&
            SetupIdentity.SamePath(item.ManifestPath,
                MediaCachePaths.Manifest(scope, item.Evidence.PackageSha256)) &&
            SetupIdentity.SamePath(item.Evidence.AppDirectory, scope.AppDirectory)),
            SetupCode.JournalInvalid);
    }

    private static PackageEvidence Rebind(
        PackageEvidence package, ImmutableArray<RetainedMedia> media,
        UserInstallationScope scope)
    {
        SetupIo.Require(media.Count(item =>
            SetupIdentity.SameContent(item.Evidence, package)) == 1,
            SetupCode.JournalInvalid);
        return package with
        {
            SourcePath = MediaCachePaths.Package(scope, package.PackageSha256),
            ManifestSourcePath = MediaCachePaths.Manifest(scope, package.PackageSha256)
        };
    }

    private static PackageEvidence? Package(
        InstallationTarget target, Func<PackageEvidence, PackageEvidence> bind) =>
        target switch
        {
            AbsentTarget => null,
            PresentTarget present => bind(present.Package),
            _ => throw new SetupIoException(SetupCode.JournalInvalid)
        };

    private static InstallationTarget Target(
        PackageEvidence? package, Func<PackageEvidence, PackageEvidence> bind) =>
        package is { } present ? new PresentTarget(bind(present)) : AbsentTarget.Instance;

    private static StoredInterrupted? StoreInterrupted(
        InterruptedOperation? interrupted, Func<PackageEvidence, PackageEvidence> bind) =>
        interrupted is { } old
            ? new(old.OperationId, old.ActiveRunId, old.RevokedRunIds,
                Package(old.Previous, bind), Package(old.Desired, bind),
                old.Packages.Select(bind).ToImmutableArray())
            : null;

    private static InterruptedOperation? RestoreInterrupted(
        StoredInterrupted? interrupted, Func<PackageEvidence, PackageEvidence> bind)
    {
        if (interrupted is not { } old)
            return null;
        ValidateHeader(old.OperationId, old.ActiveRunId, old.RevokedRunIds);
        SetupIo.Require(!old.Packages.IsDefault, SetupCode.JournalInvalid);
        return new(old.OperationId, old.ActiveRunId, old.RevokedRunIds,
            Target(old.Previous, bind), Target(old.Desired, bind),
            old.Packages.Select(bind).ToImmutableArray());
    }
}

internal sealed class FileOperationJournal(
    SetupDisk disk, UserInstallationScope scope) : IOperationJournal, ISetupJournalReader
{
    private string PathName => Path.Combine(scope.ServicingDirectory, "operation.json");

    public Task<Effect<Unit>> SaveAsync(
        OperationJournal journal, CancellationToken cancellationToken) =>
        SetupIo.RunAsync(() => Save(journal), SetupCode.JournalPublicationFailed,
            cancellationToken);

    private Unit Save(OperationJournal journal)
    {
        SetupIo.Require(SetupIdentity.SamePath(
            journal.Plan.Scope.AppDirectory, scope.AppDirectory), SetupCode.RootMismatch);
        var document = JournalDocuments.Encode(journal, scope);
        disk.WriteAtomic(PathName, JsonSerializer.SerializeToUtf8Bytes(
            document, SetupStorageJson.Default.JournalDocument));
        return default;
    }

    internal Task<Effect<OperationJournal?>> LoadAsync(CancellationToken cancellationToken) =>
        SetupIo.RunAsync(Read, SetupCode.JournalInvalid, cancellationToken);

    public OperationJournal? Read()
    {
        if (disk.Files.Entry(PathName).Kind == DiskEntryKind.Missing)
            return null;
        var bytes = disk.ReadBytes(PathName, 4 * 1024 * 1024);
        var document = StrictSetupJson.Read(bytes, SetupStorageJson.Default.JournalDocument);
        return JournalDocuments.Decode(document, scope);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(JournalDocument))]
[JsonSerializable(typeof(PayloadManifest))]
internal partial class SetupStorageJson : JsonSerializerContext;
