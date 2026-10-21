using System.Collections.Immutable;
using System.Text;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed class SignatureImportDiagnosticTests
{
    [Theory]
    [InlineData("Signature\tSignature\r\n",
        "5369676E6174757265095369676E61747572650D0A")]
    [InlineData("FeatureComponents\tFeature_\tComponent_\r\n",
        "46656174757265436F6D706F6E656E747309466561747572655F09436F6D706F6E656E745F0D0A")]
    [InlineData("Upgrade\tUpgradeCode\tActionProperty\r\n",
        "557067726164650955706772616465436F646509416374696F6E50726F70657274790D0A")]
    [InlineData("Signature\tSignature\n",
        "5369676E6174757265095369676E61747572650A")]
    [InlineData("\r\n", "0D0A")]
    public void CapturesExactThirdLineIncludingTerminatorWithoutReadingRows(
        string thirdLine, string expectedHex)
    {
        byte[] header = [.. "column\r\ns72\r\n"u8,
            .. Encoding.ASCII.GetBytes(thirdLine)];
        using var source = new MemoryStream(
            [.. header, .. "ROW_MUST_NOT_BE_READ\r\n"u8]);
        Assert.Equal(expectedHex, SignatureFixture.ReadThirdHeaderLineHex(source));
        Assert.Equal((long)header.Length, source.Position);
    }

    [Fact]
    public void CaptureDoesNotDecodeOrNormalizeBytes()
    {
        using var source = new MemoryStream(
            [.. "one\ntwo\n"u8, 0x00, 0x80, 0xFF, .. "\r\nROW"u8]);
        Assert.Equal("0080FF0D0A", SignatureFixture.ReadThirdHeaderLineHex(source));
        Assert.Equal(13L, source.Position);
    }

    [Theory]
    [InlineData(0, "580D0A")]
    [InlineData(1, "unknown")]
    public void TotalHeaderReadIsBoundedIncludingTheFirstTwoLines(
        int overflow, string expectedHex)
    {
        var prefix = Enumerable.Repeat((byte)'x',
            SignatureFixture.ImportHeaderByteLimit - 5 + overflow);
        using var source = new MemoryStream([.. prefix, .. "\n\nX\r\nROW"u8]);
        Assert.Equal(expectedHex, SignatureFixture.ReadThirdHeaderLineHex(source));
        Assert.Equal((long)SignatureFixture.ImportHeaderByteLimit, source.Position);
    }

    [Theory]
    [InlineData("")]
    [InlineData("one\n")]
    [InlineData("one\ntwo\nSignature\tSignature\r")]
    public void IncompleteHeaderReturnsUnknownRatherThanPartialBytes(string content)
    {
        using var source = new MemoryStream(Encoding.ASCII.GetBytes(content));
        Assert.Equal("unknown", SignatureFixture.ReadThirdHeaderLineHex(source));
    }

    [Fact]
    public void ReadFailureReturnsUnknown()
    {
        using var source = new MemoryStream();
        source.Dispose();
        Assert.Equal("unknown", SignatureFixture.ReadThirdHeaderLineHex(source));
    }

    [Fact]
    public void FileCaptureUsesActualBytesAndRetainsOnlyTheKnownTableOnReadFailure()
    {
        const string filename = "control.idt";
        var root = Directory.CreateTempSubdirectory("Hermes.Signature.Header.Tests-");
        try
        {
            File.WriteAllBytes(Path.Combine(root.FullName, filename),
                "Name\r\ns72\r\nSignature\tSignature\r\nROW_MUST_NOT_BE_READ\r\n"u8.ToArray());
            var actual = SignatureFixture.CaptureImportHeader(
                "Signature", root.FullName, filename);
            Assert.Equal(("Signature", "5369676E6174757265095369676E61747572650D0A"), actual);
            File.Delete(Path.Combine(root.FullName, filename));
            Assert.Equal(("Signature", "unknown"),
                SignatureFixture.CaptureImportHeader("Signature", root.FullName, filename));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void UnknownTableIsNotEchoedAndItsFileIsNotOpened()
    {
        Assert.Equal(("unknown", "unknown"),
            SignatureFixture.CaptureImportHeader(
                "arbitrary-private-identifier", "\0", "\0"));
    }

    [Fact]
    public void SuccessfulOutputIsNotDuplicated()
    {
        var lines = ImmutableArray.CreateBuilder<string>();
        SignatureFixture.WriteImportDiagnostic(lines.Add, "HEADER=", "0D0A");
        Assert.Equal("HEADER=0D0A", Assert.Single(lines));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutputFailureAttemptsOnlyFixedUnknownAndCannotEscape(bool failFallback)
    {
        var calls = 0;
        string? delivered = null;
        void Write(string value)
        {
            calls++;
            if (calls == 1 || failFallback)
                throw new IOException("Synthetic diagnostic output failure.");
            delivered = value;
        }
        SignatureFixture.WriteImportDiagnostic(Write, "HEADER=", "DEADBEEF");
        Assert.Equal(2, calls);
        Assert.Equal(failFallback ? null : "HEADER=unknown", delivered);
    }
}
