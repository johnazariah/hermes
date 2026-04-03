---
description: "Implementation plan for Doc 15: VS Code-style Three-Column Shell UI. Six phases (U1–U6), each a silver-thread vertical slice — backend + UI + tests + proof."
design-doc: ".project/design/15-rich-ui.md"
depends-on:
  - "Doc 11 (Email Backfill) — ✅ completed"
  - "Doc 12 (Bills & Reminders) — ✅ completed"
---

# Hermes — Implementation Plan: Rich UI (VS Code-Style Shell)

## Prerequisites

```
dotnet build hermes.slnx --nologo
dotnet test tests/Hermes.Tests/Hermes.Tests.fsproj --nologo
```

**Expected**: 0 errors, 0 warnings, 258+ tests pass. If not, fix before proceeding.

**Critical rules — read FIRST**:
- Read `.github/copilot-instructions.md` — especially "Agent Workflow Requirements" and "UI integration: definition of done"
- **F# code** (`Hermes.Core`) must go through `@fsharp-dev` agent
- **C# code** (`Hermes.App`) must go through `@csharp-dev` agent
- **UI definition of done**: XAML exists → code-behind wired → buttons functional → data live → build clean → smoke tested
- **Every phase has a PROOF** — do not mark complete until the proof passes

## Dependency Map

```
U1: Shell Layout                (standalone — replaces existing ShellWindow)
 └─ U2: Documents Navigator     (needs U1 shell + existing DB queries)
 └─ U3: Email Threads Navigator  (needs U1 shell + new Threads.fs)
 └─ U4: Action Items + Nav Stack (needs U1 shell + Reminders module)
 └─ U5: Timeline + Activity Log  (needs U1 shell + new activity_log table)
U6: Chat ↔ Content Integration   (needs U1 shell + chat pane)
```

U2–U5 are independent of each other after U1. They can be done in any order. U6 depends on U1 only (chat is in its own pane).

---

## Phase U1: Four-Column Shell Layout with Chat Pane

**Silver thread**: Activity bar click → navigator panel switches content. 💬 toggle shows/hides chat pane. Layout renders at all window sizes.

### What to build

**File: `src/Hermes.App/ViewModels/ShellViewModel.cs`** (new or replace existing)

```csharp
// NavigatorMode enum: ActionItems, Documents, Threads, Timeline, Activity
// IsChatPaneVisible: bool (persisted to config)
// ActiveMode: NavigatorMode (drives navigator panel content)
// NavigateTo(NavigationItem item), NavigateBack()
// NavigationStack: Stack<NavigationItem> for breadcrumb
// StatusBar properties: DocCount, ActionItemCount, BackfillProgress, ServiceHealth
```

**File: `src/Hermes.App/Views/ShellWindow.axaml`** (replace existing)

- 4-column Grid: ActivityBar (60px) | Navigator (240px + GridSplitter) | Content (fill) | ChatPane (300px + GridSplitter)
- ActivityBar: 5 mode icon buttons (📋 📁 📧 ⏰ ⚡) + service health dots (Ollama, DB, MCP) + 💬 toggle + ⚙ settings
- Active icon: accent border on left edge
- Navigator: ContentControl that swaps based on ActiveMode
- Content: ContentControl that swaps based on CurrentItem (empty state per mode for now)
- Chat: existing chat UI relocated to right pane
- StatusBar: Bottom row spanning all columns

**File: `src/Hermes.App/Views/ShellWindow.axaml.cs`** (replace existing)

- Wire activity bar icon Click handlers → set ActiveMode → navigator swaps
- Wire 💬 toggle → show/hide chat column
- Wire ⚙ → open Settings dialog (existing)
- Service health dots populated from HermesServiceBridge

### What to test

- `ShellViewModel_ChangeMode_UpdatesActiveMode` — set mode → property changes
- `ShellViewModel_ToggleChat_TogglesVisibility` — toggle → IsChatPaneVisible flips
- `ShellViewModel_NavigateTo_PushesToStack` — navigate → stack grows
- `ShellViewModel_NavigateBack_PopsStack` — back → stack shrinks, current item changes

### PROOF

