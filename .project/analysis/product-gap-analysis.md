# Hermes — Product Analysis & Gap Report

> Written April 13, 2026 after a multi-day development sprint.
> Purpose: honest assessment of where we are vs where we need to be.

## 1. What Hermes Is Supposed To Be

From [01-vision-and-goals.md](../design/01-vision-and-goals.md):

> A quiet, always-on, local-first document intelligence service. Install it once, forget it's there, never lose a document again.

**Target user**: Non-technical. "Mum-friendly." One-click install. System tray. No terminal.

**Core promise**: Every email and document that passes through your digital life is automatically ingested, classified, extracted, indexed, and searchable — including by AI agents via MCP.

## 2. What We Actually Built (Current State)

### Metrics (April 13, 2026)

| Metric | Value | Status |
|--------|-------|--------|
| Tests | 773 passing | ✅ |
| Documents in DB | 3,528 | ⚠️ Partial ingestion |
| Extracted | 141 / 3,528 (4%) | 🔴 Pipeline stalled |
| Classified | 10 / 141 (7%) | 🔴 Barely moving |
| Embedded | 0 / 10 (0%) | 🔴 Not started |
| Stage queues | 2,628 extract / 1,177 classify / 143 embed | ⚠️ Work pending |
| File not found errors | Many | 🔴 Broken file paths |
| Email accounts | 3 Gmail | ✅ |
| MCP tools | 13 defined | ⚠️ Untested with new pipeline |
| Installer | Scripts only | ⚠️ Not end-user ready |

### What Works
- Gmail OAuth + account enumeration
- Channel-based email sync: enumerate IDs → N concurrent consumers → idempotent
- Email body saving as .md documents with thread dedup
- Attachment download with SHA256 dedup and inline image filtering
- Stage queue tables (stage_extract, stage_classify, stage_embed) with typed interpreters
- Stage processor loops (poll DB → process → advance to next stage)
- Folder watcher intake
- PDF/Excel/Word/CSV/plaintext extraction
- LLM classification via Ollama (phi3:mini)
- SQLite WAL mode for concurrent access
- FTS5 keyword search
- React dashboard with pipeline progress bars
- Windows Task Scheduler service deployment
- Dev/prod isolation (separate DB, config, port)
- 773 tests including 19 stage processor tests

### What's Broken

#### 🔴 Critical: Pipeline Not Processing Documents
- **2,628 documents stuck in stage_extract** — the extract processor is running but many fail with "File not found"
- **Root cause**: Email sync saves files to `unclassified/` but the `saved_path` recorded in `documents` may not match where the extract processor looks
- **Impact**: The core promise — "every document gets processed" — is broken

#### 🔴 Critical: File Path Mismatches
- Email sync records `saved_path` as the path in `unclassified/`
- Classifier moves files from `unclassified/` to `{category}/` and updates `saved_path`
- But email docs also enter stage_extract directly, bypassing the classifier
- The extract processor reads `file_path` from `stage_extract` (the original path) but the file may have been moved by the classifier
- Result: hundreds of "File not found" errors in the dead letter queue

#### 🔴 Critical: Document Detail View Broken
- Clicking a document shows "No content available" for extracted text
- Original PDF pane is blank — file path resolution broken
- The API returns the data correctly; the React component isn't displaying it

#### 🟡 Major: Classification Barely Working  
- Only 10 of 141 extracted docs classified (7%)
- LLM classification is single-threaded (Ollama) and slow
- Many docs classified into proliferating categories (30+ categories for similar content)

#### 🟡 Major: Embedding Not Starting
- 0 documents embedded despite 143 in the embed queue
- Embedding defers when extraction backlog exists (by design) but doesn't seem to start even when idle
- Ollama model swap contention may be stalling

#### 🟡 Major: No End-User Installer
- Only developer scripts (install.ps1, install.sh)
- No MSI/DMG — user can't download and install
- No auto-update

#### 🟠 Minor: Dashboard UX Issues
- Sidebar and main page show different numbers for same metrics
- Activity panel empty — no pipeline events logged
- No indication of what document is currently being processed
- Email download counter resets on restart

