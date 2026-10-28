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
    InventoryChanged = 9,
    CanonicalRootInvalid = 10,
    ImageIsDirectory = 11,
    ImageLinkCountNotOne = 12,
    AncestorDepthExceeded = 13,
    DirectoryIsNotDirectory = 14,
    FinalPathPrefixUnsupported = 15,
    ReadyBindingMissing = 16,
    ReadyInventoryInvalid = 17,
    ReadyBodyTooLarge = 18,
    CandidateMediaRead = 19,
    CandidateAuthoringRead = 20,
    CandidateValidatorInventory = 21,
    CandidateValidatorRemainder = 22,
    CandidateResources = 23,
    CandidateValidatorManifest = 24,
    CandidateValidatorPayload = 25,
    CandidateValidatorProperties = 26,
    CandidateValidatorSearch = 27,
    CandidateValidatorUpgrade = 28,
    CandidateValidatorActions = 29,
    CandidateValidatorSequences = 30,
    CandidateValidatorDirectories = 31,
    CandidateValidatorRegistry = 32,
    CandidateValidatorComponents = 33,
    CandidateValidatorFeature = 34,
    CandidateValidatorGuard = 35,
    CandidateValidatorShortcut = 36,
    CandidateValidatorRemovals = 37,
    CandidatePropertyCount = 38,
    CandidatePropertyName = 39,
    CandidatePropertySecureSet = 40,
    CandidatePropertyValue = 41,
    CandidateGuardInspection = 42,
    CandidateGuardApprovedHash = 43,
    CandidateGuardPeReader = 44,
    CandidateGuardPeHeaders = 45,
    CandidateGuardMachine = 46,
    CandidateGuardDll = 47,
    CandidateGuardExecutable = 48,
    CandidateGuardNoClrHeader = 49,
    CandidateGuardOptionalHeader = 50,
    CandidateGuardPe32Plus = 51,
    CandidateGuardNoClrDirectory = 52,
    CandidateGuardExportRva = 53,
    CandidateGuardExportSize = 54,
    CandidateGuardFunctionCount = 55,
    CandidateGuardNameCount = 56,
    CandidateGuardFunctionTableRva = 57,
    CandidateGuardNameTableRva = 58,
    CandidateGuardOrdinalTableRva = 59,
    CandidateGuardFunctionAddresses = 60,
    CandidateGuardDistinctFunctions = 61,
    CandidateGuardPositiveTarget = 62,
    CandidateGuardNonForwardedTarget = 63,
    CandidateGuardExecutableTarget = 64,
    CandidateGuardOrdinalIndexes = 65,
    CandidateGuardOrdinalPermutation = 66,
    CandidateGuardNameRvas = 67,
    CandidateGuardNameData = 68,
    CandidateGuardNameTerminator = 69,
    CandidateGuardNameAscii = 70,
    CandidateGuardExportNames = 71,
    CandidateGuardFunctionCountRead = 72,
    CandidateGuardNameCountRead = 73
}

