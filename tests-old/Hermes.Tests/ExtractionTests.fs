module Hermes.Tests.ExtractionTests

open System
open Xunit
open Hermes.Core

// ─── Date parsing tests ──────────────────────────────────────────────

[<Theory>]
[<InlineData("Invoice date: 15/03/2025", "15/03/2025")>]
[<InlineData("Date: 03-15-2025", "03-15-2025")>]
[<InlineData("Issued 2025-01-31", "2025-01-31")>]
[<InlineData("Due: 5/1/2024", "5/1/2024")>]
[<Trait("Category", "Unit")>]
let ``Extraction_TryExtractDate_CommonFormats_ExtractsDate`` (input: string, expected: string) =
    let result = Extraction.tryExtractDate input
    Assert.True(result.IsSome, $"Expected Some for input: {input}")
    Assert.Equal(expected, result.Value)

[<Theory>]
[<InlineData("Date: 15 March 2025", "15 March 2025")>]
[<InlineData("Issued on 1 Jan 2024", "1 Jan 2024")>]
[<InlineData("Due: 28 February 2023", "28 February 2023")>]
[<InlineData("Date: 3 Dec 2024", "3 Dec 2024")>]
[<Trait("Category", "Unit")>]
let ``Extraction_TryExtractDate_MonthNameFormats_ExtractsDate`` (input: string, expected: string) =
    let result = Extraction.tryExtractDate input
    Assert.True(result.IsSome, $"Expected Some for input: {input}")
    Assert.Equal(expected, result.Value)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_TryExtractDate_NoDate_ReturnsNone`` () =
    let result = Extraction.tryExtractDate "No date information here"
    Assert.True(result.IsNone)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_TryExtractDate_EmptyInput_ReturnsNone`` () =
    let result = Extraction.tryExtractDate ""
    Assert.True(result.IsNone)

// ─── Amount extraction tests ─────────────────────────────────────────

[<Theory>]
[<InlineData("Total: $1,234.56", 1234.56)>]
[<InlineData("Amount due: $99.00", 99.00)>]
[<InlineData("AUD 500.00", 500.00)>]
[<InlineData("$42.50 paid", 42.50)>]
[<Trait("Category", "Unit")>]
let ``Extraction_TryExtractAmount_CommonFormats_ExtractsAmount`` (input: string, expected: float) =
    let result = Extraction.tryExtractAmount input
    Assert.True(result.IsSome, $"Expected Some for input: {input}")
    Assert.Equal(decimal expected, result.Value)

[<Theory>]
[<InlineData("Total: $10,000.00", 10000.00)>]
[<InlineData("$1,234,567.89", 1234567.89)>]
[<Trait("Category", "Unit")>]
let ``Extraction_TryExtractAmount_LargeAmounts_ExtractsCorrectly`` (input: string, expected: float) =
    let result = Extraction.tryExtractAmount input
    Assert.True(result.IsSome, $"Expected Some for input: {input}")
    Assert.Equal(decimal expected, result.Value)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_TryExtractAmount_NoAmount_ReturnsNone`` () =
    let result = Extraction.tryExtractAmount "No amount here"
    Assert.True(result.IsNone)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_TryExtractAmount_EmptyInput_ReturnsNone`` () =
    let result = Extraction.tryExtractAmount ""
    Assert.True(result.IsNone)

// ─── ABN/ACN pattern matching tests ──────────────────────────────────

[<Theory>]
[<InlineData("ABN: 51 824 753 556", "51824753556")>]
[<InlineData("ABN 12345678901", "12345678901")>]
[<InlineData("ABN:11 222 333 444", "11222333444")>]
[<Trait("Category", "Unit")>]
let ``Extraction_TryExtractAbn_ValidABN_ExtractsDigits`` (input: string, expected: string) =
    let result = Extraction.tryExtractAbn input
    Assert.True(result.IsSome, $"Expected Some for input: {input}")
    Assert.Equal(expected, result.Value)

[<Theory>]
[<InlineData("ACN: 123 456 789", "123456789")>]
[<InlineData("ACN:987 654 321", "987654321")>]
[<Trait("Category", "Unit")>]
let ``Extraction_TryExtractAbn_ValidACN_ExtractsDigits`` (input: string, expected: string) =
    let result = Extraction.tryExtractAbn input
    Assert.True(result.IsSome, $"Expected Some for input: {input}")
    Assert.Equal(expected, result.Value)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_TryExtractAbn_NoAbn_ReturnsNone`` () =
    let result = Extraction.tryExtractAbn "No business number here"
    Assert.True(result.IsNone)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_TryExtractAbn_EmptyInput_ReturnsNone`` () =
    let result = Extraction.tryExtractAbn ""
    Assert.True(result.IsNone)

// ─── Vendor extraction tests ─────────────────────────────────────────

