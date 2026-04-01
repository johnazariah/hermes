# Hermes — Rich UI: VS Code-Style Three-Column Shell

> Design doc for the Hermes shell window — a VS Code-inspired layout with status bar, navigator, and adaptive content pane.  
> Supersedes the tab-based design. Builds on: doc 09, doc 12, doc 17, doc 18.  
> Created: 2026-04-01. Revised: 2026-04-01.

---

## 1. Problem

The current UI shows aggregate stats but you can't see individual documents, email threads, or processing history. The original doc 15 proposed a 4-tab layout (Chat, TODO, Documents, Activity). But tabs force context-switching — "I see a bill reminder, I want to see the document, then check the email thread" means 3 tab switches.

**The real workflow is cross-cutting.** Users navigate between documents, threads, reminders, and chat fluidly. The UI should support this without losing context.

---

## 2. Architecture: Three Columns

Inspired by VS Code (Activity Bar + Side Bar + Editor), Outlook (folders + list + reading pane), and Finder (sidebar + content).

```
┌─────────────────────────────────────────────────────────────┐
│  ⚡ Hermes — Document Intelligence                    ─ □ × │
├────────┬──────────────────┬─────────────────────────────────┤
│        │                  │                                  │
│ STATUS │   NAVIGATOR      │      CONTENT PANE                │
│  BAR   │                  │                                  │
│ (left) │  (middle)        │  (right, adaptive)               │
│        │                  │                                  │
│ 60px   │  240px           │  fill remaining                  │
│ fixed  │  resizable       │                                  │
│        │                  │                                  │
├────────┴──────────────────┴─────────────────────────────────┤
│  ●● Ready · 2,163 docs · 3 action items · Backfill 74%     │
└─────────────────────────────────────────────────────────────┘
```

### 2.1 — Status Bar (left column, narrow)

The existing left panel shrinks to a **VS Code Activity Bar** — icon buttons that switch the navigator's active section, plus at-a-glance health indicators.

```
┌────────┐
│  💬    │  ← Chat (default)
│  📋    │  ← Action Items (badge: 🔴1 🟡2)
│  📁    │  ← Documents
│  📧    │  ← Email Threads
│  ⏰    │  ← Timeline
│  ⚡    │  ← Activity Log
│        │
│  ──── │
│        │
│  ● Oll │  ← Ollama status dot
│  ● DB  │  ← Database status dot
│  ● MCP │  ← MCP server status dot
│        │
│  ──── │
│        │
│  ⚙    │  ← Settings
└────────┘
```

- Each icon button switches what the Navigator (middle column) shows
- Active icon is highlighted (accent colour left border, like VS Code)
- Service health dots are always visible at the bottom
- Badge overlays on Action Items icon show overdue/upcoming counts
- Tooltip on each icon shows full label

### 2.2 — Navigator (middle column, resizable)

Shows a context-specific list/tree based on which activity bar icon is selected. `GridSplitter` between navigator and content pane.

**When 💬 Chat is active:**
```
┌──────────────────┐
│ 🔍 [search...]   │
│                   │
│ Suggested queries │
│ [car insurance]   │
│ [recent invoices] │
│ [tax documents]   │
│                   │
│ Recent            │
│  "car insurance"  │
│  "bank statements"│
│  "allianz renewal"│
└──────────────────┘
```
Input box at top. Suggested query chips when empty. Recent search history below.

**When 📋 Action Items is active:**
```
┌──────────────────┐
│ ACTION ITEMS (3)  │
│                   │
│ ⚠ OVERDUE         │
│  🔴 Allianz $1234 │
│                   │
│ 📋 UPCOMING       │
│  🟡 AGL $287      │
│  🟡 Telstra $89   │
│                   │
│ ✅ COMPLETED       │
│  (collapsed)      │
└──────────────────┘
```
Click a reminder → content pane shows detail + action buttons.

