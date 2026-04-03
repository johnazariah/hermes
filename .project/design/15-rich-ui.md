# Hermes — Rich UI: Pipeline Funnel Shell

> Design doc for the Hermes shell window — a pipeline funnel layout where documents visibly flow from sources through extraction and classification into the indexed library.  
> Supersedes all previous UI designs (doc 09, activity bar, tab-based).  
> Created: 2026-04-01. Revised: 2026-04-03.

---

## 1. Core Concept: The Pipeline Funnel

The navigator sidebar is a **visual representation of the document processing pipeline**, read top to bottom:

```
SOURCES          ← where documents come from
    ↓
INTAKE           ← raw files awaiting extraction
    ↓
EXTRACTING       ← being parsed into structured markdown
    ↓
CLASSIFYING      ← extracted, deciding what it is
    ↓
LIBRARY          ← classified documents by category
    ↓
INDEX            ← searchable + embedded coverage
    ↓
ACTION ITEMS     ← bills, reminders (from trigger evaluation)
    ↓
SERVICES         ← infrastructure health + controls
```

Documents flow downward. The user sees at a glance where everything is. Counts add up: `intake + extracting + classifying + library = total ingested`.

The funnel is **always visible** — no mode switching, no hidden state. Click any section or item to see its detail in the content pane.

---

## 2. Layout: Three Columns

```
┌────────────────────────┬─────────────────────────┬───────────────────┐
│                        │                          │                   │
│  PIPELINE FUNNEL       │    CONTENT PANE          │   CHAT PANE       │
│  (navigator)           │   (adaptive)             │   (toggle via 💬) │
│                        │                          │                   │
│  ── SOURCES ────────── │                          │  You: car ins..  │
│  📧 john-personal      │                          │                   │
│     357 emails         │  📄 Allianz-Policy.pdf   │  Hermes:          │
│  📧 smitha             │  Category: insurance     │  Found 2 docs...  │
│     1,250 emails       │  Amount: $1,234.00       │                   │
│  📁 ~/Downloads *.pdf  │                          │                   │
│                        │  ## Policy Details        │                   │
│  ── INTAKE (12) ────── │  - **Policy:** AZ-1234   │                   │
│  📥 12 new files       │  - **Vehicle:** Camry    │                   │
│                        │                          │                   │
│  ── EXTRACTING (5) ─── │  | Coverage  | Limit |   │                   │
│  ⏳ 5 in progress      │  |-----------|-------|   │                   │
│                        │  | 3rd Party | $20M  |   │                   │
│  ── CLASSIFYING (3) ── │                          │ ┌───────────┬──┐ │
│  ⏳ 3 pending          │  [Open] [Reclassify ▾]   │ │Ask Hermes │→ │ │
│                        │  [Re-extract]            │ └───────────┴──┘ │
│  ── LIBRARY ────────── │                          │                   │
│  📄 2,163 documents    │                          │                   │
│    invoices (239)      │                          │                   │
│    bank-stmts (104)    │                          │                   │
│    payslips (15)       │                          │                   │
│    ► property          │                          │                   │
│                        │                          │                   │
│  ── INDEX ──────────── │                          │                   │
│  🔍 2,100 searchable   │                          │                   │
│  🧠 1,650 embedded     │                          │                   │
│                        │                          │                   │
│  ── ACTION ITEMS ───── │                          │                   │
│  🔴 1  🟡 2            │                          │                   │
│                        │                          │                   │
│  ── SERVICES ────────── │                          │                   │
│  ● Ollama  ● DB  ● MCP │                          │                   │
│  💬 ⇆  ⚙              │                          │                   │
├────────────────────────┴─────────────────────────┴───────────────────┤
│  ●● Ready · 2,163 docs · 5 extracting · 3 classifying · 3 items     │
└──────────────────────────────────────────────────────────────────────┘
```

**Three columns:**
- **Pipeline Funnel** (left, 260px, resizable via GridSplitter): persistent pipeline view, always visible, always showing the full document lifecycle.
- **Content Pane** (centre, fill): adaptive renderer — shows detail for whatever's clicked in the funnel.
- **Chat Pane** (right, 300px, toggleable via 💬): always available for questions alongside browsing.

