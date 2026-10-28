using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hermes.Tray.Access;

internal static class AccessJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static AccessProtocolFault Invalid() => new(OwnerFailure.Protocol);

    public static T Read<T>(ReadOnlyMemory<byte> bytes, Func<JsonElement, T> read)
    {
        if (bytes.Length is < 1 or > 8192)
            throw Invalid();
        try
        {
            var text = Utf8.GetString(bytes.Span);
            using var document = JsonDocument.Parse(text, new()
            {
                MaxDepth = 8, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow
            });
            RequireUnique(document.RootElement);
            return read(document.RootElement);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        {
            throw Invalid();
        }
    }

    private static void RequireUnique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw Invalid();
                RequireUnique(property.Value);
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                RequireUnique(item);
    }

    public static void Object(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Invalid();
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != names.Length ||
            actual.Any(name => !names.Contains(name, StringComparer.Ordinal)))
            throw Invalid();
    }

    public static string Text(JsonElement value, string name, int maximum = 256)
    {
        var field = value.GetProperty(name);
        return field.ValueKind == JsonValueKind.String &&
               field.GetString() is { Length: > 0 } text && text.Length <= maximum
            ? text : throw Invalid();
    }

    public static int Integer(JsonElement value, string name)
    {
        var field = value.GetProperty(name);
        return field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var number)
            ? number : throw Invalid();
    }

    public static bool Boolean(JsonElement value, string name) => value.GetProperty(name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw Invalid()
    };

    public static DateTimeOffset Timestamp(JsonElement value, string name)
    {
        var field = value.GetProperty(name);
        return field.ValueKind == JsonValueKind.String &&
               field.TryGetDateTimeOffset(out var timestamp) && timestamp.Offset == TimeSpan.Zero
            ? timestamp : throw Invalid();
    }

    public static string Identifier(JsonElement value, string name) =>
        RequireProof(Text(value, name, 22), 16);

    public static string Challenge(JsonElement value, string name) =>
        RequireProof(Text(value, name, 43), 32);

    public static string Code(JsonElement value, string name) => RequireCode(Text(value, name, 14));

    public static string RequireCode(string value)
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        return value.Length == 14 && value[4] == '-' && value[9] == '-' &&
               value.Where((_, index) => index is not (4 or 9)).All(alphabet.Contains)
            ? value : throw Invalid();
    }

    public static string RequireProof(string value, int byteCount)
    {
        var length = byteCount == 16 ? 22 : 43;
        if (value.Length != length ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw Invalid();
        try
        {
            var decoded = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') +
                new string('=', (4 - value.Length % 4) % 4));
            return decoded.Length == byteCount && Encode(decoded) == value ? value : throw Invalid();
        }
        catch (FormatException) { throw Invalid(); }
    }

    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string NewIdentifier() => Encode(RandomNumberGenerator.GetBytes(16));

    public static ImmutableArray<AccessCapability> Capabilities(
        JsonElement value, string name, bool allowArchive)
    {
        var array = value.GetProperty(name);
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 4)
            throw Invalid();
        var capabilities = array.EnumerateArray().Select(Capability).ToImmutableArray();
        var names = capabilities.Select(AccessCapabilities.Name).ToArray();
        if ((!allowArchive && capabilities.Contains(AccessCapability.ArchiveRead)) ||
            names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            !names.SequenceEqual(names.Order(StringComparer.Ordinal)))
            throw Invalid();
        return capabilities;
    }

    private static AccessCapability Capability(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            ? AccessCapabilities.Parse(text) : throw Invalid();
}
