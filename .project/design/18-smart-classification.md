# Hermes — Smart Classification Pipeline

> Design doc for reordering the pipeline to extract-first, then classify on content, with LLM classification for ambiguous documents.  
> Replaces the current classify-first pipeline. Builds on doc 17 (PDF-to-Markdown).  
> Created: 2026-04-01

---

## 1. Problem

The current pipeline classifies **before** extraction:

```
Ingest → Classify (filename + sender rules) → Extract → Embed
```

Result: 2,988 out of 4,166 documents (72%) are in `unsorted/` because the rule engine only sees metadata. A PDF from `noreply@workday.com` named `document_283847.pdf` can't be classified until we read it and discover it's a payslip.

---

## 2. New Pipeline Order

```
Ingest → Extract structured markdown → Classify (3-tier) → Embed → Trigger evaluation
```

### Three classification tiers

| Tier | Input | Speed | Cost | Accuracy | When |
|------|-------|-------|------|----------|------|
| **1: Rules** | Filename, sender, email subject | Instant | Free | High for known patterns | Always runs first |
| **2: Content** | Extracted markdown: keywords, table headers, key-value pairs | Fast (~10ms) | Free | Good for structured documents | When Tier 1 → unsorted |
| **3: LLM** | Full extracted markdown sent to Azure OpenAI / Ollama | Slow (~2s) | ~$0.001/doc | Excellent for ambiguous docs | When Tier 2 → unsorted |

**Flow**:
```
Document arrives
  → Tier 1 (rules): match filename/sender/subject?
    → YES → category assigned, done
    → NO  → extract markdown first
      → Tier 2 (content keywords): match table headers/keywords?
        → YES → category assigned, done
        → NO  → Tier 3 (LLM): ask the model
          → category assigned (or "unsorted" with low confidence)
```

---

## 3. Tier 1: Rule-Based Classification (existing, unchanged)

The current `Rules.fs` engine stays as-is. It's fast and correct for known patterns:

```yaml
rules:
  - name: microsoft-payslips
    match:
      sender: "@microsoft.com"
      filename: "payslip"
    category: payslips
  - name: agl-invoices
    match:
      sender: "@agl.com.au"
    category: invoices
```

**What changes**: If Tier 1 produces `unsorted`, the document is **not moved yet**. It enters extraction first, then Tier 2/3 classify it.

---

## 4. Tier 2: Content-Based Classification (new)

After extraction produces structured markdown, match on content features.

### Rule format extension

```yaml
content_rules:
  - name: payslip-by-content
    match:
      content_any: ["gross pay", "tax withheld", "net pay", "pay period"]
      content_all: ["gross", "tax"]  # must contain ALL of these
    category: payslips
    confidence: 0.85

  - name: bank-statement-by-content
    match:
      has_table: true
      table_headers_any: ["narrative", "description", "transaction"]
      table_headers_all: ["date", "balance"]
    category: bank-statements
    confidence: 0.80

  - name: invoice-by-content
    match:
      content_any: ["amount due", "invoice number", "invoice date", "payment due"]
      has_amount: true
    category: invoices
    confidence: 0.80

  - name: rental-statement-by-content
    match:
      content_any: ["folio", "owner statement", "rental income", "management fee", "disbursement"]
    category: rental-statements
    confidence: 0.80

  - name: tax-by-content
    match:
      content_any: ["tax return", "assessment notice", "income statement", "PAYG summary", "taxable income"]
      content_any: ["australian taxation office", "ato.gov.au", "ABN"]
    category: tax
    confidence: 0.85

  - name: insurance-by-content
    match:
      content_any: ["policy number", "premium", "sum insured", "certificate of insurance", "renewal"]
    category: insurance
    confidence: 0.75

  - name: receipt-by-content
    match:
      content_any: ["receipt", "payment received", "thank you for your payment"]
      has_amount: true
    category: receipts
    confidence: 0.70
```

### Content match functions

