using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.ExceptionServices;

namespace Hermes.Servicing.Acceptance;

internal enum CrashQueryPrecision { Unproven }

// An explicit caller assertion, not verification performed by this component.
internal enum CrashQueryAdmission
{
    NotEstablished,
    CallerAttestsFixedProviderAndSchema
}

internal enum CrashQueryReason
{
    InvalidSubject, InvalidWindow, QueryLength, AdmissionRequired,
    UnsupportedPlatform, UnsupportedAbi, Open, Next, MalformedBatch,
    EventClose, QueryClose
}

// Future callers must bind these values to the actual owned lifecycle while its
// CrashObservation is held. Constructing this record proves neither origin nor
// ownership, provider/schema admission, authentication, or filtering precision.
internal sealed record CrashQueryWindow(
    DateTimeOffset StartInclusive, DateTimeOffset EndInclusive)
{
    public override string ToString() => nameof(CrashQueryWindow);
}

internal sealed record CrashQueryRequest(
    CrashEventSubject Subject, CrashQueryWindow Window)
{
    public override string ToString() => nameof(CrashQueryRequest);
}

internal abstract record CrashQueryResult
{
    internal CrashQueryPrecision Precision => CrashQueryPrecision.Unproven;
}

// This expression is private input, never a log/report/export representation.
// Enumeration accepts a request, not a caller-supplied expression or plan.
internal sealed record CrashQueryConstructed(string Expression) : CrashQueryResult
{
    public override string ToString() => nameof(CrashQueryConstructed);
}

internal sealed record CrashQueryEmpty : CrashQueryResult;
internal sealed record CrashQueryAmbiguous : CrashQueryResult;

// A single candidate in this bounded batch, not native precision/acceptance proof
// or a claim of uniqueness in a snapshot of the log.
internal sealed record CrashQueryVisited(CrashEventRead Read) : CrashQueryResult
{
    public override string ToString() => nameof(CrashQueryVisited);
}

// One first-batch candidate followed by an observed ERROR_NO_MORE_ITEMS
// end marker on this query. This is not a global/log-snapshot uniqueness claim.
internal sealed record CrashQueryCompleted(CrashEventRead Read) : CrashQueryResult
{
    public override string ToString() => nameof(CrashQueryCompleted);
}

internal readonly record struct CrashQueryFault(
    CrashEventFailureKind Kind, CrashQueryReason Reason, int NativeError = 0);

internal sealed record CrashQueryFailure(
    ImmutableArray<CrashQueryFault> Faults) : CrashQueryResult;

internal sealed class CrashQueryException(ImmutableArray<CrashQueryFault> faults)
    : IOException("Bounded crash-event query failed.")
{
    internal ImmutableArray<CrashQueryFault> Faults { get; } = faults;
}

internal readonly record struct CrashQueryOpenCall(nint Handle, int Error);
internal readonly record struct CrashQueryNextCall(
    bool Success, int Error, uint Returned);
internal readonly record struct CrashQueryCloseCall(bool Success, int Error);

// Open transfers query ownership. Every distinct valid event written into the
// two-slot output array transfers ownership, including on failure/exception.
// Read borrows the unique candidate synchronously and returns managed values.
// No capability may retain handles, schedule work, or change the output later.
internal interface ICrashEventQueryApi
{
    CrashQueryOpenCall Open(
        nint session, string channel, string expression, uint flags);
    CrashQueryNextCall Next(
        nint query, nint[] events, uint timeoutMilliseconds, uint flags);
    CrashEventRead Read(nint borrowedEvent, CrashEventSubject expected);
    CrashQueryCloseCall Close(nint ownedHandle);
}

// Fixed Application / Application Error / 1000 / v0 construction and one batch.
// All native operations and the reader run on this synchronous call stack.
//
// EvtQuery is cooperative and may be uninterruptible. Before ANY future native
// invocation, a worker-process deadline and private-job containment are mandatory;
// a CancellationToken or abandoned Task is not a substitute.
//
// Windows XPath subset documentation does NOT prove lossless UInt64 comparison.
// Decimal construction below deliberately makes no floating-point conversion.
// Before baseline use, a separate native control must demonstrate exact birth=1
// match and birth-1/birth+1=0 matches for the same PID/image/window.
internal static class CrashEventQuery
{
    internal const int MaximumQueryCharacters = 2048;
    internal const int MaximumWindowSeconds = 120;
    internal const int Capacity = 2;
    internal const uint QueryFlags = 0x101;
    internal const uint TimeoutMilliseconds = 0;
    internal const string Channel = "Application";
    private const int NoMoreItems = 259;
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    internal static CrashQueryResult Construct(CrashQueryRequest request) =>
        Attempt(() => new CrashQueryConstructed(Build(request)));

