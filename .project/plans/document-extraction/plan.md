---
description: "Implementation plan for Doc 17: Structured Document-to-Markdown Extraction (PDF, Excel, Word, CSV). Twelve phases (P1–P12), each a silver-thread vertical slice."
design-doc: ".project/design/17-pdf-to-markdown.md"
depends-on:
  - "Doc 15 Phase U2 (Documents Navigator) — for UI preview of extracted markdown"
---

# Hermes — Implementation Plan: Document Extraction (PDF/Excel/Word/CSV)

## Prerequisites

```
dotnet build hermes.slnx --nologo
dotnet test tests/Hermes.Tests/Hermes.Tests.fsproj --nologo
```

**Expected**: 0 errors, 0 warnings, all tests pass.

**Critical rules**:
- **F# code** must go through `@fsharp-dev` agent
- **C# code** must go through `@csharp-dev` agent
- **Every phase has a PROOF** — do not mark complete until the proof passes
- Each phase must store results in the DB and be visible via at least one UI surface or MCP tool

## Dependency Map

```
P1: Letter extraction + line clustering         (foundation)
 └─ P2: Heading detection                        (needs P1 lines)
 └─ P3: Table detection                          (needs P1 lines — CRITICAL)
 └─ P4: Key-value detection                      (needs P1 lines)
P5: Multi-page table continuation                (needs P3 tables)
P6: CID fallback + confidence scoring            (needs P1 + OCR path)
P7: Pipeline integration                         (needs P1–P6 assembled)
P8: MCP integration                              (needs P7)
P9: Excel extraction                             (independent of P1–P8)
P10: Word extraction                             (independent of P1–P8)
P11: CSV extraction                              (independent of P1–P8)
P12: Format dispatch                             (needs P7 + P9 + P10 + P11)
```

P9–P11 can run in parallel with P1–P6. P12 ties everything together.

---

## Phase P1: Letter Extraction + Line Clustering

**Silver thread**: Read a PDF with PdfPig → extract letters with positions → cluster into words → cluster into lines → assemble into reading-order text → store as `extracted_text` → text is searchable via FTS5.

### What to build

**File: `src/Hermes.Core/PdfStructure.fs`** (new module)

```fsharp
[<RequireQualifiedAccess>]
module PdfStructure

type Word = { Text: string; X: float; Y: float; Width: float; FontSize: float; FontName: string }
type Line = { Words: Word list; Y: float; Text: string }

/// Extract all letters from PDF bytes, grouped by page
let extractLetters (pdfBytes: byte[]) : (int * UglyToad.PdfPig.Content.Letter list) list

/// Group letters into words by x-proximity
let lettersToWords (letters: UglyToad.PdfPig.Content.Letter list) : Word list

/// Group words into lines by y-proximity
let wordsToLines (words: Word list) : Line list

/// Full pipeline: bytes → pages of lines
let extractLines (pdfBytes: byte[]) : (int * Line list) list

/// Basic text assembly (lines joined with newlines)
let linesToText (lines: Line list) : string
```

**fsproj ordering**: Add `PdfStructure.fs` after `Extraction.fs` in `Hermes.Core.fsproj`

### What to test

**File: `tests/Hermes.Tests/PdfStructureTests.fs`** (new)

- `PdfStructure_LettersToWords_GroupsByXProximity` — synthetic letters at close x → one word; gap → two words
- `PdfStructure_WordsToLines_GroupsByYProximity` — words at same y → one line; different y → two lines
- `PdfStructure_LinesToText_PreservesReadingOrder` — lines sorted by y-desc (PDF coords) → top-to-bottom text

### Integration test (real PDF)

- Use a test PDF (can be generated or checked in as `tests/Hermes.Tests/test-data/simple.pdf`)
- `PdfStructure_ExtractLines_RealPdf_ReturnsCorrectLineCount`

### PROOF

Run a Microsoft payslip PDF through `PdfStructure.extractLines` → verify all text appears in reading order → verify line boundaries match visual line breaks. Text stored via existing `extractPdfText` path → searchable in chat.

### Commit