**When 📁 Documents is active:**
```
┌──────────────────┐
│ 🔍 [filter...]   │
│                   │
│ ▾ All (2,163)     │
│   invoices (239)  │
│   bank-stmts(104) │
│   receipts (39)   │
│   tax (34)        │
│   payslips (15)   │
│   insurance (28)  │
│   ► property      │
│   unsorted (1736) │
│                   │
│ ── invoices ──    │
│ 📄 Allianz-Pol... │
│ 📄 AGL-Invoice... │
│ 📄 Telstra-Feb... │
│ 📄 ...            │
└──────────────────┘
```
Category tree at top. Click category → document list fills below. Click document → content pane shows detail.

**When 📧 Email Threads is active:**
```
┌──────────────────┐
│ 🔍 [search...]   │
│                   │
│ Recent Threads    │
│ 📧 Allianz Renew  │
│    5 msgs · 2 att │
│ 📧 AGL Account    │
│    3 msgs · 1 att │
│ 📧 Microsoft HR   │
│    12 msgs · 4 att│
│ 📧 Ray White      │
│    8 msgs · 6 att │
│                   │
│ By Account        │
│ ▾ john-personal   │
│   📧 thread...    │
│ ▾ smitha          │
│   📧 thread...    │
└──────────────────┘
```
Threads grouped by recency or by account. Click thread → content pane shows timeline.

**When ⏰ Timeline is active:**
```
┌──────────────────┐
│ ⏰ Timeline       │
│                   │
│ Today (3)         │
│  📄 AGL invoice   │
│  📄 Westpac stmt  │
│  📧 Allianz reply │
│                   │
│ Yesterday (5)     │
│  📄 Telstra bill  │
│  📄 payslip       │
│  📧 Microsoft HR  │
│  📄 receipt       │
│  📄 receipt       │
│                   │
│ This Week (12)    │
│ Last Week (8)     │
│ This Month (45)   │
└──────────────────┘
```
Chronological feed of all documents and emails. Click any → content pane shows detail.

**When ⚡ Activity is active:**
```
┌──────────────────┐
│ ⚡ Activity Log   │
│                   │
│ 🔵 14:32 Sync     │
│   3 new, 2 attach │
│ 🟢 14:32 Classfy  │
│   3 documents     │
│ 🟢 14:32 Extract  │
│   2 documents     │
│ 🟡 14:32 Backfill │
│   200 msgs (74%)  │
│ 🔴 14:31 Error    │
│   OCR timeout     │
│ 🟢 14:31 Reminder │
│   2 new bills     │
│                   │
│ [Clear] [Export]  │
└──────────────────┘
```
Scrolling event log. Click an event → content pane shows detail (e.g., click a classification event → shows the document).

### 2.3 — Content Pane (right, adaptive)

Renders whatever's selected in the navigator. The content pane **adapts its layout** based on the item type:

**Document selected:**
```
┌─────────────────────────────────────────┐
│ 📄 Allianz-Policy-2025.pdf              │
│ Category: insurance  🏷 car             │
│ Classified by: LLM (92%)               │
│                                          │
│ ┌─ Metadata ──────────────────────────┐ │
│ │ Date:    2025-01-15                 │ │
│ │ Amount:  $1,234.00                  │ │
│ │ Vendor:  Allianz Australia          │ │
│ │ Sender:  noreply@allianz.com.au     │ │
│ │ Source:  john-personal (email)      │ │
│ │ Status:  ✅ extracted ✅ embedded   │ │
│ └─────────────────────────────────────┘ │
│                                          │
│ ── Content ─────────────────────────── │
│                                          │
│ ## Allianz Car Insurance                │
│                                          │
│ - **Policy:** AZ-2025-1234              │
│ - **Vehicle:** 2020 Toyota Camry        │
│ - **Premium:** $1,234.00 per annum      │
│                                          │
│ | Coverage    | Limit       |           │
│ |-------------|-------------|           │
│ | Third Party | $20,000,000 |           │
│ | Fire/Theft  | $35,000     |           │
│ | Windscreen  | $500        |           │
│                                          │
│ [Open File] [Reclassify ▾] [Re-extract] │
│ [Show Email Thread]                     │
└─────────────────────────────────────────┘
```