    internal static CrashQueryResult EnumerateCandidate(
        ICrashEventQueryApi api, CrashQueryRequest request,
        CrashQueryAdmission admission) => Attempt(() =>
    {
        Require(admission == CrashQueryAdmission.CallerAttestsFixedProviderAndSchema,
            CrashQueryReason.AdmissionRequired);
        var expression = Build(request);
        var lifetime = new Lifetime();
        return lifetime.Run(() => Enumerate(api, request, expression, lifetime));
    });

    internal static CrashQueryResult EnumerateCompletedCandidate(
        ICrashEventQueryApi api, CrashQueryRequest request,
        CrashQueryAdmission admission, CrashEventTelemetry? telemetry = null) => Attempt(() =>
    {
        Require(admission == CrashQueryAdmission.CallerAttestsFixedProviderAndSchema,
            CrashQueryReason.AdmissionRequired);
        var expression = Build(request);
        var lifetime = new Lifetime(telemetry);
        CrashQueryResult Run() => lifetime.Run(() =>
            EnumerateCompleted(api, request, expression, lifetime, telemetry));
        return telemetry is { } measured ? measured.Query(Run) : Run();
    });

    internal static CrashQueryFailure Failure(
        CrashEventFailureKind kind, CrashQueryReason reason, int error = 0) =>
        new([new(kind, reason, error)]);

    private static string Build(CrashQueryRequest request)
    {
        Validate(request);
        var subject = request.Subject;
        var pid = subject.ProcessId.ToString(CultureInfo.InvariantCulture);
        var birth = subject.CreationFileTime.ToString(CultureInfo.InvariantCulture);
        var start = Timestamp(request.Window.StartInclusive);
        var end = Timestamp(request.Window.EndInclusive);
        var expression =
            "*[System[Provider[@Name='Application Error'] and EventID=1000 " +
            $"and Version=0 and TimeCreated[@SystemTime>='{start}' " +
            $"and @SystemTime<='{end}']] and EventData[" +
            $"Data[@Name='ProcessId']={pid} and " +
            $"Data[@Name='ProcessCreationTime']={birth} and " +
            $"Data[@Name='AppPath']='{subject.ActualImagePath}']]";
        Require(expression.Length <= MaximumQueryCharacters, CrashQueryReason.QueryLength);
        return expression;
    }

