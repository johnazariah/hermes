# Hermes — Functional Requirements

## FR1: Email Sync (Producer)

### FR1.1 Multiple Accounts
- Connect to multiple Gmail accounts via OAuth2.
- One shared `credentials.json` (OAuth client), separate per-account token files.
- `gmail.readonly` scope only — Hermes never modifies email.
- Account config in `~/.config/hermes/config.yaml` (macOS) / `%APPDATA%\hermes\config.yaml` (Windows).

### FR1.2 Incremental Sync
- Track which messages have been processed per account (by Gmail message ID).
- On each run, fetch only new messages since last sync.
- Support `--full` resync that re-scans all messages but skips already-downloaded files (by SHA256).
- Safe to interrupt — restart resumes from last completed message.

### FR1.3 Attachment Download
- Download attachments from synced emails.
- Skip inline images below configurable threshold (default 20KB) — logos, signatures, tracking pixels.
- Default mime types: PDF, PNG, JPEG, TIFF. Configurable to include .docx, .xlsx, .csv, .zip.
- Write each attachment to the **`unclassified/`** folder with a standardised filename: `{date}_{sender_short}_{original_name}.{ext}`
- Record provenance metadata (account, gmail_id, sender, subject, date) alongside the file.

### FR1.4 Email Metadata Storage
- Store email metadata (sender, subject, date, labels) in the `messages` table for provenance and filtering.
- Email body text is **not indexed in v1** — emails are tracked as provenance for their attachments.
- Future: index email bodies so "who did we invite to the birthday party" queries work across all content.

### FR1.5 Rate Limiting
- Gmail API: sleep 1s every 45 API calls. Exponential backoff on 429.

---

## FR2: Folder Watcher (Universal Intake)

### FR2.1 Watch `unclassified/`
- A background thread continuously watches the `unclassified/` folder for new files.
- This is the universal intake point. All producers (email sync, folder watchers, manual drops) deposit files here.

### FR2.2 Watch Additional Folders (Extension)
- Optionally watch user-configured folders (e.g. `~/Downloads`, `~/Desktop`).
- Files matching configured patterns (e.g. `*.pdf`, `*statement*.pdf`) are **copied** (not moved) into `unclassified/` with source metadata.
- Configurable per-folder rules for what to pick up.

### FR2.3 Processing Pipeline
When a new file appears in `unclassified/`:

1. **Dedup**: SHA256 hash. If already in the archive, link the DB row to the existing file; skip re-download.
2. **Classify**: Run rules cascade (see FR3) to determine category. Move file from `unclassified/` to `{category}/`.
3. **Queue for extraction**: Mark the file as needing text extraction + embedding.

### FR2.4 File System Events
- Use OS-native file watching: `FSEvents` (macOS), `ReadDirectoryChangesW` (Windows).
- Fallback: periodic polling (every 30s) if native events unavailable.

---

## FR3: Classification

### FR3.1 Rules Cascade (First Match Wins)

Three signals checked in priority order:

| Priority | Signal | Source | Reliability |
|----------|--------|--------|-------------|
| 1 | Sender domain | Email metadata | Highest — zero ambiguity |
| 2 | Filename pattern | Regex on filename | High |
| 3 | Subject pattern | Regex on email subject | Medium |
| 4 | Fallback | — | `unsorted/` within the category tree |

Rules defined in `~/.config/hermes/rules.yaml`, user-editable.

### FR3.2 Default Category Folders

```
bank-statements/
insurance/
invoices/
legal/
medical/
donations/
payslips/
property/               ← user adds subcategories: property/manorwoods/
rates-and-tax/
receipts/
subscriptions/
tax/
utilities/
unsorted/               ← classified as "unsorted" — still in the archive
```

### FR3.3 Folders Are Truth
- The filesystem is the authoritative category. Users correct classification by **moving files**.
- `reconcile` walks the archive directory, detects moves/renames/deletes, and updates the DB.
- No "reclassify" command that overwrites user decisions.

### FR3.4 Rule Learning
- `suggest-rules` analyses user overrides (files moved out of `unsorted/` or between categories).
- Proposes new rules for the user to accept/reject.
- Never auto-applies rules — always asks.

---

## FR4: Text Extraction (Consumer)

### FR4.1 PDF Text Extraction
- Extract native text from PDFs (pdfplumber or equivalent).
- For scanned/image-only PDFs, OCR via Tesseract or Ollama vision model.

### FR4.2 Structured Field Parsing
- Parse common fields from extracted text: date, amount, vendor/payee, ABN/ACN.
- Use heuristics first (regex patterns for dates, currency, ABN format).
- Optionally use Ollama for complex documents (invoices with non-standard layouts).

