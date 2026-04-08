# Hermes v2 — Implementation Plan

## Reference

- End-state spec: [20-pipeline-v2-endstate.md](20-pipeline-v2-endstate.md)
- Approved 2026-04-08. React + F# service architecture.

## Scope Summary

| Track | Phases | What changes |
|-------|--------|--------------|
| **A: Pipeline** | A1–A8 | Replace sequential `ServiceHost.runSyncCycle` with channel-driven pipeline + plugin registry |
| **B: Service + React** | B1–B8 | Replace Avalonia with F# HTTP API + React frontend (Vite + Tailwind + shadcn/ui) |

Tracks are sequential: A completes pipeline, B builds the new UI on top. A1 starts immediately.

---

## Track A: Pipeline

### A1 — Fix classify/extract ordering

**Goal:** Documents get extracted in the same sync cycle they're classified. Currently takes two cycles.

**Change:** In `ServiceHost.runSyncCycle`, swap `runExtraction` and `classifyUnclassified` so classify runs first, then extract sees the new DB rows.

**Files:**
- `ServiceHost.fs` — reorder two lines in `runSyncCycle`

**Test:** Drop a PDF in `unclassified/`, run one sync cycle, verify `extracted_at IS NOT NULL`.

**Size:** ~5 minutes.

---

### A2 — Channel between classify and extract

**Goal:** Classification writes doc IDs to an in-memory channel. Extraction reads from it. Documents flow within a single cycle without a DB round-trip.

**Change:**
- Create `Channel<int64>` in `ServiceHost`
- `classifyUnclassified` writes docId after INSERT
- `runExtraction` reads from channel (falls back to DB query for existing backlog)

**Files:**
- `ServiceHost.fs` — add channel, wire stages
- `Classifier.fs` — `processFile` returns docId on success (minor signature change)

**Test:** Existing tests + new test: classify → extract flows within one call.

**Size:** ~1 session.

---

### A3 — Extract stages into modules

**Goal:** Each pipeline stage is its own F# module with a clear input/output contract. `ServiceHost` becomes a thin orchestrator.

**New files:**
- `ClassifyStage.fs` — consumes `Channel<IngestEvent>`, produces to `Channel<int64>`
- `ExtractStage.fs` — consumes `Channel<int64>`, produces to `Channel<int64>`
- `PostStage.fs` — consumes `Channel<int64>`, runs fan-out (reclassify, embed, reminders)

**Moves from:**
- `ServiceHost.fs` — `classifyUnclassified`, `runExtraction`, `reclassifyUnsorted`, `runEmbedding`, `evaluateReminders` move into stage modules
- `ServiceHost.fs` shrinks to: channel creation + `Task.WhenAll(stages)` + heartbeat loop

**Test:** All existing tests pass. New tests per stage module.

**Size:** ~2 sessions. This is the biggest structural change.

---

### A4 — Dead letter channel

**Goal:** Failed documents route to a dead letter channel instead of blocking the queue or silently failing.

**New types in `Domain.fs`:**
```fsharp
type DeadLetter = {
    DocId: int64; Stage: string; Error: string
    Retryable: bool; FailedAt: DateTimeOffset
    RetryCount: int; OriginalName: string
}
```

**Changes:**
- `ExtractStage.fs` — permanent failures write to `deadLetterChannel`
- New `dead_letters` DB table for crash recovery
- Retry: push dead letters back to `extractChannel` with reset counter

**Files:**
- `Domain.fs` — add `DeadLetter` type
- `ExtractStage.fs` — dead letter routing
- `Database.fs` — `dead_letters` table + migration

**Removes:** The `extraction_method = 'failed'` hack we just added. Dead letters are the proper solution.

**Test:** Drop an encrypted PDF + a normal PDF. Normal one completes. Encrypted one appears in dead letter channel.

**Size:** ~1 session.

---

### A5 — Plugin registry

**Goal:** Extractors, classifiers, and post-processors are registered as plugins. Existing code wrapped in plugin records.

**New types in `Algebra.fs`:**
```fsharp
type ExtractorPlugin = { Name: string; Priority: int; CanHandle: string -> byte[] -> bool; Extract: byte[] -> Task<Result<ExtractionResult, string>> }
type ClassifierPlugin = { Name: string; Priority: int; Classify: SidecarMetadata option -> string -> ClassificationResult option }
type PostProcessor = { Name: string; Trigger: int64 -> Task<bool>; Process: int64 -> Task<unit> }
type PipelinePlugins = { Extractors: ExtractorPlugin list; Classifiers: ClassifierPlugin list; PostProcessors: PostProcessor list }
```

**Changes:**
- `Extraction.fs` — wrap `extractPdfContent`, `extractPdfText`, etc. as `ExtractorPlugin` records
- `ExtractStage.fs` — try plugins in priority order instead of `extractFromBytes` switch
- `Classifier.fs` — wrap `processFile` rules as `ClassifierPlugin`
- `PostStage.fs` — reminders, embedding become `PostProcessor` records

**Key:** No logic changes. Same functions, wrapped in records. The plugin dispatch loop is ~10 lines.

