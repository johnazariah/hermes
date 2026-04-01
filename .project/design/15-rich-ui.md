# Hermes — Rich UI: Document Browser, Queue & Activity

> Design doc for expanding the shell window from chat-only to a full document management interface.  
> Builds on: doc 09 (UI Redesign), doc 12 (Bills & Reminders — TODO tab).  
> Created: 2026-04-01

---

## 1. Problem

The current UI shows aggregate stats (2,163 documents, 239 invoices) but you can't see **what** those documents are. You can search via chat, but you can't:
- Browse the archive by category
- See what's in the processing queue
- See what failed or needs attention
- Preview a document's extracted content
- Understand what happened during the last sync cycle
- Reclassify a misfiled document
- See whether extraction captured the right fields

For Hermes to be a trustworthy platform, users need visibility into what it's doing.

---

## 2. Main Area Tabs

The main area expands from 2 tabs to 4:

```
[💬 Chat]  [📋 Action Items (3)]  [📁 Documents]  [⚡ Activity]
```

| Tab | Purpose | Primary user need |
|-----|---------|-------------------|
| **Chat** | Search + AI conversation | "Find me something" |
| **Action Items** | Bills, reminders, pending approvals | "What needs my attention?" |
| **Documents** | Browse, filter, preview, reclassify | "What's in my archive?" |
| **Activity** | Processing log, sync history, errors | "What just happened?" |

Chat remains the default tab.

---

## 3. Documents Tab

### 3.1 — Layout

```
┌────────────────────────────────────────────────────────┐
│  [💬 Chat]  [📋 TODO (3)]  [📁 Documents]  [⚡ Activity] │
├──────────────────────┬─────────────────────────────────┤
│                      │                                  │
│  FILTER / BROWSE     │        DOCUMENT DETAIL           │
│                      │                                  │
│  🔍 [search...    ]  │  📄 Allianz-Policy-2025.pdf      │
│                      │  Category: insurance              │
│  ▾ All (2,163)       │  Source: john-personal (email)    │
│    invoices (239)    │  Date: 2025-01-15                │
│    bank-stmts (104)  │  Amount: $1,234.00               │
│  ► receipts (39)     │  Vendor: Allianz Australia        │
│  ► tax (34)          │  Status: ✅ extracted ✅ embedded  │
│  ► payslips (2)      │                                  │
│  ► unsorted (1,736)  │  ── Extracted Content ──────     │
│                      │                                  │
│  ── Documents ──     │  ## Allianz Car Insurance         │
│  📄 Allianz-Poli...  │  Policy Number: AZ-2025-1234    │
│  📄 AGL-Invoice...   │  Vehicle: 2020 Toyota Camry      │
│  📄 Telstra-Feb...   │  Premium: $1,234.00 per annum   │
│  📄 Westpac-Sta...   │  ...                            │
│  📄 ...              │                                  │
│                      │  [Open File] [Reclassify] [Re-extract] │
└──────────────────────┴─────────────────────────────────┘
```

### 3.2 — Left: Category tree + document list

**Category tree** (collapsible):
- Each category shows document count
- Click category → filter document list
- "All" shows everything
- Nested categories supported (`property/manorwoods`)

**Document list** (below category tree or replacing it when a category is selected):
- Compact rows: icon + filename + date + status dots
- Status dots: 🟢 fully processed, 🟡 partially processed, 🔴 error
- Sorted by date descending (newest first) by default
- Sortable by: date, name, amount, category
- Paginated or virtual-scrolled for large lists

**Search bar** at top:
- Filters the document list (client-side for current category, or FTS5 for global)
- Searches filename, vendor, sender

### 3.3 — Right: Document detail pane

When a document is selected from the list:

**Header**: filename, category badge, source info (email account or watch folder)

**Metadata grid**:
| Field | Value |
|-------|-------|
| Date | 2025-01-15 |
| Amount | $1,234.00 |
| Vendor | Allianz Australia |
| Sender | noreply@allianz.com.au |
| Category | insurance |
| SHA-256 | abc123... (truncated) |
| Ingested | 2026-03-28 14:30 |
| Extracted | 2026-03-28 14:31 |
| Embedded | 2026-03-28 14:32 |

**Content preview**: Extracted text or structured markdown rendered as formatted text. For PDFs: the markdown output from the extraction pipeline. For CSVs: rendered as a table.

