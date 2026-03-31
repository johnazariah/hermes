# Hermes — Bills & Reminders: First Skill Demo

> Design doc for the "Upcoming Bills" skill — Hermes's first active capability.  
> Builds on the trigger/skill architecture from doc 10 (Agent Evolution).  
> Created: 2026-03-31

---

## 1. What This Is

The first concrete skill that brings Hermes from passive index to active assistant. When Hermes extracts an invoice or bill with a due date, it automatically creates a **reminder** visible in a new **Action Items** panel in the shell window.

This is the minimum viable trigger→skill→UI loop:

```
Document arrives → Extract → detect due date + amount
                     ↓
                  Trigger: "invoice with due date within ±30 days"
                     ↓
                  Skill: create reminder
                     ↓
                  UI: Action Items panel shows the bill
                     ↓
                  User: marks as paid / dismisses / snoozes
```

---

## 2. User Experience

### What the user sees

After Hermes has been running and indexing documents, the shell window gains a new panel:

```
┌─────────────────────────────────────────────────────────┐
│  Hermes — Document Intelligence                    ─ □ × │
├───────────┬─────────────────────────────────────────────┤
│           │                                             │
│  STATUS   │  ACTION ITEMS (3)              [Chat │ TODO]│
│  PANEL    │                                             │
│           │  ⚠️ OVERDUE                                 │
│  Services │  ┌────────────────────────────────────────┐ │
│  Index    │  │ 🔴 Allianz Car Insurance    $1,234.00  │ │
│  ...      │  │    Due: 15 Mar 2026 (16 days ago)      │ │
│  ─────── │  │    📄 Allianz-Policy-2025.pdf          │ │
│           │  │    [Mark Paid] [Snooze 7d] [Dismiss]   │ │
│  ACTION   │  └────────────────────────────────────────┘ │
│  ITEMS    │                                             │
│  🔴 1     │  📋 UPCOMING                               │
│  🟡 2     │  ┌────────────────────────────────────────┐ │
│           │  │ 🟡 AGL Energy              $287.50     │ │
│           │  │    Due: 8 Apr 2026 (in 8 days)         │ │
│           │  │    📄 2026-03-15_agl_invoice.pdf       │ │
│           │  │    [Mark Paid] [Snooze 7d] [Dismiss]   │ │
│           │  ├────────────────────────────────────────┤ │
│           │  │ 🟡 Telstra Mobile          $89.00      │ │
│           │  │    Due: 12 Apr 2026 (in 12 days)       │ │
│           │  │    📄 2026-03-20_telstra_invoice.pdf   │ │
│           │  │    [Mark Paid] [Snooze 7d] [Dismiss]   │ │
│           │  └────────────────────────────────────────┘ │
│           │                                             │
│           │  ✅ RECENTLY COMPLETED                      │
│           │  └ Allianz Home Insurance $456.00 — paid   │
│           │                                             │
├───────────┴─────────────────────────────────────────────┤
│  ●● Ready · 1,234 docs · 3 action items                │
└─────────────────────────────────────────────────────────┘
```

### Interaction model

| Action | What happens |
|--------|-------------|
| **Mark Paid** | Moves to "completed" section, records `completed_at` timestamp |
| **Snooze 7d** | Hides from list until snooze expires, then reappears |
| **Dismiss** | Permanently removes — this isn't a bill or user doesn't care |
| **Click document link** | Opens the source PDF in default app (existing behaviour) |
| **Click item** | Expands to show full details (vendor, amount, date, category, snippet) |

### Navigation: Chat ↔ TODO

The main area has two views, toggled by tab buttons at the top right:
- **Chat** (default) — the existing search/chat interface
- **TODO** — the Action Items panel

The left sidebar also shows a summary badge:
```
ACTION ITEMS
🔴 1  🟡 2
```

This gives at-a-glance visibility without leaving the chat.

---

## 3. Data Model

### `reminders` table