Launch app → see 4 columns (activity bar, navigator, content, chat) → click each activity bar icon → navigator heading text changes per mode → click 💬 → chat pane hides → click 💬 again → chat pane shows → type a chat query → response appears → switch to Documents mode in navigator → chat conversation persists in right pane. Resize window → layout remains (no breakpoint logic).

### Commit

```
feat(ui): VS Code-style four-column shell layout with chat pane
```

---

## Phase U2: Documents Navigator + Document Detail

**Silver thread**: Click 📁 Documents icon → category tree loads from DB → click category → document list populates → click document → content pane shows metadata + markdown preview + action buttons.

### What to build

**File: `src/Hermes.Core/DocumentBrowser.fs`** (new module)

```fsharp
[<RequireQualifiedAccess>]
module DocumentBrowser

/// List categories with document counts
let listCategories (db: Database) : (string * int) list

/// List documents in a category with pagination
let listDocuments (db: Database) (category: string) (offset: int) (limit: int) : DocumentSummary list

/// Get full document detail including extracted text
let getDocumentDetail (db: Database) (documentId: int64) : DocumentDetail option

/// Get document's linked thread (by gmail_id)
let getLinkedThread (db: Database) (documentId: int64) : string option

type DocumentSummary = {
    Id: int64; OriginalName: string; Category: string;
    ExtractedDate: string option; ExtractedAmount: decimal option;
    Sender: string option; ClassificationTier: string option;
    ClassificationConfidence: float option
}

type DocumentDetail = {
    Summary: DocumentSummary; ExtractedText: string option;
    FilePath: string; Vendor: string option;
    IngestedAt: string; ExtractedAt: string option;
    EmbeddedAt: string option; PipelineStatus: PipelineStatus
}

type PipelineStatus = { Classified: bool; Extracted: bool; Embedded: bool }
```

**File: `src/Hermes.App/Views/DocumentsNavigator.axaml`** (new UserControl)

- Filter TextBox at top
- TreeView for categories (category name + count badge)
- ListBox for documents in selected category (virtualised)
- Each document row: filename, date, amount (if present), classification badge

**File: `src/Hermes.App/Views/DocumentDetailView.axaml`** (new UserControl)

- Header: filename + category badge + classification info
- Metadata grid: Date, Amount, Vendor, Sender, Source, Pipeline status dots
- Markdown preview: ScrollViewer with styled TextBlocks (headings, tables, KV pairs)
- Action buttons: Open File, Reclassify (dropdown), Re-extract, Show Email Thread

**Files: code-behind for both UserControls**

- DocumentsNavigator: category click → load documents → selection change → fire event
- DocumentDetailView: populate from DocumentDetail record, wire action buttons

**File: `src/Hermes.App/HermesServiceBridge.cs`**

- Add: `GetCategoriesAsync()`, `GetDocumentsAsync(category, offset, limit)`, `GetDocumentDetailAsync(id)`
- Each calls the corresponding `DocumentBrowser` function

### What to test

**F# tests:**
- `DocumentBrowser_ListCategories_ReturnsCountsGroupedByCategory`
- `DocumentBrowser_ListDocuments_ReturnsPaginatedResults`
- `DocumentBrowser_GetDocumentDetail_ReturnsFullDetail`
- `DocumentBrowser_GetDocumentDetail_NonExistent_ReturnsNone`

### PROOF

Launch app → click 📁 → categories tree shows (invoices: 239, bank-statements: 104, etc.) → click "invoices" → document list populates with 239 items → scroll → virtualised (no lag) → click a document → content pane shows: name, category badge, metadata grid, extracted markdown with rendered tables, classification info → click "Open File" → document opens in default OS app. Filter by typing → list narrows.

### Commit

```
feat(ui): documents navigator with category tree and document detail pane
```

---

## Phase U3: Email Threads Navigator + Thread Timeline

**Silver thread**: Click 📧 Threads → thread list loads from DB (grouped by thread_id) → click thread → content pane shows chronological messages with attachment links → click attachment → navigates to document detail → back button returns.

### What to build

**File: `src/Hermes.Core/Threads.fs`** (new module)

