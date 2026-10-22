using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Hermes.Servicing.Acceptance;

internal enum CrashCorrelationReason
{
    None, NotObserved, Guard, Schema, Receipt, ReceiptChanged, ReceiptCleanup,
    Subject, Window, WindowExpired, NoDelivery, Ambiguous, IdentityMismatch,
    Reader, Query, EnumerationIncomplete, AdjacentCandidate, Scope,
    Cancelled, Control, Publication, Unexpected
}

internal sealed class CrashCorrelationException(CrashCorrelationReason reason)
    : IOException("Synthetic crash-event control refused.")
{
    internal CrashCorrelationReason Reason { get; } = reason;
}

internal static class CrashCorrelationErrors
{
    internal static void Require(bool accepted, CrashCorrelationReason reason)
    {
        if (!accepted)
            throw new CrashCorrelationException(reason);
    }

    internal static bool Operational(Exception error) => error switch
    {
        CrashEventSamplingException => false,
        AggregateException aggregate =>
            aggregate.InnerExceptions.Count > 0 &&
            aggregate.InnerExceptions.All(Operational),
        _ => CrashControl.Operational(error)
    };

    internal static CrashCorrelationReason Reason(Exception error) => error switch
    {
        CrashEventSamplingException sampling =>
            sampling.AuthorityFailure is { } original
                ? Reason(original) : CrashCorrelationReason.Unexpected,
        CrashCorrelationException failure => failure.Reason,
        OperationCanceledException => CrashCorrelationReason.Cancelled,
        CrashExitException => CrashCorrelationReason.Scope,
        AggregateException { InnerExceptions.Count: 0 } =>
            CrashCorrelationReason.Unexpected,
        AggregateException aggregate when
            aggregate.InnerExceptions.Any(item => !Operational(item)) =>
                CrashCorrelationReason.Unexpected,
        AggregateException aggregate => Reason(aggregate.InnerExceptions[0]),
        _ when Operational(error) => CrashCorrelationReason.Receipt,
        _ => CrashCorrelationReason.Unexpected
    };

    internal static Exception? Capture(Action operation, Exception? previous)
    {
        try { operation(); return previous; }
        catch (Exception error)
        {
            return previous is { } initiating
                ? new AggregateException("Synthetic control fences failed.", initiating, error)
                : error;
        }
    }

