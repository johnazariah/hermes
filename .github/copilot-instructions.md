# Hermes — Copilot Instructions

## Working Style

**Default: discuss, don't code.** Propose changes and wait for approval before implementing. When asked to implement, proceed with full context.

## Project Overview

Hermes is a local-first document intelligence service for macOS and Windows. It connects to email accounts, watches local folders, and continuously ingests, classifies, and indexes documents — exposing everything through an MCP server for AI agents.

| Concept      | Description                                                               |
| ------------ | ------------------------------------------------------------------------- |
| **Archive**  | `~/Documents/Hermes/` — categorised document storage                      |
| **Intake**   | `unclassified/` folder — universal queue for all document sources         |
| **Pipeline** | Classify → Extract → Embed (async `Channel<T>` stages)                    |
| **Rules**    | YAML-configured cascade: sender domain → filename → subject → `unsorted/` |
| **Index**    | SQLite + FTS5 (keyword) + sqlite-vec (semantic)                           |
| **MCP**      | Streamable HTTP on `localhost:21740`, stdio shim for compat               |

## Architecture

```
Hermes Process (.NET 9)
├── Producers: Email Sync, Folder Watchers → unclassified/
├── Pipeline: Classifier → Extractor → Embedder (Channel<T>)
├── Store: db.sqlite (tables + FTS5 + sqlite-vec)
├── MCP Server: HTTP on localhost (+ stdio shim)
└── UI: Avalonia tray icon + shell window
```

## Technology Stack

| Component  | Choice                                               |
| ---------- | ---------------------------------------------------- |
| Runtime    | .NET 9, self-contained                               |
| Language   | F# (core logic), C# (Avalonia UI)                    |
| UI         | Avalonia (cross-platform tray + shell)               |
| Database   | SQLite via `Microsoft.Data.Sqlite`                   |
| Email      | `Google.Apis.Gmail.v1`                               |
| PDF        | PdfPig (`UglyToad.PdfPig`)                           |
| Embeddings | Ollama REST API / ONNX Runtime fallback              |
| OCR        | Ollama `llava` / Azure Document Intelligence         |
| Config     | YAML via `YamlDotNet`                                |
| Hosting    | `Microsoft.Extensions.Hosting` (`BackgroundService`) |
| Pipeline   | `System.Threading.Channels`                          |
| Testing    | xUnit + FsCheck                                      |
| Logging    | Serilog                                              |

## Solution Structure

```
src/
├── Hermes.Core/          F# library — domain, pipeline, DB, config
├── Hermes.App/           Avalonia entry point — tray, shell, service host
└── Hermes.Cli/           CLI entry point — thin wrapper calling Core
tests/
└── Hermes.Tests/         xUnit + FsCheck
.project/
├── design/               Architecture & design docs
├── specs/                Phase specs with task checklists
├── phases.md             Dependency graph for automated planning
├── testing-register.md   Test catalog
└── STATUS.md             Project status dashboard
```

## Development Commands

```bash
dotnet build                              # build all
dotnet test                               # run all tests
dotnet run --project src/Hermes.Cli       # run CLI
dotnet run --project src/Hermes.App       # run app (tray + service)
dotnet publish -c Release -r win-x64 --self-contained   # publish Windows
dotnet publish -c Release -r osx-arm64 --self-contained  # publish macOS
```

## Code Conventions

### F# (Hermes.Core)

- **Immutable by default**: records, discriminated unions, `let` bindings
- **Pipeline operators**: `|>` chains for data flow
- **Result type**: `Result<'T, 'Error>` for operations that can fail — no exceptions for business logic
- **Async**: `task { }` computation expressions, avoid `Async.RunSynchronously`
- **Naming**: PascalCase for types and public functions, camelCase for local bindings
- **Module structure**: one module per concept, `[<RequireQualifiedAccess>]` for disambiguation

### C# (Hermes.App — Avalonia)

- **Nullable reference types**: enabled, no suppressions
- **Primary constructors**: where appropriate
- **Records**: for DTOs and view models
- **Warnings as errors**: `TreatWarningsAsErrors` in `Directory.Build.props`

### General

- **Line endings**: LF everywhere (`.gitattributes` enforced)
- **Encoding**: UTF-8 with BOM for F#/C#, UTF-8 without BOM for YAML/JSON/MD
- **Indentation**: 4 spaces (F#/C#), 2 spaces (YAML/JSON)
- **Conventional commits**: `feat:`, `fix:`, `docs:`, `test:`, `chore:`, `refactor:`

## Testing Conventions

- **xUnit** for test framework
- **FsCheck** for property-based tests
- **Test naming**: `Module_Function_Condition_ExpectedResult`
- **Test categories**: `[<Trait("Category", "Unit")>]`, `Integration`, `Property`
- **Update the testing register** (`.project/testing-register.md`) when adding/modifying tests

## Key Files

| File                                      | Purpose                              |
| ----------------------------------------- | ------------------------------------ |
| `.project/design/03-architecture.md`      | Architecture overview with diagrams  |
| `.project/design/04-data-model.md`        | SQLite schema, config YAML format    |
| `.project/design/05-mcp-server-design.md` | MCP tools with JSON schemas          |
| `.project/design/07-open-questions.md`    | All decisions (resolved)             |
| `.project/specs/phase-*.md`               | Phase specs with acceptance criteria |
| `.project/testing-register.md`            | Test catalog — keep in sync          |

## Tips for AI Agents

1. Read the current phase spec before implementing
2. Check `.project/design/07-open-questions.md` for resolved decisions
3. Update the testing register when tests change
4. Use the commit prompt (`.github/prompts/commit.prompt.md`) for clean commits
5. Run `dotnet build` and `dotnet test` before committing
