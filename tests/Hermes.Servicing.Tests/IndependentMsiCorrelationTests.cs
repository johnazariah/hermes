using System.Collections.Immutable;
using Hermes.Servicing.Acceptance.Model;
using Xunit;

namespace Hermes.Servicing.Tests;

public sealed class IndependentMsiCorrelationTests
{
    private static readonly ProcessKey OldClient = new(11, 111);
    private static readonly ProcessKey NewClient = new(22, 222);
    private static readonly ProcessKey OldHost = new(33, 333);
    private static readonly ProcessKey NewHost = new(44, 444);
    private static readonly Guid OldRun = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid NewRun = new("22222222-2222-2222-2222-222222222222");
    private static readonly PackageKey Package = new(
        new("33333333-3333-3333-3333-333333333333"),
        new("44444444-4444-4444-4444-444444444444"),
        new('a', 64), new('b', 64), "Owned", "1.2.3", "1033", "Owned vendor");

    private static ExpectedOld Old => new(OldClient, OldRun, Package,
        "Repair", OldBoundary.BeforeTransaction, OldHost, false);

    private static ExpectedFresh Fresh => new(NewClient, NewRun, Package,
        "Repair", NewHost, NewHost, true, true);

    [Fact]
    public void Bound_refusal_terminal_and_completed_fresh_fences_can_complete()
    {
        var proof = BarrierCorrelation.Complete(Snapshot(), Old, [Fresh]);
        Assert.Equal(1603u, proof.Old.Status);
        Assert.True(proof.Old.EndCursor < proof.Fresh[0].TransactionCursor);
    }

    [Theory]
    [InlineData(120)]
    [InlineData(130)]
    [InlineData(170)]
    [InlineData(180)]
    [InlineData(190)]
    [InlineData(200)]
    [InlineData(210)]
    public void Every_diagnostic_requires_native_binding(long cursor)
    {
        var snapshot = Snapshot();
        var facts = snapshot.Facts.Select(fact => fact.Cursor == cursor
            ? fact with { NativeWriter = null } : fact).ToImmutableArray();
        Assert.Throws<EvidenceGap>(() =>
            BarrierCorrelation.Complete(snapshot with { Facts = facts }, Old, [Fresh]));
    }

    [Theory]
    [InlineData("native-pid")]
    [InlineData("native-time")]
    [InlineData("binding-cursor")]
    [InlineData("observed-start")]
    public void Claimed_expected_live_writer_does_not_override_native_binding(string mutation)
    {
        var snapshot = Snapshot();
        var decision = snapshot.Facts[2];
        var binding = Assert.IsType<NativeWriterBinding>(decision.NativeWriter);
        var changed = mutation switch
        {
            "native-pid" => binding with { Reporter = binding.Reporter with { ProcessId = 99 } },
            "native-time" => binding with { Reporter = binding.Reporter with { EventFileTime = 1 } },
            "binding-cursor" => binding with { Cursor = 129 },
            "observed-start" => binding with { Process = OldHost with { CreationFileTime = 334 } },
            _ => throw new ArgumentException(nameof(mutation))
        };
        Assert.Throws<EvidenceGap>(() => BarrierCorrelation.Complete(
            snapshot with { Facts = snapshot.Facts.SetItem(2, decision with { NativeWriter = changed }) },
            Old, [Fresh]));
    }

    [Fact]
    public void Competing_guard_invocation_cannot_be_ignored()
    {
        var extra = Guard(125, "GuardEnter", OldClient, OldHost, OldRun, "Transaction", null);
        var diagnostic = Assert.IsType<DiagnosticValue>(extra.Diagnostic);
        var snapshot = Add(Snapshot(), extra with
        {
            Diagnostic = diagnostic with { Invocation = Guid.NewGuid() }
        });
        Assert.Throws<EvidenceGap>(() => BarrierCorrelation.Complete(snapshot, Old, [Fresh]));
    }

    [Theory]
    [InlineData("missing-decision")]
    [InlineData("old-replay")]
    [InlineData("wrong-client-start")]
    [InlineData("wrong-product")]
    [InlineData("ambiguous-decision")]
    [InlineData("missing-fresh-fence")]
    [InlineData("wrong-outcome")]
    [InlineData("unbound-outcome-product")]
    [InlineData("foreign-begin")]
    [InlineData("old-reentry")]
    [InlineData("observer-fault")]
    public void Adverse_records_cannot_complete(string mutation) =>
        Assert.Throws<EvidenceGap>(() =>
            BarrierCorrelation.Complete(Mutate(Snapshot(), mutation), Old, [Fresh]));

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Receipt_and_full_verification_are_both_required(bool receipt, bool verified) =>
        Assert.Throws<EvidenceGap>(() => BarrierCorrelation.Complete(Snapshot(), Old,
            [Fresh with { SynchronousReceiptVerified = receipt, FullVerificationReady = verified }]));

    [Fact]
    public void Parent_acceptance_without_actual_next_action_is_not_past_fence()
    {
        var records = Snapshot().Facts.Select(fact => fact.Cursor == 130
            ? fact with { Diagnostic = fact.Diagnostic is { } value
                ? value with { Status = 0 } : null } : fact).ToImmutableArray();
        Assert.Throws<EvidenceGap>(() => BarrierCorrelation.Complete(
            Snapshot() with { Facts = records },
            Old with { Boundary = OldBoundary.AfterParentFence }, [Fresh]));
    }

