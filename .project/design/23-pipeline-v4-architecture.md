# 23 — Pipeline v4 Architecture

> Supersedes: 03-architecture.md, 20-pipeline-v2-endstate.md, 21-v2-implementation-plan.md, wave-v2b-pipeline-fixes.md

## What Hermes Is

A local-first document intelligence service. It ingests documents from email and local folders, understands them through LLM comprehension, and exposes structured knowledge via MCP server and web UI.

Hermes is the document backbone for downstream consumers like Osprey (tax computation). It does the heavy lifting — ingestion, deduplication, text extraction, comprehension, embedding — so consumers can query for structured data without managing documents themselves.

## Pipeline

```
Producers              Channels              Consumers
─────────           ─────────────           ──────────
Email Sync ─┐       ┌─────────┐
(N accounts) ├──→   │ Extract │ ──→ Extract ──→ ┐
Folder Watch ┘      │ Channel │                  │
                    └─────────┘                  ▼
                                            ┌─────────────┐
                                            │ Comprehend  │ ──→ ┐
                                            │   Channel   │      │
                                            └─────────────┘      ▼
                                                            ┌─────────┐
                                                            │  Embed  │
                                                            │ Channel │
                                                            └─────────┘
```

Four stages:

| Stage | Input | Output | Resource |
|-------|-------|--------|----------|
| **Ingest** | Email/file | Document record + file on disk | CPU, network |
| **Extract** | Raw file | Extracted text (PdfPig, OCR) | CPU |
| **Comprehend** | Extracted text | Structured JSON + document_type | Ollama (GPU) |
| **Embed** | Comprehension text | Vector embeddings for search | Ollama (GPU) |

**There is no separate classify stage.** Comprehension subsumes classification — understanding a document produces its type, fields, and metadata as byproducts. Category is not a filing decision; it emerges from understanding.

## Core Primitives

### Document = Map<string, obj>

The document is a property bag. Keys are column names. Values are typed at access time via `decode<'T>`. Stages add keys; they never remove them.

```fsharp
module Document =
    type T = Map<string, obj>
    let decode<'T> (key: string) (doc: T) : 'T option
    let encode (key: string) (value: obj) (doc: T) : T
    let persist (db: Database) (doc: T) : Task<unit>
    let hydrate (db: Database) : Task<T list>
```

Why a property bag instead of a typed record:
- SQL boundary already strips types — the fiction of compile-time safety is dishonest
- Idempotency check (`doc.ContainsKey outputKey`) is trivial — no per-stage `isDone` function
- Adding a stage = adding keys, not changing types
- The DB row IS the property bag — `fromRow` is identity, `toParams` is a fold

### Channel<Document>

Runtime flow between stages. Backpressure, multiple consumers, efficient blocking. No polling, no SQLite locking.

Hydration on startup: `SELECT * FROM documents WHERE stage NOT IN ('embedded', 'failed')` → seed into extract channel. Consumers are idempotent — they check their output key and pass through if already done. Pipeline self-sorts documents to the correct stage.

### Workflow.runStage

Generic monad that handles all boilerplate:

```fsharp
type StageDefinition = {
    Name: string
    OutputKey: string           // presence = stage is done
    RequiredKeys: string list   // must be present before processing
    Process: Document.T -> Task<Document.T>  // pure function
    ResourceLock: SemaphoreSlim option       // GPU mutex
    MaxHoldTime: TimeSpan                    // burst duration
}
```

