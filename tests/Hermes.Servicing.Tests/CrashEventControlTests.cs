using System.Text;
using System.Text.Json;
using Hermes.Servicing.Acceptance;
using Hermes.Servicing.Acceptance.Model;
using Xunit;

namespace Hermes.Servicing.Tests;

public sealed class CrashEventControlTests
{
    private const long Birth = 9007199254740993;
    private const uint CrashExit = 0x80131623;
    private const string Image = @"C:\owned\child.exe";
    private const string PrivateFailure = "must-not-be-exported";
    private static string Hash => new('b', 64);
    private static CrashIdentity Terminal => new(
        new(42, Birth), Image, Hash, Hash, Birth + TimeSpan.TicksPerSecond, CrashExit);
    private static CrashQueryResult Match => new CrashQueryCompleted(
        new CrashEventMatched(new("80131623", "private.dll", @"C:\private\private.dll")));

    private const string Metadata =
        """{"schemaVersion":1,"usable":1,"unavailable":0,"metadataCount":1,"versions":[{"id":1000,"version":0,"unavailable":0,"fieldCount":15,"duplicateCount":0,"caseMismatchCount":0,"knownFieldPresentBits":63,"templateSha256":"cd0a3d85a95e98a2909d1a77bada29db34ed1abbe37a058d6c06e9aca599557e","fields":[[1,1,1,20,18],[2,1,1,21,19],[3,1,1,1,1],[4,1,1,1,1],[5,1,1,1,1],[6,1,1,1,1]]}]}""";

    private enum Call
    {
        Context, Receipt, Start, Arm, Recheck, Query, Delay,
        ObservationSummary, ControlSummary, EventSummary,
        ControlReport, EventReport, DisposeReceipt, DisposeChild
    }

    [Fact]
    public async Task Held_callback_fences_all_three_queries_and_changes_only_birth()
    {
        using var fixture = new ObserverFixture();
        await fixture.Run();
        Assert.True(fixture.Observer.Proof.Verified);
        Assert.Equal(3, fixture.Requests.Count);
        var plan = CrashCorrelationPlan.Create(Terminal);
        Assert.Equal<CrashQueryRequest>([plan.Exact, plan.Lower, plan.Upper], fixture.Requests);
        Assert.All(fixture.Requests, request => Assert.Same(plan.Exact.Window.GetType(),
            request.Window.GetType()));
        fixture.AssertQueryFences();
        Assert.Equal(Call.ObservationSummary, fixture.Calls[^1]);
        Assert.False(fixture.Receipt.Closed);
        Assert.Equal(CrashExit, fixture.Scope.Recheck().ExitCode);
    }

    [Fact]
    public async Task Empty_exact_queries_poll_without_running_adjacent_controls_early()
    {
        using var fixture = new ObserverFixture();
        fixture.QueryPolicy = (index, request) => index < 2
            ? new CrashQueryEmpty() : Matching(request);
        await fixture.Run();
        Assert.True(fixture.Observer.Proof.Verified);
        Assert.Equal(3, fixture.Observer.Proof.Polls);
        Assert.Equal(5, fixture.Requests.Count);
        Assert.All(fixture.Delays, delay => Assert.InRange(delay,
            TimeSpan.FromTicks(1), TimeSpan.FromMilliseconds(250)));
        Assert.All(fixture.Requests.Take(3),
            request => Assert.Equal((ulong)Birth, request.Subject.CreationFileTime));
    }

    [Theory]
    [InlineData(2UL)]
    [InlineData(9007199254740992UL)]
    [InlineData(9007199254740993UL)]
    [InlineData(9223372036854775807UL)]
    [InlineData(18446744073709551614UL)]
    public void Adjacent_requests_preserve_UInt64_values_and_every_other_predicate(ulong birth)
    {
        var request = CrashCorrelationPlan.Create(Terminal).Exact;
        request = request with { Subject = request.Subject with { CreationFileTime = birth } };
        var plan = CrashCorrelationPlan.Adjacent(request);
        Assert.Equal(request, plan.Exact);
        Assert.Equal(request with
        {
            Subject = request.Subject with { CreationFileTime = checked(birth - 1) }
        }, plan.Lower);
        Assert.Equal(request with
        {
            Subject = request.Subject with { CreationFileTime = checked(birth + 1) }
        }, plan.Upper);
        Assert.Same(request.Window, plan.Lower.Window);
        Assert.Same(request.Window, plan.Upper.Window);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(18446744073709551615UL)]
    public void Invalid_neighbor_boundaries_refuse_without_wrapping(ulong birth)
    {
        var request = CrashCorrelationPlan.Create(Terminal).Exact;
        var failure = Assert.Throws<CrashCorrelationException>(() =>
            CrashCorrelationPlan.Adjacent(request with
            {
                Subject = request.Subject with { CreationFileTime = birth }
            }));
        Assert.Equal(CrashCorrelationReason.Subject, failure.Reason);
    }

    [Fact]
    public void Window_is_fixed_to_actual_birth_and_exit_and_never_widened()
    {
        var plan = CrashCorrelationPlan.Create(Terminal);
        Assert.Equal(new DateTimeOffset(DateTime.FromFileTimeUtc(Birth)),
            plan.Exact.Window.StartInclusive);
        Assert.Equal(new DateTimeOffset(DateTime.FromFileTimeUtc(Terminal.ExitFileTime))
            .AddSeconds(20), plan.Exact.Window.EndInclusive);
        Assert.Throws<CrashCorrelationException>(() => CrashCorrelationPlan.Create(
            Terminal with { ExitFileTime = Birth + TimeSpan.FromSeconds(101).Ticks }));
    }

