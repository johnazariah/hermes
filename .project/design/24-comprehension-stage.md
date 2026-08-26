# 24 - Two-Phase Comprehension

> Supersedes standalone classification. Pipeline integration is defined by [30-pipeline-v5-architecture.md](30-pipeline-v5-architecture.md).

## Problem

Raw extraction is not useful enough for downstream consumers. Osprey needs values such as gross pay and tax withheld, while users need meaningful summaries, tags, senders, dates, amounts, and review signals.

A single large-model pass over every document is also too expensive for local hardware. Most documents need a quick routing decision; only high-value documents need deep structured understanding.

## Design

Hermes uses two comprehension passes:

```text
extract
  |
  +--> triage --------------------------> initial artifact
  |       |
  |       +-- financial/high-value --> deep-comprehend --> final artifact
  |
  +--> embed
```

### Triage

Triage runs for every extracted document using the configured fast provider. It reads archive-backed extracted text plus sender, subject, preferences, content rules, and learned sender patterns.

It produces:

- `document_type`;
- category;
- confidence;
- short summary;
- review tier; and
- an initial `thread.comprehension.json` artifact.

Low-confidence output creates a review suggestion. Financial and other configured high-value categories are marked ready for deep comprehension.

### Deep comprehension

Deep comprehension depends on extraction and triage and is gated by category. It uses the configured instruct provider, the full thread context, type-specific prompt registry, preferences, and retrieval-augmented schema hints.

It produces a richer, variable JSON structure:

```json
{
  "document_type": "payslip",
  "confidence": 0.92,
  "summary": "March 2026 payslip from Microsoft.",
  "tags": ["payslip", "employment", "tax"],
  "fields": {
    "employer": "Microsoft",
    "period": "2026-03",
    "gross_pay": 8500.0,
    "tax_withheld": 2100.0,
    "net_pay": 5550.0
  }
}
```

The `fields` object is intentionally open-ended. Type-specific prompts improve extraction without imposing a global schema registry.

## File and Database Outputs

Human- and LLM-readable content stays in the archive:

| Artifact | Purpose |
|----------|---------|
| `<document>.extracted.md` | Extraction input for both comprehension passes |
| `thread.comprehension.json` | Latest thread-level understanding |
| `.hermes.json` | Source identity and file metadata |

SQLite stores lightweight operational metadata:

| Table | Purpose |
|-------|---------|
| `triage` | Type, category, confidence, completion time |
| `comprehension` | Deep result metadata and schema version |
| `learned_patterns` | Sender-domain to document-type evidence |
| `suggestions` | Human review queue |
| `tags` | Multi-label categorisation |
| `stage_completions` | Pipeline idempotency |

The legacy `documents` category, confidence, tier, and stage fields remain compatibility projections for existing API and UI consumers. Content is not stored there.

## Retrieval-Augmented Comprehension

Past results are used as schema hints, not examples containing values:

1. Find prior comprehension artifacts from the same sender domain.
2. Extract document type and field names.
3. Compact and cap the hint.
4. Add the hint to the deep prompt.

This preserves useful structural memory while avoiding contamination from stale dates, names, and monetary values.

## Learning and Feedback

1. Comprehension records sender/type evidence.
2. Low confidence creates a suggestion.
3. User approval or rejection updates the suggestion and learned pattern.
4. Corrections can trigger recomprehension.
5. Preferences are included in future triage and deep prompts.

## GPU Scheduling

Triage, deep comprehension, and embedding may require different local models. Pipeline V5 groups stages by model and serializes model use through `GpuScheduler`, avoiding simultaneous model residency on constrained GPUs.

## Downstream Contract

Osprey queries Hermes through MCP and receives document metadata plus file-backed comprehension. It should consume structured fields rather than reparse raw extraction.

## Open Design Questions

1. Which categories should be added to or removed from the deep-comprehension gate after live-corpus measurement?
2. When should cloud escalation be offered for low-confidence local results?
3. What schema-version policy should trigger bulk recomprehension?
4. Which comprehension fields deserve dedicated SQLite indexes beyond FTS5 and vectors?
