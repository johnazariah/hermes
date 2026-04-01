# Hermes — Osprey Integration: First Platform Consumer

> Design doc for making Osprey (tax-database) the first consumer of the Hermes document platform.  
> Builds on: doc 13 (Document Feed), doc 14 (MCP Platform API).  
> Created: 2026-04-01

---

## 1. Context

Osprey is a personal Australian tax preparation system. It currently has its own Python collector that watches `~/osprey/inbox/`, classifies documents with confidence-scored parsers, extracts tax events, and POSTs them to Pelican Core (its F# GL engine).

Hermes replaces the collector. Osprey becomes a pure **tax domain processor** that consumes Hermes's document feed via MCP.

---

## 2. What Gets Removed from Osprey

| Component | Status | Replaced by |
|-----------|--------|-------------|
| `collector/watcher.py` | DELETE | Hermes folder watcher + email sync |
| `collector/pipeline.py` | DELETE | Hermes classify → extract pipeline |
| `collector/store.py` | DELETE | Hermes SQLite (`documents` table) |
| `collector/models.py` (Document, DocStatus) | DELETE | Hermes `Domain.Document` via MCP |
| `collector/models.py` (TaxEvent, EventType) | KEEP | Osprey's domain output |
| `collector/parser_protocol.py` | KEEP (adapt) | Parsers consume MCP content instead of file paths |
| `collector/extractors/*` | KEEP (adapt) | Same logic, different input source |

**What stays**: The 7 tax parsers, TaxEvent model, Pelican Core posting, tax calculation engine, Excel export, dashboard. All domain logic stays.

---

## 3. New Architecture

### Before (Osprey standalone)

```
~/osprey/inbox/
    ↓
Osprey Collector (Python)
  watcher → dedup → classify → extract → POST to Pelican Core
    ↓
Pelican Core (F# GL)
    ↓
Tax Dashboard (Blazor)
```

### After (Hermes as platform)

```
Email (Gmail) + ~/Downloads + manual drops
    ↓
Hermes (ingest, classify, extract, index, embed)
    ↓ MCP: hermes_list_documents + hermes_get_document_content
    ↓
Osprey Tax Processor (Python)
  poll feed → filter relevant → parse → POST to Pelican Core
    ↓
Pelican Core (F# GL)
    ↓
Tax Dashboard (Blazor)
```

### What changes for the user

**Before**: Drop a file into `~/osprey/inbox/`. Hope it gets classified.

**After**: Documents arrive automatically from email. Or drop files into `~/Downloads` (watched by Hermes). Or drop into `~/Documents/Hermes/unclassified/`. Everything is indexed, searchable, and visible in the Hermes UI. Osprey silently processes relevant documents in the background.

---

## 4. Osprey Tax Processor

A new lightweight service that replaces the collector. Minimal code — just a poll loop + parser dispatch.

### Architecture

```python
class TaxProcessor:
    """Polls Hermes for new documents, runs tax parsers, posts events."""
    
    def __init__(self, hermes_url: str, core_url: str, cursor_db: str):
        self._hermes = HermesMcpClient(hermes_url)
        self._core = httpx.Client(base_url=core_url)
        self._cursor = CursorStore(cursor_db)
        self._registry = PluginRegistry()  # existing parser registry
    
    def run_cycle(self):
        cursor = self._cursor.load()
        
        # 1. Poll Hermes for new extracted documents in tax-relevant categories
        docs = self._hermes.list_documents(
            since_id=cursor,
            state="extracted",
            categories=["invoices", "payslips", "bank-statements", 
                        "receipts", "tax", "insurance", "donations"],
            limit=100
        )
        
        if not docs:
            return
        
        for doc in docs:
            # 2. Get structured content
            content = self._hermes.get_document_content(
                document_id=doc["id"],
                format="markdown"  # or "raw" for CSVs
            )
            
            # 3. Find best parser
            match = self._registry.find_best_for_content(
                filename=doc["original_name"],
                category=doc["category"],
                content=content,
                metadata=doc
            )
            
            if match is None:
                continue  # Not a tax-relevant document
            
            parser, confidence = match
            
            if confidence < 0.70:
                # Log for manual review, don't auto-process
                logger.warning(f"Low confidence ({confidence:.0%}) for {doc['original_name']}")
                continue
            
            # 4. Extract tax events
            events = parser.extract_from_content(content, doc)
            
            # 5. POST to Pelican Core
            for event in events:
                self._post_event(event)
        
        # 6. Save cursor
        self._cursor.save(max(d["id"] for d in docs))
```

### Parser interface adaptation

**Before** (file-based):
```python
class ParserPlugin(Protocol):
    def can_handle(self, file_path: Path, metadata: dict) -> float: ...
    def extract(self, file_path: Path) -> list[TaxEvent]: ...
```

**After** (content-based):
```python
class ParserPlugin(Protocol):
    def can_handle(self, filename: str, category: str, content: str, metadata: dict) -> float: ...
    def extract_from_content(self, content: str, metadata: dict) -> list[TaxEvent]: ...
```

The parsers receive content (markdown or raw text) instead of file paths. Most parser logic stays the same — regex patterns on text, CSV column detection, keyword matching. The main change is the input source.

### Parser adaptations needed

| Parser | Input change | Logic change |
|--------|-------------|--------------|
| **PayslipParser** | file_path → markdown content | None — already does text pattern matching |
| **BankStatementParser** | CSV file_path → raw CSV content | Minor — read from string instead of file |
| **RentalStatementParser** | PDF file_path → markdown content | None — text pattern matching |
| **DividendParser** | CSV file_path → raw CSV content | Minor — read from string |
| **AmazonParser** | CSV file_path → raw CSV content | Minor — read from string |
| **ReceiptParser** | PDF file_path → markdown content | None — text/regex matching |
| **CreditCardParser** | CSV file_path → raw CSV content | Minor — read from string |

**Key insight**: Hermes returns markdown for PDFs and raw content for CSVs (via `format="raw"`). CSV parsers use `io.StringIO` instead of `open(file_path)`. PDF parsers get pre-extracted structured markdown instead of raw PDF bytes — they don't need PdfPig or any PDF library.

---

## 5. Hermes MCP Client (Python)

A thin MCP client for the tax processor:

```python
class HermesMcpClient:
    """Minimal MCP client using JSON-RPC over HTTP."""
    
    def __init__(self, url: str = "http://localhost:21740/mcp"):
        self._url = url
        self._client = httpx.Client(timeout=30.0)
        self._id = 0
    
    def _call(self, method: str, params: dict) -> dict:
        self._id += 1
        resp = self._client.post(self._url, json={
            "jsonrpc": "2.0",
            "id": self._id,
            "method": method,
            "params": params
        })
        result = resp.json()
        if "error" in result:
            raise RuntimeError(result["error"])
        return result["result"]
    
    def list_documents(self, since_id=0, state="extracted", 
                       categories=None, limit=100):
        return self._call("tools/call", {
            "name": "hermes_list_documents",
            "arguments": {
                "since_id": since_id,
                "state": state,
                "category": ",".join(categories) if categories else None,
                "limit": limit
            }
        })
    
    def get_document_content(self, document_id, format="markdown"):
        return self._call("tools/call", {
            "name": "hermes_get_document_content",
            "arguments": {
                "document_id": document_id,
                "format": format
            }
        })
```

---

## 6. Cursor Storage

Simple SQLite in Osprey's own data directory:

```python
class CursorStore:
    def __init__(self, db_path: str):
        self._conn = sqlite3.connect(db_path)
        self._conn.execute("""
            CREATE TABLE IF NOT EXISTS hermes_cursor (
                consumer_id TEXT PRIMARY KEY,
                last_doc_id INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL
            )
        """)
    
    def load(self, consumer_id="osprey-tax") -> int:
        row = self._conn.execute(
            "SELECT last_doc_id FROM hermes_cursor WHERE consumer_id = ?",
            (consumer_id,)
        ).fetchone()
        return row[0] if row else 0
    
    def save(self, doc_id: int, consumer_id="osprey-tax"):
        self._conn.execute("""
            INSERT INTO hermes_cursor (consumer_id, last_doc_id, updated_at)
            VALUES (?, ?, datetime('now'))
            ON CONFLICT(consumer_id) DO UPDATE SET 
                last_doc_id = excluded.last_doc_id,
                updated_at = excluded.updated_at
        """, (consumer_id, doc_id))
        self._conn.commit()
```

---

## 7. End-to-End Flow

### Happy path: payslip arrives via email

```
1. Gmail sends john-personal an email with payslip.pdf attached
2. Hermes email sync downloads payslip.pdf → unclassified/
3. Hermes classifier: sender "payroll@microsoft.com" → category "payslips"
4. Hermes extractor: PdfPig → structured markdown with salary table
5. Hermes assigns document id #12,489
6. Osprey TaxProcessor polls: hermes_list_documents(since_id=12488, state="extracted")
7. Gets document #12,489 — category "payslips", has extracted_amount
8. Calls hermes_get_document_content(12489, format="markdown")
9. PayslipParser.can_handle("payslip.pdf", "payslips", content, metadata) → 0.92
10. PayslipParser.extract_from_content(content, metadata) → [TaxEvent(SALARY, ...)]
11. POST to Pelican Core: /api/events/employment/salary → journal #J-2026-0142
12. Save cursor: 12,489
13. Dashboard shows updated YTD salary income
```

### Happy path: bank statement CSV from Downloads

```
1. User downloads Westpac statement CSV to ~/Downloads
2. Hermes folder watcher detects new CSV → copies to unclassified/
3. Hermes classifier: filename "WestpacStatementDownload.csv" → category "bank-statements"
4. Hermes extractor: reads CSV, stores as extracted text
5. Hermes assigns document id #12,490
6. Osprey TaxProcessor polls, gets #12,490
7. Calls hermes_get_document_content(12490, format="raw") → raw CSV text
8. BankStatementParser.can_handle("WestpacStatement.csv", "bank-statements", content) → 0.88
9. BankStatementParser.extract_from_content(csv_content) → [TaxEvent(TRANSFER, ...), ...]
10. POST each to Pelican Core
11. Save cursor: 12,490
```

---

## 8. Prerequisites

| Prerequisite | Source | Status |
|-------------|--------|--------|
| `hermes_list_documents` MCP tool | Doc 14, phase M1 | Not started |
| `hermes_get_document_content` MCP tool | Doc 14, phase M1 | Not started |
| Structured PDF → markdown extraction | Separate design doc (pending) | Not started |
| CSV raw content via MCP | Trivial — read file as text | Not started |
| Hermes running with email sync + folder watching | Already working | ✅ Done |

---

## 9. Implementation Phases

| Phase | What | Side | Effort |
|-------|------|------|--------|
| **I1** | Hermes: `hermes_list_documents` + `hermes_get_document_content` | Hermes | Medium |
| **I2** | Hermes: Structured PDF → markdown (separate doc) | Hermes | Large |
| **I3** | Osprey: `HermesMcpClient` + `CursorStore` | Osprey | Small |
| **I4** | Osprey: `TaxProcessor` poll loop | Osprey | Medium |
| **I5** | Osprey: Adapt parsers from file-based to content-based | Osprey | Medium |
| **I6** | Osprey: Delete collector, update Aspire host | Osprey | Small |
| **I7** | End-to-end test: email → Hermes → Osprey → Pelican Core → dashboard | Both | Medium |

I1 is the gate — nothing on the Osprey side can start until the MCP feed tools exist.

---

## 10. Open Questions

| # | Question | Leaning |
|---|----------|---------|
| 1 | Should Osprey run as a separate process or integrate into the Hermes service? | Separate — clean platform boundary. Osprey is a consumer, not part of Hermes. |
| 2 | Should the tax processor run continuously or on a schedule? | Continuous poll with 30-second interval — same as Hermes sync. |
| 3 | How should Osprey handle documents it can't parse? | Skip and advance cursor. The document stays in Hermes for manual inspection. |
| 4 | Should Osprey write results back to Hermes (e.g. "this document produced tax event X")? | Future — via a `hermes_annotate_document` MCP tool. Not needed for v1. |
| 5 | Should Osprey's dashboard link to Hermes for document viewing? | Yes — link to `hermes://documents/{id}` or just show the Hermes shell window. |
| 6 | What about CSV files that Hermes extracts as text but aren't structured markdown? | `format="raw"` returns the original CSV content. CSV parsers use this directly. |
| 7 | Should we keep Osprey's Python collector as a fallback? | No — clean cut. Hermes is the sole ingestion path. |
