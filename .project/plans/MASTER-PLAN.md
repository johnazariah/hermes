---
description: "Master execution plan showing dependencies, parallelisation opportunities, and recommended build order across all 6 feature specs."
---

# Hermes — Master Execution Plan

## Feature Plans

| Spec | Plan | Phases | Silver Thread? |
|------|------|--------|---------------|
| Doc 15: Rich UI | [rich-ui/plan.md](rich-ui/plan.md) | U1–U6 | ✅ Every phase |
| Doc 17: Document Extraction | [document-extraction/plan.md](document-extraction/plan.md) | P1–P12 | ✅ Every phase |
| Doc 18: Smart Classification | [smart-classification/plan.md](smart-classification/plan.md) | C1–C5 | ✅ Every phase |
| Doc 13: Document Feed | [document-feed/plan.md](document-feed/plan.md) | F1–F4 | ✅ Every phase |
| Doc 14: MCP Platform API | [mcp-platform-api/plan.md](mcp-platform-api/plan.md) | M1–M4 | ✅ Every phase |
| Doc 16: Osprey Integration | [osprey-integration/plan.md](osprey-integration/plan.md) | I1–I7 | ✅ Every phase |

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

## Recommended Execution Order

### Wave 1: Foundation (no cross-dependencies)

These can run in **parallel** — they have no dependencies on each other:

| Track | Phases | What | Effort |
|-------|--------|------|--------|
| **UI Foundation** | U1 | Four-column shell layout | Large |
| **PDF Extraction** | P1, P2, P3, P4 | Core PDF structural extraction | Large |
| **Feed Tools** | F1, F2 (= M1) | Document feed MCP tools | Medium |

### Wave 2: Integration (depends on Wave 1)

| Track | Phases | Depends on | What | Effort |
|-------|--------|------------|------|--------|
| **PDF Advanced** | P5, P6 | P1–P4 | Multi-page tables, CID fallback | Medium |
| **Pipeline Integration** | P7 | P1–P6 | Replace extractPdfText with structured pipeline | Medium |
| **UI Documents** | U2 | U1 | Documents navigator + detail pane | Large |
| **UI Chat** | U6 | U1 | Chat ↔ content pane integration | Medium |
| **Reminder MCP** | M2 | F1 (M1) | Reminder tools over MCP | Medium |

### Wave 3: Smart Processing (depends on Wave 2)

| Track | Phases | Depends on | What | Effort |
|-------|--------|------------|------|--------|
| **Classification** | C1, C2, C3 | P7 | Extract-first pipeline + 3-tier classification | Large |
| **Format Support** | P9, P10, P11, P12 | P7 | Excel, Word, CSV extraction | Medium |
| **MCP Content** | P8 | P7 | format="markdown" via MCP | Small |
| **UI Threads** | U3 | U1 | Email threads navigator | Large |
| **UI Action Items** | U4 | U1 | Action items + cross-navigation | Medium |
| **Doc Mgmt MCP** | M3 | F1 + C2 | Reclassify + reextract tools | Medium |

### Wave 4: Polish & Consumers (depends on Wave 3)

| Track | Phases | Depends on | What | Effort |
|-------|--------|------------|------|--------|
| **Bulk Reclassify** | C4 | C3 | Reclassify 2,988 unsorted docs | Medium |
| **Classification UI** | C5 | C2 + U2 | Classification insight in document browser | Medium |
| **UI Timeline** | U5 | U1 | Timeline + activity log | Medium |
| **Osprey Client** | I3, I4 | F1+F2 | Hermes MCP client + poll loop | Medium |
| **Osprey Parsers** | I5 | I4 + P8 | Adapt parsers to content-based | Medium |

### Wave 5: Cleanup & Verification

| Track | Phases | Depends on | What | Effort |
|-------|--------|------------|------|--------|
| **Osprey Cutover** | I6 | I5 | Delete collector, update host | Small |
| **E2E Test** | I7 | I6 | End-to-end integration test | Medium |
| **Consumer Docs** | F3 | F1+F2 | Consumer protocol documentation | Small |
| **Alerts** | M4 | M2 | Alert + confirmation queue (future) | Medium |

## Phase-by-Phase Checklist

### Total: 33 phases across 6 specs

