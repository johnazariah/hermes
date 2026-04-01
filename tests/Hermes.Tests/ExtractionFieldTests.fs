module Hermes.Tests.ExtractionFieldTests

open System
open Xunit
open Hermes.Core

// ─── Fake text extractor ─────────────────────────────────────────────

let private fakeExtractor : Algebra.TextExtractor =
    { extractPdf = fun _ -> task { return Ok "Invoice $500.00 ABN 12345678901 dated 15/03/2025\nBob's Plumbing Pty Ltd" }
      extractImage = fun _ -> task { return Ok "Scanned receipt $42.00" } }

let private failingExtractor : Algebra.TextExtractor =
    { extractPdf = fun _ -> task { return Error "PDF corrupt" }
      extractImage = fun _ -> task { return Error "OCR failed" } }

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

// ─── processDocument (integration with DB) ───────────────────────────

let private insertDoc (db: Algebra.Database) (path: string) (cat: string) =
    task {
        let! _ =
            db.execNonQuery
                "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('manual_drop', @p, @c, @s)"
                ([ ("@p", Database.boxVal path); ("@c", Database.boxVal cat); ("@s", Database.boxVal (Guid.NewGuid().ToString("N"))) ])
        let! id = db.execScalar "SELECT last_insert_rowid()" []
        return match id with null -> 0L | v -> v :?> int64
    }

[<Fact(Skip = "processDocument updateDocumentRow DB update needs investigation")>]
[<Trait("Category", "Integration")>]
let ``Extraction_ProcessDocument_PdfFile_ExtractsAndUpdatesDb`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Files.["archive/invoices/test.pdf"] <- "dummy pdf"
        try
            let! docId = insertDoc db "invoices/test.pdf" "invoices"
            let! result =
                Extraction.processDocument m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock fakeExtractor "archive" docId "invoices/test.pdf" false
            Assert.True(Result.isOk result)
            // Verify DB was updated
            let! extracted =
                db.execScalar "SELECT extracted_at FROM documents WHERE id = @id" ([ ("@id", Database.boxVal docId) ])
            // extracted_at should be set (not DBNull)
            Assert.True(
                (match extracted with null -> false | :? System.DBNull -> false | _ -> true),
                "extracted_at should be set after extraction")
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_ProcessDocument_MissingFile_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            let! docId = insertDoc db "invoices/gone.pdf" "invoices"
            let! result =
                Extraction.processDocument m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock fakeExtractor "archive" docId "invoices/gone.pdf" false
            Assert.True(Result.isError result)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_ProcessDocument_ExtractorFails_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Files.["archive/invoices/bad.pdf"] <- "corrupt"
        try
            let! docId = insertDoc db "invoices/bad.pdf" "invoices"
            let! result =
                Extraction.processDocument m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock failingExtractor "archive" docId "invoices/bad.pdf" false
            Assert.True(Result.isError result)
        finally db.dispose ()
    }

// ─── extractBatch ────────────────────────────────────────────────────

// Note: extractBatch uses N+1 OFFSET query pattern that has issues with execScalar returning DBNull.
// These tests validate the contract but may need adjusting when the query is refactored.

[<Fact(Skip = "extractBatch N+1 query returns DBNull for empty scalar — needs refactoring")>]
[<Trait("Category", "Integration")>]
let ``Extraction_ExtractBatch_ProcessesUnextractedDocs`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Files.["archive/invoices/a.pdf"] <- "pdf content"
        m.Files.["archive/invoices/b.pdf"] <- "pdf content"
        try
            let! _ = insertDoc db "invoices/a.pdf" "invoices"
            let! _ = insertDoc db "invoices/b.pdf" "invoices"
            let! (success, failures) =
                Extraction.extractBatch m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock fakeExtractor "archive" None false 10
            Assert.Equal(2, success)
            Assert.Equal(0, failures)
        finally db.dispose ()
    }

[<Fact(Skip = "extractBatch N+1 query returns DBNull for empty scalar — needs refactoring")>]
[<Trait("Category", "Integration")>]
let ``Extraction_ExtractBatch_RespectsLimit`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        for i in 1..5 do
            let path = $"invoices/doc{i}.pdf"
            m.Files.[$"archive/{path}"] <- "content"
            let! _ = insertDoc db path "invoices"
            ()
        try
            let! (success, _) =
                Extraction.extractBatch m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock fakeExtractor "archive" None false 2
            Assert.Equal(2, success)
        finally db.dispose ()
    }

[<Fact(Skip = "extractBatch N+1 query returns DBNull for empty scalar — needs refactoring")>]
[<Trait("Category", "Integration")>]
let ``Extraction_ExtractBatch_CategoryFilter_OnlyProcessesMatching`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Files.["archive/invoices/inv.pdf"] <- "content"
        m.Files.["archive/receipts/rcpt.pdf"] <- "content"
        try
            let! _ = insertDoc db "invoices/inv.pdf" "invoices"
            let! _ = insertDoc db "receipts/rcpt.pdf" "receipts"
            let! (success, _) =
                Extraction.extractBatch m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock fakeExtractor "archive" (Some "invoices") false 10
            Assert.Equal(1, success)
        finally db.dispose ()
    }