### FR4.3 Ollama Integration
- Use Ollama for:
  - Text extraction from complex/scanned documents (vision models).
  - Structured field parsing ("extract the date, amount, and vendor from this text").
  - Classification assistance for ambiguous documents.
- Graceful degradation: if Ollama is unavailable, fall back to heuristics-only.
- Configurable model name (default: a small, fast model suitable for the task).

### FR4.4 Incremental
- Only process files that haven't been extracted yet.
- `--force` flag to re-extract.
- Rate-limited to avoid overwhelming the GPU.

---

## FR5: Indexing

### FR5.1 Full-Text Search (FTS5)
- SQLite FTS5 index covering: sender, subject, filename, category, extracted text, vendor.
- Kept in sync via triggers on insert/update.
- Future: add email body text to create a truly unified "find me everything about X" index.

### FR5.2 Semantic Search (Embeddings)
- Generate vector embeddings for extracted text using Ollama embedding models.
- Store in sqlite-vec (384 or 768 dimensions depending on model).
- Chunking: ~500 chars with 100-char overlap. Typical document = 1–5 chunks.
- Embeddings generated locally — no API calls, no data leaving the machine.

### FR5.3 Combined Search
- Support keyword search (FTS5), semantic search (vector similarity), and combined (semantic within a category/date range).

---

## FR6: Background Service & UI

### FR6.1 Single Process
- One .NET process hosts everything: background tasks, MCP server, Avalonia UI.
- macOS: launchd user agent (LaunchAgent plist). Starts on login, restarts on crash.
- Windows: Windows Service or Task Scheduler task. Starts on boot, restarts on crash.

### FR6.2 Avalonia Shell
- System tray icon showing status: Idle / Syncing / Processing / Error.
- Tray menu: open settings, open archive folder, pause/resume, quit.
- Shell window (opens from tray): settings panel, account management, status dashboard, sync history.
- Future: integrated chat window for querying the index directly.

### FR6.3 Scheduling
- Email sync: configurable interval (default: every 15 minutes).
- Folder watching: real-time (filesystem events) + periodic sweep.
- Extraction/embedding: continuous, processing the queue as files arrive via `Channel<T>`.

### FR6.4 Resource Awareness
- Detect Ollama availability on startup. If not running, skip AI-powered features gracefully.
- Respect system idle — optionally defer heavy work (OCR, embeddings) until machine is idle.

---

## FR7: MCP Server

See [05-mcp-server-design.md](05-mcp-server-design.md) for full tool/resource definitions.

- Integrated into the Hermes service process (single process).
- Streamable HTTP on `localhost:{port}` — AI tools connect directly.
- Stdio shim (`hermes mcp`) for MCP clients that only support stdio transport.
- Read-only — MCP cannot modify the archive.

---

## FR8: Installer & Packaging

### FR8.1 Self-Contained
- .NET 10 self-contained single binary. No runtime prerequisites.
- macOS: `.dmg` app bundle. Registers LaunchAgent on first run.
- Windows: `.msi` installer (WiX). Registers service/scheduled task on install.

### FR8.2 Ollama Auto-Install
- Installer detects GPU availability.
- If GPU present: install Ollama via `brew install ollama` (macOS) / `winget install Ollama.Ollama` (Windows), verifying package managers exist first.
- Pull default models: `nomic-embed-text`, `llava`, `llama3.2:3b`.
- If no GPU: skip Ollama, configure Azure Document Intelligence as fallback (user provides key in settings).

### FR8.3 First-Run Experience
- On first launch, Avalonia shell window opens with setup wizard:
  1. Authenticate email account(s) — opens browser for OAuth.
  2. Choose archive location (default: `~/Documents/Hermes/`).
  3. Optionally configure watched folders.
  4. Ollama status check — show what's available.
  5. Done — Hermes starts syncing.

### FR8.4 Updates
- Check for updates on a configurable schedule. Notify via tray icon. User chooses when to update.

---

## FR9: CLI (Power Users)

Full CLI available alongside the background service for scripting and direct control.

```
hermes sync [--account ACCT] [--since DATE] [--full] [--dry-run]
hermes extract [--category CAT] [--force] [--limit N]
hermes embed [--force] [--limit N]
hermes search QUERY [--category CAT] [--from DATE] [--to DATE] [--semantic]
hermes reconcile [--dry-run]
hermes stats [--account ACCT]
hermes accounts
hermes auth LABEL
hermes suggest-rules
hermes watch add PATH [--patterns "*.pdf"]
hermes watch list
hermes watch remove PATH
hermes service start|stop|status|install|uninstall
hermes mcp                          # stdio shim → localhost HTTP
```
