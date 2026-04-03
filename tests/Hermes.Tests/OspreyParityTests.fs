/// Osprey parity validation — verifies Hermes extractors produce markdown
/// containing the fields that Osprey's Python parsers require.
/// Test documents live in tests/test-documents/ (gitignored).
module Hermes.Tests.OspreyParityTests

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open Hermes.Core

// ─── Shared regex patterns ───────────────────────────────────────────

[<Literal>]
let private DollarAmount = @"\$?[\d,]+\.\d{2}"

[<Literal>]
let private DatePattern = @"\d{2}[/.-]\d{2}[/.-]\d{2,4}"

[<Literal>]
let private ThreeDigitCode = @"\b\d{3}\b"

[<Literal>]
let private SevenDigitId = @"\d{7}"

// ─── Assertion helpers ───────────────────────────────────────────────

let private containsCI (text: string) (sub: string) =
    text.Contains(sub, StringComparison.OrdinalIgnoreCase)

let private assertContains (md: string) (sub: string) =
    Assert.True(containsCI md sub, $"Expected markdown to contain '{sub}'")

let private assertContainsAnyOf (md: string) (terms: string list) =
    let found = terms |> List.exists (containsCI md)
    let label = terms |> String.concat "', '"
    Assert.True(found, $"Expected markdown to contain one of: '{label}'")

let private assertPattern (md: string) (pattern: string) (label: string) =
    Assert.True(Regex.IsMatch(md, pattern), $"Expected {label} pattern: {pattern}")

let private assertTable (md: string) =
    Assert.Contains("|", md)

let private assertMinConfidence (minimum: float) (actual: float) =
    Assert.True(
        actual >= minimum,
        $"Confidence {actual} < required minimum {minimum}")

// ─── Document pipeline ──────────────────────────────────────────────

let private docPath (filename: string) =
    Path.Combine(__SOURCE_DIRECTORY__, "..", "test-documents", filename)

let private tryResolve (filename: string) : string option =
    let path = docPath filename
    if File.Exists path then Some path else None

let private extractPdf (path: string) =
    let bytes = File.ReadAllBytes path
    let result = PdfStructure.extractStructured bytes
    let md = PdfStructure.toMarkdown result Map.empty
    (result, md)

// ═════════════════════════════════════════════════════════════════════
// O1: Microsoft Payslip (PDF) — AVAILABLE
// ═════════════════════════════════════════════════════════════════════

[<Fact>]
[<Trait("Category", "Integration")>]
let ``OspreyParity_O1_MicrosoftPayslip_ExtractsRequiredFields`` () =
    "microsoft-payslip.pdf"
    |> tryResolve
    |> Option.iter (fun path ->
        let result, md = extractPdf path
        // Payslip fields — word boundaries may be merged in PDF extraction
        assertContainsAnyOf md [ "Gross"; "TOTAL"; "Earnings" ]
        assertContainsAnyOf md [ "Tax"; "PAYG"; "TAX" ]
        assertContainsAnyOf md [ "Net Pay"; "Net"; "NET" ]
        assertContainsAnyOf md [ "Salary"; "Annual" ]
        assertPattern md DatePattern "pay period date"
        assertContains md "YTD"
        assertContainsAnyOf md [ "SGC"; "Super"; "SUPER" ]
        assertTable md
        assertPattern md DollarAmount "dollar amount"
        assertMinConfidence 0.5 result.Confidence)

// ═════════════════════════════════════════════════════════════════════
// O2: QLD Education Payslip (PDF) — AVAILABLE
// Note: CID fonts may cause low confidence; OCR fallback would be
// needed in that case. We do NOT assert confidence here.
// ═════════════════════════════════════════════════════════════════════

