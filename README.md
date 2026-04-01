# Hermes

[![CI](https://github.com/johnazariah/hermes/actions/workflows/ci.yml/badge.svg)](https://github.com/johnazariah/hermes/actions/workflows/ci.yml)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Tests](https://img.shields.io/badge/tests-258_passing-brightgreen)](#testing)
[![Coverage](https://img.shields.io/badge/coverage-55%25_line-yellow)](#testing)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Lines of Code](https://img.shields.io/badge/lines-~12k-blue)](#architecture)

**Local-first document intelligence service for macOS and Windows.**

Hermes connects to your email accounts, watches local folders, and continuously ingests, classifies, and indexes every document that passes through your digital life. Everything stays on your machine, processed by local AI (Ollama), and exposed through an MCP server so AI agents can search and reason over your documents.

Install it once, forget it's there, and never lose a document again.

## What It Does

- **Syncs email attachments** from multiple Gmail accounts (incrementally, safely)
- **Watches local folders** (Downloads, Desktop) for new documents
- **Classifies** into category folders using fast rules (sender domain → filename → subject)
- **Extracts text** from PDFs and images (PdfPig, Ollama vision, Azure Document Intelligence)
- **Indexes everything** — FTS5 keyword search + Ollama vector embeddings for semantic search
- **MCP server** — AI agents (Claude, Copilot) query your archive: _"find the plumber invoice from March"_

## Architecture

```
Hermes Process (.NET 9 / F#)
├── Producers: Email Sync, Folder Watchers → unclassified/
├── Pipeline: Classifier → Extractor → Embedder (Channel<T>)
├── Store: db.sqlite (tables + FTS5 + sqlite-vec)
├── MCP Server: Streamable HTTP on localhost:21740
└── UI: Avalonia tray icon + shell window
```

## Technology

| Component | Choice |
|-----------|--------|
| Runtime | .NET 9, self-contained |
| Core Logic | F# |
| UI | Avalonia (C#) |
| Database | SQLite + FTS5 + sqlite-vec |
| AI | Ollama (local GPU) / Azure OpenAI / ONNX Runtime |

## Development

```bash
dotnet build           # build all
dotnet test            # run all tests
dotnet run --project src/Hermes.Cli    # run CLI
dotnet run --project src/Hermes.App    # run app
```

## Testing

```bash
dotnet test                                           # run all tests
dotnet test --collect:"XPlat Code Coverage"             # with coverage
```

| Metric | Current | Target |
|--------|---------|--------|
| Tests | 258 | Growing |
| Line coverage | 55% | **85%** |
| Branch coverage | 22% | **85%** |

Coverage target is enforced in CI. New code must maintain or improve coverage.

## Project Status

All 12 core phases complete. Active development on platform evolution.

See [.project/STATUS.md](.project/STATUS.md) for full details.

| Phase | Status |
|-------|--------|
| 0–11 | ✅ All core phases complete |
| Backfill + Reminders | ✅ Done |
| Azure OpenAI Chat | ✅ Done |
| VS Code UI Redesign | 📐 Designing |
| Document Extraction (PDF/Excel/Word/CSV) | 📐 Designing |
| Smart Classification (3-tier) | 📐 Designing |
| Document Feed + Consumer Model | 📐 Designing |
| Osprey Integration | 📐 Designing |

## Documentation

- [Vision & Goals](.project/design/01-vision-and-goals.md)
- [Functional Requirements](.project/design/02-functional-requirements.md)
- [Architecture](.project/design/03-architecture.md)
- [Data Model](.project/design/04-data-model.md)
- [MCP Server Design](.project/design/05-mcp-server-design.md)
- [Development Phases](.project/design/06-development-phases.md)
- [Decisions Log](.project/design/07-open-questions.md)
- [Phase Specs](.project/specs/)

## License

MIT
