---
description: "Wave 1.5: Validate extraction quality against Osprey's proven parsers using 10 real documents from the archive."
---

# Osprey Parity Validation — Wave 1.5

> **Wave status**: `.project/waves/wave-1.5-osprey-parity.md` — update task status and append log entries as you work.
> **Project status**: `.project/STATUS.md`

**Branch**: `feat/osprey-parity`

**IMPORTANT: Use a git worktree.**
```
cd c:\work\hermes
git worktree add ..\hermes-parity feat/osprey-parity 2>/dev/null || git worktree add ..\hermes-parity -b feat/osprey-parity
cd c:\work\hermes-parity
```

All commands below run in `c:\work\hermes-parity`.

**Rules**:
- Use `@fsharp-dev` for all F# code
- Build + test after each document type: `dotnet build hermes.slnx --nologo && dotnet test tests/Hermes.Tests/Hermes.Tests.fsproj --nologo --no-build`
- Do NOT modify test documents — they are real user files
- DO commit test code that references test documents by relative path

---

## Context

Hermes has built extractors (PdfStructure.fs, CsvExtraction.fs, ExcelExtraction.fs, WordExtraction.fs) but hasn't validated them against real documents. Osprey (c:\work\tax-database) successfully processed these same email accounts for FY2024-25 tax preparation. Its Python parsers define exactly what fields must be extractable.

**Goal**: For each of 10 document types, prove the F# extractor produces markdown containing the fields Osprey's parser needs. Fix any extraction gaps found.

---

## Setup

1. Create `tests/test-documents/` directory (gitignored — real documents)
2. Add to `.gitignore`: `tests/test-documents/`
3. Create `tests/Hermes.Tests/OspreyParityTests.fs` — add to fsproj
4. Each test loads a real document, runs the extractor, asserts expected fields are present

---

## Document Types to Validate

### O1: Microsoft Payslip (PDF)

**Find in archive**: `~/Documents/Hermes/payslips/` — any file with "payslip" in the name from Microsoft
**Copy to**: `tests/test-documents/microsoft-payslip.pdf`

**Run through**: `PdfStructure.extractStructured` → `PdfStructure.toMarkdown`