```fsharp
type ContentMatch =
    | ContentAny of keywords: string list       // document contains ANY of these (case-insensitive)
    | ContentAll of keywords: string list       // document contains ALL of these
    | HasTable                                   // document has at least one markdown table
    | TableHeadersAny of headers: string list   // any table has ANY of these column headers
    | TableHeadersAll of headers: string list   // any table has ALL of these column headers
    | HasAmount                                  // extracted_amount is not null
    | HasDate                                    // extracted_date is not null

type ContentRule = {
    Name: string
    Conditions: ContentMatch list    // all must match (AND)
    Category: string
    Confidence: float
}

let evaluateContentRule (markdown: string) (tables: Table list) (amounts: decimal option) (rule: ContentRule) : (string * float) option =
    let allMatch = rule.Conditions |> List.forall (fun cond ->
        match cond with
        | ContentAny kws -> kws |> List.exists (fun kw -> markdown.Contains(kw, StringComparison.OrdinalIgnoreCase))
        | ContentAll kws -> kws |> List.forall (fun kw -> markdown.Contains(kw, StringComparison.OrdinalIgnoreCase))
        | HasTable -> not tables.IsEmpty
        | TableHeadersAny hdrs -> tables |> List.exists (fun t -> hdrs |> List.exists (fun h -> t.Headers |> List.exists (fun th -> th.Contains(h, StringComparison.OrdinalIgnoreCase))))
        | TableHeadersAll hdrs -> tables |> List.exists (fun t -> hdrs |> List.forall (fun h -> t.Headers |> List.exists (fun th -> th.Contains(h, StringComparison.OrdinalIgnoreCase))))
        | HasAmount -> amounts.IsSome
        | HasDate -> true) // simplified
    if allMatch then Some (rule.Category, rule.Confidence) else None
```

### Pick best match

If multiple content rules match, pick the one with highest confidence. If none match, pass to Tier 3.

---

## 5. Tier 3: LLM Classification (new)

For documents that neither rules nor content keywords classify, ask the LLM.

### Prompt

```
You are a document classifier for a personal document archive. 
Classify this document into one of the following categories:

Categories: payslips, invoices, bank-statements, receipts, tax, insurance, 
rental-statements, donations, medical, legal, subscriptions, utilities, 
rates-and-tax, property, unsorted

Based on the document content below, respond with a JSON object:
{
  "category": "the best matching category",
  "subcategory": "optional subcategory (e.g. 'car' for insurance/car)",
  "document_type": "specific type (e.g. 'renewal_notice', 'monthly_statement', 'payslip')",
  "confidence": 0.0 to 1.0,
  "reasoning": "one sentence explaining why"
}

Document content:
---
{first 2000 chars of extracted markdown}
---
```

### Implementation

```fsharp
let classifyWithLlm 
    (chatConfig: Domain.ChatConfig) 
    (ollamaUrl: string) 
    (ollamaModel: string) 
    (markdown: string) 
    : Task<(string * float) option> =
    task {
        let truncated = if markdown.Length > 2000 then markdown.[..1999] else markdown
        let prompt = buildClassificationPrompt truncated
        
        let! result = 
            match chatConfig.Provider with
            | Domain.ChatProviderKind.AzureOpenAI -> 
                Chat.askAzureOpenAI chatConfig.AzureOpenAI prompt []
            | _ -> 
                Chat.askOllama ollamaUrl ollamaModel prompt []
        
        match result with
        | Ok response -> return parseClassificationResponse response
        | Error _ -> return None
    }
```

### Cost estimate

- Input: ~500 tokens (prompt + 2000 chars of markdown)
- Output: ~50 tokens (JSON response)
- GPT-4o-mini: ~$0.0001 per document
- One-time reclassification of 2,988 unsorted docs: ~$0.30
- Ongoing: only for documents that Tier 1+2 can't classify

### Confidence threshold

- LLM confidence ≥ 0.7 → auto-classify, move file
- LLM confidence 0.4–0.7 → classify but flag for review in Activity log
- LLM confidence < 0.4 → leave as `unsorted`, log as "unable to classify"

---

## 6. Pipeline Integration

### New pipeline order in `ServiceHost.runSyncCycle`

```fsharp
// 1. Email sync → unclassified/
// 2. Watch folder scan → unclassified/

// 3. EXTRACT first (before classifying)
//    Run extraction on all unclassified + unsorted documents that lack extracted text
let! _extractResult = Extraction.extractBatch fs db logger clock extractor archiveDir None false 50

// 4. CLASSIFY with 3-tier engine
//    Tier 1: metadata rules (existing, for newly ingested files only)
//    Tier 2: content rules (for anything still unclassified/unsorted after extraction)
//    Tier 3: LLM (for anything Tier 2 couldn't handle)
let! _classifyResult = Classifier.classifyBatch fs db logger rules contentRules chatConfig archiveDir 50

// 5. Evaluate triggers (reminders, alerts)
let! _reminders = Reminders.evaluateNewDocuments db logger (clock.utcNow())

// 6. Embed
let! _embedResult = Embeddings.batchEmbed db logger embedder false (Some 50) None
```