    private static ObservationSnapshot Snapshot() => new(100,
    [
        Begin(110, OldClient),
        Guard(120, "GuardEnter", OldClient, OldHost, OldRun, "Transaction", null),
        Guard(130, "GuardDecision", OldClient, OldHost, OldRun, "Transaction", 1603),
        Outcome(140, 1603),
        End(150, OldClient),
        Begin(160, NewClient),
        Guard(170, "GuardEnter", NewClient, NewHost, NewRun, "Preflight", null),
        Guard(180, "GuardDecision", NewClient, NewHost, NewRun, "Preflight", 0),
        Guard(190, "GuardEnter", NewClient, NewHost, NewRun, "Transaction", null),
        Guard(200, "GuardDecision", NewClient, NewHost, NewRun, "Transaction", 0),
        InstallEnd(210),
        Outcome(220, 0),
        End(230, NewClient)
    ], []);

    private static ObservationSnapshot Mutate(ObservationSnapshot snapshot, string mutation)
    {
        var facts = snapshot.Facts;
        return mutation switch
        {
            "missing-decision" => snapshot with { Facts = facts.RemoveAt(2) },
            "old-replay" => snapshot with { Facts = facts.SetItem(0, facts[0] with { Cursor = 99 }) },
            "wrong-client-start" => ChangeDiagnostic(snapshot, 130,
                value => value with { Coordinator = OldClient with { CreationFileTime = 999 } }),
            "wrong-product" => ChangeDiagnostic(snapshot, 130,
                value => value with { ProductCode = Guid.Empty }),
            "ambiguous-decision" => Add(snapshot, facts[2] with { Cursor = 131 }),
            "missing-fresh-fence" => snapshot with { Facts = facts.Where(fact => fact.Cursor != 200).ToImmutableArray() },
            "wrong-outcome" => snapshot with { Facts = facts.SetItem(3, Outcome(140, 3010)) },
            "unbound-outcome-product" => snapshot with { Facts = facts.SetItem(3,
                facts[3] with { Outcome = OutcomeValue(1603) with { ProductCode = Guid.Empty } }) },
            "foreign-begin" => Add(snapshot, Begin(125, new(99, 999))),
            "old-reentry" => Add(snapshot,
                Guard(240, "GuardDecision", OldClient, OldHost, OldRun, "Transaction", 0)),
            "observer-fault" => snapshot with { Faults = ["NativeSubscriptionLost"] },
            _ => throw new ArgumentException(nameof(mutation))
        };
    }

    private static ObservationSnapshot ChangeDiagnostic(ObservationSnapshot snapshot,
        long cursor, Func<DiagnosticValue, DiagnosticValue> change) =>
        snapshot with
        {
            Facts = snapshot.Facts.Select(fact => fact.Cursor == cursor &&
                fact.Diagnostic is { } value
                    ? fact with { Diagnostic = change(value) } : fact).ToImmutableArray()
        };

    private static ObservationSnapshot Add(ObservationSnapshot snapshot, LogFact fact) =>
        snapshot with { Facts = snapshot.Facts.Add(fact).OrderBy(value => value.Cursor).ToImmutableArray() };

    private static LogFact Begin(long cursor, ProcessKey client) =>
        new(cursor, FactKind.Begin, client, Package, @"C:\owned.msi", null, null);

    private static LogFact End(long cursor, ProcessKey client) =>
        new(cursor, FactKind.End, client, Package, @"C:\owned.msi", null, null);

    private static ProductOutcome OutcomeValue(uint status) =>
        new(1035, Package.ProductCode, Package.ProductName, Package.ProductVersion,
            Package.ProductLanguage, Package.Manufacturer, status);

    private static LogFact Outcome(long cursor, uint status) =>
        new(cursor, FactKind.Outcome, null, null, null, null, OutcomeValue(status));

    private static LogFact Guard(long cursor, string kind, ProcessKey client,
        ProcessKey host, Guid run, string phase, int? status) =>
        new(cursor, FactKind.Diagnostic, client, null, null,
            new(1, kind, Invocation(run, phase), run, client, host, Package.ProductCode,
                null, phase, "Repair", status, null, 0), null, Binding(cursor, host));

    private static Guid Invocation(Guid run, string phase) =>
        phase == "Transaction" ? run : new Guid("55555555-5555-5555-5555-555555555555");

    private static LogFact InstallEnd(long cursor) =>
        new(cursor, FactKind.Diagnostic, NewClient, null, null,
            new(1, "InstallEnd", Guid.Empty, NewRun, NewClient, NewClient,
                Package.ProductCode, null, null, null, 0, null, 0), null, Binding(cursor, NewClient));

    private static NativeWriterBinding Binding(long cursor, ProcessKey writer) =>
        NativeWriterProof.Bind(cursor,
            new(NativeWriterProof.ProviderId, writer.ProcessId, 10000),
            writer, writer, 20000);
}
