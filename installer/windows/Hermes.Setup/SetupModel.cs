using System.Collections.Immutable;
using Hermes.Servicing;
using Hermes.Installer.Control;

namespace Hermes.Setup;

internal enum SetupCode
{
    None,
    InvalidArguments,
    InvalidEvidence,
    Busy,
    Unavailable,
    Cancelled,
    OwnershipRefused,
    IncompatibleInstallation,
    RootMismatch,
    Collision,
    MediaInvalid,
    MediaIdentityMismatch,
    RecoveryMediaUnavailable,
    RollbackNotAllowed,
    UnsupportedPackageChange,
    NotInstalled,
    RecoveryRequired,
    NoRecoveryPending,
    JournalInvalid,
    ClosedPublicationFailed,
    JournalPublicationFailed,
    MsiPreparationFailed,
    MsiCallFailed,
    MsiDidNotComplete,
    TransactionFenceMissing,
    VerificationFailed,
    RebootRequired,
    ReadyPublicationFailed
}

internal enum GateDisposition { Missing, Ready, Closed }
internal enum SetupAdmissionState { Unknown, Closed, Ready }
internal enum PackageScope { PerUser, Machine }

internal abstract record Effect<T>
{
    internal sealed record Success(T Value) : Effect<T>;
    internal sealed record Failure(SetupCode Code) : Effect<T>;

    internal SetupRefusalEvidence? Evidence { get; init; }

    internal static Effect<T> Ok(T value) => new Success(value);
    internal static Effect<T> Fail(
        SetupCode code, SetupRefusalEvidence? evidence = null) =>
        new Failure(code) { Evidence = evidence };

    internal SetupCode FailureCode =>
        this is Failure(var code) && code != SetupCode.None
            ? code : SetupCode.InvalidEvidence;
}

internal readonly record struct Unit;

// Only the trusted known-folder interpreter constructs this value.
// It is not an installation-location option.
internal sealed record UserInstallationScope
{
    internal string AppDirectory { get; }
    internal string ServicingDirectory { get; }
    internal string PackagesDirectory { get; }
    internal string GatePath { get; }

    private UserInstallationScope(string appDirectory, string servicingDirectory)
    {
        AppDirectory = appDirectory;
        ServicingDirectory = servicingDirectory;
        PackagesDirectory = Path.Combine(servicingDirectory, "packages");
        GatePath = Path.Combine(servicingDirectory, "admission.bin");
    }

    internal static UserInstallationScope FromKnownFolder(string knownFolder)
    {
        if (string.IsNullOrWhiteSpace(knownFolder) ||
            !Path.IsPathFullyQualified(knownFolder))
            throw new ArgumentException("A resolved known folder is required.", nameof(knownFolder));
        return new(
            Path.Combine(knownFolder, "Hermes", "App"),
            Path.Combine(knownFolder, "Hermes", "Servicing",
                HermesServicingProtocol.UpgradeCode.ToString("D")));
    }
}

internal sealed record PackageIdentity(
    Guid UpgradeCode,
    Guid ProductCode,
    Guid PackageCode,
    Version Version,
    int AdmissionAbi,
    PackageScope Scope);

// This is evidence produced by inspection, not an unchecked manifest DTO.
// ManifestSha256 identifies the complete validated owned-payload inventory.
internal sealed record PackageEvidence(
    PackageIdentity Identity,
    string AppDirectory,
    string SourcePath,
    string ManifestSourcePath,
    string PackageSha256,
    string ManifestSha256,
    ImmutableArray<ManagedImage> Images);

internal sealed record RetainedMedia(
    PackageEvidence Evidence,
    string PackagePath,
    string ManifestPath);

internal abstract record InstallationTarget;

internal sealed record AbsentTarget : InstallationTarget
{
    internal static AbsentTarget Instance { get; } = new();
}

internal sealed record PresentTarget(PackageEvidence Package) : InstallationTarget;

internal sealed record InterruptedOperation(
    Guid OperationId,
    Guid ActiveRunId,
    ImmutableArray<Guid> RevokedRunIds,
    InstallationTarget Previous,
    InstallationTarget Desired,
    ImmutableArray<PackageEvidence> Packages);

internal sealed record Inspection(
    UserInstallationScope Scope,
    GateDisposition Gate,
    InstallationTarget Observed,
    PackageEvidence? Candidate,
    InterruptedOperation? Interrupted);

