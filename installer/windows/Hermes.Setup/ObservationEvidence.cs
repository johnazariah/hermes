using System.Collections.Immutable;

namespace Hermes.Setup;

internal enum ObservationState { Known, Missing, Unknown }
internal enum ObservationFault { None, NativeQuery, Malformed, Ambiguous, Identity, Unreadable }

internal sealed record ReadObservation<T>(
    ObservationState State,
    T? Value,
    ObservationFault Fault = ObservationFault.None,
    uint NativeCode = 0) where T : class
{
    internal static ReadObservation<T> Known(T value) => new(ObservationState.Known, value);
    internal static ReadObservation<T> Missing() => new(ObservationState.Missing, null);
    internal static ReadObservation<T> Unknown(ObservationFault fault, uint code = 0) =>
        new(ObservationState.Unknown, null, fault, code);
}

internal enum RegistryAtomKind { Missing, Text, Integer, Strings, Unsupported }

internal sealed record RegistryAtom(
    RegistryAtomKind Kind,
    string? Text,
    int? Integer,
    ImmutableArray<string> Strings);

internal sealed record RegistryBytes(uint Type, ImmutableArray<byte> Bytes);
internal sealed record PathIdentity(string Path);

internal interface IPathIdentityQuery
{
    ReadObservation<PathIdentity> Read(string path);
}

// A portable spelling parser, not an installation-placement decision.
// Positive outside evidence additionally requires the physical-path capability.
internal static class WindowsEvidencePath
{
    internal static string Normalize(string path)
    {
        var text = path.Replace('\\', '/');
        var drive = text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1..3] == ":/";
        var prefix = drive ? 3 : text.StartsWith("//", StringComparison.Ordinal) ? 2 : 0;
        if (prefix == 0)
            throw new FormatException("An ordinary absolute Windows path is required.");
        var tail = text[prefix..].TrimEnd('/');
        var segments = tail.Length == 0 ? Array.Empty<string>() : tail.Split('/');
        if ((!drive && segments.Length < 2) || !segments.All(ValidSegment))
            throw new FormatException("The Windows path has an unsupported identity.");
        return (drive ? $"{char.ToUpperInvariant(text[0])}:/" : "//") +
            string.Join('/', segments);
    }

    internal static string FromPending(string path) => Normalize(path switch
    {
        var unc when unc.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase) =>
            @"\\" + unc[8..],
        var dos when dos.StartsWith(@"\??\", StringComparison.Ordinal) => dos[4..],
        var unc when unc.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) =>
            @"\\" + unc[8..],
        var dos when dos.StartsWith(@"\\?\", StringComparison.Ordinal) => dos[4..],
        _ => path
    });

    internal static bool Same(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    internal static bool Intersects(string left, string right) =>
        Contains(Normalize(left), Normalize(right)) ||
        Contains(Normalize(right), Normalize(left));

    private static bool Contains(string root, string path) =>
        string.Equals(root, path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.EndsWith('/') ? root : root + "/",
            StringComparison.OrdinalIgnoreCase);

    private static bool ValidSegment(string segment) =>
        segment.Length > 0 && segment[^1] is not ('.' or ' ') &&
        segment.All(character => character >= ' ' &&
            character is not ('<' or '>' or ':' or '"' or '|' or '?' or '*')) &&
        !Device(segment);

    private static bool Device(string segment)
    {
        var name = segment.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            name.Length == 4 && (name[..3] is "COM" or "LPT") &&
            name[3] is >= '1' and <= '9' or '¹' or '²' or '³';
    }
}
