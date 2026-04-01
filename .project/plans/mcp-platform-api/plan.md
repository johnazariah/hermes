---
description: "Implementation plan for Doc 14: MCP Platform API — evolving the MCP server from read-only search to full platform API. Four phases (M1–M4), each a silver-thread vertical slice."
design-doc: ".project/design/14-mcp-platform-api.md"
depends-on:
  - "Doc 13 Phase F1+F2 (Document Feed tools) — M1 IS F1+F2, shared implementation"
  - "Doc 12 (Bills & Reminders) — for M2 reminder tools"
  - "Doc 18 (Smart Classification) — for M3 reclassify/reextract tools"
---

# Hermes — Implementation Plan: MCP Platform API

## Prerequisites

```
dotnet build hermes.slnx --nologo
dotnet test tests/Hermes.Tests/Hermes.Tests.fsproj --nologo
```

**Expected**: 0 errors, 0 warnings, all tests pass.

**Critical rules**:
- **F# code** must go through `@fsharp-dev` agent
- **Every phase has a PROOF** — do not mark complete until the proof passes

## Dependency Map & Relationship to Other Plans

```
M1: Platform Feed tools             = Doc 13 F1+F2 (same implementation, not duplicated)
 └─ M2: Reminder tools              (needs Doc 12 reminders — already done)
 └─ M3: Document management tools   (needs Doc 18 C2+ for reclassify; Doc 17 P7 for reextract)
 └─ M4: Alert + confirmation queue   (future — doc 10 agent evolution)
```

**Important**: M1 is identical to Doc 13 phases F1+F2. Do NOT implement twice. If Doc 13 F1+F2 are done, M1 is done. This plan focuses on M2, M3, and M4 as additive tools on top of the feed.

---

## Phase M1: Platform Feed Tools (= Doc 13 F1+F2)

**See [Document Feed plan](../document-feed/plan.md) phases F1 and F2.**

Delivers:
- `hermes_list_documents` — cursor-based document feed
- `hermes_get_document_content` — text/markdown/raw content retrieval
- `hermes_get_feed_stats` — archive statistics for consumers

**This phase is NOT duplicated.** Mark M1 complete when Doc 13 F1+F2 are complete.

---

## Phase M2: Reminder MCP Tools

**Silver thread**: MCP client (or AI agent) calls `hermes_list_reminders` → sees overdue bills → calls `hermes_update_reminder(id, action="mark_paid")` → reminder status updates in DB → next list call shows updated status → UI reflects the change.

### What to build

**File: `src/Hermes.Core/McpTools.fs`** (extend)

Add tool registrations:

**`hermes_list_reminders`**
```json
{
  "name": "hermes_list_reminders",
  "description": "List bill reminders with optional status filter.",
  "inputSchema": {
    "properties": {
      "status": { "type": "string", "enum": ["active", "overdue", "upcoming", "completed", "dismissed", "all"], "default": "active" },
      "limit": { "type": "integer", "default": 50 }
    }
  }
}
```

**`hermes_update_reminder`**
```json
{
  "name": "hermes_update_reminder",
  "description": "Update a reminder: mark paid, snooze, or dismiss.",
  "inputSchema": {
    "properties": {
      "reminder_id": { "type": "integer" },
      "action": { "type": "string", "enum": ["mark_paid", "snooze", "dismiss"] },
      "snooze_days": { "type": "integer", "default": 7, "description": "Days to snooze (only for action=snooze)" }
    },
    "required": ["reminder_id", "action"]
  }
}
```

**File: `src/Hermes.Core/McpServer.fs`** (wire handlers)

- `hermes_list_reminders` → calls `Reminders.listReminders` (already exists from Doc 12)
- `hermes_update_reminder` → calls `Reminders.markPaid`, `Reminders.snooze`, or `Reminders.dismiss` based on action

### What to test

- `McpTools_ListReminders_Active_ReturnsActiveReminders`
- `McpTools_ListReminders_Overdue_ReturnsOnlyOverdue`
- `McpTools_UpdateReminder_MarkPaid_StatusChanges`
- `McpTools_UpdateReminder_Snooze_SetsSnoozedUntil`
- `McpTools_UpdateReminder_Dismiss_StatusChanges`
- `McpTools_UpdateReminder_InvalidId_ReturnsError`

