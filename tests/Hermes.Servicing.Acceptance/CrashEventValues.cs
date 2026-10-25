using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Text;

namespace Hermes.Servicing.Acceptance;

internal enum CrashEventSelection { Identity, Details }
internal enum CrashEventFailureKind { Unavailable, Refused }

internal enum CrashEventReason
{
    None, InvalidSubject, InvalidEventHandle, UnsupportedPlatform, UnsupportedAbi,
    ContextCreate, ContextClose, Render, BufferRelease, BufferSize,
    BufferAlignment, PropertyCount, MissingValue, VariantType, PointerRange,
    StringLimit, InvalidUtf16, MalformedPath, InvalidDetail,
    ProcessIdMismatch, CreationTimeMismatch, ImagePathMismatch
}

internal sealed record CrashEventSubject(
    uint ProcessId, ulong CreationFileTime, string ActualImagePath)
{
    public override string ToString() => nameof(CrashEventSubject);
}

// Private inputs for a later bounded projection, not an evidence/report format.
internal sealed record CrashEventDetails(
    string ExceptionCode, string ModuleName, string ModulePath)
{
    public override string ToString() => nameof(CrashEventDetails);
}

internal abstract record CrashEventRead;

internal sealed record CrashEventMatched(CrashEventDetails Details) : CrashEventRead
{
    public override string ToString() => nameof(CrashEventMatched);
}

internal sealed record CrashEventMismatch(CrashEventReason Reason) : CrashEventRead;
internal readonly record struct CrashEventFault(
    CrashEventFailureKind Kind, CrashEventReason Reason);
internal sealed record CrashEventFailure(
    ImmutableArray<CrashEventFault> Faults) : CrashEventRead;

// Diagnostic-only results. These cannot carry identity values or event details
// and deliberately do not derive from CrashEventRead/CrashEventMatched.
internal enum CrashIdentityComparison
{
    Same = 1, BirthDifferent = 2, ProcessDifferent = 3, ImageDifferent = 4
}

internal abstract record CrashIdentityRead;
internal sealed record CrashIdentityCompared(
    CrashIdentityComparison Comparison) : CrashIdentityRead;
internal sealed record CrashIdentityFailure(
    ImmutableArray<CrashEventFault> Faults) : CrashIdentityRead;

internal sealed class CrashEventValuesException(
    ImmutableArray<CrashEventFault> faults)
    : IOException("Selective crash-event values failed.")
{
    internal ImmutableArray<CrashEventFault> Faults { get; } = faults;
}

// SDK winevt.h: an eight-byte union followed by DWORD Count and DWORD Type.
// The Windows adapter derives these values from its actual interop declarations.
internal readonly record struct CrashEventAbi(
    int PointerBytes, int UnionBytes, int CountOffset, int TypeOffset,
    int VariantBytes, bool LittleEndian, bool UnionAtZero)
{
    internal static CrashEventAbi X64 => new(8, 8, 8, 12, 16, true, true);
}

internal readonly record struct CrashEventRenderCall(
    bool Success, int Error, uint UsedBytes, uint PropertyCount);

internal readonly record struct CrashEventRendered(
    ReadOnlyMemory<byte> Bytes, ulong Address, int UsedBytes, uint PropertyCount)
{
    public override string ToString() => nameof(CrashEventRendered);
}

// Only the fixed reader uses this internal capability. It is not a selector API.
// The event handle is borrowed for synchronous calls; Close accepts contexts only.
internal interface ICrashEventApi
{
    CrashEventAbi Abi { get; }
    nint CreateContext(IReadOnlyList<string> valuePaths);
    CrashEventRenderCall Render(
        nint context, nint borrowedEvent, ICrashEventAllocation? allocation);
    ICrashEventAllocation Allocate(int capacity);
    bool Close(nint context);
}

internal interface ICrashEventAllocation : IDisposable
{
    ulong Address { get; }
    int Capacity { get; }
    ReadOnlyMemory<byte> Snapshot(int usedBytes);
}

