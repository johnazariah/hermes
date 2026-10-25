using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Hermes.Servicing.Acceptance;
using Xunit;

namespace Hermes.Servicing.Tests;

public sealed partial class CrashEventValuesTests
{
    private static CrashIdentityRead ReadIdentityOnly(
        FakeApi api, CrashEventSubject? subject = null, nint handle = 77) =>
        CrashEventValues.ReadIdentity(api, handle, subject ?? Subject);

    [Fact]
    public void Presence_reader_returns_only_classification_and_never_reads_details()
    {
        var api = new FakeApi(details: []);
        var read = Assert.IsType<CrashIdentityCompared>(ReadIdentityOnly(api));
        Assert.Equal(CrashIdentityComparison.Same, read.Comparison);
        Assert.False(typeof(CrashEventRead).IsAssignableFrom(typeof(CrashIdentityRead)));
        Assert.Equal<Call>(
            [Call.CreateIdentity, Call.ProbeIdentity, Call.AllocateIdentity,
                Call.RenderIdentity, Call.SnapshotIdentity, Call.FreeIdentity,
                Call.CloseIdentity], api.Calls);
        AssertNoDetails(api);
    }

    [Theory]
    [MemberData(nameof(Births))]
    public void Presence_birth_comparison_is_lossless_and_identity_only(ulong birth)
    {
        var expected = Subject with { CreationFileTime = birth };
        AssertPresenceComparison(new(Identity(birth)), expected,
            CrashIdentityComparison.Same);
        AssertPresenceComparison(new(Identity(birth - 1)), expected,
            CrashIdentityComparison.BirthDifferent);
        if (birth < ulong.MaxValue)
            AssertPresenceComparison(new(Identity(birth + 1)), expected,
                CrashIdentityComparison.BirthDifferent);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Foreign_pid_or_image_is_not_classified_as_only_a_birth_difference(
        bool pidDifferent, bool imageDifferent)
    {
        var api = new FakeApi(Identity(Birth + 1,
            imageDifferent ? @"C:\other\child.exe" : Image,
            pidDifferent ? 41u : uint.MaxValue));
        AssertPresenceComparison(api, Subject, pidDifferent
            ? CrashIdentityComparison.ProcessDifferent
            : CrashIdentityComparison.ImageDifferent);
    }

    [Theory]
    [InlineData(@"c:\owned\child.exe")]
    [InlineData(@"C:\owned\CHILD.exe")]
    public void Presence_image_comparison_does_not_fold_case(string path) =>
        AssertPresenceComparison(new(Identity(path: path)), Subject,
            CrashIdentityComparison.ImageDifferent);

    private static void AssertPresenceComparison(
        FakeApi api, CrashEventSubject subject, CrashIdentityComparison expected)
    {
        Assert.Equal(expected,
            Assert.IsType<CrashIdentityCompared>(ReadIdentityOnly(api, subject)).Comparison);
        AssertNoDetails(api);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Presence_invalid_inputs_refuse_before_native_capabilities(int defect)
    {
        var api = new FakeApi();
        var subject = defect switch
        {
            0 => Subject with { ProcessId = 0 },
            1 => Subject with { CreationFileTime = 0 },
            2 => Subject with { ActualImagePath = @"C:\owned\..\child.exe" },
            _ => Subject
        };
        var handle = defect switch { 3 => (nint)0, 4 => (nint)(-1), _ => OwnedEvent };
        var reason = defect < 3
            ? CrashEventReason.InvalidSubject : CrashEventReason.InvalidEventHandle;
        AssertIdentityFault(ReadIdentityOnly(api, subject, handle), reason);
        Assert.Empty(api.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Presence_checks_every_abi_invariant_before_context_creation(int defect)
    {
        var api = new FakeApi { Abi = CorruptAbi(CrashEventAbi.X64, defect) };
        AssertIdentityFault(ReadIdentityOnly(api), CrashEventReason.UnsupportedAbi);
        Assert.Empty(api.Calls);
    }

    public static IEnumerable<object[]> PresenceTypes() =>
        new (int Index, uint Type)[]
        {
            (0, 0), (0, 8), (0, 12), (0, 148), (0, 276),
            (1, 0), (1, 9), (1, 10), (1, 149),
            (2, 0), (2, 20), (2, 129)
        }.Select(value => new object[] { value.Index, value.Type });

    [Theory]
    [MemberData(nameof(PresenceTypes))]
    public void Presence_reuses_exact_scalar_identity_types(int index, uint type)
    {
        var bytes = Identity();
        SetType(bytes, index, type);
        var api = new FakeApi(bytes);
        AssertIdentityFault(ReadIdentityOnly(api), type == 0
            ? CrashEventReason.MissingValue : CrashEventReason.VariantType);
        AssertNoDetails(api);
    }

    [Theory]
    [InlineData(0, (int)CrashEventReason.BufferSize)]
    [InlineData(1, (int)CrashEventReason.BufferSize)]
    [InlineData(2, (int)CrashEventReason.PropertyCount)]
    [InlineData(3, (int)CrashEventReason.BufferSize)]
    [InlineData(4, (int)CrashEventReason.BufferSize)]
    [InlineData(5, (int)CrashEventReason.PointerRange)]
    [InlineData(6, (int)CrashEventReason.BufferAlignment)]
    public void Presence_reuses_owned_buffer_bounds(int defect, int reason)
    {
        var api = PresenceBufferDefect(defect);
        AssertIdentityFault(ReadIdentityOnly(api), (CrashEventReason)reason);
        AssertNoDetails(api);
    }

    private static FakeApi PresenceBufferDefect(int defect) => defect switch
    {
        0 => new() { ProbeSize = 65537 },
        1 => new() { UsedBytes = 47 },
        2 => new() { PropertyCount = 4 },
        3 => new() { CapacityAdjustment = 1 },
        4 => new() { SnapshotAdjustment = -1 },
        5 => new() { AllocationAddress = 0 },
        6 => new() { AllocationAddress = Address + 1 },
        _ => throw new ArgumentOutOfRangeException(nameof(defect))
    };

    [Theory]
    [InlineData(0, (int)CrashEventReason.PointerRange)]
    [InlineData(1, (int)CrashEventReason.InvalidUtf16)]
    [InlineData(2, (int)CrashEventReason.StringLimit)]
    [InlineData(3, (int)CrashEventReason.MalformedPath)]
    public void Presence_reuses_strict_pointer_string_and_path_decoding(
        int defect, int reason)
    {
        var bytes = defect == 3 ? Identity(path: "relative.exe") : Identity();
        switch (defect)
        {
            case 0: BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), Address); break;
            case 1: BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(48), 0xd800); break;
            case 2: BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 2), 1); break;
        }
        var api = new FakeApi(bytes);
        AssertIdentityFault(ReadIdentityOnly(api), (CrashEventReason)reason);
        AssertNoDetails(api);
    }

    public static IEnumerable<object[]> PresenceNativeFailures() =>
        NativeFailures().Where(row => (Call)(int)row[0] <= Call.CloseIdentity);

    [Theory]
    [MemberData(nameof(PresenceNativeFailures))]
    public void Presence_native_and_cleanup_faults_are_not_comparisons(int failed, int reason)
    {
        var api = new FakeApi { Failures = [(Call)failed] };
        var fault = Assert.Single(
            Assert.IsType<CrashIdentityFailure>(ReadIdentityOnly(api)).Faults);
        Assert.Equal((CrashEventReason)reason, fault.Reason);
        Assert.Equal((CrashEventReason)reason is
            CrashEventReason.ContextClose or CrashEventReason.BufferRelease
                ? CrashEventFailureKind.Refused : CrashEventFailureKind.Unavailable,
            fault.Kind);
        AssertNoDetails(api);
    }

    [Fact]
    public void Presence_preserves_both_owned_cleanup_failures()
    {
        var api = new FakeApi { Failures = [Call.FreeIdentity, Call.CloseIdentity] };
        var failure = Assert.IsType<CrashIdentityFailure>(ReadIdentityOnly(api));
        Assert.Equal<CrashEventReason>(
            [CrashEventReason.BufferRelease, CrashEventReason.ContextClose],
            failure.Faults.Select(fault => fault.Reason));
        AssertNoDetails(api);
    }

    public static IEnumerable<object[]> PresenceUnexpectedCalls() =>
        UnexpectedCalls().Where(row => (Call)(int)row[0] <= Call.CloseIdentity);

    [Theory]
    [MemberData(nameof(PresenceUnexpectedCalls))]
    public void Presence_unexpected_failures_escape_after_owned_cleanup(int failed)
    {
        var api = new FakeApi { UnexpectedAt = (Call)failed };
        Assert.Throws<ArithmeticException>(() => ReadIdentityOnly(api));
        AssertNoDetails(api);
    }

    [Fact]
    public void Presence_cleanup_does_not_replace_an_unexpected_render_failure()
    {
        var api = new FakeApi
        {
            UnexpectedAt = Call.RenderIdentity,
            Failures = [Call.FreeIdentity, Call.CloseIdentity]
        };
        var failure = Assert.Throws<AggregateException>(() => ReadIdentityOnly(api));
        Assert.Contains(failure.InnerExceptions, error => error is ArithmeticException);
        Assert.Equal(2, failure.InnerExceptions.OfType<CrashEventValuesException>().Count());
        AssertNoDetails(api);
    }

    private static void AssertIdentityFault(
        CrashIdentityRead read, CrashEventReason reason) =>
        Assert.Equal(new CrashEventFault(CrashEventFailureKind.Refused, reason),
            Assert.Single(Assert.IsType<CrashIdentityFailure>(read).Faults));
}

