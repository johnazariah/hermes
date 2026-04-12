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

### Bug 3: Sidebar pipeline section duplicates dashboard

**Acceptance criteria**:
1. Pipeline progress is shown in exactly one location (dashboard OR sidebar, not both)
2. If sidebar retains a pipeline indicator, it is a compact summary that links to the dashboard
3. No conflicting numbers visible to the user at any time

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

## Open Questions

1. **Bug 3 resolution**: Remove sidebar pipeline section entirely, or replace with compact summary?
2. **Activity log granularity**: Every single document event, or batch/summarise?
3. **Current-item tracking scope**: Track per-stage or per-consumer?

## Workshop Opportunities

| Topic | Type | Why Workshop | Key Questions |
|-------|------|--------------|---------------|
| Activity log write strategy | Storage Design | High-volume inserts could impact throughput; need batching strategy | Batch size? Separate channel? Retention policy? |
| Pipeline observability model | State Machine | Current-item + counters + activity log overlap — design unified model | Single observable state record? Push vs poll? |
