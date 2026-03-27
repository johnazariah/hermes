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
- [ ] Build a search function in `Hermes.Core` that queries `documents_fts`
- [ ] Accept parameters: query string, category filter, sender filter, date range, account filter, source_type filter, limit
- [ ] Use FTS5 `MATCH` syntax with `bm25()` ranking
- [ ] Join back to `documents` table for full metadata
- [ ] Return results as typed F# records:
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
- [ ] Use FTS5 `snippet()` auxiliary function to generate highlighted excerpts
- [ ] Show up to 64 tokens of context around matching terms
- [ ] Strip excessive whitespace from snippets

### 4.3 — Query Parsing
- [ ] Support natural query input: `"CBA statement 2025"` → FTS5 query
- [ ] Handle quoted phrases: `"bank statement"` → exact phrase match
- [ ] Handle implicit AND between terms
- [ ] Handle prefix matching: `invoi*` matches "invoice", "invoices"
- [ ] Validate and sanitise input to prevent FTS5 syntax errors

### 4.4 — CLI Command
- [ ] `hermes search QUERY` — keyword search, output as formatted table
- [ ] `hermes search QUERY --category invoices` — filter by category
- [ ] `hermes search QUERY --from 2025-01-01 --to 2025-12-31` — date range filter
- [ ] `hermes search QUERY --sender cba.com.au` — filter by sender
- [ ] `hermes search QUERY --account john-personal` — filter by account
- [ ] `--limit N` — cap results (default 20)
- [ ] `--json` — output as JSON for piping/scripting

### 4.5 — Result Formatting
- [ ] Table format (default):
  ```
  Score  Date        Category        Sender            Filename                              Amount
  ─────  ──────────  ──────────────  ────────────────  ────────────────────────────────────  ──────
  12.3   2025-03-15  invoices        bob@plumbing.com  Invoice-2025-001.pdf                  $385.00
  11.8   2025-01-05  bank-statements cba.com.au        Statement-Jan-2025.pdf                —
  ```
- [ ] JSON format (`--json`):
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

- [ ] `hermes search "CBA statement"` returns matching documents ranked by relevance
- [ ] `hermes search "invoice" --category invoices --from 2025-01-01` correctly filters
- [ ] Snippets show highlighted matching terms in context
- [ ] Query with no matches returns empty result (not an error)
- [ ] Invalid FTS5 syntax is caught and a helpful error message shown
- [ ] Results display in <200ms for a database with 10,000 documents
- [ ] `--json` output is valid JSON parseable by downstream tools
