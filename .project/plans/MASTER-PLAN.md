---
description: "Master execution plan — sequential on main, one commit per step. Ordered task list for an agent to follow. No worktrees. No parallel branches."
---

# Hermes — Master Execution Plan

## Strategy: Sequential on `main`

**All work happens on `main`, one step at a time, one commit per step.** No worktrees, no parallel branches. This is the fastest approach because nearly every spec touches the same files (`Hermes.Core.fsproj`, `Database.fs`, `McpTools.fs`, `ServiceHost.fs`, `Extraction.fs`, `ShellWindow.axaml`). Parallel branches would produce constant merge conflicts — especially the F# `.fsproj` file ordering.

## Feature Plans (reference)

| Spec | Plan | Phases |
|------|------|--------|
| Doc 15: Rich UI | [rich-ui/plan.md](rich-ui/plan.md) | U1–U6 |
| Doc 17: Document Extraction | [document-extraction/plan.md](document-extraction/plan.md) | P1–P12 |
| Doc 18: Smart Classification | [smart-classification/plan.md](smart-classification/plan.md) | C1–C5 |
| Doc 13: Document Feed | [document-feed/plan.md](document-feed/plan.md) | F1–F4 |
| Doc 14: MCP Platform API | [mcp-platform-api/plan.md](mcp-platform-api/plan.md) | M1–M4 |
| Doc 16: Osprey Integration | [osprey-integration/plan.md](osprey-integration/plan.md) | I1–I7 |

## Dependency Graph

```
ALREADY DONE                HERMES BACKEND              UI                  PLATFORM/CONSUMERS
═══════════                 ══════════════              ══                  ══════════════════

Doc 11 ✅ ──┐
Doc 12 ✅ ──┤
            │
            ├── Doc 17 P1–P6 ──── P7 ──── P8 ─────────────────────────── Doc 13 F1+F2 (= M1)
            │   (PDF structure)   (pipe)  (MCP)                          (Feed tools)
            │                       │       │                                  │
            │                       │       │                                  ├── Doc 14 M2
            │                       │       │                                  │   (Reminder MCP)
            │                       │       │                                  │
            │                       ▼       │                                  ├── Doc 14 M3
            │               Doc 18 C1 ──── C2 ─── C3 ─── C4                   │   (Doc mgmt MCP)
            │               (reorder)     (content) (LLM) (bulk)               │
            │                               │                                  │
            │                               ▼                                  │
            │                           Doc 18 C5                              │
            │                           (insight UI)                           │
            │                                                                  │
            │                                                                  ▼
            │   Doc 17 P9 ─────────────────────────────────────── Doc 16 I3–I7
            │   (Excel)                                           (Osprey consumer)
            │   Doc 17 P10 ────────────────────────────────────┘
            │   (Word)
            │   Doc 17 P11 ────────────────────────────────────┘
            │   (CSV)
            │       │
            │       ▼
            │   Doc 17 P12
            │   (format dispatch)
            │
            │
            └── Doc 15 U1 ──── U2 ──── U3 ──── U4 ──── U5 ──── U6
                (shell)       (docs)  (threads) (items) (timeline) (chat)
```

---

## Agent Instructions

### Before every step

```
dotnet build hermes.slnx --nologo
dotnet test tests/Hermes.Tests/Hermes.Tests.fsproj --nologo
```

Both must pass with 0 errors, 0 warnings. If not, fix before proceeding.

### Rules (always active)

1. Read the detailed plan for each step from the linked plan file. The step descriptions below are summaries — the plans have full specifications (types, functions, SQL, test names, PROOF steps).
2. **F# code** must go through `@fsharp-dev` agent. Do not write F# without it.
3. **C# code** must go through `@csharp-dev` agent. Do not write C# without it.
4. **Python code** (Osprey steps) must go through `@python-dev` agent.
5. **UI definition of done**: XAML exists → code-behind wired → buttons functional → data live → build clean → smoke tested.
6. **Silver thread**: Before marking any step complete, trace: Input → Processing → Backend → Presentation → UI/MCP Response. If any link is broken, the step is NOT done.
7. **PROOF**: Every step has a PROOF in its plan. Run it. Do not mark complete until it passes.
8. **Commit**: Each step is one commit with the message specified in the plan. Run `dotnet build` + `dotnet test` before committing.
9. **Update this checklist**: After committing each step, update the status below from ⬜ to ✅.