**Email thread selected:**
```
┌─────────────────────────────────────────┐
│ 📧 Allianz Car Insurance Renewal        │
│ 5 messages · 2 attachments · Dec–Jan    │
│                                          │
│ ── Summary ─────────────────────────── │
│ Car insurance renewed with 5% loyalty   │
│ discount. Premium: $1,172.30.           │
│                                          │
│ ── Timeline ────────────────────────── │
│                                          │
│ ┌─ 15 Dec 2025 ───── Allianz → John  ┐│
│ │ Renewal notice. Premium $1,234.00.  ││
│ │ 📎 Allianz-Renewal-2025.pdf          ││
│ └─────────────────────────────────────┘│
│                                          │
│ ┌─ 28 Dec 2025 ───── John → Allianz  ┐│
│ │ Requested breakdown of premium      ││
│ │ increase.                            ││
│ └─────────────────────────────────────┘│
│                                          │
│ ┌─ 5 Jan 2026 ────── Allianz → John  ┐│
│ │ Increase due to CTP changes.        ││
│ │ Offered 5% loyalty discount.        ││
│ └─────────────────────────────────────┘│
│                                          │
│ ┌─ 8 Jan 2026 ────── John → Allianz  ┐│
│ │ Accepted renewal with discount.     ││
│ └─────────────────────────────────────┘│
│                                          │
│ ┌─ 20 Jan 2026 ───── Allianz → John  ┐│
│ │ Confirmed. New premium $1,172.30.   ││
│ │ 📎 Allianz-Certificate-2026.pdf      ││
│ └─────────────────────────────────────┘│
│                                          │
│ [Open in Gmail]                         │
└─────────────────────────────────────────┘
```

Clicking an attachment link (📎) navigates to that document in the content pane. Clicking a timeline entry expands to show the full message body.

**Action item (reminder) selected:**
```
┌─────────────────────────────────────────┐
│ 🔴 Allianz Car Insurance     $1,234.00 │
│ Due: 15 Jan 2026 (overdue by 76 days)  │
│                                          │
│ 📄 Allianz-Renewal-2025.pdf             │
│ 📧 Thread: 5 messages                   │
│                                          │
│ [Mark Paid ✓] [Snooze 7d ⏰] [Dismiss ×]│
└─────────────────────────────────────────┘
```

Clicking the document or thread link navigates to that view in the content pane (without changing the navigator — breadcrumb-style navigation).

**Chat active:**
```
┌─────────────────────────────────────────┐
│ You: find my car insurance documents    │
│                                          │
│ Hermes:                                 │
│ ┌ 📄 Allianz-Policy-2025.pdf ──────── ┐│
│ │ insurance · 2025-01-15 · $1,234     ││
│ │ "comprehensive car insurance..."     ││
│ └─────────────────────────────────────┘│
│ ┌ 📄 Allianz-Certificate-2026.pdf ─── ┐│
│ │ insurance · 2026-01-20 · $1,172     ││
│ └─────────────────────────────────────┘│
│                                          │
│ AI: You have two car insurance docs...  │
│                                          │
│ ┌──────────────────────────┬──┬──┐     │
│ │ Ask Hermes...            │AI│→ │     │
│ └──────────────────────────┴──┴──┘     │
└─────────────────────────────────────────┘
```

Clicking a document card in chat results navigates to the document view.

---

## 3. Cross-Cutting Navigation

The key UX principle: **everything is linked**. The user follows connections without switching modes.

| From | Click | Content pane shows |
|------|-------|-------------------|
| Reminder card | Document link (📄) | Document detail + markdown preview |
| Reminder card | Thread link (📧) | Email thread timeline |
| Document detail | "Show Email Thread" button | Thread timeline for that document's thread |
| Thread timeline | Attachment link (📎) | Document detail for that attachment |
| Chat result | Document card | Document detail |
| Activity event | Classification entry | Document detail for that document |
| Timeline entry | Any item | Document detail or thread view |
| Document detail | Category badge | Navigator switches to Documents, filtered to that category |
| Document detail | Sender link | Navigator filters to all documents from that sender |

