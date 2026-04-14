# Wave v2b: Pipeline Stage Tables (CQRS-lite Architecture)

## Summary

Replace the fragile channel-only pipeline with durable stage queue tables. Each pipeline stage owns a SQLite table as its work queue. The `documents` table becomes the read model — progressively enriched as stages complete. This kills 4 of the 6 reported bugs architecturally and makes the pipeline observable, debuggable, and crash-proof.

## Problem

The current pipeline uses in-memory `Channel<T>` as the sole coordination mechanism. This causes:
- **Bug 2 (extract stall)**: Email sync inserts into `documents` AND saves the file. The classify consumer sees the SHA as duplicate and drops the doc — it never reaches the extract channel.
- **Bug 5 (no visibility)**: Channel depth is ephemeral — items flow through in milliseconds. No way to query "what's stuck."
- **Bug 6 (counter race)**: Shared mutable `int ref` counters across concurrent accounts produce `processed > queued`.
- **Crash fragility**: Channels are lost on restart. Recovery code tries to re-seed from DB but deadlocks on bounded channels.

## Solution: Stage Queue Tables

```
Email Sync ──► stage_extract ──► stage_classify ──► stage_embed
                    │                  │                 │
Folder Watch ──►    │                  │                 │
                    ▼                  ▼                 ▼
              [extract proc]    [classify proc]    [embed proc]
                    │                  │                 │
                    └──────── documents (read model) ────┘
```

### Schema

```sql
-- Work queue: documents awaiting text extraction
CREATE TABLE stage_extract (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    doc_id      INTEGER NOT NULL REFERENCES documents(id),
    file_path   TEXT NOT NULL,
    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
    attempts    INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX idx_stage_extract_created ON stage_extract(created_at);

-- Work queue: documents awaiting LLM classification
CREATE TABLE stage_classify (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    doc_id      INTEGER NOT NULL REFERENCES documents(id),
    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
    attempts    INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX idx_stage_classify_created ON stage_classify(created_at);

-- Work queue: documents awaiting embedding
CREATE TABLE stage_embed (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    doc_id      INTEGER NOT NULL REFERENCES documents(id),
    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
    attempts    INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX idx_stage_embed_created ON stage_embed(created_at);
```

### Processor pattern (each stage)

```
loop:
  SELECT doc_id, file_path FROM stage_X ORDER BY created_at LIMIT batch_size
  for each row:
    do work (extract text / classify / embed)
    UPDATE documents SET <stage columns> WHERE id = doc_id
    INSERT INTO stage_Y (doc_id, ...) VALUES (...)   -- next stage's queue
    DELETE FROM stage_X WHERE id = row.id
  sleep(interval) if no work found
```

### Entry points

- **Email sync**: `recordDocument` → INSERT into `documents` + INSERT into `stage_extract`
- **Folder watcher**: `Classifier.processFile` → INSERT into `documents` + INSERT into `stage_extract`
- **Recovery on startup**: No recovery needed — queue tables persist. Just start processors.

### Dashboard reads

```sql
SELECT COUNT(*) FROM stage_extract   -- "awaiting reading"
SELECT COUNT(*) FROM stage_classify  -- "awaiting filing"
SELECT COUNT(*) FROM stage_embed     -- "awaiting memorising"
```

Always accurate. No mutable counters. No polling lag. No race conditions.

## Goals

- Every document that enters the pipeline completes all stages (no drops)
- Pipeline state survives crashes — no recovery logic needed
- Dashboard numbers are always accurate (direct DB queries on queue tables)
- Each stage is independently observable and debuggable
- Adding a new pipeline stage = new table + new processor
- `documents` table is the clean read model for MCP, search, and UI

## Non-Goals

- Removing in-memory channels entirely (they become optional prefetch acceleration)
- Changing the classification algorithm or extraction logic
- Redesigning the email sync enumeration (that works fine)
- UI changes beyond fixing the sidebar (v2c handles dashboard polish)

## Acceptance Criteria

