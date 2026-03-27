# Phase 8: Avalonia UI & Installer

**Status**: Not Started  
**Depends On**: Phase 7 (Background Service)  
**Deliverable**: Download `.dmg`/`.msi`, install, authenticate, done. Hermes runs silently forever.

---

## Objective

Build the Avalonia cross-platform UI (system tray + shell window), the first-run experience, the Ollama auto-install flow, and platform-specific installers.

---

## Tasks

### 8.1 — Avalonia System Tray
- [ ] System tray icon using Avalonia's `TrayIcon` API
- [ ] Tray icon states with visual indicator:
  - 🟢 Idle — all caught up
  - 🔵 Syncing — email sync in progress
  - 🟡 Processing — classifying / extracting / embedding
  - 🔴 Error — something needs attention
- [ ] Tray context menu:
  - **Status**: "Idle — 1,234 documents indexed" (not clickable, just info)
  - **Open Hermes** → opens shell window
  - **Open Archive Folder** → opens `~/Documents/Hermes/` in Finder/Explorer
  - **Pause / Resume** → toggles all background processing
  - **Quit** → graceful shutdown

### 8.2 — Shell Window: Status Dashboard
- [ ] Main tab showing live status:
  - Service status (running/paused)
  - Last sync time per account
  - Processing queue: X to classify, Y to extract, Z to embed
  - Document counts per category (summary table)
  - Disk usage
  - Ollama status (available/unavailable, model names)
- [ ] Auto-refreshes on a timer (every 5 seconds)

### 8.3 — Shell Window: Account Management
- [ ] Accounts tab:
  - List configured accounts with status (valid/expired/missing token)
  - "Add Account" button → opens browser for Gmail OAuth, stores token
  - "Re-authenticate" button → refresh expired tokens
  - "Remove Account" button → removes token and config entry (with confirmation)
  - Last sync time and message count per account

### 8.4 — Shell Window: Settings
- [ ] Settings tab:
  - Archive location (path picker)
  - Sync interval (minutes)
  - Min attachment size (KB)
  - Ollama settings: base URL, model names, enable/disable
  - Azure Document Intelligence: endpoint, key (masked)
  - Watched folders (list with add/remove)
  - Watched folder patterns per folder
- [ ] All settings persisted to `config.yaml`
- [ ] Changes take effect immediately (hot reload)

### 8.5 — First-Run Wizard
- [ ] On first launch (no `config.yaml` exists), show a guided setup flow:
  1. **Welcome**: "Hermes will organise your email attachments and documents. Let's get set up."
  2. **Archive Location**: picker with default `~/Documents/Hermes/`. Create the directory.
  3. **Email Accounts**: "Add your first Gmail account" → OAuth flow. Option to add more.
  4. **Watched Folders**: "Would you like Hermes to watch Downloads for new documents?" Toggle + picker.
  5. **Ollama**: Auto-detect GPU. If present: "Install Ollama for AI-powered search?" → triggers install. If not: "You can add an Azure Document Intelligence key later for OCR."
  6. **Done**: "Hermes is set up! It will sync your email every 15 minutes." → start service, minimise to tray.

### 8.6 — Ollama Auto-Install
- [ ] Detect GPU availability (check for NVIDIA/AMD GPU via system info)
- [ ] **macOS**:
  - Check for `brew`: `which brew`
  - If present: `brew install ollama`
  - If not: prompt user to install Homebrew first (link), or skip Ollama
- [ ] **Windows**:
  - Check for `winget`: `where winget`
  - If present: `winget install Ollama.Ollama --accept-package-agreements`
  - If not: prompt user to install from App Installer, or skip Ollama
- [ ] After Ollama installed: `ollama pull nomic-embed-text && ollama pull llava && ollama pull llama3.2:3b`
- [ ] Show progress in the wizard: "Downloading AI models... (this may take a few minutes)"
- [ ] If install fails: log error, continue without Ollama, notify user via settings panel

### 8.7 — macOS Installer (.dmg)
- [ ] `dotnet publish -c Release -r osx-arm64 --self-contained` (and `osx-x64`)
- [ ] Create `.app` bundle structure:
  ```
  Hermes.app/
  ├── Contents/
  │   ├── Info.plist
  │   ├── MacOS/
  │   │   └── Hermes          # published binary
  │   └── Resources/
  │       └── hermes.icns      # app icon
  ```
- [ ] Package as `.dmg` with drag-to-Applications layout (create-dmg tool)
- [ ] Codesign and notarise (for Gatekeeper) — requires Apple Developer account
- [ ] First launch: register LaunchAgent (auto-start on login)

### 8.8 — Windows Installer (.msi)
- [ ] `dotnet publish -c Release -r win-x64 --self-contained`
- [ ] WiX v4 project to create `.msi`:
  - Install to `%ProgramFiles%\Hermes\`
  - Add `hermes.exe` to PATH
  - Register Windows Service (or Task Scheduler task)
  - Create Start Menu shortcut
  - Create Desktop shortcut (optional)
- [ ] Application icon for the tray and shortcuts
- [ ] First launch: open the first-run wizard

### 8.9 — Update Mechanism
- [ ] On startup (and daily thereafter): check GitHub Releases API for newer version
- [ ] If update available: show notification via tray icon
- [ ] Tray menu item: "Update Available — v1.2.0" → opens download page in browser
- [ ] No auto-update in v1 — user downloads and installs manually

---

## Acceptance Criteria

- [ ] Tray icon appears on login, shows correct status
- [ ] Tray menu items all function correctly
- [ ] Shell window opens from tray, shows live status
- [ ] Accounts can be added, re-authenticated, and removed from the UI
- [ ] Settings changes are persisted and take effect immediately
- [ ] First-run wizard successfully guides a new user through setup
- [ ] Ollama is auto-installed (with user consent) if GPU is detected
- [ ] macOS `.dmg` installs cleanly, app launches, LaunchAgent registered
- [ ] Windows `.msi` installs cleanly, service registered, tray icon appears
- [ ] Update notification appears when a new version is available
- [ ] "Mum test": a non-technical user can install and set up Hermes without documentation