### How to read a step

Each step below says:
- **Plan reference**: which plan file and phase to read for full specs
- **Summary**: what gets built
- **Why here**: why this step is in this position (dependency reasoning)
- **Files touched**: the primary files modified/created (for conflict awareness)
- **Commit message**: exact commit message to use

Read the full phase specification from the plan file before starting work. The plan has types, function signatures, SQL statements, test names, and PROOF steps that this checklist intentionally does not duplicate.

---

## Ordered Execution Checklist

### Step 1 · P1: PDF Letter Extraction + Line Clustering
- **Plan**: [document-extraction/plan.md](document-extraction/plan.md) → Phase P1
- **Summary**: New `PdfStructure.fs` module — extract letters from PdfPig, cluster into words and lines, assemble reading-order text
- **Why here**: Foundation for all PDF extraction. No dependencies. Pure addition — no existing files modified.
- **Files touched**: `+PdfStructure.fs`, `Hermes.Core.fsproj` (add Compile), `+PdfStructureTests.fs`, `Hermes.Tests.fsproj`
- **Commit**: `feat: PdfStructure module — letter extraction and line clustering from PdfPig`
- **Status**: ⬜

### Step 2 · P2: Heading Detection
- **Plan**: [document-extraction/plan.md](document-extraction/plan.md) → Phase P2
- **Summary**: Detect headings by font size, bold, all-caps in `PdfStructure.fs`. Emit `## Heading` markdown.
- **Why here**: Needs P1 lines. Extends PdfStructure.fs (no new files in fsproj).
- **Files touched**: `PdfStructure.fs`, `PdfStructureTests.fs`
- **Commit**: `feat: heading detection in PdfStructure — font size, bold, and all-caps`
- **Status**: ⬜

### Step 3 · P3: Table Detection
- **Plan**: [document-extraction/plan.md](document-extraction/plan.md) → Phase P3
- **Summary**: Column-aligned text → table extraction with headers and rows. This is the hardest algorithm.
- **Why here**: Needs P1 lines. Independent of P2. Critical for downstream value (bank statements, payslips).
- **Files touched**: `PdfStructure.fs`, `PdfStructureTests.fs`
- **Commit**: `feat: table detection in PdfStructure — column alignment and cell extraction`
- **Status**: ⬜

### Step 4 · P4: Key-Value Pair Detection
- **Plan**: [document-extraction/plan.md](document-extraction/plan.md) → Phase P4
- **Summary**: "Label: Value" and gap-separated patterns → `- **Label:** Value` markdown.
- **Why here**: Needs P1 lines. Independent of P2/P3.
- **Files touched**: `PdfStructure.fs`, `PdfStructureTests.fs`
- **Commit**: `feat: key-value pair detection in PdfStructure — colon and gap patterns`
- **Status**: ⬜

### Step 5 · P5: Multi-Page Table Continuation
- **Plan**: [document-extraction/plan.md](document-extraction/plan.md) → Phase P5
- **Summary**: Detect table continuation across pages (same column boundaries) → merge into single table.
- **Why here**: Needs P3 tables. Bank statements spanning 3+ pages need this.
- **Files touched**: `PdfStructure.fs`, `PdfStructureTests.fs`
- **Commit**: `feat: multi-page table continuation in PdfStructure`
- **Status**: ⬜

### Step 6 · P6: CID Fallback + Confidence Scoring
- **Plan**: [document-extraction/plan.md](document-extraction/plan.md) → Phase P6
- **Summary**: CID-encoded font detection, confidence scoring, OCR fallback trigger. `extractStructured` main entry point.
- **Why here**: Completes the PdfStructure module. Needed before P7 pipeline integration.
- **Files touched**: `PdfStructure.fs`, `Extraction.fs` (fallback path), `PdfStructureTests.fs`
- **Commit**: `feat: CID detection, confidence scoring, and OCR fallback in PdfStructure`
- **Status**: ⬜