```
feat: PdfStructure module — letter extraction and line clustering from PdfPig
```

---

## Phase P2: Heading Detection

**Silver thread**: PDF lines → detect headings by font size/bold/all-caps → emit `## Heading` markdown → headings visible in document preview.

### What to build

**File: `src/Hermes.Core/PdfStructure.fs`** (extend)

```fsharp
type Block =
    | Heading of level: int * text: string
    | Paragraph of text: string
    | TableBlock of Table
    | KeyValueBlock of KeyValue list

/// Determine the most common font size (body text)
let detectBodyFontSize (lines: Line list) : float

/// Detect heading lines by font size > body, bold font name, or ALL CAPS
let detectHeadings (lines: Line list) (bodySize: float) : (Line * int option) list
// Returns each line paired with heading level (Some 1/2/3) or None (body text)
```

### What to test

- `PdfStructure_DetectBodyFontSize_ReturnsMostCommonSize`
- `PdfStructure_DetectHeadings_LargeFont_ReturnsH1`
- `PdfStructure_DetectHeadings_BoldFont_ReturnsH2`
- `PdfStructure_DetectHeadings_AllCaps_ReturnsH3`
- `PdfStructure_DetectHeadings_BodyText_ReturnsNone`

### PROOF

Run a payslip PDF through heading detection → "EARNINGS AND ALLOWANCES" detected as `## EARNINGS AND ALLOWANCES` → "DEDUCTIONS" detected similarly → body text not marked as heading.

### Commit

```
feat: heading detection in PdfStructure — font size, bold, and all-caps
```

---

## Phase P3: Table Detection

**Silver thread**: PDF lines → detect column-aligned text across consecutive rows → extract as `Table` with headers + rows → emit markdown table → tables visible in document preview and searchable.

### What to build

**File: `src/Hermes.Core/PdfStructure.fs`** (extend)

```fsharp
type Table = { Headers: string list; Rows: string list list }

/// Find column boundaries by clustering word x-positions
let findColumnBoundaries (lines: Line list) (gapThreshold: float) : float list

/// Check if a set of lines form a table (3+ columns aligned across 3+ rows)
let isTableRegion (lines: Line list) : bool

/// Extract cell values from a table region, assigning words to columns
let extractTableCells (lines: Line list) (colBoundaries: float list) : string list list

/// Detect and extract all tables from a page's lines
let detectTables (lines: Line list) : (Line list * Table) list
// Returns: (remaining non-table lines, extracted tables)
```

### What to test

- `PdfStructure_FindColumnBoundaries_ThreeColumns_ReturnsThreeBoundaries`
- `PdfStructure_IsTableRegion_AlignedRows_ReturnsTrue`
- `PdfStructure_IsTableRegion_ParagraphText_ReturnsFalse`
- `PdfStructure_ExtractTableCells_AssignsWordsToCorrectColumns`
- `PdfStructure_DetectTables_BankStatement_ExtractsTransactionTable`

### PROOF

Run a Westpac bank statement PDF → table detection extracts the transaction table → headers: Date, Narrative, Debit, Credit, Balance → rows contain correct values in correct columns → markdown table renders:

```
| Date | Narrative | Debit | Credit | Balance |
|------|-----------|-------|--------|---------|
| 01/10/2024 | Opening Balance | | | $5,000.00 |
```

### Commit

```
feat: table detection in PdfStructure — column alignment and cell extraction
```

---

## Phase P4: Key-Value Pair Detection

**Silver thread**: PDF lines → detect "Label: Value" and gap-separated label/value patterns → emit `- **Label:** Value` markdown → KV pairs visible in document preview.

### What to build

**File: `src/Hermes.Core/PdfStructure.fs`** (extend)

```fsharp
type KeyValue = { Key: string; Value: string }

/// Detect key-value pairs from lines (colon-separated or large-gap-separated)
let detectKeyValues (lines: Line list) : (Line * KeyValue option) list

/// Group consecutive KV pairs into a block
let groupKeyValues (kvPairs: (Line * KeyValue option) list) : Block list
```

### What to test