**Test:** Existing tests pass. New test: register a custom plugin, verify it's called for matching files.

**Size:** ~1 session.

---

### A6 — Startup recovery

**Goal:** On startup, repopulate channels from durable state so no documents are lost after a crash.

**Changes:**
- Scan `unclassified/` → push to `ingestChannel`
- Query `documents WHERE extracted_at IS NULL` → push to `extractChannel`
- Query `documents WHERE extracted_text IS NOT NULL AND embedded_at IS NULL` → push to `postChannel`
- Load `dead_letters` table → populate `deadLetterChannel`

**Files:**
- New `Recovery.fs` — ~60 lines

**Test:** Insert docs in various states, restart, verify they all complete.

**Size:** ~30 minutes.

---

### A7 — Pipeline observer

**Goal:** Live pipeline state from channel counters, not DB polling.

**New file:**
- `PipelineObserver.fs` — reads `channel.Reader.Count` for each channel, tracks rates, exposes `PipelineState` record + SSE-compatible `subscribe` function

**Type:**
```fsharp
type PipelineState = {
    IngestQueueDepth: int; ExtractQueueDepth: int; PostQueueDepth: int
    DeadLetterCount: int; ExtractedPerMinute: float; CurrentDoc: string option
    TotalDocuments: int64; TotalExtracted: int64; TotalEmbedded: int64
}
```

**Test:** Wire observer to test channels, push items, verify state updates.

**Size:** ~1 session.

---

### A8 — Post-processor plugin API

**Goal:** Reminders, embedding, and activity logging run as registered post-processors. External hooks (Pelican/Osprey) can register the same way.

**Changes:**
- `PostStage.fs` — iterates `plugins.PostProcessors` for each completed doc
- `Reminders.fs` — wrap `evaluateNewDocuments` as a `PostProcessor`
- `Embeddings.fs` — wrap `batchEmbed` as a `PostProcessor`
- `ActivityLog.fs` — wrap as a `PostProcessor`

**Test:** Register a test post-processor, verify it fires on doc completion.

**Size:** ~1 session.

---

**Track A complete.** At this point:
- Pipeline is channel-driven with concurrent stages
- Failed docs route to dead letters
- Plugins can be registered for extraction, classification, and post-processing
- Pipeline state is observable without DB polling
- **The Avalonia app still works** — `ServiceHost` is refactored but the bridge can still read `PipelineObserver.state()`

---

## Track B: Service + React

### B1 — Create `Hermes.Service` project + HTTP API

**Goal:** A new F# project that hosts the pipeline, MCP server, and HTTP API in one process.

**New project:** `src/Hermes.Service/Hermes.Service.fsproj`

**New files:**
- `ApiServer.fs` — HTTP routes using ASP.NET Core minimal API or Giraffe (~200 lines):
  - `GET /api/pipeline/state` (SSE stream)
  - `GET /api/documents`, `GET /api/documents/:id`, `GET /api/categories`
  - `GET /api/stats`, `GET /api/reminders`
  - `POST /api/sync`, `POST /api/chat`
  - `POST /api/dead-letters/retry`, `POST /api/dead-letters/dismiss`
  - `GET/PUT /api/settings`, `POST /api/accounts`, `POST /api/watch-folders`
- `CompositionRoot.fs` — replaces `HermesServiceBridge`. Wires everything once.
- `Program.fs` — entry point: load config, build composition root, start pipeline + API + MCP

**Moves from:**
- `McpServer.fs` / `McpTools.fs` — stay in Core, started from Service
- Pipeline stages from Track A — started from Service

**Test:** `curl localhost:21741/api/stats` returns JSON. SSE stream pushes pipeline state.

**Size:** ~2 sessions.

---

### B2 — Scaffold React app + Shell + pipeline hook

**Goal:** React app connects to the service and shows live pipeline state.

**Setup:**
```bash
npm create vite@latest hermes-web -- --template react-ts
cd hermes-web
npx shadcn@latest init
npm install @tanstack/react-query
```

**New files:**
- `src/api/hermes.ts` — typed fetch client
- `src/hooks/usePipelineState.ts` — SSE subscription
- `src/components/layout/Shell.tsx` — 3-column layout (sidebar, content, chat)
- `src/App.tsx` — router + query client provider

**Vite config:** proxy `/api/*` to `localhost:21741` during dev.

**Test:** `npm run dev` → browser shows shell with live pipeline state updating.

**Size:** ~1 session.

---

### B3 — Sidebar: pipeline funnel + library

**Goal:** The sidebar shows sources, extraction/classification progress, and library categories.

**New files:**
- `src/components/pipeline/SourcesPanel.tsx`
- `src/components/pipeline/StageProgress.tsx` — reusable (name, count, bar, rate, ETA)
- `src/components/pipeline/LibraryPanel.tsx` — category list with counts
- `src/components/layout/Sidebar.tsx` — composes the panels

**Data:** `usePipelineState()` for live counts, `useQuery('categories')` for library.

**Test:** Sidebar shows real data from the service. Stage progress updates live.