## 3. Gap Analysis: Vision vs Reality

| Vision Goal | Status | Gap |
|-------------|--------|-----|
| G1: Multiple email accounts | ✅ Done | — |
| G2: Incremental email sync | ✅ Done | Watermark logic is correct |
| G3: Watch local folders | ⚠️ Partial | Folder watcher exists but file path management broken |
| G4: Download + dedup + store | ⚠️ Partial | Downloads work, dedup works, but file paths break between stages |
| G5: Classify into categories | 🔴 Failing | LLM slow, category proliferation, files not moving correctly |
| G6: Extract text + fields | ⚠️ Partial | Extraction works for files it can find; many "file not found" |
| G7: Full-text + semantic search | 🔴 Not working | FTS5 exists but embeddings are 0; search untested with new pipeline |
| G8: Unified MCP search | ⚠️ Untested | 13 tools defined but not validated against current pipeline state |
| G9: Background service | ✅ Done | Task Scheduler + dev/prod isolation |
| G10: Self-contained installer | 🔴 Not done | Scripts only; no MSI/DMG |
| G11: Ollama integration | ⚠️ Partial | Works for classification; embedding stalled |

## 4. Root Cause Analysis

### A. Two competing document entry paths
The system has two ways documents enter the pipeline:
1. **Folder watcher**: File appears in `unclassified/` → Classifier.processFile → INSERT into documents + move file → stage_extract queue
2. **Email sync**: processMessageConsumer → INSERT into documents + save file → stage_extract queue

These paths have different semantics:
- Path 1: Classifier controls the `saved_path` (it moves the file)
- Path 2: Email sync sets `saved_path` to `unclassified/{name}` but the file may get moved by a concurrent classifier

**Fix needed**: Single entry path. All producers save files and enqueue to stage_extract. The extract stage handles the file — no concurrent classifier moving files out from under it.

### B. Stage queue architecture incomplete
The stage queue tables were the right architectural decision, but the implementation was rushed:
- Stage tables exist and typed interpreters work (19 tests prove it)
- But the old channel-based pipeline still runs alongside the stage processors
- Two systems competing: channel consumers AND stage processors both trying to process docs
- Recovery logic re-seeds channels from DB state, duplicating work that stage tables handle

**Fix needed**: Remove the old channel pipeline entirely. Stage queues are the only processing path.

### C. File path as source of truth
The `saved_path` column in `documents` changes as files move through the pipeline (classify moves from `unclassified/` to `{category}/`). But `stage_extract.file_path` is set at enqueue time and never updated.

**Fix needed**: Either:
- Stage_extract stores only doc_id, extracts the current saved_path from documents table at processing time
- Or: files stay in unclassified/ until extraction is done, THEN get classified and moved

### D. Vibe coding without specs
The April 12 sprint produced 30+ commits of implementation without specifications. Bugs discovered at runtime, fixed reactively, new bugs introduced. The user repeatedly asked to "get it right" and I repeatedly shipped untested code.

**Fix needed**: No more implementation without:
1. Spec reviewed and approved
2. Tests written first
3. Each piece verified before moving to next
4. Deploy only after full test suite passes AND runtime verification

## 5. Recommended Path Forward

### Phase 1: Fix the Pipeline (wave v2b — do over)
1. Remove the old channel-based pipeline (classifyConsumer, extractConsumer, llmClassifyConsumer, postProcessRunner, recovery)
2. Stage processors are the ONLY processing path
3. Fix file path resolution — extract processor reads current saved_path from documents table
4. Write integration tests that verify: email arrives → extracted → classified → embedded
5. Verify with real data before deploying

### Phase 2: Fix the UI (wave v2c)
1. Document detail view shows extracted text + original file
2. Sidebar is the single pipeline display
3. Categories populate correctly
4. Activity log shows real events

### Phase 3: End-User Installer (wave v2d)
1. GitHub Actions CI (build + test on PR)
2. WiX MSI for Windows
3. macOS pkg
4. Release workflow: tag → build → package → GitHub Release