| # | Phase | Spec | Status |
|---|-------|------|--------|
| 1 | U1: Shell Layout | Doc 15 | ⬜ Not started |
| 2 | P1: Letter Extraction | Doc 17 | ⬜ Not started |
| 3 | P2: Heading Detection | Doc 17 | ⬜ Not started |
| 4 | P3: Table Detection | Doc 17 | ⬜ Not started |
| 5 | P4: Key-Value Detection | Doc 17 | ⬜ Not started |
| 6 | F1: hermes_list_documents | Doc 13 | ⬜ Not started |
| 7 | F2: hermes_get_document_content | Doc 13 | ⬜ Not started |
| 8 | P5: Multi-Page Tables | Doc 17 | ⬜ Not started |
| 9 | P6: CID Fallback | Doc 17 | ⬜ Not started |
| 10 | P7: Pipeline Integration | Doc 17 | ⬜ Not started |
| 11 | U2: Documents Navigator | Doc 15 | ⬜ Not started |
| 12 | U6: Chat Integration | Doc 15 | ⬜ Not started |
| 13 | M2: Reminder MCP Tools | Doc 14 | ⬜ Not started |
| 14 | C1: Extract-First Reorder | Doc 18 | ⬜ Not started |
| 15 | C2: Content Rules Engine | Doc 18 | ⬜ Not started |
| 16 | C3: LLM Classification | Doc 18 | ⬜ Not started |
| 17 | P8: MCP markdown format | Doc 17 | ⬜ Not started |
| 18 | P9: Excel Extraction | Doc 17 | ⬜ Not started |
| 19 | P10: Word Extraction | Doc 17 | ⬜ Not started |
| 20 | P11: CSV Extraction | Doc 17 | ⬜ Not started |
| 21 | P12: Format Dispatch | Doc 17 | ⬜ Not started |
| 22 | U3: Threads Navigator | Doc 15 | ⬜ Not started |
| 23 | U4: Action Items | Doc 15 | ⬜ Not started |
| 24 | M3: Doc Mgmt MCP Tools | Doc 14 | ⬜ Not started |
| 25 | C4: Bulk Reclassification | Doc 18 | ⬜ Not started |
| 26 | C5: Classification Insight | Doc 18 | ⬜ Not started |
| 27 | U5: Timeline + Activity | Doc 15 | ⬜ Not started |
| 28 | I3: Osprey MCP Client | Doc 16 | ⬜ Not started |
| 29 | I4: TaxProcessor Loop | Doc 16 | ⬜ Not started |
| 30 | I5: Parser Adaptation | Doc 16 | ⬜ Not started |
| 31 | I6: Collector Deletion | Doc 16 | ⬜ Not started |
| 32 | I7: E2E Integration | Doc 16 | ⬜ Not started |
| 33 | F3: Consumer Docs | Doc 13 | ⬜ Not started |
| 34 | M4: Alerts (future) | Doc 14 | ⬜ Deferred |

## Cross-Plan Shared Phases (avoid duplication)

| Shared Work | Referenced By | Implement Once In |
|-------------|--------------|-------------------|
| `hermes_list_documents` + `hermes_get_document_content` | Doc 13 F1+F2, Doc 14 M1, Doc 16 I1 | Doc 13 (Document Feed) plan |
| Structured PDF→Markdown pipeline | Doc 17 P7, Doc 16 I2, Doc 18 C2 dependency | Doc 17 (Document Extraction) plan |
| `activity_log` table | Doc 15 U5, Doc 18 C2/C3 logging | Doc 15 (Rich UI) plan |
| `classification_tier` + `classification_confidence` columns | Doc 18 C2, Doc 15 U2 (display) | Doc 18 (Smart Classification) plan |

## Silver Thread Coverage Assessment

Every phase in every plan follows the silver-thread paradigm:

- **Input trigger** identified (file drop, MCP call, button click, sync cycle)
- **Processing** specified (what backend code runs)
- **Storage** specified (what DB changes)
- **UI surface** specified (where user sees the result) OR **MCP surface** (where consumer receives the result)
- **PROOF** defined (concrete verification steps)

**One flag**: Phase C5 (Classification Insight UI) depends on U2 (Documents Navigator) for its UI surface. If U2 isn't done, C5 backend can be built but UI can't land. Recommendation: build C5 backend first, plug into UI when U2 ships.

**One flag**: Phase M4 (Alerts + Confirmation Queue) is future work from Doc 10 (Agent Evolution). It is designed but deferred. No other phase depends on it.

No phase has broken silver thread — every phase delivers observable, end-to-end value.