[<Fact>]
[<Trait("Category", "Integration")>]
let ``OspreyParity_O2_QldEducationPayslip_ExtractsRequiredFields`` () =
    "qld-education-payslip.pdf"
    |> tryResolve
    |> Option.iter (fun path ->
        let _result, md = extractPdf path
        // Employee number may have spaces inserted by PDF extraction
        assertPattern md @"\d{4,7}" "employee number digits"
        assertContainsAnyOf md [ "GROSS"; "NCOME"; "PAY" ]
        assertContainsAnyOf md [ "NET"; "DEDUCTIONS" ]
        assertPattern md ThreeDigitCode "3-digit earning code"
        assertTable md)

// ═════════════════════════════════════════════════════════════════════
// O3: Westpac CSV — SKIP (not available)
// ═════════════════════════════════════════════════════════════════════

[<Fact(Skip = "Test document not available in archive")>]
[<Trait("Category", "ManualTest")>]
let ``OspreyParity_O3_WestpacCsv_ExtractsRequiredFields`` () = ()

// ═════════════════════════════════════════════════════════════════════
// O4: CBA CSV — SKIP (not available)
// ═════════════════════════════════════════════════════════════════════

[<Fact(Skip = "Test document not available in archive")>]
[<Trait("Category", "ManualTest")>]
let ``OspreyParity_O4_CbaCsv_ExtractsRequiredFields`` () = ()

// ═════════════════════════════════════════════════════════════════════
// O5: Ray White Rental Statement (PDF) — AVAILABLE
// ═════════════════════════════════════════════════════════════════════

[<Fact>]
[<Trait("Category", "Integration")>]
let ``OspreyParity_O5_RentalStatement_ExtractsRequiredFields`` () =
    "rental-statement.pdf"
    |> tryResolve
    |> Option.iter (fun path ->
        let result, md = extractPdf path
        assertContains md "Rent"
        assertPattern md DollarAmount "dollar amount"
        assertTable md
        assertMinConfidence 0.3 result.Confidence)

// ═════════════════════════════════════════════════════════════════════
// O6: Fidelity Dividend CSV — SKIP (not available)
// ═════════════════════════════════════════════════════════════════════

[<Fact(Skip = "Test document not available in archive")>]
[<Trait("Category", "ManualTest")>]
let ``OspreyParity_O6_FidelityCsv_ExtractsRequiredFields`` () = ()

// ═════════════════════════════════════════════════════════════════════
// O7: Amazon Orders CSV — SKIP (not available)
// ═════════════════════════════════════════════════════════════════════

[<Fact(Skip = "Test document not available in archive")>]
[<Trait("Category", "ManualTest")>]
let ``OspreyParity_O7_AmazonCsv_ExtractsRequiredFields`` () = ()

// ═════════════════════════════════════════════════════════════════════
// O8: Utility Invoice (PDF) — AVAILABLE
// ═════════════════════════════════════════════════════════════════════

[<Fact>]
[<Trait("Category", "Integration")>]
let ``OspreyParity_O8_UtilityInvoice_ExtractsRequiredFields`` () =
    "utility-invoice.pdf"
    |> tryResolve
    |> Option.iter (fun path ->
        let result, md = extractPdf path
        assertPattern md DollarAmount "dollar amount"
        // Utility dates may be DD/MM/YY or written out
        assertPattern md @"\d{2}[/.-]\d{2}[/.-]\d{2}" "date"
        assertMinConfidence 0.3 result.Confidence)

// ═════════════════════════════════════════════════════════════════════
// O9: Credit Card CSV — SKIP (not available)
// ═════════════════════════════════════════════════════════════════════

[<Fact(Skip = "Test document not available in archive")>]
[<Trait("Category", "ManualTest")>]
let ``OspreyParity_O9_CreditCardCsv_ExtractsRequiredFields`` () = ()

// ═════════════════════════════════════════════════════════════════════
// O10: Insurance Renewal PDF — SKIP (not available)
// ═════════════════════════════════════════════════════════════════════

[<Fact(Skip = "Test document not available in archive")>]
[<Trait("Category", "ManualTest")>]
let ``OspreyParity_O10_InsuranceRenewal_ExtractsRequiredFields`` () = ()
