using System.Collections.Immutable;
using System.ComponentModel;
using System.Text;

namespace Hermes.Setup;

internal enum PendingDisposition { None, Relevant, Alias, Unknown }
internal sealed record PendingOperation(string Source, string? Destination);
internal sealed record PendingEvidence(
    PendingDisposition Disposition,
    ImmutableArray<PendingOperation> Operations,
    uint NativeCode = 0);

internal static class PendingFileClassifier
{
    internal static PendingEvidence Read(
        IReadOnlyList<ReadObservation<RegistryBytes>> values,
        IReadOnlyList<string> resources,
        IPathIdentityQuery paths)
    {
        if (values.Count != 2 || resources.Count == 0)
            return new(PendingDisposition.Unknown, []);
        try
        {
            var operations = values.SelectMany(value => Decode(value)).ToImmutableArray();
            var disposition = operations.SelectMany(Endpoints)
                .SelectMany(path => resources.Select(resource => Relation(path, resource, paths)))
                .DefaultIfEmpty(PendingDisposition.None).Max();
            return new(disposition, operations);
        }
        catch (Win32Exception error)
        { return new(PendingDisposition.Unknown, [], unchecked((uint)error.NativeErrorCode)); }
        catch (Exception error) when (error is FormatException or DecoderFallbackException)
        { return new(PendingDisposition.Unknown, []); }
    }

    private static ImmutableArray<PendingOperation> Decode(ReadObservation<RegistryBytes> value)
    {
        if (value.State == ObservationState.Missing)
            return [];
        if (value is not { State: ObservationState.Known, Value: { } raw })
            throw new Win32Exception(unchecked((int)value.NativeCode));
        if (raw.Type != 7 || raw.Bytes.IsDefault)
            throw new FormatException("Pending operations must be REG_MULTI_SZ.");
        return DecodeBytes(raw.Bytes);
    }

    private static ImmutableArray<PendingOperation> DecodeBytes(ImmutableArray<byte> bytes)
    {
        if (bytes.Length < 4 || (bytes.Length & 1) != 0)
            throw new FormatException("Malformed UTF-16 MULTI_SZ.");
        var text = new UnicodeEncoding(false, false, true).GetString(bytes.AsSpan());
        if (!text.EndsWith("\0\0", StringComparison.Ordinal))
            throw new FormatException("Unterminated MULTI_SZ.");
        if (text == "\0\0")
            return [];
        var parts = text.Split('\0');
        var count = parts.Length - 1;
        if ((count & 1) != 0)
            count = RemoveTerminator(parts, count);
        return Enumerable.Range(0, count / 2)
            .Select(index => Operation(parts[index * 2], parts[index * 2 + 1])).ToImmutableArray();
    }

    private static int RemoveTerminator(string[] parts, int count) =>
        parts[count - 1].Length == 0 ? count - 1 :
            throw new FormatException("Pending operations require source/destination pairs.");

    private static PendingOperation Operation(string source, string destination) => new(
        WindowsEvidencePath.FromPending(source),
        destination.Length == 0 ? null : WindowsEvidencePath.FromPending(
            destination[0] == '!' ? destination[1..] : destination));

    private static IEnumerable<string> Endpoints(PendingOperation operation) =>
        operation.Destination is { } destination
            ? [operation.Source, destination] : [operation.Source];

    private static PendingDisposition Relation(string path, string resource, IPathIdentityQuery paths)
    {
        var candidate = WindowsEvidencePath.Normalize(path);
        var root = WindowsEvidencePath.Normalize(resource);
        if (paths.Read(candidate) is not { State: ObservationState.Known, Value: { } actual } ||
            paths.Read(root) is not { State: ObservationState.Known, Value: { } actualRoot })
            return PendingDisposition.Unknown;
        if (!WindowsEvidencePath.Same(candidate, actual.Path) ||
            !WindowsEvidencePath.Same(root, actualRoot.Path))
            return PendingDisposition.Alias;
        return WindowsEvidencePath.Intersects(actual.Path, actualRoot.Path)
            ? PendingDisposition.Relevant : PendingDisposition.None;
    }
}