- `PdfStructure_DetectKV_ColonSeparated_ReturnsKV` — "Pay Date: 31.07.2024" → Key="Pay Date", Value="31.07.2024"
- `PdfStructure_DetectKV_GapSeparated_ReturnsKV` — "Employee #" at x=50, "12345678" at x=300 → KV pair
- `PdfStructure_DetectKV_ParagraphText_ReturnsNone` — regular sentence → not a KV pair

### PROOF

Run a payslip PDF → KV detection finds: Pay Date, Employee #, Gross Pay, Tax Withheld, Net Pay → markdown output:

```
- **Pay Date:** 31.07.2024
- **Employee #:** 12345678
- **Gross Pay:** $2,732.60
```

### Commit

```
feat: key-value pair detection in PdfStructure — colon and gap patterns
```

---

## Phase P5: Multi-Page Table Continuation

**Silver thread**: PDF with table spanning pages 1–3 → detect continuation (no header re-detection on page 2+) → merge into single table → one continuous markdown table.

### What to build

**File: `src/Hermes.Core/PdfStructure.fs`** (extend)

```fsharp
/// Detect if a table continues from the previous page
/// (same column count and similar column boundaries)
let isContinuation (prevTable: Table) (currentLines: Line list) (colBoundaries: float list) : bool

/// Merge tables across pages when continuation is detected
let mergeMultiPageTables (pageResults: (int * Table list) list) : Table list
```

### What to test

- `PdfStructure_IsContinuation_SameColumns_ReturnsTrue`
- `PdfStructure_IsContinuation_DifferentColumns_ReturnsFalse`
- `PdfStructure_MergeMultiPageTables_CombinesRows_KeepsSingleHeader`

### PROOF

Run a 3-page Westpac statement → all transaction rows merge into one table → no duplicate header rows on page 2/3 → markdown table has correct row count across all pages.

### Commit

```
feat: multi-page table continuation in PdfStructure
```

---

## Phase P6: CID Fallback + Confidence Scoring

**Silver thread**: PDF with CID-encoded fonts → detection triggers OCR fallback → result is still structured markdown (lower quality) → confidence score < 0.5 flagged in activity log → visible as warning in UI.

### What to build

**File: `src/Hermes.Core/PdfStructure.fs`** (extend)

```fsharp
type DocumentContent = { Pages: PageContent list; Confidence: float }
type PageContent = { PageNumber: int; Blocks: Block list }

/// Check if text is mostly CID-encoded (> 30% contains "(cid:" sequences)
let isCidEncoded (text: string) : bool

/// Calculate extraction confidence
/// Based on: % text decoded, structural patterns detected, tables found
let calculateConfidence (pages: PageContent list) (rawText: string) : float

/// Main entry: extract structured content from PDF bytes
let extractStructured (pdfBytes: byte[]) : DocumentContent
```

**File: `src/Hermes.Core/Extraction.fs`** (modify)

- When PdfStructure returns confidence < 0.3, fall back to existing OCR path (Ollama llava or Azure Doc Intelligence)
- Log warning to ActivityLog: "CID-encoded font detected, falling back to OCR"

### What to test

- `PdfStructure_IsCidEncoded_WithCidSequences_ReturnsTrue`
- `PdfStructure_IsCidEncoded_NormalText_ReturnsFalse`
- `PdfStructure_CalculateConfidence_FullyDecoded_ReturnsHigh`
- `PdfStructure_CalculateConfidence_MostlyCid_ReturnsLow`
- `Extraction_LowConfidence_FallsBackToOcr` (integration test with mock)

### PROOF

Run a QLD Government payslip with CID fonts → PdfStructure detects CID → confidence < 0.3 → falls back to OCR → extracted text is still produced (via OCR) → activity log shows "CID-encoded font detected" warning.

### Commit

```
feat: CID detection, confidence scoring, and OCR fallback in PdfStructure
```

---

## Phase P7: Pipeline Integration — Replace extractPdfText

**Silver thread**: File arrives in archive → extraction pipeline calls PdfStructure → structured markdown stored in `extracted_text` → document is searchable → document preview shows headings + tables + KV pairs → re-extraction is possible.