**No activity bar, no mode switching.** The entire pipeline is visible at once. The user sees sources, processing stages, library, index, and services simultaneously.

---

## 3. Pipeline Funnel Sections

Each section is an Avalonia `Expander` with a header showing the section name + count. Processing sections (Intake, Extracting, Classifying) auto-collapse when empty to save space.

### 3.1 — SOURCES

Where documents come from. Always visible.

```
── SOURCES ──────────────────
📧 john.azariah.australia@gmail.com   ● Synced
   357 emails · 2 min ago
   Backfill ████████░░ 74%
📧 smitha.azariah@gmail.com           ● Synced
   1,250 emails · 14 min ago
📧 john.azariah@gmail.com             ● Synced
   1,252 emails · 14 min ago
📁 ~/Downloads  *.pdf
[+ Add Account]  [+ Add Folder]
```

**Click account** → content pane: account detail (sync history, message count, backfill progress, re-auth button)
**Click folder** → content pane: folder config (path, patterns, file count, last scan)
**[+ Add Account]** → Gmail OAuth dialog
**[+ Add Folder]** → folder picker

### 3.2 — INTAKE

Raw files in `unclassified/` awaiting extraction. Transient — not a failure state.

```
── INTAKE (12) ──────────────
📄 document_283847.pdf    3.2 MB  just now
📄 scan_receipt.pdf       890 KB  2 min ago
📄 westpac_april.csv      45 KB  2 min ago
📄 contract.docx          120 KB  5 min ago
...
```

**Click file** → content pane: raw file info (name, size, source email/folder, original filename, SHA256). For text files (CSV, TXT), show raw content. For PDFs, show "Awaiting extraction."

Auto-collapses when empty.

### 3.3 — EXTRACTING

Documents currently being parsed. Shows which extractor is running.

```
── EXTRACTING (5) ───────────
⏳ document_283847.pdf     PdfStructure
⏳ annual_report.pdf       PdfStructure (OCR fallback)
⏳ dividends_q4.xlsx       Excel
⏳ contract.docx           Word
⏳ transactions.csv        CSV
```

**Click file** → content pane: extraction status (extractor type, start time, any warnings/errors)

Auto-collapses when empty. On the 5-second refresh, items move from Extracting → Classifying.

### 3.4 — CLASSIFYING

Extracted documents awaiting classification. The key UX win of extract-first: **users can preview content before classification finishes**.

```
── CLASSIFYING (3) ──────────
📄 document_283847.pdf     extracted ✓
   Suggested: payslips (content: 87%)
📄 mystery_scan.pdf        extracted ✓
   Suggested: (LLM analyzing...)
📄 random_attachment.pdf   extracted ✓
   Suggested: unsorted (35% — unable to determine)
```

**Click file** → content pane: **extracted markdown preview + suggested category + manual classify dropdown**. User can see structured tables, headings, amounts and decide the category if auto-classification is slow or wrong.

The classify dropdown shows content-based suggestions:
```
[Classify as ▾]
  ★ payslips (content match: 87%)
  ★ tax (LLM: 62%)
  ──────────────
  invoices
  bank-statements
  receipts
  insurance
  ...
```

Auto-collapses when empty.

### 3.5 — LIBRARY

Classified documents by category. The main body of the archive.

```
── LIBRARY ──────────────────
📄 2,163 documents
  invoices (239)
  bank-statements (104)
  receipts (39)
  tax (34)
  payslips (15)
  insurance (28)
  ► property
    manorwoods (12)
    avalon (8)
  donations (6)
  subscriptions (4)
  utilities (18)
```

**Click category** → content pane: document list (compact rows with filename, date, amount, classification badge). Sorted by date descending. Virtual scrolling for large lists.

**Click document** → content pane: full document detail:
- Metadata grid (date, amount, vendor, sender, source, SHA256)
- Classification info: tier (rule/content/LLM) + confidence + reasoning
- Pipeline status: extract ✅ → classify ✅ → embed ✅/🔄
- **Structured markdown preview** with rendered tables, headings, key-value pairs
- Actions: [Open File] [Reclassify ▾] [Re-extract] [Open in Explorer]
- Related: [Show Email Thread] if from email

