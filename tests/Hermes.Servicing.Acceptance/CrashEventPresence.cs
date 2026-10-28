namespace Hermes.Servicing.Acceptance;

// These classifications describe this owned diagnostic only, not acceptance.
internal enum CrashPresenceOutcome
{
    NotObserved, Empty, SameIdentity, DifferentBirth, DifferentProcess,
    DifferentImage, Ambiguous, ReaderFailure, QueryFailure, Incomplete
}

internal static class CrashEventPresence
{
    internal static CrashPresenceOutcome Classify(CrashQueryResult result) => result switch
    {
        CrashQueryEmpty => CrashPresenceOutcome.Empty,
        CrashQueryAmbiguous => CrashPresenceOutcome.Ambiguous,
        CrashQueryFailure => CrashPresenceOutcome.QueryFailure,
        CrashQueryPresence { Read: CrashIdentityFailure } => CrashPresenceOutcome.ReaderFailure,
        CrashQueryPresence { Read: CrashIdentityCompared compared } => compared.Comparison switch
        {
            CrashIdentityComparison.Same => CrashPresenceOutcome.SameIdentity,
            CrashIdentityComparison.BirthDifferent => CrashPresenceOutcome.DifferentBirth,
            CrashIdentityComparison.ProcessDifferent => CrashPresenceOutcome.DifferentProcess,
            CrashIdentityComparison.ImageDifferent => CrashPresenceOutcome.DifferentImage,
            _ => CrashPresenceOutcome.Incomplete
        },
        _ => CrashPresenceOutcome.Incomplete
    };

    internal static bool Continue(CrashPresenceOutcome outcome) => outcome switch
    {
        CrashPresenceOutcome.Empty => false,
        CrashPresenceOutcome.SameIdentity or CrashPresenceOutcome.DifferentBirth => true,
        _ => throw new CrashCorrelationException(Reason(outcome))
    };

    private static CrashCorrelationReason Reason(CrashPresenceOutcome outcome) => outcome switch
    {
        CrashPresenceOutcome.DifferentProcess or CrashPresenceOutcome.DifferentImage =>
            CrashCorrelationReason.IdentityMismatch,
        CrashPresenceOutcome.Ambiguous => CrashCorrelationReason.Ambiguous,
        CrashPresenceOutcome.ReaderFailure => CrashCorrelationReason.Reader,
        CrashPresenceOutcome.QueryFailure => CrashCorrelationReason.Query,
        _ => CrashCorrelationReason.EnumerationIncomplete
    };
}

internal static class CrashEventPresenceReport
{
    internal const int MaximumSummaryBytes = 206;
    internal const int MaximumOutputBytes =
        MaximumSummaryBytes + CrashEventTelemetry.MaximumSummaryBytes;

    internal static string Summary(
        CrashPresenceOutcome outcome, CrashEventControlResult result)
    {
        Validate(outcome, result);
        return FormattableString.Invariant(
            $"HERMES_CRASH_EVENT_PRESENCE v=1 status=1 outcome={(int)outcome} reason={(int)result.Reason} controlStatus={result.Control.ExitCode} controlError={(int)result.Control.Error} cleanupError={(int)result.Control.CleanupError} receiptClosed={(result.ReceiptClosed ? 1 : 0)} polls={result.Proof.Polls} queries={result.Proof.Queries} originalGatePassed=0 precisionAccepted=0 nativeAcceptance=0\n");
    }

    private static void Validate(
        CrashPresenceOutcome outcome, CrashEventControlResult result) =>
        CrashCorrelationErrors.Require(
            Enum.IsDefined(outcome) && Enum.IsDefined(result.Reason) &&
            Enum.IsDefined(result.Control.Error) &&
            (int)result.Control.CleanupError is >= 0 and <= 255 &&
            result.Proof is
            {
                Matched: false, LowerEmpty: false, UpperEmpty: false,
                Polls: >= 0 and <= CrashEventObserver.MaximumPolls,
                Queries: >= 0 and <= CrashEventObserver.MaximumPolls
            } &&
            result.ReportError == 0 && result.Control.ReportError == 0,
            CrashCorrelationReason.Publication);
}
