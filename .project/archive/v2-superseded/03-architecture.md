# Hermes — Architecture Overview

## High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                     Hermes Process (.NET 10 / F#)                │
│                                                                  │
│  ┌───────────────────── Background Tasks ──────────────────────┐ │
│  │                                                              │ │
│  │  ┌─────────┐  ┌──────────┐  ┌───────────┐                  │ │
│  │  │ Email   │  │ Folder   │  │ (Future   │   Producers       │ │
│  │  │ Sync    │  │ Watchers │  │ Providers)│                   │ │
│  │  └────┬────┘  └────┬─────┘  └─────┬─────┘                  │ │
│  │       │            │              │                          │ │
│  │       ▼            ▼              ▼                          │ │
│  │  ┌─────────────────────────────────────┐                    │ │
│  │  │       unclassified/ folder          │                    │ │
│  │  │    (universal intake queue)         │                    │ │
│  │  └──────────────┬──────────────────────┘                    │ │
│  │                 │                                            │ │
│  │                 ▼                                            │ │
│  │  ┌─────────────────────────────────────┐                    │ │
│  │  │    Classifier (Channel<T>)          │                    │ │
│  │  │  dedup → rules cascade → move file  │                    │ │
│  │  └──────────────┬──────────────────────┘                    │ │
│  │                 │                                            │ │
│  │                 ▼                                            │ │
│  │  ┌─────────────────────────────────────┐                    │ │
│  │  │    Extractor (Channel<T>)           │                    │ │
│  │  │  PDF text → OCR → parse fields      │                    │ │
│  │  │  (Ollama or Azure Doc Intelligence) │                    │ │
│  │  └──────────────┬──────────────────────┘                    │ │
│  │                 │                                            │ │
│  │                 ▼                                            │ │
│  │  ┌─────────────────────────────────────┐                    │ │
│  │  │    Embedder (Channel<T>)            │                    │ │
│  │  │  chunk → Ollama/ONNX embeddings     │                    │ │
│  │  │  → write to sqlite-vec              │                    │ │
│  │  └─────────────────────────────────────┘                    │ │
│  └──────────────────────────────────────────────────────────────┘ │
│                                                                    │
│  ┌────────────────────────────────────────────────────────────┐   │
│  │                    db.sqlite                                │   │
│  │   ┌──────────┬───────────┬──────────────────────┐          │   │
│  │   │ tables   │ FTS5      │ sqlite-vec (embeds)  │          │   │
│  │   └──────────┴───────────┴──────────────────────┘          │   │
│  └────────────────────────────────────────────────────────────┘   │
│                          │                                        │
│  ┌───────────────────────┴────────────────────────────────────┐   │
│  │        MCP Server (Streamable HTTP on localhost)            │   │
│  │   search • filter • retrieve • stats                        │   │
│  │   + stdio shim for clients that need it                     │   │
│  └────────────────────────────────────────────────────────────┘   │
│                                                                    │
│  ┌────────────────────────────────────────────────────────────┐   │
│  │           Avalonia UI                                       │   │
│  │   System tray icon + shell window (settings/status)         │   │
│  │   Future: built-in chat interface                           │   │
│  └────────────────────────────────────────────────────────────┘   │
└────────────────────────────────────────────────────────────────────┘

External:
  ┌──────────┐   ┌──────────┐   ┌──────────────┐   ┌─────────────┐
  │ Gmail    │   │ Ollama   │   │ AI Tools     │   │ Azure Doc   │
  │ API      │   │ (local)  │   │ (via MCP)    │   │ Intelligence│
  │ (OAuth2) │   │ GPU      │   │ Claude, etc. │   │ (fallback)  │
  └──────────┘   └──────────┘   └──────────────┘   └─────────────┘
```

## Key Architectural Decisions

### 1. Single Process — Service + MCP Server + UI

Hermes is **one process** that hosts everything:
- **Background tasks**: email sync, folder watching, classification, extraction, embedding.
- **MCP server**: streamable HTTP on `localhost:{port}`. AI tools connect directly. A thin `hermes mcp` CLI shim does stdio↔HTTP proxying for MCP clients that only support stdio.
- **Avalonia UI**: system tray icon + a shell window for settings, status, and first-run configuration. Future: built-in chat window.

One process means: one DB connection, no IPC, no coordination, clean lifecycle management.

### 2. Producer/Consumer via `unclassified/` Folder

The `unclassified/` folder is the universal intake queue. **All** document sources are producers that drop files here:

- **Email sync task** → downloads attachment → writes to `unclassified/`
- **Folder watcher task** → detects new PDF in `~/Downloads` → copies to `unclassified/`
- **Manual drop** → user drags a file into `unclassified/`
- **Future sources** (IMAP, Outlook, Slack, etc.) → same pattern

The **classifier** watches `unclassified/` and is the sole consumer. This decoupling means:
- Adding a new source never touches the classification/extraction pipeline.
- Each stage can run at its own pace.
- The folder itself is the queue — no message broker needed.

### 3. .NET Async Pipeline with `Channel<T>`

```
Producer → unclassified/ → Classify → category/ → Extract → Embed → Indexed
```

Each stage is a long-running `Task` reading from a `Channel<T>`:

```fsharp
// Conceptual pipeline wiring
let classifyChannel = Channel.CreateBounded<FileEvent>(100)
let extractChannel  = Channel.CreateBounded<DocumentId>(100)
let embedChannel    = Channel.CreateBounded<DocumentId>(100)

