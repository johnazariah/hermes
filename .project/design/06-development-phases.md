# Hermes — Development Phases

## Technology Stack

| Component | Choice | Notes |
|-----------|--------|-------|
| **Runtime** | .NET 9 | Self-contained single binary, cross-platform |
| **Language** | F# (core logic), C# (Avalonia UI) | F# pipelines for data flow, C# for XAML bindings |
| **UI** | Avalonia | Cross-platform tray icon + shell window |
| **Database** | SQLite via `Microsoft.Data.Sqlite` | FTS5 + sqlite-vec native extensions |
| **Email** | Google.Apis.Gmail.v1 NuGet | Gmail API with OAuth2 |
| **PDF** | PdfPig | .NET-native PDF text extraction |
| **Embeddings** | Ollama REST API / ONNX Runtime fallback | `nomic-embed-text` (768d) via Ollama, bundled ONNX model for CPU fallback |
| **OCR** | Ollama `llava` / Azure Document Intelligence | Vision model for scanned docs, Azure as no-GPU fallback |
| **MCP** | Streamable HTTP on localhost | Thin stdio shim for clients that need it |
| **Config** | YAML (`YamlDotNet`) | `config.yaml` + `rules.yaml` |
| **FS watching** | `System.IO.FileSystemWatcher` | Cross-platform, native OS events |
| **Pipeline** | `System.Threading.Channels` | Bounded async producer-consumer queues |
| **Hosting** | `Microsoft.Extensions.Hosting` | `BackgroundService` for long-running tasks |

---

## Phase 0: Project Skeleton

**Goal**: Buildable, testable project structure.

