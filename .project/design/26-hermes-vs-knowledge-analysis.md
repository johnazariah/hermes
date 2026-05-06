# 26 — Hermes vs Knowledge: Comparative Analysis

> Reference: [matvelloso/knowledge](https://github.com/matvelloso/knowledge) — a privacy-first local knowledge builder
> Date: May 2026

## Overview

Both Hermes and Knowledge are local-first document intelligence systems that ingest documents from email and local folders, process them through LLM comprehension, and make knowledge searchable. They share the same thesis: your documents should be understood and indexed on your machine, not uploaded to a cloud service.

This analysis compares their architectures after Hermes adopted several ideas from Knowledge.

## Architecture Comparison

| Dimension | Hermes | Knowledge |
|-----------|--------|-----------|
| **Runtime** | .NET 10 / F# | Electron / JavaScript |
| **UI** | React 19 + Vite (web) | Vue 2 + Electron (desktop) |
| **Database** | SQLite + FTS5 + sqlite-vec | SQLite (vectors as BLOBs) + JSON dataCenter |
| **LLM** | Ollama (llama3:8b + nomic-embed-text) | Ollama (gemma4 + nomic-embed-text) |
| **Email** | Gmail + Outlook (Graph API) | Gmail + Outlook (Graph API) |
| **Pipeline** | Channel\<Document\> stages with workflow monad | Monolithic orchestrator loop |
| **Architecture** | Tagless-Final (records of functions) | Service classes with side effects |
| **API** | MCP server (streamable HTTP) | None (trapped in Electron) |
| **Extraction** | PDF, DOCX, XLSX, PPTX, CSV, plain text, images | PDF, DOCX, XLSX, PPTX, RTF |
| **Type safety** | F# discriminated unions, Result types | Untyped JavaScript |
| **Tests** | 833 (xUnit + FsCheck) | Minimal (Karma + Mocha) |
| **LOC (core)** | ~10,000 F# | ~7,500 JS |
| **LOC (tests)** | ~10,500 F# | ~minimal |

## Feature Parity

| Feature | Hermes | Knowledge | Notes |
|---------|--------|-----------|-------|
| Email sync (Gmail) | ✅ | ✅ | Both use Gmail API |
| Email sync (Outlook) | ✅ (new) | ✅ | Both use Graph API. Hermes added immutable IDs. |
| Document extraction | ✅ 7 types | ✅ 6 types | Hermes has image OCR, Knowledge has RTF |
| LLM comprehension | ✅ structured JSON | ✅ synthesis + classification | Hermes produces typed fields; Knowledge produces prose |
| Retrieval-augmented comprehension | ✅ schema hints | ✅ full examples | Hermes passes field names only (no value contamination) |
| Semantic search | ✅ FTS5 + sqlite-vec | ✅ BLOB vectors + keyword overlap | Hermes uses native extensions; Knowledge does cosine in JS |
| Knowledge graph | ❌ | ✅ multi-edge graph | Knowledge has explicit links, tag co-membership, semantic edges |
| Email style profiling | ❌ | ✅ | Knowledge learns greeting/signoff/tone per correspondent |
| Managed markdown | ❌ | ✅ | Knowledge generates topic notes in `knowledge/` folder |
| MY PREFERENCES.md | ✅ (adapted) | ✅ | Hermes stores in config, injects into prompt context |
| Learned patterns | ✅ (new) | ❌ | Hermes accumulates sender→type mappings from comprehension |
| Suggestion approval | ✅ (new) | ✅ | Knowledge calls it "review queue"; similar concept |
| MCP server | ✅ | ❌ | Hermes exposes tools for downstream consumers (Osprey) |
| External API | ✅ REST + MCP | ❌ | Knowledge is Electron-only, no external access |
| Pipeline idempotency | ✅ workflow monad | ⚠️ manual | Hermes has `runStage` with output key check; Knowledge reconciles on startup |
| Folder watching | ✅ chokidar-style | ✅ chokidar | Both watch local folders |
| Contact extraction | ✅ | ❌ | Hermes harvests contacts from comprehension output |
| Reminders | ✅ | ❌ | Hermes creates reminders from due dates in documents |
| Deep extraction (Pass 2) | ✅ | ❌ | Type-specific re-extraction with richer prompts |

## Where Knowledge is still ahead

1. **Knowledge graph** — multi-edge topic graph with LLM-driven clustering. Hermes has learned_patterns but no graph structure connecting documents by topic, link, or semantic similarity.

2. **Email style profiling** — learns per-correspondent writing patterns for reply drafting. Useful for Pelican integration.

3. **Generated markdown artifacts** — topic notes, project summaries, daily digests as readable files. Hermes keeps everything in SQLite (machine-friendly but not human-browsable).

4. **Calendar integration** — Knowledge has Google Calendar and Microsoft Calendar adapters.

## Where Hermes is ahead

1. **Type safety** — F# discriminated unions, Result types, and the Tagless-Final architecture catch errors at compile time. Knowledge's untyped JavaScript relies on runtime discipline.

2. **Pipeline architecture** — Channel\<Document\> with workflow monad provides backpressure, idempotency, GPU resource locking, and error isolation per stage. Knowledge's monolithic orchestrator is harder to test and reason about.

3. **MCP server** — Hermes exposes structured data to downstream consumers. Knowledge traps knowledge inside Electron.

4. **Test coverage** — 833 tests including property-based tests. Knowledge has minimal test infrastructure.

5. **Structured comprehension** — Hermes produces typed JSON with `document_type`, `fields`, `confidence`. Knowledge produces prose summaries that require further parsing.

6. **RAC safety** — Hermes passes schema hints only (field names, no values) to avoid contaminating extraction with stale data. Knowledge passes full previous examples.

7. **Suggestion pipeline** — Hermes creates reviewable suggestions for low-confidence comprehensions with approval/rejection that reinforces learned patterns. Feedback loop is explicit.

## What we adopted

| Knowledge pattern | How Hermes adapted it |
|---|---|
| MY PREFERENCES.md | `Preferences` config field, injected into LLM prompt context |
| Microsoft Outlook adapter | `OutlookProvider.fs` with immutable IDs and typed algebra |
| PPTX extraction | `PptxExtraction.fs` using Open XML SDK (not shell `unzip`) |
| Past comprehension as examples | Schema hints only (document_type + field_names, no values) |
| Review queue / suggestions | `suggestions` table with approve/reject + learned pattern reinforcement |
| Managed sections concept | `learned_patterns` table (agent knowledge, separate from user preferences) |

## What we deliberately didn't adopt

| Knowledge pattern | Why not |
|---|---|
| Markdown editor as UI | Not accessible to non-technical users (the mother-in-law test) |
| Full previous examples in prompt | Risk of value contamination — stale amounts/dates copied into new docs |
| Knowledge graph | Premature — learned_patterns covers the most valuable case (sender→type) |
| Email style profiling | Future — useful for Pelican but not core Hermes value |
| Calendar integration | Future — not part of document intelligence core |
| Vue 2 + Electron | Dead stack; React 19 + Vite is the right choice |

## Conclusion

Knowledge validated Hermes' core thesis and contributed several valuable patterns. The most impactful adoptions were the Outlook provider (doubling email sources), retrieval-augmented comprehension (improving classification consistency), and the suggestion/feedback loop (closing the learning cycle). Hermes' architecture is more principled and testable, but Knowledge's user-facing features (knowledge graph, generated markdown, email profiling) point to interesting future directions.