```sql
CREATE TABLE IF NOT EXISTS reminders (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    document_id     INTEGER REFERENCES documents(id),
    vendor          TEXT,
    amount          REAL,
    due_date        TEXT,           -- ISO 8601
    category        TEXT NOT NULL,  -- 'bill', 'insurance', 'subscription', etc.
    status          TEXT NOT NULL DEFAULT 'active',
        -- 'active', 'snoozed', 'completed', 'dismissed'
    snoozed_until   TEXT,           -- ISO 8601, NULL unless snoozed
    created_at      TEXT NOT NULL DEFAULT (datetime('now')),
    completed_at    TEXT,
    dismissed_at    TEXT,
    trigger_name    TEXT,           -- which trigger created it
    notes           TEXT            -- user-added notes (future)
);

CREATE INDEX IF NOT EXISTS idx_reminder_status ON reminders(status);
CREATE INDEX IF NOT EXISTS idx_reminder_due ON reminders(due_date);
CREATE INDEX IF NOT EXISTS idx_reminder_doc ON reminders(document_id);
```

### Domain types

```fsharp
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

### Schema migration

This is part of the v3 schema migration (alongside backfill columns):

```fsharp
let private migrateV2toV3 (db: Database) =
    task {
        // Backfill columns (from doc 11)...
        // Reminders table
        let! _ = db.execNonQuery remindersSchemaSql []
        let! _ = db.execNonQuery "INSERT OR REPLACE INTO schema_version (version) VALUES (3)" []
        return ()
    }
```

---

## 4. Trigger: Bill Detection

The built-in "bill detector" trigger fires after extraction. It doesn't require YAML config — it's a compiled-in default trigger that runs on every extracted document.

### Detection logic

```fsharp
/// Evaluate whether a document looks like a bill with a due date.
let detectBill (doc: Domain.Document) : Reminder option =
    match doc.ExtractedDate, doc.ExtractedAmount with
    | Some dateStr, Some amount when amount > 0m ->
        match DateTimeOffset.TryParse(dateStr) with
        | true, dueDate ->
            let now = DateTimeOffset.UtcNow
            let daysUntilDue = (dueDate - now).TotalDays
            // Only create reminders for bills due within -30 to +60 days
            if daysUntilDue > -30.0 && daysUntilDue < 60.0 then
                Some {
                    Id = 0L  // assigned by DB
                    DocumentId = Some doc.Id
                    Vendor = doc.ExtractedVendor
                    Amount = Some amount
                    DueDate = Some dueDate
                    Category = "bill"
                    Status = Active
                    SnoozedUntil = None
                    CreatedAt = now
                    CompletedAt = None
                }
            else None
        | _ -> None
    | _ -> None
```

### When it runs

Inserted into the pipeline after extraction, before embedding:

```
Classify → Extract → **Trigger Evaluation** → Embed
                           │
                           └─→ detectBill → insert into reminders table
```

### Dedup

Before inserting a reminder, check if one already exists for the same `document_id`:

```sql
SELECT COUNT(*) FROM reminders WHERE document_id = @docId AND status != 'dismissed'
```

If a reminder already exists, skip. This prevents duplicates when documents are re-extracted or updated.

---

## 5. Core Module: `Reminders.fs`

New F# module in `Hermes.Core`:

```fsharp
[<RequireQualifiedAccess>]
module Reminders =

    /// Get all active + snoozed reminders, ordered by due date.
    let getActive (db: Algebra.Database) (now: DateTimeOffset) : Task<Reminder list>

    /// Get recently completed reminders (last 7 days).
    let getRecentlyCompleted (db: Algebra.Database) : Task<Reminder list>

    /// Get summary counts for sidebar badge.
    let getSummary (db: Algebra.Database) (now: DateTimeOffset) : Task<ReminderSummary>

    /// Mark a reminder as completed (paid).
    let markCompleted (db: Algebra.Database) (reminderId: int64) (now: DateTimeOffset) : Task<unit>

    /// Snooze a reminder for N days.
    let snooze (db: Algebra.Database) (reminderId: int64) (days: int) (now: DateTimeOffset) : Task<unit>

    /// Dismiss a reminder permanently.
    let dismiss (db: Algebra.Database) (reminderId: int64) (now: DateTimeOffset) : Task<unit>

    /// Detect and create reminders for newly extracted documents.
    /// Called after extraction batch completes.
    let evaluateNewDocuments (db: Algebra.Database) (logger: Algebra.Logger) (now: DateTimeOffset) : Task<int>

    /// Un-snooze reminders whose snooze period has expired.
    let unsnoozeExpired (db: Algebra.Database) (now: DateTimeOffset) : Task<int>
