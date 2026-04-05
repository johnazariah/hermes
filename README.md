# Hermes

[![CI](https://github.com/johnazariah/hermes/actions/workflows/ci.yml/badge.svg)](https://github.com/johnazariah/hermes/actions/workflows/ci.yml)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Tests](https://img.shields.io/badge/tests-817_passing-brightgreen)](#testing)
[![Coverage](https://img.shields.io/badge/coverage-86%25_line-brightgreen)](#testing)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Lines of Code](https://img.shields.io/badge/lines-~15k-blue)](#architecture)

**Local-first document intelligence service for macOS and Windows.**

Hermes connects to your email accounts, watches local folders, and continuously ingests, classifies, and indexes every document that passes through your digital life. Everything stays on your machine, processed by local AI (Ollama), and exposed through an MCP server so AI agents can search and reason over your documents.

Install it once, forget it's there, and never lose a document again.

## What It Does

- **Syncs email attachments** from multiple Gmail accounts (incrementally, safely)
- **Watches local folders** (Downloads, Desktop) for new documents
- **Classifies** into category folders using 3-tier system: rules (instant) → content keywords (fast) → LLM (smart)
- **Extracts structured content** from PDFs, Excel, Word, CSV → markdown with tables, headings, key-value pairs
- **Indexes everything** — FTS5 keyword search + Ollama vector embeddings for semantic search
- **MCP server** — 13 tools for AI agents to search, browse, classify, and manage documents
- **Bill detection** — automatic reminders for invoices with due dates

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
| Tests | 817 (3 projects) | Growing |
| Line coverage | 86% | **85%** |
| Branch coverage | ~45% | **60%** |

Coverage target is enforced in CI. New code must maintain or improve coverage.

## Project Status

All 12 core phases complete. Platform evolution in progress.

See [.project/STATUS.md](.project/STATUS.md) for full details.

| Wave | Status |
|------|--------|
| 0–11 Core Phases | ✅ Complete |
| Backfill + Reminders | ✅ Done |
| Tagless-Final Cleanup | ✅ Done |
| Coverage Push (85% line) | ✅ Done |
| Osprey Parity Validation | ✅ Done |
| Structured Extraction Pipeline | ✅ Done |
| Smart Classification (3-tier) | ✅ Done |
| UI: Pipeline Funnel | ⏳ Next |
| Pelican GL Integration | Planned |
| Polish + Production | Planned |

## Documentation

- [Project Status](.project/STATUS.md)
- [Documentation Governance](.project/GOVERNANCE.md)
- [Vision & Goals](.project/design/01-vision-and-goals.md)
- [Architecture](.project/design/03-architecture.md)
- [Data Model](.project/design/04-data-model.md)
- [MCP Server Design](.project/design/05-mcp-server-design.md)
- [Pipeline Funnel UI](.project/design/15-rich-ui.md)
- [Document Extraction](.project/design/17-pdf-to-markdown.md)
- [Smart Classification](.project/design/18-smart-classification.md)
- [Wave Files](.project/waves/)

## License

MIT