    internal static void Throw(Exception? error)
    {
        if (error is { } failure)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

// Metadata only. Neither this snapshot nor its hash authenticates an event or
// independently establishes the publishing workflow/job/revision.
internal sealed record CrashSchemaSnapshot(
    ReadOnlyMemory<byte> Json, string SourceSha256)
{
    public override string ToString() => nameof(CrashSchemaSnapshot);
}

internal sealed record CrashSchemaAdmission(
    string MetadataSha256, string SourceSha256)
{
    public override string ToString() => nameof(CrashSchemaAdmission);
}

// Read must recheck the same retained read-only files, directory pins, source
// hash and local freshness bounds. The caller reparses every returned snapshot.
internal interface ICrashSchemaReceipt : IDisposable
{
    CrashSchemaSnapshot Read();
}

internal static class CrashEventSchema
{
    internal const int MaximumBytes = 4096;
    internal static TimeSpan MaximumLocalAge => TimeSpan.FromMinutes(5);
    internal const string TemplateSha256 =
        "cd0a3d85a95e98a2909d1a77bada29db34ed1abbe37a058d6c06e9aca599557e";

    internal static CrashSchemaAdmission Admit(CrashSchemaSnapshot snapshot)
    {
        CrashCorrelationErrors.Require(snapshot.Json.Length is > 0 and <= MaximumBytes &&
            CrashControl.IsHash(snapshot.SourceSha256), CrashCorrelationReason.Schema);
        try
        {
            using var document = JsonDocument.Parse(snapshot.Json,
                new JsonDocumentOptions { MaxDepth = 8 });
            ValidateRoot(document.RootElement);
            return new(Convert.ToHexString(SHA256.HashData(snapshot.Json.Span))
                .ToLowerInvariant(), snapshot.SourceSha256);
        }
        catch (Exception error) when (error is JsonException or
            InvalidOperationException or FormatException or OverflowException)
        {
            throw new CrashCorrelationException(CrashCorrelationReason.Schema);
        }
    }

    internal static void RequireFresh(
        DateTimeOffset created, DateTimeOffset written,
        DateTimeOffset opened, DateTimeOffset now) =>
        CrashCorrelationErrors.Require(
            created.Offset == TimeSpan.Zero && written.Offset == TimeSpan.Zero &&
            opened.Offset == TimeSpan.Zero && now.Offset == TimeSpan.Zero &&
            created <= opened && written <= opened && now >= opened &&
            now - created <= MaximumLocalAge &&
            now - written <= MaximumLocalAge,
            CrashCorrelationReason.Receipt);

    private static void ValidateRoot(JsonElement root)
    {
        Object(root, "schemaVersion", "usable", "unavailable", "metadataCount", "versions");
        Equal(root, "schemaVersion", 1);
        Equal(root, "usable", 1);
        Equal(root, "unavailable", 0);
        var versions = Array(root.GetProperty("versions"), 1, 8);
        Equal(root, "metadataCount", versions.Length);
        var identities = versions.Select(ValidateVersion).ToArray();
        Require(identities.Distinct().Count() == identities.Length &&
            identities.Count(version => version == 0) == 1);
    }

    private static int ValidateVersion(JsonElement row)
    {
        Object(row, "id", "version", "unavailable", "fieldCount", "duplicateCount",
            "caseMismatchCount", "knownFieldPresentBits", "templateSha256", "fields");
        Equal(row, "id", 1000);
        Equal(row, "unavailable", 0);
        Equal(row, "duplicateCount", 0);
        Equal(row, "caseMismatchCount", 0);
        var version = Bounded(row.GetProperty("version"), 0, 255);
        var count = Bounded(row.GetProperty("fieldCount"), 1, 128);
        var bits = Bounded(row.GetProperty("knownFieldPresentBits"), 0, 63);
        Require(row.GetProperty("templateSha256").GetString() is { } hash &&
            CrashControl.IsHash(hash));
        var fields = Array(row.GetProperty("fields"), 6, 6);
        ValidateFields(fields, bits, count, version == 0);
        if (version == 0)
            ValidateVersionZero(row);
        return version;
    }

    private static void ValidateVersionZero(JsonElement row)
    {
        Equal(row, "fieldCount", 15);
        Equal(row, "knownFieldPresentBits", 63);
        Require(row.GetProperty("templateSha256").GetString() == TemplateSha256);
    }

    private static void ValidateFields(
        JsonElement[] fields, int bits, int count, bool versionZero)
    {
        var rows = fields.Select((field, index) =>
            ValidateField(field, index + 1, versionZero)).ToArray();
        Require(rows.Sum(row => row.Present) <= count);
        Require(rows.Select((row, index) => row.Present << index)
            .Aggregate(0, (current, bit) => current | bit) == bits);
    }

    private static (int Present, int Occurrences) ValidateField(
        JsonElement field, int name, bool versionZero)
    {
        var row = Array(field, 5, 5).Select(Number).ToArray();
        Require(row[0] == name && row[1] is 0 or 1 &&
            row[2] is 0 or 1 && row[1] == row[2]);
        Require(row[3] is 0 or 1 or 8 or 10 or 17 or 20 or 21);
        Require(row[4] is 0 or 1 or 2 or 8 or 10 or 18 or 19 or 20 or 29 or 30 or 31 or 32);
        Require(row[1] != 0 || (row[3] == 0 && row[4] == 0));
        if (versionZero)
            Require(row.SequenceEqual(ExpectedField(name)));
        return (row[1], row[2]);
    }

    private static int[] ExpectedField(int name) => name switch
    {
        1 => [1, 1, 1, 20, 18],
        2 => [2, 1, 1, 21, 19],
        _ => [name, 1, 1, 1, 1]
    };

    private static void Object(JsonElement value, params string[] names)
    {
        Require(value.ValueKind == JsonValueKind.Object);
        var properties = value.EnumerateObject().ToArray();
        Require(properties.Length == names.Length &&
            properties.Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal).SetEquals(names));
    }

    private static JsonElement[] Array(JsonElement value, int minimum, int maximum)
    {
        Require(value.ValueKind == JsonValueKind.Array);
        var elements = value.EnumerateArray().ToArray();
        Require(elements.Length >= minimum && elements.Length <= maximum);
        return elements;
    }

    private static int Number(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Number);
        var number = value.GetInt32();
        Require(value.GetRawText() == number.ToString(CultureInfo.InvariantCulture));
        return number;
    }

