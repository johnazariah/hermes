# Phase 8: Avalonia UI & Installer

**Status**: Done (functional) — UI polish tracked in [09-ui-redesign.md](../design/09-ui-redesign.md)  
**Depends On**: Phase 7 (Background Service)  
**Deliverable**: Download `.dmg`/`.msi`, install, authenticate, done. Hermes runs silently forever.

---

## Objective

Build the Avalonia cross-platform UI (system tray + shell window), the first-run experience, the Ollama auto-install flow, and platform-specific installers.

---

## Tasks

### 8.1 — Avalonia System Tray
- [x] System tray icon using Avalonia's `TrayIcon` API
- [ ] Tray icon states with visual indicator:
  - 🟢 Idle — all caught up
  - 🔵 Syncing — email sync in progress
  - 🟡 Processing — classifying / extracting / embedding
  - 🔴 Error — something needs attention
  - **Status**: Tray icon works, but no dynamic state icons — always shows default
- [x] Tray context menu:
  - **Status**: "Idle — 1,234 documents indexed" (not clickable, just info)
  - **Open Hermes** → opens shell window
  - **Open Archive Folder** → opens `~/Documents/Hermes/` in Finder/Explorer
  - **Pause / Resume** → toggles all background processing
  - **Quit** → graceful shutdown

### 8.2 — Shell Window: Status Dashboard
- [x] Left panel showing live status:
  - [x] Ollama status (available/unavailable, model names)
  - [x] Document counts and index stats (docs, extracted, embedded, DB size)
  - [x] Category summary (directory scan)
  - [x] Account list with email counts and last sync time
  - [x] Watch folders list
  - [x] Last sync time
  - [ ] Processing queue: X to classify, Y to extract, Z to embed — **not implemented**
  - [ ] Disk usage (total archive) — **not implemented** (only DB size shown)
- [x] Auto-refreshes on a timer (every 5 seconds)
- **Issues**: All status data is plain text dumps; DB queries run from UI code-behind

### 8.3 — Shell Window: Account Management
- [x] Account list in left panel with email count and last sync time
- [x] "Add Account" button → opens browser for Gmail OAuth, stores token
- [ ] Token status display (valid/expired/missing) — **not implemented** (shows ✅ for all)
- [ ] "Re-authenticate" button — **not implemented**
- [ ] "Remove Account" button — **not implemented**

### 8.4 — Shell Window: Settings
- [x] Settings modal dialog with:
  - [x] Archive location (display only — no picker in settings, picker in wizard only)
  - [x] Sync interval (minutes)
  - [x] Min attachment size (KB)
  - [x] Ollama base URL
  - [ ] Ollama model names — **not configurable in settings**
  - [ ] Azure Document Intelligence endpoint/key — **not implemented**
  - [x] Watched folders (Add Folder button with pattern config)
  - [ ] Remove watched folder — **not implemented**
- [x] Settings persisted to `config.yaml`
- [x] Changes take effect on save (config reload)

### 8.5 — First-Run Wizard
- [x] On first launch (no `config.yaml` exists), show a guided setup flow:
  1. [x] **Welcome** page
  2. [x] **Archive Location** with path picker and default
  3. [x] **Email Accounts** with Gmail OAuth flow
  4. [x] **Watched Folders** with Downloads/Desktop checkboxes
  5. [x] **Ollama** with GPU detection and install option
  6. [x] **Done** summary and start

### 8.6 — Ollama Auto-Install
- [x] Detect GPU availability
- [x] **Windows**: `winget install Ollama.Ollama`
- [x] **macOS**: `brew install ollama`
- [x] After install: pull models (`nomic-embed-text`, `llava`, `llama3.2:3b`)
- [x] Progress shown in wizard
- [x] Graceful failure — continue without Ollama

### 8.7 — macOS Installer (.dmg)
- [ ] `.app` bundle structure — **not implemented**
- [ ] `.dmg` packaging — **not implemented**
- [ ] Codesign and notarise — **not implemented**
- [ ] LaunchAgent registration — **not implemented**

### 8.8 — Windows Installer (.msi)
- [x] `dotnet publish -c Release -r win-x64 --self-contained`
- [x] WiX v4 project to create `.msi`
- [x] Install to `%ProgramFiles%\Hermes\`
- [x] Add `hermes.exe` to PATH
- [ ] Register Windows Service (or Task Scheduler task) — **deferred: tray app auto-start via registry Run key is sufficient**
- [x] Create Start Menu shortcut — **done in Package.wxs (HermesMenuFolder)**
- [ ] Create Desktop shortcut — **not implemented**
- [x] Application icon

### 8.9 — Update Mechanism
- [x] On startup: check GitHub Releases API for newer version
- [x] If update available: show in tray menu
- [x] Tray menu item opens download page in browser
- [x] No auto-update in v1

### 8.10 — Chat Interface (added during implementation)
- [x] Chat panel in right side of shell window
- [x] Text input with Enter-to-send
- [x] AI toggle button for Ollama-enhanced answers
- [x] `Chat.fs` module: FTS5 search → optional Ollama summarisation
- [x] FTS5 snippet markers (`>>>` / `<<<`) leak into displayed text — **fixed: empty string markers**
- [x] Hardcoded model name `"llama3:8b"` instead of config — **fixed: reads from config**
- [ ] No loading indicator during search/AI — **missing** (Phase D polish)
- [x] Visual distinction between user and Hermes messages — **done: styled bubbles**
- [x] Results displayed as structured document cards — **done: cards with category badge, date, amount**
- [x] Singleton guard (one instance only)

---

## Acceptance Criteria

- [x] Tray icon appears on login, shows correct status
- [x] Tray menu items all function correctly
- [x] Shell window opens from tray, shows live status
- [x] Accounts can be added from the UI
- [ ] Accounts can be re-authenticated and removed from the UI — **specced in implementation plan (Task 3)**
- [x] Settings changes are persisted and take effect
- [x] First-run wizard successfully guides a new user through setup
- [x] Ollama is auto-installed (with user consent) if GPU is detected
- [ ] macOS `.dmg` installs cleanly — **not done**
- [x] Windows `.msi` installs cleanly, tray icon appears
- [x] Update notification appears when a new version is available
- [ ] "Mum test" — **mostly done**: wizard works, chat renders styled results, document cards clickable. Remaining: loading indicator, account management