- [ ] Solution structure: `Hermes.Core` (F# lib), `Hermes.App` (Avalonia entry point), `Hermes.Cli` (CLI shim), `Hermes.Tests`
- [ ] `Microsoft.Extensions.Hosting` setup with DI and configuration
- [ ] Config loading (`config.yaml`, `rules.yaml`) via `YamlDotNet` with sensible defaults
- [ ] Cross-platform path handling (`Environment.SpecialFolder` / `platformdirs` equivalent)
- [ ] SQLite database initialisation (create tables, FTS5, indexes)
- [ ] Structured logging via `Microsoft.Extensions.Logging` (file + console, Serilog sink)
- [ ] Basic test harness (xUnit + FsCheck for property tests)
- [ ] CI: `dotnet build` + `dotnet test` on macOS and Windows

**Deliverable**: `hermes --version` works. `hermes init` creates config and database.

---

## Phase 1: Email Sync → `unclassified/`

**Goal**: Connect to Gmail, download attachments, drop them into `unclassified/`.

- [ ] Gmail OAuth2 flow (`hermes auth LABEL` — opens browser, stores token per account)
- [ ] `IEmailProvider` abstraction (Gmail implements it; IMAP/Graph slot in later)
- [ ] Incremental message listing (Gmail history API or date-based)
- [ ] Attachment download with size filtering (skip < 20KB)
- [ ] Standardised filename: `{date}_{sender_short}_{original_name}.{ext}`
- [ ] Write to `unclassified/` folder
- [ ] Record provenance in `messages` table (metadata only — no body text in v1)
- [ ] Store `thread_id` for future use
- [ ] SHA256 computation on download
- [ ] Rate limiting (1s sleep per 45 calls, exponential backoff on 429)
- [ ] Interrupt-safe: track progress in `sync_state` table

**Deferred**: Email body text extraction and indexing — future phase for unified search.

**Deliverable**: `hermes sync` downloads new attachments to `unclassified/`. `hermes accounts` shows sync status.

---

## Phase 2: Classification Pipeline

**Goal**: Watch `unclassified/`, classify files, move to category folders.

- [ ] `FileSystemWatcher` on `unclassified/` directory
- [ ] `Channel<FileEvent>` feeds the classifier task
- [ ] Rules engine: load `rules.yaml`, evaluate cascade (domain → filename → subject → fallback)
- [ ] SHA256 dedup check before classifying
- [ ] Move file from `unclassified/` to `{category}/{filename}`
- [ ] Insert `documents` row with category and metadata
- [ ] Post `DocumentId` to extraction channel
- [ ] `hermes reconcile` — walk archive, detect user moves, update DB categories
- [ ] `hermes suggest-rules` — analyse overrides, propose new rules
- [ ] Default category folder creation on init

**Deliverable**: Files auto-classify within seconds of appearing in `unclassified/`. User can correct by moving files and running `reconcile`.

---

## Phase 3: Text Extraction

**Goal**: Extract text and structured fields from PDFs and images.

- [ ] Native PDF text extraction via PdfPig
- [ ] Ollama vision model integration for scanned/image PDFs (`llava`)
- [ ] Azure Document Intelligence as cloud OCR fallback (configurable API key)
- [ ] Structured field parsing: date, amount, vendor, ABN (regex heuristics)
- [ ] Ollama instruct model (`llama3.2:3b`) for complex field extraction (optional)
- [ ] Update `documents` row with extracted fields
- [ ] `Channel<DocumentId>` from classifier feeds this stage
- [ ] Post to embed channel on success
- [ ] `hermes extract` CLI command with `--category`, `--force`, `--limit`

**Deliverable**: Classified documents get text extracted automatically. Extracted text and fields visible in DB.

---

## Phase 4: Full-Text Search

**Goal**: Keyword search across all indexed content.

- [ ] FTS5 index population (via triggers — already in schema)
- [ ] `hermes search QUERY` — keyword search with metadata filters
- [ ] Result formatting: table with date, category, filename, sender, relevance
- [ ] Snippet/highlight generation from FTS5

**Deliverable**: `hermes search "CBA statement 2025"` returns results instantly.

---

## Phase 5: Embeddings & Semantic Search

**Goal**: Vector similarity search via Ollama or ONNX embeddings.

- [ ] Ollama REST client for embedding generation (`nomic-embed-text`, 768d)
- [ ] ONNX Runtime fallback with bundled model for machines without Ollama
- [ ] Text chunking (~500 chars, 100 overlap)
- [ ] sqlite-vec native extension loading and `vec_chunks` population
- [ ] `Channel<DocumentId>` from extractor feeds this stage
- [ ] `hermes embed` CLI command
- [ ] `hermes search --semantic QUERY` — convert query to embedding, search vec_chunks
- [ ] Hybrid search: semantic results filtered by metadata (category, date range)

**Deliverable**: `hermes search --semantic "plumbing invoice for the rental"` finds relevant documents even without exact keyword matches.

---

## Phase 6: MCP Server

**Goal**: Expose the archive to AI agents via MCP protocol.

- [ ] Streamable HTTP MCP server on `localhost:{port}` (integrated into the single process)
- [ ] Stdio shim (`hermes mcp`) that proxies stdin/stdout ↔ HTTP for compatible clients
- [ ] `hermes_search` tool (keyword + semantic + hybrid — unified across all content)
- [ ] `hermes_get_document` tool
- [ ] `hermes_get_email` tool (provenance lookup)
- [ ] `hermes_list_categories` tool
- [ ] `hermes_stats` tool
- [ ] `hermes_list_accounts` tool
- [ ] `hermes_read_file` tool (with path sandboxing)
- [ ] Claude Desktop / VS Code MCP config examples

**Deliverable**: Add Hermes to Claude's MCP config, ask "find my electricity bills from last quarter" and get answers.

---

## Phase 7: Background Service

**Goal**: Hermes runs quietly, survives reboots.

- [ ] `BackgroundService` host orchestrates all tasks (sync, classify, extract, embed, MCP)
- [ ] macOS: launchd LaunchAgent plist, `hermes service install/uninstall`
- [ ] Windows: Windows Service registration or Task Scheduler, `hermes service install/uninstall`
- [ ] `hermes service start|stop|status`
- [ ] Configurable sync interval (default 15 min)
- [ ] Graceful shutdown on SIGTERM / service stop
- [ ] Auto-restart on crash (launchd `KeepAlive` / Windows Service recovery)

**Deliverable**: `hermes service install` → Hermes starts on login and runs forever.

---

## Phase 8: Avalonia UI & Installer

**Goal**: Mum-friendly packaging.

- [ ] Avalonia system tray icon: status (idle/syncing/processing/error), counts
- [ ] Tray menu: open settings, open archive folder, pause/resume, quit
- [ ] Shell window (opens from tray): settings, account management, status dashboard
- [ ] First-run experience: authenticate Gmail accounts (opens browser for OAuth), pick archive folder, configure watched folders
- [ ] macOS: `.dmg` bundle (`dotnet publish` self-contained + `create-dmg`)
- [ ] Windows: `.msi` installer (`dotnet publish` self-contained + WiX)
- [ ] Ollama auto-install: detect GPU → `brew install ollama` / `winget install Ollama.Ollama` → pull models
- [ ] Update check mechanism (GitHub releases or similar)

**Deliverable**: Download `.dmg`/`.msi`, install, authenticate, done. Hermes runs silently forever.

---

## Phase 9: Folder Watching (Extension)

**Goal**: Watch `~/Downloads` and other folders for documents.

- [ ] Configurable watched folders with glob patterns (in config.yaml and settings UI)
- [ ] `FileSystemWatcher` per watched folder
- [ ] Copy matching files to `unclassified/` with source metadata (`source_type = watched_folder`)
- [ ] Dedup: don't re-copy files already in the archive (SHA256 check)
- [ ] `hermes watch add/list/remove` CLI commands
- [ ] Integration with Avalonia settings panel

**Deliverable**: Save a bank statement PDF to `~/Downloads` → automatically classified and indexed within seconds.

---

## Phase Summary

| Phase | Name | Depends On | User Value |
|-------|------|-----------|------------|
| 0 | Project skeleton | — | Buildable project |
| 1 | Email sync | 0 | Attachments downloaded from Gmail |
| 2 | Classification | 0 | Documents auto-sorted into folders |
| 3 | Text extraction | 2 | Document text and fields parseable |
| 4 | Full-text search | 3 | Keyword search across everything |
| 5 | Embeddings | 3 | Semantic/natural language search |
| 6 | MCP server | 4, 5 | AI agents can query the archive |
| 7 | Background service | 1, 2, 3 | Runs unattended, survives reboots |
| 8 | Avalonia UI & installer | 7 | Non-technical users can install and use |
| 9 | Folder watching | 2 | Local files (Downloads etc.) also indexed |

Phases 1–6 can be developed and used via CLI. Phases 7–8 make it mum-friendly. Phase 9 extends the intake pipeline.

### Future Phase: Email Body Indexing + Unified Search

When ready, add email body text to the `messages` table and feed it into the same FTS5 + embedding pipeline. `hermes_search` becomes truly unified — "find me everything related to X" returns documents, email bodies, and any future content types from one query. No new MCP tools needed; the existing search surface just gets richer.

### Future: Built-in Chat Window

An Avalonia chat panel could provide a native conversational interface to the Hermes index — ask questions directly without needing Claude or another external AI tool. This would use the same search/retrieval pipeline that the MCP server exposes.
