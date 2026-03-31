---
description: "Step-by-step implementation plan for Hermes next features: Settings expansion, Email Backfill, Bills & Reminders. Each task is a silver-thread vertical slice — backend + UI + tests + proof."
---

# Hermes — Implementation Plan: Settings → Backfill → Reminders

## Prerequisites

Before starting, commit and verify the current working state:

```
git add -A
git commit -m "feat: Azure OpenAI chat provider, Stats.fs, ShellViewModel, UI redesign XAML"
dotnet build hermes.slnx --nologo
dotnet test tests/Hermes.Tests/Hermes.Tests.fsproj --nologo
```

**Expected**: 0 errors, 0 warnings, 241+ tests pass. If not, fix before proceeding.

**Critical rules — read FIRST**:
- Read `.github/copilot-instructions.md` — especially "Agent Workflow Requirements"
- **F# code** must go through `@fsharp-dev` agent
- **C# code** must go through `@csharp-dev` agent
- **UI definition of done**: XAML exists → code-behind wired → buttons functional → data live → build clean → smoke tested
- **Every task has a PROOF** — do not mark complete until the proof passes

---

## Task 1: Schema Migration v2 → v3

**Goal**: Single shared migration that adds both backfill columns and reminders table.

### What to build

**File: `src/Hermes.Core/Database.fs`**

1. Change `CurrentSchemaVersion` from `2` to `3`
2. Add migration function `migrateV2toV3`:

```sql
-- Backfill columns on sync_state
ALTER TABLE sync_state ADD COLUMN backfill_page_token TEXT;
ALTER TABLE sync_state ADD COLUMN backfill_total_estimate INTEGER;
ALTER TABLE sync_state ADD COLUMN backfill_scanned INTEGER NOT NULL DEFAULT 0;
ALTER TABLE sync_state ADD COLUMN backfill_completed INTEGER NOT NULL DEFAULT 0;
ALTER TABLE sync_state ADD COLUMN backfill_started_at TEXT;

-- Reminders table
CREATE TABLE IF NOT EXISTS reminders (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    document_id     INTEGER REFERENCES documents(id),
    vendor          TEXT,
    amount          REAL,
    due_date        TEXT,
    category        TEXT NOT NULL,
    status          TEXT NOT NULL DEFAULT 'active',
    snoozed_until   TEXT,
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    completed_at    TEXT,
    dismissed_at    TEXT,
    trigger_name    TEXT,
    notes           TEXT
);
CREATE INDEX IF NOT EXISTS idx_reminder_status ON reminders(status);
CREATE INDEX IF NOT EXISTS idx_reminder_due ON reminders(due_date);
CREATE INDEX IF NOT EXISTS idx_reminder_doc ON reminders(document_id);
```

3. Wire migration into `initSchema` — detect current version, run migration if < 3

### What to test

**File: `tests/Hermes.Tests/DatabaseTests.fs`**

- `Database_InitSchema_MigratesV2ToV3_AddsBackfillColumns` — create v2 DB, run initSchema, verify `backfill_page_token` column exists in `sync_state`
- `Database_InitSchema_MigratesV2ToV3_CreatesRemindersTable` — verify `reminders` table exists after migration
- `Database_InitSchema_V3AlreadyApplied_NoError` — run initSchema twice, no crash

### Build + verify

```
dotnet build hermes.slnx --nologo
dotnet test tests/Hermes.Tests/Hermes.Tests.fsproj --nologo
```

### PROOF

All tests pass. Open SQLite DB manually → `PRAGMA table_info(sync_state)` shows backfill columns → `SELECT name FROM sqlite_master WHERE type='table'` includes `reminders`.

### Commit

```
feat: schema migration v3 — backfill columns and reminders table
```

---

## Task 2: Backfill + Reminder Config Types

**Goal**: Domain types and YAML parsing for backfill config and reminder types.

### What to build

**File: `src/Hermes.Core/Domain.fs`**

Add after `AzureOpenAIConfig`:

```fsharp
type BackfillConfig =
    { Enabled: bool
      Since: DateTimeOffset option
      BatchSize: int
      AttachmentsOnly: bool
      IncludeBodies: bool }

type ReminderStatus =
    | Active
    | Snoozed
    | Completed
    | Dismissed

type Reminder = {
    Id: int64
    DocumentId: int64 option
    Vendor: string option
    Amount: decimal option
    DueDate: DateTimeOffset option
    Category: string
    Status: ReminderStatus
    SnoozedUntil: DateTimeOffset option
    CreatedAt: DateTimeOffset
    CompletedAt: DateTimeOffset option
}

type ReminderSummary = {
    OverdueCount: int
    UpcomingCount: int
    TotalActiveAmount: decimal
}
```

Update `AccountConfig` to include backfill:

```fsharp
type AccountConfig =
    { Label: string
      Provider: string
      Backfill: BackfillConfig }
```

**File: `src/Hermes.Core/Config.fs`**

Add `BackfillDto` CLIMutable type. Update `AccountDto` to include optional `BackfillDto`. Update `toConfig` mapping with defaults:
- `Enabled = true`, `Since = None`, `BatchSize = 50`, `AttachmentsOnly = true`, `IncludeBodies = false`

**File: `tests/Hermes.Tests/TestHelpers.fs`**

Update `testConfig` to include `Backfill` in `AccountConfig`.

**File: `tests/Hermes.Tests/ConfigTests.fs`**

- `Config_ParseYaml_WithBackfill_ParsesBackfillConfig` — YAML with backfill section → config has correct values
- `Config_ParseYaml_NoBackfill_DefaultsToEnabled` — account without backfill section → `Enabled = true, BatchSize = 50`

### Build + verify

```
dotnet build hermes.slnx --nologo
dotnet test tests/Hermes.Tests/Hermes.Tests.fsproj --nologo
```

### PROOF

New config tests pass. Existing 241+ tests still pass (no regressions from AccountConfig change).

### Commit

```
feat: backfill + reminder domain types and config parsing
```

---

## Task 3: Expanded Settings Dialog (Silver Thread)

**Goal**: The Settings dialog grows from 3 fields to sectioned form. User can configure chat provider, see accounts with backfill controls, and save everything.

### What to build

**File: `src/Hermes.App/HermesServiceBridge.cs`**

Add methods:

```csharp
/// Writes complete config to config.yaml, replacing the file.
public async Task UpdateFullConfigAsync(
    int syncIntervalMinutes, int minAttachmentSizeKb,
    string chatProvider, string ollamaUrl, string ollamaModel,
    string? azureEndpoint, string? azureApiKey, string? azureDeployment)

/// Removes account from config + optionally deletes token.
public async Task RemoveAccountFromConfigAsync(string label, bool deleteToken)
```

Implementation: read current config.yaml, deserialise, modify fields, serialise back with YamlDotNet `Serializer`, write file. For remove: filter out the account entry, write back, optionally delete `tokens/gmail_{label}.json`.

**File: `src/Hermes.App/Views/ShellWindow.axaml.cs`** — `ShowSettingsDialogAsync` method

Replace the current 3-field dialog with a sectioned form:

**Section: General**
- Sync interval (NumericUpDown)
- Min attachment size (NumericUpDown)

**Section: AI / Chat**
- Radio buttons: Ollama / Azure OpenAI
- Ollama URL (TextBox), Ollama model (TextBox)
- Azure endpoint (TextBox), Azure API key (TextBox with `PasswordChar`), Azure deployment (TextBox)
- Radio button click toggles visibility of provider-specific fields

**Section: Accounts**
- For each account in config: row with label, [Re-auth] button, [×] button
- Per-account: backfill toggle (ToggleSwitch), batch size (NumericUpDown)
- [+ Add Gmail Account] button at bottom (reuse existing `AddGmailAccountAsync`)

**Section: Watch Folders**
- For each folder: row with path + patterns + [×] button
- [+ Add Folder] at bottom (reuse existing `AddWatchFolderAsync`)

**[Save] button** calls `UpdateFullConfigAsync` with all field values.

### Important wiring rules

- Every field pre-populated from `_vm.Bridge.Config` on dialog open
- Azure API key NEVER displayed in plain text — use `PasswordChar="●"`
- Radio buttons: when Ollama selected, hide Azure fields and vice versa
- Remove account: show confirmation dialog first
- Save button: validate fields, call bridge method, show success/error status
- Dialog size: `Width=520, Height=600, CanResize=True`

