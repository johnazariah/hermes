# Hermes v2 — Pipeline & UI End-State Specification

## Status: APPROVED — React + F# service architecture

## Decision Record

- **2026-04-08**: Avalonia UI replaced with React (Vite + Tailwind + shadcn/ui). F# core unchanged.
- **Rationale**: Avalonia shell was monolithic (2,191-line code-behind), fragile, and hard to iterate on. React gives component ecosystem, better AI code generation, hot reload, and modern UX. Service already exposes MCP on localhost — adding an HTTP API for the React frontend is ~200 lines.
- **Fallback**: Avalonia code preserved in git history if needed.

## 1. Problem Summary

The current implementation has:
- A sequential `runSyncCycle` that processes all stages in series — one bad document blocks everything.
- Classification before extraction means documents take two sync cycles to get text.
- No pipeline flow — the DB is used as both queue and record store.
- A monolithic 1200-line `ShellWindow.axaml.cs` with no composability.
- `HermesServiceBridge` duplicating wiring that already exists in `ServiceHost`.
- No plugin points — adding an extractor means editing core modules.

The F# core modules are individually sound and well-tested (756 unit tests). The problems are at the composition boundary.

## 2. End-State Architecture

```
┌──────────────────────────────────────────────────────────────────────┐
│                        Hermes Process                                │
│                                                                      │
│  PRODUCERS (independent background tasks)                            │
│  ┌──────────┐  ┌──────────────┐  ┌──────────────┐                   │
│  │ Gmail    │  │ Folder       │  │ Backfill     │                   │
│  │ Sync     │  │ Watcher      │  │ (historical) │                   │
│  └────┬─────┘  └──────┬───────┘  └──────┬───────┘                   │
│       │               │                 │                            │
│       └───────────────┼─────────────────┘                            │
│                       ▼                                              │
│              ┌─────────────────┐                                     │
│              │ ingestChannel   │  Channel<IngestEvent>                │
│              └────────┬────────┘                                     │
│                       │                                              │
│  PIPELINE STAGES (concurrent, channel-connected)                     │
│                       ▼                                              │
│  ┌─────────────────────────────────────────────────────────────┐     │
│  │ Stage 1: CLASSIFY                                           │     │
│  │  dedup (SHA256) → rules cascade → move file → INSERT doc    │     │
│  │  Input: IngestEvent    Output: docId to extractChannel      │     │
│  └──────────────────────────┬──────────────────────────────────┘     │
│                              ▼                                       │
│              ┌─────────────────┐                                     │
│              │ extractChannel  │  Channel<int64>                     │
│              └────────┬────────┘                                     │
│                       ▼                                              │
│  ┌─────────────────────────────────────────────────────────────┐     │
│  │ Stage 2: EXTRACT                                            │     │
│  │  plugin registry → try extractors in priority order         │     │
│  │  success → update DB → write to postChannel                 │     │
│  │  permanent failure → write to deadLetterChannel             │     │
│  │  transient failure → back of extractChannel (with counter)  │     │
│  └──────────────────────────┬──────────────────────────────────┘     │
│                              ▼                                       │
│              ┌─────────────────┐                                     │
│              │ postChannel     │  Channel<int64>                     │
│              └────────┬────────┘                                     │
│                       │                                              │
│         ┌─────────────┼─────────────┬──────────────┐                 │
│         ▼             ▼             ▼              ▼                 │
│  ┌────────────┐ ┌──────────┐ ┌──────────┐ ┌──────────────┐          │
│  │ Reclassify │ │ Embed    │ │ Reminders│ │ Plugin Hooks │          │
│  │ (Tier 2+3) │ │ (vector) │ │ (dates)  │ │ (Pelican,   │          │
│  │            │ │          │ │          │ │  Osprey)     │          │
│  └────────────┘ └──────────┘ └──────────┘ └──────────────┘          │
│                                                                      │
│  DEAD LETTER                                                         │
│  ┌─────────────────────────────────────────────────────────────┐     │
│  │ deadLetterChannel: Channel<DeadLetter>                      │     │
│  │  Permanent failures surface in UI as actionable items       │     │
│  │  "Retry all" pushes back to extractChannel                  │     │
│  └─────────────────────────────────────────────────────────────┘     │
│                                                                      │
│  PERSISTENCE (record of outcomes, not a queue)                       │
│  ┌─────────────────────────────────────────────────────────────┐     │
│  │ db.sqlite: documents + FTS5 + sqlite-vec + activity_log     │     │
│  └─────────────────────────────────────────────────────────────┘     │
│                                                                      │
│  CONSUMERS                                                           │
│  ┌──────────┐  ┌────────────┐  ┌───────────┐                        │
│  │ MCP      │  │ Avalonia   │  │ CLI       │                        │
│  │ Server   │  │ Shell      │  │           │                        │
│  └──────────┘  └────────────┘  └───────────┘                        │
└──────────────────────────────────────────────────────────────────────┘
```