    private static int Bounded(JsonElement value, int minimum, int maximum)
    {
        var number = Number(value);
        Require(number >= minimum && number <= maximum);
        return number;
    }

    private static void Equal(JsonElement row, string name, int expected) =>
        Require(Number(row.GetProperty(name)) == expected);

    private static void Require(bool accepted) =>
        CrashCorrelationErrors.Require(accepted, CrashCorrelationReason.Schema);
}

internal sealed record CrashCorrelationPlan(
    CrashQueryRequest Exact, CrashQueryRequest Lower, CrashQueryRequest Upper)
{
    public override string ToString() => nameof(CrashCorrelationPlan);

    internal static CrashCorrelationPlan Create(CrashIdentity identity)
    {
        CrashCorrelationErrors.Require(
            identity.Process is { ProcessId: > 0, CreationFileTime: > 1 } &&
            identity.ExitFileTime >= identity.Process.CreationFileTime &&
            CrashControl.IsCrashStatus(identity.ExitCode), CrashCorrelationReason.Subject);
        var request = new CrashQueryRequest(
            new(identity.Process.ProcessId, checked((ulong)identity.Process.CreationFileTime),
                identity.ImagePath), Window(identity));
        CrashCorrelationErrors.Require(
            CrashEventQuery.Construct(request) is CrashQueryConstructed,
            CrashCorrelationReason.Subject);
        return Adjacent(request);
    }

    internal static CrashCorrelationPlan Adjacent(CrashQueryRequest exact)
    {
        var birth = exact.Subject.CreationFileTime;
        CrashCorrelationErrors.Require(birth is > 1 and < ulong.MaxValue,
            CrashCorrelationReason.Subject);
        return new(exact,
            exact with { Subject = exact.Subject with { CreationFileTime = checked(birth - 1) } },
            exact with { Subject = exact.Subject with { CreationFileTime = checked(birth + 1) } });
    }

    private static CrashQueryWindow Window(CrashIdentity identity)
    {
        try
        {
            var start = new DateTimeOffset(
                DateTime.FromFileTimeUtc(identity.Process.CreationFileTime));
            var end = new DateTimeOffset(
                DateTime.FromFileTimeUtc(identity.ExitFileTime)).AddSeconds(20);
            CrashCorrelationErrors.Require(end > start &&
                end - start <= TimeSpan.FromSeconds(CrashEventQuery.MaximumWindowSeconds),
                CrashCorrelationReason.Window);
            return new(start, end);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new CrashCorrelationException(CrashCorrelationReason.Window);
        }
    }
}

internal sealed record CrashCorrelationProof
{
    internal bool SchemaAdmitted { get; init; }
    internal bool Matched { get; init; }
    internal bool LowerEmpty { get; init; }
    internal bool UpperEmpty { get; init; }
    internal int Polls { get; init; }
    internal int Queries { get; init; }
    internal CrashCorrelationReason Reason { get; init; } = CrashCorrelationReason.NotObserved;
    internal string MetadataSha256 { get; init; } = CrashControl.EmptyHash;
    internal string SchemaSourceSha256 { get; init; } = CrashControl.EmptyHash;

    internal bool Verified => this is
    {
        SchemaAdmitted: true, Matched: true, LowerEmpty: true, UpperEmpty: true,
        Reason: CrashCorrelationReason.None
    };
}