### What to test

No new unit tests (UI wiring). Verified by PROOF.

### Build + verify

```
dotnet build hermes.slnx --nologo
dotnet test tests/Hermes.Tests/Hermes.Tests.fsproj --nologo
```

### PROOF

1. `dotnet run --project src/Hermes.App` → click ⚙ → Settings dialog opens with current config values populated
2. Switch to Azure OpenAI → Ollama fields hide, Azure fields appear
3. Fill Azure endpoint + key + deployment → Save → close & reopen → fields persist
4. Run a chat query → response uses Azure OpenAI (check response quality / latency — noticeably better than Ollama)
5. Switch back to Ollama → Save → chat uses Ollama again

### Commit

```
feat: expanded Settings dialog with chat provider, account management, full config save
```

---

## Task 4: Backfill Engine + Progress UI (Silver Thread)

**Goal**: Historical email ingestion runs after forward sync, with live progress in the account card.

### What to build

**File: `src/Hermes.Core/Algebra.fs`**

Add to `EmailProvider`:

```fsharp
type MessagePage =
    { Messages: Domain.EmailMessage list
      NextPageToken: string option
      ResultSizeEstimate: int64 }

type EmailProvider =
    { listNewMessages: DateTimeOffset option -> Task<Domain.EmailMessage list>
      getAttachments: string -> Task<Domain.EmailAttachment list>
      getMessageBody: string -> Task<string option>
      listMessagePage: string option -> string option -> int -> Task<MessagePage> }
```

**File: `src/Hermes.Core/GmailProvider.fs`**

Implement `listMessagePage` — paginated `messages.list` call with `pageToken`, `query`, `maxResults`. Return `MessagePage`.

**File: `src/Hermes.Core/EmailSync.fs`**

Add `backfillAccount` function:
- Load backfill state from `sync_state` (backfill_page_token, backfill_scanned, backfill_completed)
- If completed, skip
- Build Gmail query from `BackfillConfig` (has:attachment, after:timestamp if `since` set)
- Page through messages using `listMessagePage`, processing `batch_size` new messages per call
- For each: check `messageExists`, if new → `recordMessage`, download attachments → `unclassified/`
- Save `backfill_page_token` and `backfill_scanned` after each page
- If no more pages → set `backfill_completed = 1`

Add helper functions:
- `loadBackfillState` — reads backfill columns from sync_state
- `saveBackfillState` — updates backfill columns

**File: `src/Hermes.Core/Stats.fs`**

Add:

```fsharp
type BackfillProgress =
    { Account: string
      Scanned: int
      TotalEstimate: int64
      Completed: bool
      StartedAt: DateTimeOffset option }

let getBackfillProgress (db: Algebra.Database) (account: string) : Task<BackfillProgress option>
```

Query: `SELECT backfill_scanned, backfill_total_estimate, backfill_completed, backfill_started_at FROM sync_state WHERE account = @acc`

**File: `src/Hermes.Core/ServiceHost.fs`**

In `runSyncCycle`, after step 3 (classify), before step 4 (extract):

```fsharp
// 3.5 — Backfill historical email
for account in config.Accounts do
    if account.Backfill.Enabled then
        try
            let! provider = GmailProvider.create configDir account.Label logger
            let! _backfillResult = EmailSync.backfillAccount fs db logger clock provider config account
            ()
        with ex ->
            logger.warn $"Backfill failed for {account.Label}: {ex.Message}"
```

**File: `src/Hermes.App/ViewModels/ShellViewModel.cs`**

Add `BackfillProgress` property per account. `RefreshAsync` calls `Stats.getBackfillProgress` for each account.

**File: `src/Hermes.App/Views/ShellWindow.axaml + .cs`**

Account card gains:
- `ProgressBar` for backfill (same style as extracted/embedded bars)
- Text: `"Backfill: {scanned:N0} / ~{estimate:N0}"` or `"Backfill: complete ✓"`
- Hidden when backfill is not enabled or no data yet

Status bar includes backfill state: `"Backfilling {account} ({pct}%)..."` when active.

### What to test

