# Phase 11: Document-to-Markdown Conversion

**Status**: Not Started  
**Depends On**: Phase 3 (Text Extraction)  
**Deliverable**: Every document in the archive has a `.md` sidecar with structured Markdown + YAML frontmatter.

---

## Objective

Refactor the extraction pipeline to produce **structured Markdown** instead of raw text. Each document gets a `.md` sidecar file alongside the original. The Markdown preserves document structure (headings, tables, lists) and includes YAML frontmatter with structured metadata. This also adds Word (.docx), Excel (.xlsx), and CSV support.

---

## Architecture

### File Layout

```
~/Documents/Hermes/invoices/
├── 2025-03-15_bobplumbing_Invoice-2025-001.pdf       ← original file
└── 2025-03-15_bobplumbing_Invoice-2025-001.pdf.md    ← extracted markdown
```

### Markdown Template

```markdown
---
source: email_attachment
account: john-personal
sender: bob@plumbing.com.au
subject: Invoice for March work
date: 2025-03-15
category: invoices
original_name: Invoice-2025-001.pdf
vendor: Bob's Plumbing
amount: 385.00
abn: 12 345 678 901
extraction_method: pdfpig
---

# Invoice #2025-001

**From:** Bob's Plumbing Pty Ltd
**Date:** 15 March 2025

## Line Items

| Description | Qty | Unit Price | Total |
|------------|-----|-----------|-------|
| Hot water system repair | 1 | $350.00 | $350.00 |
| Call-out fee | 1 | $35.00 | $35.00 |

**Total: $385.00 (inc. GST)**
```

### Pipeline Change

```
Current:  Classify → Extract (raw text → extracted_text column) → Embed (chunk by char count)
New:      Classify → Convert (→ .md file + extracted_text column) → Embed (chunk by ## headings)
```

---

## Tasks

### 11.1 — Markdown Converter Algebra
- [x] Add `MarkdownConverter` to `Algebra.fs`:
  ```fsharp
  type MarkdownConverter = {
      convert: string -> byte[] -> Task<Result<string, string>>  // filePath → fileContent → markdown
      supportedExtensions: string list
  }
  ```
- [x] The converter is selected based on file extension at the composition root

### 11.2 — PDF → Markdown Converter
- [x] Use PdfPig to extract text blocks with positions
- [x] Infer headings: text blocks with larger font size → `#`, `##`
- [x] Detect tables: aligned text blocks with consistent column positions → Markdown tables (best-effort)
- [x] Page breaks → `---` horizontal rule
- [x] Preserve paragraph structure (blank lines between text blocks)
- [x] Fallback: if structure detection fails, output as plain paragraphs (current behaviour wrapped in Markdown)

### 11.3 — Scanned PDF / Image → Markdown Converter
- [x] For scanned/image PDFs and standalone images (JPEG, PNG, TIFF)
- [x] **Ollama prompt** (change from current): "Convert this document image to structured Markdown. Preserve any tables as Markdown tables, headings as # headings, and lists as bullet points. Return only the Markdown."
- [x] **Azure Document Intelligence fallback**: use `prebuilt-layout` model which returns structured output → convert to Markdown
- [x] Set `extraction_method = "ollama_vision_md"` or `"azure_doc_intelligence_md"`

### 11.4 — Word (.docx) → Markdown Converter
- [x] Add `DocumentFormat.OpenXml` NuGet package to `Hermes.Core`
- [x] Walk the document body: `document.MainDocumentPart.Document.Body`
- [x] Map elements:
  - `Paragraph` with heading style → `#` / `##` / `###` (based on outline level)
  - `Paragraph` with normal style → plain text
  - `Paragraph` with list style → `- ` bullet or `1. ` numbered
  - **Bold** → `**text**`
  - *Italic* → `*text*`
  - `Table` → Markdown table (walk rows and cells)
  - `Hyperlink` → `[text](url)`
- [x] Handle nested tables (flatten or indent)
- [x] Ignore images embedded in the document (or note `[image omitted]`)
- [x] Set `extraction_method = "openxml_docx"`