internal sealed record CrashCorrelationEffects(
    Func<CrashQueryRequest, CrashQueryAdmission, CrashEventTelemetry, CrashQueryResult> Query,
    TimeProvider Clock,
    Func<TimeSpan, CancellationToken, Task> Delay)
{
    internal TimeProvider SamplingClock { get; init; } = TimeProvider.System;
}

// One fixed predicate proof. No alternate image, channel, provider, selector,
// time-window widening, PID adoption or baseline observation is available here.
internal sealed class CrashEventObserver(
    ICrashSchemaReceipt receipt, CrashSchemaAdmission admission,
    CrashCorrelationEffects effects, Action<string> summary,
    CrashEventTelemetry? telemetry = null)
{
    internal const int MaximumPolls = 81;
    internal static TimeSpan PollBudget => TimeSpan.FromSeconds(20);
    internal static TimeSpan PollDelay => TimeSpan.FromMilliseconds(250);

    internal CrashEventTelemetry Telemetry { get; } =
        telemetry ?? new(effects.SamplingClock);

    internal CrashCorrelationProof Proof { get; private set; } = new()
    {
        SchemaAdmitted = true,
        MetadataSha256 = admission.MetadataSha256,
        SchemaSourceSha256 = admission.SourceSha256
    };

    internal async Task ObserveAsync(
        CrashObservation scope, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        Telemetry.BeginObservation();
        try { await ObserveCoreAsync(scope, cancellationToken); }
        catch (Exception caught) { failure = caught; }
        failure = CrashCorrelationErrors.Capture(
            () => Fence(scope, cancellationToken), failure);
        if (failure is { } error)
            Proof = Proof with { Reason = CrashCorrelationErrors.Reason(error) };
        else if (Telemetry.SamplingFailed)
            Proof = Proof with { Reason = CrashCorrelationReason.Unexpected };
        failure = CrashCorrelationErrors.Capture(
            () => summary(CrashEventReport.ObservationSummary(Proof)), failure);
        Telemetry.ThrowSamplingErrors(failure);
        CrashCorrelationErrors.Throw(failure);
    }

    private async Task ObserveCoreAsync(
        CrashObservation scope, CancellationToken cancellationToken)
    {
        var entered = Telemetry.Entering();
        Fence(scope, cancellationToken);
        var plan = CrashCorrelationPlan.Create(scope.Recheck());
        if (entered is { } sampled)
            Telemetry.Entered(plan.Exact.Window.EndInclusive - sampled);
        await PollAsync(scope, plan, effects.Clock.GetTimestamp(), cancellationToken);
    }

    private async Task PollAsync(
        CrashObservation scope, CrashCorrelationPlan plan, long started,
        CancellationToken cancellationToken)
    {
        foreach (var unused in Enumerable.Range(0, MaximumPolls))
        {
            _ = Remaining(plan, started, CrashEventExpiryLocation.PollEntry,
                cancellationToken);
            Proof = Proof with { Polls = Proof.Polls + 1 };
            if (Poll(scope, plan, started, cancellationToken))
            {
                Proof = Proof with { Reason = CrashCorrelationReason.None };
                return;
            }
            await DelayAsync(plan, started, cancellationToken);
        }
        Telemetry.Expired(CrashEventExpiry.PollLimit, CrashEventExpiryLocation.PollLimit);
        throw new CrashCorrelationException(CrashCorrelationReason.NoDelivery);
    }

    private async Task DelayAsync(
        CrashCorrelationPlan plan, long started, CancellationToken cancellationToken)
    {
        var remaining = Remaining(plan, started, CrashEventExpiryLocation.BeforeDelay,
            cancellationToken);
        await Telemetry.DelayAsync(effects.Delay,
            remaining < PollDelay ? remaining : PollDelay, cancellationToken);
    }

    private bool Poll(
        CrashObservation scope, CrashCorrelationPlan plan, long started,
        CancellationToken cancellationToken)
    {
        var exact = Query(scope, plan, plan.Exact, started, cancellationToken);
        if (exact is CrashQueryEmpty)
            return false;
        RequireMatch(exact);
        Proof = Proof with { Matched = true };
        RequireEmpty(Query(scope, plan, plan.Lower, started, cancellationToken));
        Proof = Proof with { LowerEmpty = true };
        RequireEmpty(Query(scope, plan, plan.Upper, started, cancellationToken));
        Proof = Proof with { UpperEmpty = true };
        return true;
    }

    private CrashQueryResult Query(
        CrashObservation scope, CrashCorrelationPlan plan, CrashQueryRequest request,
        long started, CancellationToken cancellationToken)
    {
        Check(scope, plan, started, CrashEventExpiryLocation.BeforeQuery,
            cancellationToken);
        Proof = Proof with { Queries = Proof.Queries + 1 };
        var (result, failure) = InvokeQuery(request);
        failure = CrashCorrelationErrors.Capture(
            () => PostQueryCheck(scope, plan, started, result is not null,
                cancellationToken), failure);
        CrashCorrelationErrors.Throw(failure);
        return result ?? throw new InvalidOperationException("Synthetic query result missing.");
    }

    private (CrashQueryResult? Result, Exception? Failure) InvokeQuery(
        CrashQueryRequest request)
    {
        try
        {
            var result = effects.Query(request,
                CrashQueryAdmission.CallerAttestsFixedProviderAndSchema, Telemetry);
            Telemetry.Returned(result);
            return (result, null);
        }
        catch (Exception error)
        {
            Telemetry.Failure(error);
            return (null, error);
        }
    }

    private void PostQueryCheck(
        CrashObservation scope, CrashCorrelationPlan plan, long started,
        bool returned, CancellationToken cancellationToken)
    {
        try
        {
            Check(scope, plan, started, CrashEventExpiryLocation.AfterQuery,
                cancellationToken);
        }
        catch
        {
            if (returned)
                Telemetry.Rejected();
            throw;
        }
    }

    private void Check(
        CrashObservation scope, CrashCorrelationPlan plan, long started,
        CrashEventExpiryLocation location, CancellationToken cancellationToken)
    {
        Fence(scope, cancellationToken);
        _ = Remaining(plan, started, location, cancellationToken);
    }

    private void Fence(CrashObservation scope, CancellationToken cancellationToken) =>
        Telemetry.Measure(CrashEventPhase.Fence, () =>
        {
            FenceCore(scope, cancellationToken);
            return 0;
        });

    private void FenceCore(CrashObservation scope, CancellationToken cancellationToken)
    {
        _ = scope.Recheck();
        CrashCorrelationErrors.Require(
            CrashEventSchema.Admit(receipt.Read()) == admission,
            CrashCorrelationReason.ReceiptChanged);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private TimeSpan Remaining(
        CrashCorrelationPlan plan, long started, CrashEventExpiryLocation location,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = plan.Exact.Window.EndInclusive - effects.Clock.GetUtcNow();
        if (window <= TimeSpan.Zero)
            Telemetry.Expired(CrashEventExpiry.Window, location);
        CrashCorrelationErrors.Require(window > TimeSpan.Zero,
            CrashCorrelationReason.WindowExpired);
        var budget = PollBudget - effects.Clock.GetElapsedTime(started);
        if (budget <= TimeSpan.Zero)
            Telemetry.Expired(CrashEventExpiry.PollBudget, location);
        CrashCorrelationErrors.Require(budget > TimeSpan.Zero,
            CrashCorrelationReason.NoDelivery);
        if (location == CrashEventExpiryLocation.BeforeQuery)
            Telemetry.BeforeQuery(window);
        return window < budget ? window : budget;
    }

    private static void RequireMatch(CrashQueryResult result)
    {
        var reason = result switch
        {
            CrashQueryCompleted { Read: CrashEventMatched } => CrashCorrelationReason.None,
            CrashQueryCompleted { Read: CrashEventMismatch } => CrashCorrelationReason.IdentityMismatch,
            CrashQueryCompleted { Read: CrashEventFailure } => CrashCorrelationReason.Reader,
            CrashQueryAmbiguous => CrashCorrelationReason.Ambiguous,
            CrashQueryFailure => CrashCorrelationReason.Query,
            _ => CrashCorrelationReason.EnumerationIncomplete
        };
        CrashCorrelationErrors.Require(reason == CrashCorrelationReason.None, reason);
    }

    private static void RequireEmpty(CrashQueryResult result) =>
        CrashCorrelationErrors.Require(result is CrashQueryEmpty,
            result is CrashQueryFailure
                ? CrashCorrelationReason.Query : CrashCorrelationReason.AdjacentCandidate);
}

internal sealed record CrashEventControlResult(
    CrashResult Control, CrashCorrelationProof Proof, bool ReceiptClosed,
    CrashCorrelationReason Failure = CrashCorrelationReason.None, int ReportError = 0)
{
    internal CrashCorrelationReason Reason => this switch
    {
        { Failure: not CrashCorrelationReason.None } => Failure,
        { ReceiptClosed: false } => CrashCorrelationReason.ReceiptCleanup,
        { Proof.Reason: not (CrashCorrelationReason.None or CrashCorrelationReason.NotObserved) }
            => Proof.Reason,
        { Control.ExitCode: not 0 } => CrashCorrelationReason.Control,
        { Proof.Verified: true } => CrashCorrelationReason.None,
        _ => CrashCorrelationReason.NotObserved
    };

    internal int ExitCode => Reason == CrashCorrelationReason.None &&
        Proof.Verified && Control.ExitCode == 0 && ReportError == 0 ? 0 : 1;
}

internal static class CrashEventReport
{
    internal static byte[] Encode(CrashEventControlResult result)
    {
        CrashCorrelationErrors.Require(
            CrashControl.IsHash(result.Proof.MetadataSha256) &&
            CrashControl.IsHash(result.Proof.SchemaSourceSha256),
            CrashCorrelationReason.Publication);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Shape(result));
        CrashCorrelationErrors.Require(bytes.Length <= CrashControl.MaximumReportBytes,
            CrashCorrelationReason.Publication);
        return bytes;
    }

    private static object Shape(CrashEventControlResult value) => new
    {
        schemaVersion = 1, status = value.ExitCode, reason = (int)value.Reason,
        originalGatePassed = 0, nativeAcceptance = 0, thisOwnedControlOnly = 1,
        schemaAdmitted = Bit(value.Proof.SchemaAdmitted),
        metadataSha256 = value.Proof.MetadataSha256,
        schemaSourceSha256 = value.Proof.SchemaSourceSha256,
        independentPublicationVerified = 0, receiptClosed = Bit(value.ReceiptClosed),
        exactCandidates = value.Proof.Matched ? 1 : -1,
        exactReadMatched = Bit(value.Proof.Matched),
        birthMinusOneCandidates = value.Proof.LowerEmpty ? 0 : -1,
        birthPlusOneCandidates = value.Proof.UpperEmpty ? 0 : -1,
        completedQueryEnumerations = Bit(value.Proof.Matched) +
            Bit(value.Proof.LowerEmpty) + Bit(value.Proof.UpperEmpty),
        maximumNextCallsPerQuery = 2, nextBatchCapacity = CrashEventQuery.Capacity,
        globalLogUniqueness = 0, polls = value.Proof.Polls, queries = value.Proof.Queries,
        maximumPollSeconds = 20, maximumWindowSeconds = CrashEventQuery.MaximumWindowSeconds,
        controlStatus = value.Control.ExitCode, reportError = value.ReportError,
        outerSupervisionRequired = 1, synchronousCallsInterruptible = 0
    };

    internal static string Summary(CrashEventControlResult result) =>
        FormattableString.Invariant(
            $"HERMES_CRASH_EVENT_CONTROL v=1 status={result.ExitCode} reason={(int)result.Reason} reportError={result.ReportError} originalGatePassed=0 nativeAcceptance=0\n");

    internal static string ObservationSummary(CrashCorrelationProof proof) =>
        FormattableString.Invariant(
            $"HERMES_CRASH_EVENT_OBSERVATION v=1 reason={(int)proof.Reason} matched={Bit(proof.Matched)} lowerEmpty={Bit(proof.LowerEmpty)} upperEmpty={Bit(proof.UpperEmpty)} nativeAcceptance=0\n");

    private static int Bit(bool value) => value ? 1 : 0;
}

