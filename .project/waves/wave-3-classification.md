# Wave 3: Smart Classification Wiring

> Status: **Not started** (blocked on Wave 2)  
> Design doc: [18-smart-classification.md](../design/18-smart-classification.md)  
> Master plan: `.github/prompts/development-plan-apr3.prompt.md` (Wave 3 section)

## Goal

Wire the built-but-disconnected Tier 2 (content rules) and Tier 3 (LLM) classification into the pipeline. Parse content rules from YAML. Reclassify the ~2,988 unsorted documents.

## Tasks

| # | Task | Status |
|---|------|--------|
| C1 | Parse `content_rules:` from rules.yaml | Not started |
| C2 | Wire LLM classification as Tier 3 fallback | Not started |
| C3 | `hermes reclassify-unsorted` CLI command | Not started |
| C4 | Classification insight in document detail pane | Not started |

## Log

(newest on top)