internal sealed record EngineStep(
    MaintenanceKind Operation,
    PackageEvidence Package,
    ImmutableArray<Guid> NestedRemovalProducts,
    bool AllowRollback,
    bool Quiet)
{
    internal InstallationTarget Expected =>
        Operation == MaintenanceKind.Uninstall
            ? AbsentTarget.Instance : new PresentTarget(Package);
}

internal sealed record OperationPlan(
    SetupVerb Verb,
    UserInstallationScope Scope,
    InstallationTarget Previous,
    InstallationTarget Desired,
    ImmutableArray<EngineStep> Steps,
    InterruptedOperation? Interrupted)
{
    internal bool IsRecovery => Verb == SetupVerb.Recover;

    internal ImmutableArray<PackageEvidence> Packages =>
        TargetPackages(Previous).Concat(TargetPackages(Desired))
            .Concat(Steps.Select(step => step.Package))
            .Concat(Interrupted?.Packages ?? [])
            .DistinctBy(package => package.PackageSha256, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

    private static IEnumerable<PackageEvidence> TargetPackages(InstallationTarget target) =>
        target switch
        {
            PresentTarget present => [present.Package],
            _ => []
        };
}

internal sealed record EngineRequest(
    UserInstallationScope Scope,
    EngineStep Step,
    RetainedMedia Media);

internal sealed record EngineReceipt(
    Guid RunId,
    bool ApiEntered,
    bool ApiReturned,
    uint? MsiExitCode,
    bool ControlFaulted,
    bool OwnPreflightOccurred,
    bool OwnTransactionFenceOccurred)
{
    // Ephemeral observation, never a completion-policy or journal input.
    internal MsiFailureEvidence? MsiFailure { get; init; }
    internal NativePreflightEvidence? NativePreflight { get; init; }

    internal bool CompletedApi =>
        ApiEntered && ApiReturned && MsiExitCode is not null && !ControlFaulted;

    internal int? NativeCode =>
        MsiExitCode is { } code && code <= int.MaxValue ? (int)code : null;
}

internal sealed record MsiFailureEvidence(
    int Action,
    int ErrorId,
    bool RecordReadFailed,
    bool OwnPreflight,
    bool OwnTransaction)
{
    // Ephemeral caller observation, not serialized into the public report/journal.
    internal MsiCallContextObservation? CallContext { get; init; }

    internal string ToDiagnosticLine()
    {
        var version = CallContext is null ? 1 : 2;
        var line = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"HERMES_SETUP_MSI v={version} action={Action} error={ErrorId} readFailed={(RecordReadFailed ? 1 : 0)} preflight={(OwnPreflight ? 1 : 0)} fence={(OwnTransaction ? 1 : 0)}");
        return CallContext is { } context ? line + context.ToDiagnosticSuffix() : line;
    }
}

// Source 1 is UNTRUSTED active-MSI-callback data, not native writer provenance.
// Internal properties keep this out of the public report and persisted journal.
internal sealed record NativePreflightEvidence(int Point, int Reason)
{
    internal static NativePreflightEvidence FromValues(int point, int reason)
    {
        var value = new NativePreflightEvidence(point, reason);
        value.RequireValid();
        return value;
    }

    internal string ToDiagnosticLine()
    {
        RequireValid();
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"HERMES_NATIVE_PREFLIGHT v=1 source=1 observed={(Point == 0 ? 0 : 1)} point={Point} reason={Reason}");
    }

    private void RequireValid()
    {
        var valid = (Point, Reason) switch
        {
            (0, 0) => true,
            (>= 1 and <= 9, 0 or 1 or 4) => true,
            (>= 2 and <= 9, 2) => true,
            (2, >= 10 and <= 22) => true,
            _ => false
        };
        if (!valid)
            throw new ArgumentOutOfRangeException(nameof(Point));
    }
}

internal sealed record OperationJournal(
    Guid OperationId,
    Guid ActiveRunId,
    ImmutableArray<Guid> RevokedRunIds,
    OperationPlan Plan,
    ImmutableArray<RetainedMedia> Media,
    ImmutableArray<EngineReceipt> Receipts)
{
    internal bool CanStart(Guid run) =>
        run != Guid.Empty && run != ActiveRunId &&
        !RevokedRunIds.Contains(run) &&
        !Receipts.Any(receipt => receipt.RunId == run);

    internal OperationJournal ForRun(Guid run) => this with
    {
        ActiveRunId = run,
        RevokedRunIds = ActiveRunId == Guid.Empty
            ? RevokedRunIds
            : RevokedRunIds.Add(ActiveRunId).Distinct().ToImmutableArray()
    };

    internal InterruptedOperation AsInterrupted() => new(
        OperationId, ActiveRunId, RevokedRunIds,
        Plan.Previous, Plan.Desired, Plan.Packages);
}

