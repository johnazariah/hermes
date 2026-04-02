# Master Plan Execution Report

**Date**: 2026-04-01 → 2026-04-02
**Branch**: `feat/master-plan-execution` (21 commits, merged to master as `cca20b4`)
**Scope**: 44 files changed, +3,686 / −872 lines

---

## Summary

Executed 22 of 33 master plan steps in a single session. All backend infrastructure for document extraction, MCP platform API, smart classification, and multi-format support is now on master. UI XAML has a working 4-column shell layout foundation.

---

## Steps Completed

### Document Extraction (Steps 1–7, 18–21)

| Step | Phase | Commit | What was built |
|------|-------|--------|----------------|
| 1 | P1 | `15671b7` | `PdfStructure.fs` — letter extraction + line clustering from PdfPig |
| 2 | P2 | `5d4d676` | Heading detection — font size (H1 ≥1.5×), bold (H2), all-caps (H3) |
| 3 | P3 | `987a1f0` | Table detection — column boundary clustering, cell extraction, region scanning |
| 4 | P4 | `f33a672` | Key-value pair detection — colon-separated and gap-separated patterns |
| 5 | P5 | `b6491c8` | Multi-page table continuation — merge tables with matching column headers |
| 6 | P6 | `517f756` | CID detection, confidence scoring (0.0–1.0), `extractStructured` entry point |
| 7 | P7 | `6c09f02` | `toMarkdown` renderer, `extractPdfContent` in Extraction.fs, `extraction_confidence` DB column |
| 18 | P9 | `ee1ea30` | `ExcelExtraction.fs` — ClosedXML sheets → markdown tables |
| 19 | P10 | `ee1ea30` | `WordExtraction.fs` — Open XML SDK paragraphs/headings/tables → markdown |
| 20 | P11 | `ee1ea30` | `CsvExtraction.fs` — dialect detection, quoted field parsing → markdown table |
| 21 | P12 | `ee1ea30` | Unified format dispatch: `.pdf`/`.xlsx`/`.docx`/`.csv`/`.txt` → single pipeline |

**New NuGet packages**: ClosedXML 0.104.2, DocumentFormat.OpenXml 3.2.0

### MCP Platform API (Steps 8–10, 13, 24)

| Step | Phase | Commit | What was built |
|------|-------|--------|----------------|
| 8 | F1 | `6666d03` | `DocumentFeed.fs` + `hermes_list_documents` + `hermes_get_feed_stats` MCP tools |
| 9 | F2 | `6666d03` | `hermes_get_document_content` — text/markdown/raw format retrieval |
| 10 | P8 | `4be0e70` | End-to-end MCP test verifying structured markdown through tool dispatch |
| 13 | M2 | — | Already existed: `hermes_list_reminders` + `hermes_update_reminder` |
| 24 | M3 | `2a8f7c7` | `DocumentManagement.fs` + `hermes_reclassify`, `hermes_reextract`, `hermes_get_processing_queue` |

**MCP tool count**: 7 → 13

### Smart Classification (Steps 14–16, 25)

| Step | Phase | Commit | What was built |
|------|-------|--------|----------------|
| 14 | C1 | `060e0b3` | Extract-first pipeline reorder: Sync → Extract → Classify → Embed |
| 15 | C2 | `8082b3f` | `ContentClassifier.fs` — Tier 2 content rules engine with ContentMatch DU |
| 16 | C3 | `c099a46` | Tier 3 LLM classification — prompt building (2000 char truncation) + JSON response parsing |
| 25 | C4 | `8a8a263` | `reclassifyUnsortedBatch` in Classifier.fs — batch Tier 2 reclassification |

**New Domain types**: `ContentMatch` DU (7 cases), `ContentRule` record

### UI Foundation (Steps 11–12, 22, 27)

| Step | Phase | Commit | What was built |
|------|-------|--------|----------------|
| 11 | U1 | `f0a984e` | 4-column VS Code-style shell layout — activity bar (48px) + navigator (260px) + splitter + content area. ShellViewModel: `NavigatorMode`, nav stack, `ToggleChatPane`. 5 nav icon buttons wired to `SetActiveMode()`. |
| 12 | U2 | `80fc3b6` | `DocumentBrowser.fs` — `listCategories`, `listDocuments`, `getDocumentDetail` with `PipelineStatus` |
| 22 | U3 | `456c44b` | `Threads.fs` — thread grouping by `thread_id`, `listThreads`, `getThreadDetail` with attachment doc IDs |
| 27 | U5 | `456c44b` | `ActivityLog.fs` — append-only event log, `activity_log` table, read/write operations |

### Documentation (Step 28)

| Step | Phase | Commit | What was built |
|------|-------|--------|----------------|
| 28 | F3 | `4d21aea` | `.project/design/13-consumer-protocol.md` — connection, poll loop, cursor management, idempotency, error handling |

