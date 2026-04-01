# Hermes — Structured PDF-to-Markdown Extraction

> Design doc for converting machine-generated PDFs into structured markdown that downstream consumers (Osprey, AI agents) can process without needing PDF libraries.  
> Created: 2026-04-01

---

## 1. The Goal

**Hermes is the PDF boundary.** No downstream consumer should ever need a PDF library. Hermes ingests PDFs and serves structured markdown via MCP. Consumers get clean text with tables, headings, key-value pairs, and metadata.

This transforms:
```
Consumer code: open PDF → parse bytes → extract text → find tables → regex → data
```
Into:
```
Consumer code: call hermes_get_document_content(id, "markdown") → parse markdown → data
```

---

## 2. Document Types in the Archive

From the real archive (4,166 PDFs across 3 email accounts):

| Category | Count | Typical documents | Structure type |
|----------|-------|------------------|---------------|
| unsorted | 2,988 | Mixed — needs classification | Unknown |
| invoices | 523 | Utility bills, service invoices | Key-value pairs + line items table |
| bank-statements | 319 | Westpac PDF statements | Multi-page transaction table |
| receipts | 143 | Purchase receipts, confirmation PDFs | Key-value pairs + single table |
| tax | 102 | ATO notices, income statements, PAYG summaries | Structured forms with labelled fields |
| unclassified | 76 | Intake queue | Unknown |
| payslips | 15 | Microsoft, QLD Education | Multi-section with tables |

From Osprey's parsers, the structural elements these documents contain:

| Structure | Example | Where found |
|-----------|---------|-------------|
| **Key-value pairs** | `Pay Date: 31.07.2024` | Payslips, invoices, tax notices |
| **Tables** | Earnings rows, transaction lists | Payslips, bank statements, rental statements |
| **Section headings** | `EARNINGS AND ALLOWANCES`, `DEDUCTIONS` | Payslips, tax returns |
| **Summary blocks** | `Gross: $X, Tax: $Y, Net: $Z` | Payslips, invoices |
| **Line items** | Individual charges with description + amount | Invoices, rental statements |
| **Multi-page tables** | Transaction list spanning pages | Bank statements |
| **CID-encoded text** | `(cid:42)(cid:53)...` | Some QLD Gov PDFs |

---

## 3. Two-Tier Extraction

### Tier 1 — PdfPig Structural Extraction (local, fast, free)

PdfPig extracts text with **positional metadata** — every `Letter` has x, y coordinates, font name, and font size. This is the key: machine-generated PDFs have real text with precise positions. We reconstruct structure from geometry.

**What PdfPig gives us per letter:**
```fsharp
type Letter = {
    Value: string       // the character
    Location: PdfPoint  // (x, y) position on page
    FontSize: float     // size in points
    FontName: string    // e.g. "Arial-Bold"
    Width: float        // character width
}
```

**Algorithm — positional text to structured markdown:**

```
1. EXTRACT letters from each page via PdfPig
2. GROUP letters into words (x-proximity < average char width)
3. GROUP words into lines (y-proximity < line height threshold)
4. DETECT headings (font size > body font size, or bold font name)
5. DETECT tables:
   a. Find lines with 3+ consistent column positions (x-aligned across rows)
   b. Group consecutive table rows
   c. Detect column boundaries by clustering x-positions
   d. Extract cell values by column
6. DETECT key-value pairs ("Label: Value" or "Label     Value" with tab/space gap)
7. EMIT structured markdown:
   - Headings → ## Heading
   - Tables → | col | col | ... |
   - Key-value → - **Label:** Value
   - Paragraphs → plain text with line breaks
```

### Tier 2 — OCR Fallback (scanned PDFs)

When PdfPig extracts < 50 characters per page (scanned image):

**Option A**: Azure Document Intelligence (best quality, cloud)
- Returns structured JSON with tables, key-value pairs, paragraphs
- Map to the same markdown output format

**Option B**: Ollama `llava` vision (local, free, lower quality)
- Send page image to llava with prompt: "Extract all text from this document, preserving table structure"
- Parse response into markdown

