# Hermes

[![CI](https://github.com/johnazariah/hermes/actions/workflows/ci.yml/badge.svg)](https://github.com/johnazariah/hermes/actions/workflows/ci.yml)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Tests](https://img.shields.io/badge/tests-857_passed%2C_10_skipped-yellow)](#development)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**Local-first document intelligence service.**

Hermes ingests documents from email and local folders, understands them through
LLM comprehension, and exposes structured knowledge via MCP server and web UI.
Processing is local by default with Ollama and SQLite; optional cloud model
providers require explicit configuration and may receive prompt content.

## What It Does

- **Syncs email** from Gmail and Outlook into the file-first archive, with
  keyword search over metadata, immutable bodies and current extracted content
- **Watches folders** (Downloads, Desktop) for new files
- **Extracts text** from PDFs, Excel, Word, CSV → structured markdown
- **Comprehends documents** — LLM reads each document and produces structured JSON with document type, key fields, and summary
- **Searches through HTTP and MCP** — keyword, semantic and hybrid modes share
  a typed contract with explicit missing-index/model outcomes and vector coverage
- **Reads persisted structured data** — document-scoped fields, source
  provenance and effective corrections are available without regenerating
  comprehension; unattributed shared-thread enrichment is explicitly unavailable
- **MCP server** — read-only tools by default, authenticated mutations by explicit
  opt-in, with loopback Host/origin checks
- **Preview web UI** — registered React routes for Home, Documents, Search,
  Settings, and onboarding; Pipeline and Chat route wiring remains in
  [#9](https://github.com/johnazariah/hermes/issues/9)

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
| AI | Local Ollama by default; optional Azure OpenAI; nomic-embed-text |
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

### Rebuild selected file-content search indexes

Use an existing archive and a configuration file with an explicit, nonblank
`archive_dir`. The command defaults to a read-only preview:

```bash
dotnet run --project src/Hermes.Service -- reindex-files --config /path/to/config.yaml --document-id 123 --document-id 456

# Explicitly apply the selected index updates:
dotnet run --project src/Hermes.Service -- reindex-files --config /path/to/config.yaml --document-id 123 --document-id 456 --apply
```

Select 1–100 positive, distinct IDs; there is no implicit whole-archive mode.
The command exits before starting providers, watchers or the web host. It
reports JSON outcomes for every selected document, preserves source files and
user metadata, and refuses documents with persisted processing attempts.
Missing/empty operator configuration never falls back to a default archive.
Exit codes: `0` success, `1` operational/document failure, `2` invalid arguments.
This rebuilds derived search content, not a lost SQLite database.

### Local MCP access

The Service exposes streamable HTTP at `/mcp` on port 21741 by default, or the
explicit `HERMES_PORT`. `hermes_search` defaults to keyword mode and also accepts
`semantic` and `hybrid`; vector modes require a configured query encoder and
current indexed vectors. Missing or failed dependencies are reported rather
than silently substituting keyword results. `hermes_get_comprehension` reads
already-persisted document fields without invoking a generative model.
The corresponding HTTP accessor is `GET /api/documents/{id}/comprehension`.
Its source and canonical-publication digests identify the evidence; separate
document/publication generations retain original provenance through reembedding.

Mutating tools are hidden and disabled by default. To enable them, explicitly
set `HERMES_MCP_MUTATIONS=true` and provide `HERMES_MCP_TOKEN` as a 32–4,096
character non-whitespace secret. Send `Authorization: Bearer <token>` with each
mutation request. Keep the token out of source, URLs and logs.

Browser origins must be explicit loopback HTTP(S) origins. Production 21741,
development 21742 and the configured Service port are allowed; additional
preview origins use the semicolon-separated `HERMES_ALLOWED_ORIGINS` variable.
Foreign Host/origin requests are rejected. Local reads without an Origin
remain available, but an absent Origin never authorises a mutation. This is
MCP capability authentication; broader REST/browser/Tray session integration
remains separate work.

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
