---
description: "Agent 2: Complete feature wiring — fix broken tests, wire LLM classification, content rules, activity log, navigator panels."
---

# Feature Wiring Completion — Agent 2

**Branch**: `feat/wiring-completion`

**IMPORTANT: Use a git worktree — do NOT work in the main checkout.**
Another agent is running in parallel on the same repo. You MUST use a separate worktree to avoid conflicts.

```
cd c:\work\hermes
git worktree add ..\hermes-wiring feat/wiring-completion 2>/dev/null || git worktree add ..\hermes-wiring -b feat/wiring-completion
cd c:\work\hermes-wiring
```

All commands below run in `c:\work\hermes-wiring`, NOT `c:\work\hermes`.

**Scope**: Primarily `src/` files. May add tests for new wiring code.

Read `.github/copilot-instructions.md` first — especially the **silver thread principle** and **UI definition of done**.
Read `.project/plans/EXECUTION-REPORT.md` for context on what's already built.

**You must use `@fsharp-dev` for all F# code and `@csharp-dev` for all C# code.**

Build and test before starting: `dotnet build hermes.slnx --nologo && dotnet test tests/Hermes.Tests/Hermes.Tests.fsproj --nologo --no-build`

Expected: 418 tests, 0 failures, 2 skipped. Fix the following in order, committing after each. Run build + test after every commit.

---

## Fix 1: Broken MCP reminder tests (Critical)

Two tests are skipped: `MCP_UpdateReminder_MarkComplete_ChangesStatus` and `MCP_ListReminders_ReturnsActiveReminders`. They say "MCP response structure needs debugging — neither result nor error key present". The MCP tool dispatch in `McpServer.fs` `handleToolCall` was refactored and the new reminder tools aren't returning the correct JSON-RPC response structure. Fix the dispatch so these tools return proper `{ "result": ... }` responses, then remove the `Skip` attributes from the tests.

**Proof**: Both tests pass (not skipped). Total: 418 tests, 0 failures, 0 skipped.

**Commit**: `fix: restore MCP reminder tool dispatch — JSON-RPC response structure`

---

## Fix 2: Wire LLM classification into pipeline (Silver thread)

`ContentClassifier.fs` has `buildClassificationPrompt` and `parseClassificationResponse` but they're never called. Wire Tier 3 LLM classification into `Classifier.fs` as a fallback when Tier 1 (rules) and Tier 2 (content keywords) both produce `unsorted`.

The thread:

```
Document arrives → extract → Tier 1 rules fail → Tier 2 content rules fail
  → Tier 3 LLM classifies → document moves to correct category → visible in UI
```

Use `Chat.providerFromConfig` to get a `ChatProvider` and pass `provider.complete` to the LLM classification function. Confidence gating:

- ≥ 0.7: auto-classify, set `classification_tier = 'llm'`
- 0.4–0.7: classify but log warning
- < 0.4: leave as unsorted

The classifier pipeline in `ServiceHost.runSyncCycle` needs to pass the `ChatProvider` (or the config to construct one) down to the classification step.

**Proof**: A generic PDF with no matching filename/sender rules and no content rule keywords → after sync cycle → not in unsorted (LLM classified it). `classification_tier = 'llm'` in DB.

**Commit**: `feat: wire Tier 3 LLM classification into pipeline with confidence gating`

---

## Fix 3: Content rules YAML parsing in Config.fs

`ContentClassifier.classify` accepts `ContentRule list` but `Config.fs` doesn't parse `content_rules:` from `rules.yaml`. Add parsing logic to load content rules from YAML.

Add a `ContentRuleDto` CLIMutable type and map to `Domain.ContentRule`:

```yaml
# rules.yaml format
content_rules:
    - name: payslip-by-content
      match:
          content_any: ["gross pay", "tax withheld", "net pay"]
      category: payslips
      confidence: 0.85

    - name: bank-statement-by-content
      match:
          has_table: true
          table_headers_any: ["narrative", "description"]
          table_headers_all: ["date", "balance"]
      category: bank-statements
      confidence: 0.80
```

Wire the parsed rules into `ServiceHost.runSyncCycle` → `Classifier.processFile` → `ContentClassifier.classify`.

**Proof**: Add a content rule to `rules.yaml` matching specific keywords → document matching those keywords gets classified → `classification_tier = 'content'` in DB.

**Commit**: `feat: parse content_rules from rules.yaml and wire into classification pipeline`