### What to build

**File: `src/Hermes.Core/PdfStructure.fs`** (extend)

```fsharp
/// Render DocumentContent to markdown string with YAML frontmatter
let toMarkdown (doc: DocumentContent) (frontmatter: Map<string, string>) : string
```

**File: `src/Hermes.Core/Extraction.fs`** (modify)

Replace the current `extractPdfText` call with:
```fsharp
let extractPdfContent (pdfBytes: byte[]) : Result<string * float, string> =
    let structured = PdfStructure.extractStructured pdfBytes
    if structured.Confidence < 0.3 then
        Error "Low confidence, needs OCR"
    else
        let markdown = PdfStructure.toMarkdown structured Map.empty
        Ok (markdown, structured.Confidence)
```

Wire into `extractDocument` / `extractBatch` so all new PDF documents get structured markdown.

**Schema addition**: Add `extraction_confidence REAL` column to `documents` table (migration)

### What to test

- `PdfStructure_ToMarkdown_HeadingsAndTables_WellFormed`
- `PdfStructure_ToMarkdown_Frontmatter_IncludesMetadata`
- `Extraction_ExtractDocument_Pdf_UsesStructuredPipeline`
- `Extraction_ExtractDocument_Pdf_StoresConfidence`

### PROOF

Drop a PDF into `unclassified/` → wait for sync cycle → check documents table → `extracted_text` contains structured markdown with `---` frontmatter, `##` headings, `| table |` syntax → `extraction_confidence` is populated. Open in Document Detail view (if U2 is complete) → see formatted preview. Search for a table value in chat → document found.

### Commit

```
feat: replace flat PDF extraction with PdfStructure structured markdown pipeline
```

---

## Phase P8: MCP Integration — format="markdown"

**Silver thread**: MCP client calls `hermes_get_document_content(id, format="markdown")` → returns structured markdown with tables → consumer parses clean text instead of needing a PDF library.

### What to build

**File: `src/Hermes.Core/McpTools.fs`** (modify or extend)

- Update or add `hermes_get_document_content` tool
- `format="text"`: return `extracted_text` as-is (plain text, backward compat)
- `format="markdown"`: return `extracted_text` (which IS now markdown from P7)
- `format="raw"`: for text-based files only, return original file content

**File: `src/Hermes.Core/McpServer.fs`** (wire tool handler)

### What to test

- `McpTools_GetDocumentContent_Markdown_ReturnsStructuredMarkdown`
- `McpTools_GetDocumentContent_Text_ReturnsPlainText`
- `McpTools_GetDocumentContent_Raw_ReturnsCsvContent`
- `McpTools_GetDocumentContent_InvalidId_ReturnsError`

### PROOF

Start Hermes MCP server → use MCP client (or curl) to call `hermes_get_document_content` with a PDF document ID, format="markdown" → response contains structured markdown with frontmatter, headings, and tables. Same call with format="text" → plain text.

### Commit

```
feat(mcp): hermes_get_document_content with markdown format support
```

---

## Phase P9: Excel Extraction (ClosedXML)

**Silver thread**: `.xlsx` file arrives → ClosedXML reads sheets → each sheet becomes a section with heading + markdown table → stored in DB → searchable → visible in preview → available via MCP.

### What to build

**NuGet**: Add `ClosedXML` to `Hermes.Core.fsproj`

**File: `src/Hermes.Core/ExcelExtraction.fs`** (new module)

```fsharp
[<RequireQualifiedAccess>]
module ExcelExtraction

/// Extract all sheets from an Excel workbook into DocumentContent
let extractExcel (bytes: byte[]) : PdfStructure.DocumentContent

/// Handle edge cases: empty sheets, merged cells, date/currency formatting
```

### What to test

- `ExcelExtraction_ExtractExcel_SingleSheet_ReturnsTable`
- `ExcelExtraction_ExtractExcel_MultipleSheets_ReturnsMultipleSections`
- `ExcelExtraction_ExtractExcel_EmptySheet_Skipped`
- `ExcelExtraction_ExtractExcel_DateCells_FormattedAsIso`
- `ExcelExtraction_ExtractExcel_LargeSheet_CapsAt10000Rows`

