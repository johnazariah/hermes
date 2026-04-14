# Hermes

[![CI](https://github.com/johnazariah/hermes/actions/workflows/ci.yml/badge.svg)](https://github.com/johnazariah/hermes/actions/workflows/ci.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Tests](https://img.shields.io/badge/tests-700_passing-brightgreen)](#testing)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**Local-first document intelligence service.**

Hermes ingests documents from email and local folders, understands them through LLM comprehension, and exposes structured knowledge via MCP server and web UI. Everything runs locally — Ollama for AI, SQLite for storage, no cloud dependency.

## What It Does

- **Syncs email** from multiple Gmail accounts — every message and attachment becomes a searchable document
- **Watches folders** (Downloads, Desktop) for new files
- **Extracts text** from PDFs, Excel, Word, CSV → structured markdown
- **Comprehends documents** — LLM reads each document and produces structured JSON with document type, key fields, and summary
- **Indexes everything** — FTS5 keyword search + vector embeddings for semantic search
- **MCP server** — AI agents query documents, search, and get structured data
- **Web UI** — five-page app: Pipeline dashboard, Document browser, Search, Chat, Settings

## Architecture

```
Producers                Pipeline (Channel<Document>)           Consumers
──────────              ─────────────────────────               ──────────
Email Sync ──┐          Extract → Comprehend → Embed            Web UI
(N accounts)  ├──→                                              MCP Server
Folder Watch ─┘          ↑ hydrate on restart                   Osprey (tax)
```

Core primitives:
- **Document = Map\<string, obj\>** — property bag, typed access via `decode<'T>`
- **Channel\<Document\>** — runtime flow between stages, no polling
- **Workflow.runStage** — generic monad: idempotency, write-aside, error handling
- **GPU resource lock** — SemaphoreSlim burst-hold for Ollama model contention

See [Pipeline v4 Architecture](.project/design/23-pipeline-v4-architecture.md) for full design.

## Technology

| Component | Choice |
|-----------|--------|
| Runtime | .NET 10, F# |
| Database | SQLite + FTS5 + sqlite-vec |
| AI | Ollama (llama3:8b + nomic-embed-text) |
| Web UI | React 19 + Vite + Tailwind |
| Testing | xUnit + FsCheck (F#), Playwright (UI) |

## Development

```bash
dotnet build                                    # build all
dotnet test                                     # run all tests (700+)
dotnet run --project src/Hermes.Service         # run service (prod)

# Dev mode (separate port, config, archive):
$env:HERMES_CONFIG_DIR = "$env:APPDATA\hermes-dev"
$env:HERMES_PORT = "21742"
dotnet run --project src/Hermes.Service -- --initial-sync-days 90
```

## Solution Structure

```
src/
├── Hermes.Core/          F# library — pipeline, extraction, comprehension, DB
├── Hermes.Service/       F# service — HTTP API, MCP server, pipeline host
└── Hermes.Web/           React 19 — five-page web UI
tests/
└── Hermes.Tests/         xUnit + FsCheck + Playwright
.project/
├── STATUS.md             Project dashboard
├── design/               10 active design docs
└── archive/              Historical pre-v4 material
```

## Documentation

| Doc | Topic |
|-----|-------|
| [STATUS.md](.project/STATUS.md) | Current state & roadmap |
| [23 — Pipeline v4](.project/design/23-pipeline-v4-architecture.md) | Architecture (channels, property bags, workflow monad) |
| [24 — Comprehension](.project/design/24-comprehension-stage.md) | LLM comprehension replaces classification |
| [01 — Vision](.project/design/01-vision-and-principles.md) | Design principles |
| [04 — Data Model](.project/design/04-data-model.md) | Schema, config, storage |
| [17 — PDF Extraction](.project/design/17-pdf-to-markdown.md) | PdfPig structural extraction |

## License

MIT