## 3. Pipeline Types

```fsharp
/// Event produced when a new file enters the system.
type IngestEvent = {
    FilePath: string
    Sidecar: SidecarMetadata option
    Priority: IngestPriority
    Source: IngestSource
}

and IngestPriority = Immediate | Normal | Backfill
and IngestSource = ManualDrop | FolderWatch | EmailSync | BackfillSync

/// Result of processing a document through any stage.
type StageOutcome<'T> =
    | Succeeded of 'T
    | Failed of error: string * retryable: bool

/// A document that permanently failed a pipeline stage.
type DeadLetter = {
    DocId: int64
    Stage: string
    Error: string
    Retryable: bool
    FailedAt: DateTimeOffset
    RetryCount: int
    OriginalName: string
}

/// Observable pipeline counters for the UI — read from channel state, not DB.
type PipelineState = {
    IngestQueueDepth: int
    ExtractQueueDepth: int
    PostQueueDepth: int
    DeadLetterCount: int
    ExtractedPerMinute: float
    CurrentDoc: string option
}
```

## 4. Plugin Registry

```fsharp
/// An extractor plugin: given file bytes, produces structured text.
/// Plugins are tried in priority order. First match wins.
type ExtractorPlugin = {
    Name: string
    Priority: int                                    // higher = tried first
    CanHandle: string -> byte[] -> bool              // filename + magic bytes
    Extract: byte[] -> Task<Result<ExtractionResult, string>>
}

/// A classifier plugin: given metadata, assigns a category.
/// Applied during Stage 1 (before extraction).
type ClassifierPlugin = {
    Name: string
    Priority: int
    Classify: SidecarMetadata option -> string -> ClassificationResult option
}

/// A reclassifier plugin: given extracted text, improves category.
/// Applied during Stage 3 (after extraction).
type ReclassifierPlugin = {
    Name: string
    Priority: int
    Reclassify: string -> string list -> Task<(string * float) option>  // text → categories → result
}

/// A post-processor: fires after a document completes the pipeline.
/// Used for integration hooks (Pelican, Osprey, calendar).
type PostProcessor = {
    Name: string
    Trigger: int64 -> Task<bool>    // docId → should I fire?
    Process: int64 -> Task<unit>    // docId → do the work
}

/// All plugins registered at startup.
type PipelinePlugins = {
    Extractors: ExtractorPlugin list
    Classifiers: ClassifierPlugin list
    Reclassifiers: ReclassifierPlugin list
    PostProcessors: PostProcessor list
}
```

### Default Plugins (ship with Hermes)

| Plugin | Type | Priority | Handles |
|--------|------|----------|---------|
| `PdfStructureExtractor` | Extractor | 100 | PDFs with extractable text (confidence ≥ 0.3) |
| `PdfPigExtractor` | Extractor | 50 | PDFs (PdfPig fallback) |
| `OpenXmlExtractor` | Extractor | 90 | .docx files |
| `ClosedXmlExtractor` | Extractor | 90 | .xlsx/.xls files |
| `CsvExtractor` | Extractor | 90 | .csv files |
| `PlainTextExtractor` | Extractor | 80 | .txt/.md/.log |
| `OllamaVisionExtractor` | Extractor | 10 | Images + scanned PDFs (when Ollama available) |
| `RulesClassifier` | Classifier | 100 | Sender/filename/sidecar rules |
| `ContentRulesReclassifier` | Reclassifier | 100 | Regex patterns on extracted text |
| `LlmReclassifier` | Reclassifier | 50 | Azure OpenAI / Ollama LLM |
| `ReminderProcessor` | PostProcessor | — | Docs with due dates/amounts |
| `ActivityLogProcessor` | PostProcessor | — | All docs (audit trail) |

