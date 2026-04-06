---
description: "Wave 4b: Turn funnel sections into interactive pipeline control panels with live document tracking, batch controls, and status bar ticker."
---

# Wave 4b: Interactive Pipeline Controls

> **Wave status**: `.project/waves/wave-4b-pipeline-controls.md`
> **Design reference**: `.project/design/15-rich-ui.md` sections 3.3, 3.4, 6
> **Project status**: `.project/STATUS.md`

**Branch**: `feat/wave4b-pipeline-controls`

**IMPORTANT: Use a git worktree.**

```
cd c:\work\hermes
git worktree add ..\hermes-pipeline feat/wave4b-pipeline-controls 2>/dev/null || git worktree add ..\hermes-pipeline -b feat/wave4b-pipeline-controls
cd c:\work\hermes-pipeline
```

**Rules**:

- Use `@csharp-dev` for all C# code
- Use `@fsharp-dev` for any F# changes
- Read `.github/copilot-instructions.md` — silver thread + UI definition of done
- Build + test after each task: `dotnet build hermes.slnx --nologo && dotnet test hermes.slnx --nologo --no-build`
- 817 tests baseline — must stay green

---

## Context

The funnel currently shows counts only. Users must go to Settings → Pipeline Operations to trigger extraction or reclassification — but results don't update visually because the Settings dialog is modal. The operations work backend-side but the UI doesn't reflect changes.

**Goal**: The funnel sections ARE the control surface. Expanding Extracting or Classifying shows what's happening, lets the user trigger batches, and updates live.

---

## Task P1: Extracting Section — Live Status + Queue

Replace the current Extracting expander content (just a count) with:

**XAML/Code-behind in ShellWindow.axaml.cs** — inside `RebuildExtractingSection()`:

1. **"Now:" line** — TextBlock showing the document currently being extracted + extractor type + elapsed time. Updated by a 1-second `DispatcherTimer` (separate from the 5s refresh timer).

2. **Queue list** — StackPanel with the next 5 documents waiting for extraction. Each row: filename + size + "queued" label. Click → content pane shows raw document info.

3. **Progress bar** — full-width `ProgressBar` with `Maximum = totalDocuments` and `Value = extractedCount`. Below it: "{extracted:N0} / {total:N0} ({pct:F1}%)"

4. **Rate + ETA** — TextBlock: "Rate: ~{N}/min · ETA: ~{time}". Calculated from the delta in extractedCount between refreshes.

**Data sources**:

- Queue: `SELECT id, original_name, size_bytes FROM documents WHERE extracted_at IS NULL ORDER BY id ASC LIMIT 5`
- "Now:" document: needs a way to know what's currently processing. Two options:
    - **(a)** Add a `processing_status` column or in-memory flag — complex
    - **(b)** Show the first item in the queue as "Now:" when the pipeline is running — simpler, slightly inaccurate
    - **Recommendation**: Option (b) for now. If pipeline is not running (IsSyncing == false), show "Pipeline idle" instead.

**Proof**: Launch app → see Extracting section with queue of 5 files, progress bar at 3%, rate estimate.

---

## Task P2: Extracting Section — Batch Controls

Add below the progress bar:

1. **Batch size** — `NumericUpDown` with default 500, range 10–5000
2. **"▶ Extract now" button** — Calls `_vm.Bridge.RunExtractionBatchAsync(batchSize)`. While running:
    - Button text: "⏳ Extracting..."
    - Disable button
    - Start a fast timer (500ms) that re-queries extractedCount and updates the progress bar + queue list
    - When complete: show "✅ {N} extracted" for 3 seconds, then revert button text
    - Force a full RefreshAsync to update all funnel counts
3. **"⏸ Pause" toggle** — Calls `_vm.TogglePause()` to stop/resume the automatic pipeline

**Proof**: Click "Extract now" with batch 500 → progress bar animates from 3% upward → queue list shrinks → "✅ 487 extracted" shown → funnel count updates immediately.

---

## Task P3: Classifying Section — Results List with Inline Actions

Replace the current Classifying expander content with:

1. **"Now:" line** — TextBlock showing currently-classifying document + tier/provider