### Step 7 · P7: Pipeline Integration — Replace extractPdfText
- **Plan**: [document-extraction/plan.md](document-extraction/plan.md) → Phase P7
- **Summary**: Wire PdfStructure into Extraction.fs. All new PDFs get structured markdown. Add `extraction_confidence` DB column. `toMarkdown` renderer.
- **Why here**: P1–P6 complete the algorithm. P7 plugs it into the live pipeline. Unlocks C1, C2, P8, P9–P12.
- **Files touched**: `PdfStructure.fs` (toMarkdown), `Extraction.fs`, `Database.fs` (schema migration), tests
- **Commit**: `feat: replace flat PDF extraction with PdfStructure structured markdown pipeline`
- **Status**: ⬜

### Step 8 · F1: hermes_list_documents MCP Tool
- **Plan**: [document-feed/plan.md](document-feed/plan.md) → Phase F1
- **Summary**: New `DocumentFeed.fs` module + `hermes_list_documents` + `hermes_get_feed_stats` MCP tools. Cursor-based pagination.
- **Why here**: No dependency on P1–P7 (uses existing documents.id). Unlocks M2, M3, and Osprey consumer.
- **Files touched**: `+DocumentFeed.fs`, `Hermes.Core.fsproj`, `McpTools.fs`, `McpServer.fs`, `+DocumentFeedTests.fs`
- **Commit**: `feat(mcp): hermes_list_documents and hermes_get_feed_stats cursor-based feed tools`
- **Status**: ⬜

### Step 9 · F2: hermes_get_document_content MCP Tool
- **Plan**: [document-feed/plan.md](document-feed/plan.md) → Phase F2
- **Summary**: `hermes_get_document_content` — text/markdown/raw format retrieval. Markdown format works because P7 is done.
- **Why here**: Needs F1 (same module). P7 makes `format="markdown"` return structured content.
- **Files touched**: `DocumentFeed.fs`, `McpTools.fs`, `McpServer.fs`, `DocumentFeedTests.fs`
- **Commit**: `feat(mcp): hermes_get_document_content with text, markdown, and raw formats`
- **Status**: ⬜

### Step 10 · P8: MCP markdown format integration
- **Plan**: [document-extraction/plan.md](document-extraction/plan.md) → Phase P8
- **Summary**: Verify/extend get_document_content to handle structured markdown from P7. May be mostly done by F2 — verify and test.
- **Why here**: F2 + P7 should cover this. This step is verification + any gap filling.
- **Files touched**: `McpTools.fs` (if needed), tests
- **Commit**: `feat(mcp): hermes_get_document_content with markdown format support`
- **Status**: ⬜

### Step 11 · U1: Four-Column Shell Layout with Chat Pane
- **Plan**: [rich-ui/plan.md](rich-ui/plan.md) → Phase U1
- **Summary**: Replace ShellWindow with VS Code-style 4-column layout. Activity bar + navigator + content + chat pane. ShellViewModel with NavigatorMode.
- **Why here**: Backend extraction + feed tools are complete. Now build the UI foundation all other UI phases need.
- **Files touched**: `ShellWindow.axaml`, `ShellWindow.axaml.cs`, `+ShellViewModel.cs` (or replace), `App.axaml.cs`
- **Commit**: `feat(ui): VS Code-style four-column shell layout with chat pane`
- **Status**: ⬜

### Step 12 · U2: Documents Navigator + Document Detail
- **Plan**: [rich-ui/plan.md](rich-ui/plan.md) → Phase U2
- **Summary**: New `DocumentBrowser.fs`, category tree, document list, detail pane with markdown preview. Wire to HermesServiceBridge.
- **Why here**: Needs U1 shell. P7 gives structured markdown for the detail preview.
- **Files touched**: `+DocumentBrowser.fs`, `Hermes.Core.fsproj`, `+DocumentsNavigator.axaml(.cs)`, `+DocumentDetailView.axaml(.cs)`, `HermesServiceBridge.cs`, tests
- **Commit**: `feat(ui): documents navigator with category tree and document detail pane`
- **Status**: ⬜

