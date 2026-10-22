using System.Globalization;
using Hermes.Servicing.Acceptance;
using Xunit;

namespace Hermes.Servicing.Tests;

public sealed class CrashEventQueryTests
{
    private const ulong Birth = 9007199254740993UL;
    private const string BirthLiteral = "9007199254740993";
    private const string Image = @"C:\owned\child.exe";
    private static nint Query => (nint)11;
    private static nint First => (nint)21;
    private static nint Second => (nint)22;
    private static DateTimeOffset Start =>
        new DateTimeOffset(2026, 10, 22, 16, 5, 0, TimeSpan.Zero).AddTicks(1);
    private static DateTimeOffset End =>
        new DateTimeOffset(2026, 10, 22, 16, 6, 0, TimeSpan.Zero).AddTicks(2);
    private static CrashQueryRequest Request =>
        new(new(uint.MaxValue, Birth, Image), new(Start, End));

    private enum Call { Open, Next, Read, CloseFirst, CloseSecond, CloseQuery }

    private static string Golden(
        string birth = BirthLiteral, string image = Image, string pid = "4294967295") =>
        "*[System[Provider[@Name='Application Error'] and EventID=1000 " +
        "and Version=0 and TimeCreated[@SystemTime>='2026-10-22T16:05:00.0000001Z' " +
        "and @SystemTime<='2026-10-22T16:06:00.0000002Z']] and EventData[" +
        $"Data[@Name='ProcessId']={pid} and " +
        $"Data[@Name='ProcessCreationTime']={birth} and " +
        $"Data[@Name='AppPath']='{image}']]";

    [Fact]
    public void Construction_has_all_fixed_predicates_and_remains_unproven()
    {
        var constructed = Construct(Request);
        Assert.Equal(Golden(), constructed.Expression);
        Assert.Equal(CrashQueryPrecision.Unproven, constructed.Precision);
        Assert.Equal(nameof(CrashQueryConstructed), constructed.ToString());
        Assert.Equal(nameof(CrashQueryRequest), Request.ToString());
        Assert.Equal(nameof(CrashQueryWindow), Request.Window.ToString());
    }

    public static IEnumerable<object[]> BirthGoldens() =>
        new (ulong Value, string Literal)[]
        {
            (1, "1"),
            (9007199254740991, "9007199254740991"),
            (9007199254740992, "9007199254740992"),
            (9007199254740993, "9007199254740993"),
            (9223372036854775807, "9223372036854775807"),
            (9223372036854775808, "9223372036854775808"),
            (18446744073709551614, "18446744073709551614"),
            (18446744073709551615, "18446744073709551615")
        }.Select(item => new object[] { item.Value, item.Literal });

    [Theory]
    [MemberData(nameof(BirthGoldens))]
    public void UInt64_literals_are_canonical_without_numeric_rounding(
        ulong birth, string literal)
    {
        Assert.Equal(Golden(literal), Construct(WithBirth(birth)).Expression);
        if (birth > 1)
            Assert.NotEqual(Construct(WithBirth(birth)).Expression,
                Construct(WithBirth(birth - 1)).Expression);
        if (birth < ulong.MaxValue)
            Assert.NotEqual(Construct(WithBirth(birth)).Expression,
                Construct(WithBirth(birth + 1)).Expression);
    }