**Decision**: Tier 1 handles ~90% of documents (machine-generated). Tier 2 is fallback for the ~10% that are scanned.

---

## 4. Table Detection Algorithm

The hardest part. PdfPig gives positioned text but no table structure. We infer it.

### Step 1: Line clustering

Group letters into lines by y-coordinate (allow ±2pt tolerance for baseline variations):

```fsharp
let clusterLines (letters: Letter list) : Line list =
    letters
    |> List.sortBy (fun l -> l.Location.Y, l.Location.X)
    |> List.fold (fun (lines, currentY) letter ->
        if abs (letter.Location.Y - currentY) < 2.0 then
            // same line
            ...
        else
            // new line
            ...
    ) ([], 0.0)
```

### Step 2: Column detection

For each pair of consecutive lines, check if words align at similar x-positions:

```
Line 1:  "Date"      "Description"          "Amount"
         x=50        x=150                  x=400

Line 2:  "01/10"     "Salary Deposit"       "$2,500"
         x=50        x=150                  x=400
```

If 3+ columns align across 3+ consecutive lines → it's a table.

### Step 3: Column boundary inference

Cluster all x-positions across table rows using a gap threshold:

```fsharp
let findColumnBoundaries (lines: Line list) : float list =
    let allXPositions = 
        lines 
        |> List.collect (fun l -> l.Words |> List.map (fun w -> w.X))
    // Cluster by proximity (gap > 20pt = new column)
    clusterByProximity allXPositions 20.0
    |> List.map List.average  // column center x-position
```

### Step 4: Cell extraction

For each row, assign words to columns based on nearest column boundary:

```fsharp
let extractCells (row: Line) (colBoundaries: float list) : string list =
    colBoundaries
    |> List.map (fun colX ->
        row.Words
        |> List.filter (fun w -> abs (w.X - colX) < columnWidth / 2.0)
        |> List.map (fun w -> w.Text)
        |> String.concat " ")
```

### Edge cases

| Case | Handling |
|------|----------|
| Multi-line cell (text wraps within a cell) | Detect by same column x-position on next line without other column data |
| Header row detection | First row of table, often bold or different font size |
| Merged cells | Spanning full width → treat as section header, not table row |
| Right-aligned numbers | Column boundary at right edge, not left edge, for numeric columns |
| Currency symbols | `$1,234.56` treated as one token |
| Negative amounts | `1,234.56-` or `(1,234.56)` or `-1,234.56` — normalise |

---

## 5. Key-Value Pair Detection

Many documents have labelled fields:

```
Pay Date:     31.07.2024
Employee #:   12345678
Gross Pay:    $2,732.60
```

Detection:

```fsharp
let isKeyValuePair (line: Line) : (string * string) option =
    // Pattern 1: "Label: Value"
    match line.Text with
    | Regex @"^(.+?):\s+(.+)$" [key; value] -> Some (key.Trim(), value.Trim())
    | _ ->
    // Pattern 2: Large gap between two word groups on same line
    let groups = groupByXProximity line.Words 30.0  // 30pt gap threshold
    match groups with
    | [left; right] when left.Length <= 4 -> Some (wordsToText left, wordsToText right)
    | _ -> None
```

---

## 6. Heading Detection

Headings are detected by font properties:

```fsharp
let isHeading (line: Line) (bodyFontSize: float) : int option =
    let avgFontSize = line.Words |> List.averageBy (fun w -> w.FontSize)
    let isBold = line.Words |> List.exists (fun w -> w.FontName.Contains("Bold"))
    let isAllCaps = line.Text = line.Text.ToUpperInvariant() && line.Text.Length > 3

    if avgFontSize > bodyFontSize * 1.4 then Some 1           // # H1
    elif avgFontSize > bodyFontSize * 1.1 || isBold then Some 2 // ## H2
    elif isAllCaps then Some 3                                   // ### H3
    else None
```

---

## 7. CID-Encoded Text Handling

Some PDFs (QLD Government) use CID font encoding where text appears as `(cid:42)(cid:53)...`. PdfPig may not decode these correctly.

