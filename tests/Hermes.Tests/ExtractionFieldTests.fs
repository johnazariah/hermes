module Hermes.Tests.ExtractionFieldTests

open Xunit
open Hermes.Core

// ─── Date extraction ─────────────────────────────────────────────────

[<Theory>]
[<InlineData("Invoice dated 15/03/2025", "15/03/2025")>]
[<InlineData("Date: 2025-03-15", "2025-03-15")>]
[<InlineData("Issued 15 March 2025", "15 March 2025")>]
[<InlineData("Due: 1 Jan 2026", "1 Jan 2026")>]
[<Trait("Category", "Unit")>]
let ``Extraction_TryExtractDate_FindsDates`` (text: string, expected: string) =
    match Extraction.tryExtractDate text with
    | Some d -> Assert.Contains(expected, d)
    | None -> failwith $"Expected date '{expected}' in: {text}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_TryExtractDate_NoDate_ReturnsNone`` () =
    Assert.True((Extraction.tryExtractDate "no dates here").IsNone)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_TryExtractDate_EmptyString_ReturnsNone`` () =
    Assert.True((Extraction.tryExtractDate "").IsNone)

// ─── Amount extraction ───────────────────────────────────────────────

[<Theory>]
[<InlineData("Total: $385.00", 385.0)>]
[<InlineData("Amount Due: $1,234.56", 1234.56)>]
[<InlineData("AUD 99.99", 99.99)>]
[<Trait("Category", "Unit")>]
let ``Extraction_TryExtractAmount_FindsAmounts`` (text: string, expected: float) =
    match Extraction.tryExtractAmount text with
    | Some d -> Assert.Equal(decimal expected, d)
    | None -> failwith $"Expected amount {expected} in: {text}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_TryExtractAmount_NoAmount_ReturnsNone`` () =
    Assert.True((Extraction.tryExtractAmount "no amounts here").IsNone)

// ─── ABN/ACN extraction ─────────────────────────────────────────────

[<Theory>]
[<InlineData("ABN: 12 345 678 901", "12345678901")>]
[<InlineData("ABN 12345678901", "12345678901")>]
[<InlineData("ACN: 123 456 789", "123456789")>]
[<Trait("Category", "Unit")>]
let ``Extraction_TryExtractAbn_FindsAbnAcn`` (text: string, expected: string) =
    match Extraction.tryExtractAbn text with
    | Some abn -> Assert.Equal(expected, abn)
    | None -> failwith $"Expected ABN '{expected}' in: {text}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_TryExtractAbn_NoAbn_ReturnsNone`` () =
    Assert.True((Extraction.tryExtractAbn "no business numbers").IsNone)

// ─── Vendor extraction ──────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_TryExtractVendor_FindsCompanyName`` () =
    let text = "From: Bob's Plumbing Pty Ltd\nABN: 12345678901\nInvoice"
    match Extraction.tryExtractVendor text with
    | Some v -> Assert.Contains("Plumbing", v)
    | None -> failwith "Expected vendor name"

// ─── Scanned detection ──────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_IsLikelyScanned_ShortText_ReturnsTrue`` () =
    Assert.True(Extraction.isLikelyScanned "abc")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_IsLikelyScanned_LongText_ReturnsFalse`` () =
    Assert.False(Extraction.isLikelyScanned (String.replicate 100 "word "))

// ─── File type detection ─────────────────────────────────────────────

[<Theory>]
[<InlineData("doc.pdf", true)>]
[<InlineData("DOC.PDF", true)>]
[<InlineData("doc.txt", false)>]
[<Trait("Category", "Unit")>]
let ``Extraction_IsPdf_DetectsCorrectly`` (path: string, expected: bool) =
    Assert.Equal(expected, Extraction.isPdf path)

[<Theory>]
[<InlineData("photo.png", true)>]
[<InlineData("scan.tiff", true)>]
[<InlineData("photo.JPEG", true)>]
[<InlineData("doc.pdf", false)>]
[<Trait("Category", "Unit")>]
let ``Extraction_IsImage_DetectsCorrectly`` (path: string, expected: bool) =
    Assert.Equal(expected, Extraction.isImage path)

// ─── AnalyseText ─────────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_AnalyseText_PopulatesFields`` () =
    let result = Extraction.analyseText "Invoice $500.00 ABN 12345678901 dated 15/03/2025" "pdfpig" None
    Assert.Equal("pdfpig", result.Method)
    Assert.True(result.Amount.IsSome)
    Assert.True(result.Date.IsSome)
    Assert.True(result.Abn.IsSome)