**Actions** (bottom of detail pane):
- **[Open File]** — opens in default app (existing behaviour)
- **[Reclassify ▾]** — dropdown with category list, moves file + updates DB
- **[Re-extract]** — queues document for re-extraction on next sync cycle
- **[Open in Explorer]** — opens the containing folder

### 3.4 — Processing status indicators

Each document shows its pipeline status:

```
⚙ classify → ⚙ extract → ⚙ embed
   ✅           ✅          🔄       ← this one is pending embedding
```

Or as compact dots: `✅✅🔄` (classified, extracted, embedding pending)

For error states: `✅❌—` (classified, extraction failed, embedding skipped)

---

## 4. Activity Tab

### 4.1 — Layout

```
┌────────────────────────────────────────────────────────┐
│  [💬 Chat]  [📋 TODO (3)]  [📁 Documents]  [⚡ Activity]│
├────────────────────────────────────────────────────────┤
│                                                         │
│  🔵 14:32:05  Sync cycle completed                     │
│     john-personal: 3 new messages, 2 attachments       │
│     smitha: 0 new messages                             │
│     john-work: 1 new message, 1 attachment             │
│                                                         │
│  🟢 14:32:04  Classified 3 documents                   │
│     2026-04-01_allianz_renewal.pdf → insurance         │
│     2026-03-30_agl_invoice.pdf → invoices              │
│     photo_001.jpg → unsorted                           │
│                                                         │
│  🟢 14:32:03  Extracted 2 documents                    │
│     Allianz: $1,234.00, due 15 Apr 2026               │
│     AGL: $287.50, due 8 Apr 2026                      │
│                                                         │
│  🟡 14:32:02  Backfill john-personal                   │
│     Scanned 200 messages (6,540 / ~8,500 total)       │
│                                                         │
│  🔴 14:31:58  Extraction failed                        │
│     scan_Receipt_2025.pdf — OCR timeout (Ollama)       │
│                                                         │
│  🟢 14:31:55  Created 2 reminders                      │
│     AGL Energy $287.50 due 8 Apr                       │
│     Allianz $1,234.00 due 15 Apr                       │
│                                                         │
│  ─── Earlier today ────────────────────────────────    │
│  🔵 14:17:05  Sync cycle completed ...                 │
│                                                         │
│  [Clear] [Export Log]                                  │
└────────────────────────────────────────────────────────┘
```

### 4.2 — Event types

| Icon | Category | Examples |
|------|----------|----------|
| 🔵 | Sync | Sync cycle start/complete, email counts, backfill progress |
| 🟢 | Success | Document classified, extracted, embedded, reminder created |
| 🟡 | Warning | Low extraction confidence, backfill slow, Ollama unavailable |
| 🔴 | Error | Extraction failed, Gmail auth expired, disk full |
| ⚪ | Info | Config reloaded, UI refreshed, MCP tool invoked |

### 4.3 — Data source

Activity events come from the Serilog logger. Two options:

**Option A (simple)**: Ring buffer in memory — last N events, lost on restart. Populated by hooking into the existing `Algebra.Logger` calls.

**Option B (persistent)**: SQLite `activity_log` table. Survives restarts. Queryable. Adds schema but more useful.

**Recommendation**: Option B — a lightweight table:

```sql
CREATE TABLE IF NOT EXISTS activity_log (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp   TEXT NOT NULL DEFAULT (datetime('now')),
    level       TEXT NOT NULL,  -- 'info', 'success', 'warning', 'error'
    category    TEXT NOT NULL,  -- 'sync', 'classify', 'extract', 'embed', 'reminder', 'config'
    message     TEXT NOT NULL,
    details     TEXT            -- JSON, optional extra data
);

CREATE INDEX IF NOT EXISTS idx_activity_ts ON activity_log(timestamp);
```

Auto-purge entries older than 7 days to prevent unbounded growth.

---

## 5. Sidebar Updates

### Processing queue badge

Below the INDEX expander, add a QUEUE section (only visible when items are pending):

```
▾ QUEUE
  ⏳ 5 to classify
  ⏳ 23 to extract
  ⏳ 156 to embed
```

Data from `ServiceHost.countUnclassified`, `countUnextracted`, and a new `countUnembedded`.

### Documents tab badge

The Documents tab button shows total count:
```
[📁 Documents (2,163)]
```

