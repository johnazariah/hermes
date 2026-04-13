module Hermes.Tests.MarkdownTests

open Xunit
open Hermes.Core

// ─── Frontmatter rendering ───────────────────────────────────────────

let private sampleFm : Markdown.Frontmatter =
    { Source = "email_attachment"; Account = Some "john"; Sender = Some "bob@co.com"
      Subject = Some "Invoice"; Date = Some "2025-03-15"; Category = "invoices"
      OriginalName = "inv.pdf"; Vendor = Some "Bob Co"; Amount = Some "385.00"
      Abn = Some "12 345 678 901"; ExtractionMethod = "pdfpig" }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Markdown_RenderFrontmatter_ContainsAllFields`` () =
    let result = Markdown.renderFrontmatter sampleFm
    Assert.StartsWith("---", result)
    Assert.Contains("source: email_attachment", result)
    Assert.Contains("vendor: Bob Co", result)
    Assert.Contains("amount: 385.00", result)
    Assert.EndsWith("---", result.Trim())

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Markdown_RenderFrontmatter_OmitsNoneFields`` () =
    let fm = { sampleFm with Account = None; Abn = None }
    let result = Markdown.renderFrontmatter fm
    Assert.DoesNotContain("account:", result)
    Assert.DoesNotContain("abn:", result)

// ─── Field extraction ────────────────────────────────────────────────

[<Theory>]
[<InlineData("Invoice dated 15/03/2025 for services", "15/03/2025")>]
[<InlineData("Date: 2025-03-15", "2025-03-15")>]
[<InlineData("Issued 15 March 2025", "15 March 2025")>]
[<Trait("Category", "Unit")>]
let ``Markdown_ExtractDate_FindsDates`` (text: string, expected: string) =
    Assert.Equal(Some expected, Markdown.extractDate text)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Markdown_ExtractDate_NoDate_ReturnsNone`` () =
    Assert.Equal(None, Markdown.extractDate "no dates here")

[<Theory>]
[<InlineData("Total: $385.00", "385.00")>]
[<InlineData("Amount Due: $1,234.56", "1234.56")>]
[<Trait("Category", "Unit")>]
let ``Markdown_ExtractAmount_FindsAmounts`` (text: string, expected: string) =
    Assert.Equal(Some expected, Markdown.extractAmount text)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Markdown_ExtractAmount_MultiplePicks_LargestAmount`` () =
    let result = Markdown.extractAmount "Subtotal $100.00 GST $10.00 Total $110.00"
    Assert.Equal(Some "110.00", result)

[<Theory>]
[<InlineData("ABN: 12 345 678 901", "12 345 678 901")>]
[<InlineData("ABN 12345678901", "12345678901")>]
[<InlineData("ACN: 123 456 789", "123 456 789")>]
[<Trait("Category", "Unit")>]
let ``Markdown_ExtractAbn_FindsAbnAcn`` (text: string, expected: string) =
    Assert.Equal(Some expected, Markdown.extractAbn text)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Markdown_ExtractVendor_FirstLine`` () =
    Assert.Equal(Some "Bob's Plumbing Pty Ltd", Markdown.extractVendor "Bob's Plumbing Pty Ltd\nABN: 12345678901\nInvoice #42")

// ─── CSV to Markdown ─────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Markdown_CsvToMarkdown_BasicTable`` () =
    let csv = "Name,Amount\nAlice,100\nBob,200"
    let result = Markdown.csvToMarkdown csv
    Assert.Contains("| Name | Amount |", result)
    Assert.Contains("| Alice | 100 |", result)
    Assert.Contains("| --- | --- |", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Markdown_CsvToMarkdown_QuotedFields`` () =
    let csv = "Name,Note\n\"Smith, John\",\"has comma\""
    let result = Markdown.csvToMarkdown csv
    Assert.Contains("Smith, John", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Markdown_CsvToMarkdown_Empty_ReturnsPlaceholder`` () =
    Assert.Contains("Empty", Markdown.csvToMarkdown "")

// ─── Text to Markdown ────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Markdown_TextToMarkdown_PreservesParagraphs`` () =
    let result = Markdown.textToMarkdown "First paragraph\n\nSecond paragraph"
    Assert.Contains("First paragraph", result)
    Assert.Contains("Second paragraph", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Markdown_TextToMarkdown_EmptyReturnsPlaceholder`` () =
    Assert.Contains("No text", Markdown.textToMarkdown "")

// ─── Heading-aware chunking ──────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Markdown_ChunkByHeadings_SplitsOnHeadings`` () =
    let md = "## Section 1\nContent one\n\n## Section 2\nContent two"
    let chunks = Markdown.chunkByHeadings md 1000
    Assert.True(chunks.Length >= 2)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Markdown_ChunkByHeadings_ShortTextSingleChunk`` () =
    let chunks = Markdown.chunkByHeadings "Short text" 1000
    Assert.Equal(1, chunks.Length)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Markdown_ChunkByHeadings_LongSectionFallsBackToCharSplit`` () =
    let longSection = String.replicate 200 "word "
    let md = $"## Big Section\n{longSection}"
    let chunks = Markdown.chunkByHeadings md 500
    Assert.True(chunks.Length > 1)

// ─── BuildConversion integration ─────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Markdown_BuildConversion_IncludesFrontmatterAndBody`` () =
    let result =
        Markdown.buildConversion
            "Invoice total $500.00\nABN: 12345678901"
            "invoices" "inv.pdf" "email_attachment"
            (Some "john") (Some "bob@co.com") (Some "Your Invoice")
            (Some "2025-03-15") "pdfpig"
    Assert.StartsWith("---", result.Markdown)
    Assert.Contains("category: invoices", result.Markdown)
    Assert.Contains("500.00", result.Markdown)
    Assert.Equal(Some "500.00", result.Frontmatter.Amount)
    Assert.Equal(Some "12345678901", result.Frontmatter.Abn)

// ─── WriteSidecar ────────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Markdown_WriteSidecar_CreatesFile`` () =
    task {
        let m = TestHelpers.memFs ()
        let conversion =
            Markdown.buildConversion "Invoice $100" "invoices" "inv.pdf" "manual_drop" None None None None "pdfpig"
        let! result = Markdown.writeSidecar m.Fs "/archive" "invoices/inv.pdf" conversion
        Assert.True(Result.isOk result)
        Assert.True((m.Get "/archive/invoices/inv.pdf.md").IsSome)
    }

// ─── ProcessDocument with DB ─────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Markdown_ProcessDocument_WritesMarkdownSidecar`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            let! _ = db.execNonQuery
                        "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text, extraction_method) VALUES ('manual_drop', 'invoices/inv.pdf', 'invoices', 'sha1', 'Invoice $300 ABN 12345678901', 'pdfpig')"
                        []
            let! result = Markdown.processDocument m.Fs db TestHelpers.silentLogger "/archive" 1L
            Assert.True(Result.isOk result)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Markdown_ProcessDocument_NoText_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            let! _ = db.execNonQuery
                        "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('manual_drop', 'a.pdf', 'invoices', 'sha1')"
                        []
            let! result = Markdown.processDocument m.Fs db TestHelpers.silentLogger "/archive" 1L
            Assert.True(Result.isError result)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Markdown_ProcessDocument_NonexistentDoc_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            let! result = Markdown.processDocument m.Fs db TestHelpers.silentLogger "/archive" 999L
            Assert.True(Result.isError result)
        finally db.dispose ()
    }