    [Theory]
    [InlineData("\"usable\":1", "\"usable\":0")]
    [InlineData("\"unavailable\":0", "\"unavailable\":14")]
    [InlineData("\"metadataCount\":1", "\"metadataCount\":2")]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":\"1\"")]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":1.0")]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":1,\"extra\":0")]
    [InlineData("\"fieldCount\":15", "\"fieldCount\":14")]
    [InlineData("\"duplicateCount\":0", "\"duplicateCount\":1")]
    [InlineData("\"caseMismatchCount\":0", "\"caseMismatchCount\":1")]
    [InlineData("\"knownFieldPresentBits\":63", "\"knownFieldPresentBits\":31")]
    [InlineData("\"version\":0", "\"version\":1")]
    [InlineData("[1,1,1,20,18]", "[1,1,1,8,18]")]
    [InlineData("[2,1,1,21,19]", "[2,1,1,21,10]")]
    [InlineData("[6,1,1,1,1]", "[6,1,1,1,0]")]
    [InlineData("[6,1,1,1,1]", "[5,1,1,1,1]")]
    [InlineData("[6,1,1,1,1]", "[6,1,1,1,1,0]")]
    [InlineData("cd0a3d85", "Cd0a3d85")]
    public async Task Wrong_metadata_prevents_child_start_and_every_query(
        string oldValue, string newValue)
    {
        var fixture = new CommandFixture(Metadata.Replace(oldValue, newValue,
            StringComparison.Ordinal));
        Assert.Equal(1, await fixture.Run());
        Assert.DoesNotContain(Call.Start, fixture.Calls);
        Assert.DoesNotContain(Call.Query, fixture.Calls);
        Assert.True(fixture.Receipt.Closed);
        Assert.Equal(1, EventStatus(fixture.EventBytes));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":true}")]
    public async Task Missing_or_malformed_receipts_cannot_start_a_control(string json)
    {
        var fixture = new CommandFixture(json);
        Assert.Equal(1, await fixture.Run());
        Assert.DoesNotContain(Call.Start, fixture.Calls);
        Assert.DoesNotContain(Call.Query, fixture.Calls);
    }

    [Fact]
    public void Receipt_bounds_codebook_and_source_hash_are_not_inferred()
    {
        Assert.Throws<CrashCorrelationException>(() => CrashEventSchema.Admit(
            new(new byte[4097], Hash)));
        Assert.Throws<CrashCorrelationException>(() => CrashEventSchema.Admit(
            new(Encoding.UTF8.GetBytes(Metadata), "not-a-hash")));
        var unknown = Metadata.Replace("[3,1,1,1,1]", "[3,1,1,999,1]",
            StringComparison.Ordinal);
        Assert.Throws<CrashCorrelationException>(() => CrashEventSchema.Admit(
            new(Encoding.UTF8.GetBytes(unknown), Hash)));
    }

    [Theory]
    [InlineData(-301, -1, 0)]
    [InlineData(-1, -301, 0)]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, -1)]
    [InlineData(-1, -1, 301)]
    public void Stale_future_or_reversed_local_receipt_times_refuse(
        int created, int written, int now)
    {
        var opened = DateTimeOffset.UnixEpoch;
        Assert.Throws<CrashCorrelationException>(() => CrashEventSchema.RequireFresh(
            opened.AddSeconds(created), opened.AddSeconds(written),
            opened, opened.AddSeconds(now)));
    }

    [Fact]
    public void Fresh_local_receipt_times_do_not_claim_independent_publication()
    {
        var now = DateTimeOffset.UnixEpoch;
        CrashEventSchema.RequireFresh(now.AddSeconds(-1), now.AddSeconds(-1), now, now);
        var admission = CrashEventSchema.Admit(
            new(Encoding.UTF8.GetBytes(Metadata), Hash));
        Assert.True(CrashControl.IsHash(admission.MetadataSha256));
        Assert.Equal(Hash, admission.SourceSha256);
        Assert.Equal(nameof(CrashSchemaAdmission), admission.ToString());
    }

    public static IEnumerable<object[]> BadPositiveResults() =>
        new (CrashQueryResult Result, CrashCorrelationReason Reason)[]
        {
            (new CrashQueryAmbiguous(), CrashCorrelationReason.Ambiguous),
            (new CrashQueryCompleted(new CrashEventMismatch(
                CrashEventReason.CreationTimeMismatch)), CrashCorrelationReason.IdentityMismatch),
            (new CrashQueryCompleted(CrashEventValues.Failure(
                CrashEventFailureKind.Unavailable, CrashEventReason.Render)),
                CrashCorrelationReason.Reader),
            (new CrashQueryVisited(new CrashEventMatched(
                new("80131623", "private.dll", @"C:\private\private.dll"))),
                CrashCorrelationReason.EnumerationIncomplete),
            (CrashEventQuery.Failure(CrashEventFailureKind.Unavailable,
                CrashQueryReason.Open), CrashCorrelationReason.Query),
            (CrashEventQuery.Failure(CrashEventFailureKind.Refused,
                CrashQueryReason.EventClose), CrashCorrelationReason.Query)
        }.Select(item => new object[] { item.Result, item.Reason });

    [Theory]
    [MemberData(nameof(BadPositiveResults))]
    internal async Task Positive_failures_never_run_negative_controls_or_pass(
        CrashQueryResult result, CrashCorrelationReason reason)
    {
        using var fixture = new ObserverFixture();
        fixture.QueryPolicy = (_, _) => result;
        await Assert.ThrowsAsync<CrashCorrelationException>(fixture.Run);
        Assert.False(fixture.Observer.Proof.Verified);
        Assert.Equal(reason, fixture.Observer.Proof.Reason);
        Assert.Single(fixture.Requests);
        Assert.Equal(Call.ObservationSummary, fixture.Calls[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Any_adjacent_candidate_including_reader_mismatch_fails_precision(bool upper)
    {
        using var fixture = new ObserverFixture();
        fixture.QueryPolicy = (_, request) => request.Subject.CreationFileTime ==
            checked((ulong)Birth + (upper ? 1UL : 0UL))
                ? new CrashQueryCompleted(new CrashEventMismatch(CrashEventReason.CreationTimeMismatch))
                : Matching(request);
        if (!upper)
            fixture.QueryPolicy = (index, request) => index == 1
                ? new CrashQueryVisited(new CrashEventMismatch(CrashEventReason.CreationTimeMismatch))
                : Matching(request);
        await Assert.ThrowsAsync<CrashCorrelationException>(fixture.Run);
        Assert.Equal(CrashCorrelationReason.AdjacentCandidate, fixture.Observer.Proof.Reason);
        Assert.False(fixture.Observer.Proof.Verified);
    }

    [Fact]
    public async Task Absent_delivery_is_bounded_and_does_not_fabricate_a_match()
    {
        using var fixture = new ObserverFixture();
        fixture.QueryPolicy = (_, _) => new CrashQueryEmpty();
        await Assert.ThrowsAsync<CrashCorrelationException>(fixture.Run);
        Assert.False(fixture.Observer.Proof.Verified);
        Assert.InRange(fixture.Observer.Proof.Polls, 1, CrashEventObserver.MaximumPolls);
        Assert.True(fixture.Clock.Elapsed <= CrashEventObserver.PollBudget);
        Assert.All(fixture.Requests, request =>
            Assert.Equal((ulong)Birth, request.Subject.CreationFileTime));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expiry_or_cancellation_after_native_return_cannot_pass(bool cancel)
    {
        using var fixture = new ObserverFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.QueryPolicy = (_, _) =>
        {
            if (cancel)
                cancellation.Cancel();
            else
                fixture.Clock.Advance(TimeSpan.FromSeconds(21));
            return Match;
        };
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Run(cancellation.Token));
        Assert.False(fixture.Observer.Proof.Verified);
        Assert.Single(fixture.Requests);
        Assert.Equal(cancel ? CrashCorrelationReason.Cancelled :
            CrashCorrelationReason.WindowExpired, fixture.Observer.Proof.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_or_schema_change_during_query_invalidates_the_observation(bool source)
    {
        using var fixture = new ObserverFixture();
        fixture.QueryPolicy = (_, _) =>
        {
            if (source)
                fixture.Receipt.SourceHash = new string('c', 64);
            else
                fixture.Receipt.Json = Metadata.Replace("\"usable\":1", "\"usable\":0",
                    StringComparison.Ordinal);
            return Match;
        };
        await Assert.ThrowsAnyAsync<Exception>(fixture.Run);
        Assert.False(fixture.Observer.Proof.Verified);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task Retained_identity_is_rechecked_after_each_query()
    {
        using var fixture = new ObserverFixture();
        fixture.QueryPolicy = (_, _) =>
        {
            fixture.Current = Terminal with { Process = new(43, Birth) };
            return Match;
        };
        await Assert.ThrowsAnyAsync<Exception>(fixture.Run);
        Assert.False(fixture.Observer.Proof.Verified);
        Assert.Single(fixture.Requests);
        Assert.Equal(CrashCorrelationReason.Scope, fixture.Observer.Proof.Reason);
    }

    [Fact]
    public async Task Unexpected_query_errors_propagate_after_safe_numeric_diagnostic()
    {
        using var fixture = new ObserverFixture();
        var unexpected = new ArithmeticException("must-not-be-exported");
        fixture.QueryPolicy = (_, _) => throw unexpected;
        Assert.Same(unexpected, await Assert.ThrowsAsync<ArithmeticException>(fixture.Run));
        Assert.False(fixture.Observer.Proof.Verified);
        Assert.All(fixture.Summaries, text => Assert.DoesNotContain(unexpected.Message, text));
        Assert.Equal(Call.ObservationSummary, fixture.Calls[^1]);
    }

    [Fact]
    public async Task Real_owned_coordinator_invokes_callback_before_disposing_the_child()
    {
        var fixture = new CommandFixture(Metadata) { RefuseReceiptDuringCallback = true };
        Assert.Equal(1, await fixture.Run());
        Assert.Contains(Call.Arm, fixture.Calls);
        Assert.Contains(Call.Recheck, fixture.Calls);
        Assert.DoesNotContain(Call.Query, fixture.Calls);
        Assert.True(fixture.Calls.IndexOf(Call.ObservationSummary) <
            fixture.Calls.IndexOf(Call.DisposeChild));
        Assert.True(fixture.ChildDisposed && fixture.Receipt.Closed);
        using var report = JsonDocument.Parse(fixture.ControlBytes);
        Assert.Equal(28, report.RootElement.EnumerateObject().Count());
        Assert.Equal(1, report.RootElement.GetProperty("status").GetInt32());
        AssertOwnedControl(fixture, false, CrashFault.Callback);
        AssertEventFacts(fixture, CrashCorrelationReason.Schema, 1, true);
        Assert.Equal(2, fixture.CallbackReceiptReads);
    }

    [Fact]
    public async Task Command_success_comes_from_the_real_owned_coordinator()
    {
        var fixture = new CommandFixture(Metadata);
        Assert.Equal(0, await fixture.Run());
        AssertOwnedControl(fixture, true, CrashFault.None);
        AssertEventFacts(fixture, CrashCorrelationReason.None, 0, true, true);
        var plan = CrashCorrelationPlan.Create(Terminal);
        Assert.Equal<CrashQueryRequest>(
            [plan.Exact, plan.Lower, plan.Upper], fixture.Requests);
        AssertCommandQueryFences(fixture.Calls);
        Assert.Equal(8, fixture.CallbackReceiptReads);
        Assert.True(fixture.ControlPublished && fixture.EventPublished);
        Assert.Equal(Call.EventSummary, fixture.Calls[^1]);
    }

    [Theory]
    [InlineData("C:/owned/child.exe")]
    [InlineData(@"\\server\share\child.exe")]
    [InlineData(@"\\?\C:\owned\child.exe")]
    public void General_qualification_does_not_widen_the_fixed_query(string image)
    {
        Assert.True(CrashWindowsPath.IsFullyQualified(image));
        var failure = Assert.Throws<CrashCorrelationException>(() =>
            CrashCorrelationPlan.Create(Terminal with { ImagePath = image }));
        Assert.Equal(CrashCorrelationReason.Subject, failure.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Final_receipt_validation_does_not_rewrite_a_successful_control(int change)
    {
        var fixture = new CommandFixture(Metadata);
        ChangeFinalReceipt(fixture, change);
        Assert.Equal(1, await fixture.Run());
        AssertOwnedControl(fixture, true, CrashFault.None);
        AssertEventFacts(fixture, change == 2 ? CrashCorrelationReason.Schema :
            CrashCorrelationReason.ReceiptChanged, 0, true, true);
        Assert.Equal(3, fixture.Requests.Count);
        AssertCommandQueryFences(fixture.Calls);
    }

    [Fact]
    public async Task Receipt_disposal_failure_preserves_a_successful_control()
    {
        var fixture = new CommandFixture(Metadata) { ReceiptCleanupFailure = true };
        Assert.Equal(1, await fixture.Run());
        AssertOwnedControl(fixture, true, CrashFault.None);
        AssertEventFacts(fixture, CrashCorrelationReason.ReceiptCleanup, 0, false, true);
        Assert.Equal(3, fixture.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_failures_change_report_status_not_the_control_fault(bool control)
    {
        var error = new IOException(PrivateFailure);
        var fixture = new CommandFixture(Metadata)
        {
            ControlPublicationError = control ? error : null,
            EventPublicationError = control ? null : error
        };
        Assert.Equal(1, await fixture.Run());
        AssertOwnedControl(fixture, true, CrashFault.None);
        AssertPublicationFailure(fixture, control);
        AssertSafeDiagnostics(fixture, [error]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unexpected_publication_errors_escape_after_owned_settlement(bool control)
    {
        var error = new ArithmeticException(PrivateFailure);
        var fixture = new CommandFixture(Metadata)
        {
            ControlPublicationError = control ? error : null,
            EventPublicationError = control ? null : error
        };
        Assert.Same(error, await Assert.ThrowsAsync<ArithmeticException>(() => fixture.Run()));
        AssertOwnedControl(fixture, true, CrashFault.None);
        Assert.Equal(!control, fixture.ControlPublished);
        Assert.False(fixture.EventPublished);
        Assert.Equal(control, fixture.EventBytes.Length == 0);
        AssertSafeDiagnostics(fixture, [error]);
    }

    [Fact]
    public async Task Adopted_start_aggregate_waits_for_the_real_reap_before_escaping()
    {
        var first = new IOException(PrivateFailure);
        var last = new IOException(PrivateFailure);
        var coordinator = new AggregateException(first, last);
        var fixture = new CommandFixture(Metadata)
            { StartError = coordinator, DeferExit = true };
        var run = fixture.Run();
        Assert.False(run.IsCompleted);
        fixture.AssertReapPending();
        fixture.CompleteExit();
        Assert.Same(coordinator,
            await Assert.ThrowsAsync<AggregateException>(() => run));
        AssertFenceFailures(coordinator, first, last);
        fixture.AssertAdoptedStartCleanup();
        AssertCoordinatorEscape(fixture, [coordinator, first, last]);
        Assert.Empty(fixture.Requests);
        Assert.DoesNotContain(Call.Arm, fixture.Calls);
    }

    [Fact]
    public async Task Coordinator_cleanup_aggregate_escapes_without_publishing_default_facts()
    {
        var first = new IOException(PrivateFailure);
        var last = new IOException(PrivateFailure);
        var coordinator = new AggregateException(first, last);
        var fixture = new CommandFixture(Metadata)
            { ChildDisposalError = coordinator };
        Assert.Same(coordinator,
            await Assert.ThrowsAsync<AggregateException>(() => fixture.Run()));
        AssertFenceFailures(coordinator, first, last);
        fixture.AssertOwnedCleanup();
        AssertCoordinatorEscape(fixture, [coordinator, first, last]);
        Assert.Equal(3, fixture.Requests.Count);
        AssertCommandQueryFences(fixture.Calls);
        Assert.Equal(8, fixture.CallbackReceiptReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Coordinator_cleanup_and_observer_errors_each_escape_once(bool unexpectedObserver)
    {
        Exception observer = unexpectedObserver
            ? new ArithmeticException(PrivateFailure) : new IOException(PrivateFailure);
        var first = new IOException(PrivateFailure);
        var last = new IOException(PrivateFailure);
        var coordinator = new AggregateException(first, last);
        var fixture = new CommandFixture(Metadata)
            { ChildDisposalError = coordinator, QueryPolicy = _ => throw observer };
        var failure = await Assert.ThrowsAsync<AggregateException>(() => fixture.Run());
        Assert.Collection(failure.InnerExceptions,
            error => Assert.Same(observer, error), error => Assert.Same(coordinator, error));
        AssertFenceFailures(coordinator, first, last);
        fixture.AssertOwnedCleanup();
        AssertCoordinatorEscape(fixture, [observer, coordinator, first, last]);
        Assert.Single(fixture.Requests);
        Assert.Equal(4, fixture.CallbackReceiptReads);
    }

    [Fact]
    public async Task Repeated_operational_fences_retain_both_original_errors()
    {
        using var fixture = new ObserverFixture();
        var first = new IOException(PrivateFailure);
        var last = new IOException(PrivateFailure);
        var failures = new Queue<Exception>([first, last]);
        fixture.Receipt.BeforeRead = () => throw failures.Dequeue();
        var failure = await Assert.ThrowsAsync<AggregateException>(fixture.Run);
        AssertFenceFailures(failure, first, last);
        Assert.Empty(failures);
        Assert.Empty(fixture.Requests);
        Assert.Equal(CrashCorrelationReason.Receipt, fixture.Observer.Proof.Reason);
    }

    [Fact]
    public async Task Repeated_cancelled_fences_preserve_cancellation_and_settlement()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new CommandFixture(Metadata);
        fixture.Receipt.BeforeRead = () =>
        {
            if (fixture.InCallback)
                cancellation.Cancel();
        };
        Assert.Equal(1, await fixture.Run(cancellation.Token));
        AssertOwnedControl(fixture, false, CrashFault.Cancelled, true);
        AssertEventFacts(fixture, CrashCorrelationReason.Cancelled, 1, true);
        Assert.Equal(2, fixture.CallbackReceiptReads);
        Assert.Empty(fixture.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unexpected_fences_retain_both_errors_until_after_cleanup(bool operationalFirst)
    {
        Exception first = operationalFirst
            ? new IOException(PrivateFailure) : new ArithmeticException(PrivateFailure);
        var last = new ArithmeticException(PrivateFailure);
        var fixture = new CommandFixture(Metadata);
        RefuseFences(fixture, [first, last]);
        var failure = await Assert.ThrowsAsync<AggregateException>(() => fixture.Run());
        AssertFenceFailures(failure, first, last);
        AssertUnexpectedCleanup(fixture, [first, last]);
        Assert.Equal(2, fixture.CallbackReceiptReads);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task Cancellation_does_not_hide_an_unexpected_mandatory_final_fence()
    {
        using var cancellation = new CancellationTokenSource();
        var last = new ArithmeticException(PrivateFailure);
        var fixture = new CommandFixture(Metadata);
        CancelThenRefuseFinalFence(fixture, cancellation, last);
        var failure = await Assert.ThrowsAsync<AggregateException>(
            () => fixture.Run(cancellation.Token));
        var errors = failure.Flatten().InnerExceptions;
        Assert.Equal(2, errors.Count);
        Assert.IsType<OperationCanceledException>(errors[0]);
        Assert.Same(last, errors[1]);
        AssertUnexpectedCleanup(fixture, errors);
        Assert.Equal(2, fixture.CallbackReceiptReads);
    }

    [Fact]
    public async Task Repeated_post_query_fence_failures_preserve_the_real_callback_fault()
    {
        var first = new IOException(PrivateFailure);
        var last = new IOException(PrivateFailure);
        var fixture = new CommandFixture(Metadata);
        RefuseFences(fixture, [first, last], true);
        Assert.Equal(1, await fixture.Run());
        AssertOwnedControl(fixture, false, CrashFault.Callback);
        AssertEventFacts(fixture, CrashCorrelationReason.Receipt, 1, true);
        Assert.Single(fixture.Requests);
        Assert.Equal(CrashCorrelationPlan.Create(Terminal).Exact, fixture.Requests[0]);
        Assert.Equal(4, fixture.CallbackReceiptReads);
        AssertSafeDiagnostics(fixture, [first, last]);
    }

    [Fact]
    public async Task Unexpected_post_query_fences_escape_with_both_original_errors()
    {
        var first = new ArithmeticException(PrivateFailure);
        var last = new IOException(PrivateFailure);
        var fixture = new CommandFixture(Metadata);
        RefuseFences(fixture, [first, last], true);
        var failure = await Assert.ThrowsAsync<AggregateException>(() => fixture.Run());
        AssertFenceFailures(failure, first, last);
        AssertUnexpectedCleanup(fixture, [first, last]);
        Assert.Single(fixture.Requests);
        Assert.Equal(CrashCorrelationPlan.Create(Terminal).Exact, fixture.Requests[0]);
        Assert.Equal(4, fixture.CallbackReceiptReads);
    }

    [Fact]
    public async Task Query_error_and_both_required_fence_errors_survive_actual_cleanup()
    {
        var query = new ArithmeticException(PrivateFailure);
        var afterQuery = new IOException(PrivateFailure);
        var final = new IOException(PrivateFailure);
        var fixture = new CommandFixture(Metadata) { QueryPolicy = _ => throw query };
        RefuseFences(fixture, [afterQuery, final], true);
        var failure = await Assert.ThrowsAsync<AggregateException>(() => fixture.Run());
        var errors = failure.Flatten().InnerExceptions;
        Assert.Equal(3, errors.Count);
        Assert.All<Exception>([query, afterQuery, final], expected =>
            Assert.Single(errors, error => ReferenceEquals(expected, error)));
        AssertUnexpectedCleanup(fixture, errors);
        Assert.Single(fixture.Requests);
        Assert.Equal(4, fixture.CallbackReceiptReads);
    }

    [Fact]
    public async Task Empty_query_aggregate_remains_unexpected_after_actual_cleanup()
    {
        var error = new AggregateException();
        var fixture = new CommandFixture(Metadata) { QueryPolicy = _ => throw error };
        Assert.False(CrashCorrelationErrors.Operational(error));
        Assert.Same(error, await Assert.ThrowsAsync<AggregateException>(() => fixture.Run()));
        AssertUnexpectedCleanup(fixture, [error]);
        Assert.Single(fixture.Requests);
        Assert.Equal(4, fixture.CallbackReceiptReads);
    }

    [Fact]
    public async Task Schema_receipt_cleanup_failure_cannot_publish_acceptance()
    {
        var fixture = new CommandFixture("{}") { ReceiptCleanupFailure = true };
        Assert.Equal(1, await fixture.Run());
        using var report = JsonDocument.Parse(fixture.EventBytes);
        Assert.Equal(1, report.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(0, report.RootElement.GetProperty("receiptClosed").GetInt32());
    }

    [Theory]
    [InlineData("startup-crash-event-probe", "--pid")]
    [InlineData("startup-crash-event-probe", "42")]
    [InlineData("startup-crash-probe", "")]
    [InlineData("startup-crash-control", "")]
    public async Task Event_dispatcher_accepts_only_the_single_exact_command(
        string command, string argument)
    {
        var fixture = new CommandFixture(Metadata);
        var arguments = argument.Length == 0 ? new[] { command } : [command, argument];
        Assert.Equal(2, await CrashEventControl.DispatchAsync(
            arguments, fixture.Effects, CancellationToken.None));
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task Guard_refusal_precedes_receipt_process_query_and_publication()
    {
        var fixture = new CommandFixture(Metadata) { GuardFailure = true };
        Assert.Equal(1, await fixture.Run());
        Assert.DoesNotContain(Call.Receipt, fixture.Calls);
        Assert.DoesNotContain(Call.Start, fixture.Calls);
        Assert.DoesNotContain(Call.Query, fixture.Calls);
        Assert.DoesNotContain(Call.ControlReport, fixture.Calls);
        Assert.DoesNotContain(Call.EventReport, fixture.Calls);
    }

    [Fact]
    public async Task Legacy_dispatcher_does_not_accept_the_new_command()
    {
        var effects = new CrashCommandEffects(
            () => throw new ArithmeticException(),
            _ => throw new ArithmeticException(), _ => throw new ArithmeticException(),
            _ => throw new ArithmeticException(), _ => throw new ArithmeticException());
        Assert.Equal(2, await CrashCommands.DispatchAsync(
            [CrashEventControl.Command], effects, CancellationToken.None));
        Assert.Equal("startup-crash-probe", CrashControl.ProbeCommand);
        Assert.Equal("startup-crash-control", CrashControl.ChildCommand);
    }

    [Fact]
    public async Task Report_is_bounded_scalar_only_and_never_original_acceptance()
    {
        using var fixture = new ObserverFixture();
        await fixture.Run();
        var result = new CrashEventControlResult(PassedControl(), fixture.Observer.Proof, true);
        var bytes = CrashEventReport.Encode(result);
        using var document = JsonDocument.Parse(bytes);
        Assert.InRange(bytes.Length, 1, 4096);
        Assert.Equal(0, result.ExitCode);
        Assert.All(document.RootElement.EnumerateObject(), AssertScalar);
        AssertClosedClaims(document.RootElement);
        Assert.DoesNotContain(Image, Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("private.dll", Encoding.UTF8.GetString(bytes));
        Assert.Equal(1, (result with { Control = result.Control with
            { Cleanup = false, CleanupError = CrashCleanup.Dispose } }).ExitCode);
    }

    [Fact]
    public void Portable_policy_is_in_the_existing_model_assembly()
    {
        Assert.Same(typeof(ProcessKey).Assembly, typeof(CrashEventControl).Assembly);
        Assert.Same(typeof(ProcessKey).Assembly, typeof(CrashEventSchema).Assembly);
        Assert.Same(typeof(ProcessKey).Assembly, typeof(CrashEventObserver).Assembly);
    }

    [Fact]
    public void Completed_query_requires_a_second_bounded_end_marker()
    {
        var api = new BatchApi([new(true, 0, 1, 21), new(false, 259, 0)]);
        var result = Assert.IsType<CrashQueryCompleted>(Enumerate(api));
        Assert.IsType<CrashEventMatched>(result.Read);
        Assert.Equal(CrashQueryPrecision.Unproven, result.Precision);
        Assert.Equal(2, api.NextCalls);
        Assert.Equal(1, api.ReadCalls);
        Assert.Equal<nint>([21, 11], api.CloseAttempts);
        api.AssertSettled();
    }

    [Fact]
    public void Additional_candidate_is_ambiguous_even_after_a_matched_first_read()
    {
        var api = new BatchApi([new(true, 0, 1, 21), new(true, 0, 1, 22)]);
        Assert.IsType<CrashQueryAmbiguous>(Enumerate(api));
        Assert.Equal(2, api.NextCalls);
        Assert.Equal(1, api.ReadCalls);
        Assert.Equal<nint>([22, 21, 11], api.CloseAttempts);
        api.AssertSettled();
    }

    [Fact]
    public void First_empty_or_ambiguous_batch_does_not_make_an_extra_next_call()
    {
        var empty = new BatchApi([new(false, 259, 0)]);
        Assert.IsType<CrashQueryEmpty>(Enumerate(empty));
        Assert.Equal(1, empty.NextCalls);
        empty.AssertSettled();
        var ambiguous = new BatchApi([new(true, 0, 2, 21, 22)]);
        Assert.IsType<CrashQueryAmbiguous>(Enumerate(ambiguous));
        Assert.Equal(1, ambiguous.NextCalls);
        Assert.Equal(0, ambiguous.ReadCalls);
        ambiguous.AssertSettled();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void End_marker_or_cleanup_failure_invalidates_completion(bool cleanup)
    {
        var api = new BatchApi([new(true, 0, 1, 21),
            new(false, cleanup ? 259 : 1460, 0)]) { FailClose = cleanup };
        Assert.IsType<CrashQueryFailure>(Enumerate(api));
        api.AssertSettled();
    }

    [Fact]
    public void Cross_batch_alias_is_malformed_and_receives_one_close_attempt()
    {
        var api = new BatchApi([new(true, 0, 1, 21), new(true, 0, 1, 21)]);
        Assert.IsType<CrashQueryFailure>(Enumerate(api));
        Assert.Equal<nint>([21, 11], api.CloseAttempts);
        api.AssertSettled();
    }

    [Fact]
    public void Unexpected_second_next_error_closes_both_batches_before_propagating()
    {
        var api = new BatchApi([new(true, 0, 1, 21), new(true, 0, 1, 22)])
            { ThrowOnSecondNext = true };
        Assert.Same(api.Unexpected, Assert.Throws<ArithmeticException>(() => Enumerate(api)));
        Assert.Equal<nint>([22, 21, 11], api.CloseAttempts);
        api.AssertSettled();
    }

    private static CrashQueryResult Enumerate(BatchApi api) =>
        CrashEventQuery.EnumerateCompletedCandidate(api,
            CrashCorrelationPlan.Create(Terminal).Exact,
            CrashQueryAdmission.CallerAttestsFixedProviderAndSchema);

    private static CrashQueryResult Matching(CrashQueryRequest request) =>
        request.Subject.CreationFileTime == (ulong)Birth ? Match : new CrashQueryEmpty();

    private static int EventStatus(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.GetProperty("status").GetInt32();
    }

    private static int Number(JsonElement value, string name) =>
        value.GetProperty(name).GetInt32();

    private static void AssertOwnedControl(
        CommandFixture fixture, bool held, CrashFault fault, bool cancelled = false)
    {
        fixture.AssertOwnedCleanup();
        using var document = JsonDocument.Parse(fixture.ControlBytes);
        var report = document.RootElement;
        Assert.Equal(28, report.EnumerateObject().Count());
        AssertSettledFlags(report, held, cancelled);
        Assert.Equal((int)fault, Number(report, "error"));
        Assert.Equal(held && fault == CrashFault.None && !cancelled ? 0 : 1,
            Number(report, "status"));
        Assert.Equal(CrashExit, report.GetProperty("exit").GetUInt32());
        Assert.Equal(Hash, report.GetProperty("imageSha256").GetString());
        Assert.Equal(Hash, report.GetProperty("sourceSha256").GetString());
    }

    private static void AssertSettledFlags(JsonElement report, bool held, bool cancelled)
    {
        string[] completed =
        [
            "started", "ready", "identity", "armAttempted", "armed", "exited",
            "cleanup", "waitSettled", "drainsSettled", "writeSettled",
            "callbackSettled", "stdoutComplete", "stderrComplete", "outerSupervisionRequired"
        ];
        Assert.All(completed, name => Assert.Equal(1, Number(report, name)));
        Assert.Equal(held ? 1 : 0, Number(report, "held"));
        Assert.Equal(cancelled ? 1 : 0, Number(report, "cancelled"));
        Assert.Equal(0, Number(report, "deadline"));
        Assert.Equal((int)CrashCleanup.None, Number(report, "cleanupError"));
        Assert.Equal(0, Number(report, "reportError"));
        Assert.Equal(0, Number(report, "originalGatePassed"));
        Assert.Equal(0, Number(report, "nativeAcceptance"));
        Assert.Equal(0, Number(report, "synchronousCallsInterruptible"));
    }

    private static void AssertEventFacts(CommandFixture fixture,
        CrashCorrelationReason reason, int controlStatus, bool receiptClosed, bool matched = false)
    {
        using var document = JsonDocument.Parse(fixture.EventBytes);
        var report = document.RootElement;
        Assert.Equal(reason == CrashCorrelationReason.None ? 0 : 1, Number(report, "status"));
        Assert.Equal((int)reason, Number(report, "reason"));
        Assert.Equal(controlStatus, Number(report, "controlStatus"));
        Assert.Equal(receiptClosed ? 1 : 0, Number(report, "receiptClosed"));
        Assert.Equal(0, Number(report, "reportError"));
        AssertEventProof(report, fixture.Requests.Count, matched);
        AssertEventBounds(report);
        Assert.All(report.EnumerateObject(), AssertScalar);
    }

    private static void AssertEventProof(JsonElement report, int queries, bool matched)
    {
        var admission = CrashEventSchema.Admit(new(Encoding.UTF8.GetBytes(Metadata), Hash));
        Assert.Equal(admission.MetadataSha256, report.GetProperty("metadataSha256").GetString());
        Assert.Equal(Hash, report.GetProperty("schemaSourceSha256").GetString());
        Assert.Equal(1, Number(report, "schemaAdmitted"));
        Assert.Equal(matched ? 1 : -1, Number(report, "exactCandidates"));
        Assert.Equal(matched ? 1 : 0, Number(report, "exactReadMatched"));
        Assert.Equal(matched ? 0 : -1, Number(report, "birthMinusOneCandidates"));
        Assert.Equal(matched ? 0 : -1, Number(report, "birthPlusOneCandidates"));
        Assert.Equal(matched ? 3 : 0, Number(report, "completedQueryEnumerations"));
        Assert.Equal(queries, Number(report, "queries"));
        Assert.Equal(queries > 0 ? 1 : 0, Number(report, "polls"));
    }

    private static void AssertEventBounds(JsonElement report)
    {
        Assert.Equal(0, Number(report, "originalGatePassed"));
        Assert.Equal(0, Number(report, "nativeAcceptance"));
        Assert.Equal(0, Number(report, "globalLogUniqueness"));
        Assert.Equal(0, Number(report, "independentPublicationVerified"));
        Assert.Equal(1, Number(report, "thisOwnedControlOnly"));
        Assert.Equal(1, Number(report, "outerSupervisionRequired"));
        Assert.Equal(0, Number(report, "synchronousCallsInterruptible"));
        Assert.Equal(2, Number(report, "maximumNextCallsPerQuery"));
        Assert.Equal(2, Number(report, "nextBatchCapacity"));
        Assert.Equal(20, Number(report, "maximumPollSeconds"));
        Assert.Equal(120, Number(report, "maximumWindowSeconds"));
    }

    private static void AssertPublicationFailure(CommandFixture fixture, bool control)
    {
        Assert.Equal(!control, fixture.ControlPublished);
        Assert.Equal(control, fixture.EventPublished);
        using var document = JsonDocument.Parse(fixture.EventBytes);
        var report = document.RootElement;
        Assert.Equal(control ? 1 : 0, Number(report, "status"));
        Assert.Equal(control ? 1 : 0, Number(report, "controlStatus"));
        Assert.Equal(control ? 1 : 0, Number(report, "reportError"));
        Assert.Equal((int)(control ? CrashCorrelationReason.Publication :
            CrashCorrelationReason.None), Number(report, "reason"));
        AssertClosedClaims(report);
        AssertEventProof(report, 3, true);
        AssertEventBounds(report);
        AssertPublicationSummaries(fixture, control);
    }

    private static void AssertPublicationSummaries(CommandFixture fixture, bool control)
    {
        Assert.Contains(FormattableString.Invariant(
            $"HERMES_CRASH_CONTROL v=1 status={(control ? 1 : 0)} exit={CrashExit} error=0 cleanupError=0 reportError={(control ? 1 : 0)} nativeAcceptance=0\n"),
            fixture.Summaries);
        Assert.Contains(FormattableString.Invariant(
            $"HERMES_CRASH_EVENT_CONTROL v=1 status=1 reason={(int)CrashCorrelationReason.Publication} reportError=1 originalGatePassed=0 nativeAcceptance=0\n"),
            fixture.Summaries);
    }

    private static void ChangeFinalReceipt(CommandFixture fixture, int change) =>
        fixture.Receipt.BeforeRead = () =>
        {
            if (!fixture.ChildDisposed)
                return;
            (fixture.Receipt.SourceHash, fixture.Receipt.Json) = change switch
            {
                0 => (new string('c', 64), Metadata),
                1 => (Hash, Metadata + " "),
                _ => (Hash, "{}")
            };
        };

    private static void RefuseFences(
        CommandFixture fixture, IReadOnlyList<Exception> errors, bool afterQuery = false)
    {
        var failures = new Queue<Exception>(errors);
        fixture.Receipt.BeforeRead = () =>
        {
            if (fixture.InCallback && (!afterQuery || fixture.Requests.Count > 0))
                throw failures.Dequeue();
        };
    }

    private static void CancelThenRefuseFinalFence(
        CommandFixture fixture, CancellationTokenSource cancellation, Exception error) =>
        fixture.Receipt.BeforeRead = () =>
        {
            if (!fixture.InCallback)
                return;
            if (cancellation.IsCancellationRequested)
                throw error;
            cancellation.Cancel();
        };

    private static void AssertFenceFailures(
        AggregateException failure, Exception first, Exception last)
    {
        var errors = failure.Flatten().InnerExceptions;
        Assert.Equal(2, errors.Count);
        Assert.Same(first, errors[0]);
        Assert.Same(last, errors[1]);
    }

    private static void AssertCoordinatorEscape(
        CommandFixture fixture, IReadOnlyList<Exception> errors)
    {
        Assert.True(fixture.ChildAdopted);
        Assert.True(fixture.ChildDisposed && fixture.Receipt.Closed);
        Assert.False(fixture.ControlPublished || fixture.EventPublished);
        Assert.Empty(fixture.ControlBytes);
        Assert.Empty(fixture.EventBytes);
        Assert.DoesNotContain(Call.ControlReport, fixture.Calls);
        Assert.DoesNotContain(Call.EventReport, fixture.Calls);
        Assert.DoesNotContain(Call.ControlSummary, fixture.Calls);
        Assert.DoesNotContain(Call.EventSummary, fixture.Calls);
        Assert.NotEmpty(fixture.Summaries);
        AssertSafeDiagnostics(fixture, errors);
    }

    private static void AssertUnexpectedCleanup(
        CommandFixture fixture, IReadOnlyList<Exception> errors)
    {
        fixture.AssertOwnedCleanup();
        Assert.Empty(fixture.ControlBytes);
        Assert.Empty(fixture.EventBytes);
        Assert.DoesNotContain(Call.ControlReport, fixture.Calls);
        Assert.DoesNotContain(Call.EventReport, fixture.Calls);
        Assert.Contains(fixture.Summaries, text => text.StartsWith(
            FormattableString.Invariant(
                $"HERMES_CRASH_EVENT_OBSERVATION v=1 reason={(int)CrashCorrelationReason.Unexpected} "),
            StringComparison.Ordinal));
        AssertSafeDiagnostics(fixture, errors);
    }

    private static void AssertSafeDiagnostics(
        CommandFixture fixture, IReadOnlyList<Exception> errors) =>
        Assert.All(fixture.Summaries, text =>
        {
            Assert.DoesNotContain(Image, text);
            Assert.All(errors, error => Assert.DoesNotContain(error.Message, text));
        });

    private static void AssertCommandQueryFences(IReadOnlyList<Call> calls)
    {
        foreach (var index in Enumerable.Range(0, calls.Count)
            .Where(index => calls[index] == Call.Query))
        {
            Assert.Equal<Call>([Call.Recheck, Call.Receipt], calls.Skip(index - 2).Take(2));
            Assert.Equal<Call>([Call.Recheck, Call.Receipt], calls.Skip(index + 1).Take(2));
        }
    }

    private static void AssertScalar(JsonProperty property) =>
        Assert.True(property.Value.ValueKind == JsonValueKind.Number ||
            (property.Name is "metadataSha256" or "schemaSourceSha256" &&
                property.Value.GetString() is { } hash && CrashControl.IsHash(hash)));

    private static void AssertClosedClaims(JsonElement report)
    {
        Assert.Equal(0, report.GetProperty("nativeAcceptance").GetInt32());
        Assert.Equal(0, report.GetProperty("originalGatePassed").GetInt32());
        Assert.Equal(0, report.GetProperty("globalLogUniqueness").GetInt32());
        Assert.Equal(0, report.GetProperty("independentPublicationVerified").GetInt32());
        Assert.Equal(1, report.GetProperty("exactCandidates").GetInt32());
        Assert.Equal(0, report.GetProperty("birthMinusOneCandidates").GetInt32());
        Assert.Equal(0, report.GetProperty("birthPlusOneCandidates").GetInt32());
        Assert.Equal(3, report.GetProperty("completedQueryEnumerations").GetInt32());
    }

    private static CrashResult PassedControl() => new()
    {
        Started = true, Ready = true, Identity = true, ArmAttempted = true,
        Armed = true, Exited = true, Held = true, Cleanup = true,
        WaitSettled = true, DrainsSettled = true, WriteSettled = true,
        CallbackSettled = true, StdoutComplete = true, StderrComplete = true,
        Exit = CrashExit, ImageSha256 = Hash, SourceSha256 = Hash
    };

    private sealed class Clock : TimeProvider
    {
        private readonly DateTimeOffset _start =
            new(DateTime.FromFileTimeUtc(Terminal.ExitFileTime));

        internal TimeSpan Elapsed { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => _start + Elapsed;
        internal void Advance(TimeSpan delay) => Elapsed += delay;
    }

    private sealed class Receipt(List<Call> calls, string json) : ICrashSchemaReceipt
    {
        internal string Json { get; set; } = json;
        internal string SourceHash { get; set; } = Hash;
        internal bool Closed { get; private set; }
        internal bool CleanupFailure { get; set; }
        internal Action? BeforeRead { get; set; }

        public CrashSchemaSnapshot Read()
        {
            Assert.False(Closed);
            calls.Add(Call.Receipt);
            BeforeRead?.Invoke();
            return new(Encoding.UTF8.GetBytes(Json), SourceHash);
        }

        public void Dispose()
        {
            calls.Add(Call.DisposeReceipt);
            Closed = true;
            if (CleanupFailure)
                throw new IOException("not-exported");
        }
    }

    private sealed class ObserverFixture : IDisposable
    {
        internal List<Call> Calls { get; } = [];
        internal List<CrashQueryRequest> Requests { get; } = [];
        internal List<TimeSpan> Delays { get; } = [];
        internal List<string> Summaries { get; } = [];
        internal Clock Clock { get; } = new();
        internal Receipt Receipt { get; }
        internal CrashObservation Scope { get; }
        internal CrashEventObserver Observer { get; }
        internal CrashIdentity Current { get; set; } = Terminal;
        internal Func<int, CrashQueryRequest, CrashQueryResult> QueryPolicy { get; set; } =
            (_, request) => Matching(request);

        internal ObserverFixture()
        {
            Receipt = new(Calls, Metadata);
            var admission = CrashEventSchema.Admit(Receipt.Read());
            Scope = new(Terminal with { ExitFileTime = 0, ExitCode = 259 }, CrashExit, ReadIdentity);
            Observer = new(Receipt, admission, new(Query, Clock, Delay), WriteSummary);
            Calls.Clear();
        }

        internal Task Run() => Run(CancellationToken.None);
        internal Task Run(CancellationToken cancellationToken) =>
            Observer.ObserveAsync(Scope, cancellationToken);

        private CrashIdentity ReadIdentity()
        {
            Calls.Add(Call.Recheck);
            Assert.False(Receipt.Closed);
            return Current;
        }

        private CrashQueryResult Query(CrashQueryRequest request, CrashQueryAdmission admission)
        {
            Assert.Equal(CrashQueryAdmission.CallerAttestsFixedProviderAndSchema, admission);
            Calls.Add(Call.Query);
            var index = Requests.Count;
            Requests.Add(request);
            return QueryPolicy(index, request);
        }

        private Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(Call.Delay);
            Delays.Add(delay);
            Clock.Advance(delay);
            return Task.CompletedTask;
        }

        private void WriteSummary(string text)
        {
            Calls.Add(Call.ObservationSummary);
            Summaries.Add(text);
        }

        internal void AssertQueryFences()
        {
            foreach (var index in Enumerable.Range(0, Calls.Count)
                .Where(index => Calls[index] == Call.Query))
            {
                Assert.Equal<Call>([Call.Recheck, Call.Receipt], Calls.Skip(index - 2).Take(2));
                Assert.Equal<Call>([Call.Recheck, Call.Receipt], Calls.Skip(index + 1).Take(2));
            }
        }

        public void Dispose()
        {
            Scope.Dispose();
            Receipt.Dispose();
        }
    }

    private sealed class CommandFixture
    {
        private readonly TaskCompletionSource<uint> _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly byte[] _ready = Encoding.ASCII.GetBytes(CrashControl.Ready);
        private int _offset;
        private bool _exited;
        private bool _inputClosed;
        private bool _outputClosed;
        private bool _errorClosed;
        private bool _outputEnded;
        private bool _errorEnded;
        private Task? _write;
        private Task<uint>? _wait;

        internal List<Call> Calls { get; } = [];
        internal List<CrashQueryRequest> Requests { get; } = [];
        internal List<string> Summaries { get; } = [];
        internal Clock Clock { get; } = new();
        internal Receipt Receipt { get; }
        internal bool GuardFailure { get; init; }
        internal bool RefuseReceiptDuringCallback { get; init; }
        internal bool ReceiptCleanupFailure { get; init; }
        internal bool DeferExit { get; init; }
        internal Exception? StartError { get; init; }
        internal Exception? ChildDisposalError { get; init; }
        internal Exception? ControlPublicationError { get; init; }
        internal Exception? EventPublicationError { get; init; }
        internal Func<CrashQueryRequest, CrashQueryResult> QueryPolicy { get; init; } = Matching;
        internal bool ChildAdopted { get; private set; }
        internal bool ChildDisposed { get; private set; }
        internal bool ControlPublished { get; private set; }
        internal bool EventPublished { get; private set; }
        internal byte[] ControlBytes { get; private set; } = [];
        internal byte[] EventBytes { get; private set; } = [];
        internal bool InCallback => _exited && !ChildDisposed;
        internal int CallbackReceiptReads => Calls.Skip(Calls.IndexOf(Call.Arm) + 1)
            .TakeWhile(call => call != Call.DisposeChild).Count(call => call == Call.Receipt);

        internal CommandFixture(string json)
        {
            Receipt = new(Calls, json);
            Receipt.BeforeRead = () =>
            {
                if (RefuseReceiptDuringCallback && _exited)
                    Receipt.Json = "{}";
            };
        }

        internal CrashEventControlEffects Effects => new(
            Context, OpenReceipt, Start,
            new(Query, Clock, Delay), PublishControl, PublishEvent, Summary);

        internal Task<int> Run() => Run(CancellationToken.None);

        internal Task<int> Run(CancellationToken cancellationToken) =>
            CrashEventControl.DispatchAsync(
                [CrashEventControl.Command], Effects, cancellationToken);

        private void Context()
        {
            Calls.Add(Call.Context);
            if (GuardFailure)
                throw new InvalidOperationException("not-exported");
        }

        private ICrashSchemaReceipt OpenReceipt()
        {
            Receipt.CleanupFailure = ReceiptCleanupFailure;
            return Receipt;
        }

        private void Start(Action<CrashOwnedChild> adopt, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(Call.Start);
            adopt(new(new(ReadOutput), new(ReadError), Arm,
                () => _inputClosed = true, () => _outputClosed = true,
                () => _errorClosed = true, Identity, Wait, Exit, DisposeChild));
            ChildAdopted = true;
            if (StartError is { } error)
                throw error;
        }

        private CrashRead ReadOutput(Memory<byte> destination)
        {
            if (_offset == _ready.Length)
            {
                _outputEnded = _exited;
                return new(_exited ? CrashReadKind.End : CrashReadKind.Pending);
            }
            var count = Math.Min(destination.Length, _ready.Length - _offset);
            _ready.AsMemory(_offset, count).CopyTo(destination);
            _offset += count;
            return new(CrashReadKind.Data, count);
        }

        private Task Arm(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(Call.Arm);
            Exit();
            return _write = Task.CompletedTask;
        }

        private CrashIdentity Identity()
        {
            Calls.Add(Call.Recheck);
            Assert.False(ChildDisposed);
            return new(new(42, Birth),
                Image,
                Hash, Hash, _exited ? Terminal.ExitFileTime : 0, _exited ? CrashExit : 259);
        }

        private CrashRead ReadError(Memory<byte> unused)
        {
            _errorEnded = _exited;
            return new(_exited ? CrashReadKind.End : CrashReadKind.Pending);
        }

        private Task<uint> Wait(CancellationToken cancellationToken) =>
            _wait = _exit.Task.WaitAsync(cancellationToken);

        private void Exit()
        {
            _exited = true;
            if (!DeferExit)
                CompleteExit();
        }

        internal void CompleteExit() => _exit.TrySetResult(CrashExit);

        private void DisposeChild()
        {
            Calls.Add(Call.DisposeChild);
            ChildDisposed = true;
            if (ChildDisposalError is { } error)
                throw error;
        }

        private CrashQueryResult Query(
            CrashQueryRequest request, CrashQueryAdmission admission)
        {
            Assert.Equal(CrashQueryAdmission.CallerAttestsFixedProviderAndSchema, admission);
            Assert.False(ChildDisposed || Receipt.Closed);
            Calls.Add(Call.Query);
            Requests.Add(request);
            return QueryPolicy(request);
        }

        private Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(Call.Delay);
            Clock.Advance(delay);
            return Task.CompletedTask;
        }

        private void PublishControl(byte[] bytes)
        {
            Assert.Equal(Calls.Contains(Call.Start), ChildDisposed);
            Assert.Equal(Calls.Contains(Call.Receipt), Receipt.Closed);
            Calls.Add(Call.ControlReport);
            // Retain attempted bytes; Published records whether the write succeeded.
            ControlBytes = bytes;
            if (ControlPublicationError is { } error)
                throw error;
            ControlPublished = true;
        }

        private void PublishEvent(byte[] bytes)
        {
            Assert.Equal(Calls.Contains(Call.Start), ChildDisposed);
            Assert.Equal(Calls.Contains(Call.Receipt), Receipt.Closed);
            Calls.Add(Call.EventReport);
            EventBytes = bytes;
            if (EventPublicationError is { } error)
                throw error;
            EventPublished = true;
        }

        internal void AssertReapPending()
        {
            Assert.True(ChildAdopted && _exited);
            Assert.True(_wait is { IsCompleted: false });
            Assert.False(_exit.Task.IsCompleted);
            Assert.False(ChildDisposed || Receipt.Closed);
            Assert.DoesNotContain(Call.DisposeChild, Calls);
            Assert.DoesNotContain(Call.DisposeReceipt, Calls);
        }

        internal void AssertAdoptedStartCleanup()
        {
            Assert.True(ChildAdopted && ChildDisposed && Receipt.Closed);
            Assert.True(_inputClosed && _outputClosed && _errorClosed);
            Assert.False(_outputEnded || _errorEnded);
            Assert.Equal(0, _offset);
            Assert.Null(_write);
            Assert.True(_wait is { IsCompletedSuccessfully: true });
            Assert.True(_exit.Task.IsCompletedSuccessfully);
            Assert.Equal(CrashExit, _exit.Task.GetAwaiter().GetResult());
            Assert.Equal(1, Calls.Count(call => call == Call.Start));
            Assert.Equal(1, Calls.Count(call => call == Call.DisposeChild));
            Assert.Equal(1, Calls.Count(call => call == Call.DisposeReceipt));
            Assert.True(Calls.IndexOf(Call.DisposeChild) < Calls.IndexOf(Call.DisposeReceipt));
            Assert.DoesNotContain(Call.Recheck, Calls);
        }

        internal void AssertOwnedCleanup()
        {
            Assert.True(ChildDisposed && Receipt.Closed);
            Assert.True(_inputClosed && _outputClosed && _errorClosed);
            Assert.True(_outputEnded && _errorEnded);
            Assert.Equal(_ready.Length, _offset);
            Assert.True(_write is { IsCompletedSuccessfully: true });
            Assert.True(_wait is { IsCompletedSuccessfully: true });
            Assert.Equal(CrashExit, _exit.Task.GetAwaiter().GetResult());
            Assert.Equal(1, Calls.Count(call => call == Call.Arm));
            Assert.Equal(1, Calls.Count(call => call == Call.DisposeChild));
            Assert.Equal(1, Calls.Count(call => call == Call.DisposeReceipt));
            Assert.True(Calls.IndexOf(Call.ObservationSummary) < Calls.IndexOf(Call.DisposeChild));
            Assert.True(Calls.IndexOf(Call.DisposeChild) < Calls.IndexOf(Call.DisposeReceipt));
            Assert.DoesNotContain(Call.Recheck, Calls.Skip(Calls.IndexOf(Call.DisposeChild) + 1));
            Assert.DoesNotContain(Call.Query, Calls.Skip(Calls.IndexOf(Call.DisposeChild) + 1));
        }

        private void Summary(string text)
        {
            Summaries.Add(text);
            Calls.Add(text switch
            {
                var value when value.StartsWith("HERMES_CRASH_EVENT_OBSERVATION ", StringComparison.Ordinal)
                    => Call.ObservationSummary,
                var value when value.StartsWith("HERMES_CRASH_EVENT_CONTROL ", StringComparison.Ordinal)
                    => Call.EventSummary,
                _ => Call.ControlSummary
            });
        }
    }

    private sealed record Batch(
        bool Success, int Error, uint Count, nint First = 0, nint Second = 0);

    private sealed class BatchApi(IEnumerable<Batch> batches) : ICrashEventQueryApi
    {
        private readonly Queue<Batch> _batches = new(batches);
        private readonly HashSet<nint> _closePending = [];
        private readonly int _thread = Environment.CurrentManagedThreadId;
        internal List<nint> CloseAttempts { get; } = [];
        internal int NextCalls { get; private set; }
        internal int ReadCalls { get; private set; }
        internal bool FailClose { get; init; }
        internal bool ThrowOnSecondNext { get; init; }
        internal ArithmeticException Unexpected { get; } = new("not-exported");

        public CrashQueryOpenCall Open(
            nint session, string channel, string expression, uint flags)
        {
            CheckThread();
            Assert.Equal((nint)0, session);
            Assert.Equal(CrashEventQuery.Channel, channel);
            Assert.Equal(CrashEventQuery.QueryFlags, flags);
            _closePending.Add(11);
            return new(11, 0);
        }

        public CrashQueryNextCall Next(
            nint query, nint[] events, uint timeoutMilliseconds, uint flags)
        {
            CheckThread();
            Assert.Equal((nint)11, query);
            Assert.Equal(2, events.Length);
            Assert.All(events, handle => Assert.Equal((nint)0, handle));
            Assert.Equal(0u, timeoutMilliseconds);
            Assert.Equal(0u, flags);
            var batch = _batches.Dequeue();
            events[0] = batch.First;
            events[1] = batch.Second;
            foreach (var handle in events.Where(handle => handle != 0 && handle != -1))
                _closePending.Add(handle);
            NextCalls++;
            if (ThrowOnSecondNext && NextCalls == 2)
                throw Unexpected;
            return new(batch.Success, batch.Error, batch.Count);
        }

        public CrashEventRead Read(nint borrowedEvent, CrashEventSubject expected)
        {
            CheckThread();
            Assert.Contains((nint)11, _closePending);
            Assert.Contains(borrowedEvent, _closePending);
            Assert.Equal((ulong)Birth, expected.CreationFileTime);
            ReadCalls++;
            return new CrashEventMatched(new("80131623", "private.dll", @"C:\private\private.dll"));
        }

        public CrashQueryCloseCall Close(nint ownedHandle)
        {
            CheckThread();
            Assert.True(_closePending.Remove(ownedHandle));
            Assert.DoesNotContain(ownedHandle, CloseAttempts);
            CloseAttempts.Add(ownedHandle);
            return FailClose ? new(false, 5) : new(true, 0);
        }

        private void CheckThread() =>
            Assert.Equal(_thread, Environment.CurrentManagedThreadId);

        internal void AssertSettled()
        {
            Assert.Empty(_closePending);
            Assert.InRange(NextCalls, 1, 2);
            Assert.InRange(ReadCalls, 0, 1);
        }
    }
}