// Classifier reads FS events, writes to extractChannel
// Extractor reads from extractChannel, writes to embedChannel
// Embedder reads from embedChannel, writes to sqlite-vec
```

Each stage is independent and idempotent:
- **Classify** reads from `unclassified/`, writes to `{category}/`, inserts DB row, posts to extract channel.
- **Extract** processes the document, updates DB, posts to embed channel.
- **Embed** generates vectors, writes to sqlite-vec.

Failures at any stage don't block earlier stages. A file that fails OCR is still classified and searchable by metadata.

### 4. SQLite as the Single Store

One `db.sqlite` file holds everything:
- Relational tables (messages, documents)
- FTS5 full-text index
- sqlite-vec vector embeddings

Why SQLite:
- Zero infrastructure. No Postgres, no Elasticsearch, no Pinecone.
- Single file to back up.
- `Microsoft.Data.Sqlite` with WAL mode — single writer (pipeline), concurrent readers (MCP server).
- Portable across macOS and Windows.

### 5. Ollama + Azure Document Intelligence

Hermes uses Ollama for local AI, with graceful fallback:

| Task | Ollama Model | No-GPU Fallback |
|------|-------------|-----------------|
| Embeddings | `nomic-embed-text` (768d) | ONNX Runtime (CPU, bundled model) |
| OCR / Vision | `llava` | Azure Document Intelligence (cloud, key required) |
| Field extraction | `llama3.2:3b` | Regex heuristics |

**Install flow**: Hermes installer detects GPU → `brew install ollama` / `winget install Ollama.Ollama` → pulls default models. Machines without GPU get keyword search + native PDF text extraction; users can optionally configure an Azure Document Intelligence key for cloud-based OCR.

### 6. Task Model

All background work runs as `Task`-based async operations managed by a `BackgroundService` host:

| Task | Role | Wake Trigger |
|------|------|-------------|
| **Email Sync** | Downloads new emails → attachments to `unclassified/` | Timer (configurable, default 15 min) |
| **Folder Watcher** | Watches configured folders → copies to `unclassified/` | FS events / poll fallback |
| **Classifier** | Watches `unclassified/` → dedup, classify, move | FS events → `Channel<T>` |
| **Extractor** | Processes unextracted files → text + fields | `Channel<T>` from classifier |
| **Embedder** | Generates vectors for extracted text | `Channel<T>` from extractor |
| **MCP Server** | HTTP listener on localhost | Incoming HTTP request |
| **Avalonia UI** | Tray icon + shell window | UI thread (main thread) |

All DB writes serialised through a single writer. MCP reads are concurrent via WAL.

### 7. Cross-Platform

| Concern | macOS | Windows |
|---------|-------|---------|
| Runtime | .NET 10 self-contained (arm64/x64) | .NET 10 self-contained (x64) |
| Service | launchd LaunchAgent (per-user) | Windows Service or Task Scheduler |
| Auto-start | plist in `~/Library/LaunchAgents/` | Scheduled task "at logon" |
| UI | Avalonia (native rendering) | Avalonia (native rendering) |
| FS watching | `FileSystemWatcher` (.NET, uses FSEvents) | `FileSystemWatcher` (uses ReadDirectoryChangesW) |
| Config | `~/.config/hermes/` | `%APPDATA%\hermes\` |
| Archive | `~/Documents/Hermes/` | `%USERPROFILE%\Documents\Hermes\` |
| Installer | `.dmg` (app bundle) | `.msi` (WiX) |
| Ollama install | `brew install ollama` | `winget install Ollama.Ollama` |

### 8. Data Flow for a Single Attachment

```
1. Email sync fetches message M from Gmail account "john-personal"
2. M has attachment "Invoice-2025-001.pdf" (85KB)
3. Sync writes:
     ~/Documents/Hermes/unclassified/2025-03-15_bobplumbing_Invoice-2025-001.pdf
   And records provenance metadata (sender, subject, date, account, gmail_id)
4. Classifier detects new file in unclassified/ via FileSystemWatcher
5. SHA256 → not a duplicate
6. Rules cascade: sender bob@plumbing.com.au → no domain match
                   filename "Invoice" → matches invoices/ rule
7. File moved to: ~/Documents/Hermes/invoices/2025-03-15_bobplumbing_Invoice-2025-001.pdf
8. DB row inserted: category=invoices, saved_path=invoices/..., extracted_at=NULL
9. DocumentId posted to extract channel
10. Extractor picks it up:
     - Native PDF text extraction (PdfPig or similar .NET library)
     - Regex finds: date=2025-03-15, amount=$385.00, vendor="Bob's Plumbing"
     - Updates DB row with extracted fields
     - Posts to embed channel
11. Embedder picks it up:
     - Chunks text → 2 chunks
     - Ollama generates embeddings → stored in sqlite-vec
12. Document is now fully searchable by keyword, metadata, and semantic query
13. User asks Claude: "find the plumber invoice from March"
     → Claude calls Hermes MCP search tool on localhost → finds it instantly
```