// Native calls are synchronous/cooperative. A future invoker must supply its
// outer current-step deadline and private-job containment; no async abandonment.
internal static class CrashEventValues
{
    internal const int MaximumBufferBytes = 65536;
    internal const int MaximumCharacters = 4096;
    internal const int VariantBytes = 16;
    internal const int PropertyCount = 3;
    internal const int HeaderBytes = VariantBytes * PropertyCount;
    private const int InsufficientBuffer = 122;

    internal static ImmutableArray<string> ValuePaths(CrashEventSelection selection) =>
        selection switch
        {
            CrashEventSelection.Identity =>
            [
                "Event/EventData/Data[@Name=\"ProcessId\"]",
                "Event/EventData/Data[@Name=\"ProcessCreationTime\"]",
                "Event/EventData/Data[@Name=\"AppPath\"]"
            ],
            CrashEventSelection.Details =>
            [
                "Event/EventData/Data[@Name=\"ExceptionCode\"]",
                "Event/EventData/Data[@Name=\"ModuleName\"]",
                "Event/EventData/Data[@Name=\"ModulePath\"]"
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(selection))
        };

    internal static CrashEventRead Read(
        ICrashEventApi api, nint borrowedEvent, CrashEventSubject expected)
    {
        try
        {
            ValidateRead(api, borrowedEvent, expected);
            return ReadCore(api, borrowedEvent, expected);
        }
        catch (CrashEventValuesException error)
        {
            return new CrashEventFailure(error.Faults);
        }
    }

    internal static CrashIdentityRead ReadIdentity(
        ICrashEventApi api, nint borrowedEvent, CrashEventSubject expected)
    {
        try
        {
            ValidateRead(api, borrowedEvent, expected);
            var identity = ReadSelection(
                api, borrowedEvent, CrashEventSelection.Identity, DecodeIdentity);
            return new CrashIdentityCompared(ComparePresence(identity, expected));
        }
        catch (CrashEventValuesException error)
        {
            return new CrashIdentityFailure(error.Faults);
        }
    }

    private static void ValidateRead(
        ICrashEventApi api, nint borrowedEvent, CrashEventSubject expected)
    {
        Require(expected.ProcessId != 0 && expected.CreationFileTime != 0 &&
            IsCanonicalPath(expected.ActualImagePath), CrashEventReason.InvalidSubject);
        Require(borrowedEvent != 0 && borrowedEvent != -1,
            CrashEventReason.InvalidEventHandle);
        Require(api.Abi == CrashEventAbi.X64, CrashEventReason.UnsupportedAbi);
    }

    private static CrashEventRead ReadCore(
        ICrashEventApi api, nint borrowedEvent, CrashEventSubject expected)
    {
        var identity = ReadSelection(
            api, borrowedEvent, CrashEventSelection.Identity, DecodeIdentity);
        var mismatch = Compare(identity, expected);
        if (mismatch != CrashEventReason.None)
            return new CrashEventMismatch(mismatch);
        var details = ReadSelection(
            api, borrowedEvent, CrashEventSelection.Details, DecodeDetails);
        return new CrashEventMatched(details);
    }

    private static T ReadSelection<T>(
        ICrashEventApi api, nint borrowedEvent, CrashEventSelection selection,
        Func<CrashEventRendered, T> decode)
    {
        var lifetime = new Lifetime();
        return lifetime.Run(() =>
        {
            var context = Open(api, selection, lifetime);
            return decode(Render(api, context, borrowedEvent, lifetime));
        });
    }

    private static nint Open(
        ICrashEventApi api, CrashEventSelection selection, Lifetime lifetime)
    {
        var context = api.CreateContext(ValuePaths(selection));
        if (context == 0 || context == -1)
            throw Unavailable(CrashEventReason.ContextCreate);
        lifetime.Own(() =>
        {
            if (!api.Close(context))
                throw Refuse(CrashEventReason.ContextClose);
        });
        return context;
    }

