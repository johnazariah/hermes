using System.ComponentModel;
using System.Globalization;
using Hermes.Servicing;

namespace Hermes.Setup;

internal enum SetupEvidenceStage
{
    Unspecified = 0,
    Entry = 1,
    Composition = 2,
    Admission = 3,
    Gate = 4,
    Placement = 5,
    Journal = 6,
    Recovery = 7,
    InitialInventory = 8,
    CurrentPackage = 9,
    CandidatePackage = 10,
    RecoveryPackages = 11,
    FinalInventory = 12,
    Ownership = 13,
    Collisions = 14,
    Planning = 15,
    ClosedProcessing = 16
}

internal enum SetupEvidenceCause
{
    Rejected = 1,
    ExpectedException = 2,
    MsiObservation = 3,
    ShortcutObservation = 4,
    PendingUnknown = 5,
    PendingAlias = 6,
    RegistryUnsupported = 7,
    ComponentClientsIncomplete = 8,
    InventoryChanged = 9
}

internal sealed record SetupRefusalEvidence(
    SetupEvidenceStage Stage,
    SetupEvidenceCause Cause,
    ObservationFault Fault = ObservationFault.None,
    StartupEvidenceErrorKind ErrorKind = StartupEvidenceErrorKind.None,
    long ErrorCode = 0)
{
    internal const int MaximumDiagnosticLength = 192;

    internal string ToDiagnosticLine() => string.Create(
        CultureInfo.InvariantCulture,
        $"HERMES_SETUP_EVIDENCE v=1 stage={Stage:d} cause={Cause:d} fault={Fault:d} errorKind={ErrorKind:d} error={ErrorCode}");
}

// Only the existing read-only inspection boundary uses these wrappers.
// Its expected-exception fallback remains InvalidEvidence.
internal static class InspectionEvidence
{
    internal static T Read<T>(SetupEvidenceStage stage, Func<T> read)
    {
        try { return read(); }
        catch (SetupIoException error)
        {
            var evidence = error.Evidence is { } existing
                ? existing with { Stage = stage }
                : new SetupRefusalEvidence(stage, SetupEvidenceCause.Rejected);
            throw new SetupIoException(error.Code, evidence);
        }
        catch (Exception error) when (AggregateResult.Expected(error))
        {
            var (kind, code) = error switch
            {
                Win32Exception native => (StartupEvidenceErrorKind.Win32, native.NativeErrorCode),
                _ => (StartupEvidenceErrorKind.HResult, error.HResult)
            };
            throw new SetupIoException(SetupCode.InvalidEvidence,
                new(stage, SetupEvidenceCause.ExpectedException, ErrorKind: kind, ErrorCode: code));
        }
    }

    internal static void Check(SetupEvidenceStage stage, Action check)
    {
        Read(stage, () =>
        {
            check();
            return default(Unit);
        });
    }
}