// A non-null ProvenTarget means the verifier established the entire safe
// installation/absence contract, not merely byte equality or registration.
internal sealed record VerificationEvidence(
    InstallationTarget? ProvenTarget,
    bool RelevantPendingMutations);

internal abstract record CompletionDecision;

internal sealed record LeaveClosed(
    SetupCode Code,
    SetupExitCode ExitCode = SetupExitCode.ServicingFailed,
    SetupOutcome Outcome = SetupOutcome.RecoveryRequired) : CompletionDecision;

internal sealed record PublishInstallation(
    InstallationTarget Target,
    SetupOutcome Outcome,
    SetupExitCode ExitCode,
    SetupCode Code) : CompletionDecision;

internal sealed record SetupExecution(
    SetupReport Report,
    SetupExitCode ExitCode,
    SetupCode Code)
{
    // Report.Ready is a positive completion claim, not proof of admission denial.
    internal SetupAdmissionState Admission { get; init; } = SetupAdmissionState.Unknown;
    internal SetupRefusalEvidence? Evidence { get; init; }
    internal MsiFailureEvidence? MsiFailure { get; init; }
    internal NativePreflightEvidence? NativePreflight { get; init; }

    internal static SetupExecution Refused(
        SetupVerb verb, SetupCode code, SetupRefusalEvidence? evidence = null) => new(
            new(verb, SetupOutcome.Refused, false, null),
            code == SetupCode.Busy ? SetupExitCode.Busy : SetupExitCode.ServicingFailed,
            code) { Evidence = evidence };

    internal static SetupExecution Unknown(
        SetupVerb verb, SetupCode code, EngineReceipt? receipt = null) => new(
            new(verb, SetupOutcome.RecoveryRequired, false, receipt?.NativeCode),
            SetupExitCode.ServicingFailed, code);

    internal static SetupExecution Closed(
        SetupVerb verb, LeaveClosed decision, EngineReceipt? receipt = null) => new(
            new(verb, decision.Outcome, false, receipt?.NativeCode),
            decision.ExitCode, decision.Code)
        {
            Admission = SetupAdmissionState.Closed,
            MsiFailure = receipt?.MsiFailure,
            NativePreflight = receipt?.NativePreflight
        };

    internal static SetupExecution Published(
        SetupVerb verb, PublishInstallation decision, EngineReceipt receipt) => new(
            new(verb, decision.Outcome, true, receipt.NativeCode),
            decision.ExitCode, decision.Code) { Admission = SetupAdmissionState.Ready };
}

internal static class SetupIdentity
{
    internal static bool SameContent(PackageEvidence left, PackageEvidence right) =>
        left.Identity == right.Identity &&
        SamePath(left.AppDirectory, right.AppDirectory) &&
        string.Equals(left.PackageSha256, right.PackageSha256, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.ManifestSha256, right.ManifestSha256, StringComparison.OrdinalIgnoreCase) &&
        !left.Images.IsDefault && !right.Images.IsDefault &&
        left.Images.SequenceEqual(right.Images);

    internal static bool SameTarget(InstallationTarget? left, InstallationTarget right) =>
        (left, right) switch
        {
            (AbsentTarget, AbsentTarget) => true,
            (PresentTarget a, PresentTarget b) => SameContent(a.Package, b.Package),
            _ => false
        };

    // Defense-in-depth comparison after the inspector has established physical,
    // component-aware identity and rejected aliases/redirection ambiguity.
    internal static bool SamePath(string left, string right) =>
        string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    internal static GateReadyState ReadyState(
        UserInstallationScope scope, InstallationTarget target) => target switch
        {
            AbsentTarget => new(null),
            PresentTarget present => new(new(
                new(HermesServicingProtocol.UpgradeCode, scope.AppDirectory,
                    HermesServicingProtocol.AdmissionAbi),
                present.Package.Identity.ProductCode, present.Package.Images)),
            _ => throw new InvalidOperationException("Unsupported installation target.")
        };
}