### Future Plugins (not shipped, user-added)

| Plugin | Type | Purpose |
|--------|------|---------|
| `CbaBankStatementExtractor` | Extractor | Knows CBA PDF layout, extracts transactions |
| `AtoNoticeExtractor` | Extractor | Parses ATO assessment numbers, payment dates |
| `PelicanHook` | PostProcessor | Pushes invoices to General Ledger |
| `OspreyHook` | PostProcessor | Notifies tax module of new deductions |
| `CalendarHook` | PostProcessor | Creates calendar events for due dates |

## 5. Stage Contracts

### Stage 1: Classify

**Input:** `IngestEvent` from `ingestChannel`
**Output:** `int64` (docId) to `extractChannel`
**Responsibility:**
1. Compute SHA256 → dedup check → skip if duplicate
2. Load sidecar metadata (if exists)
3. Run classifier plugins in priority order → get category
4. Move file from `unclassified/` to `{category}/`
5. INSERT document row into DB
6. Write docId to `extractChannel`
7. Clean up sidecar file

**Guarantees:**
- Every file that enters `unclassified/` gets processed exactly once
- Duplicates are deleted silently (SHA256 match)
- Files that can't be classified go to `unsorted/`

### Stage 2: Extract

**Input:** `int64` (docId) from `extractChannel`
**Output:** `int64` (docId) to `postChannel`, or `DeadLetter` to `deadLetterChannel`
**Responsibility:**
1. Read file bytes from disk
2. Try extractor plugins in priority order (first `canHandle` match wins)
3. On success: update DB row with extracted text, markdown, fields, method → write to `postChannel`
4. On transient failure: increment retry counter, write back to `extractChannel` (max 3 retries)
5. On permanent failure: write to `deadLetterChannel`

**Permanent failure criteria:**
- File not found on disk
- Encrypted PDF (no password)
- Unsupported file type with no matching plugin
- 3 consecutive transient failures

**Guarantees:**
- A failed document never blocks the queue
- Every document gets extracted or explicitly marked as failed
- Extraction is idempotent — safe to retry

### Stage 3: Post-process (fan-out)

**Input:** `int64` (docId) from `postChannel`
**Output:** none (terminal stage)
**Responsibility:** Run all applicable post-processors concurrently:
1. **Reclassify**: content rules → LLM → potentially move to better category
2. **Embed**: chunk text → generate vectors → write to sqlite-vec
3. **Reminders**: extract due dates → create reminder entries
4. **Plugin hooks**: fire any registered PostProcessors where `trigger` returns true

**Guarantees:**
- Post-processors are independent — one failure doesn't block others
- Post-processor failures are logged but don't create dead letters (they can be retried via the UI)

## 6. Dead Letter Handling

Dead letters are documents that permanently failed a pipeline stage.

**Storage:** In-memory `Channel<DeadLetter>` + persisted to `dead_letters` DB table for crash recovery.

**UI surface:** Dead letters appear in the Action Items panel:
```
🔴 4 documents failed extraction
   2026-03-30_file_JA192.pdf — encrypted PDF
   2026-03-30_file_JA511.pdf — encrypted PDF
   2026-03-30_file_JA529.pdf — encrypted PDF
   2026-03-30_file_OSEE GoStream.pdf — XObject not found
   [Retry All]  [Dismiss]
```

**Recovery:**
- "Retry All" pushes documents back to `extractChannel` with reset retry counter
- Installing a new extractor plugin (e.g., OCR) and retrying resolves previously-unextractable docs
- "Dismiss" marks the dead letter as acknowledged (hidden from action items, still in DB)