1. **Stage tables exist**: `stage_extract`, `stage_classify`, `stage_embed` created in schema.
2. **Email sync writes to stage_extract**: After `recordDocument`, doc ID is inserted into `stage_extract`.
3. **Folder watcher writes to stage_extract**: After `processFile`, doc ID is inserted into `stage_extract`.
4. **Extract processor reads stage_extract**: Polls table, extracts text, updates `documents`, inserts into `stage_classify`, deletes from `stage_extract`.
5. **Classify processor reads stage_classify**: Polls table, runs LLM classification, updates `documents.category`, inserts into `stage_embed`, deletes from `stage_classify`.
6. **Embed processor reads stage_embed**: Polls table, embeds document, updates `documents.embedded_at`, deletes from `stage_embed`.
7. **No recovery logic needed**: On restart, processors find pending work in their queue tables. No channel seeding.
8. **Dashboard accuracy**: `/api/stats` includes `awaitingExtract`, `awaitingClassify`, `awaitingEmbed` counts from queue tables.
9. **Bug 2 fixed**: Email documents flow through extract → classify → embed without being dropped.
10. **Bug 6 fixed**: No mutable counters — all progress comes from DB queries.
11. **Sidebar shows 4-stage funnel**: Compact progress bars reading from queue table counts.
12. **Dead letter handling**: After N failed attempts (default 3), doc moves to `dead_letters` table with error details.
13. **All existing tests pass**: No regression.

## Complexity

- **Score**: CS-3 (medium)
- **Confidence**: 0.85
- **Phases**:
  1. Schema: Add stage tables to Database.fs
  2. Processors: Extract, Classify, Embed stage processors (replace channel consumers)
  3. Entry points: Email sync + folder watcher write to stage_extract
  4. Pipeline.start: Launch processors instead of channel consumers
  5. API + Dashboard: Read queue table counts
  6. Remove channel recovery code
  7. Tests

## Open Questions (RESOLVED)

1. **Polling interval**: 2 seconds per stage.
2. **Batch size**: 10 per poll.
3. **Max attempts before dead letter**: 3 attempts, then move to dead_letters.
4. **Channel removal**: Keep channels as optional prefetch. Processors primarily read from DB.
5. **Bug 1 (categories)**: Not a bug — reclassify does update the category column. Sidebar just needed scrolling.
6. **Bug 3 (sidebar)**: Sidebar becomes the single pipeline display with compact progress bars.
7. **Bug 4 (activity log)**: Stage processors log to activity_log table on completion (batch summary).

## Risks & Assumptions

| Risk | Impact | Mitigation |
|------|--------|------------|
| SQLite write contention on queue tables | Pipeline throughput drop | WAL mode + short transactions. Batch deletes. |
| Polling adds latency vs channels | Slower processing | 2-second poll is acceptable. Channels can prefetch. |
| Migration from channel-based to queue-based | Test breakage | Phased: add queue tables first, then wire processors, then remove old code |

**Assumptions**:
- SQLite WAL mode handles concurrent reads (dashboard) + writes (processors) without contention
- 2-second polling is responsive enough for the pipeline (not latency-sensitive)
- The `documents` table schema stays unchanged — stage tables are additive
# Wave v2b: Pipeline Bug Fixes

**Mode**: Simple

## Summary