internal sealed record SetupRefusalEvidence(
    SetupEvidenceStage Stage,
    SetupEvidenceCause Cause,
    ObservationFault Fault = ObservationFault.None,
    StartupEvidenceErrorKind ErrorKind = StartupEvidenceErrorKind.None,
    long ErrorCode = 0)
{
    internal const int MaximumDiagnosticLength = 192;

    internal bool IsCandidateMedia =>
        Stage == SetupEvidenceStage.CandidatePackage &&
        (Cause is SetupEvidenceCause.CandidateMediaRead
            or SetupEvidenceCause.CandidateAuthoringRead
            or SetupEvidenceCause.CandidateValidatorInventory
            or SetupEvidenceCause.CandidateValidatorRemainder
            or SetupEvidenceCause.CandidateResources
            or SetupEvidenceCause.CandidateValidatorManifest
            or SetupEvidenceCause.CandidateValidatorPayload
            or SetupEvidenceCause.CandidateValidatorProperties
            or SetupEvidenceCause.CandidateValidatorSearch
            or SetupEvidenceCause.CandidateValidatorUpgrade
            or SetupEvidenceCause.CandidateValidatorActions
            or SetupEvidenceCause.CandidateValidatorSequences
            or SetupEvidenceCause.CandidateValidatorDirectories
            or SetupEvidenceCause.CandidateValidatorRegistry
            or SetupEvidenceCause.CandidateValidatorComponents
            or SetupEvidenceCause.CandidateValidatorFeature
            or SetupEvidenceCause.CandidateValidatorGuard
            or SetupEvidenceCause.CandidateValidatorShortcut
            or SetupEvidenceCause.CandidateValidatorRemovals
            or SetupEvidenceCause.CandidatePropertyCount
            or SetupEvidenceCause.CandidatePropertyName
            or SetupEvidenceCause.CandidatePropertySecureSet
            or SetupEvidenceCause.CandidatePropertyValue
            or SetupEvidenceCause.CandidateGuardInspection
            or SetupEvidenceCause.CandidateGuardApprovedHash
            or SetupEvidenceCause.CandidateGuardPeReader
            or SetupEvidenceCause.CandidateGuardPeHeaders
            or SetupEvidenceCause.CandidateGuardMachine
            or SetupEvidenceCause.CandidateGuardDll
            or SetupEvidenceCause.CandidateGuardExecutable
            or SetupEvidenceCause.CandidateGuardNoClrHeader
            or SetupEvidenceCause.CandidateGuardOptionalHeader
            or SetupEvidenceCause.CandidateGuardPe32Plus
            or SetupEvidenceCause.CandidateGuardNoClrDirectory
            or SetupEvidenceCause.CandidateGuardExportRva
            or SetupEvidenceCause.CandidateGuardExportSize
            or SetupEvidenceCause.CandidateGuardFunctionCount
            or SetupEvidenceCause.CandidateGuardNameCount
            or SetupEvidenceCause.CandidateGuardFunctionTableRva
            or SetupEvidenceCause.CandidateGuardNameTableRva
            or SetupEvidenceCause.CandidateGuardOrdinalTableRva
            or SetupEvidenceCause.CandidateGuardFunctionAddresses
            or SetupEvidenceCause.CandidateGuardDistinctFunctions
            or SetupEvidenceCause.CandidateGuardPositiveTarget
            or SetupEvidenceCause.CandidateGuardNonForwardedTarget
            or SetupEvidenceCause.CandidateGuardExecutableTarget
            or SetupEvidenceCause.CandidateGuardOrdinalIndexes
            or SetupEvidenceCause.CandidateGuardOrdinalPermutation
            or SetupEvidenceCause.CandidateGuardNameRvas
            or SetupEvidenceCause.CandidateGuardNameData
            or SetupEvidenceCause.CandidateGuardNameTerminator
            or SetupEvidenceCause.CandidateGuardNameAscii
            or SetupEvidenceCause.CandidateGuardExportNames
            or SetupEvidenceCause.CandidateGuardFunctionCountRead
            or SetupEvidenceCause.CandidateGuardNameCountRead) &&
        Fault == ObservationFault.None &&
        ErrorKind == StartupEvidenceErrorKind.None && ErrorCode == 0;

    internal string ToDiagnosticLine() => string.Create(
        CultureInfo.InvariantCulture,
        $"HERMES_SETUP_EVIDENCE v=1 stage={Stage:d} cause={Cause:d} fault={Fault:d} errorKind={ErrorKind:d} error={ErrorCode}");

    internal static SetupRefusalEvidence FromAdmission(
        StartupRefusalEvidence? nativeEvidence, int hResult, AdmissionInvariant invariant)
    {
        var cause = InvariantCause(invariant);
        return nativeEvidence is null && cause != SetupEvidenceCause.ExpectedException
            ? new(SetupEvidenceStage.Entry, cause)
            : new(SetupEvidenceStage.Entry, SetupEvidenceCause.ExpectedException,
                ErrorKind: nativeEvidence?.ErrorKind ?? StartupEvidenceErrorKind.HResult,
                ErrorCode: nativeEvidence?.ErrorCode ?? hResult);
    }

    private static SetupEvidenceCause InvariantCause(AdmissionInvariant invariant) => invariant switch
    {
        AdmissionInvariant.CanonicalRootInvalid => SetupEvidenceCause.CanonicalRootInvalid,
        AdmissionInvariant.ImageIsDirectory => SetupEvidenceCause.ImageIsDirectory,
        AdmissionInvariant.ImageLinkCountNotOne => SetupEvidenceCause.ImageLinkCountNotOne,
        AdmissionInvariant.AncestorDepthExceeded => SetupEvidenceCause.AncestorDepthExceeded,
        AdmissionInvariant.DirectoryIsNotDirectory => SetupEvidenceCause.DirectoryIsNotDirectory,
        AdmissionInvariant.FinalPathPrefixUnsupported => SetupEvidenceCause.FinalPathPrefixUnsupported,
        AdmissionInvariant.ReadyBindingMissing => SetupEvidenceCause.ReadyBindingMissing,
        AdmissionInvariant.ReadyInventoryInvalid => SetupEvidenceCause.ReadyInventoryInvalid,
        AdmissionInvariant.ReadyBodyTooLarge => SetupEvidenceCause.ReadyBodyTooLarge,
        _ => SetupEvidenceCause.ExpectedException
    };
}

// Coarse candidate acceptance boundaries only. Expected exceptions still reach
// their original catches; an existing typed refusal and its evidence take priority.
internal static class CandidateMediaEvidence
{
    internal static SetupRefusalEvidence At(SetupEvidenceCause cause) =>
        new(SetupEvidenceStage.Unspecified, cause);

    internal static T Read<T>(SetupEvidenceCause cause, Func<T> read)
    {
        try { return read(); }
        catch (SetupIoException error) when
            (error is { Code: SetupCode.MediaInvalid, Evidence: null })
        {
            error.IdentifyCandidateMedia(cause);
            throw;
        }
    }
}

// Per-validation diagnostic state only. These calls neither catch exceptions
// nor change validation policy; the validator's original catches remain authoritative.
internal sealed class CandidateValidatorSourcePoint
{
    private SetupEvidenceCause _cause = SetupEvidenceCause.CandidateValidatorInventory;

    internal SetupEvidenceCause Cause => _cause;

    internal T Read<T>(SetupEvidenceCause cause, Func<T> read)
    {
        _cause = cause;
        return read();
    }

    internal void Check(SetupEvidenceCause cause, Action check)
    {
        _cause = cause;
        check();
    }
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
