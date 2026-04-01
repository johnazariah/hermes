---
description: "Implementation plan for Doc 18: Smart Classification Pipeline — extract-first, then three-tier classify. Five phases (C1–C5), each a silver-thread vertical slice."
design-doc: ".project/design/18-smart-classification.md"
depends-on:
  - "Doc 17 Phase P7 (Pipeline integration) — extraction must run before content-based classification"
  - "Doc 15 Phase U2 (Documents Navigator) — for classification visibility in UI"
  - "Doc 15 Phase U5 (Activity Log) — for classification event logging"
---

# Hermes — Implementation Plan: Smart Classification

## Prerequisites

```
dotnet build hermes.slnx --nologo
dotnet test tests/Hermes.Tests/Hermes.Tests.fsproj --nologo
```

**Expected**: 0 errors, 0 warnings, all tests pass.

**Hard dependency**: Doc 17 Phase P7 must be complete (extraction produces structured markdown). Content-based classification (Tier 2) requires extracted text with tables and headings. If P7 is not done, only C1 (pipeline reorder) can proceed — C2+ will lack content to classify on.

**Critical rules**:
- **F# code** must go through `@fsharp-dev` agent
- **C# code** must go through `@csharp-dev` agent
- **Every phase has a PROOF** — do not mark complete until the proof passes

## Dependency Map

```
C1: Extract-first pipeline reorder    (standalone — changes ServiceHost.fs flow)
 └─ C2: Tier 2 content rules engine    (needs C1 + Doc 17 P7 structured markdown)
     └─ C3: Tier 3 LLM classification  (needs C2 — fallback when C2 fails)
         └─ C4: Bulk reclassification  (needs C3 — processes unsorted backlog)
C5: Classification insight UI          (needs C2+ schema changes — can parallel C3/C4)
```

C1 is independent. C2→C3→C4 are sequential (escalating tiers). C5 can start after C2.

---

## Phase C1: Extract-First Pipeline Reorder

**Silver thread**: File arrives in `unclassified/` → extraction runs on it (NEW: extraction now handles unclassified files) → Tier 1 rules classify on metadata → file moves to category → UI shows document with extracted content.

### What to build

**File: `src/Hermes.Core/ServiceHost.fs`** (modify)

Reorder `runSyncCycle` to run extraction BEFORE classification:

```
Current order:  Sync → Classify → Extract → Embed
New order:      Sync → Extract (incl. unclassified/) → Classify → Embed
```

Key change: `Extraction.extractBatch` must accept files from `unclassified/` directory (currently it may skip them — verify and fix).

**File: `src/Hermes.Core/Extraction.fs`** (modify if needed)

- Ensure `extractBatch` processes files in `unclassified/` directory (not just category folders)
- Documents in `unclassified/` that have no `extracted_text` should be extraction candidates

### What to test

- `ServiceHost_RunSyncCycle_ExtractsBeforeClassifies` — mock pipeline: verify extract is called before classify
- `Extraction_ExtractBatch_IncludesUnclassifiedFiles` — integration test: file in unclassified/ gets `extracted_text` populated

### PROOF

Drop `test.pdf` into `~/Documents/Hermes/unclassified/` → wait for sync cycle → check DB → document has `extracted_text` populated BEFORE classification runs → if sender matches a Tier 1 rule, document moves to correct category → document visible in UI with content preview.

### Commit

```
feat: reorder pipeline — extract before classify, include unclassified files
```

---

## Phase C2: Tier 2 Content Rules Engine

**Silver thread**: Document that Tier 1 couldn't classify → content rules match on extracted markdown keywords/table headers → document reclassified with `classification_tier='content'` → UI shows "Classified by: content keywords (85%)".

### What to build

**File: `src/Hermes.Core/Domain.fs`** (extend)

```fsharp
type ContentMatch =
    | ContentAny of keywords: string list
    | ContentAll of keywords: string list
    | HasTable
    | TableHeadersAny of headers: string list
    | TableHeadersAll of headers: string list
    | HasAmount
    | HasDate

type ContentRule = {
    Name: string
    Conditions: ContentMatch list    // all must match (AND)
    Category: string
    Confidence: float
}
```