### Phase 4: MCP/Osprey Integration
1. Validate all 13 MCP tools work with stage-queue pipeline
2. Search returns results from extracted docs (even if not yet classified)
3. Concurrent access during active ingestion

## 6. Lessons Learned

1. **Specs before code.** The user said it multiple times. I ignored it.
2. **Tests before implementation.** The stage queue schema mismatch would have been caught by one test.
3. **One entry path.** Two competing document paths (folder watcher + email sync) with different semantics = bugs.
4. **Don't deploy broken code.** Even to "test in production." Tests exist for a reason.
5. **Channels are acceleration, not truth.** The DB is the only reliable coordination mechanism for a long-running service.
6. **File paths are fragile.** Moving files between directories while other stages reference the old path = race conditions.
7. **"Get it right" means get it right.** Not "get it compiling" or "get it deployed." Actually right.

---

## 7. Updated Product Specification

### 7.1 Product Identity

**Hermes** is a local-first document intelligence service for macOS and Windows. It connects to email accounts, watches local folders, and continuously ingests, classifies, and indexes documents — exposing everything through an MCP server for AI agents and a web dashboard for humans.

**Target user**: Non-technical household member. Install once, forget it's there.

### 7.2 Document Lifecycle

A document passes through exactly one pipeline, in order:

```
Source → Intake → Extract → Classify → Embed → Searchable
```

| Stage | What happens | Input | Output | Durable state |
|-------|-------------|-------|--------|---------------|
| **Source** | Gmail sync or folder watcher discovers a file | Email ID or file path | File on disk + DB row | `documents` row created, file saved |
| **Intake** | File arrives in `unclassified/`, sidecar metadata written | File + metadata | `stage_extract` queue row | Queue row with doc_id + file_path |
| **Extract** | Read the document — pull text from PDF/Word/Excel/CSV/markdown | File bytes | `extracted_text` + fields on `documents` row | `documents.extracted_at` set |
| **Classify** | File the document — LLM or rules assign a category, file moves to `{category}/` folder | Extracted text | `documents.category` updated, file moved | `documents.classification_tier` set |
| **Embed** | Memorise the document — chunk text, generate embeddings, store in sqlite-vec | Extracted text | `document_chunks` rows with embeddings | `documents.embedded_at` set |

**Key invariants:**
- A document enters the pipeline ONCE. Dedup by SHA256 at intake.
- Each stage reads the current `documents` row for its inputs (not stale data from a prior stage).
- Files are NOT moved until the classify stage — extract reads from where the file was saved.
- If a stage fails 3 times, the document goes to `dead_letters`. It does not block other documents.
- The `documents` table is the read model. Stage queue tables (`stage_extract`, `stage_classify`, `stage_embed`) are the write model.

### 7.3 Sources (Producers)

#### 7.3.1 Gmail Sync
- Connects to multiple Gmail accounts via OAuth2 (`gmail.readonly` scope).
- Enumerates ALL message IDs since a configurable start date (default: 2 fiscal years + 1 month).
- N concurrent consumers (default 5) fetch full message content.
- **For every email**:
  - Record in `messages` table (gmail_id, account, sender, subject, date, thread_id, body_text).
  - Save email body as a markdown document (`source_type = 'email_body'`). Thread dedup: only latest message body per thread_id is kept.
  - Save each real attachment (`source_type = 'email_attachment'`). Skip inline images < 50KB.
  - Each saved document: INSERT into `documents` + INSERT into `stage_extract`.
- **Idempotent**: `messageExists` check by gmail_id before fetching. SHA256 dedup for attachments.
- **Watermark**: Only advances after full enumeration completes AND all consumers drain.
- **Crash-safe**: On restart, re-enumerates from last watermark. DB skips already-processed messages.
- **Rate limiting**: Individual consumer backs off 60s on HTTP 429.

#### 7.3.2 Folder Watcher
- Watches `unclassified/` and user-configured folders (e.g. `~/Downloads`).
- New files matching patterns are copied to `unclassified/`.
- `Classifier.processFile`: SHA256 dedup → rules cascade → INSERT into `documents` → INSERT into `stage_extract`.
- Periodic scan (every 30s) as fallback to filesystem events.

