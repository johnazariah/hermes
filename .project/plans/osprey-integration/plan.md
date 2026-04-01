---
description: "Implementation plan for Doc 16: Osprey Integration — making Osprey the first platform consumer. Seven phases (I1–I7), each a silver-thread vertical slice."
design-doc: ".project/design/16-osprey-integration.md"
depends-on:
  - "Doc 13 F1+F2 / Doc 14 M1 — hermes_list_documents + hermes_get_document_content must exist"
  - "Doc 17 P7 (Pipeline integration) — structured markdown for PDF parsers"
  - "Doc 17 P11 (CSV extraction) — raw CSV content for bank statement parsers"
---

# Hermes — Implementation Plan: Osprey Integration

## Prerequisites

**Hermes-side** (must be complete before I3+):
```
dotnet build hermes.slnx --nologo
dotnet test tests/Hermes.Tests/Hermes.Tests.fsproj --nologo
```
- Doc 13 F1+F2 (Document Feed MCP tools) — ✅ or ready
- Hermes running with email sync + folder watching — ✅ Done

**Osprey-side**:
- Osprey repository accessible
- Python 3.12+ environment
- Pelican Core running (for tax event posting)

**Critical rules**:
- **F# code** (Hermes) must go through `@fsharp-dev` agent
- **Python code** (Osprey) must go through `@python-dev` agent
- **Every phase has a PROOF** — do not mark complete until the proof passes

## Dependency Map

```
I1: Hermes list_documents + get_document_content    = Doc 13 F1+F2 (NOT duplicated)
I2: Hermes structured PDF markdown                   = Doc 17 P7 (NOT duplicated)
 └─ I3: Osprey HermesMcpClient + CursorStore          (Osprey-side — Python)
     └─ I4: Osprey TaxProcessor poll loop              (Osprey-side — Python)
         └─ I5: Osprey parser adaptation               (Osprey-side — Python)
             └─ I6: Osprey collector deletion          (Osprey-side — cleanup)
                 └─ I7: End-to-end integration test    (Both sides)
```

I1 and I2 are Hermes-side work covered by other plans. I3–I7 are Osprey-side. I3→I4→I5→I6→I7 are sequential.

---

## Phase I1: Hermes Feed MCP Tools (= Doc 13 F1+F2)

**See [Document Feed plan](../document-feed/plan.md) phases F1 and F2.**

This phase is NOT duplicated. Mark I1 complete when Doc 13 F1+F2 are complete.

---

## Phase I2: Hermes Structured PDF Extraction (= Doc 17 P7)

**See [Document Extraction plan](../document-extraction/plan.md) phase P7.**

This phase is NOT duplicated. Mark I2 complete when Doc 17 P7 is complete.

**Degraded mode**: If P7 is not complete, Osprey can still work with flat extracted text. PDF parsers will have lower accuracy (no table structure) but can still regex-match amounts, dates, and keywords. CSV parsers work regardless (they use `format="raw"`).

---

## Phase I3: Osprey HermesMcpClient + CursorStore

**Silver thread**: Python client connects to Hermes MCP → calls `hermes_list_documents` → receives JSON document list → cursor stored in local SQLite → next call returns only new documents.

### What to build

**File: `osprey/hermes_client.py`** (new — in Osprey repo)

```python
class HermesMcpClient:
    """Thin MCP client using JSON-RPC over HTTP."""
    
    def __init__(self, url: str = "http://localhost:21740/mcp"):
        ...
    
    def list_documents(self, since_id: int = 0, state: str = "extracted",
                       categories: list[str] | None = None, limit: int = 100) -> list[dict]:
        ...
    
    def get_document_content(self, document_id: int, format: str = "markdown") -> str:
        ...
    
    def get_feed_stats(self) -> dict:
        ...
```

**File: `osprey/cursor_store.py`** (new — in Osprey repo)

```python
class CursorStore:
    """SQLite-backed cursor storage for consumer position tracking."""
    
    def __init__(self, db_path: str):
        ...
    
    def load(self, consumer_id: str = "osprey-tax") -> int:
        ...
    
    def save(self, doc_id: int, consumer_id: str = "osprey-tax") -> None:
        ...
```

### What to test