    [Theory]
    [InlineData(1u, "1")]
    [InlineData(uint.MaxValue, "4294967295")]
    public void UInt32_pid_literals_are_canonical(uint pid, string literal)
    {
        var request = Request with { Subject = Request.Subject with { ProcessId = pid } };
        Assert.Equal(Golden(pid: literal), Construct(request).Expression);
    }

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("fr-FR")]
    public void Construction_is_invariant_under_current_culture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(Golden(), Construct(Request).Expression);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Zero_pid_or_birth_is_refused_without_native_calls()
    {
        AssertRefused(Request with
        {
            Subject = Request.Subject with { ProcessId = 0 }
        }, CrashQueryReason.InvalidSubject);
        AssertRefused(WithBirth(0), CrashQueryReason.InvalidSubject);
    }

    [Theory]
    [InlineData("")]
    [InlineData("child.exe")]
    [InlineData(@"C:child.exe")]
    [InlineData(@"C:/owned/child.exe")]
    [InlineData(@"\\server\owned\child.exe")]
    [InlineData(@"\\?\C:\owned\child.exe")]
    [InlineData(@"C:\owned\.\child.exe")]
    [InlineData(@"C:\owned\..\child.exe")]
    [InlineData(@"C:\owned\\child.exe")]
    [InlineData(@"C:\owned\child.exe.")]
    [InlineData(@"C:\owned\child.exe ")]
    [InlineData(@"C:\ owned\child.exe")]
    [InlineData(@"C:\owned\chi'ld.exe")]
    [InlineData("C:\\owned\\chi\"ld.exe")]
    [InlineData(@"C:\owned\child.exe' or EventID=1 or '")]
    [InlineData("C:\\owned\\bad\u0000.exe")]
    [InlineData("C:\\owned\\bad\u0001.exe")]
    [InlineData("C:\\owned\\bad\u007f.exe")]
    [InlineData("C:\\owned\\\u00e9.exe")]
    [InlineData("C:\\owned\\\U0001f680.exe")]
    public void Unsupported_or_noncanonical_paths_refuse_before_capabilities(string path)
    {
        AssertRefused(WithPath(path), CrashQueryReason.InvalidSubject);
    }

    [Theory]
    [InlineData(0xd800)]
    [InlineData(0xdc00)]
    public void Runtime_unpaired_surrogates_are_not_replaced_or_escaped(int value)
    {
        var path = @"C:\owned\" + new string((char)value, 1) + ".exe";
        AssertRefused(WithPath(path), CrashQueryReason.InvalidSubject);
    }

    [Theory]
    [InlineData(@"c:\owned\child.exe")]
    [InlineData(@"C:\owned\CHILD.exe")]
    [InlineData(@"C:\owned\child [x]=1.exe")]
    public void Supported_path_text_is_preserved_exactly(string path)
    {
        Assert.Equal(Golden(image: path), Construct(WithPath(path)).Expression);
    }

    [Fact]
    public void Path_and_utf16_query_lengths_are_bounded_inclusively()
    {
        var overhead = Construct(WithPath(@"C:\x")).Expression.Length - 4;
        var length = CrashEventQuery.MaximumQueryCharacters - overhead;
        var path = @"C:\" + new string('a', length - 3);
        Assert.Equal(CrashEventQuery.MaximumQueryCharacters,
            Construct(WithPath(path)).Expression.Length);
        AssertRefused(WithPath(path + "a"), CrashQueryReason.QueryLength);
        AssertRefused(WithPath(@"C:\" + new string('a',
            CrashEventValues.MaximumCharacters - 2)), CrashQueryReason.InvalidSubject);
    }

    public static IEnumerable<object[]> InvalidWindows() =>
        new[]
        {
            new CrashQueryWindow(Start, Start),
            new CrashQueryWindow(End, Start),
            new CrashQueryWindow(Start, Start.AddSeconds(121)),
            new CrashQueryWindow(Start, Start.AddSeconds(120).AddTicks(1)),
            new CrashQueryWindow(Start.ToOffset(TimeSpan.FromHours(1)), End),
            new CrashQueryWindow(Start, End.ToOffset(TimeSpan.FromHours(-1))),
            new CrashQueryWindow(DateTimeOffset.MinValue,
                DateTimeOffset.MinValue.AddTicks(1)),
            new CrashQueryWindow(
                new DateTimeOffset(1600, 12, 31, 23, 59, 59, TimeSpan.Zero),
                new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero))
        }.Select(window => new object[] { window });

    [Theory]
    [MemberData(nameof(InvalidWindows))]
    internal void Invalid_windows_refuse_without_normalizing_or_rounding(
        CrashQueryWindow window)
    {
        AssertRefused(Request with { Window = window }, CrashQueryReason.InvalidWindow);
    }

    [Fact]
    public void Supported_windows_preserve_ticks_and_do_not_convert_birth_to_time()
    {
        var maximum = new CrashQueryWindow(
            DateTimeOffset.MaxValue.AddTicks(-1), DateTimeOffset.MaxValue);
        var expression = Construct(WithBirth(ulong.MaxValue) with { Window = maximum })
            .Expression;
        Assert.Contains("@SystemTime>='9999-12-31T23:59:59.9999998Z'", expression);
        Assert.Contains("@SystemTime<='9999-12-31T23:59:59.9999999Z'", expression);
        Assert.IsType<CrashQueryConstructed>(CrashEventQuery.Construct(
            Request with { Window = new(Start, Start.AddSeconds(120)) }));
        Assert.IsType<CrashQueryConstructed>(CrashEventQuery.Construct(
            Request with { Window = new(Start, Start.AddTicks(1)) }));
    }

    [Theory]
    [InlineData((int)CrashQueryAdmission.NotEstablished)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void Provider_and_schema_admission_is_an_explicit_precondition(int admission)
    {
        var api = new FakeApi();
        AssertFault(CrashEventQuery.EnumerateCandidate(
            api, Request, (CrashQueryAdmission)admission),
            CrashQueryReason.AdmissionRequired);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void Unique_batch_visits_while_both_handles_live_then_closes_in_order()
    {
        var thread = Environment.CurrentManagedThreadId;
        var api = new FakeApi();
        var visited = Assert.IsType<CrashQueryVisited>(Enumerate(api));
        Assert.Same(api.Reading, visited.Read);
        Assert.Equal(CrashQueryPrecision.Unproven, visited.Precision);
        Assert.Equal(nameof(CrashQueryVisited), visited.ToString());
        Assert.Equal<Call>(
            [Call.Open, Call.Next, Call.Read, Call.CloseFirst, Call.CloseQuery], api.Calls);
        Assert.All(api.Threads, value => Assert.Equal(thread, value));
        api.AssertSettled();
    }

    [Fact]
    public void Distinct_constructed_request_reaches_all_exact_native_arguments()
    {
        const string path = @"c:\owned\OTHER.exe";
        var request = WithBirth(ulong.MaxValue) with
        {
            Subject = new(1, ulong.MaxValue, path)
        };
        var api = new FakeApi
        {
            ExpectedRequest = request,
            ExpectedExpression = Golden("18446744073709551615", path, "1")
        };
        Assert.IsType<CrashQueryVisited>(Enumerate(api, request));
        api.AssertSettled();
    }

    [Fact]
    public void Only_no_more_items_with_an_empty_batch_is_a_legitimate_end()
    {
        var api = new FakeApi
        {
            Handles = [0, 0],
            NextCall = new(false, 259, 0)
        };
        Assert.IsType<CrashQueryEmpty>(Enumerate(api));
        Assert.Equal<Call>([Call.Open, Call.Next, Call.CloseQuery], api.Calls);
        api.AssertSettled();
    }

    [Fact]
    public void Two_events_are_ambiguous_never_visited_and_close_in_reverse_order()
    {
        var api = TwoEvents();
        Assert.IsType<CrashQueryAmbiguous>(Enumerate(api));
        Assert.Equal<Call>(
            [Call.Open, Call.Next, Call.CloseSecond, Call.CloseFirst, Call.CloseQuery],
            api.Calls);
        api.AssertSettled();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(1460)]
    [InlineData(1722)]
    [InlineData(int.MinValue)]
    public void Other_next_errors_including_timeout_are_unavailable(int error)
    {
        var api = new FakeApi
        {
            Handles = [0, 0],
            NextCall = new(false, error, 0)
        };
        AssertFault(Enumerate(api), CrashQueryReason.Next, error,
            CrashEventFailureKind.Unavailable);
        Assert.DoesNotContain(Call.Read, api.Calls);
        api.AssertSettled();
    }

    [Theory]
    [InlineData(0L, 0)]
    [InlineData(0L, 5)]
    [InlineData(-1L, 259)]
    public void Invalid_query_handles_are_not_closed_or_enumerated(long handle, int error)
    {
        var api = new FakeApi { OpenCall = new((nint)handle, error) };
        AssertFault(Enumerate(api), CrashQueryReason.Open, error,
            CrashEventFailureKind.Unavailable);
        Assert.Equal<Call>([Call.Open], api.Calls);
        api.AssertSettled();
    }

    [Fact]
    public void Inconsistent_open_result_still_owns_and_closes_its_valid_handle()
    {
        var api = new FakeApi { OpenCall = new(Query, 5) };
        AssertFault(Enumerate(api), CrashQueryReason.Open, 5);
        Assert.Equal<Call>([Call.Open, Call.CloseQuery], api.Calls);
        api.AssertSettled();
    }

    [Theory]
    [InlineData(true, 0, 0u, 0L, 0L)]
    [InlineData(true, 0, 3u, 21L, 22L)]
    [InlineData(true, 0, uint.MaxValue, 21L, 22L)]
    [InlineData(true, 5, 1u, 21L, 0L)]
    [InlineData(true, 0, 1u, 0L, 0L)]
    [InlineData(true, 0, 1u, -1L, 0L)]
    [InlineData(true, 0, 1u, 11L, 0L)]
    [InlineData(true, 0, 1u, 21L, 22L)]
    [InlineData(true, 0, 2u, 21L, 0L)]
    [InlineData(true, 0, 2u, 21L, -1L)]
    [InlineData(true, 0, 2u, 21L, 21L)]
    [InlineData(true, 0, 2u, 21L, 11L)]
    [InlineData(false, 259, 1u, 21L, 0L)]
    [InlineData(false, 259, 0u, 21L, 0L)]
    [InlineData(false, 5, 1u, 21L, 0L)]
    [InlineData(false, 1460, 2u, 21L, 22L)]
    public void Malformed_or_partial_batches_never_visit_and_close_all_real_outputs(
        bool success, int error, uint count, long first, long second)
    {
        var api = new FakeApi
        {
            Handles = [(nint)first, (nint)second],
            NextCall = new(success, error, count)
        };
        AssertFault(Enumerate(api), CrashQueryReason.MalformedBatch, error);
        Assert.DoesNotContain(Call.Read, api.Calls);
        api.AssertSettled();
    }

    [Fact]
    public void Duplicate_output_is_closed_once_before_query()
    {
        var api = new FakeApi
        {
            Handles = [First, First],
            NextCall = new(true, 0, 2)
        };
        AssertFault(Enumerate(api), CrashQueryReason.MalformedBatch);
        Assert.Equal<Call>(
            [Call.Open, Call.Next, Call.CloseFirst, Call.CloseQuery], api.Calls);
        api.AssertSettled();
    }

    [Fact]
    public void Client_mismatch_and_reader_failure_are_not_upgraded_to_matches()
    {
        CrashEventRead[] readings =
        [
            new CrashEventMismatch(CrashEventReason.CreationTimeMismatch),
            CrashEventValues.Failure(
                CrashEventFailureKind.Unavailable, CrashEventReason.Render)
        ];
        foreach (var reading in readings)
        {
            var api = new FakeApi { Reading = reading };
            Assert.Same(reading, Assert.IsType<CrashQueryVisited>(Enumerate(api)).Read);
            api.AssertSettled();
        }
    }

    [Theory]
    [InlineData((int)Call.CloseFirst)]
    [InlineData((int)Call.CloseQuery)]
    public void Cleanup_fault_invalidates_an_otherwise_visited_candidate(int failed)
    {
        var call = (Call)failed;
        var api = new FakeApi { CloseFailures = [call] };
        AssertFault(Enumerate(api), call == Call.CloseQuery
            ? CrashQueryReason.QueryClose : CrashQueryReason.EventClose, 5);
        api.AssertSettled();
    }

    [Fact]
    public void Cleanup_fault_is_not_disguised_as_no_match()
    {
        var api = new FakeApi
        {
            Handles = [0, 0],
            NextCall = new(false, 259, 0),
            CloseFailures = [Call.CloseQuery]
        };
        AssertFault(Enumerate(api), CrashQueryReason.QueryClose, 5);
        api.AssertSettled();
    }

    [Fact]
    public void Every_cleanup_fault_is_retained_in_reverse_ownership_order()
    {
        var api = new FakeApi
        {
            Handles = [First, Second],
            NextCall = new(true, 0, 2),
            CloseFailures = [Call.CloseFirst, Call.CloseSecond, Call.CloseQuery]
        };
        var failure = Assert.IsType<CrashQueryFailure>(Enumerate(api));
        Assert.Equal<CrashQueryReason>(
            [CrashQueryReason.EventClose, CrashQueryReason.EventClose,
                CrashQueryReason.QueryClose], failure.Faults.Select(fault => fault.Reason));
        Assert.All(failure.Faults, fault => Assert.Equal(5, fault.NativeError));
        Assert.DoesNotContain(Call.Read, api.Calls);
        api.AssertSettled();
    }

    [Fact]
    public void Enumeration_error_and_query_cleanup_error_are_both_retained()
    {
        var api = new FakeApi
        {
            Handles = [0, 0],
            NextCall = new(false, 1460, 0),
            CloseFailures = [Call.CloseQuery]
        };
        var failure = Assert.IsType<CrashQueryFailure>(Enumerate(api));
        Assert.Equal<CrashQueryFault>(
            [new(CrashEventFailureKind.Unavailable, CrashQueryReason.Next, 1460),
                new(CrashEventFailureKind.Refused, CrashQueryReason.QueryClose, 5)],
            failure.Faults);
        api.AssertSettled();
    }

    [Theory]
    [InlineData((int)Call.Open)]
    [InlineData((int)Call.Next)]
    [InlineData((int)Call.Read)]
    [InlineData((int)Call.CloseFirst)]
    [InlineData((int)Call.CloseSecond)]
    [InlineData((int)Call.CloseQuery)]
    public void Unexpected_exceptions_rethrow_the_original_after_owned_cleanup(int failed)
    {
        var call = (Call)failed;
        var api = new FakeApi
        {
            Handles = call == Call.CloseSecond ? [First, Second] : [First, 0],
            NextCall = new(true, 0, call == Call.CloseSecond ? 2u : 1u),
            UnexpectedAt = call
        };
        Assert.Same(api.Unexpected,
            Assert.Throws<ArithmeticException>(() => Enumerate(api)));
        api.AssertSettled();
    }

    [Fact]
    public void Next_exception_after_writing_partial_outputs_still_closes_them()
    {
        var api = new FakeApi
        {
            Handles = [First, Second],
            UnexpectedAt = Call.Next
        };
        Assert.Same(api.Unexpected,
            Assert.Throws<ArithmeticException>(() => Enumerate(api)));
        Assert.Equal<Call>(
            [Call.Open, Call.Next, Call.CloseSecond, Call.CloseFirst, Call.CloseQuery],
            api.Calls);
        api.AssertSettled();
    }

    [Fact]
    public void Unexpected_reader_error_and_all_cleanup_faults_are_preserved()
    {
        var api = new FakeApi
        {
            UnexpectedAt = Call.Read,
            CloseFailures = [Call.CloseFirst, Call.CloseQuery]
        };
        var error = Assert.Throws<AggregateException>(() => Enumerate(api));
        Assert.Same(api.Unexpected, error.InnerExceptions[0]);
        Assert.Equal<CrashQueryReason>(
            [CrashQueryReason.EventClose, CrashQueryReason.QueryClose],
            error.InnerExceptions.OfType<CrashQueryException>()
                .SelectMany(item => item.Faults).Select(fault => fault.Reason));
        api.AssertSettled();
    }

    [Fact]
    public void Unexpected_cleanup_cannot_replace_an_enumeration_fault()
    {
        var api = new FakeApi
        {
            NextCall = new(false, 1460, 1),
            UnexpectedAt = Call.CloseFirst,
            CloseFailures = [Call.CloseQuery]
        };
        var error = Assert.Throws<AggregateException>(() => Enumerate(api));
        Assert.Contains(api.Unexpected, error.InnerExceptions);
        Assert.Equal<CrashQueryReason>(
            [CrashQueryReason.MalformedBatch, CrashQueryReason.QueryClose],
            error.InnerExceptions.OfType<CrashQueryException>()
                .SelectMany(item => item.Faults).Select(fault => fault.Reason));
        api.AssertSettled();
    }

    [Fact]
    public void Reader_exception_is_not_blanket_converted_to_a_query_observation()
    {
        var readerError = new CrashEventValuesException(
            [new(CrashEventFailureKind.Refused, CrashEventReason.ContextClose)]);
        var api = new FakeApi { ReaderException = readerError };
        Assert.Same(readerError,
            Assert.Throws<CrashEventValuesException>(() => Enumerate(api)));
        api.AssertSettled();
    }

    private static CrashQueryRequest WithBirth(ulong birth) =>
        Request with { Subject = Request.Subject with { CreationFileTime = birth } };

    private static CrashQueryRequest WithPath(string path) =>
        Request with { Subject = Request.Subject with { ActualImagePath = path } };

    private static CrashQueryConstructed Construct(CrashQueryRequest request) =>
        Assert.IsType<CrashQueryConstructed>(CrashEventQuery.Construct(request));

    private static CrashQueryResult Enumerate(
        FakeApi api, CrashQueryRequest? request = null) =>
        CrashEventQuery.EnumerateCandidate(api, request ?? Request,
            CrashQueryAdmission.CallerAttestsFixedProviderAndSchema);

    private static FakeApi TwoEvents() => new()
    {
        Handles = [First, Second],
        NextCall = new(true, 0, 2)
    };

    private static void AssertRefused(
        CrashQueryRequest request, CrashQueryReason reason)
    {
        AssertFault(CrashEventQuery.Construct(request), reason);
        var api = new FakeApi();
        AssertFault(Enumerate(api, request), reason);
        Assert.Empty(api.Calls);
    }

    private static void AssertFault(
        CrashQueryResult result, CrashQueryReason reason, int nativeError = 0,
        CrashEventFailureKind kind = CrashEventFailureKind.Refused)
    {
        var failure = Assert.IsType<CrashQueryFailure>(result);
        Assert.Equal(new CrashQueryFault(kind, reason, nativeError),
            Assert.Single(failure.Faults));
    }

    private sealed class FakeApi : ICrashEventQueryApi
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly List<Call> _calls = [];
        private readonly List<int> _threads = [];
        private readonly HashSet<nint> _owned = [];
        private readonly HashSet<nint> _issued = [];
        private readonly List<nint> _closeAttempts = [];

        internal IReadOnlyList<Call> Calls => _calls;
        internal IReadOnlyList<int> Threads => _threads;
        internal CrashQueryRequest ExpectedRequest { get; init; } = Request;
        internal string ExpectedExpression { get; init; } = Golden();
        internal CrashQueryOpenCall OpenCall { get; init; } = new(Query, 0);
        internal CrashQueryNextCall NextCall { get; init; } = new(true, 0, 1);
        internal IReadOnlyList<nint> Handles { get; init; } = [First, 0];
        internal IReadOnlyList<Call> CloseFailures { get; init; } = [];
        internal Call? UnexpectedAt { get; init; }
        internal Exception? ReaderException { get; init; }
        internal ArithmeticException Unexpected { get; } =
            new("Synthetic query capability failure.");
        internal CrashEventRead Reading { get; init; } = new CrashEventMatched(
            new("80131623", "module.dll", @"C:\owned\module.dll"));

        public CrashQueryOpenCall Open(
            nint session, string channel, string expression, uint flags)
        {
            Assert.Equal((nint)0, session);
            Assert.Equal("Application", channel);
            Assert.Equal(ExpectedExpression, expression);
            Assert.Equal(0x101u, flags);
            Hit(Call.Open);
            Issue(OpenCall.Handle);
            return OpenCall;
        }

        public CrashQueryNextCall Next(
            nint query, nint[] events, uint timeoutMilliseconds, uint flags)
        {
            Assert.Equal(Query, query);
            Assert.Contains(query, _owned);
            Assert.Equal(2, events.Length);
            Assert.All(events, handle => Assert.Equal((nint)0, handle));
            Assert.Equal(0u, timeoutMilliseconds);
            Assert.Equal(0u, flags);
            foreach (var index in Enumerable.Range(0, 2))
            {
                events[index] = Handles[index];
                Issue(events[index]);
            }
            Hit(Call.Next);
            return NextCall;
        }

        public CrashEventRead Read(nint borrowedEvent, CrashEventSubject expected)
        {
            Assert.Equal(First, borrowedEvent);
            Assert.Contains(Query, _owned);
            Assert.Contains(borrowedEvent, _owned);
            Assert.Equal(ExpectedRequest.Subject, expected);
            Assert.DoesNotContain(borrowedEvent, _closeAttempts);
            Hit(Call.Read);
            if (ReaderException is { } error)
                throw error;
            return Reading;
        }

        public CrashQueryCloseCall Close(nint ownedHandle)
        {
            Assert.True(_owned.Remove(ownedHandle));
            Assert.DoesNotContain(ownedHandle, _closeAttempts);
            _closeAttempts.Add(ownedHandle);
            if (ownedHandle == Query)
                Assert.Empty(_owned);
            else
                Assert.Contains(Query, _owned);
            var call = CloseCall(ownedHandle);
            Hit(call);
            return CloseFailures.Contains(call) ? new(false, 5) : new(true, 0);
        }

        private static Call CloseCall(nint handle) => handle switch
        {
            11 => Call.CloseQuery,
            21 => Call.CloseFirst,
            22 => Call.CloseSecond,
            _ => throw new InvalidOperationException("Unexpected synthetic handle.")
        };

        private void Issue(nint handle)
        {
            if (handle == 0 || handle == -1 || !_issued.Add(handle))
                return;
            Assert.True(_owned.Add(handle));
        }

        private void Hit(Call call)
        {
            var thread = Environment.CurrentManagedThreadId;
            _threads.Add(thread);
            Assert.Equal(_thread, thread);
            _calls.Add(call);
            if (UnexpectedAt == call)
                throw Unexpected;
        }

        internal void AssertSettled()
        {
            // These assert exactly-once cleanup ATTEMPTS. A false/throwing Close
            // is intentionally not represented as proof of native release.
            Assert.Empty(_owned);
            Assert.Equal<nint>(_issued.Order(), _closeAttempts.Order());
            Assert.Equal(_closeAttempts.Count, _closeAttempts.Distinct().Count());
            Assert.True(_calls.Count(call => call == Call.Next) <= 1);
            Assert.True(_calls.Count(call => call == Call.Read) <= 1);
        }
    }
}