## 7. Startup & Recovery

On startup, the pipeline must recover from a previous crash or shutdown:

1. **Scan `unclassified/` directory** → push all files to `ingestChannel` (handles files that arrived while stopped)
2. **Query DB for unextracted docs** (`extracted_at IS NULL AND extraction_method != 'failed'`) → push to `extractChannel`
3. **Query DB for unembedded docs** (`extracted_text IS NOT NULL AND embedded_at IS NULL`) → push to `postChannel`
4. **Load dead letters from DB** → populate `deadLetterChannel`

This makes the pipeline crash-safe. No document is lost. Channels are repopulated from durable state (filesystem + DB).

## 8. Backpressure & Priority

**Bounded channels** with configurable capacity:
```fsharp
let ingestChannel  = Channel.CreateBounded<IngestEvent>(BoundedChannelOptions(1000, FullMode = Wait))
let extractChannel = Channel.CreateBounded<int64>(500)
let postChannel    = Channel.CreateBounded<int64>(500)
```

When a channel is full, the producer waits. This prevents unbounded memory growth during large backfills.

**Priority:** `IngestPriority` determines ordering:
- `Immediate` — manual drops, folder watcher (user is watching)
- `Normal` — new email sync
- `Backfill` — historical email download

Implementation: two channels (fast + slow), with the classify stage draining fast first.

## 9. Observability

The pipeline exposes a `PipelineState` record that the UI reads directly — no DB polling for live progress.

```fsharp
type PipelineObserver = {
    state: unit -> PipelineState
    subscribe: (PipelineState -> unit) -> IDisposable
}
```

The UI subscribes and updates on every state change. No 5-second refresh timer. No `FindControl<>` / manual text setting.

## 10. Process Model

```
Hermes.Service (always running — .NET 10 / F#)
  ├── Pipeline (channels, stages, plugins)
  ├── MCP Server (localhost:21740, streamable HTTP + stdio shim)
  ├── HTTP API (localhost:21741, serves React app + JSON API + SSE)
  └── Static file serving (React build output)

Hermes.Tray (optional, starts with OS — .NET 10 / C#, ~50 lines)
  └── System tray icon → opens http://localhost:21741

Browser
  └── React app (Vite + Tailwind + shadcn/ui)
```

## 11. HTTP API (`Hermes.Service/ApiServer.fs`)

The API exists alongside the MCP server. Both share the same DB and pipeline observer.

```
Pipeline state (live)
  GET  /api/pipeline/state                → SSE stream of PipelineState
  GET  /api/pipeline/dead-letters         → DeadLetter[]
  POST /api/pipeline/dead-letters/retry   → retry all failed
  POST /api/pipeline/dead-letters/dismiss → dismiss all

Documents
  GET  /api/documents?category=X&offset=N&limit=N → DocumentSummary[]
  GET  /api/documents/:id                 → DocumentDetail
  GET  /api/documents/:id/content         → markdown body
  GET  /api/categories                    → CategoryCount[]

Status
  GET  /api/stats                         → IndexStats
  GET  /api/reminders                     → ReminderItem[]
  GET  /api/services/ollama               → OllamaStatus

Actions
  POST /api/sync                          → trigger sync
  POST /api/chat                          → streaming chat response (SSE)

Configuration
  GET  /api/settings                      → current config
  PUT  /api/settings                      → update config
  POST /api/accounts                      → add Gmail account
  POST /api/watch-folders                 → add watch folder
```

~200 lines of F# routing. Half already exists as MCP tool implementations in `McpTools.fs`.

**Security:** API binds to `localhost` only. No authentication needed (same trust model as MCP server).

## 12. React Frontend (`Hermes.Web`)

### Tech Stack

| Tool | Purpose |
|------|---------|
| Vite | Build + dev server with HMR |
| React 19 | Component framework |
| TypeScript | Strict mode, no `any` |
| Tailwind CSS 4 | Utility-first styling |
| shadcn/ui | Polished component library (not a dependency — copied into project) |
| Tanstack Query | Server state caching + refetch |