**Assert these fields are present in the markdown output** (Osprey's PayslipParser needs them):
- "Gross" or "TAXABLE GROSS" with a dollar amount
- "Tax" or "Income tax" or "Full Income tax" with a dollar amount
- "NETPAY" or "Net Pay" with a dollar amount
- "Base Salary" with a dollar amount
- A date that looks like a pay period (DD.MM.YYYY or similar)
- "YTD" with amounts (YTD Gross, YTD Tax)
- "SGC" or "Super" with an amount
- At least one table (earnings or deductions)

**If extraction fails**: Check if PdfStructure detects the payslip's multi-column layout. Microsoft payslips have a left column (earnings) and right column (tax info) side by side. The table detector may need to handle this.

### O2: QLD Education Payslip (PDF)

**Find in archive**: Look for 7-digit employee ID filenames (e.g., `5356132_14-07-2024.pdf`)
**Copy to**: `tests/test-documents/qld-education-payslip.pdf`

**Assert**:
- Employee number (7 digits)
- Period ending date
- Earning lines with codes (001, 011, 099, etc.)
- "GROSS" amount
- "NET INCOME" amount
- CID-encoded text handling (if present — these PDFs sometimes use CID fonts)

**Known challenge**: CID fonts. If the extractor can't decode, verify confidence score < 0.5 and OCR fallback path exists.

### O3: Westpac Bank Statement (CSV)

**Find in archive**: `~/Documents/Hermes/bank-statements/` — any CSV with "Westpac" in name or containing "Narrative" column
**Copy to**: `tests/test-documents/westpac-statement.csv`

**Run through**: `CsvExtraction.extractCsv`

**Assert**:
- Headers detected: Date, Narrative, Debit, Credit, Balance (Westpac format)
- All rows preserved in markdown table
- Amounts are parseable numbers
- Dates in DD/MM/YYYY format

### O4: CBA Bank Statement (CSV)

**Find in archive**: CSV with Amount, Description, Balance columns
**Copy to**: `tests/test-documents/cba-statement.csv`

**Assert**:
- Headers: Date, Amount, Description, Balance (CBA format)
- Negative amounts for debits
- All rows in markdown table

### O5: Ray White Rental Statement (PDF)

**Find in archive**: `~/Documents/Hermes/` — search for "folio" or "ray white" or "owner statement"
**Copy to**: `tests/test-documents/rental-statement.pdf`

**Assert**:
- Property address extracted
- "Folio" number
- Period dates (From/To)
- Monthly section headers (Jul 2024, Aug 2024, etc.)
- Income lines (Rent amount)
- Expense lines (Management Fees, Water, Insurance, etc.) with amounts
- At least one table per month

### O6: Fidelity Dividend Statement (CSV)

**Find in archive**: CSV with columns like pay_date, stock, gross_usd
**Copy to**: `tests/test-documents/dividend-statement.csv`

**If not found**: Skip with `[<Trait("Category", "ManualTest")>]` — may not be in email archive.

**Assert**:
- Stock ticker symbols
- USD amounts
- Pay dates
- All columns in markdown table

### O7: Amazon Order History (CSV)

**Find in archive**: CSV with ASIN, Title, Item Total columns
**Copy to**: `tests/test-documents/amazon-orders.csv`

**If not found**: Skip — may need manual export from Amazon.

**Assert**:
- ASIN codes
- Item titles (quoted fields with commas handled)
- Dollar amounts
- Order dates

### O8: Utility Invoice — AGL or Telstra (PDF)

**Find in archive**: `~/Documents/Hermes/invoices/` — AGL, Telstra, or similar utility
**Copy to**: `tests/test-documents/utility-invoice.pdf`

**Assert**:
- "Amount Due" or "Total" with dollar amount
- Due date
- Account number
- At least one table (charges/line items)

### O9: Credit Card Statement (CSV)

**Find in archive**: CSV with Date, Description, Amount columns from CBA or similar
**Copy to**: `tests/test-documents/credit-card.csv`

**If not found**: Skip.

**Assert**:
- Merchant descriptions preserved (for regex classification)
- Dollar amounts
- Dates

### O10: Insurance Renewal (PDF)

**Find in archive**: `~/Documents/Hermes/insurance/` — Allianz, NRMA, or similar
**Copy to**: `tests/test-documents/insurance-renewal.pdf`

**Assert**:
- "Policy" number
- "Premium" amount
- Renewal/expiry date
- Vehicle or property details (if visible)
- Key-value pairs detected

---

## Test Structure

Each test should follow this pattern:

```fsharp
[<Fact>]
[<Trait("Category", "Integration")>]
let ``OspreyParity_MicrosoftPayslip_ExtractsRequiredFields`` () =
    let testDocPath = Path.Combine(__SOURCE_DIRECTORY__, "..", "test-documents", "microsoft-payslip.pdf")
    if not (File.Exists testDocPath) then
        Skip.If(true, "Test document not available — copy a Microsoft payslip to tests/test-documents/microsoft-payslip.pdf")
    else
        let bytes = File.ReadAllBytes(testDocPath)
        let result = PdfStructure.extractStructured bytes
        let markdown = PdfStructure.toMarkdown result Map.empty

        // Field presence assertions
        Assert.Contains("Gross", markdown, StringComparison.OrdinalIgnoreCase)
        Assert.Contains("Tax", markdown, StringComparison.OrdinalIgnoreCase)
        Assert.Contains("Net", markdown, StringComparison.OrdinalIgnoreCase)

        // Table detection
        Assert.Contains("|", markdown)  // markdown table syntax

        // Amount extraction (at least one dollar amount in the output)
        Assert.Matches(@"\$?[\d,]+\.\d{2}", markdown)

        // Confidence should be reasonable for machine-generated PDF
        Assert.True(result.Confidence >= 0.5, $"Confidence too low: {result.Confidence}")
```

---

## What to Fix

If a test fails because the extractor doesn't capture required fields:

1. **Table detection issue**: PdfStructure may not detect multi-column layouts (Microsoft payslips) or monthly sections (rental statements). Fix column boundary detection.

2. **Key-value detection issue**: Fields like "Policy: AZ-1234" may not be detected if the gap between label and value is unusual. Adjust gap threshold.

3. **CID font issue**: QLD Education payslips may use CID encoding. Verify confidence drops below 0.5 and the extractor signals OCR fallback needed.

4. **CSV dialect issue**: Semicolons, tabs, or unusual quoting may not be auto-detected. Fix `detectDelimiter`.

5. **Amount format issue**: European decimals (`1.234,56`), trailing negatives (`1,234.56-`), parenthetical negatives (`(1,234.56)`) may not survive extraction. Fix parsing.

For each fix: update the extractor module, add a regression test, verify the fix doesn't break other document types.

---

## Merge Gate

All 10 document types (or as many as exist in the archive) produce markdown containing the fields listed above. Each test passes. Any extraction gaps are fixed with regression tests.

```
dotnet build hermes.slnx --nologo
dotnet test tests/Hermes.Tests/Hermes.Tests.fsproj --nologo --no-build
```

All tests pass (existing 706 + new parity tests).

```
git add -A
git commit -m "test: Osprey parity validation — 10 document types verified against extraction pipeline"
git push -u origin feat/osprey-parity
```

Do NOT merge — await review.
