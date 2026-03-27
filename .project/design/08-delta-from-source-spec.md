# Hermes — Delta from Source Spec (Osprey Spec 12: Mail Archive)

## What We Took

| Source Concept | Hermes Adoption | Notes |
|---------------|-----------------|-------|
| Three-phase pipeline (Ingest → Extract → Embed) | ✅ Kept, refined | Decoupled into async tasks with `Channel<T>` queues |
| `unclassified/` intake folder | ✅ Kept, promoted | Now the universal intake point for all document sources |
| Classification rules cascade (domain → filename → subject) | ✅ Kept as-is | Proven design, user-correctable |
| "Folders are truth" + reconcile | ✅ Kept as-is | Core UX principle |
| SQLite + FTS5 + sqlite-vec | ✅ Kept as-is | Single-file store via `Microsoft.Data.Sqlite` |
| SHA256 dedup | ✅ Kept as-is | |
| Gmail OAuth2, per-account tokens | ✅ Kept as-is | |
| `suggest-rules` from user overrides | ✅ Kept as-is | |
| Rate limiting (1s/45 calls, backoff on 429) | ✅ Kept as-is | |
| Interrupt-safe sync | ✅ Kept as-is | |

## What We Changed

| Source Concept | Hermes Change | Why |
|---------------|--------------|-----|
| **Python** | → **.NET 10 / F#** | Self-contained binary (~30-50MB), native service support, no runtime dependency, type safety |
| CLI-only tool | → **Background service + Avalonia tray/shell + CLI** | Needs to run unattended for non-technical users |
| pystray / rumps | → **Avalonia** | Cross-platform .NET UI: tray icon + shell window + future chat panel |
| `sentence-transformers` default | → **Ollama embeddings**, ONNX Runtime fallback | Leverages local GPU, keeps data on machine |
| Tesseract OCR fallback | → **Azure Document Intelligence** fallback | Better quality on complex layouts for machines without GPU |
| pdfplumber | → **PdfPig** (.NET native) | Same function, .NET ecosystem |
| MCP via stdio subprocess | → **MCP via streamable HTTP in-process** | Service IS the MCP server; no separate process, no DB contention |
| Attachments only (v1) | → **Documents primary, email bodies deferred** | Email metadata stored for provenance; body indexing is a future unified search phase |
| `attachments` table | → `documents` table | Encompasses email attachments + watched folder files + manual drops |
| `mail-archive` name | → `hermes` | General-purpose document intelligence, not just mail |
| Developer audience | → "Mum-friendly" primary target | Self-contained installer, Avalonia UI, no terminal for daily use |
| Manual cron scheduling | → Built-in service with configurable interval | |
| `all-MiniLM-L6-v2` (384d) | → `nomic-embed-text` (768d) via Ollama | Better quality, GPU-accelerated |
| PyInstaller packaging | → `dotnet publish` self-contained | Smaller binary, faster startup, no Python runtime |
| Local web page for setup | → **Avalonia shell window** | Native setup wizard in the same UI framework |

## What We Added

| New in Hermes | Description |
|---------------|-------------|
| **Avalonia UI** | System tray icon + shell window (settings, status, first-run wizard). Future: chat panel. |
| **Folder watching** | Watch `~/Downloads` etc. for new documents — same pipeline as email |
| **MCP server (in-process)** | Streamable HTTP on localhost + stdio shim. Primary AI query interface. |
| **Background service** | launchd (macOS) / Windows Service; auto-start, crash recovery |
| **Ollama auto-install** | Installer detects GPU → `brew install` / `winget install` → pull models |
| **Azure Document Intelligence** | Cloud OCR fallback for machines without Ollama/GPU |
| **ONNX Runtime** | CPU embedding fallback with bundled model |
| **`Channel<T>` pipeline** | Type-safe async producer-consumer queues between pipeline stages |
| **`IEmailProvider` abstraction** | Gmail implements it; IMAP/Graph slot in later |
| **`source_type` tracking** | Know whether a document came from email, watched folder, or manual drop |
| **Update mechanism** | Check for updates, notify via tray |

## What We Dropped

| Source Concept | Dropped Because |
|---------------|-----------------|
| **Python** | .NET gives smaller binary, native services, type safety, Avalonia for UI |
| Osprey integration | Hermes is standalone; Osprey can read the archive if needed |
| `--query GMAIL_QUERY` passthrough | Premature; add later if needed |
| Explicit phase commands as the only interface | Service runs all phases automatically; CLI commands still available for power users |
| Australia-specific defaults (ABN, ATO, etc.) | Hermes config is locale-agnostic; users configure their own domain/filename rules |
| Email body indexing (v1) | Deferred — future phase feeds into unified search |
| Tesseract OCR | Replaced by Ollama vision (primary) + Azure Doc Intelligence (fallback) |
| `sentence-transformers` | Replaced by Ollama (primary) + ONNX Runtime (fallback) |

## Risk Register

| Risk | Impact | Mitigation |
|------|--------|-----------|
| F# ecosystem smaller than Python for ML/NLP | Fewer libraries to choose from | Ollama is REST API (language-agnostic); PdfPig covers PDF; ONNX Runtime has .NET bindings |
| Avalonia cross-platform UI quirks | Inconsistent look on macOS vs Windows | Test on both platforms in CI; Avalonia is mature for tray + simple panels |
| Ollama not installed / install fails | No semantic search, weaker extraction | Full graceful degradation: rules + PdfPig + FTS5 keyword search. Azure Doc Intelligence for OCR. |
| `brew` / `winget` not available on target machine | Ollama auto-install fails | Detect and fall back to manual install prompt; provide download link in settings panel |
| Gmail OAuth consent screen requires Google verification | Blocks non-developer users | Use "Testing" mode for personal use; or publish to Google if audience grows |
| sqlite-vec native extension loading on both platforms | Build/distribution complexity | Pre-built native binaries for macOS arm64/x64 + Windows x64; tested in CI |
| .NET self-contained binary size | ~50-80MB per platform | Acceptable for a desktop app; trimming and ReadyToRun reduce size and improve startup |