- `test_hermes_client_list_documents_returns_list` — mock HTTP → parsed correctly
- `test_hermes_client_get_content_returns_string` — mock HTTP → content returned
- `test_cursor_store_load_default_zero` — empty DB → returns 0
- `test_cursor_store_save_and_load_roundtrip` — save 42 → load → returns 42
- `test_cursor_store_upsert_updates` — save 42 → save 100 → load → returns 100

### PROOF

Start Hermes MCP server → run Python test: `HermesMcpClient().list_documents(since_id=0, limit=5)` → returns 5 documents → `HermesMcpClient().get_document_content(docs[0]["id"], format="markdown")` → returns content string. Save cursor to CursorStore → restart script → cursor loads correctly → next poll returns only new documents.

### Commit

```
feat(osprey): HermesMcpClient and CursorStore for Hermes feed consumption
```

---

## Phase I4: Osprey TaxProcessor Poll Loop

**Silver thread**: TaxProcessor starts → loads cursor → polls Hermes for new extracted documents in tax-relevant categories → for each document, finds best parser → extracts tax events → posts to Pelican Core → saves cursor → sleeps → repeats.

### What to build

**File: `osprey/tax_processor.py`** (new — in Osprey repo)

```python
class TaxProcessor:
    """Polls Hermes for new documents, runs tax parsers, posts events to Pelican Core."""
    
    def __init__(self, hermes_url: str, core_url: str, cursor_db: str):
        self._hermes = HermesMcpClient(hermes_url)
        self._core = httpx.Client(base_url=core_url)
        self._cursor = CursorStore(cursor_db)
        self._registry = PluginRegistry()  # existing parser registry
    
    def run_cycle(self) -> int:
        """Process one batch. Returns number of documents processed."""
        cursor = self._cursor.load()
        docs = self._hermes.list_documents(
            since_id=cursor, state="extracted",
            categories=["invoices", "payslips", "bank-statements", 
                        "receipts", "tax", "insurance", "donations"],
            limit=100
        )
        if not docs: return 0
        
        processed = 0
        for doc in docs:
            content = self._hermes.get_document_content(doc["id"], format=self._format_for(doc))
            match = self._registry.find_best_for_content(doc["original_name"], doc["category"], content, doc)
            if match is None: continue
            parser, confidence = match
            if confidence < 0.70: continue
            events = parser.extract_from_content(content, doc)
            for event in events:
                self._post_event(event)
            processed += 1
        
        self._cursor.save(max(d["id"] for d in docs))
        return processed
    
    def run_forever(self, interval_seconds: int = 30):
        """Continuous poll loop."""
        ...
    
    def _format_for(self, doc: dict) -> str:
        """Return 'raw' for CSVs, 'markdown' for everything else."""
        ext = Path(doc["original_name"]).suffix.lower()
        return "raw" if ext == ".csv" else "markdown"
```

### What to test

- `test_tax_processor_run_cycle_processes_documents` — mock Hermes, mock Core → events posted
- `test_tax_processor_run_cycle_skips_irrelevant_categories`
- `test_tax_processor_run_cycle_skips_low_confidence`
- `test_tax_processor_run_cycle_saves_cursor`
- `test_tax_processor_run_cycle_empty_returns_zero`
- `test_tax_processor_format_csv_returns_raw`
- `test_tax_processor_format_pdf_returns_markdown`

### PROOF

Mock Hermes with 3 test documents (payslip, invoice, CSV bank statement) → run one cycle → PayslipParser processes payslip → InvoiceParser skips (no parser matches) → BankStatementParser processes CSV → 2 events posted to mock Core → cursor advances to max(id).

### Commit

```
feat(osprey): TaxProcessor poll loop with parser dispatch and cursor management
```

---

## Phase I5: Osprey Parser Adaptation (File-Based → Content-Based)

**Silver thread**: Existing parsers (PayslipParser, BankStatementParser, etc.) adapted from `extract(file_path)` to `extract_from_content(content, metadata)` → same tax events produced → same data in Pelican Core → dashboard unchanged.

### What to build

**Adapt the `ParserPlugin` protocol**:

```python
# Before
class ParserPlugin(Protocol):
    def can_handle(self, file_path: Path, metadata: dict) -> float: ...
    def extract(self, file_path: Path) -> list[TaxEvent]: ...

# After
class ParserPlugin(Protocol):
    def can_handle(self, filename: str, category: str, content: str, metadata: dict) -> float: ...
    def extract_from_content(self, content: str, metadata: dict) -> list[TaxEvent]: ...
```