**Detection**: If > 30% of extracted text contains `(cid:` sequences.

**Handling**: 
1. Strip CID sequences, keep any readable text around them
2. If page is mostly CID → fall back to Tier 2 (OCR)
3. Log a warning: "CID-encoded font detected, falling back to OCR"

---

## 8. Output Format

### Markdown structure

Every PDF produces markdown with this structure:

```markdown
---
source: email_attachment
account: john-personal
sender: noreply@westpac.com.au
subject: Your monthly statement
date: 2024-10-01
category: bank-statements
pages: 3
extraction_method: pdfpig-structural
extraction_confidence: 0.92
---

# Westpac Transaction Statement

## Account Details
- **Account:** Savings Maximiser
- **BSB:** 032-000
- **Account Number:** 12-3456
- **Period:** 01 Oct 2024 — 31 Oct 2024

## Transactions
| Date | Narrative | Debit | Credit | Balance |
|------|-----------|-------|--------|---------|
| 01/10/2024 | Opening Balance | | | $5,000.00 |
| 02/10/2024 | Salary Deposit | | $2,500.00 | $7,500.00 |
| 03/10/2024 | Transfer to Investment | $1,000.00 | | $6,500.00 |
| 05/10/2024 | AGL Energy | $150.00 | | $6,350.00 |

## Summary
- **Total Credits:** $2,500.00
- **Total Debits:** $1,150.00
- **Closing Balance:** $6,350.00
```

### Quality signal

Each document gets an `extraction_confidence` based on:
- Percentage of text successfully decoded (vs CID/garbled)
- Number of tables detected vs expected (based on page count)
- Percentage of lines that fit a structural pattern (heading/table/kv/paragraph)

Low confidence (< 0.5) triggers a warning in the activity log and flags the document for manual review in the UI.

---

## 9. Module Design

### `PdfStructure.fs` (new module in Hermes.Core)

```fsharp
[<RequireQualifiedAccess>]
module PdfStructure =

    // Types
    type Word = { Text: string; X: float; Y: float; FontSize: float; FontName: string }
    type Line = { Words: Word list; Y: float }
    type TableCell = { Text: string; Column: int; Row: int }
    type Table = { Headers: string list; Rows: string list list }
    type KeyValue = { Key: string; Value: string }
    
    type Block =
        | Heading of level: int * text: string
        | Paragraph of text: string
        | TableBlock of Table
        | KeyValueBlock of KeyValue list
    
    type PageContent = { PageNumber: int; Blocks: Block list }
    type DocumentContent = { Pages: PageContent list; Confidence: float }

    // Core pipeline
    let extractLetters (pdfBytes: byte[]) : Word list list  // per page
    let clusterLines (words: Word list) : Line list
    let detectBodyFontSize (lines: Line list) : float
    let detectHeadings (lines: Line list) (bodySize: float) : (Line * int) list
    let detectTables (lines: Line list) : (Line list * Table) list
    let detectKeyValues (lines: Line list) : KeyValue list
    
    // Main entry point
    let extractStructured (pdfBytes: byte[]) : DocumentContent
    
    // Markdown rendering
    let toMarkdown (doc: DocumentContent) (frontmatter: Map<string, string>) : string
```

### Integration with existing pipeline

`Extraction.fs` currently calls `extractPdfText` which returns flat text. Replace with:

```fsharp
let extractPdfContent (pdfBytes: byte[]) : Result<string * float, string> =
    let structured = PdfStructure.extractStructured pdfBytes
    if structured.Confidence < 0.3 then
        // Fall back to Tier 2 OCR
        Error "Low confidence, needs OCR"
    else
        let markdown = PdfStructure.toMarkdown structured Map.empty
        Ok (markdown, structured.Confidence)
```

The `extracted_text` column in `documents` stores the markdown output. FTS5 indexes it. MCP serves it via `hermes_get_document_content`.

### fsproj ordering

`PdfStructure.fs` goes between `Extraction.fs` and `Markdown.fs` (or replaces parts of `Markdown.fs`).