---

## Fix 4: Wire ActivityLog into ServiceHost

`ActivityLog.fs` exists with `logEvent`, `getRecent`, `purgeOld` but is never called. Add `ActivityLog.logEvent` calls at key points in `ServiceHost.runSyncCycle`:

| Point                  | Level | Category | Example message                                     |
| ---------------------- | ----- | -------- | --------------------------------------------------- |
| Sync cycle start       | info  | sync     | "Sync cycle started"                                |
| Email sync per account | info  | sync     | "john-personal: 3 messages, 2 attachments"          |
| Backfill per account   | info  | sync     | "Backfill john-personal: 200 scanned (74%)"         |
| Classification batch   | info  | classify | "Classified 5 documents (3 rule, 1 content, 1 llm)" |
| Extraction batch       | info  | extract  | "Extracted 3 documents"                             |
| Reminder evaluation    | info  | reminder | "Created 2 new reminders"                           |
| Sync cycle complete    | info  | sync     | "Sync cycle completed"                              |
| Any error              | error | (varies) | The error message                                   |

Pass the `db` algebra to `logEvent` — it writes to the `activity_log` table.

Also call `purgeOld` once per sync cycle to auto-clean entries older than 7 days.

**Proof**: Run the app → trigger a sync → switch to Activity mode in activity bar → see recent events listed with timestamps, levels, and messages.

**Commit**: `feat: integrate ActivityLog into service pipeline — events at all key points`

---

## Fix 5: Navigator content panel wiring (Silver thread — largest fix)

The activity bar's 5 mode buttons set `NavigatorMode` and update `NavigatorTitle` text, but the navigator column doesn't swap content. Each mode needs to populate the navigator `StackPanel` (or `ScrollViewer` content) with mode-specific UI.

### 📋 Action Items mode

- Show overdue reminders section (red header) with reminder items
- Show upcoming reminders section (amber header)
- Click reminder → content pane shows detail (reuse `CreateReminderCard` pattern)
- Empty state: "✅ All clear"

### 📁 Documents mode

- Category tree at top: each category as a clickable row with count
- Document list below when a category is selected
- Use `DocumentBrowser.listCategories` and `DocumentBrowser.listDocuments`
- Click document → content pane shows `DocumentBrowser.getDocumentDetail`
- Document detail: metadata grid + extracted text/markdown preview + action buttons (Open File, Reclassify)

### 📧 Threads mode

- Thread list from `Threads.listThreads` — subject, message count, date, participants
- Click thread → content pane shows `Threads.getThreadDetail` — chronological message list
- Empty state: "No email threads found"

### ⏰ Timeline mode

- Documents grouped by day: `SELECT * FROM documents ORDER BY ingested_at DESC LIMIT 100`
- Group by date → section headers ("Today", "Yesterday", "2 Apr 2026")
- Click document → content pane shows detail

### ⚡ Activity mode

- Event list from `ActivityLog.getRecent(100)`
- Level icon (🔵 info, 🟢 success, 🟡 warning, 🔴 error) + timestamp + message
- Auto-refreshes on the 5-second timer

For each mode:

1. `ShellWindow.axaml.cs`: method `RebuildNavigatorFor{Mode}()` that clears and rebuilds navigator content
2. Call appropriate Core queries via ViewModel
3. Wire click handlers to update content pane

**Definition of done** (per `.github/copilot-instructions.md`):

- XAML controls exist
- Code-behind wired with event handlers
- Buttons/clicks do something
- Data is live from DB
- Build clean
- Smoke test each mode

**Proof**: Launch app → click each activity bar icon → navigator shows real content (not just a title) → click items → content pane updates with detail.

**Commit**: `feat: wire navigator content panels for all 5 modes with live data`

---

## Final verification

```
dotnet build hermes.slnx --nologo    # 0 errors
dotnet test --nologo --no-build       # all tests pass, 0 skipped
```

Walk through every mode manually:

- [ ] 📋 Action Items: reminders show, Mark Paid works
- [ ] 📁 Documents: categories load, document list shows, detail renders
- [ ] 📧 Threads: thread list shows, clicking shows chronological messages
- [ ] ⏰ Timeline: documents grouped by day
- [ ] ⚡ Activity: events listed after a sync cycle
- [ ] Chat pane: still works alongside all modes

```
git push -u origin feat/wiring-completion
```

Do NOT merge to master — that will be done after review and after the test agent finishes.