### Key change: extraction runs on `unclassified/` files

Currently, extraction only runs on classified documents (files already moved to category folders). In the new pipeline:

1. Files arrive in `unclassified/`
2. Extraction runs on `unclassified/` files (reads the PDF, produces markdown)
3. Classification runs with full content available
4. If classified → file moves to category folder
5. If still unsorted → file moves to `unsorted/` (but with extracted text — searchable, just not categorised)

### Backward compatibility

- Tier 1 rules still run on ingestion (for obvious matches)
- Only the "unsorted" remainder gets the new Tier 2/3 treatment
- Existing classified documents are not re-classified unless explicitly requested
- A `hermes reclassify-unsorted` CLI command triggers bulk Tier 2/3 reclassification of the 2,988 unsorted docs

---

## 7. Schema Changes

### New column on `documents`

```sql
ALTER TABLE documents ADD COLUMN classification_tier TEXT;
-- Values: 'rule', 'content', 'llm', 'manual', NULL (legacy)

ALTER TABLE documents ADD COLUMN classification_confidence REAL;
-- 0.0 to 1.0
```

This enables:
- UI showing how each document was classified (and confidence)
- Filtering for low-confidence classifications that need review
- Analytics: how many documents does each tier handle?

### Content rules storage

Content rules live in `rules.yaml` alongside the existing metadata rules:

```yaml
rules:
  # Tier 1: metadata (existing format)
  - name: agl-invoices
    match:
      sender: "@agl.com.au"
    category: invoices

content_rules:
  # Tier 2: content-based (new format)
  - name: payslip-by-content
    match:
      content_any: ["gross pay", "tax withheld", "net pay"]
    category: payslips
    confidence: 0.85
```

---

## 8. UI Integration

### Document detail pane

Show classification source:

```
Category: payslips
Classified by: content keywords (confidence: 85%)
```

Or:
```
Category: insurance/car
Classified by: LLM (confidence: 92%)
Reasoning: "Contains Allianz policy number, vehicle registration, and annual premium"
```

### Activity log entries

```
🟢 14:32:04  Classified 5 documents
   payslip_july.pdf → payslips (rule: microsoft-payslips)
   document_283847.pdf → payslips (content: "gross pay" + "tax withheld")
   scan_receipt.pdf → receipts (LLM: 87% — "contains purchase receipt from JB Hi-Fi")
   random_attachment.pdf → unsorted (LLM: 35% — unable to determine)
```

### Reclassify action in UI

The document detail pane's "Reclassify" button gains a "Re-analyze" option that re-runs Tier 2/3 classification. Useful when extraction improves or the user thinks the classification is wrong.

---

## 9. Silver Thread Implementation — Phase by Phase

**Silver thread principle**: every phase delivers working end-to-end functionality. UI request → processing → backend → presentation → UI response. No orphaned backend. No dead UI.

### Phase C1: Extract-First Pipeline Reorder

**The thread**: File arrives in `unclassified/` → extraction runs on it (new) → Tier 1 rules classify → file moves to category → UI shows the document in the correct category with extracted content.

| Layer | What | Verification |
|-------|------|-------------|
| **ServiceHost.fs** | Reorder: run `Extraction.extractBatch` on unclassified files BEFORE `Classifier.classifyBatch` | Unit test: mock pipeline confirms extract runs before classify |
| **Extraction.fs** | Accept files from `unclassified/` directory (currently skips them) | Integration test: drop a PDF in unclassified → extracted_text populated |
| **Classifier.fs** | No change yet — Tier 1 rules still work on metadata | Existing classifier tests still pass |
| **ShellWindow (Activity tab)** | Log entry: "Extracted document_283847.pdf (3 tables, 15 key-value pairs)" | UI shows extraction event in Activity tab |
| **PROOF** | Drop `test.pdf` into `unclassified/` → wait for sync → document has `extracted_text` populated → if sender matches a rule, document moves to correct category → visible in Documents tab with extracted content preview |

### Phase C2: Tier 2 Content Rules Engine