**File: `tests/Hermes.Tests/EmailSyncTests.fs`** (or new `BackfillTests.fs`)

- `Backfill_ProcessesBatchSize_StopsAtLimit` — fake provider returns 100 messages, batch_size=10 → only 10 processed
- `Backfill_SkipsExistingMessages` — messages already in DB → skipped, budget not consumed
- `Backfill_SavesPageToken_ResumesCorrectly` — process one page, verify token saved, call again → resumes from token
- `Backfill_CompletesWhenNoMorePages` — last page has no nextPageToken → backfill_completed = 1
- `Backfill_DisabledConfig_Skips` — backfill.enabled = false → nothing happens

Update `tests/Hermes.Tests/TestHelpers.fs` — fake `EmailProvider` needs `listMessagePage` field.

### Build + verify

```
dotnet build hermes.slnx --nologo
dotnet test tests/Hermes.Tests/Hermes.Tests.fsproj --nologo
```

### PROOF

1. All backfill tests pass
2. `dotnet run --project src/Hermes.App` → account card shows backfill progress bar
3. After a sync cycle, backfill_scanned increases (visible in account card on next refresh)
4. Status bar shows "Backfilling {account} (N%)..." during active backfill

### Commit

```
feat: email backfill engine with paginated Gmail sync and live progress UI
```

---

## Task 5: Reminders Core Module (Silver Thread — the big one)

**Goal**: Bill detection runs after extraction, reminders show in a TODO tab, user can mark paid / snooze / dismiss. The complete loop.

### What to build — Core (F#)

**File: `src/Hermes.Core/Reminders.fs`** (NEW)

Place in fsproj between `Stats.fs` and `Chat.fs`.

Functions:

```fsharp
[<RequireQualifiedAccess>]
module Reminders =

    let private billCategories =
        set [ "invoices"; "utilities"; "insurance"; "subscriptions"; "rates-and-tax" ]

    /// Detect if a document is a bill with an actionable due date.
    let detectBill (now: DateTimeOffset) (doc: Domain.Document) : Domain.Reminder option

    /// Scan newly extracted documents and create reminders.
    let evaluateNewDocuments (db: Algebra.Database) (logger: Algebra.Logger) (now: DateTimeOffset) : Task<int>

    /// Get active + un-snoozed reminders, JOINed with document paths.
    let getActive (db: Algebra.Database) (now: DateTimeOffset) : Task<(Domain.Reminder * string option * string option) list>
    // Returns (reminder, saved_path option, original_name option)

    /// Get recently completed reminders (last 7 days).
    let getRecentlyCompleted (db: Algebra.Database) : Task<Domain.Reminder list>

    /// Get summary for sidebar badge.
    let getSummary (db: Algebra.Database) (now: DateTimeOffset) : Task<Domain.ReminderSummary>

    /// Mark as paid.
    let markCompleted (db: Algebra.Database) (id: int64) (now: DateTimeOffset) : Task<unit>

    /// Snooze for N days.
    let snooze (db: Algebra.Database) (id: int64) (days: int) (now: DateTimeOffset) : Task<unit>

    /// Dismiss permanently.
    let dismiss (db: Algebra.Database) (id: int64) (now: DateTimeOffset) : Task<unit>

    /// Un-snooze expired reminders.
    let unsnoozeExpired (db: Algebra.Database) (now: DateTimeOffset) : Task<int>
```

Key SQL for `getActive`:
```sql
SELECT r.*, d.saved_path, d.original_name
FROM reminders r
LEFT JOIN documents d ON r.document_id = d.id
WHERE (r.status = 'active'
       OR (r.status = 'snoozed' AND r.snoozed_until <= @now))
ORDER BY r.due_date ASC
```

Key rule for `detectBill`:
- Only categories in `billCategories`
- Must have `extracted_amount > 0` AND parseable `extracted_date`
- Due date must be within -30 to +60 days of `now`
- Dedup: skip if `SELECT COUNT(*) FROM reminders WHERE document_id = @id AND status != 'dismissed'` > 0

**File: `src/Hermes.Core/Hermes.Core.fsproj`**

Add `<Compile Include="Reminders.fs" />` between `Stats.fs` and `Chat.fs`.