[<Theory>]
[<InlineData("From: Acme Corporation", "Acme Corporation")>]
[<InlineData("Billed by: Widget Co", "Widget Co")>]
[<InlineData("Vendor: Smith & Sons", "Smith & Sons")>]
[<InlineData("Supplier: Tech Services", "Tech Services")>]
[<Trait("Category", "Unit")>]
let ``Extraction_TryExtractVendor_LabelledVendor_ExtractsName`` (input: string, expected: string) =
    let result = Extraction.tryExtractVendor input
    Assert.True(result.IsSome, $"Expected Some for input: {input}")
    Assert.Equal(expected, result.Value)

[<Theory>]
[<InlineData("Smith & Associates Pty Ltd\nInvoice #123")>]
[<InlineData("Jones Plumbing Pty. Ltd.\nDate: 15/03/2025")>]
[<Trait("Category", "Unit")>]
let ``Extraction_TryExtractVendor_PtyLtdPattern_ExtractsCompany`` (input: string) =
    let result = Extraction.tryExtractVendor input
    Assert.True(result.IsSome, $"Expected Some for input: {input}")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_TryExtractVendor_NoVendor_ReturnsNone`` () =
    let result = Extraction.tryExtractVendor "Just some plain text with no vendor"
    Assert.True(result.IsNone)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_TryExtractVendor_EmptyInput_ReturnsNone`` () =
    let result = Extraction.tryExtractVendor ""
    Assert.True(result.IsNone)

// ─── Scanned PDF detection tests ─────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_IsLikelyScanned_ShortText_ReturnsTrue`` () =
    Assert.True(Extraction.isLikelyScanned "abc")
    Assert.True(Extraction.isLikelyScanned "")
    Assert.True(Extraction.isLikelyScanned "  short text  ")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_IsLikelyScanned_EmptyText_ReturnsTrue`` () =
    Assert.True(Extraction.isLikelyScanned "")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_IsLikelyScanned_LongText_ReturnsFalse`` () =
    let longText = String.replicate 100 "This is a proper document with plenty of text. "
    Assert.False(Extraction.isLikelyScanned longText)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_IsLikelyScanned_ExactlyAtThreshold_ReturnsFalse`` () =
    let text = String('x', Extraction.ScannedCharThreshold)
    Assert.False(Extraction.isLikelyScanned text)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_IsLikelyScanned_JustBelowThreshold_ReturnsTrue`` () =
    let text = String('x', Extraction.ScannedCharThreshold - 1)
    Assert.True(Extraction.isLikelyScanned text)

// ─── AnalyseText integration tests ──────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_AnalyseText_FullDocument_ExtractsAllFields`` () =
    let text = """
INVOICE
From: Acme Trading Pty Ltd
ABN: 51 824 753 556

Date: 15/03/2025
Invoice #: INV-2025-042

Description         Amount
Widget repair       $1,234.56

Total due: $1,234.56
"""

    let result = Extraction.analyseText text "pdfpig" None
    Assert.True(result.Date.IsSome, "Expected date")
    Assert.True(result.Amount.IsSome, "Expected amount")
    Assert.True(result.Abn.IsSome, "Expected ABN")
    Assert.True(result.Vendor.IsSome, "Expected vendor")
    Assert.Equal("pdfpig", result.Method)
    Assert.True(result.OcrConfidence.IsNone)

// ─── File type detection tests ───────────────────────────────────────

[<Theory>]
[<InlineData("invoice.pdf", true)>]
[<InlineData("DOCUMENT.PDF", true)>]
[<InlineData("file.Pdf", true)>]
[<InlineData("image.png", false)>]
[<InlineData("report.docx", false)>]
[<Trait("Category", "Unit")>]
let ``Extraction_IsPdf_DetectsCorrectly`` (path: string, expected: bool) =
    Assert.Equal(expected, Extraction.isPdf path)

[<Theory>]
[<InlineData("photo.png", true)>]
[<InlineData("scan.jpg", true)>]
[<InlineData("document.jpeg", true)>]
[<InlineData("scan.tiff", true)>]
[<InlineData("scan.tif", true)>]
[<InlineData("photo.bmp", true)>]
[<InlineData("image.webp", true)>]
[<InlineData("file.pdf", false)>]
[<InlineData("doc.txt", false)>]
[<Trait("Category", "Unit")>]
let ``Extraction_IsImage_DetectsCorrectly`` (path: string, expected: bool) =
    Assert.Equal(expected, Extraction.isImage path)

// ─── PDF text extraction tests ───────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_ExtractPdfText_InvalidPdf_ReturnsError`` () =
    let bytes = System.Text.Encoding.UTF8.GetBytes("not a pdf")
    let result = Extraction.extractPdfText bytes
    Assert.True(Result.isError result)