### Structure

```
src/Hermes.Web/
├── src/
│   ├── App.tsx
│   ├── api/
│   │   └── hermes.ts              # Typed fetch client + SSE helpers
│   ├── components/
│   │   ├── layout/
│   │   │   ├── Shell.tsx          # 3-column: sidebar + content + chat
│   │   │   ├── Sidebar.tsx        # Pipeline funnel
│   │   │   └── StatusBar.tsx      # Bottom bar with service status
│   │   ├── pipeline/
│   │   │   ├── SourcesPanel.tsx   # Email accounts + watch folders
│   │   │   ├── StageProgress.tsx  # Reusable: name, count, bar, rate, ETA
│   │   │   ├── LibraryPanel.tsx   # Category list with counts + click to browse
│   │   │   └── DeadLetterPanel.tsx # Failed docs + retry/dismiss
│   │   ├── documents/
│   │   │   ├── DocumentList.tsx   # Table/grid of docs in a category
│   │   │   ├── DocumentCard.tsx   # Summary row: name, date, amount, tier
│   │   │   └── DocumentDetail.tsx # Full view: metadata + extracted markdown
│   │   ├── chat/
│   │   │   ├── ChatPane.tsx       # Message list + input + suggested queries
│   │   │   └── ChatMessage.tsx    # Single message (user or Hermes)
│   │   ├── settings/
│   │   │   ├── SettingsDialog.tsx  # Modal: accounts, folders, sync, chat provider
│   │   │   ├── AccountsForm.tsx
│   │   │   └── WatchFoldersForm.tsx
│   │   └── ui/                    # shadcn/ui components (Button, Dialog, Progress, etc.)
│   ├── hooks/
│   │   ├── usePipelineState.ts    # SSE subscription → reactive PipelineState
│   │   ├── useDocuments.ts        # Tanstack Query for document lists
│   │   ├── useChat.ts             # SSE streaming chat
│   │   └── useSettings.ts         # Config read/write
│   └── types/
│       └── hermes.ts              # TypeScript types matching F# API contracts
├── index.html
├── vite.config.ts
├── tailwind.config.ts
├── tsconfig.json
└── package.json
```

### Key Hook: `usePipelineState`

```typescript
export function usePipelineState() {
  const [state, setState] = useState<PipelineState>(initial);

  useEffect(() => {
    const source = new EventSource('/api/pipeline/state');
    source.onmessage = (e) => setState(JSON.parse(e.data));
    return () => source.close();
  }, []);

  return state;
}
```

No polling. Service pushes state changes via SSE when channel depths change.

### UX Principles

1. **Library is the default view.** Documents by category. That's what matters.
2. **Pipeline is background.** Status line in the sidebar: "Extracting 234/3,931 · ~2,200/min". Expand for details.
3. **No manual trigger buttons for normal operations.** Extraction runs continuously. "Retry failed" on the dead letter panel is the only action.
4. **Problems surface as toast notifications.** "4 documents couldn't be extracted. [View]" — not a broken progress bar.
5. **Components are independent.** Change the extraction panel without touching chat.
6. **Dark mode by default.** Tailwind dark classes. Toggle available.

### Dev Workflow

```bash
# Development (two terminals)
cd src/Hermes.Service && dotnet run          # F# service on :21741
cd src/Hermes.Web && npm run dev             # Vite dev server on :5173, proxies API to :21741

# Production build
cd src/Hermes.Web && npm run build           # outputs to dist/
# Service serves dist/ as static files — single process
```

## 13. Composition Root

One place wires everything:

```fsharp
module CompositionRoot =

    let build (config: HermesConfig) (db: Database) (fs: FileSystem) (logger: Logger) (clock: Clock) =
        // Channels
        let ingestCh  = Channel.CreateBounded<IngestEvent>(1000)
        let extractCh = Channel.CreateBounded<int64>(500)
        let postCh    = Channel.CreateBounded<int64>(500)
        let deadCh    = Channel.CreateUnbounded<DeadLetter>()

        // Plugin registry
        let plugins = {
            Extractors = [
                PdfStructureExtractor.plugin      // priority 100
                PdfPigExtractor.plugin             // priority 50
                OpenXmlExtractor.plugin            // priority 90
                ClosedXmlExtractor.plugin          // priority 90
                CsvExtractor.plugin                // priority 90
                PlainTextExtractor.plugin          // priority 80
                OllamaVisionExtractor.plugin       // priority 10 (when available)
            ]
            Classifiers = [ RulesClassifier.plugin ]
            Reclassifiers = [
                ContentRulesReclassifier.plugin    // priority 100
                LlmReclassifier.plugin             // priority 50 (when available)
            ]
            PostProcessors = [
                ReminderProcessor.plugin
                ActivityLogProcessor.plugin
            ]
        }

        // Stages (each is a background task)
        let classifyStage   = ClassifyStage.create fs db logger clock plugins ingestCh extractCh
        let extractStage    = ExtractStage.create fs db logger clock plugins extractCh postCh deadCh
        let postStage       = PostStage.create db logger clock plugins postCh
        let deadLetterStore = DeadLetterStore.create db deadCh

        // Producers
        let emailProducer   = EmailProducer.create fs db logger clock config ingestCh
        let folderProducer  = FolderProducer.create fs logger config ingestCh
        let recoveryLoader  = RecoveryLoader.create fs db ingestCh extractCh postCh deadCh

        // Observer for UI
        let observer = PipelineObserver.create ingestCh extractCh postCh deadCh

        // HTTP API + static file serving
        let apiServer = ApiServer.create db fs logger observer config

        {| Stages = [classifyStage; extractStage; postStage; deadLetterStore]
           Producers = [emailProducer; folderProducer]
           Recovery = recoveryLoader
           Observer = observer
           Plugins = plugins
           ApiServer = apiServer |}
```

## 14. Migration Path

Not a rewrite. Incremental refactoring — the app works after every phase.

### Track A: Pipeline (F# backend)

| Phase | What | Risk | Validates |
|-------|------|------|-----------|
| **A1** | Fix classify/extract ordering in current sequential loop | Minimal | Documents extract in same cycle they're classified |
| **A2** | Introduce `Channel<int64>` between classify and extract within `runSyncCycle` | Low | Channel plumbing works, extraction starts flowing immediately |
| **A3** | Extract each stage into its own module (`ClassifyStage.fs`, `ExtractStage.fs`, `PostStage.fs`) | Medium | Stages run as independent background tasks |
| **A4** | Add `DeadLetter` channel and type. Failed docs route there instead of blocking | Low | Poison pills handled cleanly |
| **A5** | Wrap existing extractors as `ExtractorPlugin` records. Build plugin registry | Low | Existing behavior preserved, new plugin points available |
| **A6** | Add startup recovery (scan unclassified/, query DB for incomplete docs) | Low | Crash-safe pipeline |
| **A7** | Build `PipelineObserver` from channel state | Low | Live progress without DB polling |
| **A8** | Add post-processor plugin API. Wire reminders as first post-processor | Low | Plugin hooks work for Pelican/Osprey |

### Track B: HTTP API + React (can start after A7)