**File: `src/Hermes.Core/ContentClassifier.fs`** (new module)

```fsharp
[<RequireQualifiedAccess>]
module ContentClassifier

/// Evaluate a single content rule against document markdown
let evaluateRule (markdown: string) (tables: PdfStructure.Table list) (amount: decimal option) (rule: ContentRule) : (string * float) option

/// Evaluate all content rules, return best match (highest confidence)
let classify (markdown: string) (tables: PdfStructure.Table list) (amount: decimal option) (rules: ContentRule list) : (string * float) option
```

**File: `src/Hermes.Core/Config.fs`** (extend)

- Parse `content_rules:` section from `rules.yaml`
- Map YAML to `ContentRule` list

**File: `src/Hermes.Core/Classifier.fs`** (modify)

- After Tier 1 produces `unsorted`, call `ContentClassifier.classify` on the extracted markdown
- If match found: update category, set `classification_tier = 'content'`, set `classification_confidence`

**Schema migration**: Add columns to `documents` table:

```sql
ALTER TABLE documents ADD COLUMN classification_tier TEXT;
ALTER TABLE documents ADD COLUMN classification_confidence REAL;
```

### What to test

- `ContentClassifier_Evaluate_PayslipKeywords_MatchesPayslips` — markdown with "gross pay", "tax withheld" → payslips at 0.85
- `ContentClassifier_Evaluate_BankStatementHeaders_MatchesBankStatements` — table with Date/Narrative/Balance headers → bank-statements at 0.80
- `ContentClassifier_Evaluate_NoMatch_ReturnsNone`
- `ContentClassifier_Classify_MultipleMatches_ReturnsBestConfidence`
- `Config_ParseContentRules_ReturnsCorrectRules`
- `Classifier_ClassifyBatch_Tier1Fails_Tier2Succeeds` — integration test

### PROOF

Drop a payslip PDF with unknown sender and generic filename → Tier 1 → unsorted → extraction produces markdown with "Gross Pay: $2,732.60" and "Tax Withheld: $684.65" → Tier 2 matches `payslip-by-content` rule → document moves to `payslips/` → DB shows `classification_tier='content'`, `classification_confidence=0.85`. In UI (if U2 done): document detail shows "Classified by: content keywords (85%)".

### Commit

```
feat: Tier 2 content-based classification engine with YAML content rules
```

---

## Phase C3: Tier 3 LLM Classification

**Silver thread**: Document that Tier 1+2 couldn't classify → LLM reads first 2000 chars of markdown → returns category + confidence + reasoning → document reclassified → UI shows LLM reasoning.

### What to build

**File: `src/Hermes.Core/ContentClassifier.fs`** (extend)

```fsharp
/// Build the classification prompt with truncated document content
let buildClassificationPrompt (markdown: string) (categories: string list) : string

/// Parse LLM JSON response into (category, subcategory option, confidence, reasoning)
let parseClassificationResponse (response: string) : (string * string option * float * string) option

/// Classify a single document using the LLM
let classifyWithLlm (chatProvider: ChatProvider) (markdown: string) (categories: string list) : Task<(string * string option * float * string) option>
```

**File: `src/Hermes.Core/Classifier.fs`** (modify)

- After Tier 2 produces `unsorted`, if LLM classification is enabled:
  - Call `ContentClassifier.classifyWithLlm` with extracted markdown
  - Confidence gating: ≥ 0.7 auto-classify, 0.4–0.7 classify + flag for review, < 0.4 leave as unsorted
  - Set `classification_tier = 'llm'`, store confidence
  - Log reasoning to activity log

**File: `src/Hermes.Core/Config.fs`** (extend)

- Add `llm_classification:` section with `enabled: bool`, `rate_limit: int` (default 10/min)

### What to test

