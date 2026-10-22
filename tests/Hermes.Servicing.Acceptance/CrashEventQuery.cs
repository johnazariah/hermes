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
        ICrashEventQueryApi api, string expression, Lifetime lifetime)
    {
        var call = api.Open(0, Channel, expression, QueryFlags);
        if (!IsHandle(call.Handle))
            throw Fault(CrashEventFailureKind.Unavailable, CrashQueryReason.Open, call.Error);
        lifetime.Own(() => Close(api, call.Handle, CrashQueryReason.QueryClose));
        Require(call.Error == 0, CrashQueryReason.Open, call.Error);
        return call.Handle;
    }

    private static nint[] PrepareEvents(
        ICrashEventQueryApi api, nint query, Lifetime lifetime)
    {
        var events = new nint[Capacity];
        // Register both slots BEFORE Next, including its exceptional/partial path.
        // LIFO closes slot 1, slot 0, then query. Aliases are closed only once;
        // malformed aliases to the query are left to its sole query owner.
        var attempted = new HashSet<nint> { 0, -1, query };
        foreach (var index in Enumerable.Range(0, Capacity))
            lifetime.Own(() =>
            {
                if (attempted.Add(events[index]))
                    Close(api, events[index], CrashQueryReason.EventClose);
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
        ICrashEventQueryApi api, nint handle, CrashQueryReason reason)
    {
        var call = api.Close(handle);
        Require(call.Success && call.Error == 0, reason, call.Error);
    }

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
    private sealed class Lifetime
    {
        private readonly List<Action> _cleanup = [];
        private readonly List<Exception> _errors = [];

        internal void Own(Action cleanup) => _cleanup.Add(cleanup);

        internal T Run<T>(Func<T> operation)
        {
            try { return operation(); }
            catch (Exception error) { _errors.Add(error); throw; }
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
            catch (Exception error) { _errors.Add(error); }
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
