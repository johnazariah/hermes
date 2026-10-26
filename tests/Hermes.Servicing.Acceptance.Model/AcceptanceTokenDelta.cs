using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Hermes.Servicing.Acceptance;

internal enum AcceptanceTokenDeltaStage
{
    Source = 1,
    AfterFilter = 2,
    AfterMedium = 3
}

internal sealed record AcceptanceTokenDeltaFacts(int Integrity, int RestrictedCount)
{
    internal void Require()
    {
        if (Integrity < 0 || RestrictedCount is < 0 or > 8191)
            throw AcceptanceTokenDeltaBuffer.Failure(13);
    }
}

internal sealed record AcceptanceTokenDeltaSample(
    AcceptanceTokenDeltaStage Stage,
    AcceptanceTokenDeltaFacts? Facts,
    Exception? Failure)
{
    internal string Row => FormattableString.Invariant(
        $"HERMES_RESTRICTED_TOKEN_DELTA v=1 stage={(int)Stage} integrity={Facts?.Integrity ?? -1} restrictedCount={Facts?.RestrictedCount ?? -1} available={(Facts is { } ? 1 : 0)} error={AcceptanceSourceControlOrchestration.Code(Failure)}");
}

// Per-construction instrumentation, not an acceptance predicate.
// No subject, read delegate, native buffer, or handle survives a synchronous read.
internal sealed class AcceptanceTokenDelta
{
    private readonly AcceptanceTokenDeltaSample?[] _samples = new AcceptanceTokenDeltaSample?[3];

    internal IReadOnlyList<AcceptanceTokenDeltaSample> Samples =>
        _samples.OfType<AcceptanceTokenDeltaSample>().ToArray();

    internal TFiltered Filter<TSource, TFiltered>(
        TSource source,
        Func<TSource, TFiltered> filter,
        Func<TSource, AcceptanceTokenDeltaFacts> readSource,
        Func<TFiltered, AcceptanceTokenDeltaFacts> readFiltered)
    {
        Read(AcceptanceTokenDeltaStage.Source, () => readSource(source));
        var filtered = filter(source);
        Read(AcceptanceTokenDeltaStage.AfterFilter, () => readFiltered(filtered));
        return filtered;
    }

    internal void SetMedium<TFiltered>(
        TFiltered filtered,
        Action<TFiltered> setMedium,
        Func<TFiltered, AcceptanceTokenDeltaFacts> readFiltered)
    {
        setMedium(filtered);
        Read(AcceptanceTokenDeltaStage.AfterMedium, () => readFiltered(filtered));
    }

    // Called only after the original operation, gate, and token disposal finish.
    // Output failures cannot skip other rows or replace the original failure.
    internal void Complete(Exception? original, IAcceptanceSourceControlOutput output)
    {
        var reporting = AcceptanceSourceControlOrchestration.Capture(() =>
            AcceptanceSourceControlOrchestration.RunAll(Samples.Select(sample =>
                (Action)(() => output.WriteLine(sample.Row))).ToArray()));
        AcceptanceSourceControlOrchestration.Throw(
            AcceptanceSourceControlOrchestration.Combine(
                [original, .. Samples.Select(sample => sample.Failure), reporting]));
    }

    private void Read(
        AcceptanceTokenDeltaStage stage, Func<AcceptanceTokenDeltaFacts> read)
    {
        var index = (int)stage - 1;
        var previous = _samples[index];
        AcceptanceTokenDeltaFacts? facts = null;
        var failure = AcceptanceSourceControlOrchestration.Capture(() =>
        {
            if (previous is { })
                throw new InvalidOperationException("Token delta stage already sampled.");
            facts = read();
            facts.Require();
        });
        _samples[index] = new(stage, failure is null ? facts : null,
            AcceptanceSourceControlOrchestration.Combine(previous?.Failure, failure));
    }
}

// Extracted from the existing TokenBuffer/ReadGroups validators without changing
// their rules. Native callers still own and bound every allocation.
internal static class AcceptanceTokenDeltaBuffer
{
    internal static Win32Exception Failure(int code) =>
        new(code > 0 ? code : 13, "Acceptance token operation refused.");

    internal static int RequiredSize(int result, int error, uint length, int minimum)
    {
        if (result != 0 || error != 122 || length < minimum || length > 65536)
            throw Failure(result == 0 && error != 122 ? error : 13);
        return checked((int)length);
    }

    internal static ImmutableArray<T> ReadGroups<T>(
        nint pointer, int length, int stride, Func<int, T> readGroup)
    {
        if (length < sizeof(uint))
            throw Failure(13);
        var count = Marshal.ReadInt32(pointer);
        if (count == 0)
            return [];
        var offset = nint.Size;
        if (count < 0 || length < offset || count > (length - offset) / stride)
            throw Failure(13);
        return Enumerable.Range(0, count)
            .Select(index => readGroup(offset + index * stride))
            .ToImmutableArray();
    }

    internal static nint RequireSid(nint pointer, int length, nint sid)
    {
        var offset = sid.ToInt64() - pointer.ToInt64();
        if (offset < 0 || offset > length - 8)
            throw Failure(13);
        var count = Marshal.ReadByte(sid, 1);
        if (Marshal.ReadByte(sid) != 1 || count > 15 ||
            offset > (long)length - 8 - 4 * count)
            throw Failure(13);
        return sid;
    }
}