**File: `src/Hermes.Core/ServiceHost.fs`**

After extraction step (step 4), before embedding (step 5):

```fsharp
// 4.5 — Evaluate bill reminders
logger.debug "Evaluating bill reminders..."
let! newReminders = Reminders.evaluateNewDocuments db logger (clock.utcNow())
if newReminders > 0 then logger.info $"Created {newReminders} new reminder(s)"

// 4.6 — Un-snooze expired reminders
let! unsnoozed = Reminders.unsnoozeExpired db (clock.utcNow())
if unsnoozed > 0 then logger.info $"Un-snoozed {unsnoozed} reminder(s)"
```

### What to build — UI (C#)

**File: `src/Hermes.App/ViewModels/ShellViewModel.cs`**

Add:

```csharp
public record ReminderItem(
    long Id, string? Vendor, string? Amount, string? DueDate,
    string DueLabel, bool IsOverdue, string? DocumentPath, string? FileName);

// Collections
public ObservableCollection<ReminderItem> OverdueReminders { get; }
public ObservableCollection<ReminderItem> UpcomingReminders { get; }
public ObservableCollection<ReminderItem> CompletedReminders { get; }

// Properties
public int ActionItemCount { get; }
public string ActionItemSummary { get; }
public bool IsChatActive { get; set; } = true;

// Actions
public async Task MarkPaidAsync(long reminderId);
public async Task SnoozeAsync(long reminderId, int days = 7);
public async Task DismissAsync(long reminderId);
```

`RefreshAsync` extension: call `Reminders.getActive`, split into overdue/upcoming, call `Reminders.getSummary`. Map F# tuples to `ReminderItem`.

**File: `src/Hermes.App/Views/ShellWindow.axaml`**

In the right panel (Grid.Column="2"), above the chat content:

1. Tab bar: two `ToggleButton`s — `ChatTabButton` ("💬 Chat") and `TodoTabButton` ("📋 Action Items (N)")
2. Two content areas with visibility toggled:
   - Chat area (existing: `ChatScroller` + `ChatPanel` + input bar)
   - TODO area (new: `ScrollViewer` with `StackPanel` containing sections)

In the left sidebar, add ACTION ITEMS expander below Watch Folders:

```xml
<Expander Classes="section" x:Name="ActionItemsExpander" IsExpanded="True" IsVisible="False">
  <Expander.Header>
    <TextBlock Text="ACTION ITEMS" FontSize="10" FontWeight="SemiBold" ... />
  </Expander.Header>
  <StackPanel Margin="20,4,12,12" Spacing="4">
    <TextBlock x:Name="ActionItemsBadge" Text="" FontSize="11" />
  </StackPanel>
</Expander>
```

**File: `src/Hermes.App/Views/ShellWindow.axaml.cs`**

1. Resolve new controls: `ChatTabButton`, `TodoTabButton`, `TodoPanel`, `ActionItemsExpander`, `ActionItemsBadge`

2. Tab toggle:
```csharp
chatTabButton.Click += (_, _) => { _vm.IsChatActive = true; UpdateTabVisibility(); };
todoTabButton.Click += (_, _) => { _vm.IsChatActive = false; UpdateTabVisibility(); };
```

3. `UpdateTabVisibility()` — show/hide chat area vs TODO area

4. `OnViewModelChanged` — handle `OverdueReminders`, `UpcomingReminders`, `CompletedReminders`, `ActionItemCount`:
   - Rebuild TODO panel content (clear children, add section headers, add reminder cards)
   - Update sidebar badge text and visibility
   - Update tab button text with count

5. `CreateReminderCard(ReminderItem item)` — returns a `Border` with:
   - Left colour edge (red if overdue, amber if upcoming)
   - Vendor + amount header
   - Due date with relative label ("3 days overdue" / "in 5 days")
   - Document file link (📄 filename, `PointerPressed` → open in default app)
   - Three buttons: [Mark Paid ✓] [Snooze 7d ⏰] [Dismiss ×]
   - Button click handlers call `_vm.MarkPaidAsync(id)` etc, then rebuild the panel

6. Empty state: when 0 reminders, TODO panel shows "✅ All clear — no bills or reminders" centered text

### What to test