**The thread**: Document that Tier 1 couldn't classify → content rules match on extracted markdown → document reclassified → UI shows the category change.

| Layer | What | Verification |
|-------|------|-------------|
| **Domain.fs** | `ContentMatch`, `ContentRule` types | Compiles |
| **Rules.fs (or new ContentClassifier.fs)** | `evaluateContentRules` function — takes markdown + tables + metadata → best match | Unit test: markdown with "gross pay" + "tax withheld" → category "payslips" at 0.85 |
| **Config.fs** | Parse `content_rules:` section from `rules.yaml` | Config test: YAML with content_rules → parsed correctly |
| **Classifier.fs** | After Tier 1, if category is "unsorted", run `evaluateContentRules` | Integration test: document with invoice keywords → moves from unsorted to invoices |
| **Database.fs** | Schema migration: add `classification_tier` and `classification_confidence` columns | Migration test |
| **Documents tab (detail pane)** | Show "Classified by: content keywords (85%)" | UI shows classification source |
| **Activity tab** | Log: "Classified document_283847.pdf → payslips (content: 'gross pay' + 'tax withheld')" | UI shows content classification event |
| **PROOF** | Drop a payslip PDF with no recognisable sender/filename → Tier 1 fails → Tier 2 detects "gross pay" + "tax withheld" → classified as payslips → visible in Documents tab under payslips → detail pane shows "Classified by: content keywords (85%)" |

### Phase C3: Tier 3 LLM Classification

**The thread**: Document that Tier 1+2 couldn't classify → LLM reads markdown → returns category + reasoning → document reclassified → UI shows LLM reasoning.

| Layer | What | Verification |
|-------|------|-------------|
| **Classifier.fs** | `classifyWithLlm` function using `Chat.askAzureOpenAI` or `Chat.askOllama` | Unit test with mock LLM response: JSON parsed → category + confidence extracted |
| **Classifier.fs** | Confidence gating: ≥0.7 auto, 0.4–0.7 flag, <0.4 unsorted | Unit test: various confidence levels → correct outcome |
| **Config.fs** | `llm_classification.enabled` setting (default: true when Azure OpenAI configured) | Config test |
| **Documents tab** | Show "Classified by: LLM (92%)" + reasoning tooltip | UI shows LLM reasoning |
| **Activity tab** | Log: "Classified scan_receipt.pdf → receipts (LLM: 87% — 'purchase receipt from JB Hi-Fi')" | UI shows LLM event |
| **PROOF** | Drop an ambiguous PDF (e.g. an insurance renewal with a generic filename and unknown sender) → Tier 1 fails → Tier 2 fails → LLM classifies as "insurance/car" with 92% confidence and reasoning → document moves to insurance folder → UI shows LLM classification with reasoning |

### Phase C4: Bulk Reclassification of Unsorted

**The thread**: User clicks "Re-analyze unsorted" → all 2,988 unsorted documents queue for Tier 2/3 → progress visible in UI → documents gradually move to correct categories.

| Layer | What | Verification |
|-------|------|-------------|
| **Classifier.fs** | `reclassifyUnsortedBatch` — processes N unsorted docs per cycle through Tier 2/3 | Unit test with test documents |
| **Program.fs (CLI)** | `hermes reclassify-unsorted` command — triggers bulk reclassification | CLI test |
| **ServiceHost.fs** | Optionally run as a low-priority background task | Fires after normal sync cycle |
| **Documents tab** | "Re-analyze" button on unsorted category | Button triggers reclassification |
| **Activity tab** | Progress: "Reclassified 45/2,988 unsorted documents (invoices: 12, payslips: 3, insurance: 8, ...)" | UI shows progress |
| **Status bar** | "Reclassifying unsorted (45/2,988)..." | Status bar shows progress |
| **PROOF** | Click "Re-analyze unsorted" → status bar shows progress → unsorted count decreases over multiple sync cycles → documents appear in correct categories → Activity log shows each reclassification with tier and confidence |

### Phase C5: UI Visibility — Classification Insight

**The thread**: User browses Documents tab → sees classification source and confidence for every document → can filter by classification quality.