### Step 13 · M2: Reminder MCP Tools
- **Plan**: [mcp-platform-api/plan.md](mcp-platform-api/plan.md) → Phase M2
- **Summary**: `hermes_list_reminders` + `hermes_update_reminder` (mark paid, snooze, dismiss) over MCP.
- **Why here**: Needs F1 (MCP infrastructure). Reminders module exists (Doc 12 ✅). Quick win.
- **Files touched**: `McpTools.fs`, `McpServer.fs`, tests
- **Commit**: `feat(mcp): hermes_list_reminders and hermes_update_reminder tools`
- **Status**: ⬜

### Step 14 · C1: Extract-First Pipeline Reorder
- **Plan**: [smart-classification/plan.md](smart-classification/plan.md) → Phase C1
- **Summary**: Reorder ServiceHost: extract BEFORE classify. Extraction includes `unclassified/` files.
- **Why here**: Needs P7 (extraction produces structured markdown). Must happen before C2 (content classification needs extracted text).
- **Files touched**: `ServiceHost.fs`, `Extraction.fs` (include unclassified), tests
- **Commit**: `feat: reorder pipeline — extract before classify, include unclassified files`
- **Status**: ⬜

### Step 15 · C2: Tier 2 Content Rules Engine
- **Plan**: [smart-classification/plan.md](smart-classification/plan.md) → Phase C2
- **Summary**: `ContentClassifier.fs`, content rules in YAML, schema migration for `classification_tier` + `classification_confidence` columns.
- **Why here**: Needs C1 (extraction before classification) + P7 (structured markdown to match on).
- **Files touched**: `Domain.fs`, `+ContentClassifier.fs`, `Hermes.Core.fsproj`, `Config.fs`, `Classifier.fs`, `Database.fs`, tests
- **Commit**: `feat: Tier 2 content-based classification engine with YAML content rules`
- **Status**: ⬜

### Step 16 · C3: Tier 3 LLM Classification
- **Plan**: [smart-classification/plan.md](smart-classification/plan.md) → Phase C3
- **Summary**: LLM classification for documents Tier 1+2 can't handle. Confidence gating. Reasoning stored.
- **Why here**: Needs C2 (fallback path when content rules fail).
- **Files touched**: `ContentClassifier.fs`, `Classifier.fs`, `Config.fs`, tests
- **Commit**: `feat: Tier 3 LLM classification with confidence gating and reasoning`
- **Status**: ⬜

### Step 17 · U6: Chat Pane ↔ Content Pane Integration
- **Plan**: [rich-ui/plan.md](rich-ui/plan.md) → Phase U6
- **Summary**: Chat results link to content pane. Click document card → content pane shows detail. Chat persists while browsing.
- **Why here**: Needs U1 (shell) + U2 helps (document detail renderer). Done before U3/U4 because chat is the most-used feature.
- **Files touched**: `ShellViewModel.cs`, `+ChatPane.axaml(.cs)`, `ShellWindow.axaml.cs`
- **Commit**: `feat(ui): chat pane ↔ content pane integration with clickable document cards`
- **Status**: ⬜

### Step 18 · P9: Excel Extraction (ClosedXML)
- **Plan**: [document-extraction/plan.md](document-extraction/plan.md) → Phase P9
- **Summary**: New `ExcelExtraction.fs`. ClosedXML NuGet. Sheets → markdown tables.
- **Why here**: Needs P7 (shared types). Independent of classification/UI work.
- **Files touched**: `+ExcelExtraction.fs`, `Hermes.Core.fsproj` (Compile + NuGet), tests
- **Commit**: `feat: Excel extraction via ClosedXML — sheets to markdown tables`
- **Status**: ⬜