    private static CrashEventRendered Render(
        ICrashEventApi api, nint context, nint borrowedEvent, Lifetime lifetime)
    {
        var probe = api.Render(context, borrowedEvent, null);
        if (probe.Success || probe.Error != InsufficientBuffer)
            throw Unavailable(CrashEventReason.Render);
        var allocation = Allocate(api, CheckedSize(probe.UsedBytes), lifetime);
        var call = api.Render(context, borrowedEvent, allocation);
        if (!call.Success)
            throw Unavailable(CrashEventReason.Render);
        var used = CheckedSize(call.UsedBytes);
        Require(used <= allocation.Capacity, CrashEventReason.BufferSize);
        Require(call.PropertyCount == PropertyCount, CrashEventReason.PropertyCount);
        return new(allocation.Snapshot(used), allocation.Address, used, call.PropertyCount);
    }

    private static ICrashEventAllocation Allocate(
        ICrashEventApi api, int size, Lifetime lifetime)
    {
        var allocation = api.Allocate(size);
        lifetime.Own(allocation.Dispose);
        Require(allocation.Capacity == size, CrashEventReason.BufferSize);
        ValidateRange(allocation.Address, size);
        return allocation;
    }

    private static int CheckedSize(uint size)
    {
        Require(size is >= HeaderBytes and <= MaximumBufferBytes,
            CrashEventReason.BufferSize);
        return checked((int)size);
    }

    private static void ValidateRange(ulong address, int size)
    {
        Require(address != 0 && address <= ulong.MaxValue - (uint)size,
            CrashEventReason.PointerRange);
        Require((address & 7) == 0, CrashEventReason.BufferAlignment);
    }

    private static void Validate(CrashEventRendered rendered)
    {
        _ = CheckedSize(checked((uint)rendered.UsedBytes));
        Require(rendered.Bytes.Length == rendered.UsedBytes,
            CrashEventReason.BufferSize);
        Require(rendered.PropertyCount == PropertyCount,
            CrashEventReason.PropertyCount);
        ValidateRange(rendered.Address, rendered.UsedBytes);
    }

    private static CrashEventSubject DecodeIdentity(CrashEventRendered rendered)
    {
        Validate(rendered);
        RequireType(rendered, 0, 20);
        RequireType(rendered, 1, 21);
        var pid = BinaryPrimitives.ReadUInt32LittleEndian(rendered.Bytes.Span);
        var birth = BinaryPrimitives.ReadUInt64LittleEndian(
            rendered.Bytes.Span.Slice(VariantBytes));
        var path = ReadString(rendered, 2);
        Require(IsCanonicalPath(path), CrashEventReason.MalformedPath);
        return new(pid, birth, path);
    }

    private static CrashEventDetails DecodeDetails(CrashEventRendered rendered)
    {
        Validate(rendered);
        var code = ReadString(rendered, 0);
        var module = ReadString(rendered, 1);
        var path = ReadString(rendered, 2);
        Require(code.Length == 8 && code.All(IsHex) &&
            module.Length > 0 && path.Length > 0, CrashEventReason.InvalidDetail);
        return new(code, module, path);
    }

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static void RequireType(
        CrashEventRendered rendered, int index, uint expected)
    {
        var type = BinaryPrimitives.ReadUInt32LittleEndian(
            rendered.Bytes.Span.Slice(index * VariantBytes + 12));
        if (type == 0)
            throw Refuse(CrashEventReason.MissingValue);
        // Exact type equality also rejects arrays, reserved bits and coercions.
        // Scalar Count is undefined and is intentionally never inspected.
        Require(type == expected, CrashEventReason.VariantType);
    }

