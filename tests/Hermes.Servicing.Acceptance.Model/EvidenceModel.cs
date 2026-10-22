using System.Collections.Immutable;

namespace Hermes.Servicing.Acceptance.Model;

public sealed class EvidenceGap(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record ProcessKey(uint ProcessId, long CreationFileTime);

public sealed record PackageKey(
    Guid ProductCode,
    Guid PackageCode,
    string WholeMsiSha256,
    string ManifestSha256,
    string ProductName,
    string ProductVersion,
    string ProductLanguage,
    string Manufacturer);

public sealed record DiagnosticValue(
    int SchemaVersion,
    string Kind,
    Guid Invocation,
    Guid RunId,
    ProcessKey Coordinator,
    ProcessKey Writer,
    Guid ProductCode,
    Guid? UpgradingProductCode,
    string? Phase,
    string? Operation,
    int? Status,
    string? Action,
    int Failures);

public sealed record ProductOutcome(
    int EventId,
    Guid ProductCode,
    string ProductName,
    string ProductVersion,
    string ProductLanguage,
    string Manufacturer,
    uint Status);

public enum FactKind { Begin, End, Diagnostic, Outcome }

public sealed record LogFact(
    long Cursor,
    FactKind Kind,
    ProcessKey? Client,
    PackageKey? Package,
    string? PackagePath,
    DiagnosticValue? Diagnostic,
    ProductOutcome? Outcome,
    NativeWriterBinding? NativeWriter = null);

public sealed record ObservationSnapshot(
    long ReadyCursor,
    ImmutableArray<LogFact> Facts,
    ImmutableArray<string> Faults);

public enum OldBoundary { BeforePreflight, BeforeTransaction, AfterParentFence, Returned }

public sealed record ExpectedOld(
    ProcessKey Client,
    Guid RunId,
    PackageKey Package,
    string Operation,
    OldBoundary Boundary,
    ProcessKey GuardHost,
    bool ReturnedReceiptVerified);

public sealed record ExpectedFresh(
    ProcessKey Client,
    Guid RunId,
    PackageKey Package,
    string Operation,
    ProcessKey PreflightHost,
    ProcessKey TransactionHost,
    bool SynchronousReceiptVerified,
    bool FullVerificationReady);

public sealed record OldProof(
    long BeginCursor,
    long DecisionCursor,
    long? AfterFenceActionCursor,
    long OutcomeCursor,
    long EndCursor,
    uint Status);

public sealed record FreshProof(
    Guid RunId,
    long BeginCursor,
    long PreflightCursor,
    long TransactionCursor,
    long InstallEndCursor,
    long OutcomeCursor,
    long EndCursor);

public sealed record BarrierProof(
    OldProof Old,
    ImmutableArray<FreshProof> Fresh);

public static class BarrierCorrelation
{
    public static OldProof Old(ObservationSnapshot snapshot, ExpectedOld expected)
    {
        Integrity(snapshot);
        var phase = expected.Boundary == OldBoundary.BeforePreflight ? "Preflight" : "Transaction";
        var status = expected.Boundary is OldBoundary.BeforePreflight or OldBoundary.BeforeTransaction
            ? 1603 : 0;
        var decision = Decision(snapshot, expected.Client, expected.RunId,
            expected.Package, phase, expected.GuardHost, status);
        var session = Session(snapshot, expected.Client, expected.Package,
            expected.Operation, decision.Cursor);
        CheckOldBoundary(snapshot, expected, decision, session);
        var action = AfterFence(snapshot, expected, decision.Cursor, session.End.Cursor);
        return new(session.Begin.Cursor, decision.Cursor, action,
            session.Outcome.Cursor, session.End.Cursor,
            Value(session.Outcome.Outcome).Status);
    }

    public static BarrierProof Complete(ObservationSnapshot snapshot,
        ExpectedOld old, IReadOnlyList<ExpectedFresh> fresh)
    {
        var oldProof = Old(snapshot, old);
        Need(fresh.Count > 0, "MissingFreshCall");
        var proofs = fresh.Select(call => Fresh(snapshot, call, oldProof.EndCursor))
            .ToImmutableArray();
        Need(proofs.Select(value => value.RunId).Distinct().Count() == proofs.Length,
            "FreshRunReplay");
        Need(!snapshot.Facts.Any(fact => fact.Cursor > oldProof.EndCursor &&
            fact.Diagnostic is { Kind: "GuardEnter" or "GuardDecision" } diagnostic &&
            diagnostic.RunId == old.RunId), "OldReentryAfterTerminal");
        return new(oldProof, proofs);
    }

    private static FreshProof Fresh(
        ObservationSnapshot snapshot, ExpectedFresh expected, long oldEnd)
    {
        Need(expected.SynchronousReceiptVerified, "MissingOwnFreshReceipt");
        Need(expected.FullVerificationReady, "MissingFullVerificationReady");
        var preflight = Decision(snapshot, expected.Client, expected.RunId,
            expected.Package, "Preflight", expected.PreflightHost, 0);
        var transaction = Decision(snapshot, expected.Client, expected.RunId,
            expected.Package, "Transaction", expected.TransactionHost, 0);
        Need(oldEnd < preflight.Cursor && preflight.Cursor < transaction.Cursor,
            "FreshFenceBeforeOldTerminal");
        var session = Session(snapshot, expected.Client, expected.Package,
            expected.Operation, preflight.Cursor);
        Need(Value(session.Outcome.Outcome).Status == 0, "FreshEngineFailure");
        var end = One(snapshot.Facts.Where(fact => fact.Diagnostic is { } diagnostic &&
            diagnostic.Kind == "InstallEnd" && diagnostic.Coordinator == expected.Client &&
            diagnostic.RunId == expected.RunId && diagnostic.ProductCode == expected.Package.ProductCode &&
            diagnostic.Status == 0), "MissingOrAmbiguousFreshInstallEnd");
        Need(transaction.Cursor < end.Cursor, "InstallEndWithoutFence");
        return new(expected.RunId, session.Begin.Cursor, preflight.Cursor,
            transaction.Cursor, end.Cursor, session.Outcome.Cursor, session.End.Cursor);
    }

    private static LogFact Decision(ObservationSnapshot snapshot, ProcessKey client,
        Guid run, PackageKey package, string phase, ProcessKey host, int status)
    {
        var decision = One(snapshot.Facts.Where(fact => fact.Diagnostic is { } value &&
            value.Kind == "GuardDecision" && value.Coordinator == client &&
            value.RunId == run && value.ProductCode == package.ProductCode &&
            value.UpgradingProductCode is null && value.Phase == phase),
            "MissingOrAmbiguousGuardDecision");
        var value = Value(decision.Diagnostic);
        Need(value.Status == status && value.Writer == host &&
            NativeWriterProof.Require(decision).Process == host,
            "WrongGuardDecisionOrHost");
        Entered(snapshot, decision, client, run, package, phase, host);
        return decision;
    }

    private static void Entered(ObservationSnapshot snapshot, LogFact decision,
        ProcessKey client, Guid run, PackageKey package, string phase, ProcessKey host)
    {
        var value = Value(decision.Diagnostic);
        var entered = One(snapshot.Facts.Where(fact => fact.Diagnostic is { } entry &&
            entry.Kind == "GuardEnter" && entry.RunId == run &&
            entry.Coordinator == client && entry.ProductCode == package.ProductCode &&
            entry.UpgradingProductCode is null && entry.Phase == phase),
            "MissingOrAmbiguousGuardEnter");
        var entry = Value(entered.Diagnostic);
        Need(entry.Invocation == value.Invocation && entry.Writer == host &&
            entry.Operation == value.Operation &&
            NativeWriterProof.Require(entered).Process == host &&
            entered.Cursor < decision.Cursor,
            "UnboundGuardInvocation");
    }

    private sealed record SessionFacts(LogFact Begin, LogFact Outcome, LogFact End);

    private static SessionFacts Session(ObservationSnapshot snapshot, ProcessKey client,
        PackageKey package, string operation, long guardCursor)
    {
        var begin = snapshot.Facts.Where(fact => fact.Kind == FactKind.Begin &&
            fact.Client == client && fact.Package == package && fact.Cursor < guardCursor)
            .MaxBy(fact => fact.Cursor)
            ?? throw new EvidenceGap("MissingBoundTransactionBegin");
        var end = snapshot.Facts.Where(fact => fact.Kind == FactKind.End &&
            fact.Client == client && fact.Package == package &&
            fact.PackagePath == begin.PackagePath && fact.Cursor > guardCursor)
            .MinBy(fact => fact.Cursor)
            ?? throw new EvidenceGap("MissingBoundTransactionEnd");
        var inside = snapshot.Facts.Where(fact =>
            begin.Cursor < fact.Cursor && fact.Cursor < end.Cursor).ToArray();
        Need(!inside.Any(fact => fact.Kind == FactKind.Begin), "CompetingTransactionCandidate");
        var outcome = One(inside.Where(fact =>
            fact.Outcome?.ProductCode == package.ProductCode), "MissingOrAmbiguousOutcome");
        CheckOutcome(Value(outcome.Outcome), package, operation);
        return new(begin, outcome, end);
    }

    private static void CheckOutcome(
        ProductOutcome outcome, PackageKey package, string operation)
    {
        var expectedId = operation switch
        {
            "Install" => 1033,
            "Uninstall" => 1034,
            "Repair" => 1035,
            _ => throw new EvidenceGap("UnsupportedOperation")
        };
        Need(outcome.EventId == expectedId &&
            outcome.ProductName == package.ProductName &&
            outcome.ProductVersion == package.ProductVersion &&
            outcome.ProductLanguage == package.ProductLanguage &&
            outcome.Manufacturer == package.Manufacturer, "WrongOutcomeBinding");
        Need(outcome.Status is 0 or 1602 or 1603, "UnsupportedTerminalOutcome");
    }

    private static void CheckOldBoundary(ObservationSnapshot snapshot,
        ExpectedOld expected, LogFact decision, SessionFacts session)
    {
        Need(session.Begin.Cursor < decision.Cursor &&
            decision.Cursor < session.Outcome.Cursor, "DecisionOutsideTransaction");
        var refused = expected.Boundary is OldBoundary.BeforePreflight or OldBoundary.BeforeTransaction;
        if (refused)
            Need(!snapshot.Facts.Any(fact => fact.Diagnostic is { } value &&
                value.RunId == expected.RunId && value.UpgradingProductCode is null &&
                value.Kind == "GuardDecision" && value.Phase == "Transaction" &&
                value.Status == 0), "OldOwnFenceWasPermitted");
        if (refused)
            Need(Value(session.Outcome.Outcome).Status is 1602 or 1603, "RefusalWithoutFailure");
        if (expected.Boundary == OldBoundary.Returned)
            Need(expected.ReturnedReceiptVerified &&
                Value(session.Outcome.Outcome).Status == 0, "MissingReturnedOldReceipt");
    }

    private static long? AfterFence(ObservationSnapshot snapshot,
        ExpectedOld expected, long decision, long end)
    {
        if (expected.Boundary != OldBoundary.AfterParentFence)
            return null;
        var action = One(snapshot.Facts.Where(fact => fact.Diagnostic is { } value &&
            value.Kind == "ActionStart" && value.Coordinator == expected.Client &&
            value.RunId == expected.RunId && value.ProductCode == expected.Package.ProductCode &&
            value.Action == "RemoveExistingProducts"), "MissingOrAmbiguousAfterFenceAction");
        Need(decision < action.Cursor && action.Cursor < end, "ActionNotAfterReturnedFence");
        return action.Cursor;
    }

    private static void Integrity(ObservationSnapshot snapshot)
    {
        Need(snapshot.ReadyCursor > 0 && snapshot.Faults.IsEmpty, "ObserverUnavailableOrFaulted");
        var cursors = snapshot.Facts.Select(fact => fact.Cursor).ToArray();
        Need(cursors.All(value => value > snapshot.ReadyCursor) &&
            cursors.Distinct().Count() == cursors.Length &&
            cursors.SequenceEqual(cursors.Order()), "CursorReplayOrReordering");
        Need(!snapshot.Facts.Any(fact => fact.Diagnostic is { } value &&
            (value.SchemaVersion != 1 || value.Failures != 0 || value.Kind == "ObserverFault")),
            "SourceObserverFailure");
        WriterIntegrity(snapshot);
        InvocationIntegrity(snapshot);
    }

    private static void WriterIntegrity(ObservationSnapshot snapshot)
    {
        var writers = new NativeWriterTracker();
        foreach (var fact in snapshot.Facts.Where(fact => fact.Diagnostic is not null))
        {
            var value = Value(fact.Diagnostic);
            var binding = NativeWriterProof.Require(fact);
            _ = writers.Observe(fact.Cursor, binding.Reporter, value.Writer,
                binding.Process, binding.ObservedFileTime);
            Need(value.Kind is not ("InstallStart" or "InstallEnd" or "ActionStart") ||
                value.Writer == value.Coordinator, "WrongMsiCallbackWriter");
        }
    }

    private static void InvocationIntegrity(ObservationSnapshot snapshot)
    {
        Check("GuardEnter");
        Check("GuardDecision");

        void Check(string kind)
        {
            var invocations = snapshot.Facts
                .Where(fact => fact.Diagnostic?.Kind == kind)
                .Select(fact => Value(fact.Diagnostic).Invocation).ToArray();
            Need(invocations.All(value => value != Guid.Empty) &&
                invocations.Distinct().Count() == invocations.Length,
                "GuardInvocationReplay");
        }
    }

    private static LogFact One(IEnumerable<LogFact> values, string code) =>
        values.Take(2).ToArray() switch
        {
            [var value] => value,
            _ => throw new EvidenceGap(code)
        };

    private static T Value<T>(T? value) where T : class =>
        value ?? throw new EvidenceGap("MissingValue");

    private static void Need(bool condition, string code)
    {
        if (!condition)
            throw new EvidenceGap(code);
    }
}