### Step 19 · P10: Word Extraction (Open XML SDK)
- **Plan**: [document-extraction/plan.md](document-extraction/plan.md) → Phase P10
- **Summary**: New `WordExtraction.fs`. Open XML SDK NuGet. Paragraphs/tables/headings → markdown.
- **Why here**: Independent of P9. Adds another format.
- **Files touched**: `+WordExtraction.fs`, `Hermes.Core.fsproj` (Compile + NuGet), tests
- **Commit**: `feat: Word extraction via Open XML SDK — paragraphs, headings, tables to markdown`
- **Status**: ⬜

### Step 20 · P11: CSV Extraction
- **Plan**: [document-extraction/plan.md](document-extraction/plan.md) → Phase P11
- **Summary**: New `CsvExtraction.fs`. Dialect detection. Markdown table + raw content for MCP `format="raw"`.
- **Why here**: Independent of P9/P10. Enables Osprey CSV parsers (I5).
- **Files touched**: `+CsvExtraction.fs`, `Hermes.Core.fsproj`, tests
- **Commit**: `feat: CSV extraction — dialect detection, markdown table, and raw content via MCP`
- **Status**: ⬜

### Step 21 · P12: Format Dispatch — Unified Pipeline
- **Plan**: [document-extraction/plan.md](document-extraction/plan.md) → Phase P12
- **Summary**: Extension-based routing in Extraction.fs. `.pdf` → PdfStructure, `.xlsx` → ExcelExtraction, `.docx` → WordExtraction, `.csv` → CsvExtraction.
- **Why here**: Needs P7 + P9 + P10 + P11. Ties all extractors together.
- **Files touched**: `Extraction.fs`, tests
- **Commit**: `feat: unified format dispatch — single extraction pipeline for PDF, Excel, Word, CSV, Text`
- **Status**: ⬜

### Step 22 · U3: Email Threads Navigator + Thread Timeline
- **Plan**: [rich-ui/plan.md](rich-ui/plan.md) → Phase U3
- **Summary**: New `Threads.fs`, `thread_summaries` table, thread list in navigator, chronological timeline in content pane, attachment cross-links.
- **Why here**: Needs U1 shell. Independent of U2/U4/U5.
- **Files touched**: `+Threads.fs`, `Hermes.Core.fsproj`, `Database.fs`, `+ThreadsNavigator.axaml(.cs)`, `+ThreadDetailView.axaml(.cs)`, `HermesServiceBridge.cs`, tests
- **Commit**: `feat(ui): email threads navigator with thread timeline and attachment links`
- **Status**: ⬜

### Step 23 · U4: Action Items Navigator + Cross-Navigation
- **Plan**: [rich-ui/plan.md](rich-ui/plan.md) → Phase U4
- **Summary**: Breadcrumb bar, navigation stack, reminder detail with cross-links to documents/threads.
- **Why here**: Needs U1 shell + Reminders module (Doc 12 ✅). Benefits from U2 (document detail) and U3 (thread detail) for cross-navigation targets.
- **Files touched**: `ShellViewModel.cs`, `+ActionItemsNavigator.axaml(.cs)`, `+ReminderDetailView.axaml(.cs)`, `+BreadcrumbBar.axaml(.cs)`, `HermesServiceBridge.cs`
- **Commit**: `feat(ui): action items navigator with cross-navigation and breadcrumb`
- **Status**: ⬜

### Step 24 · M3: Document Management MCP Tools
- **Plan**: [mcp-platform-api/plan.md](mcp-platform-api/plan.md) → Phase M3
- **Summary**: `hermes_reclassify` + `hermes_reextract` + `hermes_get_processing_queue`. New `DocumentManagement.fs`.
- **Why here**: Needs F1 (MCP infra) + C2 (classification_tier for reclassify). Write tools for AI agents.
- **Files touched**: `+DocumentManagement.fs`, `Hermes.Core.fsproj`, `McpTools.fs`, `McpServer.fs`, tests
- **Commit**: `feat(mcp): hermes_reclassify, hermes_reextract, hermes_get_processing_queue tools`
- **Status**: ⬜