**Breadcrumb navigation**: Content pane has a back button (←) and breadcrumb trail:
```
← 📋 Action Items / 🔴 Allianz Renewal / 📄 Allianz-Policy-2025.pdf
```

This lets the user drill into connected items and navigate back without losing their place.

---

## 4. Content Type Renderers

The content pane uses a renderer registry — each content type has a builder function:

```csharp
// Renderer dispatch
Control RenderContent(NavigationItem item) => item switch
{
    DocumentItem doc => RenderDocumentDetail(doc),
    ThreadItem thread => RenderThreadTimeline(thread),
    ReminderItem reminder => RenderReminderDetail(reminder),
    ChatSession chat => RenderChatView(chat),
    ActivityEvent evt => RenderActivityDetail(evt),
    TimelineDay day => RenderTimelineList(day),
    CategoryView cat => RenderDocumentList(cat),
    _ => RenderEmptyState()
};
```

### Document renderer
- Metadata grid (key-value pairs)
- Classification badge with tier + confidence
- Structured markdown preview (tables rendered as grids, headings as styled text)
- Pipeline status dots (classify ✅ → extract ✅ → embed 🔄)
- Action buttons: Open File, Reclassify, Re-extract, Show Thread

### Thread renderer
- LLM-generated summary at top (if available)
- Chronological message cards with sender, date, preview
- Attachment links inline (📎 click → document detail)
- "Summarise Thread" button (triggers LLM summary generation)

### Reminder renderer
- Same as doc 12 reminder cards but full-width in content pane
- Connected document preview inline
- Connected thread link
- Action buttons: Mark Paid, Snooze, Dismiss

### Chat renderer
- Same as current chat implementation but in the content pane
- Document result cards are clickable → navigate to document detail
- Chat input bar at bottom

---

## 5. Data Model Additions

### Thread grouping

Gmail provides `thread_id` on every message. Group messages by thread:

```sql
-- Thread summary view
SELECT 
    thread_id,
    MIN(date) as first_date,
    MAX(date) as last_date,
    COUNT(*) as message_count,
    GROUP_CONCAT(DISTINCT sender) as participants,
    (SELECT subject FROM messages m2 WHERE m2.thread_id = m.thread_id ORDER BY date ASC LIMIT 1) as subject,
    (SELECT COUNT(*) FROM documents d WHERE d.gmail_id IN 
        (SELECT gmail_id FROM messages m3 WHERE m3.thread_id = m.thread_id)) as attachment_count
FROM messages m
GROUP BY thread_id
HAVING message_count >= 2
ORDER BY last_date DESC;
```

### Thread summaries table

```sql
CREATE TABLE IF NOT EXISTS thread_summaries (
    thread_id       TEXT PRIMARY KEY,
    account         TEXT NOT NULL,
    subject         TEXT,
    summary         TEXT,           -- LLM-generated markdown summary
    timeline_md     TEXT,           -- full chronological timeline markdown
    message_count   INTEGER,
    attachment_count INTEGER,
    first_date      TEXT,
    last_date       TEXT,
    participants    TEXT,           -- comma-separated
    generated_at    TEXT,
    stale           INTEGER NOT NULL DEFAULT 0  -- set to 1 when new messages arrive
);
```

### Activity log table

```sql
CREATE TABLE IF NOT EXISTS activity_log (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp   TEXT NOT NULL DEFAULT (datetime('now')),
    level       TEXT NOT NULL,
    category    TEXT NOT NULL,
    message     TEXT NOT NULL,
    details     TEXT,
    document_id INTEGER REFERENCES documents(id)
);
CREATE INDEX IF NOT EXISTS idx_activity_ts ON activity_log(timestamp);
```

Auto-purge > 7 days.

---

## 6. ViewModel Architecture