public sealed partial class CrashEventQueryTests
{
    [Theory]
    [MemberData(nameof(BirthGoldens))]
    public void Presence_expression_deletes_only_the_exact_birth_predicate(
        ulong birth, string literal)
    {
        var request = WithBirth(birth);
        var exact = Construct(request).Expression;
        var diagnostic = Assert.IsType<CrashQueryConstructed>(
            CrashEventQuery.ConstructPresence(request));
        var removed = $"Data[@Name='ProcessCreationTime']={literal} and ";
        Assert.Equal(Golden(literal).Replace(removed, "", StringComparison.Ordinal),
            diagnostic.Expression);
        Assert.Equal(exact.Length - removed.Length, diagnostic.Expression.Length);
        Assert.Equal(CrashQueryPrecision.Unproven, diagnostic.Precision);
        Assert.DoesNotContain("ProcessCreationTime", diagnostic.Expression);
    }

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("fr-FR")]
    public void Presence_expression_preserves_all_nonbirth_literals_under_culture(
        string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var expression = Assert.IsType<CrashQueryConstructed>(
                CrashEventQuery.ConstructPresence(Request)).Expression;
            Assert.Equal(Golden().Replace(
                $"Data[@Name='ProcessCreationTime']={BirthLiteral} and ",
                "", StringComparison.Ordinal), expression);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [MemberData(nameof(InvalidWindows))]
    internal void Presence_retains_every_original_window_refusal(CrashQueryWindow window) =>
        AssertFault(CrashEventQuery.ConstructPresence(Request with { Window = window }),
            CrashQueryReason.InvalidWindow);

    [Theory]
    [InlineData("")]
    [InlineData(@"relative.exe")]
    [InlineData(@"C:\owned\..\child.exe")]
    [InlineData(@"\\?\C:\owned\child.exe")]
    [InlineData(@"C:\owned\chi'ld.exe")]
    [InlineData("C:\\owned\\\u00e9.exe")]
    public void Presence_does_not_add_path_rewriting_or_escaping(string path) =>
        AssertFault(CrashEventQuery.ConstructPresence(WithPath(path)),
            CrashQueryReason.InvalidSubject);

    [Fact]
    public void Presence_does_not_relax_missing_identity_or_original_query_length_bounds()
    {
        AssertFault(CrashEventQuery.ConstructPresence(WithBirth(0)),
            CrashQueryReason.InvalidSubject);
        AssertFault(CrashEventQuery.ConstructPresence(Request with
        {
            Subject = Request.Subject with { ProcessId = 0 }
        }), CrashQueryReason.InvalidSubject);
        var overhead = Construct(WithPath(@"C:\x")).Expression.Length - 4;
        var length = CrashEventQuery.MaximumQueryCharacters - overhead;
        var path = @"C:\" + new string('a', length - 3);
        Assert.IsType<CrashQueryConstructed>(
            CrashEventQuery.ConstructPresence(WithPath(path)));
        AssertFault(CrashEventQuery.ConstructPresence(WithPath(path + "a")),
            CrashQueryReason.QueryLength);
    }
}