```fsharp
[<RequireQualifiedAccess>]
module Threads

type ThreadSummary = {
    ThreadId: string; Subject: string; Account: string;
    MessageCount: int; AttachmentCount: int;
    FirstDate: string; LastDate: string;
    Participants: string list; Summary: string option
}

type ThreadMessage = {
    GmailId: string; Sender: string; Date: string;
    Subject: string; BodyPreview: string;
    AttachmentDocIds: int64 list; IsFromUser: bool
}

type ThreadDetail = {
    Summary: ThreadSummary; Messages: ThreadMessage list
}

/// List threads with pagination, most recent first
let listThreads (db: Database) (offset: int) (limit: int) : ThreadSummary list

/// List threads for a specific account
let listThreadsByAccount (db: Database) (account: string) (offset: int) (limit: int) : ThreadSummary list

/// Get full thread with all messages and attachment links
let getThreadDetail (db: Database) (threadId: string) : ThreadDetail option

/// Generate LLM summary for a thread (on demand)
let generateSummary (chat: ChatProvider) (thread: ThreadDetail) : Task<string>
```

**Schema: `thread_summaries` table** (from design doc §5)

Add to Database.fs migration:
```sql
CREATE TABLE IF NOT EXISTS thread_summaries (
    thread_id TEXT PRIMARY KEY, account TEXT NOT NULL,
    subject TEXT, summary TEXT, message_count INTEGER,
    attachment_count INTEGER, first_date TEXT, last_date TEXT,
    participants TEXT, generated_at TEXT, stale INTEGER NOT NULL DEFAULT 0
);
```

**File: `src/Hermes.App/Views/ThreadsNavigator.axaml`** (new UserControl)

- Search TextBox at top
- ListBox for threads: subject, message count, attachment count, date range, participant names
- Grouped by: "Recent" | "By Account" toggle

**File: `src/Hermes.App/Views/ThreadDetailView.axaml`** (new UserControl)

- Header: subject, message count, date range
- Summary block (if available, or "Summarise" button)
- Chronological message cards: sender, date, body preview, attachment links (📎)
- Attachment link click → navigate to document detail

### What to test

**F# tests:**
- `Threads_ListThreads_GroupsByThreadId_ReturnsCorrectCounts`
- `Threads_GetThreadDetail_ReturnsMessagesInChronologicalOrder`
- `Threads_GetThreadDetail_IncludesAttachmentDocIds`
- `Threads_ListThreads_PaginationWorks`

### PROOF

Launch app → click 📧 → see thread list sorted by recency (subject, N msgs, N attachments) → click a thread with attachments → content pane shows chronological messages with sender/date/body → see 📎 attachment links inline → click 📎 → content pane switches to document detail → breadcrumb shows "Threads / [Subject] / [Filename]" → click ← back → returns to thread view. Click "Summarise" → LLM generates summary → summary appears at top.

### Commit

```
feat(ui): email threads navigator with thread timeline and attachment links
```

---

## Phase U4: Action Items Navigator + Cross-Navigation

**Silver thread**: Click 📋 Action Items → reminders load grouped by status (overdue/upcoming/completed) → click reminder → detail with document link → click document link → content pane shows document → breadcrumb + back button work.

### What to build

**File: `src/Hermes.App/ViewModels/ShellViewModel.cs`** (extend)

```csharp
// NavigationItem record: Type (string), Label (string), Id (long?)
// NavigateTo(NavigationItem) → pushes to stack, loads content
// NavigateBack() → pops stack, loads previous content
// BreadcrumbText → computed from NavigationStack
```

**File: `src/Hermes.App/Views/ActionItemsNavigator.axaml`** (new UserControl)

- Sections: "⚠ OVERDUE" (red), "📋 UPCOMING" (yellow), "✅ COMPLETED" (collapsible)
- Each reminder row: vendor, amount, due date, status icon
- Badge count on OVERDUE section header

**File: `src/Hermes.App/Views/ReminderDetailView.axaml`** (new UserControl)

- Full-width reminder card with: vendor, amount, due date, overdue indicator
- Linked document preview (clickable → navigates to document detail)
- Linked thread (clickable → navigates to thread view)
- Action buttons: Mark Paid ✓, Snooze 7d ⏰, Dismiss ×

**File: `src/Hermes.App/Views/BreadcrumbBar.axaml`** (new UserControl)

- ← back button + breadcrumb trail: "📋 Action Items / 🔴 Allianz / 📄 Policy.pdf"
- Each breadcrumb segment clickable → navigates to that level

**File: `src/Hermes.App/HermesServiceBridge.cs`** (extend)

