---
description: "Master development plan: wire the built-but-not-connected pipeline, fix Tagless-Final violations, complete UI, reach 85% coverage."
---

# Hermes — Development Plan (April 3, 2026)

## Current State

- 461 tests on master, 699 on feat/coverage-85 (85.5% line)
- 13 MCP tools
- 4-column VS Code-style UI shell with navigator panels wired
- PdfStructure, Excel, Word, CSV extractors built but **not connected to pipeline**
- Content rules + LLM classification built but **not wired**
- 4 Tagless-Final violations remaining
- 2,988 documents stuck in unsorted (classification can't see content)
- Pelican GL integration planned (replaces Osprey)

## Priority Order

Everything chains from one critical wire: **connect the structured extractors to the actual processing pipeline.** Once documents have structured markdown, classification works, the UI can render previews, and Pelican can consume the feed.

---

## Wave 1: Tagless-Final Cleanup (prerequisite)

**Branch**: `feat/tagless-final-cleanup` (use worktree)
**Prompt**: `.github/prompts/agent3-tagless-final-refactor.prompt.md`
**Scope**: 4 remaining violations

| Task | File             | Fix                                                                                                                                    |
| ---- | ---------------- | -------------------------------------------------------------------------------------------------------------------------------------- |
| T1   | ServiceHost.fs   | Move `buildProductionDeps` to composition roots (Program.fs, HermesServiceBridge.cs). ServiceHost receives all algebras as parameters. |
| T2   | ServiceHost.fs   | `requestSync` → take `fs: FileSystem` + `clock: Clock` parameters                                                                      |
| T3   | Embeddings.fs    | `ollamaClient` → accept `HttpClient` as parameter, don't construct                                                                     |
| T4   | GmailProvider.fs | Add `TokenStore` algebra. `create` takes pre-authenticated credential or token store.                                                  |

**Merge gate**: `grep -rn "new HttpClient\|File.WriteAllText\|FileDataStore" src/Hermes.Core/ --include="*.fs"` returns only hits in `Algebra.fs` Interpreters.

**Why first**: Every subsequent wave touches ServiceHost and composition roots. Get the architecture right before building on it.

---

## Wave 2: Wire Structured Extraction Pipeline (the critical path)

**Branch**: `feat/structured-extraction-pipeline`

This is the single most impactful change. Everything downstream depends on it.

| Task | What                                                                                                                                                                                           | Silver Thread                                                                                                        |
| ---- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------- |
| E1   | Add `extracted_markdown` column to `documents` table (schema v4 migration)                                                                                                                     | DB stores structured markdown alongside flat text                                                                    |
| E2   | Wire `Extraction.fs` → format dispatch → PdfStructure/Excel/Word/CSV extractors for NEW documents                                                                                              | File ingested → extracted → `extracted_markdown` populated with tables/headings/KV → visible in document detail pane |
| E3   | Wire `hermes_get_document_content(format="markdown")` to read `extracted_markdown` column                                                                                                      | MCP consumer calls tool → gets structured markdown with tables                                                       |
| E4   | Re-extraction pipeline: `hermes reextract-all` CLI command that clears `extracted_at` + `extracted_markdown` on all documents, triggering re-extraction with new extractors on next sync cycle | User runs command → 2,163+ documents gradually re-extracted with structure                                           |
| E5   | Document detail pane in UI renders `extracted_markdown` (basic markdown → styled TextBlocks: headings bold, tables as grids, KV as label/value rows)                                           | Click document in navigator → content pane shows formatted preview with tables                                       |

**Merge gate**: Drop a PDF with tables → sync cycle → `extracted_markdown` has `| col | col |` table syntax → MCP returns it → UI renders it.

---

## Wave 3: Wire Smart Classification (unblock the 2,988)

**Branch**: `feat/smart-classification-wiring`

The classification engine exists. Three wires to connect.

| Task | What                                                                                                | Silver Thread                                                                                           |
| ---- | --------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------- |
| C1   | Parse `content_rules:` from `rules.yaml` in Config.fs/Rules.fs → pass to Classifier pipeline        | Add content rules to YAML → document matching keywords → classified by content tier                     |
| C2   | Wire LLM classification as Tier 3 fallback in Classifier.processFile (using `ChatProvider` algebra) | Ambiguous document → Tier 1 fails → Tier 2 fails → LLM classifies → `classification_tier = 'llm'` in DB |
| C3   | `hermes reclassify-unsorted` CLI command triggers batch Tier 2+3 on all unsorted documents          | Run command → unsorted count drops from 2,988 → documents appear in correct categories                  |
| C4   | Classification insight in document detail pane: show tier + confidence + LLM reasoning              | Click document → see "Classified by: LLM (87%) — insurance renewal notice"                              |

**Merge gate**: Run `hermes reclassify-unsorted` → unsorted count decreases significantly → reclassified documents have `classification_tier` and `classification_confidence` set.

---

## Wave 4: Complete UI Silver Threads

**Branch**: `feat/ui-completion`

The shell layout works. The navigator panels are wired. The content pane needs enrichment.

| Task | What                                                                                                                                                   | Silver Thread                                                                    |
| ---- | ------------------------------------------------------------------------------------------------------------------------------------------------------ | -------------------------------------------------------------------------------- |
| U1   | Document detail pane: metadata grid + structured markdown preview + pipeline status dots + action buttons (Open File, Reclassify dropdown, Re-extract) | Click document → see full detail → click Reclassify → pick category → file moves |
| U2   | Thread timeline in content pane: chronological messages with attachment links                                                                          | Click thread → see messages in order → click 📎 → navigate to document           |
| U3   | Activity log integration: `ActivityLog.logEvent` calls in ServiceHost at sync/classify/extract/embed/reminder/error points                             | Run sync → Activity mode shows timestamped events                                |
| U4   | Action Items content pane: full reminder detail with document link + thread link                                                                       | Click reminder → see bill detail → click document → see the invoice              |
| U5   | Empty states for all modes (no documents, no threads, no activity, no reminders)                                                                       | Launch fresh → each mode shows helpful "get started" message                     |

**Merge gate**: Launch app → click through all 5 modes → each renders live data → cross-navigation works (document → thread → reminder → back).

---

## Wave 5: Coverage to 85%+ and Test Quality

**Branch**: `feat/coverage-final`

If feat/coverage-85 isn't merged yet, rebase it. Otherwise start from master.

| Task | What                                                                                                 |
| ---- | ---------------------------------------------------------------------------------------------------- |
| V1   | Merge/rebase feat/coverage-85 (699 tests, 85.5% line) onto current master                            |
| V2   | Add tests for any new code from Waves 2–4 (extraction pipeline, classification wiring, UI ViewModel) |
| V3   | Fix the 2 skipped MCP reminder tests                                                                 |
| V4   | Achieve ≥85% line AND ≥85% branch on Hermes.Core                                                     |

**Merge gate**: `dotnet test --collect:"XPlat Code Coverage"` → ≥85% line, ≥85% branch.

---

## Wave 6: Pelican Integration (Document Feed → GL via REST API)

**Branch**: `feat/pelican-integration`

Pelican (https://github.com/johnazariah/pelican) exposes a REST API, MCP servers, and could subsume Hermes directly for tighter integration. **For now: use the REST API connection.** This keeps Hermes and Pelican as independent services that communicate via HTTP.

Integration options (for context):

- **REST API** (chosen for v1) — Hermes posts tax events to Pelican's `/api/events/*` endpoints
- **MCP** (future) — Pelican calls Hermes MCP tools to pull documents on demand
- **Subsume** (future) — Hermes becomes a Pelican module, sharing process and DB

| Task | What                                                                                                                                                                                     |
| ---- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| P1   | Add `Algebra.GlClient` record: `{ postEvent: string -> Map<string,obj> -> Task<Result<string,string>> }` — abstracts the Pelican REST API call                                           |
| P2   | Implement `PelicanClient` interpreter: HTTP POST to `{pelicanUrl}/api/events/{eventType}` with JSON payload, return journal ID                                                           |
| P3   | Add `pelican:` section to `config.yaml`: `base_url`, `enabled`, `auto_post_threshold` (confidence gate)                                                                                  |
| P4   | Build `TaxEventDetector.fs` — runs after extraction, detects tax-relevant documents (payslips, invoices, bank statements), produces typed event payloads matching Pelican's API contract |
| P5   | Wire into pipeline: extract → classify → detect tax events → POST to Pelican (if enabled + above confidence threshold)                                                                   |
| P6   | End-to-end: payslip PDF → Hermes ingests → extracts structured markdown → detects salary event → POSTs to Pelican `/api/events/employment/salary` → journal created                      |

**Merge gate**: Drop a payslip PDF → Hermes extracts → tax event detected → Pelican API called → journal ID returned and logged in activity.

---

## Wave 7: Polish and Production Readiness

| Task | What                                                                      |
| ---- | ------------------------------------------------------------------------- |
| R1   | Rename `master` → `main`                                                  |
| R2   | CI pipeline with build + test + coverage gate                             |
| R3   | README badges auto-updated from CI (test count, coverage %, build status) |
| R4   | Thread timeline LLM summaries (using ChatProvider algebra)                |
| R5   | macOS installer (.app bundle, LaunchAgent)                                |
| R6   | Dark mode toggle in settings                                              |
| R7   | Keyboard navigation in navigator (arrow keys, Enter, Escape)              |

---

## Dependency Graph

```
Wave 1 (Tagless-Final)
  ↓
Wave 2 (Structured Extraction) ← critical path, everything depends on this
  ↓
Wave 3 (Smart Classification) ← needs extracted markdown from Wave 2
  ↓
Wave 4 (UI Completion) ← needs classification + extraction to show
  ↓
Wave 5 (Coverage) ← tests for all new code from Waves 2-4
  ↓
Wave 6 (Pelican) ← needs structured feed from Wave 2 + classification from Wave 3
  ↓
Wave 7 (Polish)
```

Waves 1 and 2 are sequential (Wave 2 needs clean architecture from Wave 1).
Waves 3 and 4 can partially overlap (classification doesn't block UI layout, just content quality).
Wave 5 runs after 2–4.
Wave 6 can start once Wave 2 is done (doesn't need classification or UI).
Wave 7 is independent.

---

## Agent Assignment Strategy

| Wave | Agent                         | Branch                                | Worktree               |
| ---- | ----------------------------- | ------------------------------------- | ---------------------- |
| 1    | `@fsharp-dev`                 | `feat/tagless-final-cleanup`          | `../hermes-tagless`    |
| 2    | `@fsharp-dev`                 | `feat/structured-extraction-pipeline` | `../hermes-extraction` |
| 3    | `@fsharp-dev`                 | `feat/smart-classification-wiring`    | `../hermes-classify`   |
| 4    | `@csharp-dev` + `@fsharp-dev` | `feat/ui-completion`                  | `../hermes-ui`         |
| 5    | Any                           | `feat/coverage-final`                 | `../hermes-coverage`   |
| 6    | In Pelican repo               | —                                     | —                      |
| 7    | Any                           | `feat/polish`                         | `../hermes-polish`     |

Each wave merges to master before the next starts (except where overlap is noted).