internal sealed record CrashEventControlEffects(
    Action RequireContext,
    Func<ICrashSchemaReceipt> OpenReceipt,
    Action<Action<CrashOwnedChild>, CancellationToken> Start,
    CrashCorrelationEffects Observation,
    Action<byte[]> PublishControl,
    Action<byte[]> PublishEvent,
    Action<string> Summary);

internal static class CrashEventControl
{
    internal const string Command = "startup-crash-event-probe";

    internal static async Task<int> DispatchAsync(
        string[] arguments, CrashEventControlEffects effects,
        CancellationToken cancellationToken)
    {
        if (arguments is not [Command])
            return 2;
        try { effects.RequireContext(); }
        catch (Exception error) when (CrashCorrelationErrors.Operational(error))
        {
            return Refuse(effects);
        }
        return await new Session(effects).RunAsync(cancellationToken);
    }

    private static int Refuse(CrashEventControlEffects effects)
    {
        var control = new CrashResult { Error = CrashFault.Guard };
        var result = new CrashEventControlResult(
            control, new(), false, CrashCorrelationReason.Guard);
        try
        {
            effects.Summary(CrashReport.Summary(control));
            effects.Summary(CrashEventReport.Summary(result));
        }
        catch (Exception error) when (CrashCorrelationErrors.Operational(error)) { }
        return 1;
    }

