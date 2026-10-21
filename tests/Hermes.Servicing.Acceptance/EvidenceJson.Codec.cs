using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hermes.Servicing.Acceptance;

internal static partial class EvidenceJson
{
    internal static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        return options;
    }

    internal static string Encode<T>(T value) =>
        JsonSerializer.Serialize(value, Options);

    internal static T Decode<T>(string text) =>
        JsonSerializer.Deserialize<T>(text, Options)
        ?? throw new InvalidDataException("Missing diagnostic value.");
}