### 3.6 — INDEX

Search and embedding coverage.

```
── INDEX ────────────────────
🔍 2,100 / 2,163 searchable
   ████████████████████░ 97%
🧠 1,650 / 2,163 embedded
   ███████████████░░░░░ 76%
   DB: 45.2 MB
```

**Click** → content pane: detailed coverage stats, per-category breakdown, recent indexing activity.

Progress bars show the gap between total documents, FTS5-indexed, and vector-embedded.

### 3.7 — ACTION ITEMS

Bills, reminders, pending approvals from trigger evaluation.

```
── ACTION ITEMS ─────────────
🔴 1 overdue  🟡 2 upcoming
Total: $1,610.50 due
```

**Click** → content pane: reminder list with overdue (red), upcoming (amber), completed (green collapsed) sections.

**Click reminder** → content pane: full detail with Mark Paid / Snooze / Dismiss, linked document, linked email thread.

Hidden when no active reminders.

### 3.8 — SERVICES

Infrastructure health. Always at the bottom.

```
── SERVICES ─────────────────
● Ollama    3 models
● Database  2,163 docs · 45 MB
● MCP       localhost:21740 · 13 tools
● Pipeline  idle
💬 ⇆  ⚙
```

**Click service** → content pane: service detail (Ollama model list, MCP tool list, DB stats, pipeline cycle history)

💬 toggles the chat pane. ⚙ opens Settings dialog.

---

## 4. Content Pane Renderers

The content pane adapts by item type:

### Document Detail (Library → category → document)

```
┌──────────────────────────────────────────┐
│ 📄 Allianz-Policy-2025.pdf               │
│ Category: insurance 🏷 car               │
│ Classified by: LLM (92%)                │
│                                          │
│ ┌─ Metadata ───────────────────────────┐ │
│ │ Date:    2025-01-15                  │ │
│ │ Amount:  $1,234.00                   │ │
│ │ Vendor:  Allianz Australia           │ │
│ │ Sender:  noreply@allianz.com.au      │ │
│ │ Source:  john-personal (email)       │ │
│ │ Status:  ✅ extract ✅ classify ✅ embed│ │
│ └──────────────────────────────────────┘ │
│                                          │
│ ── Extracted Content ─────────────────── │
│                                          │
│ ## Allianz Car Insurance                 │
│ - **Policy:** AZ-2025-1234              │
│ - **Vehicle:** 2020 Toyota Camry        │
│                                          │
│ | Coverage    | Limit       |           │
│ |-------------|-------------|           │
│ | Third Party | $20,000,000 |           │
│ | Fire/Theft  | $35,000     |           │
│                                          │
│ [Open File] [Reclassify ▾] [Re-extract] │
│ [Show Email Thread]                     │
└──────────────────────────────────────────┘
```

### Classifying Document

Same as document detail but with prominent [Classify as ▾] dropdown and suggested categories. User can preview content and decide.

### Email Thread

```
┌──────────────────────────────────────────┐
│ 📧 Allianz Car Insurance Renewal         │
│ 5 messages · 2 attachments · Dec–Jan     │
│                                          │
│ ── Summary ─────────────────────────── │
│ Renewed with 5% loyalty discount.       │
│ Premium: $1,172.30.                     │
│                                          │
│ ── Timeline ────────────────────────── │
│ ┌─ 15 Dec ── Allianz → John ────────┐ │
│ │ Renewal notice. $1,234.00.         │ │
│ │ 📎 Allianz-Renewal-2025.pdf        │ │
│ └────────────────────────────────────┘ │
│ ┌─ 28 Dec ── John → Allianz ────────┐ │
│ │ Requested breakdown of increase.   │ │
│ └────────────────────────────────────┘ │
│ ┌─ 20 Jan ── Allianz → John ────────┐ │
│ │ Confirmed. $1,172.30.              │ │
│ │ 📎 Allianz-Certificate-2026.pdf    │ │
│ └────────────────────────────────────┘ │
└──────────────────────────────────────────┘
```

📎 links navigate to document detail. Back button returns.