public sealed partial class CrashEventControlTests
{
    private const string PresencePrefix = "HERMES_CRASH_EVENT_PRESENCE ";
    private static CrashQueryResult PresenceSame =>
        new CrashQueryPresence(new CrashIdentityCompared(CrashIdentityComparison.Same));

    private static Task<int> RunPresence(
        CommandFixture fixture, CancellationToken cancellationToken = default) =>
        CrashEventControl.DispatchPresenceAsync(
            [CrashEventControl.PresenceCommand], fixture.Effects, cancellationToken);

    private static CrashQueryResult EnumeratePresence(
        BatchApi api, CrashEventTelemetry? telemetry = null) =>
        CrashEventQuery.EnumeratePresence(api, CrashCorrelationPlan.Create(Terminal).Exact,
            CrashQueryAdmission.CallerAttestsFixedProviderAndSchema, telemetry);

    [Fact]
    public void Presence_completed_enumeration_uses_only_identity_and_owned_handles()
    {
        var api = new BatchApi([new(true, 0, 1, 21), new(false, 259, 0)]);
        var result = Assert.IsType<CrashQueryPresence>(EnumeratePresence(api));
        Assert.Same(api.IdentityReading, result.Read);
        Assert.Equal(CrashQueryPrecision.Unproven, result.Precision);
        Assert.Equal(2, api.NextCalls);
        Assert.Equal(1, api.IdentityReadCalls);
        Assert.Equal(0, api.ReadCalls);
        Assert.Equal<nint>([21, 11], api.CloseAttempts);
        var exact = Assert.IsType<CrashQueryConstructed>(
            CrashEventQuery.Construct(CrashCorrelationPlan.Create(Terminal).Exact)).Expression;
        Assert.Equal(exact.Replace(
            $"Data[@Name='ProcessCreationTime']={Birth} and ",
            "", StringComparison.Ordinal), api.Expression);
        api.AssertSettled();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Presence_empty_or_initial_ambiguity_never_reads_identity(bool ambiguous)
    {
        var api = new BatchApi(ambiguous
            ? [new(true, 0, 2, 21, 22)] : [new(false, 259, 0)]);
        var result = EnumeratePresence(api);
        Assert.Equal(ambiguous ? CrashPresenceOutcome.Ambiguous : CrashPresenceOutcome.Empty,
            CrashEventPresence.Classify(result));
        Assert.Equal(1, api.NextCalls);
        Assert.Equal(0, api.IdentityReadCalls);
        Assert.Equal(0, api.ReadCalls);
        api.AssertSettled();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Presence_requires_an_end_marker_after_the_identity_read(bool alias)
    {
        var api = new BatchApi(
            [new(true, 0, 1, 21), new(true, 0, 1, alias ? 21 : 22)]);
        var result = EnumeratePresence(api);
        Assert.Equal(alias ? CrashPresenceOutcome.QueryFailure : CrashPresenceOutcome.Ambiguous,
            CrashEventPresence.Classify(result));
        Assert.Equal(1, api.IdentityReadCalls);
        Assert.Equal(0, api.ReadCalls);
        Assert.Equal<nint>(alias ? [21, 11] : [22, 21, 11], api.CloseAttempts);
        api.AssertSettled();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Presence_query_errors_or_failed_closes_invalidate_completion(bool close)
    {
        var api = new BatchApi(
            [new(true, 0, 1, 21), new(false, close ? 259 : 1460, 0)])
            { FailClose = close };
        Assert.IsType<CrashQueryFailure>(EnumeratePresence(api));
        Assert.Equal(0, api.ReadCalls);
        api.AssertSettled();
    }

    [Theory]
    [InlineData(true, 0, 0u, 0, 0)]
    [InlineData(true, 0, 1u, 0, 0)]
    [InlineData(true, 0, 1u, 21, 22)]
    [InlineData(true, 0, 2u, 21, 21)]
    [InlineData(false, 5, 1u, 21, 0)]
    [InlineData(false, 259, 0u, 21, 0)]
    public void Presence_malformed_batches_close_owned_events_without_reading(
        bool success, int error, uint returned, int first, int second)
    {
        var api = new BatchApi([new(success, error, returned, first, second)]);
        Assert.IsType<CrashQueryFailure>(EnumeratePresence(api));
        Assert.Equal(0, api.IdentityReadCalls);
        Assert.Equal(0, api.ReadCalls);
        api.AssertSettled();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Presence_unexpected_reader_or_second_next_errors_escape_after_closes(
        bool secondNext)
    {
        var error = new ArithmeticException(PrivateFailure);
        var api = new BatchApi([new(true, 0, 1, 21), new(true, 0, 1, 22)])
        {
            ReaderError = secondNext ? null : error,
            ThrowOnSecondNext = secondNext, Unexpected = error
        };
        Assert.Same(error, Assert.Throws<ArithmeticException>(() => EnumeratePresence(api)));
        Assert.Equal<nint>(secondNext ? [22, 21, 11] : [21, 11], api.CloseAttempts);
        Assert.Equal(0, api.ReadCalls);
        api.AssertSettled();
    }

    [Fact]
    public void Presence_reader_faults_remain_numeric_and_not_a_matched_read()
    {
        var telemetry = new CrashEventTelemetry(new Clock());
        var api = new BatchApi([new(true, 0, 1, 21), new(false, 259, 0)])
        {
            IdentityReading = new CrashIdentityFailure(
                [new(CrashEventFailureKind.Refused, CrashEventReason.VariantType)])
        };
        var result = EnumeratePresence(api, telemetry);
        telemetry.Returned(result);
        Assert.Equal(CrashPresenceOutcome.ReaderFailure, CrashEventPresence.Classify(result));
        var row = Metrics(telemetry.Summary(new()));
        Assert.Equal(2L, row["failure"]);
        Assert.Equal((long)CrashEventReason.VariantType + 1, row["code"]);
        Assert.Equal(0, api.ReadCalls);
        api.AssertSettled();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2)]
    public void Presence_requires_explicit_schema_admission_before_open(int admission)
    {
        var api = new BatchApi([]);
        var result = CrashEventQuery.EnumeratePresence(api,
            CrashCorrelationPlan.Create(Terminal).Exact, (CrashQueryAdmission)admission);
        var fault = Assert.Single(Assert.IsType<CrashQueryFailure>(result).Faults);
        Assert.Equal(CrashQueryReason.AdmissionRequired, fault.Reason);
        Assert.Equal(0, api.NextCalls);
        Assert.Empty(api.Expression);
        Assert.Empty(api.CloseAttempts);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void Presence_first_reader_failure_survives_completion_and_cleanup(
        int later, bool close)
    {
        var telemetry = new CrashEventTelemetry(new Clock());
        var api = PresenceReaderFailureApi(later, close);
        var result = EnumeratePresence(api, telemetry);
        AssertPresenceReaderTelemetry(telemetry);
        telemetry.Returned(result);
        AssertPresenceReaderTelemetry(telemetry);
        Assert.Equal((later, close) switch
        {
            (1, false) => CrashPresenceOutcome.Ambiguous,
            (2, false) => CrashPresenceOutcome.ReaderFailure,
            _ => CrashPresenceOutcome.QueryFailure
        }, CrashEventPresence.Classify(result));
        AssertPresenceLaterFaults(result, later, close);
        Assert.Equal(1, api.IdentityReadCalls);
        Assert.Equal(0, api.ReadCalls);
        api.AssertSettled();
    }

    private static CrashIdentityFailure PresenceReaderFailure =>
        new([new(CrashEventFailureKind.Unavailable, CrashEventReason.Render)]);

    private static BatchApi PresenceReaderFailureApi(int later, bool close) => new(
        [new(true, 0, 1, 21), later switch
        {
            0 => new(false, 1460, 0),
            1 => new(true, 0, 1, 22),
            _ => new(false, 259, 0)
        }])
    {
        IdentityReading = PresenceReaderFailure,
        FailClose = close
    };

    private static void AssertPresenceReaderTelemetry(CrashEventTelemetry telemetry)
    {
        var row = Metrics(telemetry.Summary(new()));
        Assert.Equal(2L, row["failure"]);
        Assert.Equal((long)CrashEventReason.Render + 1, row["code"]);
        Assert.Equal(-1L, row["native"]);
    }

    private static void AssertPresenceLaterFaults(
        CrashQueryResult result, int later, bool close)
    {
        if (result is not CrashQueryFailure failure)
            return;
        var closes = close ? later == 1 ? 3 : 2 : 0;
        Assert.Equal((later == 0 ? 1 : 0) + closes, failure.Faults.Length);
        Assert.Equal(later == 0 ? CrashQueryReason.Next : CrashQueryReason.EventClose,
            failure.Faults[0].Reason);
        Assert.Equal(later == 0 ? 1460 : 5, failure.Faults[0].NativeError);
        Assert.Equal(closes, failure.Faults.Count(fault =>
            fault.Reason is CrashQueryReason.EventClose or CrashQueryReason.QueryClose));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Presence_reader_failure_does_not_hide_or_duplicate_unexpected_faults(
        bool sampling, bool close)
    {
        var original = new ArithmeticException(PrivateFailure);
        var telemetry = new CrashEventTelemetry(
            new Clock { TimestampError = sampling ? original : null });
        var api = PresenceUnexpectedApi(original, close);
        var failure = Assert.ThrowsAny<Exception>(() => EnumeratePresence(api, telemetry));
        AssertPresenceEscaped(original, failure, sampling, close);
        AssertPresenceReaderTelemetry(telemetry);
        Assert.Equal<nint>([22, 21, 11], api.CloseAttempts);
        Assert.Equal(1, api.IdentityReadCalls);
        Assert.Equal(0, api.ReadCalls);
        api.AssertSettled();
    }

    private static BatchApi PresenceUnexpectedApi(
        ArithmeticException original, bool close) => new(
            [new(true, 0, 1, 21), new(true, 0, 1, 22)])
    {
        IdentityReading = PresenceReaderFailure,
        ThrowOnSecondNext = true,
        Unexpected = original,
        FailClose = close
    };

    private static void AssertPresenceEscaped(
        Exception original, Exception failure, bool sampling, bool close)
    {
        Assert.Equal((sampling, close) switch
        {
            (true, _) => typeof(CrashEventSamplingException),
            (_, true) => typeof(AggregateException),
            _ => typeof(ArithmeticException)
        }, failure.GetType());
        Exception[] leaves = failure is AggregateException aggregate
            ? [.. aggregate.Flatten().InnerExceptions] : [failure];
        Assert.Same(original,
            Assert.Single(leaves, error => ReferenceEquals(error, original)));
        Assert.Equal(close ? 4 : 1, leaves.Length);
        Assert.Equal(close ? 3 : 0, leaves.OfType<CrashQueryException>().Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Presence_invalid_requests_do_not_open_native_query(int defect)
    {
        var request = CrashCorrelationPlan.Create(Terminal).Exact;
        request = defect switch
        {
            0 => request with { Subject = request.Subject with { ProcessId = 0 } },
            1 => request with { Subject = request.Subject with { CreationFileTime = 0 } },
            _ => request with { Window = new(request.Window.EndInclusive, request.Window.StartInclusive) }
        };
        var api = new BatchApi([]);
        Assert.IsType<CrashQueryFailure>(CrashEventQuery.EnumeratePresence(api, request,
            CrashQueryAdmission.CallerAttestsFixedProviderAndSchema));
        Assert.Empty(api.Expression);
        Assert.Empty(api.CloseAttempts);
    }

    [Theory]
    [InlineData((int)CrashIdentityComparison.Same, (int)CrashPresenceOutcome.SameIdentity)]
    [InlineData((int)CrashIdentityComparison.BirthDifferent, (int)CrashPresenceOutcome.DifferentBirth)]
    [InlineData((int)CrashIdentityComparison.ProcessDifferent, (int)CrashPresenceOutcome.DifferentProcess)]
    [InlineData((int)CrashIdentityComparison.ImageDifferent, (int)CrashPresenceOutcome.DifferentImage)]
    [InlineData(-1, (int)CrashPresenceOutcome.Incomplete)]
    public void Presence_classifications_are_closed_and_identity_only(
        int comparison, int outcome) =>
        Assert.Equal((CrashPresenceOutcome)outcome, CrashEventPresence.Classify(
            new CrashQueryPresence(new CrashIdentityCompared((CrashIdentityComparison)comparison))));

    [Fact]
    public void Details_results_are_incomplete_not_presence_results()
    {
        Assert.Equal(CrashPresenceOutcome.Incomplete, CrashEventPresence.Classify(Match));
        Assert.Equal(CrashPresenceOutcome.Incomplete, CrashEventPresence.Classify(
            new CrashQueryVisited(new CrashEventMatched(
                new("80131623", "private.dll", @"C:\private\private.dll")))));
        Assert.Equal(CrashPresenceOutcome.Incomplete, CrashEventPresence.Classify(
            new CrashQueryConstructed(PrivateFailure)));
    }

    [Theory]
    [InlineData((int)CrashIdentityComparison.Same)]
    [InlineData((int)CrashIdentityComparison.BirthDifferent)]
    public async Task Presence_observer_uses_held_subject_but_never_sets_precision_proof(
        int comparison)
    {
        using var fixture = new ObserverFixture(presenceOnly: true);
        fixture.QueryPolicy = (_, _) => new CrashQueryPresence(
            new CrashIdentityCompared((CrashIdentityComparison)comparison));
        await fixture.Run();
        Assert.False(fixture.Observer.Proof.Verified);
        Assert.False(fixture.Observer.Proof.Matched);
        Assert.False(fixture.Observer.Proof.LowerEmpty || fixture.Observer.Proof.UpperEmpty);
        Assert.Equal(CrashCorrelationPlan.Create(Terminal).Exact,
            Assert.Single(fixture.Requests));
        Assert.Empty(fixture.Summaries);
        fixture.AssertQueryFences();
        Assert.Equal(CrashExit, fixture.Scope.Recheck().ExitCode);
    }

    [Fact]
    public async Task Original_precision_observer_cannot_accept_a_presence_result()
    {
        using var fixture = new ObserverFixture();
        fixture.QueryPolicy = (_, _) => PresenceSame;
        await Assert.ThrowsAsync<CrashCorrelationException>(fixture.Run);
        Assert.Equal(CrashCorrelationReason.EnumerationIncomplete, fixture.Observer.Proof.Reason);
        Assert.False(fixture.Observer.Proof.Verified || fixture.Observer.Proof.Matched);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task Presence_polling_retains_the_original_window_and_budget()
    {
        using var fixture = new ObserverFixture(presenceOnly: true);
        fixture.QueryPolicy = (_, _) => new CrashQueryEmpty();
        await Assert.ThrowsAsync<CrashCorrelationException>(fixture.Run);
        Assert.Equal(CrashPresenceOutcome.Empty, fixture.Observer.Presence);
        // This clock starts at the actual terminal exit. The event-window end
        // and poll-budget end coincide; the authority checks the window first.
        Assert.Equal(CrashCorrelationReason.WindowExpired, fixture.Observer.Proof.Reason);
        Assert.Equal(CrashEventObserver.MaximumPolls - 1, fixture.Requests.Count);
        Assert.Equal(CrashEventObserver.PollBudget, fixture.Clock.Elapsed);
        var timing = Metrics(fixture.Observer);
        Assert.Equal((long)CrashEventExpiry.Window, timing["expiry"]);
        Assert.Equal((long)CrashEventExpiryLocation.PollEntry, timing["location"]);
        Assert.All(fixture.Requests, request =>
            Assert.Equal(CrashCorrelationPlan.Create(Terminal).Exact, request));
        Assert.False(fixture.Observer.Proof.Verified);
        fixture.AssertQueryFences();
    }

    [Fact]
    public async Task Presence_return_after_window_expiry_is_rejected_by_real_postfences()
    {
        using var fixture = new ObserverFixture(presenceOnly: true);
        fixture.QueryPolicy = (_, _) =>
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(21));
            return PresenceSame;
        };
        await Assert.ThrowsAsync<CrashCorrelationException>(fixture.Run);
        Assert.Equal(CrashPresenceOutcome.NotObserved, fixture.Observer.Presence);
        Assert.Equal(CrashCorrelationReason.WindowExpired, fixture.Observer.Proof.Reason);
        Assert.Equal(1L, Metrics(fixture.Observer)["rejected"]);
        Assert.False(fixture.Observer.Proof.Verified);
    }

    [Fact]
    public async Task Presence_subject_change_after_query_is_not_a_reportable_candidate()
    {
        using var fixture = new ObserverFixture(presenceOnly: true);
        fixture.QueryPolicy = (_, _) =>
        {
            fixture.Current = Terminal with { Process = new(43, Birth) };
            return PresenceSame;
        };
        var error = await Assert.ThrowsAsync<AggregateException>(fixture.Run);
        AssertProcessMismatchFences(fixture, error);
        Assert.Equal(CrashPresenceOutcome.NotObserved, fixture.Observer.Presence);
    }

    [Theory]
    [InlineData((int)CrashIdentityComparison.Same, (int)CrashPresenceOutcome.SameIdentity)]
    [InlineData((int)CrashIdentityComparison.BirthDifferent, (int)CrashPresenceOutcome.DifferentBirth)]
    public async Task Presence_command_emits_failure_facts_only_after_owned_cleanup(
        int comparison, int outcome)
    {
        var fixture = new CommandFixture(Metadata)
        {
            QueryPolicy = _ => new CrashQueryPresence(
                new CrashIdentityCompared((CrashIdentityComparison)comparison))
        };
        Assert.Equal(1, await RunPresence(fixture));
        AssertPresenceCommandOutput(fixture, (CrashPresenceOutcome)outcome);
        Assert.Equal(0L, Metrics(fixture.Summaries[^1])["controlStatus"]);
        AssertCommandQueryFences(fixture.Calls);
        Assert.Single(fixture.Requests);
    }

    private static void AssertPresenceCommandOutput(
        CommandFixture fixture, CrashPresenceOutcome outcome)
    {
        fixture.AssertOwnedCleanup();
        Assert.False(fixture.ControlPublished || fixture.EventPublished);
        Assert.Empty(fixture.ControlBytes);
        Assert.Empty(fixture.EventBytes);
        Assert.Equal(2, fixture.Summaries.Count);
        Assert.StartsWith(DiagnosticPrefix, fixture.Summaries[0]);
        Assert.StartsWith(PresencePrefix, fixture.Summaries[1]);
        Assert.Equal((long)outcome, Metrics(fixture.Summaries[1])["outcome"]);
        Assert.Equal(1L, Metrics(fixture.Summaries[1])["status"]);
        Assert.Equal(0L, Metrics(fixture.Summaries[1])["precisionAccepted"]);
        Assert.Equal(1L, Metrics(fixture.Summaries[1])["receiptClosed"]);
        Assert.True(fixture.Calls.IndexOf(Call.DisposeReceipt) <
            fixture.Calls.IndexOf(Call.DiagnosticSummary));
        Assert.InRange(Encoding.UTF8.GetByteCount(string.Concat(fixture.Summaries)),
            1, CrashEventPresenceReport.MaximumOutputBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Presence_command_refusals_do_not_publish_or_claim_acceptance(int failure)
    {
        var fixture = new CommandFixture(Metadata)
        {
            QueryPolicy = _ => failure switch
            {
                0 => new CrashQueryAmbiguous(),
                1 => new CrashQueryPresence(new CrashIdentityCompared(
                    CrashIdentityComparison.ProcessDifferent)),
                2 => new CrashQueryPresence(new CrashIdentityFailure(
                    [new(CrashEventFailureKind.Refused, CrashEventReason.VariantType)])),
                _ => Match
            }
        };
        Assert.Equal(1, await RunPresence(fixture));
        AssertPresenceCommandOutput(fixture, failure switch
        {
            0 => CrashPresenceOutcome.Ambiguous,
            1 => CrashPresenceOutcome.DifferentProcess,
            2 => CrashPresenceOutcome.ReaderFailure,
            _ => CrashPresenceOutcome.Incomplete
        });
    }

    [Fact]
    public async Task Presence_receipt_cleanup_failure_remains_failure_after_child_cleanup()
    {
        var fixture = new CommandFixture(Metadata)
            { QueryPolicy = _ => PresenceSame, ReceiptCleanupFailure = true };
        Assert.Equal(1, await RunPresence(fixture));
        Assert.False(fixture.ControlPublished || fixture.EventPublished);
        Assert.True(fixture.ChildDisposed);
        var row = Metrics(fixture.Summaries[^1]);
        Assert.Equal((long)CrashCorrelationReason.ReceiptCleanup, row["reason"]);
        Assert.Equal(0L, row["receiptClosed"]);
        Assert.Equal(1L, row["status"]);
    }

    [Fact]
    public async Task Presence_unexpected_query_error_survives_real_cleanup_without_reports()
    {
        var error = new ArithmeticException(PrivateFailure);
        var fixture = new CommandFixture(Metadata) { QueryPolicy = _ => throw error };
        Assert.Same(error, await Assert.ThrowsAsync<ArithmeticException>(
            () => RunPresence(fixture)));
        fixture.AssertOwnedCleanup();
        Assert.False(fixture.ControlPublished || fixture.EventPublished);
        Assert.DoesNotContain(fixture.Summaries,
            row => row.StartsWith(PresencePrefix, StringComparison.Ordinal));
        AssertSafeDiagnostics(fixture, [error]);
    }

    [Fact]
    public async Task Presence_sampling_faults_remain_faults_after_actual_owned_cleanup()
    {
        var sample = new IOException(PrivateFailure);
        var fixture = new CommandFixture(Metadata)
        {
            QueryPolicy = _ => PresenceSame,
            SamplingClock = new Clock { UtcError = sample }
        };
        var failure = await Assert.ThrowsAsync<CrashEventSamplingException>(
            () => RunPresence(fixture));
        Assert.Same(sample, Assert.Single(failure.Flatten().InnerExceptions));
        fixture.AssertOwnedCleanup();
        Assert.False(fixture.ControlPublished || fixture.EventPublished);
        Assert.DoesNotContain(fixture.Summaries,
            row => row.StartsWith(PresencePrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Presence_does_not_report_a_settled_lifetime_while_actual_reap_is_pending()
    {
        var error = new ArithmeticException(PrivateFailure);
        var fixture = new CommandFixture(Metadata) { StartError = error, DeferExit = true };
        var run = RunPresence(fixture);
        Assert.False(run.IsCompleted);
        fixture.AssertReapPending();
        Assert.Empty(fixture.Summaries);
        fixture.CompleteExit();
        Assert.Same(error, await Assert.ThrowsAsync<ArithmeticException>(() => run));
        Assert.True(fixture.ChildDisposed && fixture.Receipt.Closed);
        Assert.False(fixture.ControlPublished || fixture.EventPublished);
        Assert.DoesNotContain(fixture.Summaries,
            row => row.StartsWith(PresencePrefix, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Presence_guard_or_schema_failure_cannot_start_or_publish(bool guard)
    {
        var fixture = new CommandFixture(guard ? Metadata : "{}") { GuardFailure = guard };
        Assert.Equal(1, await RunPresence(fixture));
        Assert.DoesNotContain(Call.Start, fixture.Calls);
        Assert.Empty(fixture.Requests);
        Assert.False(fixture.ControlPublished || fixture.EventPublished);
        var row = Metrics(Assert.Single(fixture.Summaries));
        Assert.Equal(0L, row["outcome"]);
        Assert.Equal(1L, row["status"]);
        Assert.Equal(0L, row["precisionAccepted"]);
    }

    [Theory]
    [InlineData("startup-crash-event-presence", "42")]
    [InlineData("startup-crash-event-presence", "--pid")]
    [InlineData("startup-crash-event-probe", "")]
    [InlineData("startup-crash-probe", "")]
    public async Task Presence_dispatch_accepts_only_its_one_fixed_command(
        string command, string extra)
    {
        var fixture = new CommandFixture(Metadata);
        var arguments = extra.Length == 0 ? new[] { command } : [command, extra];
        Assert.Equal(2, await CrashEventControl.DispatchPresenceAsync(
            arguments, fixture.Effects, CancellationToken.None));
        Assert.Empty(fixture.Calls);
        Assert.Empty(fixture.Summaries);
    }

    [Fact]
    public async Task Original_event_dispatch_does_not_adopt_the_presence_command()
    {
        var fixture = new CommandFixture(Metadata);
        Assert.Equal(2, await CrashEventControl.DispatchAsync(
            [CrashEventControl.PresenceCommand], fixture.Effects, CancellationToken.None));
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public void Presence_protocol_is_numeric_and_has_an_exact_bounded_maximum()
    {
        var result = PresenceMaximumResult();
        var row = CrashEventPresenceReport.Summary(CrashPresenceOutcome.Incomplete, result);
        Assert.Equal(CrashEventPresenceReport.MaximumSummaryBytes,
            Encoding.ASCII.GetByteCount(row));
        Assert.Equal(206, row.Length);
        Assert.Equal<string>(("v status outcome reason controlStatus controlError cleanupError " +
            "receiptClosed polls queries originalGatePassed precisionAccepted nativeAcceptance")
            .Split(' '), Metrics(row).Keys);
        Assert.EndsWith("\n", row);
        Assert.DoesNotContain("\r", row);
        Assert.DoesNotContain(Image, row);
        Assert.DoesNotContain(PrivateFailure, row);
        Assert.DoesNotContain(Birth.ToString(CultureInfo.InvariantCulture), row);
        Assert.Equal(599, CrashEventPresenceReport.MaximumOutputBytes);
        Assert.True(Encoding.ASCII.GetByteCount(row +
            SaturatedTelemetry().Summary(new())) <= CrashEventPresenceReport.MaximumOutputBytes);
    }

    private static CrashEventControlResult PresenceMaximumResult() => new(
        new() { Error = CrashFault.Deadline, CleanupError = (CrashCleanup)255 },
        new()
        {
            Reason = CrashCorrelationReason.Unexpected,
            Polls = CrashEventObserver.MaximumPolls,
            Queries = CrashEventObserver.MaximumPolls
        }, true);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Presence_protocol_refuses_precision_claims_and_out_of_range_facts(int defect)
    {
        var result = PresenceMaximumResult();
        result = defect switch
        {
            0 => result with { Proof = result.Proof with { Matched = true } },
            1 => result with { Proof = result.Proof with { LowerEmpty = true } },
            2 => result with { Proof = result.Proof with { Queries = 82 } },
            3 => result with { Proof = result.Proof with { Polls = -1 } },
            _ => result with { Control = result.Control with { CleanupError = (CrashCleanup)256 } }
        };
        Assert.Throws<CrashCorrelationException>(() =>
            CrashEventPresenceReport.Summary(CrashPresenceOutcome.Empty, result));
        Assert.Throws<CrashCorrelationException>(() =>
            CrashEventPresenceReport.Summary((CrashPresenceOutcome)10, PresenceMaximumResult()));
    }
}