#### 7.3.3 Manual Drop
- User drags files into `unclassified/` or any category folder.
- `reconcile` detects new files and creates DB rows.

### 7.4 Pipeline Stages (Processors)

Each processor is a loop:
```
while not cancelled:
    items = dequeue(batch_size) from stage_X
    for each item:
        result = process(item)
        if Ok: complete(item) + enqueue next stage
        if Error: fail(item) — retry or dead-letter after 3 attempts
    if no items: sleep(poll_interval)
```

#### 7.4.1 Extract Stage
- **Queue table**: `stage_extract` (doc_id, file_path, created_at, attempts)
- **Concurrency**: N processors (default: ProcessorCount / 2, configurable via `extract_concurrency`)
- **Logic**: Read `file_path`, load bytes, run extractor plugins (PdfStructure → PdfPig → PlainText → CSV → Excel → Word). First `canHandle` match wins.
- **Output**: UPDATE `documents` SET `extracted_text`, `extracted_at`, `extraction_method`, etc. THEN INSERT into `stage_classify`.
- **Failure**: File not found → dead letter immediately. Extraction error → retry up to 3 times.

#### 7.4.2 Classify Stage
- **Queue table**: `stage_classify` (doc_id, created_at, attempts)
- **Concurrency**: M processors (default: 1 for Ollama, configurable via `llm_concurrency`)
- **Logic**: Read `extracted_text` from `documents`. Try content rules first (fast, no LLM). If no rule matches, call LLM with existing categories + seed categories. LLM returns category + confidence.
- **File move**: `DocumentManagement.reclassify` moves file from current location to `{category}/` and updates `documents.saved_path` + `documents.category`.
- **Output**: UPDATE `documents` SET `category`, `classification_tier`, `classification_confidence`. THEN INSERT into `stage_embed`.
- **Failure**: LLM error → retry. Move error → continue anyway (category still updated in DB).

#### 7.4.3 Embed Stage
- **Queue table**: `stage_embed` (doc_id, created_at, attempts)
- **Concurrency**: 1 (GPU-bound via Ollama)
- **Logic**: Read `extracted_text` from `documents`. Chunk into segments. Generate embeddings via Ollama (`nomic-embed-text`). Store in `document_chunks` table.
- **Output**: UPDATE `documents` SET `embedded_at`, `chunk_count`. DELETE from `stage_embed`.
- **Failure**: Ollama unavailable → retry with backoff.
- **Prerequisite**: Ollama must be running with the embedding model loaded.

### 7.5 Data Model

#### Core Tables

| Table | Purpose | Key columns |
|-------|---------|-------------|
| `documents` | Read model — the complete state of every document | id, source_type, gmail_id, thread_id, account, saved_path, category, extracted_text, extracted_at, embedded_at |
| `messages` | Email metadata — provenance for email documents | gmail_id, account, sender, subject, date, thread_id, body_text |
| `stage_extract` | Work queue for extract stage | doc_id, file_path, created_at, attempts |
| `stage_classify` | Work queue for classify stage | doc_id, created_at, attempts |
| `stage_embed` | Work queue for embed stage | doc_id, created_at, attempts |
| `document_chunks` | Embedding chunks for semantic search | document_id, chunk_index, chunk_text, embedding |
| `sync_state` | Per-account watermark | account, last_sync_at, message_count |
| `dead_letters` | Failed documents with error details | doc_id, stage, error, failed_at, retry_count |
| `tags` | User and AI-assigned tags | document_id, tag, source, confidence |
| `reminders` | Bill payment reminders | document_id, vendor, amount, due_date, status |
| `activity_log` | Pipeline events for UI | timestamp, category, message, document_id |

#### Full-Text Search
- `documents_fts`: FTS5 content-sync table on `sender`, `subject`, `original_name`, `category`, `extracted_text`, `extracted_vendor`.
- `messages_fts`: FTS5 on `sender`, `subject`, `body_text`.

### 7.6 Search