### Reminder Detail

Full detail with Mark Paid / Snooze / Dismiss buttons, document link, thread link.

### Account Detail

Sync history, backfill progress, re-auth / remove buttons.

---

## 5. Chat Pane

Permanent right column. Toggleable via 💬. Default: visible.

- Click document card in chat results → content pane navigates to that document
- Search results include documents from all pipeline stages (even not-yet-classified)
- Chat input at bottom with AI toggle and send button
- Suggested query chips when empty

---

## 6. Status Bar

```
●● Ready · 2,163 docs · 5 extracting · 3 classifying · 3 action items · Backfill 74%
```

Reflects the pipeline state. States: Ready (green), Syncing (blue), Processing (yellow), Error (red).

---

## 7. Cross-Navigation

| From | Click | Content pane shows |
|------|-------|-------------------|
| Reminder | Document link | Document detail |
| Reminder | Thread link | Email thread timeline |
| Document detail | Show Email Thread | Thread timeline |
| Thread timeline | Attachment 📎 | Document detail |
| Chat result | Document card | Document detail |
| Classifying | Classify dropdown | Applies category, item moves to Library |
| Document detail | Category badge | Library filtered to that category |

Breadcrumb bar at top of content pane: `← Library / insurance / Allianz-Policy-2025.pdf`

---

## 8. ViewModel

```csharp
public class ShellViewModel : INotifyPropertyChanged
{
    // Pipeline stage counts (refresh every 5s)
    public int IntakeCount { get; }
    public int ExtractingCount { get; }
    public int ClassifyingCount { get; }
    public int LibraryCount { get; }
    
    // Sources
    public IReadOnlyList<AccountStats> Accounts { get; }
    public IReadOnlyList<WatchFolderConfig> WatchFolders { get; }
    
    // Library
    public IReadOnlyList<CategoryNode> Categories { get; }
    public IReadOnlyList<DocumentSummary> DocumentList { get; }
    
    // Index
    public int SearchableCount { get; }
    public int EmbeddedCount { get; }
    public double DatabaseSizeMb { get; }
    
    // Action Items
    public ObservableCollection<ReminderItem> OverdueReminders { get; }
    public ObservableCollection<ReminderItem> UpcomingReminders { get; }
    
    // Content pane (adaptive)
    public object? CurrentContent { get; set; }
    public Stack<object> NavigationStack { get; }
    
    // Chat
    public bool IsChatPaneVisible { get; set; }
    public ObservableCollection<ChatMessage> Messages { get; }
}
```

---

## 9. Implementation Phases

| Phase | What | Proof |
|-------|------|-------|
| **U1** | Funnel layout shell: three columns, stacked Expanders, section headers with counts | Launch → see pipeline funnel with all sections, counts update on timer |
| **U2** | Sources + Library: account rows, category tree, document list, document detail with markdown preview | Click account → detail. Click category → list. Click document → preview. |
| **U3** | Processing stages: Intake, Extracting, Classifying with per-file rows. Classifying shows extracted preview + classify dropdown. | Drop PDF → appears in Intake → moves through stages → classify manually or wait → moves to Library |
| **U4** | Index + Action Items + Services sections | Coverage bars update. Reminders clickable. Service dots accurate. |
| **U5** | Cross-navigation + chat integration | Breadcrumbs, back button, document→thread→reminder linking. Chat results → content pane. |

---

## 10. Open Questions

| # | Question | Leaning |
|---|----------|---------|
| 1 | Should processing stages show individual files or just counts? | Both — count in header, file list when expanded. Collapsed by default when < 5 items. |
| 2 | Should Library support drag-and-drop between categories? | Future — dropdown first. |
| 3 | Should there be a search bar at the top of the funnel? | Yes — searches across all stages. Results in content pane. |
| 4 | Dark mode? | FluentTheme dark variant. Toggle in Settings. Not for v1. |
| 5 | Should threads be a separate funnel section? | No — threads are accessible via "Show Email Thread" on documents. Pipeline is document-centric. |
| 6 | What happens when pipeline is empty (fresh install)? | Each section shows a helpful message: Sources → "Add an email account to get started" |
