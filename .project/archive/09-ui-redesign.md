# Hermes — UI Redesign: Shell Window & Chat Interface

> Design doc for improving the document intelligence window and chat experience.  
> Created: 2026-03-31

---

## 1. Current State Review

### What the CLI agent built (commits `ba607df` → `f833777`)

Phase 8 delivered a functional Avalonia app with:

| Component | Status | Notes |
|-----------|--------|-------|
| System tray icon + menu | ✅ Working | Open, Pause, Quit |
| First-run wizard (5 pages) | ✅ Working | Archive, accounts, watch folders, Ollama, done |
| Ollama auto-installer | ✅ Working | winget/brew detection, model pull |
| Shell window: left status panel | ✅ Working | Ollama, index, accounts, watch folders, controls |
| Shell window: right chat panel | ✅ Working | FTS5 search + optional Ollama summarisation |
| Settings dialog | ✅ Working | Modal window, saves to config.yaml |
| Gmail OAuth in-app | ✅ Working | Browser popup, token stored |
| Watch folder picker | ✅ Working | Native folder dialog, pattern config |
| Windows MSI installer | ✅ Working | WiX, PATH registration |
| Update checker | ✅ Working | GitHub Releases API |
| `Chat.fs` module | ✅ Working | Search → Ollama prompt → response |

### What's not great

**Architecture issues in `ShellWindow.axaml.cs`:**
- 620+ lines in one code-behind file — no ViewModels for anything except the tray
- Direct SQLite queries from the UI thread (RefreshIndexStats, RefreshAccountsList)
- Dialog windows constructed in code rather than AXAML
- No separation between "what to show" and "how to show it"
- `FindControl<T>` string lookups instead of compiled bindings

**Status panel (left side):**
- Dense monospace text dumps — hard to scan
- No visual hierarchy between sections
- Category summary is just a text block, no interaction
- Account status shows "✅" for all accounts regardless of actual state
- No processing queue visibility (classify/extract/embed counts)
- No disk usage display (specified in Phase 8.2, not implemented)

**Chat interface (right side):**
- Plain text bubbles with no visual distinction between user/assistant
- No markdown rendering (Ollama returns markdown that displays as raw text)
- No loading indicator while Ollama thinks (can be 10-30+ seconds)
- No conversation history persistence
- No document preview or click-to-open on results
- Hardcoded model name `"llama3:8b"` instead of reading from config
- Search results are text-only — no cards, no visual structure
- AI toggle is a small button, easy to miss

---

## 2. Proposed Improvements

### 2.1 — Shell Window Layout (structural)

Replace the current 2-column layout with a 3-zone layout:

```
┌─────────────────────────────────────────────────────────┐
│  Hermes — Document Intelligence                    ─ □ × │
├───────────┬─────────────────────────────────────────────┤
│           │                                             │
│  STATUS   │              MAIN AREA                      │
│  PANEL    │                                             │
│           │  (Chat is the default view)                 │
│  Ollama ● │                                             │
│  Index    │  ┌─────────────────────────────────┐        │
│  Accounts │  │  You: find my car insurance      │        │
│  Folders  │  │                                  │        │
│           │  │  Hermes:                         │        │
│  ─────── │  │  📄 Allianz-Policy-2025.pdf      │        │
│  Sync Now │  │     [insurance] 2025-01-15       │        │
│  Pause    │  │  📄 NRMA-Renewal-2024.pdf        │        │
│  ⚙       │  │     [insurance] 2024-07-22       │        │
│           │  │                                  │        │
│           │  │  AI: You have two car insurance   │        │
│           │  │  documents — current Allianz      │        │
│           │  │  policy from Jan 2025 and...      │        │
│           │  └─────────────────────────────────┘        │
│           │                                             │
│           │  ┌──────────────────────────┬──┬──┐         │
│           │  │ Ask Hermes...            │🧠│🔍│         │
│           │  └──────────────────────────┴──┴──┘         │
├───────────┴─────────────────────────────────────────────┤
│  ●● Ready · 1,234 docs · 842 extracted · DB 12.3 MB    │
└─────────────────────────────────────────────────────────┘
```

**Key changes:**
- Add a **status bar** at the bottom for at-a-glance metrics (moved from left panel)
- Left panel becomes **narrower, card-based** — each section is a collapsible card
- Main area keeps the chat but with proper message rendering