- `ContentClassifier_BuildPrompt_TruncatesTo2000Chars`
- `ContentClassifier_ParseResponse_ValidJson_ReturnsCategory`
- `ContentClassifier_ParseResponse_InvalidJson_ReturnsNone`
- `ContentClassifier_ClassifyWithLlm_MockResponse_ReturnsCorrectCategory`
- `Classifier_Tier3_HighConfidence_AutoClassifies`
- `Classifier_Tier3_LowConfidence_LeavesUnsorted`
- `Classifier_Tier3_MediumConfidence_ClassifiesAndFlags`

### PROOF

Drop an ambiguous PDF (e.g. insurance renewal with generic filename and unknown sender) → Tier 1 fails → Tier 2 fails (no matching content keywords) → LLM reads markdown → responds with `{"category": "insurance", "subcategory": "car", "confidence": 0.92, "reasoning": "Contains Allianz policy number and annual premium"}` → document moves to `insurance/` → DB shows `classification_tier='llm'`, `classification_confidence=0.92`. In UI: "Classified by: LLM (92%) — Contains Allianz policy number and annual premium". In activity log: "Classified [filename] → insurance/car (LLM: 92%)".

### Commit

```
feat: Tier 3 LLM classification with confidence gating and reasoning
```

---

## Phase C4: Bulk Reclassification of Unsorted

**Silver thread**: User triggers "Re-analyze unsorted" (CLI or UI button) → all unsorted documents queue for Tier 2/3 → progress visible in status bar → documents gradually move to correct categories → unsorted count drops.

### What to build

**File: `src/Hermes.Core/Classifier.fs`** (extend)

```fsharp
/// Reclassify unsorted documents in batches through Tier 2/3
/// Returns: (reclassified count, remaining unsorted count)
let reclassifyUnsortedBatch (db: Database) (fs: FileSystem) (contentRules: ContentRule list) (chatProvider: ChatProvider option) (archiveDir: string) (batchSize: int) : Task<int * int>
```

**File: `src/Hermes.Cli/Program.fs`** (extend)

- Add `reclassify-unsorted` subcommand that calls `reclassifyUnsortedBatch` in a loop with progress output

**File: `src/Hermes.Core/ServiceHost.fs`** (extend)

- Optionally run reclassification as a low-priority background task (configurable: `reclassify_on_sync: bool`)

**File: `src/Hermes.App/HermesServiceBridge.cs`** (extend)

- Add `ReclassifyUnsortedAsync()` → triggers batch reclassification
- Progress reporting via existing status update mechanism

### What to test

- `Classifier_ReclassifyBatch_MovesDocumentsFromUnsorted`
- `Classifier_ReclassifyBatch_RespectsRateLimit`
- `Classifier_ReclassifyBatch_PartialProgress_ReportsCorrectCounts`

### PROOF (CLI)

```
hermes reclassify-unsorted
> Processing batch 1/60 (50 documents)...
> Reclassified: invoices: 12, payslips: 3, insurance: 8, bank-statements: 5, unsorted: 22
> Processing batch 2/60...
```

### PROOF (UI — if U2 + U5 done)

Click "Re-analyze unsorted" button on the Documents navigator's unsorted category → status bar shows "Reclassifying unsorted (50/2,988)..." → unsorted count decreases over multiple cycles → documents appear in correct categories → activity log shows each reclassification event.

### Commit

```
feat: bulk reclassification of unsorted documents via CLI and UI
```

---

## Phase C5: Classification Insight UI

**Silver thread**: User browses Documents → sees classification badges on every document → filters by confidence quality → clicks document → detail shows tier, confidence, reasoning.

### What to build

**File: `src/Hermes.Core/Stats.fs`** (extend)

```fsharp
/// Classification statistics: counts by tier, average confidence, low-confidence count
let getClassificationStats (db: Database) : ClassificationStats

type ClassificationStats = {
    ByTier: Map<string, int>             // rule: 1200, content: 450, llm: 300, manual: 50, unknown: 163
    AverageConfidence: float
    LowConfidenceCount: int              // confidence < 0.7
    NeedsReviewCount: int                // LLM confidence 0.4–0.7
}
```