---

## 10. Implementation Phases

| Phase | What | Proof |
|-------|------|-------|
| **P1** | Letter extraction + line clustering + basic text assembly | Extract a Microsoft payslip PDF → get all text in correct reading order |
| **P2** | Heading detection (font size + bold + all-caps) | Payslip sections (EARNINGS, DEDUCTIONS, SUMMARY) detected as `## Heading` |
| **P3** | Table detection (column alignment + row grouping) | Bank statement transactions → markdown table with correct columns |
| **P4** | Key-value detection (colon-separated + gap-separated) | Payslip metadata (Pay Date, Employee #, etc.) → `- **Label:** Value` |
| **P5** | Multi-page table continuation | Bank statement spanning 3 pages → one continuous table |
| **P6** | CID fallback + confidence scoring | QLD Education payslip with CID → falls back to OCR, logs warning |
| **P7** | Integration: replace `extractPdfText` in Extraction.fs pipeline | All new documents get structured markdown. Existing documents re-extractable. |
| **P8** | MCP integration: `hermes_get_document_content(format="markdown")` returns structured output | Osprey calls MCP → gets markdown with tables → parser extracts tax events |

---

## 11. Testing Strategy

### Unit tests

- Table detection: synthetic positioned words → correct table extraction
- Key-value detection: "Label: Value" patterns → correct pairs
- Heading detection: font size variations → correct heading levels
- Column boundary clustering: x-positions → correct column count
- CID detection: text with CID sequences → confidence < 0.5

### Integration tests with real PDFs

From the archive, test against known documents:

| Document | Expected tables | Expected headings | Expected key-values |
|----------|----------------|-------------------|---------------------|
| Microsoft payslip | 3 (earnings, deductions, super) | 5+ (sections) | 8+ (employee info, dates) |
| Westpac statement | 1 (transactions, multi-page) | 2 (account details, transactions) | 4 (BSB, account, period) |
| AGL invoice | 1 (charges) | 3 (account, charges, payment) | 6+ (account #, due date, amount) |
| Ray White rental | 2+ (per-month income/expenses) | 4+ (monthly sections) | 5 (folio, property, period) |

### Regression suite

Keep a `test-pdfs/` folder with representative documents + expected markdown output. Run as integration tests to catch regressions when the algorithm changes.

---

## 12. What This Enables

Once this is in place:

| Consumer | Before | After |
|----------|--------|-------|
| **Osprey tax** | Needs pdfplumber (Python) to read payslips, rental statements | Gets markdown tables via MCP — pure text parsing |
| **Chat / AI** | Gets flat text dump, can't see table structure | Gets structured markdown — LLM can read tables |
| **Document browser (UI)** | Shows raw text blob | Shows formatted preview with headings and tables |
| **Future consumers** | Each needs its own PDF extraction | All get structured markdown for free |
| **FTS5 search** | Searches flat text — table cells are concatenated gibberish | Searches markdown — table cells are meaningful |

---

## 13. Open Questions

| # | Question | Leaning |
|---|----------|---------|
| 1 | Should we store both flat text and structured markdown? | Yes — `extracted_text` stays as flat text (backward compat), add `extracted_markdown` column. MCP serves whichever format is requested. |
| 2 | Should we re-extract all existing 4,166 PDFs with the new algorithm? | Yes — but as a background task, not blocking. Queue via `extracted_at = NULL` reset. |
| 3 | Should PdfPig positional extraction run on all pages or cap at N? | Cap at 50 pages (covers 99% of documents). Log warning for longer docs. |
| 4 | Should we use PdfPig's built-in `PageTextExtractor` or go letter-by-letter? | Letter-by-letter for table detection. `PageTextExtractor` loses positional info. |
| 5 | Should table detection use ML (e.g. a table boundary classifier) or pure geometry? | Pure geometry for v1. ML is over-engineering until geometry fails consistently. |
| 6 | Should right-aligned number columns be detected automatically? | Yes — if > 80% of values in a column parse as numbers, treat as right-aligned. |