### 2.2 — Status Panel Improvements

Replace monospace text dumps with structured cards:

**Ollama card:**
```
🤖 Ollama                          ●  Available
   nomic-embed-text, llava, llama3
```
- Green/red dot instead of ✅/❌ text
- Model list in smaller secondary text

**Index card:**
```
📚 Index
   1,234 documents
   ████████░░ 842/1,234 extracted
   ███████░░░ 612/1,234 embedded
```
- Progress bars for extraction and embedding pipeline
- Show pending queue counts

**Accounts card:**
```
📧 john-personal            ● Synced
   423 emails · 2 min ago
📧 john-work                ● Synced
   187 emails · 14 min ago
   [+ Add Account]
```
- Per-account status dot (green = ok, yellow = stale, red = auth expired)
- Relative timestamps

**Watch Folders card:**
```
📁 ~/Downloads              *.pdf
📁 ~/Desktop                *.pdf, *.png
   [+ Add Folder]
```

### 2.3 — Chat Interface Improvements

**Message rendering:**
- **User messages**: right-aligned, accent-coloured background, rounded corners
- **Hermes messages**: left-aligned, subtle background, full width
- **Document result cards** (not text): clickable cards with icon, name, category badge, date
- **AI summary**: visually distinct block with a subtle "AI" badge
- **Loading state**: animated dots or spinner while waiting for search/Ollama
- **Empty state**: friendly illustration + suggested queries

**Document result cards:**
```
┌──────────────────────────────────────┐
│ 📄 Allianz-Policy-2025.pdf          │
│    insurance · 2025-01-15 · $1,234   │
│    "...comprehensive car insurance   │
│    policy for Toyota Camry..."       │
│                          [Open File] │
└──────────────────────────────────────┘
```
- Click card → open file in default app
- Category shown as a coloured badge/chip
- Extracted amount and date prominent
- Snippet shown in a subtle secondary style

**Input area improvements:**
- AI toggle should be a clearly labeled toggle switch, not a tiny button
- Add suggested query chips above the input when chat is empty:
  `[car insurance] [recent invoices] [tax documents 2025] [medical receipts]`
- Support Enter to send, Shift+Enter for newline
- Show character/context feedback when AI is enabled

**Conversation features:**
- Persist conversation in memory during session (already in the StackPanel, but no scroll-to-latest)
- "New conversation" button to clear
- Copy button on Hermes responses

### 2.4 — Status Bar (new)

Bottom bar showing at-a-glance system health:

```
●● Ready · 1,234 docs · 842 extracted · 612 embedded · DB 12.3 MB · Last sync 2m ago
```

States: Ready (green), Syncing (blue pulse), Processing (yellow), Error (red)

This replaces the dense stats currently in the left panel, freeing it for cards.

### 2.5 — Settings Dialog (expanded modal)

The settings dialog grows from 3 fields to a sectioned form. Stays as a modal (simpler than drawer). See [11-email-backfill.md](11-email-backfill.md) section 9.1 for the full specification.

Sections:
- **General**: sync interval, min attachment size
- **AI / Chat**: provider radio (Ollama / Azure OpenAI), model config, endpoint + masked API key
- **Accounts**: list with per-account backfill config, re-auth, remove
- **Watch Folders**: list with remove, add

All fields must save to `config.yaml` via `HermesServiceBridge.UpdateConfigAsync`.

### 2.6 — Main Area Tabs: Chat + Action Items

The main area gains a tab bar at the top:
- **Chat** (default) — search and conversation
- **Action Items** — bills, reminders, and future skill outputs

See [12-bills-and-reminders.md](12-bills-and-reminders.md) section 7 for the full TODO panel specification.

The left sidebar shows an ACTION ITEMS badge with overdue/upcoming counts.

---

## 3. Architecture Improvements

### 3.1 — Extract ViewModels

The current `ShellWindow.axaml.cs` does everything. Extract:

| ViewModel | Responsibility |
|-----------|---------------|
| `ShellViewModel` | Overall window state, timer, navigation |
| `StatusPanelViewModel` | Ollama, index, accounts, folders — refreshed on timer |
| `ChatViewModel` | Conversation history, send, AI toggle state |
| `ChatMessageViewModel` | Individual message rendering (user vs Hermes) |
| `DocumentResultViewModel` | Single search result → card display |