- Add: `GetRemindersGroupedAsync()`, `MarkReminderPaidAsync(id)`, `SnoozeReminderAsync(id, days)`, `DismissReminderAsync(id)`

### What to test

**C# ViewModel tests:**
- `ShellViewModel_NavigateTo_PushesAndUpdatesCurrentItem`
- `ShellViewModel_NavigateBack_PopsToCorrectItem`
- `ShellViewModel_BreadcrumbText_ReflectsNavigationPath`

**F# tests (if not already covered by Reminders module):**
- `Reminders_ListGroupedByStatus_ReturnsCorrectBuckets`

### PROOF

Launch app → click 📋 → see overdue bills (red) + upcoming (yellow) → click an overdue bill → content pane shows reminder detail with "📄 Allianz-Policy-2025.pdf" link → click link → content pane switches to document detail with metadata + markdown → breadcrumb shows "Action Items / Allianz Renewal / Allianz-Policy-2025.pdf" → click ← → back to reminder detail → click "Mark Paid ✓" → reminder moves to completed section → count badge decrements.

### Commit

```
feat(ui): action items navigator with cross-navigation and breadcrumb
```

---

## Phase U5: Timeline + Activity Log

**Silver thread**: Click ⏰ Timeline → see documents/emails grouped by day → Click ⚡ Activity → see processing events → trigger a sync → new events appear → click an event → navigates to related document.

### What to build

**Schema: `activity_log` table** (from design doc §5)

Add to Database.fs migration:
```sql
CREATE TABLE IF NOT EXISTS activity_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp TEXT NOT NULL DEFAULT (datetime('now')),
    level TEXT NOT NULL,
    category TEXT NOT NULL,
    message TEXT NOT NULL,
    details TEXT,
    document_id INTEGER REFERENCES documents(id)
);
CREATE INDEX IF NOT EXISTS idx_activity_ts ON activity_log(timestamp);
```

**File: `src/Hermes.Core/ActivityLog.fs`** (new module)

```fsharp
[<RequireQualifiedAccess>]
module ActivityLog

type ActivityLevel = Info | Success | Warning | Error
type ActivityCategory = Sync | Classify | Extract | Embed | Remind | Backfill | System

type ActivityEvent = {
    Id: int64; Timestamp: DateTimeOffset; Level: ActivityLevel;
    Category: ActivityCategory; Message: string;
    Details: string option; DocumentId: int64 option
}

let log (db: Database) (level: ActivityLevel) (category: ActivityCategory) (message: string) (details: string option) (docId: int64 option) : unit
let recent (db: Database) (limit: int) : ActivityEvent list
let purgeOlderThan (db: Database) (days: int) : int
```

**File: `src/Hermes.Core/Timeline.fs`** (new module)

```fsharp
[<RequireQualifiedAccess>]
module Timeline

type TimelineEntry = { Type: string; Label: string; DocumentId: int64 option; ThreadId: string option; Timestamp: DateTimeOffset }
type TimelineGroup = { Label: string; Date: DateTimeOffset; Entries: TimelineEntry list }

/// Group documents + emails by day: Today, Yesterday, This Week, etc.
let getTimeline (db: Database) (limit: int) : TimelineGroup list
```

**File: `src/Hermes.Core/ServiceHost.fs`** (extend)

- Wire `ActivityLog.log` calls at key points: sync start/complete, classify batch, extract batch, embed batch, errors, reminder evaluation

**File: `src/Hermes.App/Views/TimelineNavigator.axaml`** (new UserControl)

- Day-grouped list: "Today (3)", "Yesterday (5)", "This Week (12)"
- Each entry: icon (📄 or 📧), label, timestamp
- Click entry → navigate to document or thread

**File: `src/Hermes.App/Views/ActivityNavigator.axaml`** (new UserControl)

- Scrolling event log: level icon (🔵🟢🟡🔴), timestamp, message
- Click event with document_id → navigate to document detail
- "Clear" and "Export" buttons at bottom

### What to test

**F# tests:**
- `ActivityLog_Log_InsertsEvent`
- `ActivityLog_Recent_ReturnsInReverseChronological`
- `ActivityLog_Purge_DeletesOlderThanThreshold`
- `Timeline_GetTimeline_GroupsByDay`
- `Timeline_GetTimeline_IncludesBothDocumentsAndEmails`