### Step 25 · C4: Bulk Reclassification of Unsorted
- **Plan**: [smart-classification/plan.md](smart-classification/plan.md) → Phase C4
- **Summary**: `reclassifyUnsortedBatch` in Classifier.fs. CLI `reclassify-unsorted` command. UI trigger button. Progress reporting.
- **Why here**: Needs C3 (all 3 tiers ready). Processes the 2,988 unsorted backlog.
- **Files touched**: `Classifier.fs`, `Program.fs` (CLI), `ServiceHost.fs`, `HermesServiceBridge.cs`, tests
- **Commit**: `feat: bulk reclassification of unsorted documents via CLI and UI`
- **Status**: ⬜

### Step 26 · C5: Classification Insight UI
- **Plan**: [smart-classification/plan.md](smart-classification/plan.md) → Phase C5
- **Summary**: Classification stats in Stats.fs. Confidence badges on document rows. Tier/confidence/reasoning in detail pane. Filter by classification quality.
- **Why here**: Needs C2 (classification columns) + U2 (document navigator for UI surface).
- **Files touched**: `Stats.fs`, `DocumentsNavigator.axaml(.cs)`, `DocumentDetailView.axaml(.cs)`, tests
- **Commit**: `feat(ui): classification insight — badges, filters, and tier/confidence display`
- **Status**: ⬜

### Step 27 · U5: Timeline + Activity Log
- **Plan**: [rich-ui/plan.md](rich-ui/plan.md) → Phase U5
- **Summary**: `activity_log` table, `ActivityLog.fs`, `Timeline.fs`, wire logging into ServiceHost, timeline/activity navigators.
- **Why here**: Needs U1 shell. ActivityLog.fs also used by C2/C3 classification logging (already done — just adds more log calls).
- **Files touched**: `Database.fs`, `+ActivityLog.fs`, `+Timeline.fs`, `Hermes.Core.fsproj`, `ServiceHost.fs`, `+TimelineNavigator.axaml(.cs)`, `+ActivityNavigator.axaml(.cs)`, tests
- **Commit**: `feat(ui): timeline navigator and activity log with event wiring`
- **Status**: ⬜

### Step 28 · F3: Consumer Protocol Documentation
- **Plan**: [document-feed/plan.md](document-feed/plan.md) → Phase F3
- **Summary**: Document the consumer protocol: connection, polling, cursor management, content retrieval, idempotency, replay.
- **Why here**: All feed tools are done. Document them before Osprey integration.
- **Files touched**: `+.project/design/13-consumer-protocol.md`
- **Commit**: `docs: consumer protocol documentation for Hermes document feed`
- **Status**: ⬜

### Step 29 · I3: Osprey HermesMcpClient + CursorStore
- **Plan**: [osprey-integration/plan.md](osprey-integration/plan.md) → Phase I3
- **Summary**: Python MCP client + SQLite cursor store in Osprey repo.
- **Why here**: Hermes feed tools (F1+F2) must be running. Python-side work begins.
- **Files touched**: Osprey repo: `+hermes_client.py`, `+cursor_store.py`, tests
- **Commit**: `feat(osprey): HermesMcpClient and CursorStore for Hermes feed consumption`
- **Status**: ⬜

### Step 30 · I4: Osprey TaxProcessor Poll Loop
- **Plan**: [osprey-integration/plan.md](osprey-integration/plan.md) → Phase I4
- **Summary**: `TaxProcessor` class — poll Hermes, dispatch to parsers, post events to Pelican Core, save cursor.
- **Why here**: Needs I3 (client + cursor).
- **Files touched**: Osprey repo: `+tax_processor.py`, tests
- **Commit**: `feat(osprey): TaxProcessor poll loop with parser dispatch and cursor management`
- **Status**: ⬜

