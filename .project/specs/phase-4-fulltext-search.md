# Phase 4: Full-Text Search

**Status**: Not Started  
**Depends On**: Phase 3 (Text Extraction)  
**Deliverable**: `hermes search "CBA statement 2025"` returns results instantly.

---

## Objective

Enable keyword search across all indexed documents using SQLite FTS5. The FTS5 index is populated automatically via triggers (from Phase 0 schema), so this phase is about building the query layer and CLI.

---

## Tasks

### 4.1 — FTS5 Query Layer
- [x] Build a search function in `Hermes.Core` that queries `documents_fts`
- [x] Accept parameters: query string, category filter, sender filter, date range, account filter, source_type filter, limit
- [x] Use FTS5 `MATCH` syntax with `bm25()` ranking
- [x] Join back to `documents` table for full metadata
- [x] Return results as typed F# records:
  ```fsharp
  type SearchResult = {
      DocumentId: int
      SavedPath: string
      OriginalName: string
      Category: string
      Sender: string option
      Subject: string option
      EmailDate: DateTimeOffset option
      ExtractedVendor: string option
      ExtractedAmount: decimal option
      RelevanceScore: float
      Snippet: string option
  }
  ```

### 4.2 — Snippet Generation
- [x] Use FTS5 `snippet()` auxiliary function to generate highlighted excerpts
- [x] Show up to 64 tokens of context around matching terms
- [x] Strip excessive whitespace from snippets

### 4.3 — Query Parsing
- [x] Support natural query input: `"CBA statement 2025"` → FTS5 query
- [x] Handle quoted phrases: `"bank statement"` → exact phrase match
- [x] Handle implicit AND between terms
- [x] Handle prefix matching: `invoi*` matches "invoice", "invoices"
- [x] Validate and sanitise input to prevent FTS5 syntax errors

### 4.4 — CLI Command
- [x] `hermes search QUERY` — keyword search, output as formatted table
- [x] `hermes search QUERY --category invoices` — filter by category
- [x] `hermes search QUERY --from 2025-01-01 --to 2025-12-31` — date range filter
- [x] `hermes search QUERY --sender cba.com.au` — filter by sender
- [x] `hermes search QUERY --account john-personal` — filter by account
- [x] `--limit N` — cap results (default 20)
- [x] `--json` — output as JSON for piping/scripting

### 4.5 — Result Formatting
- [x] Table format (default):
  ```
  Score  Date        Category        Sender            Filename                              Amount
  ─────  ──────────  ──────────────  ────────────────  ────────────────────────────────────  ──────
  12.3   2025-03-15  invoices        bob@plumbing.com  Invoice-2025-001.pdf                  $385.00
  11.8   2025-01-05  bank-statements cba.com.au        Statement-Jan-2025.pdf                —
  ```
- [x] JSON format (`--json`):
  ```json
  [
    {
      "document_id": 42,
      "saved_path": "invoices/2025-03-15_bobplumbing_Invoice-2025-001.pdf",
      "category": "invoices",
      "sender": "bob@plumbing.com.au",
      "date": "2025-03-15",
      "extracted_amount": 385.00,
      "relevance_score": 12.3,
      "snippet": "...March 2025 <b>invoice</b> for plumbing work..."
    }
  ]
  ```

---

## Acceptance Criteria

- [x] `hermes search "CBA statement"` returns matching documents ranked by relevance
- [x] `hermes search "invoice" --category invoices --from 2025-01-01` correctly filters
- [x] Snippets show highlighted matching terms in context
- [x] Query with no matches returns empty result (not an error)
- [x] Invalid FTS5 syntax is caught and a helpful error message shown
- [x] Results display in <200ms for a database with 10,000 documents
- [x] `--json` output is valid JSON parseable by downstream tools
