# Phase 3: Text Extraction

**Status**: Not Started  
**Depends On**: Phase 2 (Classification Pipeline)  
**Deliverable**: Classified documents automatically get text extracted and structured fields parsed.

---

## Objective

Extract text from PDFs and images, parse structured fields (date, amount, vendor, ABN), and update the database. Uses PdfPig for native PDFs, Ollama vision for scanned documents, and Azure Document Intelligence as a cloud fallback.

---

## Tasks

### 3.1 — Extraction Pipeline Task
- [ ] Long-running `BackgroundService` task reading from `Channel<DocumentId>`
- [ ] Picks up documents posted by the classifier (Phase 2)
- [ ] Also supports polling: query DB for `extracted_at IS NULL` on startup (catch up on backlog)
- [ ] Process one document at a time (serialised to limit GPU/CPU contention)
- [ ] On success: update `documents` row with extracted fields, post to embed channel (Phase 5)
- [ ] On failure: log error, leave `extracted_at = NULL`, move to next document

### 3.2 — Native PDF Text Extraction
- [ ] Use **PdfPig** (`UglyToad.PdfPig` NuGet) to extract text from PDFs with embedded text
- [ ] Detect "scanned PDF" (image-only): if PdfPig returns <50 chars for a multi-page PDF, flag for OCR
- [ ] Set `extraction_method = "pdfpig"` on the documents row

### 3.3 — Ollama Vision OCR
- [ ] For scanned/image PDFs: convert page to image, send to Ollama vision model (`llava`)
- [ ] HTTP POST to `http://localhost:11434/api/generate` with base64 image
- [ ] Prompt: "Extract all text from this document image. Return only the text, preserving layout."
- [ ] Set `extraction_method = "ollama_vision"`
- [ ] Graceful degradation: if Ollama unavailable, skip and leave `extracted_at = NULL`
- [ ] For standalone images (JPEG, PNG, TIFF): same OCR pipeline

### 3.4 — Azure Document Intelligence Fallback
- [ ] If Ollama is unavailable and Azure credentials are configured:
- [ ] Use Azure AI Document Intelligence REST API (`prebuilt-read` model)
- [ ] Configurable endpoint and key in `config.yaml`
- [ ] Set `extraction_method = "azure_document_intelligence"`
- [ ] Respect rate limits and pricing awareness (log cost estimate per call)

### 3.5 — Structured Field Parsing (Heuristics)
- [ ] After text extraction, apply regex heuristics to parse common fields:

#### Date Extraction
```
Patterns: "dd/MM/yyyy", "dd-MM-yyyy", "yyyy-MM-dd", "d MMM yyyy", "MMMM d, yyyy"
Pick the date closest to the email date if multiple found.
Store in extracted_date.
```

#### Amount Extraction
```
Patterns: "$X,XXX.XX", "AUD X,XXX.XX", "Total: $X.XX", "Amount Due: $X.XX"
Pick the largest amount if multiple found (likely the total).
Store in extracted_amount.
```

#### Vendor Extraction
```
Heuristic: first line of text, or text near ABN/ACN, or sender display name.
Store in extracted_vendor.
```

#### ABN/ACN Extraction
```
Pattern: "ABN[:\s]*\d{2}\s?\d{3}\s?\d{3}\s?\d{3}" (11 digits)
Pattern: "ACN[:\s]*\d{3}\s?\d{3}\s?\d{3}" (9 digits)
Store in extracted_abn.
```

### 3.6 — Ollama Instruct for Complex Extraction (Optional)
- [ ] For documents where regex heuristics fail or return low confidence:
- [ ] Send extracted text to Ollama instruct model (`llama3.2:3b`)
- [ ] Prompt: structured extraction template requesting date, amount, vendor, ABN as JSON
- [ ] Parse the JSON response and populate fields
- [ ] Only used as a fallback — regex heuristics first
- [ ] Configurable: can be disabled in config

### 3.7 — OCR Confidence
- [ ] For documents processed by OCR (Ollama vision or Azure), estimate confidence:
  - PdfPig (native text): `ocr_confidence = NULL` (not applicable)
  - Ollama vision: `ocr_confidence` based on response quality heuristics (e.g. ratio of readable words)
  - Azure Document Intelligence: use the confidence score from the API response
- [ ] Store in `documents.ocr_confidence` (0.0–1.0)

### 3.8 — CLI Command
- [ ] `hermes extract` — process backlog of unextracted documents
- [ ] `hermes extract --category invoices` — only extract files in a specific category
- [ ] `hermes extract --force` — re-extract even if already extracted
- [ ] `hermes extract --limit 50` — process at most N documents (for incremental runs)
- [ ] Progress output: `[12/50] Extracting invoices/2025-03-15_bobplumbing_Invoice-2025-001.pdf...`

---

## NuGet Packages

| Package | Purpose |
|---------|---------|
| `UglyToad.PdfPig` | Native PDF text extraction |
| `System.Net.Http.Json` | Ollama REST API calls |
| (Azure SDK or raw HTTP) | Azure Document Intelligence |

---

## Acceptance Criteria

- [ ] A PDF with embedded text → PdfPig extracts text, fields parsed via regex, row updated
- [ ] A scanned PDF (image-only) → Ollama vision extracts text, fields parsed, row updated
- [ ] If Ollama unavailable and Azure configured → Azure Doc Intelligence extracts text
- [ ] If neither Ollama nor Azure available → extraction skipped, document still classified and searchable by metadata
- [ ] Date, amount, vendor, ABN extracted correctly for standard invoices and statements
- [ ] `hermes extract` processes the backlog and shows progress
- [ ] `hermes extract --force` re-extracts previously extracted documents
- [ ] Extraction failures don't block the pipeline — failed documents are logged and skipped