**File: `tests/Hermes.Tests/ReminderTests.fs`** (NEW)

Add to fsproj.

- `Reminders_DetectBill_InvoiceWithDueDate_CreatesReminder` — document in "invoices" category with amount + date in range → Some
- `Reminders_DetectBill_WrongCategory_ReturnsNone` — document in "medical" category → None
- `Reminders_DetectBill_OldDate_ReturnsNone` — due date 60+ days ago → None
- `Reminders_DetectBill_NoAmount_ReturnsNone` — no extracted_amount → None
- `Reminders_EvaluateNew_InsertsReminders` — insert test documents → evaluateNewDocuments → reminders table has entries
- `Reminders_EvaluateNew_DeduplicatesExisting` — run twice → no duplicates
- `Reminders_MarkCompleted_ChangesStatus` — insert → mark completed → getActive excludes it
- `Reminders_Snooze_HidesUntilExpiry` — snooze 7d → not in getActive → advance clock → back in getActive
- `Reminders_Dismiss_PermanentlyRemoves` — dismiss → not in getActive, not in getRecentlyCompleted
- `Reminders_GetSummary_CorrectCounts` — mix of overdue + upcoming → correct summary

### Build + verify

```
dotnet build hermes.slnx --nologo
dotnet test tests/Hermes.Tests/Hermes.Tests.fsproj --nologo
```

### PROOF

1. All reminder tests pass
2. `dotnet run --project src/Hermes.App`
3. Insert a test row via SQLite: `INSERT INTO documents (source_type, saved_path, category, sha256, extracted_amount, extracted_date, extracted_at, ingested_at) VALUES ('manual_drop', '/test/invoice.pdf', 'invoices', 'abc123', 100.0, '2026-04-05', datetime('now'), datetime('now'))`
4. Wait for sync cycle (or click Sync Now)
5. Action Items tab shows "(1)" → click tab → see reminder card with amount + due date
6. Click "Mark Paid" → card moves to completed section → tab shows "(0)"
7. Sidebar badge updates (hidden when 0)
8. Status bar shows "1 action item" → then "0 action items" after marking paid

### Commit

```
feat: bills & reminders — detection, TODO panel, mark paid/snooze/dismiss
```

---

## Task 6: MCP Tools for Reminders

**Goal**: External agents can list and manage reminders via MCP.

### What to build

**File: `src/Hermes.Core/McpServer.fs`**

Add to `toolDefinitions`:

```fsharp
{ Name = "hermes_list_reminders"
  Description = "List active bill reminders and action items with amounts and due dates."
  InputSchema = mkSchema
    [ "status", stringProp "Filter: 'active', 'overdue', 'upcoming', 'completed', 'all' (default: active)"
      "limit", intProp "Max results (default 20)" ]
    [] }

{ Name = "hermes_update_reminder"
  Description = "Mark a reminder as paid, snoozed, or dismissed."
  InputSchema = mkSchema
    [ "reminder_id", intProp "Reminder ID"
      "action", stringProp "One of: 'complete', 'snooze', 'dismiss'"
      "snooze_days", intProp "Days to snooze (default 7, only for snooze action)" ]
    [ "reminder_id"; "action" ] }
```

**File: `src/Hermes.Core/McpTools.fs`**

Add handler functions:
- `listReminders` — calls `Reminders.getActive` or `getRecentlyCompleted` based on status parameter, serialises to JSON
- `updateReminder` — parses action, calls `markCompleted`/`snooze`/`dismiss`, returns confirmation JSON

**File: `src/Hermes.Core/McpServer.fs`** — `handleToolCall`

Add cases for the two new tool names, dispatching to `McpTools` functions.

### What to test

**File: `tests/Hermes.Tests/McpTests.fs`**

- `MCP_ListReminders_ReturnsActiveReminders` — insert reminders → call tool → JSON response contains them
- `MCP_UpdateReminder_MarkComplete_ChangesStatus` — insert reminder → call update tool → status changes in DB
- `MCP_ListReminders_FilterOverdue_OnlyReturnsOverdue` — mix of overdue/upcoming → filter works

### Build + verify

```
dotnet build hermes.slnx --nologo
dotnet test tests/Hermes.Tests/Hermes.Tests.fsproj --nologo
```