---

## 6. ViewModel Extension

### New properties on `ShellViewModel`

```csharp
// Tab state
public enum MainTab { Chat, Todo, Documents, Activity }
public MainTab ActiveTab { get; set; }

// Documents tab
public ObservableCollection<CategoryNode> Categories { get; }
public ObservableCollection<DocumentSummary> DocumentList { get; }
public DocumentDetail? SelectedDocument { get; set; }
public string DocumentSearchQuery { get; set; }
public string SelectedCategory { get; set; }

// Activity tab
public ObservableCollection<ActivityEvent> ActivityLog { get; }

// Queue
public int UnclassifiedCount { get; }
public int UnextractedCount { get; }
public int UnembeddedCount { get; }

// Records
public record CategoryNode(string Name, string FullPath, int Count, bool IsExpanded);
public record DocumentSummary(long Id, string FileName, string Category, string? Date, 
    string? Amount, bool IsExtracted, bool IsEmbedded, bool HasError);
public record DocumentDetail(long Id, string FileName, string Category, string SavedPath,
    string? Sender, string? Date, string? Amount, string? Vendor,
    string? ExtractedText, string? MarkdownContent,
    DateTimeOffset IngestedAt, DateTimeOffset? ExtractedAt, DateTimeOffset? EmbeddedAt);
public record ActivityEvent(DateTimeOffset Timestamp, string Level, string Category, 
    string Message, string? Details);
```

### New Core queries needed

```fsharp
// Stats.fs or new DocumentBrowser.fs
let listDocuments (db: Database) (category: string option) (offset: int) (limit: int) : Task<DocumentSummary list>
let getDocumentDetail (db: Database) (id: int64) : Task<DocumentDetail option>
let getActivityLog (db: Database) (limit: int) : Task<ActivityEvent list>
let logActivity (db: Database) (level: string) (category: string) (message: string) (details: string option) : Task<unit>
let reclassifyDocument (db: Database) (fs: FileSystem) (id: int64) (newCategory: string) (archiveDir: string) : Task<Result<unit, string>>
let queueReextract (db: Database) (id: int64) : Task<unit>
```

---

## 7. Implementation Phases

Each phase is a silver thread — backend + UI + wired.

| Phase | What | Proof |
|-------|------|-------|
| **U1** | 4-tab shell layout (Chat, TODO, Documents, Activity). Tab switching works. Empty states for Documents and Activity. | Click each tab → correct panel shows. Documents shows "Select a category to browse." Activity shows "No recent activity." |
| **U2** | Documents tab: category tree from `Stats.getCategoryCounts` + document list from `listDocuments`. Click category → filtered list. | Click "invoices" → see 239 documents listed. Click "All" → see 2,163. |
| **U3** | Document detail pane: click document → see metadata + extracted content preview. Open File button works. | Click a document → detail pane shows sender, date, amount, vendor, extracted text. Click Open File → opens in default app. |
| **U4** | Reclassify + Re-extract actions. Reclassify moves file + updates DB. Re-extract clears extracted_at. | Reclassify a document from "unsorted" to "invoices" → file moves on disk, category updates in list. |
| **U5** | Activity log: `activity_log` table, `logActivity` calls wired into ServiceHost sync cycle. Activity tab shows recent events. | Run a sync → Activity tab shows sync/classify/extract events with timestamps. |
| **U6** | Processing queue badge in sidebar. Counts from DB. | Queue section shows pending counts. After sync cycle, counts decrease. |

---

## 8. Open Questions

| # | Question | Leaning |
|---|----------|---------|
| 1 | Should the document list use virtual scrolling for 2000+ items? | Yes — Avalonia `ListBox` with `VirtualizationMode="Simple"` handles this |
| 2 | Should Documents tab have a "grid view" (table) option besides the list? | Future — list view first, add DataGrid later |
| 3 | Should reclassify support drag-and-drop (drag document to category in tree)? | Future — button dropdown first |
| 4 | Should activity log stream in real-time or refresh on timer? | Timer (same 5-second refresh as other stats) — simpler than event streaming |
| 5 | Should document preview render markdown or show raw text? | Rendered markdown (using a simple markdown-to-Avalonia converter or TextBlock with basic formatting) |
| 6 | Should we show the original PDF inline (embedded viewer)? | No — too complex for v1. "Open File" button is sufficient. |