```

### `evaluateNewDocuments` logic

```sql
-- Find documents that were extracted, are in bill-like categories,
-- have an amount and date, but don't have a reminder yet
SELECT d.* FROM documents d
WHERE d.extracted_amount IS NOT NULL
  AND d.extracted_date IS NOT NULL
  AND d.extracted_at IS NOT NULL
  AND d.category IN ('invoices', 'utilities', 'insurance', 'subscriptions', 'rates-and-tax')
  AND NOT EXISTS (
      SELECT 1 FROM reminders r WHERE r.document_id = d.id
  )
```

### `getActive` query — JOIN for document paths

`Reminder` doesn't carry file paths. The UI needs `saved_path` and `original_name` for the document link. The query JOINs:

```sql
SELECT r.*, d.saved_path, d.original_name
FROM reminders r
LEFT JOIN documents d ON r.document_id = d.id
WHERE (r.status = 'active'
       OR (r.status = 'snoozed' AND r.snoozed_until <= @now))
ORDER BY r.due_date ASC
```

The F# function returns a tuple or enriched record. The ViewModel maps this to `ReminderItem` which includes `DocumentPath` and `FileName`.

For each, run `detectBill`. If it returns `Some reminder`, insert into the `reminders` table.

---

## 6. MCP Tools

Two new MCP tools for external agents:

### `hermes_list_reminders`

```json
{
  "name": "hermes_list_reminders",
  "description": "List active bill reminders and action items. Returns upcoming and overdue bills with amounts and due dates.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "status": {
        "type": "string",
        "enum": ["active", "overdue", "upcoming", "completed", "all"],
        "default": "active"
      },
      "limit": { "type": "integer", "default": 20 }
    }
  }
}
```

### `hermes_update_reminder`

```json
{
  "name": "hermes_update_reminder",
  "description": "Mark a reminder as paid, snoozed, or dismissed.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "reminder_id": { "type": "integer" },
      "action": {
        "type": "string",
        "enum": ["complete", "snooze", "dismiss"]
      },
      "snooze_days": { "type": "integer", "default": 7 }
    },
    "required": ["reminder_id", "action"]
  }
}
```

This means an external agent (Claude via MCP) can ask "what bills are due?" and then "mark the Allianz one as paid" — fulfilling the agent evolution vision.

---

## 7. Shell UI — Full Specification

### 7.1 Main area: Chat/TODO tab toggle

Add tab buttons to the top of the main area:

```
┌─────────────────────────────────────────────┐
│  [💬 Chat]  [📋 Action Items (3)]           │
├─────────────────────────────────────────────┤
│                                              │
│  (chat or TODO content here)                 │
│                                              │
└─────────────────────────────────────────────┘
```

**XAML**: Two `ToggleButton` controls in a `StackPanel` above the content area. A `Panel` (or `Grid`) with two children, visibility toggled by the active tab.

**Wiring**:
- `TodoTabButton.Click` → show TODO panel, hide chat
- `ChatTabButton.Click` → show chat, hide TODO
- Tab button text includes count: `Action Items ({count})`
- Count refreshes on the same timer as other stats
- **Default tab on launch**: always Chat. User switches to TODO manually.
- **When count > 0**: the TODO tab button shows a coloured badge (red if any overdue, yellow otherwise)
- **When count == 0**: the TODO tab button shows no badge, and the TODO panel shows an empty state ("All clear!")

### 7.2 TODO panel content

A `ScrollViewer` containing a `StackPanel` with three sections:

**OVERDUE section** (red header):
- Items where `due_date < now` and `status = 'active'`
- Sorted by due date ascending (most overdue first)
- Red accent colour on the card border

**UPCOMING section** (yellow/amber header):
- Items where `due_date >= now` and `status = 'active'`
- Plus items where `status = 'snoozed'` and `snoozed_until <= now` (auto-unsnoozed)
- Sorted by due date ascending (soonest first)
- Yellow/amber accent

**RECENTLY COMPLETED section** (green, collapsed by default):
- Items where `status = 'completed'` and `completed_at` within last 7 days
- Sorted by completed_at descending (most recent first)
- Green accent, compact single-line display

### 7.3 Reminder card

Each active/overdue reminder is a card:

```
┌──────────────────────────────────────────────┐
│ 🔴 Allianz Car Insurance           $1,234.00│
│    Due: 15 Mar 2026 (16 days overdue)        │
│    📄 Allianz-Policy-2025.pdf                │
│    [Mark Paid ✓] [Snooze 7d ⏰] [Dismiss ×] │
└──────────────────────────────────────────────┘
```

**XAML structure per card**:
- `Border` with coloured left edge (red/yellow/green)
- `Grid` with vendor+amount header row, due date row, document link row, button row
- Three `Button` controls in a `StackPanel Orientation="Horizontal"`

**Wiring per card**:
- `MarkPaidButton.Click` → calls `Reminders.markCompleted` → removes from active list, adds to completed
- `SnoozeButton.Click` → calls `Reminders.snooze` → removes from active list (reappears after snooze)
- `DismissButton.Click` → confirmation dialog → calls `Reminders.dismiss` → permanently removes
- Document link (`PointerPressed`) → opens file in default app (existing pattern)

### 7.4 Sidebar badge

In the left panel, below the existing sections, a new `Expander`:

```
▾ ACTION ITEMS
  🔴 1 overdue  🟡 2 upcoming
  Total: $1,610.50 due
