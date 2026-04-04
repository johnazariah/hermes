# Wave 3: Smart Classification Wiring

> Status: ✅ **Done**  
> Design doc: [18-smart-classification.md](../design/18-smart-classification.md)  
> Branch: `feat/wave3-classification` (merged to main)

## Goal

Wire the built-but-disconnected Tier 2 (content rules) and Tier 3 (LLM) classification into the pipeline. Parse content rules from YAML. Reclassify the ~2,988 unsorted documents.

## Tasks

| # | Task | Status |
|---|------|--------|
| C1 | Parse `content_rules:` from rules.yaml | ✅ Done — Config.fs +53 lines, 8 default content rules |
| C2 | Wire LLM classification as Tier 3 fallback | ✅ Done — confidence gating: ≥0.7 auto, 0.4-0.7 review, <0.4 discard |
| C3 | `hermes reclassify-unsorted` CLI command | ✅ Done — Program.fs +103 lines, Tier 2 then Tier 3 pipeline |
| C4 | Classification insight in document detail pane | ✅ Done — shows "Classification: {tier} ({confidence}%)" |

## Log

### April 4, 2026 — Review PASS
- Pedantic audit: all 4 files pass. Silver thread unbroken.
- Security: all SQL parameterized, LLM category validated against DB
- Tagless-Final: compliant, ChatProvider algebra used for LLM
- 1 minor test gap: TableHeadersAny variant untested (low priority)
- 733 tests, 0 failures

### April 3-4, 2026 — Implementation
- C1: ContentRuleDto parsing in Config.fs with 7 ContentMatch variant support
- C2: LLM classification with buildClassificationPrompt + parseClassificationResponse
- C3: CLI `hermes reclassify-unsorted` with Tier 2→3 pipeline + progress logging
- C4: ShellWindow.axaml.cs shows classification tier + confidence in doc detail
- 17 new tests in ContentClassifierTests.fs