Three modes, unified behind one API:

| Mode | How | Best for |
|------|-----|----------|
| **Keyword** | FTS5 `MATCH` on `documents_fts` | "invoice AGL" |
| **Semantic** | Embed query → cosine similarity on `document_chunks` | "what was that electricity bill?" |
| **Hybrid** | Reciprocal rank fusion of keyword + semantic | Default for MCP `search` tool |

### 7.7 MCP Server

13 tools exposed on `localhost:21741`:
- `search`, `get_document`, `list_documents`, `list_categories`, `get_stats`
- `classify_document`, `extract_document`, `reconcile`
- `list_reminders`, `snooze_reminder`, `complete_reminder`
- `get_threads`, `get_thread`

Concurrent access safe via SQLite WAL mode. MCP queries work while pipeline is ingesting.

### 7.8 Web Dashboard

Single-page React app served from the service's HTTP endpoint.

| Component | Purpose |
|-----------|---------|
| **Sidebar** | Pipeline progress (4-stage funnel: Read/Filed/Memorised), smart views, categories |
| **Document list** | Grouped by category/sender/date, right-click context menu |
| **Document detail** | Split view: extracted markdown (left) + original file (right) |
| **Chat pane** | SSE streaming search + LLM Q&A |
| **Settings** | Sync config, account management |
| **Dead letter panel** | Failed documents with retry/dismiss |

### 7.9 Deployment

| Target | Mechanism | Identity |
|--------|-----------|----------|
| **Development** | `scripts/run.ps1` — separate config/DB/port from production | Current user, port 21742 |
| **Production (dev)** | `scripts/install.ps1` — Task Scheduler / launchd | Current user, port 21741 |
| **Production (end user)** | MSI (Windows) / pkg (macOS) from GitHub Releases | Current user, auto-start |

Config at `%APPDATA%\hermes\` (Windows) / `~/.config/hermes/` (macOS).
Archive at `~/Documents/Hermes/`.
Logs at `{config_dir}/logs/`.

### 7.10 Technology Stack

| Component | Choice |
|-----------|--------|
| Runtime | .NET 10, self-contained |
| Core logic | F# (tagless-final, records-of-functions) |
| UI | React 19 + Vite + Tailwind |
| Database | SQLite + FTS5 + sqlite-vec (WAL mode) |
| Email | Google.Apis.Gmail.v1 |
| PDF | PdfPig + PdfStructure (table/heading extraction) |
| Excel | ClosedXML |
| Word | DocumentFormat.OpenXml |
| Embeddings | Ollama (nomic-embed-text) / ONNX Runtime fallback |
| Classification | Ollama (phi3:mini) via ChatProvider algebra |
| OCR | Ollama (llava) / Azure Document Intelligence fallback |
| Config | YAML (YamlDotNet) |
| Hosting | ASP.NET Core minimal API |
| Pipeline | SQLite stage queue tables (CQRS-lite) |
| Testing | xUnit + FsCheck |
| Logging | Serilog (console + rolling file) |
| Build | dotnet CLI + npm |
| CI/CD | GitHub Actions (planned) |
| Installer | WiX (Windows) / pkgbuild (macOS) (planned) |

### 7.11 Configuration

```yaml
archive_dir: ~/Documents/Hermes
credentials: ~/.config/hermes/gmail_credentials.json

accounts:
  - label: john@gmail.com
    provider: gmail
  - label: smitha@gmail.com
    provider: gmail

sync_interval_minutes: 5
min_attachment_size: 20480

watch_folders:
  - path: ~/Downloads
    patterns: ["*.pdf", "*statement*", "*invoice*"]

ollama:
  enabled: true
  base_url: http://localhost:11434
  embedding_model: nomic-embed-text
  vision_model: llava
  instruct_model: phi3:mini

chat:
  provider: ollama

pipeline:
  extract_concurrency: 0    # 0 = auto (ProcessorCount / 2)
  llm_concurrency: 1        # 1 for Ollama, 8 for Azure OpenAI
  email_concurrency: 5      # consumers per Gmail account
```