```

**Wiring**:
- `ReminderSummary` from `Reminders.getSummary` on the refresh timer
- Dot colours match the card accents
- Hidden when no active reminders exist

### 7.5 Status bar integration

When reminders exist, append to status bar:
```
●● Ready · 1,234 docs · 3 action items
```

---

## 8. ViewModel Extension

### `ShellViewModel` additions

```csharp
// Records
public record ReminderItem(
    long Id, string? Vendor, string? Amount, string? DueDate,
    string DueLabel, bool IsOverdue, string? DocumentPath, string? FileName);

// Properties
public ObservableCollection<ReminderItem> OverdueReminders { get; }
public ObservableCollection<ReminderItem> UpcomingReminders { get; }
public ObservableCollection<ReminderItem> CompletedReminders { get; }
public int ActionItemCount { get; }
public string ActionItemSummary { get; }

// Active tab
public bool IsChatActive { get; set; }   // true = chat, false = TODO
public bool IsTodoActive => !IsChatActive;

// Actions
public async Task MarkPaidAsync(long reminderId);
public async Task SnoozeAsync(long reminderId, int days = 7);
public async Task DismissAsync(long reminderId);
```

`RefreshAsync` is extended to also call `Reminders.getActive`, `Reminders.getRecentlyCompleted`, and `Reminders.getSummary`.

---

## 9. Chat Integration

The chat can also surface reminders. When the user asks "what bills are due?", the chat response includes reminder cards (same visual style as the TODO panel).

This is a natural extension of the search pipeline:
- `Chat.queryWithProvider` already searches documents
- If the query matches bill/invoice/payment terms, also include active reminders in the response
- Or: add a `hermes_list_reminders` MCP tool call to the Ollama/Azure OpenAI context

Deferred to after the core TODO panel works.

---

## 10. Pipeline Integration

### Where trigger evaluation happens

In `ServiceHost.runSyncCycle`, after extraction (step 4) and before embedding (step 5):

```fsharp
// 4. Extract text from un-extracted documents
let! _extractResult = Extraction.extractBatch ...

// 4.5 — NEW: Evaluate triggers on newly extracted documents
logger.debug "Evaluating bill reminders..."
let! newReminders = Reminders.evaluateNewDocuments db logger (clock.utcNow())
if newReminders > 0 then
    logger.info $"Created {newReminders} new reminder(s)"

// 4.6 — NEW: Un-snooze expired reminders
let! unsnoozed = Reminders.unsnoozeExpired db (clock.utcNow())
if unsnoozed > 0 then
    logger.info $"Un-snoozed {unsnoozed} reminder(s)"

