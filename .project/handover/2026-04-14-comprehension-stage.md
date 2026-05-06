# Comprehension Stage — Handover

> April 14, 2026 — branch `v3-clean`

## What was done

Replaced the `classify` pipeline stage with `understand` — a full LLM comprehension stage that produces structured JSON understanding of each document.

### Pipeline flow
```
received → extracted → understood → embedded
```

### Changed files

| File | Change |
|------|--------|
| `src/Hermes.Core/Database.fs` | Added `comprehension` TEXT and `comprehension_schema` TEXT columns |
| `src/Hermes.Core/Document.fs` | Added both columns to property bag + `normaliseStage` helper |
| `src/Hermes.Core/Stages.fs` | Replaced `classify` with `understand` — comprehension prompt, LLM call, JSON parse |
| `src/Hermes.Core/Pipeline.fs` | Renamed `Classify` channel → `Understand`, updated wiring |
| `src/Hermes.Core/Stats.fs` | `ClassifiedCount` → `UnderstoodCount`, `AwaitingClassify` → `AwaitingUnderstand` |
| `src/Hermes.Core/DocumentBrowser.fs` | `Classified` → `Understood`, added `Comprehension` field to detail |
| `src/Hermes.Service/ApiServer.fs` | Pipeline endpoint returns `understood` not `classified` |
| React UI (4 files) | "Filed" → "Understood" with 💡 icon, types updated |

### Build status
- 0 errors, 0 warnings
- 694 passed, 6 skipped, 0 failed (700 total)
- Dev DB deleted (new schema on next startup)
- Prod DB NOT deleted (manual clobber when ready)

## Comprehension output schema (v1)

```json
{
  "document_type": "payslip",
  "confidence": 0.92,
  "summary": "Payslip from Microsoft for March 2026. Gross $8,500.",
  "fields": {
    "employer": "Microsoft",
    "gross_pay": 8500.00,
    "tax_withheld": 2100.00
  }
}
```

Fields are dynamic — LLM determines what's relevant per document type.

## To run

```powershell
# 1. Pull the comprehension model (one-time)
ollama pull qwen2.5:7b

# 2. Update config to use qwen2.5:7b
# In %APPDATA%\hermes-dev\config.yml, set:
#   ollama:
#     instruct_model: qwen2.5:7b

# 3. Run dev mode
$env:HERMES_CONFIG_DIR = "$env:APPDATA\hermes-dev"
$env:HERMES_PORT = "21742"
cd c:\work\hermes
dotnet run --project src/Hermes.Service -- --initial-sync-days 90
```

## What's next

1. **Test with live data** — run dev mode, verify comprehension JSON quality
2. **Config**: consider separate `comprehension_model` field (currently uses `instruct_model`)
3. **Embed improvement**: embed `comprehension.summary` instead of raw `extracted_text`
4. **Learning**: past comprehension outputs as few-shot examples (roadmap item 3)
5. **Osprey integration**: MCP tools return comprehension JSON, Osprey reads structured fields
6. **Prod cutover**: clobber prod DB when satisfied with comprehension quality

## Key design decisions

- Content rules still serve as Tier 1 fast-path (no LLM needed for obvious matches)
- No predefined category taxonomy — `document_type` emerges from LLM understanding
- Comprehension replaces both classification AND future field extraction
- OutputKey for idempotency is `comprehension_schema` (presence = stage done)
- `normaliseStage` maps legacy "classified" → "understood" for stale data
- `qwen2.5:7b` recommended — best sub-10B model for structured JSON extraction, fits 8GB VRAM