| Phase | What | Risk | Validates |
|-------|------|------|-----------|
| **B1** | Create `Hermes.Service` project. Move `Program.fs` entry point. Build HTTP API routes (~200 lines F#) | Medium | API serves JSON, SSE streams pipeline state |
| **B2** | Scaffold `Hermes.Web` (Vite + React + Tailwind + shadcn/ui). Build `Shell.tsx` layout + `usePipelineState` hook | Low | React app connects to service, shows live pipeline state |
| **B3** | Build `Sidebar.tsx` + `StageProgress.tsx` + `LibraryPanel.tsx` | Low | Pipeline funnel renders with live data |
| **B4** | Build `DocumentList.tsx` + `DocumentDetail.tsx` | Low | Browse documents by category |
| **B5** | Build `ChatPane.tsx` with SSE streaming | Medium | Chat works with keyword + semantic search + AI |
| **B6** | Build `SettingsDialog.tsx` + `DeadLetterPanel.tsx` | Low | Config management + failed doc handling |
| **B7** | Build `Hermes.Tray` (minimal system tray, opens browser) | Minimal | Tray icon launches UI |
| **B8** | Delete `Hermes.App` (Avalonia project) | — | Clean break |

### Dependency Graph

```
A1 → A2 → A3 → A4 → A5 → A6 → A7 → A8
                                  │
                                  └──→ B1 → B2 → B3 → B4 → B5 → B6 → B7 → B8
```

Track A (pipeline) must reach A7 before Track B starts — the React app needs `PipelineObserver` and the HTTP API.

Phases A1–A2 can start immediately. The Avalonia app continues to work throughout Track A.
Track B replaces the UI. Once B6 is done, B8 deletes Avalonia.

## 15. What Doesn't Change

- **F# core modules**: `Extraction`, `Classifier`, `Embeddings`, `Database`, `Config`, `Rules`, `Domain` — all stay. They become the implementations behind plugin interfaces.
- **SQLite + FTS5 + sqlite-vec**: Same storage.
- **Algebra / Tagless-Final pattern**: Same capability abstraction.
- **MCP server**: Same tools, same port (21740).
- **Archive directory structure**: `~/Documents/Hermes/{category}/{file}`.
- **Test suite**: 756 tests continue to pass. New tests added for channel stages and API routes.
- **CLI**: `Hermes.Cli` stays as a thin wrapper for scripting.

## 16. What Gets Deleted

| File/Project | Lines | Replacement |
|---|---|---|
| `Hermes.App/` (entire Avalonia project) | ~3,800 | `Hermes.Web/` (React) + `Hermes.Tray/` (~50 lines) |
| `ServiceHost.fs` | 354 | `Pipeline.fs` + stage modules (~300 lines total) |
| `HermesServiceBridge.cs` | 482 | Gone. API server talks directly to pipeline observer + DB |

## 17. Solution Structure (End-State)

```
hermes/
├── src/
│   ├── Hermes.Core/              # F# library — domain, pipeline, DB, config (UNCHANGED)
│   ├── Hermes.Service/           # F# — headless service: pipeline + API + MCP
│   │   ├── Pipeline.fs           # Channel orchestration + stage runner
│   │   ├── PipelineObserver.fs   # Observable state from channel counters
│   │   ├── ApiServer.fs          # HTTP JSON API + SSE + static file serving
│   │   ├── CompositionRoot.fs    # Wire everything
│   │   └── Program.fs            # Entry point
│   ├── Hermes.Web/               # React frontend (Vite + Tailwind + shadcn/ui)
│   ├── Hermes.Tray/              # Minimal .NET tray icon → opens browser
│   └── Hermes.Cli/               # CLI entry point (UNCHANGED)
├── tests/
│   ├── Hermes.Tests/             # F# unit tests (UNCHANGED + new pipeline tests)
│   └── Hermes.Tests.Web/         # Playwright or Vitest for React components
└── .project/
    └── design/
        └── 20-pipeline-v2-endstate.md  # This document
```

## 18. Success Criteria

| # | Criterion | How to verify |
|---|-----------|---------------|
| S1 | A PDF dropped in a watch folder is classified, extracted, and searchable within 10 seconds | End-to-end timing test |
| S2 | An encrypted PDF lands in the dead letter channel, not blocking other documents | Drop encrypted + normal PDF, verify normal one completes |
| S3 | Adding a custom extractor requires no edits to existing modules | Register a plugin, verify it's called for matching files |
| S4 | React UI shows live pipeline progress via SSE without polling | Open browser, verify real-time updates as docs process |
| S5 | A crash during extraction loses zero documents | Kill process mid-batch, restart, verify all docs eventually complete |
| S6 | A Pelican/Osprey hook can be registered and fires on invoice classification | Register a PostProcessor, verify it receives classified invoice docs |
| S7 | `npm run dev` + `dotnet run` gives hot-reloading UI development | Change a React component, see update in < 1 second |
| S8 | Production build is a single `dotnet publish` that bundles the React app | Verify no separate web server needed |