Six bugs discovered during live testing of the channel-driven pipeline prevent Hermes from delivering its core promise: every downloaded email gets read, filed, and memorised with accurate, consistent progress reporting. These range from data-flow breaks (classify doesn't update category, extract channel stalls) to UI inconsistencies (duplicate pipeline displays, empty activity panel) to race conditions (processed > queued counters).

## Goals

- Every document that enters the pipeline completes all stages: ingest → classify → extract → embed
- Classification updates both the database `category` field AND moves the file on disk
- Dashboard and sidebar show consistent, accurate pipeline progress without duplication
- Activity log table is populated so the Activity panel displays real events
- User can see which document is currently being processed at each pipeline stage
- Email sync counters never show processed > queued, even with multiple accounts

## Non-Goals

- Redesigning the pipeline architecture (channel-driven design is correct, just buggy)
- Adding new pipeline stages or capabilities
- Changing the classification rules or improving classification quality
- Performance optimisation of pipeline throughput
- Adding retry/dead-letter semantics (future wave)

## Target Domains

| Domain | Status | Relationship | Role in This Feature |
|--------|--------|-------------|---------------------|
| Pipeline | existing | **modify** | Fix classify→extract forwarding, add current-item tracking |
| DocumentManagement | existing | **modify** | Fix reclassify to update DB `category` field alongside file move |
| EmailSync | existing | **modify** | Fix counter race condition with atomic snapshots |
| ActivityLog | existing | **modify** | Wire pipeline events to `activity_log` table inserts |
| UI/Dashboard | existing | **modify** | Add current-item display, remove sidebar pipeline duplication |

## Complexity

- **Score**: CS-3 (medium)
- **Confidence**: 0.85
- **Phases**:
  1. Data-flow fixes (bugs 1, 2) — restore pipeline correctness
  2. Observability fixes (bugs 4, 5) — populate activity log, add current-item tracking
  3. UI fixes (bugs 3, 6) — unify display, fix counters

## Bug Details & Acceptance Criteria

### Bug 1: Categories sidebar only shows invoices(1) despite 139 filed

**Root cause hypothesis**: `DocumentManagement.reclassify` moves the file on disk but does not update the `category` column in the `documents` table. The `classification_tier` and `classification_confidence` are set by the LLM classifier, but the sidebar query reads the `category` column which remains `'unclassified'`.

**Acceptance criteria**:
1. After classification, the `category` column in `documents` matches the classified category
2. Categories sidebar shows all distinct categories with correct counts
3. Count in sidebar matches `SELECT COUNT(*) FROM documents WHERE category = X`

### Bug 2: "Reading" stuck at 186 — extract consumers not picking up email documents

**Root cause hypothesis**: The `classifyConsumer` processes documents but does not forward document IDs to the extract channel after classification. Or: the extract channel (capacity 500) is full and the classify consumer blocks silently.

**Acceptance criteria**:
1. Every document that completes classification is forwarded to the extract channel
2. Extract consumer count grows monotonically as new documents are classified
3. Pipeline processes email-sourced documents through all stages
4. If a channel is at capacity, the producer awaits (backpressure) rather than dropping items

### Bug 3: Sidebar is the single pipeline display — remove main-page dashboard

**Acceptance criteria**:
1. The sidebar pipeline section is the single, always-visible pipeline progress display
2. The main-page PipelineDashboard component is removed — the home page shows summary cards and sync config only
3. Sidebar shows the full 4-stage funnel (Downloading → Reading → Filing → Memorising) in compact form
4. All numbers in the sidebar match `/api/stats` and `/api/pipeline` exactly

### Bug 4: Activity panel empty — pipeline events not logged to activity_log table

**Acceptance criteria**:
1. Document downloads, classifications, extractions, and embeddings insert activity log entries
2. Activity panel in UI shows a scrolling list of recent events with timestamps
3. Activity log entries include enough context to be meaningful (document name, not just ID)

### Bug 5: No indication of what's currently being processed

**Acceptance criteria**:
1. Dashboard shows the filename currently being processed at each active pipeline stage
2. When a stage is idle, it shows "Idle" or equivalent
3. Current-item display updates within 1-2 seconds of stage transition

### Bug 6: `emailsProcessed > emailsQueued` race condition

**Acceptance criteria**:
1. At no point does the UI display processed > queued
2. Counters are read atomically — both values are snapshotted together
3. After all accounts finish enumeration, the total is stable and accurate
4. No lock contention on the hot path

## Open Questions (RESOLVED)

1. **Bug 3 resolution**: Keep sidebar pipeline section as the single compact pipeline view. Remove the main-page PipelineDashboard component — sidebar is always visible so it should be the effective display.

2. **Activity log granularity**: Batch — log one entry per stage completion batch (e.g., "Read 50 documents"), not 50 individual entries. User can email us their log for debugging.

3. **Current-item tracking scope**: Per-stage (simpler). Show one document name per stage.

## Workshop Opportunities

| Topic | Type | Why Workshop | Key Questions |
|-------|------|--------------|---------------|
| Activity log write strategy | Storage Design | High-volume inserts could impact throughput; need batching strategy | Batch size? Separate channel? Retention policy? |
| Pipeline observability model | State Machine | Current-item + counters + activity log overlap — design unified model | Single observable state record? Push vs poll? |