### 11.5 — Excel (.xlsx) → Markdown Converter
- [x] Same `DocumentFormat.OpenXml` package
- [x] For each sheet in the workbook:
  - Output `## Sheet: {sheetName}`
  - Read all rows/cells → Markdown table
  - Resolve formulas to their cached values (don't evaluate)
  - Handle merged cells (duplicate content into each cell)
  - Limit: first 1000 rows per sheet (log warning if truncated)
- [x] If workbook has only one sheet, skip the `## Sheet:` heading
- [x] Set `extraction_method = "openxml_xlsx"`

### 11.6 — CSV → Markdown Converter
- [x] Read all lines via `File.ReadAllLines`
- [x] First line → table header
- [x] Remaining lines → table rows
- [x] Handle quoted fields with commas
- [x] Limit: first 1000 rows (log warning if truncated)
- [x] Set `extraction_method = "csv"`

### 11.7 — YAML Frontmatter Generation
- [x] After conversion, prepend YAML frontmatter block with all known metadata:
  - `source`, `account`, `sender`, `subject`, `date`, `category`
  - `original_name`, `vendor`, `amount`, `abn`
  - `extraction_method`, `extracted_at`
- [x] Use the existing structured field parser (regex heuristics) on the Markdown body to populate vendor/amount/date/ABN fields
- [x] Ollama instruct model can also be used to extract structured fields from the Markdown

### 11.8 — Write Sidecar .md File
- [x] After conversion, write `{saved_path}.md` alongside the original document
- [x] Store the full Markdown (without frontmatter) in `documents.extracted_text` for FTS5 indexing
- [x] Store the frontmatter YAML fields in the existing structured columns (`extracted_date`, `extracted_amount`, `extracted_vendor`, `extracted_abn`)
- [x] Update `documents.extraction_method` with the converter used

### 11.9 — Heading-Aware Chunker for Embeddings
- [x] Refactor `Embeddings.chunkText` to use **heading-aware splitting**:
  - Primary split: on `## ` boundaries (each section = one chunk)
  - If a section exceeds 1000 chars: fall back to the existing 500-char overlap splitter within that section
  - If no headings found: fall back entirely to the current char-based splitter
- [x] Each chunk inherits the section heading as context prefix: `"## Line Items\n| Description | ..."` → better embedding quality

### 11.10 — Supported File Types Configuration
- [x] Update `config.yaml` to support configurable file type list:
  ```yaml
  supported_types:
    - .pdf
    - .docx
    - .xlsx
    - .csv
    - .png
    - .jpg
    - .jpeg
    - .tiff
  ```
- [x] Default: all of the above
- [x] `min_attachment_size` filter applies to all types
- [x] Email sync and folder watcher respect this list

### 11.11 — MCP `hermes_read_file` Enhancement
- [x] `hermes_read_file` should return the `.md` sidecar content if it exists (structured Markdown)
- [x] Fall back to `extracted_text` from DB if no sidecar file
- [x] Add a `format` parameter: `"markdown"` (default, returns .md sidecar) or `"raw"` (returns extracted_text from DB)

---

## NuGet Packages

| Package | Purpose |
|---------|---------|
| `DocumentFormat.OpenXml` | Word and Excel extraction |
| Already have: `UglyToad.PdfPig` | PDF text |
| Already have: Ollama REST client | Vision OCR |

---

## Acceptance Criteria

- [x] A PDF document → `.pdf.md` sidecar with structured Markdown + YAML frontmatter
- [x] A scanned PDF → Ollama converts to Markdown (structure preserved)
- [x] A `.docx` → headings, paragraphs, tables, bold/italic all preserved in Markdown
- [x] A `.xlsx` → each sheet as a Markdown table with `## Sheet:` heading
- [x] A `.csv` → single Markdown table
- [x] YAML frontmatter contains source metadata + extracted fields
- [x] FTS5 search still works (extracted_text column populated with Markdown body)
- [x] Semantic search uses heading-aware chunks (better relevance for structured docs)
- [x] `hermes_read_file` returns `.md` sidecar content
- [x] `hermes search "plumber invoice table"` finds results from table content
- [x] Existing PDF pipeline still works (this is a refactor, not a rewrite)
- [x] Unsupported file types are logged and skipped gracefully