### Navigation model

```csharp
// What's selected in the activity bar
public enum NavigatorMode { Chat, ActionItems, Documents, Threads, Timeline, Activity }

// Navigation stack for breadcrumb
public record NavigationItem(string Type, string Label, long? Id);

public class ShellViewModel : INotifyPropertyChanged
{
    // Activity bar
    public NavigatorMode ActiveMode { get; set; }
    
    // Navigator content (depends on ActiveMode)
    public ObservableCollection<CategoryNode> Categories { get; }
    public ObservableCollection<DocumentSummary> DocumentList { get; }
    public ObservableCollection<ThreadSummary> ThreadList { get; }
    public ObservableCollection<TimelineGroup> TimelineGroups { get; }
    public ObservableCollection<ActivityEvent> ActivityLog { get; }
    
    // Content pane
    public NavigationItem? CurrentItem { get; set; }
    public Stack<NavigationItem> NavigationStack { get; }  // for back button
    
    // Content data (populated when CurrentItem changes)
    public DocumentDetail? SelectedDocument { get; }
    public ThreadDetail? SelectedThread { get; }
    public ReminderItem? SelectedReminder { get; }
    
    // Actions
    public void NavigateTo(NavigationItem item);
    public void NavigateBack();
    
    // Search
    public string SearchQuery { get; set; }
    public ObservableCollection<SearchResult> SearchResults { get; }
}
```

### Records

```csharp
public record ThreadSummary(string ThreadId, string Subject, string? Summary,
    int MessageCount, int AttachmentCount, string LastDate, string Participants);

public record ThreadDetail(string ThreadId, string Subject, string? Summary,
    List<ThreadMessage> Messages, List<DocumentSummary> Attachments);

public record ThreadMessage(string Sender, string Date, string Body, 
    bool IsUser, List<string> AttachmentIds);

public record TimelineGroup(string Label, DateTimeOffset Date, List<TimelineEntry> Entries);

public record TimelineEntry(string Type, string Label, long? DocumentId, 
    string? ThreadId, DateTimeOffset Timestamp);
```

---

## 7. Implementation Phases — Silver Thread

### Phase U1: Three-Column Shell Layout

**Thread**: Activity bar click → navigator panel switches → empty content pane shows placeholder.

| Layer | What |
|-------|------|
| **ShellWindow.axaml** | 3-column Grid: activity bar (60px fixed), navigator (240px + GridSplitter), content pane (fill). Activity bar with 6 icon buttons + 3 service dots + settings button. |
| **ShellWindow.axaml.cs** | Icon button click → sets `ActiveMode` → navigator panel visibility toggles. Content pane shows empty state per mode. |
| **ShellViewModel.cs** | `NavigatorMode` enum, `ActiveMode` property with change notification. |
| **PROOF** | Launch app → see 3 columns → click each activity bar icon → navigator changes, content shows "Select an item" placeholder → service dots show correct status. |

### Phase U2: Documents Navigator + Document Detail

**Thread**: Click Documents icon → see category tree → click category → see document list → click document → see metadata + markdown preview.

| Layer | What |
|-------|------|
| **Core: DocumentBrowser.fs** | `listDocuments`, `getDocumentDetail` queries |
| **ShellViewModel.cs** | `Categories`, `DocumentList`, `SelectedDocument` properties. Category click → load filtered list. Document click → load detail. |
| **Navigator panel** | Category tree (TreeView or stacked Expanders) + document list (ListBox with virtual scrolling). |
| **Content pane** | Document renderer: metadata grid + markdown preview (styled TextBlocks) + action buttons. |
| **PROOF** | Click 📁 → see categories with counts → click "invoices" → see 239 documents → click one → content pane shows metadata, extracted markdown with tables rendered, classification badge. Click "Open File" → opens in default app. |

### Phase U3: Email Threads Navigator + Thread Timeline

**Thread**: Click Threads icon → see thread list → click thread → see chronological timeline with message bodies and attachment links.