2. **Results list** — StackPanel with the most recently classified documents:
    - Each row: filename → category (confidence%) [✓ Accept] [✎ Change]
    - [✓ Accept] only shown when confidence ≥ 0.7 — confirms the classification (no DB change needed, it's already classified)
    - [✎ Change] opens a `ComboBox` with categories, suggested categories at top (from content match), user picks → calls `DocumentManagement.reclassify` → row updates
    - Low-confidence items (< 0.7) show amber highlight and only [✎ Change] (no auto-accept)

3. **Tier breakdown** — TextBlock: "Tier 2 (content): N · Tier 3 (LLM): N · Manual: N"

4. **Progress** — ProgressBar showing classified/total

**Data sources**:

- Results: `SELECT id, original_name, category, classification_tier, classification_confidence FROM documents WHERE classification_tier IS NOT NULL ORDER BY extracted_at DESC LIMIT 10`
- Remaining: `SELECT COUNT(*) FROM documents WHERE (category = 'unsorted' OR category = 'unclassified') AND extracted_at IS NOT NULL`

**Proof**: See recently classified documents with categories, confidence, and action buttons. Click [✎] → dropdown appears → pick category → row updates.

---

## Task P4: Classifying Section — Batch Controls

Add below results:

1. **Batch size** — NumericUpDown, default 200, range 10–5000
2. **Provider label** — "Provider: Azure OpenAI (gpt-4o-mini)" or "Ollama (llama3.2)"
3. **"▶ Reclassify now" button** — Calls `_vm.Bridge.RunReclassifyBatchAsync(batchSize)`. Same live-update pattern as extraction: progress updates, completion message, auto-refresh.

**Proof**: Click "Reclassify now" → progress animates → unsorted count drops → "✅ 189 reclassified, 11 remaining".

---

## Task P5: Status Bar Live Ticker

Replace the static status bar text with a live ticker.

**In ShellViewModel.cs**:

- Add `CurrentOperation` property: string describing what the pipeline is doing RIGHT NOW
- Add `CurrentDocumentName` property: the document being processed
- When pipeline is idle: `CurrentOperation = null`
- When syncing: `CurrentOperation = "Syncing {account}"`
- When extracting: `CurrentOperation = "Extracting: {filename}"`
- When classifying: `CurrentOperation = "Classifying: {filename} (LLM)"`
- When running a user batch: `CurrentOperation = "Batch extract: {N}/{total}"`

**In ShellWindow.axaml.cs**:

- Status bar text builds from: `{dot} {CurrentOperation ?? "Ready"} · {totalDocs:N0} docs · {queueCounts}`
- When `CurrentOperation != null`, tick the status bar at 1-second intervals
- Dot colour follows the tier: green=idle, blue=syncing, yellow=processing, orange=batch, red=error

**Challenge**: Getting real-time "currently processing" info from the background service. The service runs in a separate task. Options:

- **(a)** Write a "current operation" file that the service updates per-document — read by UI. Simple but disk I/O.
- **(b)** Add an in-memory shared state object between service and UI (e.g. `ConcurrentQueue<PipelineEvent>` or a simple `volatile string`). Better performance.
- **Recommendation**: (b) — Add a `PipelineProgress` record to `HermesServiceBridge` with `CurrentDocument`, `Operation`, `BatchProgress` fields. Service writes, UI reads on timer.

**Proof**: Start a batch extraction → status bar shows "Extracting: scan_receipt.pdf (PdfStructure)" updating per-document → when done: reverts to "Ready".

---

## Task P6: Remove Pipeline Operations from Settings

Delete the "Pipeline Operations" section from `BuildSettingsDialog()`. The controls now live in the funnel sections.

**Proof**: Open Settings → no Pipeline Operations section. Extraction/reclassification only available in funnel sections.

---

## Task P7: Immediate Refresh After Operations

After any pipeline operation (Extract now, Reclassify now, Accept, Change), immediately call `_vm.RefreshAsync()` to update all funnel counts. Don't wait for the 5-second timer.

Also: after batch operations, rebuild the affected funnel section (queue list, progress bar, results list).

**Proof**: Click "Extract now" → when done → funnel Extracting count drops immediately (not after 5s delay).

---

## Merge Gate

- All funnel sections show live data (not just counts)
- Extracting: progress bar, queue, batch button, live update during batch
- Classifying: results list, [✓]/[✎] buttons, batch button, live update
- Status bar: shows current operation + document name when active
- Pipeline operations removed from Settings
- Operations trigger immediate refresh
- 817+ tests pass, 0 failures
- Build clean

```
git push -u origin feat/wave4b-pipeline-controls
```

Do NOT merge — await review.