This is not about dogmatic MVVM — it's about getting SQLite queries out of the code-behind and making the chat testable.

### 3.2 — Move DB reads to Core

`ShellWindow.axaml.cs` currently opens raw SQLite connections to read stats. These should be F# functions in Core (e.g., `Database.getDocumentStats`, `Database.getAccountStats`) that the bridge calls, keeping the UI layer thin.

### 3.3 — Use compiled bindings

Replace `FindControl<T>("name")` with Avalonia compiled bindings where possible. This catches binding errors at compile time and is faster at runtime.

---

## 4. Implementation Phases

### Phase A: Architecture cleanup (no visual changes)
- Extract ViewModels
- Move DB queries from code-behind to `HermesServiceBridge` / Core
- Wire up with compiled bindings
- No user-visible changes, but makes Phase B clean

### Phase B: Status panel cards + status bar
- Replace text dumps with card-style layout
- Add bottom status bar
- Add progress indicators for extraction/embedding pipeline

### Phase C: Chat interface overhaul
- Styled message bubbles (user vs Hermes)
- Document result cards with click-to-open
- Loading indicator
- AI toggle redesign
- Suggested query chips
- Read model name from config instead of hardcoding

### Phase D: Polish & Aura-inspired refinements

Inspired by the Aura VS Code extension sidebar (collapsible sections, per-service health rows, resizable panel).

**Resizable panel**
- Add a `GridSplitter` between the left status panel and the right chat area
- Users can drag to widen/narrow the status panel (min 200, max 400, default 260)
- Persist last width in `config.yaml` under `ui.statusPanelWidth`

**Collapsible sections (Expander pattern)**
- Replace fixed StackPanel sections with Avalonia `Expander` controls
- Each section (Services, Index, Categories, Accounts, Folders) gets a chevron toggle
- Persist collapsed/expanded state per section in config
- Match Aura's density: section header + compact child rows

**Per-service health rows**
- New "Services" section at the top of the status panel, replacing the single Ollama dot
- Each row: green/yellow/red dot + service name + summary text
  - `● Ollama  3 models` (expandable → list loaded models)
  - `● Database  1,234 docs · 12.3 MB`
  - `● MCP Server  localhost:21740 · 5 tools` (expandable → tool list with status)
  - `● Pipeline  idle` / `● Pipeline  extracting 3...`
- Service health checked on the same refresh timer as stats

**MCP Server detail (Aura-inspired)**
- When expanded, shows each registered tool with connection/ready status:
  ```
  ▾ ● MCP Server  localhost:21740 · 5 tools
      🔧 hermes_search        Ready
      🔧 hermes_get_document   Ready
      🔧 hermes_list_categories Ready
      🔧 hermes_stats          Ready
      🔧 hermes_read_file      Ready
  ```
- Status sourced from `McpServer.toolDefinitions` (static list) + HTTP health check on the listener
- If the MCP listener isn't running: `● MCP Server  not started` (red dot)
- If listener is up but a tool call fails: individual tool shows yellow dot
- This gives AI agent operators (the primary MCP consumers) at-a-glance confidence that the server is reachable and tools are registered

**Remaining polish**
- Empty states: friendly illustration + message when no documents indexed yet
- Keyboard shortcuts: `Enter` to send, `Shift+Enter` for newline, `Ctrl+L` to clear chat
- Copy button on Hermes responses (clipboard icon, top-right of bubble)
- "New conversation" button to clear the chat panel
- Session conversation persistence (in-memory across window close/reopen within same process)
- Settings drawer (stretch goal — modal works fine for now)

---

## 5. Open Questions

| # | Question | Leaning |
|---|----------|---------|
| 1 | Should we use ReactiveUI (already referenced in .csproj) or plain MVVM? | ReactiveUI is already a dependency — lean into it |
| 2 | Should document cards open the file directly or show a preview panel? | Open directly in v1; preview panel is Future Enhancement |
| 3 | Should chat history persist across window close/reopen? | Yes, in-memory for the session; not persisted to disk |
| 4 | Should the AI toggle default to on or off? | On if Ollama is detected available; off otherwise |
| 5 | Worth adding a "Browse" tab alongside Chat in the main area? | Possible — but chat-first is the right default |
