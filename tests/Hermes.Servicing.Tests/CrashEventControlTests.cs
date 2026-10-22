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
    private const string DiagnosticPrefix = "HERMES_CRASH_EVENT_DIAGNOSTIC ";
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
        ControlReport, EventReport, DisposeReceipt, DisposeChild, DiagnosticSummary
    }

    [Fact]
    public async Task Diagnostic_distinguishes_expiry_without_any_query()
    {
        using var fixture = new ObserverFixture();
        fixture.Clock.Advance(TimeSpan.FromSeconds(21));
        await Assert.ThrowsAsync<CrashCorrelationException>(fixture.Run);
        var row = Metrics(fixture.Observer);
        Assert.Empty(fixture.Requests);
        Assert.Equal(0L, row["polls"]);
        Assert.Equal(0L, row["attempts"]);
        Assert.Equal(0L, row["returns"]);
        Assert.Equal(0L, row["last"]);
        Assert.Equal(-1000L, row["entryMs"]);
        Assert.Equal((long)CrashEventTelemetry.MissingSlack, row["queryMs"]);
        Assert.Equal((long)CrashEventExpiry.Window, row["expiry"]);
        Assert.Equal((long)CrashEventExpiryLocation.PollEntry, row["location"]);
        Assert.Equal(0L, row["rejected"]);
    }

    [Fact]
    public async Task Diagnostic_counts_multiple_short_clean_empty_returns()
    {
        using var fixture = new ObserverFixture();
        fixture.Clock.Advance(TimeSpan.FromSeconds(19));
        fixture.QueryPolicy = (_, _) => ShortEmpty(fixture.Clock);
        await Assert.ThrowsAsync<CrashCorrelationException>(fixture.Run);
        var row = Metrics(fixture.Observer);
        Assert.Equal(4L, row["polls"]);
        Assert.Equal(4L, row["attempts"]);
        Assert.Equal(4L, row["returns"]);
        Assert.Equal(4L, row["empty"]);
        Assert.Equal(1L, row["last"]);
        Assert.Equal(0L, row["failure"]);
        Assert.Equal(1000L, row["entryMs"]);
        Assert.Equal(247L, row["queryMs"]);
        Assert.Equal(996000L, row["dUs"]);
        Assert.Equal(250000L, row["dMaxUs"]);
        Assert.Equal(0L, row["rejected"]);
    }

    private static CrashQueryResult ShortEmpty(Clock clock)
    {
        clock.Advance(TimeSpan.FromMilliseconds(1));
        return new CrashQueryEmpty();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 3)]
    [InlineData(2, 8)]
    [InlineData(3, 2)]
    [InlineData(4, 4)]
    [InlineData(5, 5)]
    [InlineData(6, 6)]
    [InlineData(7, 7)]
    public async Task Returned_category_is_recorded_before_expiring_postfence(
        int outcome, int category)
    {
        using var fixture = new ObserverFixture();
        fixture.QueryPolicy = (_, _) =>
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(21));
            return DiagnosticOutcome(outcome);
        };
        await Assert.ThrowsAsync<CrashCorrelationException>(fixture.Run);
        AssertRejectedReturn(fixture, outcome, category);
    }

    private static CrashQueryResult DiagnosticOutcome(int outcome) => outcome switch
    {
        0 => new CrashQueryEmpty(),
        1 => Match,
        2 => CrashEventQuery.Failure(
            CrashEventFailureKind.Unavailable, CrashQueryReason.Open, 1460),
        3 => new CrashQueryAmbiguous(),
        4 => new CrashQueryCompleted(
            new CrashEventMismatch(CrashEventReason.CreationTimeMismatch)),
        5 => new CrashQueryCompleted(CrashEventValues.Failure(
            CrashEventFailureKind.Unavailable, CrashEventReason.Render)),
        6 => new CrashQueryVisited(
            new CrashEventMismatch(CrashEventReason.CreationTimeMismatch)),
        _ => new CrashQueryConstructed(PrivateFailure)
    };

    private static void AssertRejectedReturn(
        ObserverFixture fixture, int outcome, int category)
    {
        var row = Metrics(fixture.Observer);
        Assert.Single(fixture.Requests);
        Assert.False(fixture.Observer.Proof.Verified);
        Assert.Equal(CrashCorrelationReason.WindowExpired, fixture.Observer.Proof.Reason);
        Assert.Equal(1L, row["attempts"]);
        Assert.Equal(1L, row["returns"]);
        Assert.Equal(outcome == 0 ? 1L : 0L, row["empty"]);
        Assert.Equal((long)category, row["last"]);
        Assert.Equal(1L, row["rejected"]);
        Assert.Equal((long)CrashEventExpiry.Window, row["expiry"]);
        Assert.Equal((long)CrashEventExpiryLocation.AfterQuery, row["location"]);
        AssertFirstReturnedFault(row, outcome);
    }

    private static void AssertFirstReturnedFault(
        IReadOnlyDictionary<string, long> row, int outcome)
    {
        var (kind, code, native) = outcome switch
        {
            2 => (1L, (long)CrashQueryReason.Open + 1, 1460L),
            5 => (2L, (long)CrashEventReason.Render + 1, -1L),
            _ => (0L, 0L, -1L)
        };
        Assert.Equal(kind, row["failure"]);
        Assert.Equal(code, row["code"]);
        Assert.Equal(native, row["native"]);
    }

    [Fact]
    public async Task Prequery_fence_time_can_expire_without_a_query_attempt()
    {
        using var fixture = new ObserverFixture();
        var reads = 0;
        fixture.Receipt.BeforeRead = () =>
        {
            if (++reads == 2)
                fixture.Clock.Advance(TimeSpan.FromSeconds(21));
        };
        await Assert.ThrowsAsync<CrashCorrelationException>(fixture.Run);
        var row = Metrics(fixture.Observer);
        Assert.Empty(fixture.Requests);
        Assert.Equal(1L, row["polls"]);
        Assert.Equal(0L, row["attempts"]);
        Assert.Equal(21000000L, row["fUs"]);
        Assert.Equal(21000000L, row["fMaxUs"]);
        Assert.Equal((long)CrashEventExpiryLocation.BeforeQuery, row["location"]);
        Assert.Equal((long)CrashEventTelemetry.MissingSlack, row["queryMs"]);
    }

    [Fact]
    public async Task Monotonic_budget_expiry_is_distinct_from_wall_window_expiry()
    {
        using var fixture = new ObserverFixture();
        fixture.Clock.Advance(TimeSpan.FromSeconds(-5));
        fixture.QueryPolicy = (_, _) =>
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(20));
            return Match;
        };
        await Assert.ThrowsAsync<CrashCorrelationException>(fixture.Run);
        var row = Metrics(fixture.Observer);
        Assert.Equal(CrashCorrelationReason.NoDelivery, fixture.Observer.Proof.Reason);
        Assert.Equal(25000L, row["entryMs"]);
        Assert.Equal((long)CrashEventExpiry.PollBudget, row["expiry"]);
        Assert.Equal((long)CrashEventExpiryLocation.AfterQuery, row["location"]);
        Assert.Equal(3L, row["last"]);
        Assert.Equal(1L, row["rejected"]);
    }

    [Fact]
    public async Task Returned_result_is_also_recorded_before_a_nonexpiry_fence_failure()
    {
        using var fixture = new ObserverFixture();
        fixture.QueryPolicy = (_, _) =>
        {
            fixture.Current = Terminal with { Process = new(43, Birth) };
            return Match;
        };
        var failure = await Assert.ThrowsAsync<AggregateException>(fixture.Run);
        AssertProcessMismatchFences(fixture, failure);
        var row = Metrics(fixture.Observer);
        Assert.Equal(3L, row["last"]);
        Assert.Equal(1L, row["returns"]);
        Assert.Equal(1L, row["rejected"]);
        Assert.Equal(0L, row["expiry"]);
        Assert.Equal(0L, row["failure"]);
    }

    private static void AssertProcessMismatchFences(
        ObserverFixture fixture, AggregateException failure)
    {
        Assert.Collection(failure.InnerExceptions,
            AssertProcessMismatch, AssertProcessMismatch);
        Assert.NotSame(failure.InnerExceptions[0], failure.InnerExceptions[1]);
        Assert.Single(fixture.Requests);
        Assert.Equal(5, fixture.Calls.Count(call => call == Call.Recheck));
        Assert.Equal(CrashCorrelationReason.Scope, fixture.Observer.Proof.Reason);
        Assert.False(fixture.Observer.Proof.Matched || fixture.Observer.Proof.Verified);
    }

    private static void AssertProcessMismatch(Exception error)
    {
        var failure = Assert.IsType<CrashExitException>(error);
        Assert.Equal(CrashExitCheck.IdentityProcess, failure.Check);
        Assert.Equal(0U, failure.NativeError);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public async Task Cancelled_delay_keeps_its_exception_and_measures_elapsed_time()
    {
        using var fixture = new ObserverFixture();
        var error = new OperationCanceledException(PrivateFailure);
        fixture.DelayError = error;
        fixture.QueryPolicy = (_, _) => new CrashQueryEmpty();
        Assert.Same(error,
            await Assert.ThrowsAsync<OperationCanceledException>(fixture.Run));
        var row = Metrics(fixture.Observer);
        Assert.Equal(250000L, row["dUs"]);
        Assert.Equal(250000L, row["dMaxUs"]);
        Assert.Equal(1L, row["empty"]);
        Assert.Equal(0L, row["failure"]);
        Assert.Equal(0L, row["rejected"]);
    }

    [Fact]
    public void Completed_native_policy_measures_both_next_calls_and_all_cleanup()
    {
        var clock = new Clock();
        var telemetry = new CrashEventTelemetry(clock);
        var api = TimedBatch(clock);
        Assert.IsType<CrashQueryCompleted>(Enumerate(api, telemetry));
        Assert.Equal((1000, 1000), telemetry.Metric(CrashEventPhase.EvtQuery));
        Assert.Equal((4000, 2000), telemetry.Metric(CrashEventPhase.EvtNext));
        Assert.Equal((3000, 3000), telemetry.Metric(CrashEventPhase.Reader));
        Assert.Equal((8000, 4000), telemetry.Metric(CrashEventPhase.Close));
        Assert.Equal<nint>([21, 11], api.CloseAttempts);
        api.AssertSettled();
    }

    [Fact]
    public void Failed_empty_cleanup_is_a_query_failure_not_a_clean_empty_return()
    {
        var clock = new Clock();
        var telemetry = new CrashEventTelemetry(clock);
        var api = new BatchApi([new(false, 259, 0)])
            { FailClose = true, OnPhase = phase => AdvancePhase(clock, phase) };
        telemetry.Returned(Enumerate(api, telemetry));
        var row = Metrics(telemetry.Summary(new()));
        Assert.Equal(8L, row["last"]);
        Assert.Equal(0L, row["empty"]);
        Assert.Equal(1L, row["failure"]);
        Assert.Equal((long)CrashQueryReason.QueryClose + 1, row["code"]);
        Assert.Equal(5L, row["native"]);
        Assert.Equal((4000, 4000), telemetry.Metric(CrashEventPhase.Close));
        api.AssertSettled();
    }

    [Fact]
    public void First_reader_failure_survives_later_native_cleanup_failures()
    {
        var clock = new Clock();
        var telemetry = new CrashEventTelemetry(clock);
        var api = new BatchApi([new(true, 0, 1, 21), new(false, 259, 0)])
        {
            FailClose = true, OnPhase = phase => AdvancePhase(clock, phase),
            Reading = CrashEventValues.Failure(
                CrashEventFailureKind.Unavailable, CrashEventReason.Render)
        };
        var result = Assert.IsType<CrashQueryFailure>(Enumerate(api, telemetry));
        Assert.Equal(2, result.Faults.Length);
        telemetry.Returned(result);
        var row = Metrics(telemetry.Summary(new()));
        Assert.Equal(2L, row["failure"]);
        Assert.Equal((long)CrashEventReason.Render + 1, row["code"]);
        Assert.Equal(-1L, row["native"]);
        Assert.Equal(8L, row["last"]);
        api.AssertSettled();
    }

    [Fact]
    public void Unexpected_unreturned_next_preserves_exception_and_times_all_closes()
    {
        var clock = new Clock();
        var telemetry = new CrashEventTelemetry(clock);
        var api = new BatchApi([new(true, 0, 1, 21), new(true, 0, 1, 22)])
        {
            ThrowOnSecondNext = true,
            OnPhase = phase => AdvancePhase(clock, phase)
        };
        Assert.Same(api.Unexpected,
            Assert.Throws<ArithmeticException>(() => Enumerate(api, telemetry)));
        Assert.Equal((4000, 2000), telemetry.Metric(CrashEventPhase.EvtNext));
        Assert.Equal((12000, 4000), telemetry.Metric(CrashEventPhase.Close));
        Assert.Equal<nint>([22, 21, 11], api.CloseAttempts);
        var row = Metrics(telemetry.Summary(new()));
        Assert.Equal(0L, row["returns"]);
        Assert.Equal(4L, row["failure"]);
        api.AssertSettled();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_reader_cancellation_retains_original_and_cleanup_faults(bool cleanup)
    {
        var clock = new Clock();
        var telemetry = new CrashEventTelemetry(clock);
        var cancelled = new OperationCanceledException(PrivateFailure);
        var api = new BatchApi([new(true, 0, 1, 21)])
        {
            ReaderError = cancelled, FailClose = cleanup,
            OnPhase = phase => AdvancePhase(clock, phase)
        };
        AssertNativeCancellation(api, telemetry, cancelled, cleanup);
        Assert.Equal((3000, 3000), telemetry.Metric(CrashEventPhase.Reader));
        Assert.Equal((8000, 4000), telemetry.Metric(CrashEventPhase.Close));
        Assert.Equal(5L, Metrics(telemetry.Summary(new()))["failure"]);
        api.AssertSettled();
    }

    private static void AssertNativeCancellation(
        BatchApi api, CrashEventTelemetry telemetry,
        OperationCanceledException cancelled, bool cleanup)
    {
        if (!cleanup)
        {
            Assert.Same(cancelled,
                Assert.Throws<OperationCanceledException>(() => Enumerate(api, telemetry)));
            return;
        }
        var error = Assert.Throws<AggregateException>(() => Enumerate(api, telemetry));
        Assert.Same(cancelled, error.InnerExceptions[0]);
        Assert.Equal<CrashQueryReason>(
            [CrashQueryReason.EventClose, CrashQueryReason.QueryClose],
            error.InnerExceptions.OfType<CrashQueryException>()
                .SelectMany(failure => failure.Faults).Select(fault => fault.Reason));
    }

    [Fact]
    public void Timing_clock_fault_cannot_prevent_owned_close_or_hide_its_failure()
    {
        var clock = new Clock();
        var telemetry = new CrashEventTelemetry(clock);
        var timing = new ArithmeticException(PrivateFailure);
        var api = new BatchApi([new(false, 259, 0)])
        {
            FailClose = true,
            OnPhase = _ => clock.TimestampError = timing
        };
        var error = Assert.Throws<CrashEventSamplingException>(() => Enumerate(api, telemetry));
        var errors = error.Flatten().InnerExceptions;
        Assert.Single(errors, item => ReferenceEquals(timing, item));
        var close = Assert.Single(errors.OfType<CrashQueryException>());
        Assert.Equal(new CrashQueryFault(CrashEventFailureKind.Refused,
            CrashQueryReason.QueryClose, 5), Assert.Single(close.Faults));
        Assert.Equal<nint>([11], api.CloseAttempts);
        Assert.Equal(1, api.NextCalls);
        api.AssertSettled();
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public void Native_next_error_survives_start_and_end_sampling_faults(
        bool second, int points)
    {
        var clock = new Clock();
        var telemetry = new CrashEventTelemetry(clock);
        var first = new ArithmeticException(PrivateFailure);
        var last = new IOException(PrivateFailure);
        FaultSamples(clock, second ? 7 : 3, points, first, last);
        var api = NativeErrorBatch(clock, second);
        var error = Assert.Throws<CrashEventSamplingException>(() => Enumerate(api, telemetry));
        AssertNativeErrorAndSamples(error, points, first, last);
        AssertNativeErrorMetrics(api, telemetry, second);
        api.AssertSettled();
    }

    private static BatchApi NativeErrorBatch(Clock clock, bool second) =>
        new(second
            ? [new(true, 0, 1, 21), new(false, 1460, 0)]
            : [new(false, 1460, 0)])
        {
            OnPhase = phase => AdvancePhase(clock, phase)
        };

    private static void FaultSamples(
        Clock clock, int start, int points, Exception first, Exception last) =>
        clock.TimestampFailure = read => (read - start, points) switch
        {
            (0, 1 or 3) => first,
            (1, 2 or 3) => last,
            _ => null
        };

    private static void AssertSamplingPresence(
        IReadOnlyList<Exception> errors, int points, Exception first, Exception last)
    {
        Assert.Equal((points & 1) == 0 ? 0 : 1,
            errors.Count(error => ReferenceEquals(first, error)));
        Assert.Equal((points & 2) == 0 ? 0 : 1,
            errors.Count(error => ReferenceEquals(last, error)));
        Assert.Equal(errors.Count, errors.Distinct(ReferenceEqualityComparer.Instance).Count());
    }

    private static void AssertNativeErrorAndSamples(
        CrashEventSamplingException error, int points, Exception first, Exception last)
    {
        var errors = error.Flatten().InnerExceptions;
        var query = Assert.IsType<CrashQueryException>(
            Assert.Single(errors, item => item is CrashQueryException));
        Assert.Equal(new CrashQueryFault(CrashEventFailureKind.Unavailable,
            CrashQueryReason.Next, 1460), Assert.Single(query.Faults));
        Assert.Equal(1 + ((points & 1) == 0 ? 0 : 1) +
            ((points & 2) == 0 ? 0 : 1), errors.Count);
        AssertSamplingPresence(errors, points, first, last);
    }

    private static void AssertNativeErrorMetrics(
        BatchApi api, CrashEventTelemetry telemetry, bool second)
    {
        var row = Metrics(telemetry.Summary(new()));
        Assert.Equal(1L, row["failure"]);
        Assert.Equal((long)CrashQueryReason.Next + 1, row["code"]);
        Assert.Equal(1460L, row["native"]);
        Assert.Equal(second ? (2000, 2000) : (0, 0),
            telemetry.Metric(CrashEventPhase.EvtNext));
        Assert.Equal(second ? (3000, 3000) : (0, 0),
            telemetry.Metric(CrashEventPhase.Reader));
        Assert.Equal(second ? (8000, 4000) : (4000, 4000),
            telemetry.Metric(CrashEventPhase.Close));
        Assert.Equal<nint>(second ? [21, 11] : [11], api.CloseAttempts);
        Assert.Equal(second ? 2 : 1, api.NextCalls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Unexpected_second_next_and_each_sampling_fault_survive_cleanup(int points)
    {
        var clock = new Clock();
        var telemetry = new CrashEventTelemetry(clock);
        var first = new ArithmeticException(PrivateFailure);
        var last = new IOException(PrivateFailure);
        FaultSamples(clock, 7, points, first, last);
        var api = new BatchApi([new(true, 0, 1, 21), new(true, 0, 1, 22)])
        {
            ThrowOnSecondNext = true,
            OnPhase = phase => AdvancePhase(clock, phase)
        };
        var error = Assert.Throws<CrashEventSamplingException>(() => Enumerate(api, telemetry));
        Assert.Same(api.Unexpected, error.AuthorityFailure);
        AssertSamplingPresence(error.Flatten().InnerExceptions, points, first, last);
        Assert.Equal((2000, 2000), telemetry.Metric(CrashEventPhase.EvtNext));
        Assert.Equal((12000, 4000), telemetry.Metric(CrashEventPhase.Close));
        Assert.Equal<nint>([22, 21, 11], api.CloseAttempts);
        api.AssertSettled();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Sampling_faults_cannot_turn_a_completed_native_query_into_success(int points)
    {
        var clock = new Clock();
        var telemetry = new CrashEventTelemetry(clock);
        var first = new ArithmeticException(PrivateFailure);
        var last = new IOException(PrivateFailure);
        FaultSamples(clock, 3, points, first, last);
        var api = TimedBatch(clock);
        var error = Assert.Throws<CrashEventSamplingException>(() => Enumerate(api, telemetry));
        Assert.Null(error.AuthorityFailure);
        var errors = error.Flatten().InnerExceptions;
        Assert.Equal(points == 3 ? 2 : 1, errors.Count);
        AssertSamplingPresence(errors, points, first, last);
        Assert.Equal((2000, 2000), telemetry.Metric(CrashEventPhase.EvtNext));
        Assert.Equal((3000, 3000), telemetry.Metric(CrashEventPhase.Reader));
        Assert.Equal((8000, 4000), telemetry.Metric(CrashEventPhase.Close));
        api.AssertSettled();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Entry_sampling_fault_does_not_preempt_initial_authority(bool cancel)
    {
        var timing = new IOException(PrivateFailure);
        var sampling = new Clock { UtcError = timing };
        using var fixture = new ObserverFixture(sampling);
        using var cancellation = new CancellationTokenSource();
        if (cancel)
            cancellation.Cancel();
        var error = await Assert.ThrowsAsync<CrashEventSamplingException>(
            () => fixture.Run(cancellation.Token));
        AssertEntrySampling(fixture, sampling, error, timing, cancel);
    }

    private static void AssertEntrySampling(
        ObserverFixture fixture, Clock sampling, CrashEventSamplingException error,
        Exception timing, bool cancelled)
    {
        var reason = cancelled
            ? CrashCorrelationReason.Cancelled : CrashCorrelationReason.Unexpected;
        Assert.Equal(reason, fixture.Observer.Proof.Reason);
        Assert.Equal(reason, CrashCorrelationErrors.Reason(error));
        var errors = error.Flatten().InnerExceptions;
        Assert.Single(errors, item => ReferenceEquals(timing, item));
        Assert.Equal(cancelled ? 2 : 0, errors.OfType<OperationCanceledException>().Count());
        Assert.Equal(cancelled ? 3 : 1, errors.Count);
        Assert.Equal(cancelled, error.AuthorityFailure is not null);
        Assert.Equal(cancelled ? 2 : 8, fixture.Calls.Count(call => call == Call.Receipt));
        Assert.Equal(1, sampling.UtcReads);
        Assert.Equal(cancelled ? 0 : 7, fixture.Clock.UtcReads);
        AssertSamplingQueryHistory(fixture, cancelled ? 0 : 3);
        Assert.Equal((long)CrashEventTelemetry.MissingSlack, Metrics(fixture.Observer)["entryMs"]);
        Assert.Equal(0L, Metrics(fixture.Observer)["rejected"]);
    }

    private static void AssertSamplingQueryHistory(ObserverFixture fixture, int queries)
    {
        Assert.Equal(queries, fixture.Requests.Count);
        Assert.Equal(queries, fixture.Observer.Proof.Queries);
        Assert.Equal(queries == 3, fixture.Observer.Proof.Matched);
        Assert.Equal(queries == 3, fixture.Observer.Proof.LowerEmpty);
        Assert.Equal(queries == 3, fixture.Observer.Proof.UpperEmpty);
        Assert.False(fixture.Observer.Proof.Verified);
        var row = Metrics(fixture.Observer);
        Assert.Equal((long)queries, row["attempts"]);
        Assert.Equal((long)queries, row["returns"]);
        fixture.AssertQueryFences();
    }

    [Theory]
    [InlineData(2, (int)CrashCorrelationReason.Cancelled)]
    [InlineData(2, (int)CrashCorrelationReason.Schema)]
    [InlineData(2, (int)CrashCorrelationReason.ReceiptChanged)]
    [InlineData(2, (int)CrashCorrelationReason.Receipt)]
    [InlineData(8, (int)CrashCorrelationReason.Cancelled)]
    [InlineData(8, (int)CrashCorrelationReason.Schema)]
    [InlineData(8, (int)CrashCorrelationReason.ReceiptChanged)]
    [InlineData(8, (int)CrashCorrelationReason.Receipt)]
    public async Task Entry_sampling_fault_preserves_later_observer_authority(
        int receiptRead, int reasonCode)
    {
        var reason = SamplingFenceReason(reasonCode);
        var timing = new ArithmeticException(PrivateFailure);
        var refusal = new IOException(PrivateFailure);
        using var fixture = new ObserverFixture(new Clock { UtcError = timing });
        using var cancellation = new CancellationTokenSource();
        RefuseOnReceipt(fixture.Receipt, () => fixture.Calls.Count(call => call == Call.Receipt),
            receiptRead, cancellation, reason, refusal);
        var error = await Assert.ThrowsAsync<CrashEventSamplingException>(
            () => fixture.Run(cancellation.Token));
        var count = receiptRead == 2 && reason != CrashCorrelationReason.Receipt ? 2 : 1;
        AssertSamplingFenceAuthority(error, timing, refusal, reason, count);
        AssertSamplingQueryHistory(fixture, receiptRead == 2 ? 0 : 3);
        Assert.Equal(receiptRead == 2 ? 3 : 8,
            fixture.Calls.Count(call => call == Call.Receipt));
        Assert.Equal(reason, fixture.Observer.Proof.Reason);
        Assert.Equal(0L, Metrics(fixture.Observer)["rejected"]);
    }

    [Theory]
    [InlineData(2, (int)CrashCorrelationReason.Cancelled)]
    [InlineData(2, (int)CrashCorrelationReason.Schema)]
    [InlineData(2, (int)CrashCorrelationReason.ReceiptChanged)]
    [InlineData(2, (int)CrashCorrelationReason.Receipt)]
    [InlineData(8, (int)CrashCorrelationReason.Cancelled)]
    [InlineData(8, (int)CrashCorrelationReason.Schema)]
    [InlineData(8, (int)CrashCorrelationReason.ReceiptChanged)]
    [InlineData(8, (int)CrashCorrelationReason.Receipt)]
    public async Task Entry_sampling_fault_preserves_later_authority_through_owned_cleanup(
        int receiptRead, int reasonCode)
    {
        var reason = SamplingFenceReason(reasonCode);
        var timing = new ArithmeticException(PrivateFailure);
        var refusal = new IOException(PrivateFailure);
        var fixture = new CommandFixture(Metadata)
            { SamplingClock = new Clock { UtcError = timing } };
        using var cancellation = new CancellationTokenSource();
        RefuseOnReceipt(fixture.Receipt,
            () => fixture.InCallback ? fixture.CallbackReceiptReads : 0,
            receiptRead, cancellation, reason, refusal);
        var error = await Assert.ThrowsAsync<CrashEventSamplingException>(
            () => fixture.Run(cancellation.Token));
        var count = (receiptRead == 2 && reason != CrashCorrelationReason.Receipt ? 2 : 1) +
            (reason is CrashCorrelationReason.Schema or CrashCorrelationReason.ReceiptChanged ? 1 : 0);
        AssertSamplingFenceAuthority(error, timing, refusal, reason, count);
        AssertSamplingCommandHistory(fixture, receiptRead == 2 ? 0 : 3, [timing, refusal]);
    }

    private static CrashCorrelationReason SamplingFenceReason(int reasonCode) =>
        reasonCode switch
        {
            (int)CrashCorrelationReason.Cancelled => CrashCorrelationReason.Cancelled,
            (int)CrashCorrelationReason.Schema => CrashCorrelationReason.Schema,
            (int)CrashCorrelationReason.ReceiptChanged => CrashCorrelationReason.ReceiptChanged,
            (int)CrashCorrelationReason.Receipt => CrashCorrelationReason.Receipt,
            _ => throw new ArgumentOutOfRangeException(nameof(reasonCode))
        };

    private static void RefuseOnReceipt(
        Receipt receipt, Func<int> reads, int receiptRead,
        CancellationTokenSource cancellation, CrashCorrelationReason reason,
        Exception refusal) =>
        receipt.BeforeRead = () =>
        {
            if (reads() == receiptRead)
                RefuseSamplingFence(receipt, cancellation, reason, refusal);
        };

    private static void RefuseSamplingFence(
        Receipt receipt, CancellationTokenSource cancellation,
        CrashCorrelationReason reason, Exception refusal)
    {
        if (reason == CrashCorrelationReason.Receipt)
            throw refusal;
        if (reason == CrashCorrelationReason.Cancelled)
            cancellation.Cancel();
        (receipt.Json, receipt.SourceHash) = reason switch
        {
            CrashCorrelationReason.Schema => ("{}", Hash),
            CrashCorrelationReason.ReceiptChanged => (Metadata, new string('c', 64)),
            _ => (receipt.Json, receipt.SourceHash)
        };
    }

    private static void AssertSamplingFenceAuthority(
        CrashEventSamplingException error, Exception sample, Exception refusal,
        CrashCorrelationReason reason, int count)
    {
        var authority = Assert.IsAssignableFrom<Exception>(error.AuthorityFailure);
        Exception[] originals = authority is AggregateException aggregate
            ? [.. aggregate.Flatten().InnerExceptions] : [authority];
        Assert.Equal(count, originals.Length);
        Assert.All(originals, original =>
            Assert.Equal(reason, CrashCorrelationErrors.Reason(original)));
        if (reason == CrashCorrelationReason.Cancelled)
            Assert.All(originals, original => Assert.IsType<OperationCanceledException>(original));
        if (reason == CrashCorrelationReason.Receipt)
            Assert.Same(refusal, authority);
        Assert.Equal(reason, CrashCorrelationErrors.Reason(error));
        Assert.False(CrashCorrelationErrors.Operational(error));
        Assert.Collection(error.InnerExceptions,
            first => Assert.Same(authority, first), last => Assert.Same(sample, last));
        Assert.Equal(count + 1, error.Flatten().InnerExceptions.Count);
    }

    private static void AssertSamplingCommandHistory(
        CommandFixture fixture, int queries, IReadOnlyList<Exception> errors)
    {
        fixture.AssertOwnedCleanup();
        AssertCoordinatorEscape(fixture, errors);
        Assert.Equal(queries, fixture.Requests.Count);
        Assert.Equal(queries == 0 ? 3 : 8, fixture.CallbackReceiptReads);
        AssertCommandQueryFences(fixture.Calls);
        var diagnostic = Assert.Single(fixture.Summaries, text =>
            text.StartsWith(DiagnosticPrefix, StringComparison.Ordinal));
        var row = Metrics(diagnostic);
        Assert.Equal((long)queries, row["attempts"]);
        Assert.Equal((long)queries, row["returns"]);
        Assert.Equal(queries == 0 ? 0L : 2L, row["empty"]);
        Assert.Equal(queries == 0 ? 0L : 1L, row["last"]);
        Assert.Equal(0L, row["failure"]);
        Assert.Equal(0L, row["rejected"]);
    }

    [Fact]
    public async Task Sampling_only_failure_cannot_shadow_postcoordinator_receipt_failure()
    {
        var timing = new ArithmeticException(PrivateFailure);
        var final = new IOException(PrivateFailure);
        var fixture = new CommandFixture(Metadata)
            { SamplingClock = new Clock { UtcError = timing } };
        fixture.Receipt.BeforeRead = () =>
        {
            if (fixture.ChildDisposed)
                throw final;
        };
        var error = await Assert.ThrowsAsync<CrashEventSamplingException>(
            () => fixture.Run());
        Assert.Same(final, error.AuthorityFailure);
        Assert.Collection(error.InnerExceptions,
            first => Assert.Same(final, first), last => Assert.Same(timing, last));
        Assert.Equal(CrashCorrelationReason.Receipt, CrashCorrelationErrors.Reason(error));
        AssertSamplingCommandHistory(fixture, 3, [timing, final]);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(0, 3)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(1, 3)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(3, 3)]
    public async Task Postquery_sampling_fault_preserves_actual_authority_outcome(
        int outcome, int points)
    {
        var sampling = new Clock();
        var first = new ArithmeticException(PrivateFailure);
        var last = new IOException(PrivateFailure);
        FaultSamples(sampling, 5, points, first, last);
        using var fixture = new ObserverFixture(sampling);
        using var cancellation = new CancellationTokenSource();
        fixture.QueryPolicy = (index, request) => index == 0
            ? PostQueryOutcome(fixture, cancellation, outcome) : Matching(request);
        var error = await Assert.ThrowsAsync<CrashEventSamplingException>(
            () => fixture.Run(cancellation.Token));
        AssertSamplingPresence(error.Flatten().InnerExceptions, points, first, last);
        AssertPostQueryAuthority(fixture, error, outcome);
    }

    private static CrashQueryResult PostQueryOutcome(
        ObserverFixture fixture, CancellationTokenSource cancellation, int outcome)
    {
        switch (outcome)
        {
            case 1:
                fixture.Clock.Advance(TimeSpan.FromSeconds(21));
                break;
            case 2:
                cancellation.Cancel();
                break;
            case 3:
                fixture.Current = Terminal with { Process = new(43, Birth) };
                break;
        }
        return Match;
    }

    private static void AssertPostQueryAuthority(
        ObserverFixture fixture, CrashEventSamplingException error, int outcome)
    {
        var reason = outcome switch
        {
            1 => CrashCorrelationReason.WindowExpired,
            2 => CrashCorrelationReason.Cancelled,
            3 => CrashCorrelationReason.Scope,
            _ => CrashCorrelationReason.Unexpected
        };
        Assert.Equal(reason, fixture.Observer.Proof.Reason);
        Assert.Equal(reason, CrashCorrelationErrors.Reason(error));
        Assert.Equal(outcome == 0, error.AuthorityFailure is null);
        Assert.Equal(outcome == 0, fixture.Observer.Proof.Matched);
        Assert.Equal(outcome == 0, fixture.Observer.Proof.LowerEmpty);
        Assert.Equal(outcome == 0, fixture.Observer.Proof.UpperEmpty);
        Assert.False(fixture.Observer.Proof.Verified);
        AssertPostQuerySamplingMetrics(fixture, outcome);
    }

    private static void AssertPostQuerySamplingMetrics(ObserverFixture fixture, int outcome)
    {
        Assert.Equal(outcome == 0 ? 3 : 1, fixture.Requests.Count);
        Assert.Equal(outcome switch { 0 => 7, 1 => 3, _ => 2 }, fixture.Clock.UtcReads);
        var row = Metrics(fixture.Observer);
        Assert.Equal(outcome == 0 ? 1L : 3L, row["last"]);
        Assert.Equal(outcome == 0 ? 3L : 1L, row["returns"]);
        Assert.Equal(outcome == 0 ? 2L : 0L, row["empty"]);
        Assert.Equal(outcome == 0 ? 0L : 1L, row["rejected"]);
        Assert.Equal(outcome == 1 ? (long)CrashEventExpiryLocation.AfterQuery : 0L,
            row["location"]);
    }

    [Fact]
    public async Task Final_fence_sampling_cancellation_is_not_authoritative_cancellation()
    {
        var timing = new OperationCanceledException(PrivateFailure);
        var sampling = new Clock { TimestampFailure = read => read == 16 ? timing : null };
        using var fixture = new ObserverFixture(sampling);
        var error = await Assert.ThrowsAsync<CrashEventSamplingException>(fixture.Run);
        Assert.Null(error.AuthorityFailure);
        Assert.Same(timing, Assert.Single(error.Flatten().InnerExceptions));
        Assert.Equal(CrashCorrelationReason.Unexpected, CrashCorrelationErrors.Reason(error));
        Assert.Equal(CrashCorrelationReason.Unexpected, fixture.Observer.Proof.Reason);
        Assert.True(fixture.Observer.Proof.Matched);
        Assert.True(fixture.Observer.Proof.LowerEmpty && fixture.Observer.Proof.UpperEmpty);
        Assert.False(fixture.Observer.Proof.Verified);
        Assert.Equal(0L, Metrics(fixture.Observer)["rejected"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sampling_and_original_errors_escape_once_after_actual_owned_cleanup(
        bool shared)
    {
        var (fixture, api, expected) = FaultingSampleCommand(shared);
        var error = await Assert.ThrowsAsync<CrashEventSamplingException>(() => fixture.Run());
        var errors = error.Flatten().InnerExceptions;
        Assert.Equal(expected.Length, errors.Count);
        Assert.All(expected, original =>
            Assert.Single(errors, item => ReferenceEquals(original, item)));
        AssertCoordinatorEscape(fixture, expected);
        Assert.Equal(1, fixture.Calls.Count(call => call == Call.DiagnosticSummary));
        Assert.True(api.IsValueCreated);
        Assert.Equal(2, api.Value.NextCalls);
        Assert.Equal(1, api.Value.ReadCalls);
        Assert.Equal<nint>([22, 21, 11], api.Value.CloseAttempts);
        api.Value.AssertSettled();
    }

    private static (CommandFixture Fixture, Lazy<BatchApi> Api, Exception[] Errors)
        FaultingSampleCommand(bool shared)
    {
        var clock = new Clock();
        var operation = new ArithmeticException(PrivateFailure);
        Exception timing = shared ? operation : new IOException(PrivateFailure);
        var cleanup = new ArithmeticException(PrivateFailure);
        var api = new Lazy<BatchApi>(() =>
            new BatchApi([new(true, 0, 1, 21), new(true, 0, 1, 22)])
            {
                ThrowOnSecondNext = true, Unexpected = operation,
                OnPhase = phase => ArmSamplingFault(clock, phase, timing)
            });
        var fixture = new CommandFixture(Metadata)
        {
            SamplingClock = clock, ChildDisposalError = cleanup,
            QueryWithTelemetry = (_, telemetry) => Enumerate(api.Value, telemetry)
        };
        return (fixture, api, shared ? [operation, cleanup] : [operation, timing, cleanup]);
    }

    private static void ArmSamplingFault(
        Clock clock, CrashEventPhase phase, Exception error)
    {
        if (phase == CrashEventPhase.EvtNext)
            clock.TimestampError = error;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sampling_shared_with_later_cleanup_preserves_only_genuine_occurrences(
        bool duplicateOriginal)
    {
        var shared = new ArithmeticException(PrivateFailure);
        Exception cleanup = duplicateOriginal
            ? new AggregateException(shared, shared) : shared;
        var fixture = new CommandFixture(Metadata)
        {
            SamplingClock = new Clock { UtcError = shared, TimestampError = shared },
            ChildDisposalError = cleanup
        };
        var error = await Assert.ThrowsAsync<CrashEventSamplingException>(
            () => fixture.Run());
        Assert.Same(cleanup, error.AuthorityFailure);
        Assert.Same(cleanup, Assert.Single(error.InnerExceptions));
        var originals = error.Flatten().InnerExceptions;
        Assert.Equal(duplicateOriginal ? 2 : 1, originals.Count);
        Assert.All(originals, original => Assert.Same(shared, original));
        AssertSamplingCommandHistory(fixture, 3, [shared, cleanup]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sampling_only_fault_remains_explicit_after_owned_cleanup(
        bool cancellationSample)
    {
        Exception sample = cancellationSample
            ? new OperationCanceledException(PrivateFailure) : new IOException(PrivateFailure);
        var fixture = new CommandFixture(Metadata)
            { SamplingClock = new Clock { UtcError = sample, TimestampError = sample } };
        var error = await Assert.ThrowsAsync<CrashEventSamplingException>(
            () => fixture.Run());
        Assert.Null(error.AuthorityFailure);
        Assert.Same(sample, Assert.Single(error.Flatten().InnerExceptions));
        Assert.False(CrashCorrelationErrors.Operational(error));
        Assert.Equal(CrashCorrelationReason.Unexpected, CrashCorrelationErrors.Reason(error));
        AssertSamplingCommandHistory(fixture, 3, [sample]);
    }

    [Fact]
    public async Task Genuine_coordinator_aggregate_duplicates_are_not_sampler_deduplicated()
    {
        var original = new IOException(PrivateFailure);
        var coordinator = new AggregateException(original, original);
        var fixture = new CommandFixture(Metadata) { ChildDisposalError = coordinator };
        Assert.Same(coordinator,
            await Assert.ThrowsAsync<AggregateException>(() => fixture.Run()));
        Assert.Equal(2, coordinator.Flatten().InnerExceptions.Count);
        Assert.All(coordinator.Flatten().InnerExceptions, item => Assert.Same(original, item));
        AssertCoordinatorEscape(fixture, [coordinator, original]);
    }

    private static BatchApi TimedBatch(Clock clock) =>
        new([new(true, 0, 1, 21), new(false, 259, 0)])
        {
            OnPhase = phase => AdvancePhase(clock, phase)
        };

    private static void AdvancePhase(Clock clock, CrashEventPhase phase) =>
        clock.Advance(TimeSpan.FromMilliseconds((int)phase + 1));

    private static CrashQueryResult Enumerate(
        BatchApi api, CrashEventTelemetry telemetry) =>
        CrashEventQuery.EnumerateCompletedCandidate(api,
            CrashCorrelationPlan.Create(Terminal).Exact,
            CrashQueryAdmission.CallerAttestsFixedProviderAndSchema, telemetry);

    [Fact]
    public void Diagnostic_schema_is_numeric_bounded_and_has_exact_maximum_length()
    {
        var telemetry = SaturatedTelemetry();
        var row = telemetry.Summary(new() { Polls = int.MaxValue, Queries = int.MaxValue });
        Assert.Equal(CrashEventTelemetry.MaximumSummaryBytes,
            Encoding.ASCII.GetByteCount(row));
        Assert.Equal(393, row.Length);
        Assert.EndsWith("\n", row);
        Assert.DoesNotContain("\r", row);
        Assert.Equal<string>(DiagnosticKeys, Metrics(row).Keys);
        Assert.DoesNotContain(Image, row);
        Assert.DoesNotContain(PrivateFailure, row);
        Assert.DoesNotContain("private.dll", row);
        Assert.DoesNotContain(Birth.ToString(
            System.Globalization.CultureInfo.InvariantCulture), row);
        Assert.Equal(0L, Metrics(row)["nativeAcceptance"]);
    }

    private static CrashEventTelemetry SaturatedTelemetry()
    {
        var clock = new Clock();
        var telemetry = new CrashEventTelemetry(clock);
        foreach (var phase in Enum.GetValues<CrashEventPhase>())
            telemetry.Measure(phase, () => { clock.Advance(TimeSpan.FromDays(1)); return 0; });
        foreach (var unused in Enumerable.Range(0, 1001))
            telemetry.Returned(new CrashQueryEmpty());
        telemetry.Returned(null);
        telemetry.Failure(new CrashQueryException([new(CrashEventFailureKind.Refused,
            (CrashQueryReason)int.MaxValue, -1)]));
        telemetry.Expired(CrashEventExpiry.Window, CrashEventExpiryLocation.BeforeQuery);
        telemetry.Rejected();
        return telemetry;
    }

    [Fact]
    public void Slack_and_negative_duration_bounds_do_not_export_absolute_times()
    {
        var clock = new Clock();
        var telemetry = new CrashEventTelemetry(clock);
        telemetry.Entered(TimeSpan.MaxValue);
        telemetry.BeforeQuery(TimeSpan.MinValue);
        telemetry.Measure(CrashEventPhase.Fence, () =>
        {
            clock.Advance(TimeSpan.FromTicks(-10));
            return 0;
        });
        var row = Metrics(telemetry.Summary(new() { Polls = -1, Queries = -1 }));
        Assert.Equal(999999L, row["entryMs"]);
        Assert.Equal(-999999L, row["queryMs"]);
        Assert.Equal(0L, row["polls"]);
        Assert.Equal(0L, row["attempts"]);
        Assert.Equal(0L, row["fUs"]);
        Assert.Equal(0L, row["fMaxUs"]);
    }

    [Fact]
    public async Task Successful_real_command_keeps_stdout_grammar_without_new_row()
    {
        var fixture = new CommandFixture(Metadata);
        Assert.Equal(0, await fixture.Run());
        Assert.Equal<Call>(
            [Call.ObservationSummary, Call.ControlSummary, Call.EventSummary],
            fixture.Calls.Where(call => call is Call.ObservationSummary or
                Call.ControlSummary or Call.EventSummary or Call.DiagnosticSummary));
        Assert.DoesNotContain(fixture.Summaries,
            text => text.StartsWith(DiagnosticPrefix, StringComparison.Ordinal));
        AssertOwnedControl(fixture, true, CrashFault.None);
        AssertEventFacts(fixture, CrashCorrelationReason.None, 0, true, true);
    }

    [Fact]
    public async Task Failed_real_command_emits_one_row_after_actual_owned_cleanup()
    {
        var fixture = new CommandFixture(Metadata)
            { QueryPolicy = _ => new CrashQueryAmbiguous() };
        Assert.Equal(1, await fixture.Run());
        var row = Assert.Single(fixture.Summaries,
            text => text.StartsWith(DiagnosticPrefix, StringComparison.Ordinal));
        Assert.Equal(2L, Metrics(row)["last"]);
        Assert.Equal(1L, Metrics(row)["returns"]);
        Assert.True(fixture.Calls.IndexOf(Call.DisposeChild) <
            fixture.Calls.IndexOf(Call.DisposeReceipt));
        Assert.True(fixture.Calls.IndexOf(Call.DisposeReceipt) <
            fixture.Calls.IndexOf(Call.DiagnosticSummary));
        AssertOwnedControl(fixture, false, CrashFault.Callback);
        AssertEventFacts(fixture, CrashCorrelationReason.Ambiguous, 1, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_failure_also_emits_only_one_postcleanup_row(bool control)
    {
        var error = new IOException(PrivateFailure);
        var fixture = new CommandFixture(Metadata)
        {
            ControlPublicationError = control ? error : null,
            EventPublicationError = control ? null : error
        };
        Assert.Equal(1, await fixture.Run());
        Assert.Equal(1, fixture.Calls.Count(call => call == Call.DiagnosticSummary));
        Assert.True(fixture.Calls.IndexOf(Call.DisposeReceipt) <
            fixture.Calls.IndexOf(Call.DiagnosticSummary));
        Assert.Equal(3L, Metrics(Assert.Single(fixture.Summaries,
            text => text.StartsWith(DiagnosticPrefix, StringComparison.Ordinal)))["returns"]);
    }

    [Fact]
    public async Task Missing_row_while_real_reap_is_pending_is_not_a_returned_outcome()
    {
        var error = new AggregateException(new IOException(PrivateFailure));
        var fixture = new CommandFixture(Metadata) { StartError = error, DeferExit = true };
        var run = fixture.Run();
        Assert.False(run.IsCompleted);
        fixture.AssertReapPending();
        Assert.DoesNotContain(Call.DiagnosticSummary, fixture.Calls);
        fixture.CompleteExit();
        Assert.Same(error, await Assert.ThrowsAsync<AggregateException>(() => run));
        Assert.Equal(1, fixture.Calls.Count(call => call == Call.DiagnosticSummary));
        AssertCoordinatorEscape(fixture, [error]);
        Assert.Equal(0L, Metrics(Assert.Single(fixture.Summaries,
            text => text.StartsWith(DiagnosticPrefix, StringComparison.Ordinal)))["attempts"]);
    }

    [Fact]
    public async Task Escaping_cleanup_has_telemetry_but_no_default_control_history()
    {
        var error = new ArithmeticException(PrivateFailure);
        var fixture = new CommandFixture(Metadata) { ChildDisposalError = error };
        Assert.Same(error, await Assert.ThrowsAsync<ArithmeticException>(() => fixture.Run()));
        Assert.Equal(1, fixture.Calls.Count(call => call == Call.DiagnosticSummary));
        AssertCoordinatorEscape(fixture, [error]);
        Assert.Equal(3L, Metrics(Assert.Single(fixture.Summaries,
            text => text.StartsWith(DiagnosticPrefix, StringComparison.Ordinal)))["returns"]);
    }

    [Fact]
    public async Task Diagnostic_output_fault_retains_original_unexpected_exception()
    {
        var initiating = new ArithmeticException(PrivateFailure);
        var output = new IOException(PrivateFailure);
        var fixture = new CommandFixture(Metadata)
        {
            QueryPolicy = _ => throw initiating,
            TelemetrySummaryError = output
        };
        var error = await Assert.ThrowsAsync<AggregateException>(() => fixture.Run());
        Assert.Collection(error.InnerExceptions,
            item => Assert.Same(initiating, item), item => Assert.Same(output, item));
        Assert.Equal(1, fixture.Calls.Count(call => call == Call.DiagnosticSummary));
        AssertUnexpectedCleanup(fixture, [initiating, output]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sampling_and_emission_faults_preserve_only_genuine_references(bool shared)
    {
        var sample = new ArithmeticException(PrivateFailure);
        Exception output = shared ? sample : new IOException(PrivateFailure);
        var fixture = new CommandFixture(Metadata)
        {
            SamplingClock = new Clock { UtcError = sample },
            TelemetrySummaryError = output
        };
        var failure = await Assert.ThrowsAsync<CrashEventSamplingException>(
            () => fixture.Run());
        Assert.Same(output, failure.AuthorityFailure);
        var errors = failure.Flatten().InnerExceptions;
        Assert.Equal(shared ? 1 : 2, errors.Count);
        Assert.Single(errors, error => ReferenceEquals(sample, error));
        Assert.Single(errors, error => ReferenceEquals(output, error));
        AssertSamplingCommandHistory(fixture, 3, [sample, output]);
        AssertEmissionBoundary(fixture, [sample, output]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sampling_emission_preserves_query_fences_and_owned_cleanup(bool shared)
    {
        var (fixture, originals) = SamplingEmissionCommand(shared);
        var failure = await Assert.ThrowsAsync<CrashEventSamplingException>(
            () => fixture.Run());
        var errors = failure.Flatten().InnerExceptions;
        Assert.Equal(originals.Length, errors.Count);
        Assert.All(originals, original =>
            Assert.Single(errors, error => ReferenceEquals(original, error)));
        AssertEmissionBoundary(fixture, originals);
        Assert.Single(fixture.Requests);
        Assert.Equal(4, fixture.CallbackReceiptReads);
        AssertCommandQueryFences(fixture.Calls);
    }

    private static (CommandFixture Fixture, Exception[] Originals)
        SamplingEmissionCommand(bool shared)
    {
        var query = new ArithmeticException(PrivateFailure);
        var postQuery = new IOException(PrivateFailure);
        var finalFence = new IOException(PrivateFailure);
        var cleanup = new ArithmeticException(PrivateFailure);
        var sample = new ArithmeticException(PrivateFailure);
        Exception output = shared ? sample : new IOException(PrivateFailure);
        var fixture = new CommandFixture(Metadata)
        {
            SamplingClock = new Clock { UtcError = sample },
            QueryPolicy = _ => throw query, ChildDisposalError = cleanup,
            TelemetrySummaryError = output
        };
        RefuseFences(fixture, [postQuery, finalFence], true);
        return (fixture, shared ? [query, postQuery, finalFence, cleanup, sample]
            : [query, postQuery, finalFence, cleanup, sample, output]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Emission_preserves_genuine_duplicate_graphs(bool samplingEnvelope)
    {
        var sample = new ArithmeticException(PrivateFailure);
        var duplicates = new AggregateException(sample, sample);
        AggregateException genuine = samplingEnvelope
            ? new CrashEventSamplingException(duplicates, []) : duplicates;
        var output = new IOException(PrivateFailure);
        var fixture = new CommandFixture(Metadata)
        {
            SamplingClock = new Clock { UtcError = sample },
            ChildDisposalError = genuine, TelemetrySummaryError = output
        };
        var failure = await Assert.ThrowsAsync<CrashEventSamplingException>(
            () => fixture.Run());
        AssertGenuineEmissionGraph(failure, genuine, sample, output);
        AssertEmissionBoundary(fixture, [sample, genuine, output]);
    }

    private static void AssertGenuineEmissionGraph(
        CrashEventSamplingException failure, AggregateException genuine,
        Exception repeated, Exception output)
    {
        var authority = Assert.IsType<AggregateException>(failure.AuthorityFailure);
        Assert.Collection(authority.InnerExceptions,
            first => Assert.Same(genuine, first), last => Assert.Same(output, last));
        Assert.Same(authority, Assert.Single(failure.InnerExceptions));
        Assert.Collection(genuine.Flatten().InnerExceptions,
            first => Assert.Same(repeated, first), last => Assert.Same(repeated, last));
        var originals = failure.Flatten().InnerExceptions;
        Assert.Equal(3, originals.Count);
        Assert.Equal(2, originals.Count(error => ReferenceEquals(repeated, error)));
        Assert.Single(originals, error => ReferenceEquals(output, error));
    }

    private static void AssertEmissionBoundary(
        CommandFixture fixture, IReadOnlyList<Exception> originals)
    {
        fixture.AssertOwnedCleanup();
        AssertCoordinatorEscape(fixture, originals);
        Assert.Equal(1, fixture.Calls.Count(call => call == Call.DiagnosticSummary));
        Assert.True(fixture.Calls.IndexOf(Call.DisposeReceipt) <
            fixture.Calls.IndexOf(Call.DiagnosticSummary));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_new_row_is_manufactured_without_a_settled_coordinator(bool guard)
    {
        var fixture = new CommandFixture(guard ? Metadata : "{}") { GuardFailure = guard };
        Assert.Equal(1, await fixture.Run());
        Assert.DoesNotContain(Call.Start, fixture.Calls);
        Assert.DoesNotContain(Call.DiagnosticSummary, fixture.Calls);
    }

    private static string[] DiagnosticKeys =>
        ("v polls attempts returns empty last failure code native entryMs queryMs " +
         "expiry location rejected qUs qMaxUs nUs nMaxUs rUs rMaxUs cUs cMaxUs " +
         "fUs fMaxUs dUs dMaxUs nativeAcceptance").Split(' ');

    private static Dictionary<string, long> Metrics(CrashEventObserver observer) =>
        Metrics(observer.Telemetry.Summary(observer.Proof));

    private static Dictionary<string, long> Metrics(string row) =>
        row.TrimEnd('\n').Split(' ').Skip(1).Select(field => field.Split('='))
            .ToDictionary(field => field[0], field => long.Parse(field[1],
                System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);

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
        var row = Metrics(fixture.Observer);
        Assert.Equal(3L, row["last"]);
        Assert.Equal(1L, row["returns"]);
        Assert.Equal(1L, row["rejected"]);
        Assert.Equal(cancel ? 0L : (long)CrashEventExpiry.Window, row["expiry"]);
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
        internal Exception? TimestampError;
        internal Exception? UtcError;
        internal Func<int, Exception?>? TimestampFailure;
        internal int TimestampReads { get; private set; }
        internal int UtcReads { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            var failure = TimestampFailure?.Invoke(++TimestampReads) ?? TimestampError;
            return failure is { } error ? throw error : Elapsed.Ticks;
        }

        public override DateTimeOffset GetUtcNow()
        {
            UtcReads++;
            var error = UtcError;
            UtcError = null;
            return error is { } failure ? throw failure : _start + Elapsed;
        }

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
        internal Exception? DelayError;

        internal ObserverFixture(Clock? samplingClock = null)
        {
            Receipt = new(Calls, Metadata);
            var admission = CrashEventSchema.Admit(Receipt.Read());
            Scope = new(Terminal with { ExitFileTime = 0, ExitCode = 259 }, CrashExit, ReadIdentity);
            Observer = new(Receipt, admission,
                new(Query, Clock, Delay) { SamplingClock = samplingClock ?? Clock },
                WriteSummary);
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

        private CrashQueryResult Query(
            CrashQueryRequest request, CrashQueryAdmission admission,
            CrashEventTelemetry telemetry)
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
            if (DelayError is { } error)
                throw error;
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
        internal Clock SamplingClock { get; init; } = new();
        internal Receipt Receipt { get; }
        internal bool GuardFailure { get; init; }
        internal bool RefuseReceiptDuringCallback { get; init; }
        internal bool ReceiptCleanupFailure { get; init; }
        internal bool DeferExit { get; init; }
        internal Exception? StartError { get; init; }
        internal Exception? ChildDisposalError { get; init; }
        internal Exception? ControlPublicationError { get; init; }
        internal Exception? EventPublicationError { get; init; }
        internal Exception? TelemetrySummaryError { get; init; }
        internal Func<CrashQueryRequest, CrashQueryResult> QueryPolicy { get; init; } = Matching;
        internal Func<CrashQueryRequest, CrashEventTelemetry, CrashQueryResult>?
            QueryWithTelemetry { get; init; }
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
            new(Query, Clock, Delay) { SamplingClock = SamplingClock },
            PublishControl, PublishEvent, Summary);

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
            CrashQueryRequest request, CrashQueryAdmission admission,
            CrashEventTelemetry telemetry)
        {
            Assert.Equal(CrashQueryAdmission.CallerAttestsFixedProviderAndSchema, admission);
            Assert.False(ChildDisposed || Receipt.Closed);
            Calls.Add(Call.Query);
            Requests.Add(request);
            return QueryWithTelemetry is { } measured
                ? measured(request, telemetry) : QueryPolicy(request);
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
                var value when value.StartsWith(DiagnosticPrefix, StringComparison.Ordinal)
                    => Call.DiagnosticSummary,
                _ => Call.ControlSummary
            });
            if (text.StartsWith(DiagnosticPrefix, StringComparison.Ordinal))
            {
                Assert.Equal(ChildAdopted, ChildDisposed);
                Assert.True(Receipt.Closed);
                if (TelemetrySummaryError is { } error)
                    throw error;
            }
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
        internal Action<CrashEventPhase>? OnPhase { get; init; }
        internal Exception? ReaderError { get; init; }
        internal CrashEventRead Reading { get; init; } =
            new CrashEventMatched(new("80131623", "private.dll", @"C:\private\private.dll"));
        internal ArithmeticException Unexpected { get; init; } = new("not-exported");

        public CrashQueryOpenCall Open(
            nint session, string channel, string expression, uint flags)
        {
            CheckThread();
            Assert.Equal((nint)0, session);
            Assert.Equal(CrashEventQuery.Channel, channel);
            Assert.Equal(CrashEventQuery.QueryFlags, flags);
            _closePending.Add(11);
            OnPhase?.Invoke(CrashEventPhase.EvtQuery);
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
            OnPhase?.Invoke(CrashEventPhase.EvtNext);
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
            OnPhase?.Invoke(CrashEventPhase.Reader);
            if (ReaderError is { } error)
                throw error;
            return Reading;
        }

        public CrashQueryCloseCall Close(nint ownedHandle)
        {
            CheckThread();
            Assert.True(_closePending.Remove(ownedHandle));
            Assert.DoesNotContain(ownedHandle, CloseAttempts);
            CloseAttempts.Add(ownedHandle);
            OnPhase?.Invoke(CrashEventPhase.Close);
            return FailClose ? new(false, 5) : new(true, 0);
        }

        private void CheckThread() =>
            Assert.Equal(_thread, Environment.CurrentManagedThreadId);

        internal void AssertClosed() => Assert.Empty(_closePending);

        internal void AssertSettled()
        {
            AssertClosed();
            Assert.InRange(NextCalls, 1, 2);
            Assert.InRange(ReadCalls, 0, 1);
        }
    }
}
