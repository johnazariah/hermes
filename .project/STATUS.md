# Hermes — Project Status

> **Canonical status hub.** Tiny by design — points to design docs for detail.
> Updated at each checkpoint. Agents: read this first.

## Current State (April 14, 2026)

| Metric | Value |
|--------|-------|
| Architecture | Pipeline v4 — channels, property bags, workflow monad |
| Tests | 700 (694 F# passing, 6 skipped) + 9 Playwright |
| Branch | `v3-clean` |
| Pipeline | ingest → extract → comprehend → embed |
| UI | React 19 five-page app (Pipeline, Documents, Search, Chat, Settings) |
| Models | llama3:8b (comprehension), nomic-embed-text (embeddings) |
| Documents processed | 4,000+ (dev, 90-day email sync) |

## Active Work

**Comprehension stage** — replace coarse classification with LLM-driven structured understanding. This is Hermes' core value and the critical path for Osprey integration.

See: [24-comprehension-stage.md](design/24-comprehension-stage.md)

## Architecture

See: [23-pipeline-v4-architecture.md](design/23-pipeline-v4-architecture.md)

Key principles:
- **Document = Map<string, obj>** — property bag, typed access via `decode<T>`
- **Channel<Document>** — runtime flow, no polling, no SQLite queues
- **Workflow.runStage** — generic monad (idempotency, write-aside, error handling)
- **Files never move** — `saved_path` is immutable, category is metadata
- **Comprehension replaces classification** — understanding produces type + fields as byproducts
- **GPU resource lock** — SemaphoreSlim burst-hold for Ollama model contention

## Roadmap

| Priority | Item | Status |
|----------|------|--------|
| 🔴 | Comprehension stage | Design complete, implementation next |
| 🔴 | Osprey integration via MCP | Blocked on comprehension |
| 🟡 | Search + Chat testing with live data | UI exists, untested |
| 🟡 | Testing register regeneration | 258 listed vs 700 actual |
| 🟢 | Tray app / browser wrap | Future |
| 🟢 | CI/CD release pipeline | Future |

## Completed Waves (historical)

| Wave | Name | Status |
|------|------|--------|
| 1 | Backfill + Reminders | ✅ |
| 1a | Tagless-Final | ✅ |
| 1b | Coverage | ✅ |
| 1.5 | Osprey Parity | ✅ |
| 2 | Extraction | ✅ |
| 3 | Classification | ✅ (superseded by comprehension) |
| 4/4b | Avalonia UI | ✅ (superseded by React) |
| 5.5 | UI Testing | ✅ (superseded by Playwright) |
| **v4** | **Channel pipeline + React UI** | **✅ Committed: 5dff01b** |

## Key Design Docs

| Doc | Topic | Status |
|-----|-------|--------|
| [**23**](design/23-pipeline-v4-architecture.md) | **Pipeline v4 Architecture** | Current |
| [**24**](design/24-comprehension-stage.md) | **Comprehension Stage** | Design |
| [17](design/17-pdf-to-markdown.md) | PDF Extraction | Current |
| [13](design/13-document-feed-and-consumers.md) | Document Feed & Consumers | Current |
| [10](design/10-agent-evolution.md) | Agent Evolution (triggers/skills) | Aspirational |
| [22](design/22-smart-tagging-review-queue.md) | Smart Tagging & Review Queue | Aspirational |

## Blockers

None.
