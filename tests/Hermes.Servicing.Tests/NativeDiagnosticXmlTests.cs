using System.Text.Json;
using System.Xml.Linq;
using Hermes.Servicing.Acceptance.Model;
using Xunit;

namespace Hermes.Servicing.Tests;

public sealed class NativeDiagnosticXmlTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ProcessKey Writer = new(41, 1000);
    private static readonly DiagnosticValue Value = new(
        1, "GuardEnter", Guid.NewGuid(), Guid.NewGuid(), new(51, 2000), Writer,
        Guid.NewGuid(), null, "Transaction", "Repair", null, null, 0);

    [Fact]
    public void Parser_preserves_native_header_but_does_not_establish_OS_identity()
    {
        var wire = EngineXml.Parse(Xml().ToString(), null);
        var reporter = Assert.IsType<NativeReporter>(wire.Reporter);
        Assert.Equal(41u, reporter.ProcessId);
        Assert.Equal(NativeWriterProof.ProviderId, reporter.ProviderId);
        Assert.Equal(10000, reporter.EventFileTime);
        Assert.Equal(Value, wire.Diagnostic);
        Assert.Equal<string>(["Payload"], wire.DataNames);
    }

    [Theory]
    [InlineData("missing-execution")]
    [InlineData("zero-pid")]
    [InlineData("missing-pid")]
    [InlineData("missing-time")]
    [InlineData("missing-guid")]
    [InlineData("wrong-guid")]
    [InlineData("classic-provider")]
    [InlineData("classic-envelope")]
    [InlineData("duplicate-execution")]
    public void Unsupported_native_envelopes_are_not_downgraded_to_claims(string mutation)
    {
        var root = Xml();
        var system = root.Element(Ns + "System")
            ?? throw new InvalidOperationException();
        Mutate(root, system, mutation);
        Assert.Throws<EvidenceGap>(() => EngineXml.Parse(root.ToString(), null));
    }

    private static void Mutate(XElement root, XElement system, string mutation)
    {
        var execution = system.Elements(Ns + "Execution").Single();
        var provider = system.Elements(Ns + "Provider").Single();
        switch (mutation)
        {
            case "missing-execution": execution.Remove(); break;
            case "zero-pid": execution.SetAttributeValue("ProcessID", 0); break;
            case "missing-pid": execution.Attribute("ProcessID")?.Remove(); break;
            case "missing-time": system.Elements(Ns + "TimeCreated").Single().Remove(); break;
            case "missing-guid": provider.Attribute("Guid")?.Remove(); break;
            case "wrong-guid": provider.SetAttributeValue("Guid", Guid.Empty); break;
            case "classic-provider": provider.SetAttributeValue("Name", "Hermes.Servicing.Evidence"); break;
            case "classic-envelope": root.Descendants(Ns + "Data").Single().Attribute("Name")?.Remove(); break;
            case "duplicate-execution": system.Add(new XElement(execution)); break;
            default: throw new ArgumentException(nameof(mutation));
        }
    }

    private static XElement Xml() => new(Ns + "Event",
        new XElement(Ns + "System",
            new XElement(Ns + "Provider",
                new XAttribute("Name", NativeWriterProof.ProviderName),
                new XAttribute("Guid", NativeWriterProof.ProviderId.ToString("B"))),
            new XElement(Ns + "EventID", 4096),
            new XElement(Ns + "Version", 0),
            new XElement(Ns + "EventRecordID", 12),
            new XElement(Ns + "Channel", "Application"),
            new XElement(Ns + "Execution", new XAttribute("ProcessID", Writer.ProcessId)),
            new XElement(Ns + "TimeCreated", new XAttribute("SystemTime",
                DateTime.FromFileTimeUtc(10000).ToString("O")))),
        new XElement(Ns + "EventData",
            new XElement(Ns + "Data", new XAttribute("Name", "Payload"),
                JsonSerializer.Serialize(Value, Json))));
}