### Pre-requisite Fix

| Commit | What |
|--------|------|
| `4838ea0` | Fixed 12 pre-existing test failures — hoisted `norm` function in `memFs`, added `Norm` field to `MemFs` record, piped `Path.Combine` through `m.Norm` in all test dictionary accesses |

---

## New Files Created (10 F# modules + 3 test files + 1 doc)

| File | Lines | Purpose |
|------|-------|---------|
| `src/Hermes.Core/PdfStructure.fs` | 461 | PDF structure extraction: letters → words → lines → blocks → markdown |
| `src/Hermes.Core/DocumentFeed.fs` | 172 | Cursor-based document feed + content retrieval |
| `src/Hermes.Core/ContentClassifier.fs` | 124 | Tier 2 content rules + Tier 3 LLM prompt/parse |
| `src/Hermes.Core/ExcelExtraction.fs` | 40 | ClosedXML → markdown tables |
| `src/Hermes.Core/WordExtraction.fs` | 67 | Open XML SDK → markdown |
| `src/Hermes.Core/CsvExtraction.fs` | 53 | Dialect detection + quoted field parsing |
| `src/Hermes.Core/DocumentManagement.fs` | 107 | Reclassify, reextract, processing queue |
| `src/Hermes.Core/DocumentBrowser.fs` | 104 | Category tree, document list, detail queries |
| `src/Hermes.Core/Threads.fs` | 137 | Email thread grouping and timeline |
| `src/Hermes.Core/ActivityLog.fs` | 107 | Append-only event log |
| `tests/Hermes.Tests/PdfStructureTests.fs` | 399 | 25 tests for PDF extraction |
| `tests/Hermes.Tests/DocumentFeedTests.fs` | 177 | 8 tests for feed/content |
| `tests/Hermes.Tests/ContentClassifierTests.fs` | 128 | 11 tests for classification |
| `.project/design/13-consumer-protocol.md` | 91 | Consumer protocol documentation |

---

## Schema Changes

| Column | Table | Type | Migration |
|--------|-------|------|-----------|
| `extraction_confidence` | documents | REAL | Core schema + v2→v3 |
| `classification_tier` | documents | TEXT | Core schema + v2→v3 |
| `classification_confidence` | documents | REAL | Core schema + v2→v3 |
| `activity_log` (table) | — | — | v2→v3 migration |

---

## fsproj Compilation Order Changes

`PdfStructure.fs` moved before `Extraction.fs` (P7 dependency). `Classifier.fs` moved after `ContentClassifier.fs` and `DocumentManagement.fs` (C4 dependency). New modules inserted in dependency order.

---

## Test Metrics

| Metric | Before | After | Delta |
|--------|--------|-------|-------|
| Total tests | 363 | 416 | +53 |
| Passed | 363 | 416 | +53 |
| Failed | 0 | 0 | — |
| Skipped | 2 | 2 | — |

---

## Merge Conflicts Resolved

6 files conflicted during merge to master (master had parallel refactoring commits):
- `ShellWindow.axaml` / `.cs` — took feature branch's 4-column layout
- `ServiceHost.fs` — kept master's decomposed functions, preserved extract-before-classify ordering
- `ClassifierTests.fs`, `ConfigTests.fs`, `ServiceTests.fs` — combined master's `m.Put` usage with feature branch's `|> m.Norm` normalization

---

## Discovered Work (not in original plan)

1. **XAML wiring for mode-specific navigator panels**: The activity bar switches `NavigatorTitle` text but doesn't yet swap the navigator content. Each mode needs its own UserControl (DocumentsNavigator, ThreadsNavigator, etc.) loaded into the navigator column based on `ActiveMode`.

2. **Content rules YAML parsing**: `ContentClassifier.classify` accepts `ContentRule list` but `Config.fs` doesn't yet parse `content_rules:` from `rules.yaml`. Need a YAML→ContentRule parser.

3. **LLM classification wiring**: `ContentClassifier.buildClassificationPrompt` and `parseClassificationResponse` exist but aren't called from the pipeline yet. Need to wire into `Classifier.processFile` as Tier 3 fallback after Tier 2 fails.

4. **ExcelExtraction/WordExtraction tests**: No dedicated test files were created for P9/P10. The modules are exercised through the unified extraction dispatch but need unit tests with generated test files.

5. **CsvExtraction tests**: Similarly no dedicated test file — needs `parseCsvLine` and `detectDelimiter` unit tests.

6. **Activity log integration**: `ActivityLog.fs` module exists but isn't called from `ServiceHost.runSyncCycle` yet. Need to add `logInfo`/`logWarning` calls at key pipeline points.

7. **Remote branch cleanup**: `origin/feat/master-plan-execution` still exists on GitHub.