### PROOF

Start MCP server → call `hermes_list_reminders(status="overdue")` → see overdue bills with vendor, amount, due date → call `hermes_update_reminder(id=X, action="mark_paid")` → call `hermes_list_reminders(status="overdue")` again → that reminder is gone → call `hermes_list_reminders(status="completed")` → that reminder appears.

**AI agent test**: Ask in chat: "What bills are overdue?" → AI calls `hermes_list_reminders(status="overdue")` → shows bills → user says "mark the AGL bill as paid" → AI calls `hermes_update_reminder` → confirms.

### Commit

```
feat(mcp): hermes_list_reminders and hermes_update_reminder tools
```

---

## Phase M3: Document Management MCP Tools

**Silver thread**: MCP client calls `hermes_reclassify(doc_id, new_category)` → file moves on disk → DB category updates → next `hermes_list_documents` shows new category. Client calls `hermes_reextract(doc_id)` → extraction fields cleared → next sync cycle re-extracts → new content available.

### What to build

**File: `src/Hermes.Core/DocumentManagement.fs`** (new module)

```fsharp
[<RequireQualifiedAccess>]
module DocumentManagement

/// Move a document to a different category (moves file on disk + updates DB)
let reclassify (db: Database) (fs: FileSystem) (archiveDir: string) (documentId: int64) (newCategory: string) : Result<unit, string>

/// Clear extraction fields, marking document for re-extraction on next cycle
let reextract (db: Database) (documentId: int64) : Result<unit, string>

/// Get counts and sample documents for each processing stage queue
let getProcessingQueue (db: Database) (stage: string option) (limit: int) : ProcessingQueue

type ProcessingQueue = {
    Unclassified: QueueStage
    Unextracted: QueueStage
    Unembedded: QueueStage
}
type QueueStage = { Count: int; SampleDocuments: DocumentFeed.FeedDocument list }
```

**File: `src/Hermes.Core/McpTools.fs`** (extend)

Add tool registrations:

**`hermes_reclassify`** — document_id (int), new_category (string)
**`hermes_reextract`** — document_id (int)
**`hermes_get_processing_queue`** — stage (optional enum), limit (int, default 10)

**File: `src/Hermes.Core/McpServer.fs`** (wire handlers)

### What to test

- `DocumentManagement_Reclassify_MovesFileAndUpdatesDb`
- `DocumentManagement_Reclassify_SetsManualClassificationTier`
- `DocumentManagement_Reclassify_InvalidId_ReturnsError`
- `DocumentManagement_Reclassify_InvalidCategory_Validates`
- `DocumentManagement_Reextract_ClearsExtractionFields`
- `DocumentManagement_GetProcessingQueue_ReturnsCorrectCounts`
- `McpTools_Reclassify_EndToEnd` — tool call → file moved + DB updated
- `McpTools_Reextract_EndToEnd` — tool call → fields cleared

### PROOF

Start MCP server → pick a document in "unsorted" → call `hermes_reclassify(doc_id, "invoices")` → verify file moved from `unsorted/` to `invoices/` on disk → DB shows `category="invoices"`, `classification_tier="manual"` → call `hermes_reextract(doc_id)` → `extracted_text` is NULL → wait for sync cycle → `extracted_text` repopulated → call `hermes_get_processing_queue` → shows counts of unclassified, unextracted, unembedded docs.

**AI agent test**: "Move document #1234 to the invoices category" → AI calls `hermes_reclassify` → confirms. "Re-extract the document" → AI calls `hermes_reextract` → confirms.

### Commit

```
feat(mcp): hermes_reclassify, hermes_reextract, hermes_get_processing_queue tools
```

---

## Phase M4: Alert + Confirmation Queue (Future)

**Silver thread**: AI agent calls `hermes_create_alert(message)` → alert surfaces in UI notification → user sees it. Agent calls `hermes_send_email(draft)` → queued for user confirmation → user approves in UI → email sent.

### What to build