    private sealed class Session(CrashEventControlEffects effects)
    {
        private readonly List<Exception> _unexpected = [];
        private readonly CrashEventTelemetry _telemetry =
            new(effects.Observation.SamplingClock);
        private ICrashSchemaReceipt? _receipt;
        private Exception? _observationFailure;
        private CrashResult _control = new() { Error = CrashFault.Guard };
        private CrashCorrelationProof _proof = new();
        private CrashCorrelationReason _failure;
        private bool _receiptClosed;
        private bool _coordinatorEscaped;
        private bool _coordinatorSettled;
        private bool _diagnosticEmitted;

        internal async Task<int> RunAsync(CancellationToken cancellationToken)
        {
            try { await RunOwnedAsync(cancellationToken); }
            catch (Exception error) { Fail(error); }
            finally { ReleaseReceipt(); }
            ThrowUnexpected();
            try { return Publish(); }
            catch (Exception error)
            {
                CrashCorrelationErrors.Throw(
                    CrashCorrelationErrors.Capture(EmitTelemetry, error));
                throw;
            }
        }

        private async Task RunOwnedAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receipt = effects.OpenReceipt();
            _receipt = receipt;
            var admission = CrashEventSchema.Admit(receipt.Read());
            var observer = new CrashEventObserver(
                receipt, admission, effects.Observation, effects.Summary, _telemetry);
            await RunCoordinatorAsync(observer, cancellationToken);
            CrashCorrelationErrors.Require(
                CrashEventSchema.Admit(receipt.Read()) == admission,
                CrashCorrelationReason.ReceiptChanged);
        }

