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
