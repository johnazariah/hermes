# 24 — Comprehension Stage Design

> Supersedes: 18-smart-classification.md (classification is now a byproduct of comprehension)

## Problem

Hermes extracts raw text from documents (PdfPig), but raw text is not useful for consumers. Osprey needs "the gross pay from this payslip" not "a wall of OCR characters." Search needs "invoice from Telstra for $89.50" not "| Smart | | Access | | | | |".

Classification as a separate stage was a mistake — it produces a label ("invoices") but no understanding. The label alone doesn't help Osprey compute taxes or help users find specific information.

## Solution

Replace the classify stage with a **comprehension stage** that produces structured JSON understanding. Classification falls out naturally as a `document_type` field in the comprehension output.

```
extract → comprehend → embed
             │
             ├── document_type: "payslip"
             ├── structured_data: { gross_pay: 8500, tax_withheld: 2100, ... }
             ├── summary: "Payslip from Microsoft for March 2026"
             └── confidence: 0.92
```

## Stage Definition

```fsharp
{ Name = "comprehend"
  OutputKey = "comprehension"
  RequiredKeys = [ "extracted_text" ]
  Process = comprehendFn
  ResourceLock = Some ollamaLock   // shares GPU with embed
  MaxHoldTime = TimeSpan.FromSeconds(180.0) }
```

## Comprehension Output

The process function produces a JSON string stored in the property bag under `"comprehension"`:

```json
{
  "document_type": "payslip",
  "confidence": 0.92,
  "summary": "Payslip from Microsoft for John Azariah, March 2026. Gross $8,500, tax $2,100, super $850.",
  "fields": {
    "employer": "Microsoft",
    "employee": "John Azariah",
    "period": "2026-03",
    "gross_pay": 8500.00,
    "tax_withheld": 2100.00,
    "super": 850.00,
    "net_pay": 5550.00
  }
}
```

The `fields` object varies by `document_type`. The comprehension stage does NOT have a fixed schema — the LLM determines what fields are relevant based on the document content.

## LLM Prompt Design

Two-phase prompt:

### Phase 1: Understand

```
Read the following document text and produce a JSON understanding.

Include:
- document_type: what kind of document this is (e.g. payslip, invoice, bank-statement, receipt, letter, notification, report)
- confidence: 0.0-1.0 how confident you are in the classification
- summary: a 1-2 sentence human-readable summary
- fields: an object with the key structured data extracted from the document

Extract all monetary amounts, dates, names, account numbers, and identifiers you can find.

Respond with ONLY a JSON object, no explanation.

Document text:
{extracted_text}
```

### Why no predefined schema

If we told the LLM "extract these 7 fields for a payslip" we'd need a schema registry mapping document_type → field list. That's:
- Fragile (what if a document doesn't fit any schema?)
- Requires manual maintenance
- Limits what the system can learn

Instead, the LLM extracts what it finds. Over time, the system builds a corpus of comprehension outputs. The `fields` become a natural schema — "payslips from Microsoft always have these fields." This is the learning path.

## Learning

Each comprehended document becomes a training example:

1. First Telstra invoice: LLM extracts `{ vendor: "Telstra", amount: 89.50, date: "2026-03-15", abn: "33 051 775 556", service: "mobile" }`
2. Second Telstra invoice: system can offer few-shot examples from the first one — "previous Telstra invoices looked like this"
3. Over time: sender domain → expected document_type → expected fields becomes implicit knowledge

This is NOT fine-tuning the model. It's retrieval-augmented comprehension — find similar past documents, use their comprehension as examples in the prompt.

## Impact on Existing Stages

### Extract (unchanged)
Still runs PdfPig / OCR. Produces `extracted_text`. No change.

### Classify (REMOVED)
The current classify stage is replaced by comprehend. The `category` field is set from `comprehension.document_type`. The `classification_tier` and `classification_confidence` fields are set from the comprehension output.

### Embed (minor change)
Currently embeds `extracted_text`. Should embed the `summary` from comprehension instead — it's a better representation of the document's meaning for semantic search.

## Property Bag Keys

The comprehend stage adds:

| Key | Type | Description |
|-----|------|-------------|
| `comprehension` | string (JSON) | Full structured understanding |
| `comprehension_schema` | string | Version identifier (e.g. "v1") |
| `category` | string | Set from `document_type` |
| `classification_tier` | string | Always "comprehension" |
| `classification_confidence` | float | From comprehension confidence |
| `stage` | string | Advanced to "comprehended" |

## Pipeline Stage Order

```
received → extracted → comprehended → embedded
```

The `stage` column values change from `classified` to `comprehended`. This requires a DB migration for existing data (or treat `classified` as equivalent to `comprehended` during hydration).

## GPU Considerations

Comprehension uses the same Ollama model as the old classify (llama3:8b). The resource lock handles contention with embed. No new GPU pressure.

Comprehension is slower per-document than classification (more output tokens). But it's also more valuable — one LLM call produces both the type AND the structured data, instead of a classify call + a separate comprehension call.

## Osprey Integration

With comprehension, Osprey's MCP query becomes:

```
hermes_list_documents(category="payslip", stage="comprehended")
→ returns documents with comprehension JSON
→ Osprey reads fields.gross_pay, fields.tax_withheld
→ Osprey posts RecordSalary event to Pelican Core
```

No parsing on Osprey's side. Hermes did the understanding.

## Open Questions

1. **Vision**: Can llama3:8b comprehend bank statement tables from text alone? If not, we need a vision model path (render PDF pages as images). Deferred until we test with real data.
2. **Confidence threshold**: Below what confidence should we flag for human review? Suggest 0.5 initially.
3. **Recomprehension**: When should a document be re-comprehended? (Better model available, user corrects a field, schema version change.) The pipeline supports this via reflow.