### PROOF

Drop a `.xlsx` bank statement into archive → extraction runs → `extracted_text` contains markdown with sheet headings and data tables → searchable via chat ("find my October transactions") → visible in document preview with tables.

### Commit

```
feat: Excel extraction via ClosedXML — sheets to markdown tables
```

---

## Phase P10: Word Extraction (Open XML SDK)

**Silver thread**: `.docx` file arrives → Open XML SDK reads paragraphs/tables/headings → structured markdown → stored → searchable → visible → MCP accessible.

### What to build

**NuGet**: Add `DocumentFormat.OpenXml` to `Hermes.Core.fsproj`

**File: `src/Hermes.Core/WordExtraction.fs`** (new module)

```fsharp
[<RequireQualifiedAccess>]
module WordExtraction

/// Extract content from a Word document into DocumentContent
let extractWord (bytes: byte[]) : PdfStructure.DocumentContent

/// Handle edge cases: nested tables, images (omit), headers/footers, track changes
```

### What to test

- `WordExtraction_ExtractWord_Paragraphs_ReturnsParagraphBlocks`
- `WordExtraction_ExtractWord_Headings_ReturnsCorrectLevels`
- `WordExtraction_ExtractWord_Tables_ReturnsTableBlocks`
- `WordExtraction_ExtractWord_Lists_ReturnsDashedItems`
- `WordExtraction_ExtractWord_Images_OmittedWithPlaceholder`

### PROOF

Drop a `.docx` contract into archive → extraction → `extracted_text` contains markdown with headings, tables (remuneration breakdown), bullet points → searchable → visible in preview.

### Commit

```
feat: Word extraction via Open XML SDK — paragraphs, headings, tables to markdown
```

---

## Phase P11: CSV Extraction

**Silver thread**: `.csv` file arrives → read as text → parse into header + rows → store as markdown table AND preserve raw content → searchable → raw content available via MCP `format="raw"` for Osprey.

### What to build

**File: `src/Hermes.Core/CsvExtraction.fs`** (new module)

```fsharp
[<RequireQualifiedAccess>]
module CsvExtraction

/// Parse a CSV line respecting quoted fields
let parseCsvLine (line: string) : string list

/// Detect CSV dialect (comma, semicolon, tab)
let detectDelimiter (text: string) : char

/// Extract CSV content into DocumentContent (markdown table)
let extractCsv (text: string) : PdfStructure.DocumentContent
```

**File: `src/Hermes.Core/McpTools.fs`** (extend P8)

- `format="raw"` for CSV → return original file text (not markdown)

### What to test

- `CsvExtraction_ParseCsvLine_Simple_SplitsCorrectly`
- `CsvExtraction_ParseCsvLine_QuotedFields_HandlesCommasInQuotes`
- `CsvExtraction_DetectDelimiter_Comma_ReturnsComma`
- `CsvExtraction_DetectDelimiter_Semicolon_ReturnsSemicolon`
- `CsvExtraction_ExtractCsv_ReturnsTableBlock`

### PROOF

Drop a Westpac `.csv` statement → extraction → `extracted_text` has markdown table with transactions → searchable ("find Salary Deposit") → MCP `format="raw"` returns original CSV text (Osprey receives clean CSV).

### Commit

```
feat: CSV extraction — dialect detection, markdown table, and raw content via MCP
```

---

## Phase P12: Format Dispatch — Unified Extraction Pipeline

**Silver thread**: Any supported file (.pdf, .xlsx, .docx, .csv, .txt) arrives → extension-based dispatch → correct extractor runs → structured markdown stored → all formats searchable, previewable, and MCP-accessible via a single code path.

### What to build

**File: `src/Hermes.Core/Extraction.fs`** (modify)