// 5. Embed un-embedded documents
```

### Backfill interaction

When backfill processes historical documents, they go through the same extract → trigger pipeline. This means historical invoices with due dates in the past will create reminders. The `-30 days` threshold in `detectBill` limits this — we don't create reminders for 3-year-old bills. But recent ones (last month) will show up as overdue, which is useful for catching things you might have missed.

---

## 11. Implementation Phases — Silver Thread

Each phase delivers **verifiable end-to-end functionality** — from DB to pipeline to UI to user action and back.

### Phase R1: Vertical Slice — One Reminder, End to End

The minimum: detect a bill, show it, let the user act on it.

| Layer | What |
|-------|------|
| **Database.fs** | Schema v3: `reminders` table (CREATE TABLE + indexes). Shares the v2→v3 migration with Backfill (doc 11). |
| **Domain.fs** | `Reminder`, `ReminderStatus`, `ReminderSummary` types |
| **Reminders.fs** | Core module: `evaluateNewDocuments`, `getActive`, `getSummary`, `markCompleted`, `snooze`, `dismiss`, `unsnoozeExpired`. **Note**: `getActive` must JOIN `documents` to get `saved_path` and `original_name` for the UI — `Reminder` alone doesn't have file paths. Return type includes document path. |
| **Hermes.Core.fsproj** | Add `Reminders.fs` between `Stats.fs` and `Chat.fs` |
| **ServiceHost.fs** | Wire `evaluateNewDocuments` + `unsnoozeExpired` after extraction step |
| **ShellViewModel.cs** | `ReminderItem` record (with `DocumentPath`, `FileName` — from join query). `OverdueReminders`, `UpcomingReminders` collections. `MarkPaidAsync`, `SnoozeAsync`, `DismissAsync` methods. `RefreshAsync` extended to call `Reminders.getActive` + `Reminders.getSummary`. |
| **ShellWindow.axaml** | Tab toggle: `[💬 Chat] [📋 Action Items (N)]` above main content. TODO panel with overdue + upcoming sections. Reminder card template (Border + Grid + 3 buttons). |
| **ShellWindow.axaml.cs** | Tab toggle wiring (visibility swap). `CreateReminderCard` method. Button click handlers call ViewModel methods. Card document link opens file. |
| **Tests** | `detectBill`: document with amount + date in range → Some. Document with old date → None. Document with no amount → None. CRUD: insert → getActive includes it → markCompleted → getActive excludes it. Snooze → getActive excludes it → after expiry → getActive includes it. |
| **Proof** | Insert a test document with `extracted_amount=100.00` and `extracted_date` = tomorrow → run app → sync cycle fires → Action Items tab shows "(1)" → click tab → see reminder card with vendor, amount, due date, file link → click Mark Paid → card moves to completed section → tab shows "(0)" |

**Important**: the detection logic must be category-aware. Not every document with an amount and date is a bill. Filter to categories that are bill-like: `invoices`, `utilities`, `insurance`, `subscriptions`, `rates-and-tax`. Other categories (e.g. `medical`, `legal`) should not auto-create reminders.

```fsharp
let private billCategories = 
    set [ "invoices"; "utilities"; "insurance"; "subscriptions"; "rates-and-tax" ]

let detectBill (doc: Domain.Document) : Reminder option =
    if not (billCategories.Contains(doc.Category.ToLowerInvariant())) then None
    else ...
