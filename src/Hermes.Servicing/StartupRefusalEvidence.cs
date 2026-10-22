using System.Globalization;

namespace Hermes.Servicing;

public enum StartupEvidenceStage
{
    Composition = 1,
    ReservedRoot = 2,
    LoadedImage = 3,
    Gate = 4
}

public enum StartupEvidenceOperation
{
    Compose = 1,
    KnownFolder = 2,
    OpenFollowing = 3,
    OpenEntry = 4,
    StandardInformation = 5,
    FinalPath = 6,
    OpenGate = 7
}

public enum StartupEvidenceFailure
{
    EmptyKnownFolder = 1,
    NativeCallFailed = 2,
    ExistingEntry = 3,
    DeletePending = 4,
    InvalidReturnedLength = 5,
    NoParent = 6,
    ManagedException = 7
}

public enum StartupEvidenceErrorKind
{
    None = 0,
    Win32 = 1,
    HResult = 2
}

public sealed record StartupRefusalEvidence(
    StartupEvidenceStage Stage,
    StartupEvidenceOperation Operation,
    StartupEvidenceFailure Failure,
    StartupEvidenceErrorKind ErrorKind = StartupEvidenceErrorKind.None,
    int ErrorCode = 0,
    int Position = -1)
{
    public const int MaximumDiagnosticLength = 192;

    public string ToDiagnosticLine() => string.Create(
        CultureInfo.InvariantCulture,
        $"HERMES_STARTUP_EVIDENCE v=1 stage={(int)Stage} operation={(int)Operation} failure={(int)Failure} errorKind={(int)ErrorKind} error={ErrorCode} position={Position}");
}

/// <summary>
/// Single-attempt capture. Allocate before composition and inspect after refusal.
/// Contains no handle, release capability, path, or caller callback.
/// </summary>
public sealed class StartupRefusalEvidenceCapture
{
    private StartupRefusalEvidence? _evidence;

    public StartupRefusalEvidence? Evidence => _evidence;

    internal void Record(StartupRefusalEvidence evidence) => _evidence ??= evidence;
}

internal static class RuntimeGateFailures
{
    internal static GateUnavailable From(
        Exception error, StartupRefusalEvidenceCapture? evidence)
    {
        var reason = (error, error.HResult & 0xffff) switch
        {
            (IOException, 32 or 33) => RefusalReason.Busy,
            (IOException, 2 or 3) => RefusalReason.UnownedInstallation,
            _ => RefusalReason.Unavailable
        };
        if (reason == RefusalReason.Unavailable)
            evidence?.Record(new(
                StartupEvidenceStage.Gate, StartupEvidenceOperation.OpenGate,
                StartupEvidenceFailure.ManagedException,
                StartupEvidenceErrorKind.HResult, error.HResult));
        return new(reason);
    }
}
