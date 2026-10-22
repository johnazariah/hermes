using Hermes.Servicing.Acceptance.Model;
using Xunit;

namespace Hermes.Servicing.Tests;

public sealed class EngineXmlTests
{
    [Fact]
    public void Lifecycle_requires_actual_resource_agreement()
    {
        var xml = BeginXml("11");
        var record = EngineXml.Parse(xml,
            @"Beginning a Windows Installer transaction: C:\owned.msi. Client Process Id: 11.");
        Assert.Equal("Begin", record.Kind);
        Assert.Equal(11u, record.ClientPid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Ending a Windows Installer transaction: C:\\owned.msi. Client Process Id: 11.")]
    [InlineData("Beginning a Windows Installer transaction: C:\\other.msi. Client Process Id: 11.")]
    public void Id_alone_or_coincidental_fields_are_not_a_binding(string? rendering) =>
        Assert.Throws<EvidenceGap>(() => EngineXml.Parse(BeginXml("11"), rendering));

    [Fact]
    public void Wrong_channel_is_unsupported() =>
        Assert.Throws<EvidenceGap>(() => EngineXml.Parse(
            BeginXml("11").Replace("<Channel>Application</Channel>", "<Channel>System</Channel>"),
            @"Beginning a Windows Installer transaction: C:\owned.msi. Client Process Id: 11."));

    [Fact]
    public void Extra_lifecycle_field_is_unsupported() =>
        Assert.Throws<EvidenceGap>(() => EngineXml.Parse(
            BeginXml("11").Replace("</EventData>", "<Data>extra</Data></EventData>"),
            @"Beginning a Windows Installer transaction: C:\owned.msi. Client Process Id: 11."));

    private static string BeginXml(string pid) =>
        "<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\">" +
        "<System><Provider Name=\"MsiInstaller\"/><EventID>1040</EventID><Version>0</Version>" +
        "<EventRecordID>101</EventRecordID><Channel>Application</Channel></System>" +
        $"<EventData><Data>C:\\owned.msi</Data><Data>{pid}</Data></EventData></Event>";
}