**Adapt each parser**:

| Parser | Input change | Logic change |
|--------|-------------|--------------|
| PayslipParser | file_path → markdown | None — regex on text |
| BankStatementParser | CSV file → raw CSV string | `io.StringIO(content)` instead of `open(path)` |
| RentalStatementParser | PDF → markdown | None — regex on text |
| DividendParser | CSV → raw CSV string | `io.StringIO(content)` |
| AmazonParser | CSV → raw CSV string | `io.StringIO(content)` |
| ReceiptParser | PDF → markdown | None — regex on text |
| CreditCardParser | CSV → raw CSV string | `io.StringIO(content)` |

### What to test

**Per parser** (using existing test fixtures but as string content instead of file paths):

- `test_payslip_parser_from_content_extracts_events`
- `test_bank_statement_parser_from_content_extracts_transactions`
- `test_rental_parser_from_content_extracts_rental_events`
- `test_receipt_parser_from_content_extracts_purchase`
- For each CSV parser: verify `io.StringIO` produces same results as file read

**Regression**: Run existing parser test suite → all pass (backward compat maintained with adapter if needed)

### PROOF

For each parser, take a known test document → get its content from Hermes via MCP → run `extract_from_content(content, metadata)` → compare output to previous `extract(file_path)` output → identical tax events.

### Commit

```
feat(osprey): adapt all 7 tax parsers from file-based to content-based extraction
```

---

## Phase I6: Osprey Collector Deletion + Aspire Host Update

**Silver thread**: Remove the old collector code → Osprey no longer watches `~/osprey/inbox/` → all document ingestion goes through Hermes → Aspire host updated to start TaxProcessor instead of Collector.

### What to build

**Delete** (in Osprey repo):
- `collector/watcher.py` — replaced by Hermes folder watcher
- `collector/pipeline.py` — replaced by Hermes classify → extract pipeline
- `collector/store.py` — replaced by Hermes SQLite
- `collector/models.py` (Document, DocStatus only) — replaced by Hermes domain
- Keep: `collector/models.py` (TaxEvent, EventType) — Osprey's domain output
- Keep: `collector/extractors/*` — adapted parsers (from I5)

**Update** Aspire host / service configuration:
- Remove Collector service registration
- Add TaxProcessor service (continuous poll loop)
- Update environment variables / config for Hermes MCP URL

**Update** `~/osprey/inbox/` documentation:
- Users should now drop files in `~/Documents/Hermes/unclassified/` or rely on email sync
- Optionally: add Hermes folder watcher on `~/osprey/inbox/` for backward compat

### What to test

- `test_osprey_starts_without_collector` — service host starts, TaxProcessor runs
- Verify no import references to deleted modules remain
- `test_osprey_no_inbox_watcher` — no filesystem watcher on `~/osprey/inbox/`

### PROOF

Start Osprey service → no process watching `~/osprey/inbox/` → TaxProcessor logs: "Polling Hermes (cursor: 12487)..." → drop a file in `~/Documents/Hermes/unclassified/` → Hermes processes it → Osprey picks it up on next poll cycle → tax event posted to Pelican Core.

### Commit

```
refactor(osprey): remove collector, wire TaxProcessor as primary document source
```

---

## Phase I7: End-to-End Integration Test

**Silver thread**: Email with payslip arrives → Hermes syncs → classifies → extracts structured markdown → Osprey polls → PayslipParser runs → TaxEvent posted to Pelican Core → dashboard shows updated salary income. The complete silver thread from email to dashboard.

### What to build

**File: `tests/integration/test_hermes_to_osprey.py`** (new — in Osprey repo)

Integration test that exercises the full pipeline:

```python
def test_end_to_end_payslip():
    """Email → Hermes → Osprey → Pelican Core"""
    # 1. Drop test payslip PDF into Hermes unclassified/
    # 2. Wait for Hermes sync cycle (poll until document appears in feed)
    # 3. Verify document is classified as "payslips" with extracted markdown
    # 4. Run TaxProcessor.run_cycle()
    # 5. Verify TaxEvent posted to Pelican Core (mock or real)
    # 6. Verify cursor advanced

def test_end_to_end_bank_statement_csv():
    """CSV download → Hermes → Osprey → Pelican Core"""
    # 1. Drop test CSV into Hermes unclassified/
    # 2. Wait for Hermes sync
    # 3. Verify format="raw" returns original CSV
    # 4. Run TaxProcessor.run_cycle()
    # 5. Verify bank transactions posted

def test_end_to_end_replay():
    """Cursor reset → reprocess all documents"""
    # 1. Process 10 documents, cursor at 10
    # 2. Reset cursor to 0
    # 3. Run cycle → processes all 10 again
    # 4. Verify idempotent — no duplicate events in Core
```