    private static void Validate(CrashQueryRequest request)
    {
        Require(request.Subject is { ProcessId: > 0, CreationFileTime: > 0 } subject &&
            IsCanonicalPath(subject.ActualImagePath), CrashQueryReason.InvalidSubject);
        Require(request.Window is { } window &&
            window.StartInclusive.Offset == TimeSpan.Zero &&
            window.EndInclusive.Offset == TimeSpan.Zero &&
            window.StartInclusive.Year >= 1601 &&
            window.EndInclusive > window.StartInclusive &&
            window.EndInclusive - window.StartInclusive <=
                TimeSpan.FromSeconds(MaximumWindowSeconds), CrashQueryReason.InvalidWindow);
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    // A deliberately narrower lexical subset than the reader accepts.
    // No escaping, Unicode coercion, filesystem lookup, normalization, aliases,
    // environment expansion, or case folding. Unsupported paths are refusals.
    private static bool IsCanonicalPath(string? path) =>
        path is { Length: > 3 and <= CrashEventValues.MaximumCharacters } &&
        (path[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z') &&
        path[1] == ':' && path[2] == '\\' &&
        path[3..].Split('\\').All(IsPathComponent);

    private static bool IsPathComponent(string component) =>
        component.Length > 0 && component is not ("." or "..") &&
        component[0] != ' ' && component[^1] is not (' ' or '.') &&
        component.All(character => character is >= ' ' and <= '~') &&
        component.IndexOfAny(['/', ':', '*', '?', '"', '\'', '<', '>', '|']) < 0;

    private static CrashQueryResult Enumerate(
        ICrashEventQueryApi api, CrashQueryRequest request,
        string expression, Lifetime lifetime)
    {
        var query = Open(api, expression, lifetime);
        var events = PrepareEvents(api, query, lifetime);
        var call = api.Next(query, events, TimeoutMilliseconds, 0);
        ValidateBatch(call, events, query);
        if (!call.Success)
            return EndOrFail(call.Error);
        if (call.Returned == Capacity)
            return new CrashQueryAmbiguous();
        return new CrashQueryVisited(api.Read(events[0], request.Subject));
    }

    private static nint Open(
        ICrashEventQueryApi api, string expression, Lifetime lifetime,
        CrashEventTelemetry? telemetry = null) =>
        Timed(telemetry, CrashEventPhase.EvtQuery, () =>
    {
        var call = api.Open(0, Channel, expression, QueryFlags);
        if (!IsHandle(call.Handle))
            throw Fault(CrashEventFailureKind.Unavailable, CrashQueryReason.Open, call.Error);
        lifetime.Own(() => Close(
            api, call.Handle, CrashQueryReason.QueryClose, telemetry));
        Require(call.Error == 0, CrashQueryReason.Open, call.Error);
        return call.Handle;
    });

    private static CrashQueryResult EnumerateCompleted(
        ICrashEventQueryApi api, CrashQueryRequest request,
        string expression, Lifetime lifetime, CrashEventTelemetry? telemetry)
    {
        var query = Open(api, expression, lifetime, telemetry);
        var events = PrepareEvents(api, query, lifetime, telemetry);
        var call = Timed(telemetry, CrashEventPhase.EvtNext,
            () => api.Next(query, events, TimeoutMilliseconds, 0));
        ValidateBatch(call, events, query);
        if (!call.Success)
            return EndOrFail(call.Error);
        if (call.Returned == Capacity)
            return new CrashQueryAmbiguous();
        var read = Read(api, events[0], request.Subject, telemetry);
        return Complete(api, query, events[0], read, lifetime, telemetry);
    }

    private static CrashEventRead Read(
        ICrashEventQueryApi api, nint borrowedEvent, CrashEventSubject expected,
        CrashEventTelemetry? telemetry) =>
        Timed(telemetry, CrashEventPhase.Reader, () =>
        {
            var read = api.Read(borrowedEvent, expected);
            telemetry?.ReadReturned(read);
            return read;
        });

    private static CrashQueryResult Complete(
        ICrashEventQueryApi api, nint query, nint first,
        CrashEventRead read, Lifetime lifetime, CrashEventTelemetry? telemetry)
    {
        var events = PrepareEvents(api, query, lifetime, telemetry);
        var call = Timed(telemetry, CrashEventPhase.EvtNext,
            () => api.Next(query, events, TimeoutMilliseconds, 0));
        ValidateBatch(call, events, query);
        Require(!events.Contains(first), CrashQueryReason.MalformedBatch, call.Error);
        if (call.Success)
            return new CrashQueryAmbiguous();
        _ = EndOrFail(call.Error);
        return new CrashQueryCompleted(read);
    }

    private static nint[] PrepareEvents(
        ICrashEventQueryApi api, nint query, Lifetime lifetime,
        CrashEventTelemetry? telemetry = null)
    {
        var events = new nint[Capacity];
        // Register both slots BEFORE Next, including its exceptional/partial path.
        // LIFO closes slot 1, slot 0, then query. Aliases are closed only once;
        // malformed aliases to the query are left to its sole query owner.
        var attempted = lifetime.EventCloseAttempts;
        attempted.Add(query);
        foreach (var index in Enumerable.Range(0, Capacity))
            lifetime.Own(() =>
            {
                if (attempted.Add(events[index]))
                    Close(api, events[index], CrashQueryReason.EventClose, telemetry);
            });
        return events;
    }

    private static void ValidateBatch(
        CrashQueryNextCall call, nint[] events, nint query)
    {
        var shape = call.Returned <= Capacity &&
            events.Select((handle, index) => index < call.Returned
                ? IsHandle(handle) && handle != query
                : handle == 0).All(valid => valid) &&
            (call.Returned != Capacity || events[0] != events[1]);
        Require(shape, CrashQueryReason.MalformedBatch, call.Error);
        Require(call.Success
                ? call.Error == 0 && call.Returned > 0
                : call.Returned == 0,
            CrashQueryReason.MalformedBatch, call.Error);
    }

    private static CrashQueryResult EndOrFail(int error) => error switch
    {
        NoMoreItems => new CrashQueryEmpty(),
        _ => throw Fault(
            CrashEventFailureKind.Unavailable, CrashQueryReason.Next, error)
    };

    private static bool IsHandle(nint handle) => handle != 0 && handle != -1;

    private static void Close(
        ICrashEventQueryApi api, nint handle, CrashQueryReason reason,
        CrashEventTelemetry? telemetry = null) =>
        _ = Timed(telemetry, CrashEventPhase.Close, () =>
    {
        var call = api.Close(handle);
        Require(call.Success && call.Error == 0, reason, call.Error);
        return 0;
    });

    private static T Timed<T>(
        CrashEventTelemetry? telemetry, CrashEventPhase phase, Func<T> operation) =>
        telemetry is { } measured ? measured.Measure(phase, operation) : operation();

    private static void Require(bool accepted, CrashQueryReason reason, int error = 0)
    {
        if (!accepted)
            throw Fault(CrashEventFailureKind.Refused, reason, error);
    }

    private static CrashQueryException Fault(
        CrashEventFailureKind kind, CrashQueryReason reason, int error = 0) =>
        new([new(kind, reason, error)]);

    private static CrashQueryResult Attempt(Func<CrashQueryResult> operation)
    {
        try { return operation(); }
        catch (CrashQueryException error)
        {
            return new CrashQueryFailure(error.Faults);
        }
    }

    // Broad catches exist only to finish every cleanup attempt and preserve
    // initiating/cleanup exceptions. Unexpected exceptions are never observations.
    private sealed class Lifetime(CrashEventTelemetry? telemetry = null)
    {
        private readonly List<Action> _cleanup = [];
        private readonly List<Exception> _errors = [];

        internal HashSet<nint> EventCloseAttempts { get; } = [0, -1];

        internal void Own(Action cleanup) => _cleanup.Add(cleanup);

        internal T Run<T>(Func<T> operation)
        {
            try { return operation(); }
            catch (Exception error)
            {
                _errors.Add(error);
                telemetry?.Failure(error);
                throw;
            }
            finally
            {
                foreach (var cleanup in _cleanup.AsEnumerable().Reverse())
                    Capture(cleanup);
                ThrowErrors();
            }
        }

        private void Capture(Action cleanup)
        {
            try { cleanup(); }
            catch (Exception error)
            {
                _errors.Add(error);
                telemetry?.Failure(error);
            }
        }

        private void ThrowErrors()
        {
            if (_errors.Count == 0)
                return;
            if (_errors.Count == 1)
                ExceptionDispatchInfo.Capture(_errors[0]).Throw();
            if (_errors.All(error => error is CrashQueryException))
                throw new CrashQueryException(_errors.OfType<CrashQueryException>()
                    .SelectMany(error => error.Faults).ToImmutableArray());
            throw new AggregateException("Bounded crash-event cleanup failed.", _errors);
        }
    }
}

internal enum CrashEventPhase { EvtQuery, EvtNext, Reader, Close, Fence, Delay }
internal enum CrashEventExpiry { None, Window, PollBudget, PollLimit }
internal enum CrashEventExpiryLocation
{
    None, PollEntry, BeforeQuery, AfterQuery, BeforeDelay, PollLimit
}

// Sampling faults remain distinct from authority failures. Only references
// introduced by sampling are filtered; genuine operational aggregates are intact.
internal sealed class CrashEventSamplingException(
    Exception? authorityFailure, IReadOnlyList<Exception> samples)
    : AggregateException("Synthetic observation sampling failed.",
        Combine(authorityFailure, samples))
{
    internal Exception? AuthorityFailure { get; } = authorityFailure;

    private static IEnumerable<Exception> Combine(
        Exception? failure, IReadOnlyList<Exception> samples) =>
        failure is { } original
            ? samples.Where(sample => !Contains(original, sample)).Prepend(original)
            : samples;

    private static bool Contains(Exception error, Exception expected) =>
        ReferenceEquals(error, expected) || error switch
        {
            AggregateException aggregate =>
                aggregate.InnerExceptions.Any(inner => Contains(inner, expected)),
            { InnerException: { } inner } => Contains(inner, expected),
            _ => false
        };
}

// One owned observation's numeric accumulator and private sampling-fault owner.
// No identifiers, returned objects, selectors or event details are retained.
// Sampling exceptions are never included in the numeric summary.
internal sealed class CrashEventTelemetry(TimeProvider clock)
{
    internal const int MaximumSummaryBytes = 393;
    internal const int MissingSlack = -1000000;
    private const int MaximumCount = 999;
    private const int MaximumMicroseconds = 999999999;
    private const int MaximumSlack = 999999;
    private readonly (int Total, int Maximum)[] _metrics = new (int, int)[6];
    private readonly List<Exception> _samplingErrors = [];
    private bool _observing;
    private int _returns;
    private int _empty;
    private int _last;
    private int _failure;
    private int _code;
    private long _native = -1;
    private int _entryMs = MissingSlack;
    private int _queryMs = MissingSlack;
    private CrashEventExpiry _expiry;
    private CrashEventExpiryLocation _location;
    private bool _rejected;

    internal bool SamplingFailed => _samplingErrors.Count > 0;
    internal void BeginObservation() => _observing = true;

    internal DateTimeOffset? Entering()
    {
        DateTimeOffset? entered = null;
        Sample(() => entered = clock.GetUtcNow());
        return entered;
    }

    internal void ThrowSamplingErrors(Exception? authorityFailure)
    {
        if (SamplingFailed)
            throw new CrashEventSamplingException(authorityFailure, _samplingErrors);
    }

    // Standalone enumeration surfaces sampling faults only after Lifetime.Run
    // has completed every close. The observer defers them through its fences.
    internal CrashQueryResult Query(Func<CrashQueryResult> operation)
    {
        CrashQueryResult? result = null;
        Exception? failure = null;
        try { result = operation(); }
        catch (Exception error) { failure = error; }
        if (!_observing)
            ThrowSamplingErrors(failure);
        CrashCorrelationErrors.Throw(failure);
        return result ?? throw new InvalidOperationException("Synthetic query result missing.");
    }

    internal void Entered(TimeSpan slack) => _entryMs = Slack(slack);
    internal void BeforeQuery(TimeSpan slack) => _queryMs = Slack(slack);
    internal void Rejected() => _rejected = true;
    internal (int Total, int Maximum) Metric(CrashEventPhase phase) =>
        _metrics[(int)phase];

    internal void Expired(CrashEventExpiry expiry, CrashEventExpiryLocation location)
    {
        if (_expiry != CrashEventExpiry.None)
            return;
        (_expiry, _location) = (expiry, location);
    }

    internal void Returned(CrashQueryResult? result)
    {
        _returns = Count((long)_returns + 1);
        _last = Category(result);
        if (result is CrashQueryEmpty)
            _empty = Count((long)_empty + 1);
        switch (result)
        {
            case CrashQueryFailure failure: QueryFaults(failure.Faults); break;
            case CrashQueryCompleted completed: ReadReturned(completed.Read); break;
            case CrashQueryVisited visited: ReadReturned(visited.Read); break;
        }
    }

    internal void ReadReturned(CrashEventRead read)
    {
        if (read is CrashEventFailure failure)
            ReaderFaults(failure.Faults);
    }

    internal void Failure(Exception error)
    {
        if (_failure != 0)
            return;
        switch (error)
        {
            case CrashQueryException query: QueryFaults(query.Faults); break;
            case CrashEventValuesException reader: ReaderFaults(reader.Faults); break;
            case AggregateException { InnerExceptions.Count: > 0 } aggregate:
                Failure(aggregate.InnerExceptions[0]); break;
            default:
                First(error is OperationCanceledException ? 5 :
                    CrashCorrelationErrors.Operational(error) ? 3 : 4, 0,
                    error is System.ComponentModel.Win32Exception native
                        ? unchecked((uint)native.NativeErrorCode) : -1L);
                break;
        }
    }

    private void QueryFaults(ImmutableArray<CrashQueryFault> faults)
    {
        if (faults.IsDefaultOrEmpty)
            First(1, 0, -1);
        else
            First(1, Count((long)(int)faults[0].Reason + 1),
                unchecked((uint)faults[0].NativeError));
    }

    private void ReaderFaults(ImmutableArray<CrashEventFault> faults)
    {
        if (faults.IsDefaultOrEmpty)
            First(2, 0, -1);
        else
            First(2, Count((long)(int)faults[0].Reason + 1), -1);
    }

    private void First(int failure, int code, long native)
    {
        if (_failure == 0)
            (_failure, _code, _native) = (failure, code, native);
    }

    private static int Category(CrashQueryResult? result) => result switch
    {
        CrashQueryEmpty => 1,
        CrashQueryAmbiguous => 2,
        CrashQueryCompleted { Read: CrashEventMatched } => 3,
        CrashQueryCompleted { Read: CrashEventMismatch } => 4,
        CrashQueryCompleted { Read: CrashEventFailure } => 5,
        CrashQueryVisited => 6,
        CrashQueryConstructed => 7,
        CrashQueryFailure => 8,
        _ => 9
    };

    // Sampling never interrupts an operation or interpretation of its return.
    // In particular, Next's status reaches ValidateBatch/EndOrFail unchanged.
    internal T Measure<T>(CrashEventPhase phase, Func<T> operation)
    {
        var started = StartPhase();
        try { return operation(); }
        finally { FinishPhase(phase, started); }
    }

    internal async Task DelayAsync(
        Func<TimeSpan, CancellationToken, Task> delay, TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var started = StartPhase();
        try { await delay(duration, cancellationToken); }
        finally { FinishPhase(CrashEventPhase.Delay, started); }
    }

    private long? StartPhase()
    {
        long? started = null;
        Sample(() => started = clock.GetTimestamp());
        return started;
    }

    private void FinishPhase(CrashEventPhase phase, long? started) =>
        Sample(() =>
        {
            var ended = clock.GetTimestamp();
            if (started is { } beginning)
                Record(phase, clock.GetElapsedTime(beginning, ended));
        });

    private void Sample(Action sample)
    {
        try { sample(); }
        catch (Exception error)
        {
            if (!_samplingErrors.Any(previous => ReferenceEquals(previous, error)))
                _samplingErrors.Add(error);
        }
    }

    private void Record(CrashEventPhase phase, TimeSpan elapsed)
    {
        var microseconds = Duration(elapsed.Ticks / 10);
        var metric = Metric(phase);
        _metrics[(int)phase] = (
            Duration((long)metric.Total + microseconds),
            Math.Max(metric.Maximum, microseconds));
    }

    private static int Duration(long value) =>
        (int)Math.Clamp(value, 0, MaximumMicroseconds);
    private static int Count(long value) =>
        (int)Math.Clamp(value, 0, MaximumCount);
    private static int Slack(TimeSpan value) =>
        (int)Math.Clamp(value.Ticks / TimeSpan.TicksPerMillisecond,
            -MaximumSlack, MaximumSlack);

    internal string Summary(CrashCorrelationProof proof)
    {
        var q = Metric(CrashEventPhase.EvtQuery);
        var n = Metric(CrashEventPhase.EvtNext);
        var r = Metric(CrashEventPhase.Reader);
        var c = Metric(CrashEventPhase.Close);
        var f = Metric(CrashEventPhase.Fence);
        var d = Metric(CrashEventPhase.Delay);
        return FormattableString.Invariant(
            $"HERMES_CRASH_EVENT_DIAGNOSTIC v=1 polls={Count(proof.Polls)} attempts={Count(proof.Queries)} returns={_returns} empty={_empty} last={_last} failure={_failure} code={_code} native={_native} entryMs={_entryMs} queryMs={_queryMs} expiry={(int)_expiry} location={(int)_location} rejected={(_rejected ? 1 : 0)} qUs={q.Total} qMaxUs={q.Maximum} nUs={n.Total} nMaxUs={n.Maximum} rUs={r.Total} rMaxUs={r.Maximum} cUs={c.Total} cMaxUs={c.Maximum} fUs={f.Total} fMaxUs={f.Maximum} dUs={d.Total} dMaxUs={d.Maximum} nativeAcceptance=0\n");
    }
}