> ⚠ **This phase is future work** (from Doc 10: Agent Evolution). It is included for planning purposes but should not be started until the agent evolution design is finalised.

**File: `src/Hermes.Core/Alerts.fs`** (new module)

```fsharp
[<RequireQualifiedAccess>]
module Alerts

type AlertLevel = Info | Warning | Action
type Alert = { Id: int64; Message: string; Level: AlertLevel; CreatedAt: DateTimeOffset; ReadAt: DateTimeOffset option }

let create (db: Database) (message: string) (level: AlertLevel) : int64
let list (db: Database) (unreadOnly: bool) : Alert list
let markRead (db: Database) (alertId: int64) : unit
```

**File: `src/Hermes.Core/ConfirmationQueue.fs`** (new module)

```fsharp
[<RequireQualifiedAccess>]
module ConfirmationQueue

type ConfirmableAction =
    | SendEmail of to: string * subject: string * body: string
    // Future: other destructive/external actions

type QueuedAction = { Id: int64; Action: ConfirmableAction; RequestedAt: DateTimeOffset; Status: string }

let enqueue (db: Database) (action: ConfirmableAction) : int64
let list (db: Database) : QueuedAction list
let approve (db: Database) (actionId: int64) : Result<unit, string>
let reject (db: Database) (actionId: int64) : Result<unit, string>
```

**MCP tools**: `hermes_create_alert`, `hermes_send_email` (with confirmation)

### PROOF

AI agent calls `hermes_create_alert("Your car insurance expires in 7 days")` → UI shows notification badge → user opens UI → sees alert → marks read. Agent calls `hermes_send_email(to="...", subject="...", body="...")` → queued → UI shows "Pending: Send email to ..." with Approve/Reject buttons → user clicks Approve → email sent via Gmail API.

### Commit

```
feat(mcp): alert creation and email confirmation queue
```

---

## Silver Thread Integrity Check

| Phase | Input | Processing | Backend | Presentation | Response |
|-------|-------|------------|---------|-------------|----------|
| M1 | MCP tool call | SQL query | DocumentFeed.fs | JSON document array | Consumer saves cursor |
| M2 | MCP tool call | Reminder CRUD | Reminders.fs (existing) | JSON reminder list | Status updates in DB + UI |
| M3 | MCP tool call | File move + DB update | DocumentManagement.fs | JSON confirmation | File relocated, fields cleared |
| M4 | MCP tool call | Queue + confirm | Alerts.fs + ConfirmationQueue.fs | UI notification + approve/reject | Action executed after approval |

**Complete API surface after M1–M3**:

| Tool | Type | Phase |
|------|------|-------|
| `hermes_search` | Read | Existing |
| `hermes_get_document` | Read | Existing |
| `hermes_list_categories` | Read | Existing |
| `hermes_stats` | Read | Existing |
| `hermes_read_file` | Read | Existing |
| `hermes_list_documents` | Read | M1 |
| `hermes_get_document_content` | Read | M1 |
| `hermes_get_feed_stats` | Read | M1 |
| `hermes_list_reminders` | Read | M2 |
| `hermes_update_reminder` | Write | M2 |
| `hermes_reclassify` | Write | M3 |
| `hermes_reextract` | Write | M3 |
| `hermes_get_processing_queue` | Read | M3 |

This brings Hermes from 5 read-only tools to 13 tools (8 read + 5 write), covering the full platform API surface needed for Osprey and other consumers.

---

## Flags & Risks

| Risk | Impact | Mitigation |
|------|--------|------------|
| M1 duplication with Doc 13 | Wasted effort building same tools twice | Explicitly linked — M1 = F1+F2. Track in both plans, implement once. |
| Write tool safety | Reclassify could move files incorrectly | Validate category exists as folder. Log all moves. Reversible (reclassify back). |
| Reextract clears data | User loses extracted text until next cycle | Log a warning. Consider keeping old text until new extraction completes. |
| M4 email sending | Gmail API scope needed; security risk | Confirmation queue mandatory. Never auto-send. User must approve in UI. |
| M2 depends on Doc 12 | Reminder functions must exist | Doc 12 is ✅ Done — no risk. |