| Layer | What |
|-------|------|
| **Core: Threads.fs** (new) | `listThreads`, `getThreadDetail`, `generateThreadSummary` (LLM) |
| **ShellViewModel.cs** | `ThreadList`, `SelectedThread`. Thread click → load detail with messages. |
| **Navigator panel** | Thread list with subject, message count, date range, participant avatars. |
| **Content pane** | Thread renderer: summary block + chronological message cards. Attachment links (📎) navigate to document detail. |
| **PROOF** | Click 📧 → see threads sorted by recency → click "Allianz Renewal" → content pane shows 5 messages chronologically with sender/date/body and 2 attachment links. Click 📎 → content pane switches to document detail. Back button (←) returns to thread. |

### Phase U4: Action Items + Cross-Navigation

**Thread**: Click Action Items → see reminders → click reminder → see detail with document link → click document → see preview → back button returns to reminder.

| Layer | What |
|-------|------|
| **ShellViewModel.cs** | `NavigateTo(item)`, `NavigateBack()`, `NavigationStack`. |
| **Content pane** | Breadcrumb bar at top. Back button. Navigation history. |
| **Reminder renderer** | Full-width reminder detail with inline document preview link and thread link. |
| **PROOF** | Click 📋 → see overdue bill → click it → content shows reminder detail + "📄 Allianz-Policy-2025.pdf" link → click link → content shows document detail → breadcrumb shows "Action Items / Allianz / Allianz-Policy-2025.pdf" → click ← → back to reminder. |

### Phase U5: Timeline + Activity Log

**Thread**: Click Timeline → see chronological feed → click Activity → see processing log with clickable entries.

| Layer | What |
|-------|------|
| **Core** | `getTimeline` (group documents + emails by day), `getActivityLog`, `logActivity` |
| **Database.fs** | `activity_log` table schema |
| **ServiceHost.fs** | Wire `logActivity` calls into sync cycle (sync start/complete, classify, extract, errors) |
| **Navigator panels** | Timeline: day-grouped entries. Activity: event list with level icons. |
| **Content pane** | Timeline: list of documents/emails for that day. Activity: event detail. Click document in either → navigates to document detail. |
| **PROOF** | Click ⏰ → see "Today (3)" with documents → click ⚡ → see processing log → trigger sync → new events appear → click a classification event → navigates to that document. |

### Phase U6: Chat Integration + Search

**Thread**: Click Chat → search + AI conversation → click document card in results → navigate to document detail.

| Layer | What |
|-------|------|
| **ShellViewModel.cs** | Chat moved from separate tab to content pane under Chat mode. Search results return NavigationItems. |
| **Content pane / Chat renderer** | Existing chat UI, but document cards are clickable → `NavigateTo(DocumentItem)`. |
| **Navigator / Chat panel** | Search bar + suggested queries + recent searches. |
| **PROOF** | Click 💬 → see search bar + suggestions → type "car insurance" → results in content pane → click document card → document detail loads → back → returns to chat results. |

---

## 8. Open Questions

| # | Question | Leaning |
|---|----------|---------|
| 1 | Should the activity bar be on the left (VS Code) or top (browser tabs)? | Left — matches VS Code, saves vertical space |
| 2 | Should navigator width persist across sessions? | Yes — save to config.yaml under `ui.navigatorWidth` |
| 3 | Should the content pane support split view (two documents side by side)? | Future — single pane for v1 |
| 4 | Should thread summaries be generated eagerly or on demand? | On demand — "Summarise" button in thread view. Cache result. |
| 5 | Should search work across all content types (docs + threads + reminders)? | Yes — unified search, results grouped by type |
| 6 | Should the activity bar show unread counts (new docs since last viewed)? | Yes on Documents and Timeline icons — subtle badge |
| 7 | Dark mode? | Use Avalonia's FluentTheme dark variant — toggle in settings. Not for v1. |
| 8 | Keyboard navigation? | Arrow keys in navigator, Enter to open in content, Escape to go back. Phase U7 polish. |