```

### Phase R2: Empty State + Sidebar Badge

Polish the 0-reminder experience and sidebar visibility.

| Layer | What |
|-------|------|
| **ShellWindow.axaml** | Empty state illustration in TODO panel: "All clear! No bills or reminders." with a subtle icon. ACTION ITEMS expander in sidebar with badge. |
| **ShellWindow.axaml.cs** | Empty state visibility toggles. Sidebar badge wired to `ReminderSummary` — hidden when 0 reminders, shows `🔴 N overdue 🟡 N upcoming` otherwise. |
| **ShellViewModel.cs** | `ActionItemCount` and `ActionItemSummary` properties, driven by `ReminderSummary`. |
| **Status bar** | Append action item count: `●● Ready · 1,234 docs · 3 action items` |
| **Proof** | Launch with 0 reminders → Action Items tab shows empty state → Chat is default tab. Add a reminder → sidebar badge appears → status bar updates → tab shows count. |

### Phase R3: Completed Section + Dismiss Confirmation

Round out the interaction model.

| Layer | What |
|-------|------|
| **Reminders.fs** | `getRecentlyCompleted` — completed in last 7 days, ordered by completed_at desc |
| **ShellViewModel.cs** | `CompletedReminders` collection, populated in `RefreshAsync` |
| **ShellWindow.axaml + .cs** | RECENTLY COMPLETED section (green, collapsed by default via `Expander`). Dismiss button shows confirmation dialog before calling `dismiss`. |
| **Proof** | Mark Paid on a reminder → it appears in Recently Completed section (green). Dismiss → confirm dialog → permanently gone. |

### Phase R4: MCP Tools

External agent integration — the same data accessible via MCP.

| Layer | What |
|-------|------|
| **McpServer.fs** | Add `hermes_list_reminders` and `hermes_update_reminder` to `toolDefinitions` |
| **McpTools.fs** | Implement tool handlers: `listReminders` reads from `Reminders.getActive`, `updateReminder` calls `markCompleted`/`snooze`/`dismiss` |
| **Tests** | MCP tool call → correct JSON response. Update tool → DB state changes. |
| **Proof** | Configure Hermes MCP in Claude/Copilot → ask "what bills are due?" → gets reminder list. "Mark the Allianz one as paid" → reminder status changes → UI reflects it on next refresh. |

### Summary: R1→R2→R3→R4, each adds a complete loop

```
R1: Bill detected → shown in TODO tab → user marks paid → done  (core silver thread)
R2: 0-state UX + sidebar badge + status bar                     (visibility everywhere)
R3: Completed section + dismiss safety                           (full lifecycle)
R4: MCP read/write → external agent can manage reminders         (agent integration)
```

### Definition of done (every phase)

Per `.github/copilot-instructions.md`:
1. XAML exists with all controls laid out
2. Code-behind wired — every named control has event handlers
3. Buttons do something — no dead controls
4. Data is live — reads from DB, not placeholder text
5. Build clean — 0 errors, 0 warnings
6. Tests pass — new tests for new logic
7. Smoke tested — verify the specific proof for that phase
8. MCP tools (R4): tested via actual MCP client or `curl` to localhost

---

## 12. Open Questions

| # | Question | Leaning |
|---|----------|---------|
| 1 | Should reminders also be created for recurring bills (same vendor, monthly pattern)? | Future — pattern detection is a separate feature |
| 2 | Should snooze durations be configurable (1d, 3d, 7d, 14d, 30d)? | Start with fixed 7d button; add dropdown later |
| 3 | Should we show reminders as OS notifications (Windows toast / macOS notification)? | Yes, but as a follow-up — in-app first |
| 4 | Should "Mark Paid" record the payment method or actual amount? | Not in v1 — just a boolean "done" |
| 5 | Should the TODO panel support manual reminder creation (not triggered by a document)? | Future — keep it document-driven for now |
| 6 | Should completed reminders auto-purge after 30 days? | Yes — clean up old entries to avoid table growth |
| 7 | How should we handle documents where `extracted_date` is the document date, not the due date? | Known limitation — extraction heuristics don't distinguish. Improve extraction to look for "due date" / "payment due" specifically in a future phase |

---

## 13. What This Proves

This feature is the **minimum viable loop** for the agent evolution (doc 10):

```
Observe (extract invoice) → Decide (trigger: due date detected) → Act (create reminder) → Feedback (user marks paid)
```

Once this works:
- **Triggers are proven** — the condition→action pipeline works end-to-end
- **Skills are proven** — `create-reminder` is the first skill; adding `send-email`, `update-gl` follows the same pattern
- **MCP write tools are proven** — external agents can read and update reminders
- **The TODO panel is reusable** — future skills (appointments, follow-ups, approvals) add items to the same panel with different card types