### What to test

- Happy path: payslip PDF → salary tax event
- Happy path: bank statement CSV → transaction events
- Happy path: rental statement PDF → rental income event
- Edge case: irrelevant document (e.g. personal photo) → skipped by all parsers, cursor still advances
- Edge case: Hermes down → TaxProcessor logs error, retries next interval
- Edge case: replay (cursor reset) → events are idempotent

### PROOF

**Full demo**:
1. Hermes is running with email sync
2. Send a test email with payslip attachment (or drop payslip.pdf in `unclassified/`)
3. Wait ~30 seconds
4. Hermes activity log shows: "Synced 1 email" → "Classified payslip.pdf → payslips" → "Extracted payslip.pdf (3 tables, 8 KV pairs)"
5. Osprey TaxProcessor log shows: "Polled Hermes: 1 new document"
6. Osprey log: "PayslipParser matched payslip.pdf (0.92) → extracted 1 SALARY event"
7. Pelican Core log: "Posted journal #J-2026-0143: SALARY $2,732.60"
8. Tax dashboard shows updated YTD salary income

### Commit

```
test(integration): end-to-end Hermes → Osprey → Pelican Core pipeline tests
```

---

## Silver Thread Integrity Check

| Phase | Input | Processing | Backend | Presentation | Output |
|-------|-------|------------|---------|-------------|--------|
| I1 | MCP call | SQL feed query | Hermes DocumentFeed.fs | JSON array | Consumer receives documents |
| I2 | PDF bytes | PdfStructure pipeline | Hermes Extraction.fs | Structured markdown | Markdown in DB |
| I3 | Python call | HTTP JSON-RPC | HermesMcpClient | Dict/string | Client has documents + content |
| I4 | Poll cycle | Parser dispatch | TaxProcessor | Tax events | Events posted to Core |
| I5 | Content string | Regex/CSV parsing | Adapted parsers | TaxEvent list | Same events as file-based |
| I6 | Service start | No collector | Updated host | TaxProcessor runs | Clean architecture |
| I7 | File drop / email | Full pipeline | Hermes + Osprey + Core | Dashboard data | User sees tax data |

**Silver thread assessment**: ✅ Complete. The full chain from "email arrives" to "dashboard shows data" is wired through at every phase. No orphaned code — I3 enables I4 enables I5. I6 is cleanup. I7 proves it all works together.

---

## Silver Thread Flag: Cross-Repository Dependency

⚠ **I3–I7 are Osprey-side work.** This plan assumes the Osprey repository is accessible and modifiable. The Hermes-side work (I1, I2) is covered by other plans.

**Coordination required**:
- I1 (Hermes MCP tools) must be deployed/running before I3 can test against real data
- I2 (structured markdown) improves parser accuracy but isn't strictly required (parsers can work on flat text)
- JSON-RPC contract between Hermes and Osprey must be stable before I5 parser adaptation

**Minimum viable**: I1 + I3 + I4 + I5 (with flat text) → working consumer. I2 upgrades quality later.

---

## Flags & Risks

| Risk | Impact | Mitigation |
|------|--------|------------|
| Hermes MCP not running during Osprey dev | Can't test client | Mock HTTP responses. Test against real Hermes in I7. |
| Parser accuracy drops with markdown vs file | Tax events wrong or missing | Compare output against known test fixtures. Accept small accuracy delta. |
| CSV parsers need raw content, not markdown | Parse failure | `format="raw"` returns original CSV. Verified in I3 tests. |
| Cross-repo coordination | Merge conflicts, stale contracts | Pin Hermes MCP tool schemas. Version the consumer protocol. |
| Pelican Core API changes | Event posting fails | Osprey-side risk — not Hermes-related. Test against Core contract. |
| Collector deletion breaks something | Data path lost | Delete only after I7 proves end-to-end works. Keep collector on a branch for rollback. |