The `runStage` loop:
1. Read document from input channel
2. If `OutputKey` exists → pass through to next channel (idempotent)
3. If any `RequiredKey` missing → pass through (can't process yet)
4. Acquire `ResourceLock` if configured (start of burst)
5. Process document (pure function)
6. Write-aside to DB (`Document.persist`)
7. Forward to next channel
8. If channel empty or `MaxHoldTime` exceeded → release lock, restart loop
9. On exception → set `stage = "failed"`, persist, log

Stage processors are pure functions: `Document -> Task<Document>`. They receive a document, do their work, return the enriched document. No channels, no DB, no error handling in the processor.

### GPU Resource Lock (CS-bar)

Classify (comprehend) and embed both use Ollama. On 8GB VRAM, only one model fits. A shared `SemaphoreSlim(1, 1)` provides mutual exclusion:

- Consumer acquires the semaphore before processing
- Holds it for the entire burst (not per-document)
- Releases when channel is empty or `MaxHoldTime` elapses
- Self-organizing: no orchestrator, no ticker — demand-driven

Config-driven: `ollama.shared_gpu = true` creates the lock; `false` (e.g. Azure OpenAI for one stage) means no contention.

## Data Model

### Documents table

```sql
CREATE TABLE documents (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    stage           TEXT NOT NULL DEFAULT 'received',
    source_type     TEXT NOT NULL,
    -- source identity
    gmail_id TEXT, thread_id TEXT, account TEXT,
    sender TEXT, subject TEXT, email_date TEXT,
    -- file identity
    original_name TEXT, saved_path TEXT NOT NULL,
    category TEXT NOT NULL, mime_type TEXT,
    size_bytes INTEGER, sha256 TEXT NOT NULL,
    source_path TEXT,
    -- extraction output
    extracted_text TEXT, extracted_markdown TEXT,
    extraction_method TEXT, extracted_at TEXT,
    -- comprehension output (future)
    comprehension TEXT,          -- structured JSON
    comprehension_schema TEXT,   -- schema version
    -- classification (byproduct of comprehension)
    classification_tier TEXT, classification_confidence REAL,
    -- embedding output
    embedded_at TEXT, chunk_count INTEGER,
    -- metadata
    starred INTEGER DEFAULT 0,
    ingested_at TEXT NOT NULL
);
```

The `stage` column tracks pipeline state: `received → extracted → comprehended → embedded` (or `failed`).

**Files never move.** `saved_path` is immutable after ingest. All files live in `unclassified/`. Category is metadata in the `category` column, not a filesystem location.

### Write-aside pattern

Every stage writes the full document row to DB before forwarding to the next channel. If the process crashes:
1. Restart
2. Hydration query finds documents not yet `embedded`
3. Seed into extract channel
4. Idempotent consumers pass through completed stages
5. Resume from where each document left off

The DB always reflects the last known good state.

## Producers

### Email sync
- One perpetual task per configured Gmail account
- Channel-based: enumerate message IDs → N concurrent consumers → download + save + ingest
- Thread dedup: latest email body per thread wins
- Watermark-based: `sync_state` table tracks last sync timestamp per account
- `--initial-sync-days N` CLI arg limits first sync for testing

### Folder watcher
- Polls configured watch folders every 30 seconds
- Copies files to `unclassified/` with standardized names
- SHA256 deduplication against documents table
- Creates document record with `stage = 'received'`

Both producers write to the shared extract channel.

## Web UI

Five-page React 19 app served from the service process:

| Page | Purpose |
|------|---------|
| **Pipeline** | Live stage counts, document list by stage, enrichment journey, activity feed |
| **Documents** | Browse by category, split view (original file + extracted markdown) |
| **Search** | Keyword + semantic search |
| **Chat** | Conversational Q&A over documents |
| **Settings** | Email accounts, watch folders, YAML config editor |

Stack: React 19, Vite 8, Tailwind CSS 4, TanStack React Query 5, React Router, react-markdown.

## MCP Server

Streamable HTTP on `localhost:21741` (prod) / `21742` (dev). Stdio shim for compatibility.

Key tools for consumers (like Osprey):
- `hermes_search` — keyword + semantic search
- `hermes_list_documents` — filter by category, stage, date
- `hermes_get_document` — full document with comprehension data
- `hermes_get_document_content` — extracted/comprehended content
- File serving via HTTP for raw file access

## Osprey Integration

Osprey (tax-database) does Australian household tax computation. Integration path:

1. **Hermes ingests** all documents from email + watched folders
2. **Hermes comprehends** — extracts structured JSON (gross pay, transactions, expenses)
3. **Osprey queries Hermes** via MCP: "give me all payslips from FY2025 with comprehension data"
4. **Osprey computes taxes** from the structured data — no parsing on Osprey's side

This means Hermes' comprehension stage IS the critical path. Without it, Osprey has to maintain its own parsers — which defeats the purpose of Hermes.

## Technology

| Component | Choice |
|-----------|--------|
| Runtime | .NET 10, F# |
| Database | SQLite + FTS5 + sqlite-vec (WAL mode) |
| Email | Google.Apis.Gmail.v1 |
| PDF extraction | PdfPig (UglyToad.PdfPig) |
| LLM (comprehension) | Ollama llama3:8b (local) |
| Embeddings | Ollama nomic-embed-text (local) |
| Web UI | React 19 + Vite + Tailwind |
| Testing | xUnit + FsCheck (F#), Playwright (UI) |