    private static string ReadString(CrashEventRendered rendered, int index)
    {
        RequireType(rendered, index, 1);
        var pointer = BinaryPrimitives.ReadUInt64LittleEndian(
            rendered.Bytes.Span.Slice(index * VariantBytes));
        Require(pointer >= rendered.Address + HeaderBytes &&
            pointer < rendered.Address + (uint)rendered.UsedBytes &&
            (pointer & 1) == 0, CrashEventReason.PointerRange);
        var offset = checked((int)(pointer - rendered.Address));
        var available = Math.Min(
            (rendered.UsedBytes - offset) / 2, MaximumCharacters + 1);
        for (var length = 0; length < available; length++)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(
                rendered.Bytes.Span.Slice(offset + length * 2)) == 0)
                return DecodeUtf16(rendered.Bytes.Span.Slice(offset, length * 2));
        }
        throw Refuse(CrashEventReason.StringLimit);
    }

    private static string DecodeUtf16(ReadOnlySpan<byte> bytes)
    {
        try { return new UnicodeEncoding(false, false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            throw Refuse(CrashEventReason.InvalidUtf16);
        }
    }

    // No filesystem lookup, path normalization, alias resolution or case folding.
    // Other namespaces/forms are conservative refusals, not fuzzy matches.
    private static bool IsCanonicalPath(string? path) =>
        path is { Length: > 3 and <= MaximumCharacters } &&
        (path[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z') &&
        path[1] == ':' && path[2] == '\\' &&
        IsWellFormed(path) && path[3..].Split('\\').All(IsPathComponent);

    private static bool IsPathComponent(string component) =>
        component.Length > 0 && component is not ("." or "..") &&
        component[0] != ' ' && component[^1] is not (' ' or '.') &&
        component.All(character => !char.IsControl(character)) &&
        component.IndexOfAny(['/', ':', '*', '?', '"', '<', '>', '|']) < 0;

    private static bool IsWellFormed(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index]))
                continue;
            if (!char.IsHighSurrogate(value[index]) || index + 1 == value.Length ||
                !char.IsLowSurrogate(value[index + 1]))
                return false;
            index++;
        }
        return true;
    }

    private static CrashEventReason Compare(
        CrashEventSubject current, CrashEventSubject expected) =>
        (current.ProcessId == expected.ProcessId,
            current.CreationFileTime == expected.CreationFileTime,
            string.Equals(current.ActualImagePath, expected.ActualImagePath,
                StringComparison.Ordinal)) switch
        {
            (false, _, _) => CrashEventReason.ProcessIdMismatch,
            (_, false, _) => CrashEventReason.CreationTimeMismatch,
            (_, _, false) => CrashEventReason.ImagePathMismatch,
            _ => CrashEventReason.None
        };

    // PID/image disagreement wins over birth disagreement: a foreign identity
    // must never be classified as our owned process merely having another birth.
    private static CrashIdentityComparison ComparePresence(
        CrashEventSubject current, CrashEventSubject expected) =>
        (current.ProcessId == expected.ProcessId,
            string.Equals(current.ActualImagePath, expected.ActualImagePath,
                StringComparison.Ordinal),
            current.CreationFileTime == expected.CreationFileTime) switch
        {
            (false, _, _) => CrashIdentityComparison.ProcessDifferent,
            (_, false, _) => CrashIdentityComparison.ImageDifferent,
            (_, _, false) => CrashIdentityComparison.BirthDifferent,
            _ => CrashIdentityComparison.Same
        };

    internal static CrashEventFailure Failure(
        CrashEventFailureKind kind, CrashEventReason reason) =>
        new([new(kind, reason)]);

    internal static CrashEventValuesException Refuse(CrashEventReason reason) =>
        new([new(CrashEventFailureKind.Refused, reason)]);

    private static CrashEventValuesException Unavailable(CrashEventReason reason) =>
        new([new(CrashEventFailureKind.Unavailable, reason)]);

    private static void Require(bool accepted, CrashEventReason reason)
    {
        if (!accepted)
            throw Refuse(reason);
    }

    // Catching here exists solely to finish all owned cleanup and preserve errors.
    // Unexpected exceptions are rethrown, never converted into an observation.
    private sealed class Lifetime
    {
        private readonly List<Action> _cleanup = [];
        private readonly List<Exception> _errors = [];

        internal void Own(Action cleanup) => _cleanup.Add(cleanup);

        internal T Run<T>(Func<T> read)
        {
            try { return read(); }
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
            if (_errors.All(error => error is CrashEventValuesException))
                throw new CrashEventValuesException(_errors
                    .OfType<CrashEventValuesException>()
                    .SelectMany(error => error.Faults).ToImmutableArray());
            throw new AggregateException("Selective crash-event cleanup failed.", _errors);
        }
    }
}