```fsharp
/// Unified extraction dispatch by file extension
let extractToMarkdown (fs: FileSystem) (filePath: string) (pdfBytes: byte[] option) : Task<Result<PdfStructure.DocumentContent, string>> =
    task {
        let ext = Path.GetExtension(filePath).ToLowerInvariant()
        match ext with
        | ".pdf" -> return extractPdfStructured (Option.get pdfBytes)
        | ".xlsx" | ".xls" -> return Ok (ExcelExtraction.extractExcel (fs.readAllBytes filePath))
        | ".docx" -> return Ok (WordExtraction.extractWord (fs.readAllBytes filePath))
        | ".csv" -> return Ok (CsvExtraction.extractCsv (fs.readAllText filePath))
        | ".txt" | ".md" -> return Ok { Pages = [{PageNumber=1; Blocks=[Paragraph (fs.readAllText filePath)]}]; Confidence=1.0 }
        | _ -> return Error $"Unsupported format: {ext}"
    }
```

Replace existing extraction callsites with `extractToMarkdown`.

### What to test

- `Extraction_Dispatch_Pdf_UsesPdfStructure`
- `Extraction_Dispatch_Xlsx_UsesExcelExtraction`
- `Extraction_Dispatch_Docx_UsesWordExtraction`
- `Extraction_Dispatch_Csv_UsesCsvExtraction`
- `Extraction_Dispatch_Txt_PassesThrough`
- `Extraction_Dispatch_UnknownExtension_ReturnsError`

### PROOF

Drop one file of each type (.pdf, .xlsx, .docx, .csv, .txt) into the archive → trigger sync → all 5 get `extracted_text` populated → all 5 searchable in chat → all 5 show formatted preview in Documents tab → all 5 accessible via MCP with correct content.

### Commit

```
feat: unified format dispatch — single extraction pipeline for PDF, Excel, Word, CSV, Text
```

---

## Silver Thread Integrity Check

| Phase | Input | Processing | Storage | UI Surface | MCP Surface |
|-------|-------|------------|---------|-----------|-------------|
| P1 | PDF bytes | PdfPig → letters → lines | `extracted_text` (text) | Existing preview | `hermes_read_file` |
| P2 | Lines | Font analysis → headings | Headings in markdown | `## Heading` in preview | Search finds headings |
| P3 | Lines | Column clustering → tables | Tables in markdown | Rendered table in preview | Search finds table values |
| P4 | Lines | Colon/gap detection → KV | KV in markdown | `**Label:** Value` in preview | Search finds values |
| P5 | Multi-page tables | Column match → merge | Single merged table | One continuous table | Correct row count |
| P6 | CID text | Detection → OCR fallback | OCR text + warning | Activity log warning | Content still available |
| P7 | Full pipeline | PdfStructure → markdown | `extracted_text` (markdown) + confidence | Formatted preview | `format="markdown"` |
| P8 | MCP call | Tool dispatch | — | — | `hermes_get_document_content` |
| P9 | .xlsx bytes | ClosedXML → tables | Markdown with tables | Formatted preview | Structured content |
| P10 | .docx bytes | OpenXML → blocks | Markdown with structure | Formatted preview | Structured content |
| P11 | .csv text | Parse → table | Markdown table + raw | Table preview | `format="raw"` for consumers |
| P12 | Any file | Extension dispatch | Unified path | All formats rendered | All formats served |

**No orphaned processing**: Every extraction phase stores results in `extracted_text` (visible in UI, searchable, MCP-accessible).

---

## Flags & Risks

| Risk | Impact | Mitigation |
|------|--------|------------|
| Table detection accuracy | Poor column alignment on complex PDFs | Start with clean documents (bank statements, payslips). Iterate. Confidence score flags bad extractions. |
| PdfPig perf on large PDFs | Slow extraction for 50+ page documents | Cap at first 50 pages. Process async. |
| ClosedXML version conflicts | Already used by Osprey — version mismatch? | Pin version. Hermes and Osprey are separate processes. |
| CID fonts are rare | Over-engineering fallback | Detection is cheap. Just log and skip if no OCR configured. |
| CSV dialect edge cases | Semicolons, tabs, encoding | Sniff first 5 lines. Default to comma. User can override (future). |