### Step 31 · I5: Osprey Parser Adaptation
- **Plan**: [osprey-integration/plan.md](osprey-integration/plan.md) → Phase I5
- **Summary**: Adapt 7 parsers from `extract(file_path)` to `extract_from_content(content, metadata)`.
- **Why here**: Needs I4 (processor that calls parsers) + P8 (structured markdown available via MCP).
- **Files touched**: Osprey repo: all 7 parser files, tests
- **Commit**: `feat(osprey): adapt all 7 tax parsers from file-based to content-based extraction`
- **Status**: ⬜

### Step 32 · I6: Osprey Collector Deletion
- **Plan**: [osprey-integration/plan.md](osprey-integration/plan.md) → Phase I6
- **Summary**: Delete old collector code. Update Aspire host to start TaxProcessor instead.
- **Why here**: Needs I5 (all parsers adapted). Only delete after parsers work on Hermes content.
- **Files touched**: Osprey repo: delete `collector/watcher.py`, `pipeline.py`, `store.py`; update host config
- **Commit**: `refactor(osprey): remove collector, wire TaxProcessor as primary document source`
- **Status**: ⬜

### Step 33 · I7: End-to-End Integration Test
- **Plan**: [osprey-integration/plan.md](osprey-integration/plan.md) → Phase I7
- **Summary**: Email → Hermes → classify → extract → Osprey polls → parser runs → TaxEvent → Pelican Core → dashboard.
- **Why here**: Everything must work. This is the final verification of the entire platform.
- **Files touched**: Osprey repo: `+tests/integration/test_hermes_to_osprey.py`
- **Commit**: `test(integration): end-to-end Hermes → Osprey → Pelican Core pipeline tests`
- **Status**: ⬜

### Step 34 · M4: Alerts + Confirmation Queue (DEFERRED)
- **Plan**: [mcp-platform-api/plan.md](mcp-platform-api/plan.md) → Phase M4
- **Summary**: `hermes_create_alert` + email confirmation queue. Future work — Doc 10 (Agent Evolution).
- **Why here**: No other step depends on this. Build when agent evolution design is finalised.
- **Status**: ⏸️ Deferred

---

## Cross-Plan Shared Work (avoid duplication)

These items are referenced by multiple specs. Each is implemented exactly once, at the step listed.

| Shared Work | Step | Also Satisfies |
|-------------|------|----------------|
| `hermes_list_documents` + `hermes_get_document_content` | Steps 8–9 (F1+F2) | Doc 14 M1, Doc 16 I1 |
| Structured PDF→Markdown pipeline | Step 7 (P7) | Doc 16 I2, Doc 18 C2 input |
| `activity_log` table | Step 27 (U5) | Doc 18 C2/C3 logging uses it |
| `classification_tier` + `classification_confidence` columns | Step 15 (C2) | Doc 15 U2 displays them |
| `DocumentBrowser.fs` queries | Step 12 (U2) | Used by C5, U4 cross-nav |

## File Conflict Map (why sequential matters)

| File | Steps that touch it |
|------|-------------------|
| `Hermes.Core.fsproj` | 1, 8, 12, 15, 18, 19, 20, 22, 24, 27 (10 steps — F# ordering!) |
| `McpTools.fs` | 8, 9, 10, 13, 24 |
| `McpServer.fs` | 8, 9, 10, 13, 24 |
| `Database.fs` | 7, 15, 22, 27 |
| `Extraction.fs` | 6, 7, 14, 21 |
| `ServiceHost.fs` | 14, 25, 27 |
| `ShellWindow.axaml(.cs)` | 11, 17, 23 |
| `HermesServiceBridge.cs` | 12, 22, 23, 25 |
| `Classifier.fs` | 15, 16, 25 |
| `Config.fs` | 15, 16 |
| `Stats.fs` | 26 |

Any two steps touching the same file would conflict in parallel branches. Sequential execution eliminates this entirely.

---

## Progress Summary

| Status | Count |
|--------|-------|
| ⬜ Not started | 33 |
| ✅ Complete | 0 |
| ⏸️ Deferred | 1 |
| **Total** | **34** |
