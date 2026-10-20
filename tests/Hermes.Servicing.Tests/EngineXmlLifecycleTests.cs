using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Hermes.Servicing.Acceptance.Model;
using Xunit;

namespace Hermes.Servicing.Tests;

public sealed class EngineXmlLifecycleTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Path = @"C:\owned.msi";
    private const string SchemaError = "UnsupportedEventSchema";
    private static readonly ProcessKey Client = new(11, 111);
    private static readonly ProcessKey Host = new(33, 333);
    private static readonly Guid Run = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Invocation = new("55555555-5555-5555-5555-555555555555");
    private static readonly PackageKey Package = new(
        new("33333333-3333-3333-3333-333333333333"),
        new("44444444-4444-4444-4444-444444444444"),
        new('a', 64), new('b', 64), "Owned", "1.2.3", "1033", "Owned vendor");

    [Theory]
    [InlineData(1040, false, uint.MaxValue)]
    [InlineData(1042, true, 11u)]
    public void English_v0_lifecycle_preserves_client_pid_not_engine_pid(int id, bool named, uint pid)
    {
        var (xml, message) = Lifecycle(id, 110, named, pid);
        var actual = EngineXml.Parse(xml, message + "\r\n");
        AssertWire(new(110, "MsiInstaller", id, id == 1040 ? "Begin" : "End",
            pid, Path, null, null, named ? ["param1", "param2"] : ["", ""]), actual);
    }

    [Theory]
    [InlineData(1033, "Install", false)]
    [InlineData(1034, "Uninstall", true)]
    [InlineData(1035, "Repair", false)]
    public void Parsed_outcome_and_action_can_support_synthetically_bound_old_session(
        int id, string operation, bool utf16)
    {
        var outcome = ReadOutcome(id, utf16);
        var expected = new ProductOutcome(id, Package.ProductCode, Package.ProductName,
            Package.ProductVersion, Package.ProductLanguage, Package.Manufacturer, 0);
        AssertWire(new(140, "MsiInstaller", id, "Outcome", null, null, expected, null,
            ["", "", "", "", "", "", ""]), outcome);
        Assert.Equal(new OldProof(110, 130, 135, 140, 150, 0),
            BarrierCorrelation.Old(Snapshot(outcome, operation), Expected(operation)));
    }

    [Theory]
    [InlineData(1040, "<Channel>Application</Channel>", "<Channel>System</Channel>", "WrongChannel")]
    [InlineData(1040, "<Version>0</Version>", "<Version>1</Version>", "UnsupportedProviderVersion")]
    [InlineData(1040, "MsiInstaller", "OtherProvider", "WrongProvider")]
    [InlineData(1040, "Beginning", "Début", "UnvalidatedLifecycleResource")]
    [InlineData(1040, "</EventData>", "<Data>extra</Data></EventData>", "UnsupportedLifecycleShape")]
    [InlineData(1040, "<Data>", "<Data Name=\"unexpected\">", "UnsupportedLifecycleNames")]
    [InlineData(1040, "</EventData>", "<Unexpected /></EventData>", "UnknownEventDataElement")]
    [InlineData(1040, "<Data>11</Data>", "<Data>+11</Data>", SchemaError)]
    [InlineData(1040, "<Data>11</Data>", "<Data>11.0</Data>", SchemaError)]
    [InlineData(1040, "<Data>11</Data>", "<Data>4294967296</Data>", SchemaError)]
    [InlineData(1040, "<Data>11</Data>", "<Data>0</Data>", "InvalidLifecycleValue")]
    [InlineData(1040, "<EventRecordID>110</EventRecordID>",
        "<EventRecordID>9223372036854775808</EventRecordID>", SchemaError)]
    [InlineData(1040, "<EventRecordID>110</EventRecordID>",
        "<EventRecordID>0</EventRecordID>", "InvalidCursor")]
    [InlineData(1033, "<Binary>7B", "<Binary>58", SchemaError)]
    [InlineData(1033, "<Binary>7B", "<Binary>", "UnsupportedProductCodeEncoding")]
    [InlineData(1033, "(NULL)", "unexpected", "UnsupportedOutcomeTail")]
    [InlineData(1033, "<Data>", "<Data Name=\"param1\">", "UnsupportedOutcomeShape")]
    [InlineData(1033, "<Data>0</Data>", "<Data>4294967296</Data>", SchemaError)]
    [InlineData(4096, "\"schemaVersion\":1", "\"schemaVersion\":2", "UnknownDiagnosticSchema")]
    [InlineData(4096, "ActionStart", "UnknownAction", "UnknownDiagnosticSchema")]
    [InlineData(4096, "\"schemaVersion\":1",
        "\"schemaVersion\":1,\"schemaVersion\":1", "DuplicateDiagnosticProperty")]
    [InlineData(4096, "\"failures\":0", "\"failures\":0,\"extra\":0", SchemaError)]
    [InlineData(4096, "\"schemaVersion\":1,", "", SchemaError)]
    [InlineData(4096, "11111111-1111-1111-1111-111111111111", "not-a-guid", SchemaError)]
    public void Unsupported_wire_profiles_remain_evidence_gaps(
        int id, string before, string after, string code)
    {
        var (xml, message) = ValidWire(id);
        Assert.Contains(before, xml + message, StringComparison.Ordinal);
        Assert.Equal(code, Assert.Throws<EvidenceGap>(() => EngineXml.Parse(
            xml.Replace(before, after, StringComparison.Ordinal),
            message?.Replace(before, after, StringComparison.Ordinal))).Code);
    }

    [Theory]
    [InlineData(CorrelationMutation.Run, "MissingOrAmbiguousGuardDecision")]
    [InlineData(CorrelationMutation.ClientStart, "MissingOrAmbiguousGuardDecision")]
    [InlineData(CorrelationMutation.Product, "MissingOrAmbiguousGuardDecision")]
    [InlineData(CorrelationMutation.Invocation, "GuardInvocationReplay")]
    [InlineData(CorrelationMutation.WriterStart, "WriterPidReusedWithinSubscription")]
    [InlineData(CorrelationMutation.Competing, "CompetingTransactionCandidate")]
    [InlineData(CorrelationMutation.Replay, "CursorReplayOrReordering")]
    [InlineData(CorrelationMutation.Unbound, "MissingBoundTransactionBegin")]
    public void Parsing_does_not_establish_ownership_or_tolerate_competing_records(
        CorrelationMutation mutation, string code)
    {
        var snapshot = Snapshot(ReadOutcome(1035, false), "Repair");
        var decision = Assert.IsType<DiagnosticValue>(snapshot.Facts[2].Diagnostic);
        var facts = snapshot.Facts.SetItem(2, ParseDiagnostic(Corrupt(decision, mutation), 130));
        facts = mutation switch
        {
            CorrelationMutation.Competing => facts.Insert(2,
                new(125, FactKind.Begin, new(99, 999), Package, Path, null, null)),
            CorrelationMutation.Replay => facts.Insert(3, facts[2]),
            CorrelationMutation.Unbound => facts.SetItem(0,
                facts[0] with { Client = null, Package = null }),
            _ => facts
        };
        Assert.Equal(code, Assert.Throws<EvidenceGap>(() =>
            BarrierCorrelation.Old(snapshot with { Facts = facts }, Expected("Repair"))).Code);
    }

    private static ExpectedOld Expected(string operation) =>
        new(Client, Run, Package, operation, OldBoundary.AfterParentFence, Host, false);

    private static ObservationSnapshot Snapshot(WireEvent outcome, string operation)
    {
        var begin = ReadLifecycle(1040, 110);
        var end = ReadLifecycle(1042, 150);
        // Independent process/package/writer bindings are synthetic assumptions, not XML conclusions.
        return new(100,
        [
            new(begin.Cursor, FactKind.Begin, Client, Package, begin.PackagePath, null, null),
            ParseDiagnostic(Value("GuardEnter", operation), 120),
            ParseDiagnostic(Value("GuardDecision", operation), 130),
            ParseDiagnostic(Value("ActionStart", operation), 135),
            new(outcome.Cursor, FactKind.Outcome, null, null, null, null, outcome.Outcome),
            new(end.Cursor, FactKind.End, Client, Package, end.PackagePath, null, null)
        ], []);
    }

    private static DiagnosticValue Corrupt(DiagnosticValue value, CorrelationMutation mutation) =>
        mutation switch
        {
            CorrelationMutation.Run => value with { RunId = Guid.Empty },
            CorrelationMutation.Product => value with { ProductCode = Guid.Empty },
            CorrelationMutation.Invocation => value with { Invocation = Guid.Empty },
            CorrelationMutation.ClientStart => value with { Coordinator = Client with { CreationFileTime = 999 } },
            CorrelationMutation.WriterStart => value with { Writer = Host with { CreationFileTime = 999 } },
            _ => value
        };

    private static WireEvent ReadLifecycle(int id, long cursor)
    {
        var (xml, message) = Lifecycle(id, cursor);
        return EngineXml.Parse(xml, message);
    }

    private static (string Xml, string Message) Lifecycle(
        int id, long cursor, bool named = false, uint pid = 11) =>
        (Event(id, cursor, new[] { Path, pid.ToString(CultureInfo.InvariantCulture) }
            .Select((text, index) => new XElement(Ns + "Data",
                named ? new XAttribute("Name", $"param{index + 1}") : null, text))),
         $"{(id == 1040 ? "Beginning" : "Ending")} a Windows Installer transaction: " +
         $"{Path}. Client Process Id: {pid.ToString(CultureInfo.InvariantCulture)}.");

    private static WireEvent ReadOutcome(int id, bool utf16) =>
        EngineXml.Parse(OutcomeXml(id, utf16), OutcomeMessage(id));

    private static string OutcomeXml(int id, bool utf16) => Event(id, 140,
    [
        .. new[] { Package.ProductName, Package.ProductVersion, Package.ProductLanguage,
            "0", Package.Manufacturer, "(NULL)", "" }.Select(text => new XElement(Ns + "Data", text)),
        new(Ns + "Binary", Convert.ToHexString((utf16 ? Encoding.Unicode : Encoding.ASCII)
            .GetBytes(Package.ProductCode.ToString("B"))))
    ]);

    private static string OutcomeMessage(int id)
    {
        var (verb, result) = id switch
        {
            1033 => ("installed", "Installation"),
            1034 => ("removed", "Removal"),
            1035 => ("reconfigured", "Reconfiguration"),
            _ => throw new ArgumentOutOfRangeException(nameof(id))
        };
        return $"Windows Installer {verb} the product. Product Name: {Package.ProductName}. " +
            $"Product Version: {Package.ProductVersion}. Product Language: {Package.ProductLanguage}. " +
            $"Manufacturer: {Package.Manufacturer}. {result} success or error status: 0.";
    }

    private static DiagnosticValue Value(string kind, string operation) =>
        new(1, kind, Invocation, Run, Client, kind == "ActionStart" ? Client : Host,
            Package.ProductCode, null, "Transaction",
            operation, kind == "GuardDecision" ? 0 : null,
            kind == "ActionStart" ? "RemoveExistingProducts" : null, 0);

    private static string DiagnosticXml(DiagnosticValue value, long cursor) =>
        Event(4096, cursor,
            [new(Ns + "Data", new XAttribute("Name", "Payload"),
                JsonSerializer.Serialize(value, Json))],
            NativeWriterProof.ProviderName, value.Writer.ProcessId);

    private static LogFact ParseDiagnostic(DiagnosticValue value, long cursor)
    {
        var wire = EngineXml.Parse(DiagnosticXml(value, cursor), null);
        var reporter = new NativeReporter(
            NativeWriterProof.ProviderId, value.Writer.ProcessId, 10000);
        AssertWire(new(cursor, NativeWriterProof.ProviderName, 4096, "Diagnostic",
            null, null, null, value, ["Payload"], reporter), wire);
        // These OS-process observations are synthetic model inputs, not XML evidence.
        var binding = NativeWriterProof.Bind(
            cursor, wire.Reporter, value.Writer, value.Writer, 20000);
        return new(wire.Cursor, FactKind.Diagnostic, value.Coordinator,
            null, null, wire.Diagnostic, null, binding);
    }

    private static string Event(int id, long cursor, IEnumerable<XElement> data,
        string provider = "MsiInstaller", uint reporterPid = 777) => new XElement(Ns + "Event",
            new XElement(Ns + "System",
                new XElement(Ns + "Provider", new XAttribute("Name", provider),
                    provider == NativeWriterProof.ProviderName
                        ? new XAttribute("Guid", NativeWriterProof.ProviderId.ToString("B")) : null),
                new XElement(Ns + "EventID", id), new XElement(Ns + "Version", 0),
                new XElement(Ns + "EventRecordID", cursor),
                new XElement(Ns + "Channel", "Application"),
                new XElement(Ns + "Execution", new XAttribute("ProcessID", reporterPid)),
                new XElement(Ns + "TimeCreated", new XAttribute("SystemTime",
                    DateTime.FromFileTimeUtc(10000).ToString("O")))),
            new XElement(Ns + "EventData", data)).ToString(SaveOptions.DisableFormatting);

    private static (string Xml, string? Message) ValidWire(int id) => id switch
    {
        1040 => Lifecycle(1040, 110),
        1033 => (OutcomeXml(1033, false), OutcomeMessage(1033)),
        4096 => (DiagnosticXml(Value("ActionStart", "Repair"), 135), null),
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };

    private static void AssertWire(WireEvent expected, WireEvent actual)
    {
        Assert.Equal(expected, actual with { DataNames = expected.DataNames });
        Assert.Equal<string>(expected.DataNames, actual.DataNames);
    }

    public enum CorrelationMutation
    {
        Run, ClientStart, Product, Invocation, WriterStart, Competing, Replay, Unbound
    }
}
