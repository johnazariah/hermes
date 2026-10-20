using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace Hermes.Servicing.Acceptance.Model;

public sealed record WireEvent(
    long Cursor,
    string Provider,
    int EventId,
    string Kind,
    uint? ClientPid,
    string? PackagePath,
    ProductOutcome? Outcome,
    DiagnosticValue? Diagnostic,
    ImmutableArray<string> DataNames,
    NativeReporter? Reporter = null);

public static class EngineXml
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";
    private static readonly JsonSerializerOptions Json = JsonOptions();

    public static WireEvent Parse(string xml, string? formattedMessage)
    {
        try { return ParseCore(Read(xml), formattedMessage); }
        catch (EvidenceGap) { throw; }
        catch (Exception error) when (error is XmlException or FormatException or
            InvalidOperationException or JsonException or ArgumentException or OverflowException)
        { throw new EvidenceGap("UnsupportedEventSchema"); }
    }

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    private static XElement Read(string xml)
    {
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 65536
        });
        var root = XDocument.Load(reader).Root ?? throw new EvidenceGap("MissingEvent");
        Need(root.Name == Ns + "Event", "WrongEventNamespace");
        return root;
    }

    private static WireEvent ParseCore(XElement root, string? message)
    {
        var system = root.Elements(Ns + "System").Single();
        var provider = system.Elements(Ns + "Provider").Single();
        var name = Required(provider.Attribute("Name")?.Value);
        Need(system.Element(Ns + "Channel")?.Value == "Application", "WrongChannel");
        Need(system.Element(Ns + "Version")?.Value == "0", "UnsupportedProviderVersion");
        var id = int.Parse(Required(system.Element(Ns + "EventID")?.Value), CultureInfo.InvariantCulture);
        var cursor = long.Parse(Required(system.Element(Ns + "EventRecordID")?.Value),
            CultureInfo.InvariantCulture);
        Need(cursor > 0, "InvalidCursor");
        if (name == NativeWriterProof.ProviderName)
            return Diagnostic(root, cursor, id);
        Need(name == "MsiInstaller", "WrongProvider");
        if (id is not (1040 or 1042 or 1033 or 1034 or 1035))
            return new(cursor, name, id, "Ignore", null, null, null, null, []);
        var data = root.Elements(Ns + "EventData").Single();
        return id is 1040 or 1042
            ? Lifecycle(data, cursor, id, message)
            : Outcome(data, cursor, id, message);
    }

    private static WireEvent Diagnostic(XElement root, long cursor, int id)
    {
        Need(id == 4096, "WrongDiagnosticId");
        var reporter = Reporter(root.Elements(Ns + "System").Single());
        var data = root.Elements(Ns + "EventData").Single();
        var fields = data.Elements().ToArray();
        Need(fields is [var field] && field.Name == Ns + "Data" &&
            field.Attribute("Name")?.Value == "Payload", "WrongDiagnosticEnvelope");
        Need(fields[0].Value.Length is > 0 and <= 8192, "DiagnosticPayloadLimit");
        using var document = JsonDocument.Parse(fields[0].Value);
        Unique(document.RootElement);
        var value = JsonSerializer.Deserialize<DiagnosticValue>(fields[0].Value, Json)
            ?? throw new EvidenceGap("MissingDiagnostic");
        Need(value.SchemaVersion == 1 && KnownDiagnostic(value.Kind), "UnknownDiagnosticSchema");
        return new(cursor, NativeWriterProof.ProviderName, id, "Diagnostic",
            null, null, null, value, ["Payload"], reporter);
    }

    private static NativeReporter Reporter(XElement system)
    {
        var provider = system.Elements(Ns + "Provider").Single();
        var identity = Guid.Parse(Required(provider.Attribute("Guid")?.Value));
        var execution = system.Elements(Ns + "Execution").Single();
        var pid = uint.Parse(Required(execution.Attribute("ProcessID")?.Value),
            NumberStyles.None, CultureInfo.InvariantCulture);
        var time = Required(system.Elements(Ns + "TimeCreated").Single()
            .Attribute("SystemTime")?.Value);
        Need(time.EndsWith('Z'), "UnsupportedNativeEventTime");
        var fileTime = XmlConvert.ToDateTime(time,
            XmlDateTimeSerializationMode.Utc).ToFileTimeUtc();
        Need(identity == NativeWriterProof.ProviderId && pid > 0 && fileTime > 0,
            "UnsupportedNativeReporter");
        return new(identity, pid, fileTime);
    }

    private static bool KnownDiagnostic(string value) => value is
        "ObserverReady" or "ObserverFault" or "GuardEnter" or "GuardDecision" or
        "InstallStart" or "InstallEnd" or "ActionStart";

    private static void Unique(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return;
        var properties = value.EnumerateObject().ToArray();
        Need(properties.Select(property => property.Name).Distinct().Count() == properties.Length,
            "DuplicateDiagnosticProperty");
        foreach (var property in properties)
            Unique(property.Value);
    }

    private static WireEvent Lifecycle(XElement data, long cursor, int id, string? message)
    {
        var fields = Fields(data);
        Need(fields.Length == 2 && !data.Elements(Ns + "Binary").Any(),
            "UnsupportedLifecycleShape");
        var names = fields.Select(field => field.Attribute("Name")?.Value ?? "").ToImmutableArray();
        Need(names.SequenceEqual(["", ""]) || names.SequenceEqual(["param1", "param2"]),
            "UnsupportedLifecycleNames");
        var path = fields[0].Value;
        var pid = uint.Parse(fields[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        Need(pid > 0 && path.Length is > 0 and <= 32768, "InvalidLifecycleValue");
        var verb = id == 1040 ? "Beginning" : "Ending";
        var expected = $"{verb} a Windows Installer transaction: {path}. Client Process Id: {pid}.";
        Need(message?.TrimEnd('\r', '\n') == expected, "UnvalidatedLifecycleResource");
        return new(cursor, "MsiInstaller", id, id == 1040 ? "Begin" : "End",
            pid, path, null, null, names);
    }

    private static WireEvent Outcome(XElement data, long cursor, int id, string? message)
    {
        var fields = Fields(data);
        Need(fields.Length == 7 && fields.All(field => field.Attribute("Name") is null),
            "UnsupportedOutcomeShape");
        Need(fields[5].Value == "(NULL)" && fields[6].Value == "", "UnsupportedOutcomeTail");
        var product = BinaryGuid(data.Elements(Ns + "Binary").Single().Value);
        var status = uint.Parse(fields[3].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        var value = new ProductOutcome(id, product, fields[0].Value, fields[1].Value,
            fields[2].Value, fields[4].Value, status);
        Need(message?.TrimEnd('\r', '\n') == OutcomeMessage(value), "UnvalidatedOutcomeResource");
        return new(cursor, "MsiInstaller", id, "Outcome", null, null, value, null,
            fields.Select(_ => "").ToImmutableArray());
    }

    private static string OutcomeMessage(ProductOutcome value)
    {
        var (verb, result) = value.EventId switch
        {
            1033 => ("installed", "Installation"),
            1034 => ("removed", "Removal"),
            1035 => ("reconfigured", "Reconfiguration"),
            _ => throw new EvidenceGap("UnsupportedOutcomeId")
        };
        return $"Windows Installer {verb} the product. Product Name: {value.ProductName}. " +
            $"Product Version: {value.ProductVersion}. Product Language: {value.ProductLanguage}. " +
            $"Manufacturer: {value.Manufacturer}. {result} success or error status: {value.Status}.";
    }

    private static Guid BinaryGuid(string text)
    {
        var bytes = Convert.FromHexString(text);
        var value = bytes.Length switch
        {
            38 when bytes.All(value => value < 128) => Encoding.ASCII.GetString(bytes),
            76 => new UnicodeEncoding(false, false, true).GetString(bytes),
            _ => throw new EvidenceGap("UnsupportedProductCodeEncoding")
        };
        Need(value.Length == 38, "UnsupportedProductCodeEncoding");
        return Guid.ParseExact(value, "B");
    }

    private static XElement[] Fields(XElement data)
    {
        Need(data.Elements().All(element => element.Name == Ns + "Data" ||
            element.Name == Ns + "Binary"), "UnknownEventDataElement");
        return data.Elements(Ns + "Data").ToArray();
    }

    private static string Required(string? value) =>
        value is { Length: > 0 } text ? text : throw new EvidenceGap("MissingSchemaField");

    private static void Need(bool condition, string code)
    {
        if (!condition)
            throw new EvidenceGap(code);
    }
}