**Size:** ~1 session.

---

### B4 — Document browser

**Goal:** Click a category → see documents. Click a document → see detail with extracted fields + markdown content.

**New files:**
- `src/hooks/useDocuments.ts` — `useQuery` for document list + detail
- `src/components/documents/DocumentList.tsx` — sortable table
- `src/components/documents/DocumentCard.tsx` — row component
- `src/components/documents/DocumentDetail.tsx` — full view with metadata + markdown

**Test:** Click "invoices" → see document list. Click a doc → see extracted fields, dates, amounts, markdown content.

**Size:** ~1 session.

---

### B5 — Chat pane

**Goal:** Chat with Hermes. Keyword + semantic search. AI-powered Q&A with document citations.

**New files:**
- `src/hooks/useChat.ts` — POST to `/api/chat`, SSE streaming response
- `src/components/chat/ChatPane.tsx` — message list + input
- `src/components/chat/ChatMessage.tsx` — user vs Hermes messages, document cards
- `src/components/chat/SuggestedQueries.tsx` — chip buttons

**Test:** Type "find my land tax assessments" → see results with document cards. Toggle AI → get natural language answers.

**Size:** ~1–2 sessions.

---

### B6 — Settings + dead letters + reminders

**Goal:** Complete the remaining UI panels.

**New files:**
- `src/components/settings/SettingsDialog.tsx` — modal with tabs
- `src/components/settings/AccountsForm.tsx` — Gmail accounts
- `src/components/settings/WatchFoldersForm.tsx` — folder paths + patterns
- `src/components/pipeline/DeadLetterPanel.tsx` — failed docs with retry/dismiss
- `src/components/pipeline/ActionItemsPanel.tsx` — reminders with due dates

**Test:** Add a watch folder via settings, verify it takes effect. Retry a dead letter, verify it re-enters the pipeline.

**Size:** ~1 session.

---

### B7 — System tray

**Goal:** Minimal tray icon that starts with OS and opens the browser.

**New project:** `src/Hermes.Tray/` — ~50 lines of C#

**Behavior:**
- System tray icon with tooltip "Hermes — Document Intelligence"
- Left-click → open `http://localhost:21741` in default browser
- Right-click → menu: Open | Sync Now | Quit

**Size:** ~30 minutes.

---

### B8 — Delete Avalonia

**Goal:** Remove the Avalonia project. Clean break.

**Delete:**
- `src/Hermes.App/` (entire directory)
- `tests/Hermes.Tests.App/`
- `tests/Hermes.Tests.UI/`
- Remove from `hermes.slnx`

**Keep in git history** — `git log --all -- src/Hermes.App/` always recovers it.

**Size:** ~10 minutes.

---

## Timeline

| Phase | Depends on | Effort | Running total |
|-------|-----------|--------|---------------|
| A1 | — | 5 min | 5 min |
| A2 | A1 | 1 session | 1 session |
| A3 | A2 | 2 sessions | 3 sessions |
| A4 | A3 | 1 session | 4 sessions |
| A5 | A3 | 1 session | 5 sessions |
| A6 | A4 | 30 min | 5 sessions |
| A7 | A3 | 1 session | 6 sessions |
| A8 | A5 | 1 session | 7 sessions |
| B1 | A7 | 2 sessions | 9 sessions |
| B2 | B1 | 1 session | 10 sessions |
| B3 | B2 | 1 session | 11 sessions |
| B4 | B2 | 1 session | 12 sessions |
| B5 | B2 | 1–2 sessions | 13 sessions |
| B6 | B2 | 1 session | 14 sessions |
| B7 | B1 | 30 min | 14 sessions |
| B8 | B6 | 10 min | 14 sessions |

**Total: ~14 sessions.** A session is a focused coding block (agent + you). Not 40 years.

A5 and A7 can run in parallel with A4 and A6 respectively — the critical path is:

```
A1 → A2 → A3 → A4 → A6
                  ↘
               A5 → A8
                  ↘
               A7 → B1 → B2 → B3/B4/B5/B6 (parallel) → B7 → B8
```

B3, B4, B5, and B6 are independent React components — they can be built in any order or in parallel.

## Progress Tracker

| Phase | Status | Date | Notes |
|-------|--------|------|-------|
| A1 | not started | | |
| A2 | not started | | |
| A3 | not started | | |
| A4 | not started | | |
| A5 | not started | | |
| A6 | not started | | |
| A7 | not started | | |
| A8 | not started | | |
| B1 | not started | | |
| B2 | not started | | |
| B3 | not started | | |
| B4 | not started | | |
| B5 | not started | | |
| B6 | not started | | |
| B7 | not started | | |
| B8 | not started | | |

## Pre-flight: Commit current fixes

Before starting v2 work, commit the bug fixes from today's session:
- [x] Extraction progress reporting (per-document)
- [x] Reclassify progress reporting (per-document)
- [x] Background batch size increase (50 → 500)
- [x] Failed extraction marking (poison pill bypass)
- [x] Missing `classification_tier` column migration
- [ ] Commit all of the above
