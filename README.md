# Hermes

[![CI](https://github.com/johnazariah/hermes/actions/workflows/ci.yml/badge.svg)](https://github.com/johnazariah/hermes/actions/workflows/ci.yml)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Tests](https://img.shields.io/badge/tests-857_passed%2C_10_skipped-yellow)](#development)
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
- **Preview web UI** — React routes for Home, Pipeline, Documents, Search, Chat, Settings, and onboarding

## Architecture

```
Gmail ───────┐                                ┌─→ REST / preview React UI
Outlook ─────┼─→ file-first archive → V5 DAG ┼─→ MCP / Osprey
Folder watch ┘                  │             └─→ FTS5 + vector indexes
                               └─ extract → triage → deep-comprehend
                                          └──────→ embed
```

Core primitives:
- **Declarative DAG** — stages declare dependencies, output schemas, gates, and execution mode
- **`stage_completions` ledger** — durable idempotency and restart recovery
- **File-first content** — archive files hold readable content; SQLite holds metadata and indexes
- **Model-aware GPU scheduler** — serializes constrained local model use

See [Pipeline V5 Architecture](.project/design/30-pipeline-v5-architecture.md) for full design.

## Technology

| Component | Choice |
|-----------|--------|
| Runtime | .NET 9, F# |
| Database | SQLite + FTS5 + sqlite-vec |
| AI | Ollama (llama3:8b + nomic-embed-text) |
| Web UI | React 19 + Vite + Tailwind |
| Testing | xUnit + FsCheck (F#), Playwright (UI) |

## Development

```bash
dotnet build                                    # build all
dotnet test                                     # run all tests (867 currently discovered)
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
├── Hermes.Web/           React 19 — canonical preview web UI
├── Hermes.Tray/          Windows tray — preview native shell
├── Hermes.UI/            Blazor components — excluded from support
└── Hermes.Shell/         MAUI Windows/macOS — excluded from support
tests/
└── Hermes.Tests/         xUnit + FsCheck
.project/
├── STATUS.md             Project dashboard
├── waves/                Active and completed work journals
├── design/               Current architecture references
└── archive/              Superseded design and planning material
```

## Documentation

| Doc | Topic |
|-----|-------|
| [STATUS.md](.project/STATUS.md) | Current state & roadmap |
| [30 — Pipeline V5](.project/design/30-pipeline-v5-architecture.md) | Current DAG, file-first, and compatibility architecture |
| [24 — Comprehension](.project/design/24-comprehension-stage.md) | Triage and gated deep comprehension |
| [01 — Vision](.project/design/01-vision-and-principles.md) | Design principles |
| [28 — File-First Archive](.project/design/28-file-first-archive.md) | Current storage boundary |
| [04 — Data Model V4](.project/archive/04-data-model-v4.md) | Historical schema and config reference |
| [17 — PDF Extraction](.project/design/17-pdf-to-markdown.md) | PdfPig structural extraction |

## License

MIT