**File: `src/Hermes.App/Views/DocumentsNavigator.axaml`** (extend — if U2 done)

- Add confidence badge on each document row (green ≥ 0.8, yellow 0.5–0.8, red < 0.5)
- Add filter: "Needs review" (confidence < 0.7), "LLM classified", "Rule classified"

**File: `src/Hermes.App/Views/DocumentDetailView.axaml`** (extend — if U2 done)

- Show classification source: "Classified by: [tier] ([confidence]%)"
- If LLM: show reasoning in tooltip or expandable block
- If manual: show "Manually classified"

**File: `src/Hermes.App/Views/ShellWindow.axaml`** (extend sidebar stats — if UI exists)

- Classification breakdown: "72% rule, 20% content, 8% LLM"

### What to test

- `Stats_GetClassificationStats_ReturnsCorrectBreakdown`
- `Stats_GetClassificationStats_EmptyDb_ReturnsZeros`

### PROOF

Open Documents tab → each document row shows a confidence badge (green/yellow/red) → click a document → detail pane shows "Classified by: content keywords (85%)" or "Classified by: LLM (92%) — Contains Allianz policy number" → filter by "Needs review" → see only low-confidence documents → sidebar shows "Rule: 1200, Content: 450, LLM: 300".

### Commit

```
feat(ui): classification insight — badges, filters, and tier/confidence display
```

---

## Silver Thread Integrity Check

| Phase | Input Trigger | Processing | Backend | Presentation | UI Response |
|-------|--------------|------------|---------|-------------|-------------|
| C1 | File in unclassified/ | Extract runs first | ServiceHost pipeline order | Document gets `extracted_text` | Preview shows content before classification |
| C2 | Tier 1 → unsorted | Content rule matching | ContentClassifier.fs | `classification_tier` + `confidence` stored | "Classified by: content (85%)" in detail |
| C3 | Tier 2 → unsorted | LLM prompt + parse | Chat provider call | `classification_tier='llm'` + reasoning | "LLM (92%) — [reasoning]" in detail + log |
| C4 | CLI or UI button | Batch processing loop | Classifier.fs batch | Progress counter + category moves | Status bar progress + unsorted count drops |
| C5 | User browsing docs | Stats query | Stats.fs | Badge + filter data | Confidence badges + "Needs review" filter |

**No orphaned backend**: Every classification tier surfaces via schema columns read by the UI (detail pane, badges, filters, activity log).
**No dead UI**: Every badge, filter, and "Re-analyze" button is wired to real backend processing.

---

## Silver Thread Flag: C5 UI Dependency

⚠ **Phase C5 depends on Doc 15 Phase U2 (Documents Navigator)**. If the rich UI isn't built yet, C5's UI work cannot land. Options:
1. **Implement C5 backend only** (Stats.fs + schema) → defer UI to after U2
2. **Add classification info to existing UI** (current ShellWindow) as a stopgap
3. **Skip C5** until U2 is done and add as a follow-up phase

Recommendation: **Option 1** — build the Stats.fs queries and schema so they're ready, then plug into the UI when U2 ships. This avoids blocking C1–C4 progress.

---

## Flags & Risks

| Risk | Impact | Mitigation |
|------|--------|------------|
| Extraction quality affects Tier 2 | Bad markdown → keywords not found → Tier 2 misses | Depends on Doc 17 quality. Confidence scoring helps — low-confidence extraction → skip Tier 2, go to Tier 3 |
| LLM rate limiting | Bulk reclassify hits rate limit | Default 10/min. Configurable. Batch pauses between calls. |
| LLM cost for bulk reclassify | 2,988 docs × ~$0.0001 = ~$0.30 | Negligible. Log estimated cost before starting. |
| Category names diverge | LLM suggests new categories that don't exist as folders | LLM prompt constrains to known categories. Unknown → unsorted. User can add categories. |
| Content rules maintenance | Rules drift as new document types appear | Start with 7 rules from design doc. Add rules when patterns emerge from LLM classifications. |