        private async Task RunCoordinatorAsync(
            CrashEventObserver observer, CancellationToken cancellationToken)
        {
            try
            {
                _control = await OwnedCrashControl.RunAsync(
                    effects.Start, (scope, token) => ObserveAsync(observer, scope, token),
                    CrashLimits.Default, cancellationToken);
            }
            catch { _coordinatorEscaped = true; throw; }
            finally
            {
                _coordinatorSettled = true;
                _proof = observer.Proof;
                if (_observationFailure is { } failure)
                    Fail(failure);
            }
        }

        private async Task ObserveAsync(
            CrashEventObserver observer, CrashObservation scope,
            CancellationToken cancellationToken)
        {
            try { await observer.ObserveAsync(scope, cancellationToken); }
            catch (Exception error)
            {
                // Preserve every original failure. A single classifiable callback
                // refusal lets the coordinator return its actual settled result.
                // Fail processes the original afterward; unexpected failures still
                // escape after receipt cleanup, without widening coordinator catches.
                _observationFailure = error;
                Exception refusal = InitiatedByCancellation(error)
                    ? new OperationCanceledException(
                        "Synthetic observation cancelled.", error, cancellationToken)
                    : new IOException("Synthetic observation failed.", error);
                throw refusal;
            }
        }

        private static bool InitiatedByCancellation(Exception error) => error switch
        {
            CrashEventSamplingException sampling =>
                sampling.AuthorityFailure is { } original &&
                InitiatedByCancellation(original),
            OperationCanceledException => true,
            AggregateException { InnerExceptions.Count: > 0 } aggregate =>
                InitiatedByCancellation(aggregate.InnerExceptions[0]),
            _ => false
        };

