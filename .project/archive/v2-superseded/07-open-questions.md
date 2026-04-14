# Hermes — Decisions Log

All questions resolved as of 2026-03-27.

## Architecture

### OQ-1: Language/Runtime — DECIDED
- **Decision**: **.NET 10 / F#**
- **Rationale**: Self-contained single binary (~30-50MB trimmed), native Windows Service + launchd support, F# pipelines map naturally onto the processing flow, no runtime dependency headaches. Avalonia for cross-platform tray + shell UI. Ollama via REST API (`HttpClient` / OllamaSharp). SQLite via `Microsoft.Data.Sqlite` with sqlite-vec as native extension. ONNX Runtime for no-Ollama embedding fallback.

### OQ-2: Threads vs Processes — DECIDED
- **Decision**: **Single process, `Task`-based async with `Channel<T>` for queues.**
- **Rationale**: All heavy work is I/O-bound (Gmail API, disk, Ollama HTTP). .NET async/await is built for this. `Channel<T>` provides bounded producer-consumer queues between pipeline stages. CPU-bound OCR (if needed) can use `Task.Run` to offload to the thread pool. One process simplifies packaging, service lifecycle, and the fact that the service IS the MCP server.

### OQ-3: MCP Server Coexistence — DECIDED
- **Decision**: **The service IS the MCP server.** Single process hosts background tasks + MCP over streamable HTTP on `localhost:{port}`. For MCP clients that only support stdio, a thin `hermes mcp` shim proxies stdio↔HTTP to the running service.
- **Rationale**: One process, one DB connection, no WAL contention questions, no coordination. Claude/VS Code MCP config points at the localhost endpoint or the stdio shim.

---

## Email & Sync

### OQ-4: Gmail Only or IMAP — DECIDED
- **Decision**: **Gmail API only for v1.** Provider abstraction (`IEmailProvider` with `ListMessages()`, `GetAttachments()`) so IMAP and Microsoft Graph slot in later.

### OQ-5: Index Email Bodies or Just Attachments — DECIDED
- **Decision**: **Documents (attachments + local files) primary. Email bodies deferred.**
- **Rationale**: Long-term vision is unified "find me everything about X" index. For v1, documents are the core value. Email metadata stored for provenance. Body indexing is a future phase that feeds into the same unified search surface.

### OQ-6: Email Threads — DECIDED
- **Decision**: **Store `thread_id` in messages table, ignore it otherwise for v1.**

---

## Classification

### OQ-7: Ollama for Classification — DECIDED
- **Decision**: **No. Rules cascade only for v1.** Ambiguous files go to `unsorted/`, user corrects by moving, `suggest-rules` learns from corrections. Ollama classification is a future enhancement.

### OQ-8: Category Depth — DECIDED
- **Decision**: **Arbitrary depth.** `category` in DB is the full relative path (e.g. `property/manorwoods`). Users create subcategories by creating folders. Rules can target any level.

---

## Ollama & AI

### OQ-9: Default Models — DECIDED
- **Decision**: 
  - **Embeddings**: `nomic-embed-text` (768d) — good quality, reasonable size.
  - **Vision**: `llava` — for scanned document OCR.
  - **Instruct**: `llama3.2:3b` — small, fast, for field extraction.
- **Baseline**: 8GB GPU minimum. Configurable to larger models if user has more VRAM.
- **Benchmark**: Validate on sample documents before release.

### OQ-10: No Ollama Installed — DECIDED
- **Decision**: **Install Ollama at Hermes install time.** Detect GPU → `brew install ollama` (macOS) / `winget install Ollama.Ollama` (Windows), verifying package managers exist first. Then trigger default model downloads. Carry nothing — install from package managers. **Fallback**: Azure Document Intelligence for machines without GPU (requires Azure key in config).
- **Rationale**: Hermes must still be fully functional without Ollama (rules + native PDF text extraction + FTS5 keyword search). Ollama adds semantic search + better OCR + smarter extraction but is additive.

---

## Packaging & UX

### OQ-11: First-Run Setup — DECIDED
- **Decision**: **Avalonia shell window.** The tray icon opens a settings/configuration panel. First-run wizard is the same UI — authenticate accounts, pick archive folder, configure watched folders. No separate web page or terminal needed.

### OQ-12: System Tray / UI Framework — DECIDED
- **Decision**: **Avalonia.** Cross-platform .NET UI framework. Provides tray icon, notification area, and the shell window for settings/status/configuration. Single framework for all UI needs.
- **Future**: A chat window in the Avalonia UI may be added later, providing a native conversational interface to the Hermes index alongside the MCP server.

### OQ-13: Archive Location — DECIDED
- **Decision**: **`~/Documents/Hermes/`** (macOS) / **`%USERPROFILE%\Documents\Hermes\`** (Windows). Visible in Finder/Explorer. Users can browse category folders directly. Configurable in settings.

---

## Out-of-Scope (v1)

- IMAP / Microsoft Graph / Outlook support
- Sending or replying to email
- Email body indexing (deferred — future phase)
- Near-duplicate detection
- Multi-device sync
- Mobile apps
- Cloud deployment
- Built-in chat UI (future Avalonia addition)