### PROOF

1. All MCP tests pass
2. Start Hermes, ensure MCP server is listening on localhost:21740
3. Send JSON-RPC to `hermes_list_reminders` → get reminder list back
4. Send `hermes_update_reminder` with action=complete → reminder status changes
5. UI reflects the change on next refresh

### Commit

```
feat: MCP tools for listing and managing bill reminders
```

---

## Task 7: First-Run Notification + CLI Backfill Reset

**Goal**: Polish the new-account experience and add CLI control.

### What to build

**File: `src/Hermes.App/Views/ShellWindow.axaml.cs`**

In `AddGmailAccountAsync`, after successful auth + config save:

```csharp
_vm.Messages.Add(new ChatMessage("Hermes",
    $"I'm scanning your email history for {label}. " +
    "This runs in the background — about 4,800 messages per day at default settings. " +
    "You'll start seeing historical documents in search results as they're indexed.",
    false, []));
```

**File: `src/Hermes.Cli/Program.fs`**

Add `backfill reset --account {label}` subcommand:

```fsharp
| "backfill" :: "reset" :: "--account" :: label :: _ ->
    let db = Database.fromPath dbPath
    try
        let! _ = db.execNonQuery
            """UPDATE sync_state
               SET backfill_page_token = NULL,
                   backfill_scanned = 0,
                   backfill_completed = 0,
                   backfill_started_at = NULL
               WHERE account = @acc"""
            [ ("@acc", label :> obj) ]
        printfn $"Backfill reset for account '{label}'. Will restart on next sync cycle."
    finally
        db.dispose ()
```

### What to test

No new unit tests — verified by PROOF.

### Build + verify

```
dotnet build hermes.slnx --nologo
dotnet test tests/Hermes.Tests/Hermes.Tests.fsproj --nologo
```

### PROOF

1. Add a new Gmail account in the app → chat shows the backfill notification message
2. `hermes backfill reset --account test-label` → prints confirmation
3. Check DB: `SELECT backfill_completed FROM sync_state WHERE account = 'test-label'` → 0

### Commit

```
feat: first-run backfill notification and CLI backfill reset command
```

---

## Task 8: Update Tracking + Final Verification

**Goal**: All project tracking files are accurate. Full build + test.

### What to update

1. **`.project/testing-register.md`** — add all new test entries (schema migration, config, backfill, reminders, MCP)
2. **`.project/STATUS.md`** — mark Backfill and Bills & Reminders as ✅ Done
3. **`.project/phases.md`** — update status to Done for Backfill and Bills & Reminders

### Final verification

```
dotnet build hermes.slnx --nologo    # 0 errors, 0 warnings
dotnet test --nologo                  # all tests pass
dotnet run --project src/Hermes.App   # launches, window renders, all panels work
```

Walk through every feature:
- [ ] Settings dialog: all sections render, save works, provider switch works
- [ ] Account card: shows backfill progress (or "complete" or hidden)
- [ ] Chat: search works, AI toggle switches between Ollama/Azure OpenAI
- [ ] Action Items tab: toggle works, cards render, buttons respond
- [ ] Mark Paid: card moves to completed
- [ ] Snooze: card disappears (will reappear after snooze period)
- [ ] Dismiss: confirmation → permanently removed
- [ ] Sidebar badge: correct counts
- [ ] Status bar: includes doc count + action item count
- [ ] MCP: `hermes_list_reminders` and `hermes_update_reminder` respond

### Commit

```
docs: update tracking — backfill and reminders features complete
```

---

## Summary — Task Order

```
Task 1: Schema migration v3                              (foundation)
Task 2: Config types for backfill + reminders            (types, parsing)
Task 3: Expanded Settings dialog                         (UI silver thread: config → save → reload)
Task 4: Backfill engine + progress UI                    (UI silver thread: sync → progress bar)
Task 5: Reminders core + TODO panel                      (UI silver thread: detect → show → act)
Task 6: MCP tools for reminders                          (agent integration)
Task 7: First-run notification + CLI reset               (polish)
Task 8: Tracking update + final verification             (done)
```

Each task is one commit. Each commit leaves the project in a building, tested, working state.