| Layer | What | Verification |
|-------|------|-------------|
| **Stats.fs** | `getClassificationStats` — counts by tier, average confidence, low-confidence count | Query test |
| **Documents tab (list)** | Classification confidence badge on each row (green ≥0.8, yellow 0.5–0.8, red <0.5) | UI renders badges |
| **Documents tab (detail)** | Full classification info: tier, confidence, reasoning (if LLM) | UI shows detail |
| **Documents tab (filter)** | Filter by: "needs review" (confidence < 0.7), "LLM classified", "rule classified" | UI filters work |
| **Sidebar** | Classification stats: "72% rule, 20% content, 8% LLM" | UI shows breakdown |
| **PROOF** | Open Documents tab → see classification badges on each document → click one → detail shows "Classified by: LLM (87%) — insurance renewal notice" → filter by "needs review" → see low-confidence documents |

### Summary: C1 → C2 → C3 → C4 → C5

```
C1: Extract before classify — files in unclassified/ get markdown extracted
    Silver thread: drop PDF → extracted → classified by rules → shown in UI with content
    
C2: Content rules — keyword + table header matching for Tier 1 failures
    Silver thread: ambiguous PDF → content match → reclassified → UI shows "content (85%)"
    
C3: LLM classification — ask Azure OpenAI for documents Tier 1+2 can't handle
    Silver thread: mystery PDF → LLM classifies → UI shows category + reasoning
    
C4: Bulk reclassify — process the 2,988 unsorted documents
    Silver thread: click "Re-analyze" → progress visible → unsorted count drops → docs categorised
    
C5: Classification insight — see how every document was classified
    Silver thread: browse docs → see confidence badges → filter by quality → review low-confidence
```

---

## 10. Development Agent Instructions

### Mandatory workflow

1. **Use `@fsharp-dev`** for all F# code (`Hermes.Core`). Do not write F# without it.
2. **Use `@csharp-dev`** for all C# code (`Hermes.App`). Do not write C# without it.
3. **UI definition of done** — per `.github/copilot-instructions.md`:
   - XAML exists with all controls
   - Code-behind wired — every named control has event handlers
   - Buttons do something — no dead controls
   - Data is live — reads from DB/config, not placeholder text
   - Build clean — 0 errors, 0 warnings
   - Tests pass
   - Smoke tested — verify the specific PROOF for that phase
4. **Silver thread verification** — before marking a phase complete, trace the full thread:
   - **Input**: What triggers this feature? (file drop, button click, sync cycle, MCP call)
   - **Processing**: What backend code runs? (extraction, classification, DB update)
   - **Presentation**: What data flows to the UI? (ViewModel property, collection update)
   - **Output**: What does the user see? (document in correct category, activity log entry, badge update)
   - If ANY link in this chain is broken, the phase is NOT done.

### Common pitfalls to avoid

| Pitfall | What goes wrong | Prevention |
|---------|---------------|------------|
| XAML without code-behind | Buttons exist but do nothing | Wire every `x:Name` control before marking done |
| Backend without UI | Classification works but user can't see it | Every backend change must surface in at least one UI element |
| Tests without integration | Unit tests pass but feature doesn't work end-to-end | Run the PROOF steps manually |
| Hardcoded values | Model name, endpoint, category list baked into code | Read from config, test that config changes take effect |
| Fire-and-forget async | Background task runs but errors are swallowed | Log to Activity tab, surface errors in UI |

---

## 11. Open Questions

| # | Question | Leaning |
|---|----------|---------|
| 1 | Should Tier 2 content rules be in `rules.yaml` or a separate `content-rules.yaml`? | Same file, `content_rules:` section — keeps all classification logic together |
| 2 | Should LLM classification use the chat provider config or a separate LLM config? | Same `chat:` config — it's the same LLM, just a different prompt |
| 3 | Should user manual reclassification override LLM results permanently? | Yes — `classification_tier = 'manual'` is highest priority, never overwritten |
| 4 | Should we re-run classification when extraction improves (e.g. better PDF-to-markdown)? | Optional — `hermes reclassify-all` command, but never automatic (user controls) |
| 5 | What about non-PDF files (images, Word docs)? | Same pipeline — extract text/markdown first, then classify. Image → OCR → markdown → classify. |
| 6 | Should category names be a fixed enum or user-extensible? | User-extensible — any folder name is a valid category. The content rules suggest categories but don't restrict. |
| 7 | Should LLM classify into subcategories (e.g. `insurance/car` vs `insurance/home`)? | Yes — LLM returns both `category` and optional `subcategory`, mapped to folder path |
| 8 | Rate limit on LLM classification? | 10/minute default — configurable. Prevents accidental cost spike. |