        private void Fail(Exception error)
        {
            _failure = _coordinatorEscaped
                ? CrashCorrelationReason.Unexpected : CrashCorrelationErrors.Reason(error);
            Remember(error);
            Diagnostic();
        }

        private void Diagnostic()
        {
            try
            {
                effects.Summary(CrashEventReport.ObservationSummary(
                    _proof with { Reason = _failure }));
            }
            catch (Exception error) { Remember(error); }
        }

        private void ReleaseReceipt()
        {
            if (_receipt is not { } receipt)
                return;
            try { receipt.Dispose(); _receiptClosed = true; }
            catch (Exception error)
            {
                _failure = CrashCorrelationReason.ReceiptCleanup;
                Remember(error);
                Diagnostic();
            }
        }

        private void Remember(Exception error)
        {
            // Without a returned coordinator outcome, no reportable control facts
            // exist. Preserve escaping and subsequent errors regardless of leaf type.
            // Sampling prevents publication, so retain reportable failures too.
            if (_coordinatorEscaped || _telemetry.SamplingFailed ||
                !CrashCorrelationErrors.Operational(error))
                _unexpected.Add(error);
        }

        private void ThrowUnexpected()
        {
            // Reattach samples after cleanup and emission. Unwrap only
            // this observer's sampling envelope; original aggregates stay intact.
            var failures = _unexpected
                .Select(error => ReferenceEquals(error, _observationFailure) &&
                    _telemetry.SamplingFailed && error is CrashEventSamplingException sampling
                        ? sampling.AuthorityFailure : error)
                .OfType<Exception>().ToArray();
            Exception? failure = failures.Length switch
            {
                0 => null,
                1 => failures[0],
                _ => new AggregateException("Synthetic control failed.", failures)
            };
            if (failure is not null || _telemetry.SamplingFailed)
                failure = CrashCorrelationErrors.Capture(EmitTelemetry, failure);
            _telemetry.ThrowSamplingErrors(failure);
            CrashCorrelationErrors.Throw(failure);
        }

        private int Publish()
        {
            var result = new CrashEventControlResult(
                _control, _proof, _receiptClosed, _failure);
            result = PublishControl(result);
            result = PublishEvent(result);
            if (result.ExitCode != 0)
                EmitTelemetry();
            effects.Summary(CrashReport.Summary(result.Control));
            effects.Summary(CrashEventReport.Summary(result));
            return result.ExitCode;
        }

        private void EmitTelemetry()
        {
            if (!_coordinatorSettled || _diagnosticEmitted)
                return;
            _diagnosticEmitted = true;
            effects.Summary(_telemetry.Summary(_proof));
        }

        private CrashEventControlResult PublishControl(CrashEventControlResult result)
        {
            try
            {
                effects.RequireContext();
                effects.PublishControl(CrashReport.Encode(result.Control));
                return result;
            }
            catch (Exception error) when (CrashCorrelationErrors.Operational(error))
            {
                return result with
                {
                    Control = result.Control with { ReportError = 1 },
                    Failure = CrashCorrelationReason.Publication, ReportError = 1
                };
            }
        }

        private CrashEventControlResult PublishEvent(CrashEventControlResult result)
        {
            try
            {
                effects.RequireContext();
                effects.PublishEvent(CrashEventReport.Encode(result));
                return result;
            }
            catch (Exception error) when (CrashCorrelationErrors.Operational(error))
            {
                return result with { Failure = CrashCorrelationReason.Publication, ReportError = 1 };
            }
        }
    }
}