### PROOF

Launch app → click ⏰ → see "Today (N)" with recent documents → entries show correct timestamps → click a document entry → navigates to document detail → click ⚡ → see processing log (may be empty initially) → trigger a full sync cycle (via existing mechanism) → new events appear: "🔵 Sync started", "🟢 Classified 5 docs", "🟢 Extracted 3 docs" → click a classification event → navigates to the classified document.

### Commit

```
feat(ui): timeline navigator and activity log with event wiring
```

---

## Phase U6: Chat Pane ↔ Content Pane Integration

**Silver thread**: Type a search query in chat → results appear as document cards → click a card → content pane shows document detail (while chat stays visible) → ask follow-up question → AI uses document context.

### What to build

**File: `src/Hermes.App/ViewModels/ShellViewModel.cs`** (extend)

- `OnChatDocumentClicked(documentId)` → calls `NavigateTo(DocumentItem)` → content pane updates while chat pane stays

**File: `src/Hermes.App/Views/ChatPane.axaml`** (refactor existing chat UI)

- Relocate chat UI from wherever it currently is to its own UserControl
- Document result cards: clickable → fire event with document ID
- Suggested query chips when empty (e.g. "Recent invoices", "Overdue bills")
- Recent search history (last 5 queries)
- Input bar at bottom: TextBox + AI/Send button

**File: `src/Hermes.App/Views/ShellWindow.axaml.cs`** (extend)

- Wire ChatPane.DocumentClicked event → ShellViewModel.NavigateTo(DocumentItem)
- Chat pane stays visible after click; content pane updates

### What to test

- `ShellViewModel_ChatDocumentClick_NavigatesToDocument` — fire document click from chat → CurrentItem is DocumentItem
- Existing chat tests should still pass — functionality moved, not changed

### PROOF

Ensure chat pane is visible (💬 active) → type "car insurance" → results appear as clickable cards → click a card → content pane shows full document detail (metadata + markdown + action buttons) → chat conversation is still visible in the right pane → type "when does this expire?" → AI uses document context in response → document detail still visible. Navigate to Documents mode → scroll through documents → chat still visible with prior conversation.

### Commit

```
feat(ui): chat pane ↔ content pane integration with clickable document cards
```

---

## Silver Thread Integrity Check

| Phase | Input Trigger | Processing | Backend | Presentation | UI Response |
|-------|--------------|------------|---------|-------------|-------------|
| U1 | Activity bar click | Mode switch | ViewModel state | Navigator panel swap | Visual: highlighted icon + panel content change |
| U2 | Category click → Doc click | DB queries | DocumentBrowser.fs | List + Detail data | Category tree + document metadata + markdown preview |
| U3 | Thread click | DB query + LLM summary | Threads.fs | Message list | Thread timeline + attachment links (cross-nav) |
| U4 | Reminder click → Doc link | Nav stack push | ViewModel + Reminders | Breadcrumb + detail | Cross-navigation with back button |
| U5 | Sync cycle | Activity logging | ActivityLog.fs + Timeline.fs | Event feed | Timeline groups + live activity log |
| U6 | Chat doc card click | NavigateTo dispatch | ViewModel | Content swap | Document detail in content pane, chat persists |

**No orphaned backend**: Every new Core module (DocumentBrowser, Threads, ActivityLog, Timeline) surfaces in at least one UI panel.
**No dead UI**: Every clickable control (category, document, thread, reminder, attachment link, activity event, chat card) triggers real navigation with live data.

---

## Flags & Risks

| Risk | Impact | Mitigation |
|------|--------|------------|
| Markdown rendering in Avalonia | No built-in markdown control; need styled TextBlocks | Start with basic rendering (headings → bold text, tables → Grid, KV → bullet list). Upgrade later. |
| Virtual scrolling for large lists | 2,000+ documents in a category | Use Avalonia `VirtualizingStackPanel` on ListBox |
| Thread grouping perf | GROUP BY thread_id on large messages table | Pre-compute counts; index `messages(thread_id)` |
| Chat relocation | Moving chat from current location to dedicated pane | Extract as UserControl first, then host in new location |
| GridSplitter UX | Users may accidentally collapse panels | Set MinWidth on each column (60px, 160px, 300px, 200px) |
