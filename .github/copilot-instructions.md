# Hermes — Copilot Instructions

## Working Style

**Default: discuss, don't code.** Propose changes and wait for approval before implementing. When asked to implement, proceed with full context.

## Documentation

Before creating or modifying any project documentation, read `.project/GOVERNANCE.md`.

- **Project status**: `.project/STATUS.md` (read first — 50 lines)
- **Architecture**: `.project/design/23-pipeline-v4-architecture.md` (the canonical design doc)
- **Comprehension**: `.project/design/24-comprehension-stage.md` (next to implement)
- **Design reference**: `.project/design/*.md` (what/why, not status)

## Project Overview

Hermes is a local-first document intelligence service. It ingests documents from email and local folders, understands them through LLM comprehension, and exposes structured knowledge via MCP server and web UI.

| Concept       | Description                                                               |
| ------------- | ------------------------------------------------------------------------- |
| **Archive**   | `~/Documents/Hermes/` — all files live in `unclassified/`, never moved    |
| **Pipeline**  | ingest → Extract → Comprehend → Embed (`Channel<Document>` stages)        |
| **Document**  | `Map<string, obj>` property bag — stages add keys, never remove           |
| **Workflow**  | `runStage` monad — idempotency, write-aside, GPU lock, error handling     |
| **Index**     | SQLite + FTS5 (keyword) + sqlite-vec (semantic)                           |
| **MCP**       | Streamable HTTP on `localhost:21741` (prod) / `21742` (dev)               |

## Architecture

```
Producers                Pipeline (Channel<Document>)           Consumers
──────────              ─────────────────────────               ──────────
Email Sync ──┐          Extract → Comprehend → Embed            React Web UI
(N accounts)  ├──→                                              MCP Server
Folder Watch ─┘          ↑ hydrate on restart                   Osprey (tax)
```

## Technology Stack

| Component  | Choice                                               |
| ---------- | ---------------------------------------------------- |
| Runtime    | .NET 10, F#                                          |
| UI         | React 19 + Vite + Tailwind                           |
| Database   | SQLite via `Microsoft.Data.Sqlite`                   |
| Email      | `Google.Apis.Gmail.v1`                               |
| PDF        | PdfPig (`UglyToad.PdfPig`)                           |
| LLM        | Ollama llama3:8b (comprehension)                     |
| Embeddings | Ollama nomic-embed-text                              |
| Pipeline   | `System.Threading.Channels`                          |
| Testing    | xUnit + FsCheck (F#), Playwright (UI)                |
| Logging    | Serilog                                              |

## Solution Structure

```
src/
├── Hermes.Core/          F# library — pipeline, extraction, comprehension, DB
├── Hermes.Service/       F# service — HTTP API, MCP server, pipeline host
└── Hermes.Web/           React 19 — five-page web UI
tests/
├── Hermes.Tests/         xUnit + FsCheck (700 tests)
└── Hermes.Web/tests/     Playwright smoke tests (9 tests)
.project/
├── STATUS.md             Project dashboard
├── design/               10 active design docs
└── archive/              Historical pre-v4 material
```

## Development Commands

```bash
dotnet build                              # build all
dotnet test                               # run all tests

# Dev mode (separate port, config, archive):
$env:HERMES_CONFIG_DIR = "$env:APPDATA\hermes-dev"
$env:HERMES_PORT = "21742"
dotnet run --project src/Hermes.Service -- --initial-sync-days 90

# Playwright UI tests:
cd src/Hermes.Web && npx playwright test tests/smoke.spec.ts
```

## Code Conventions

### F# (Hermes.Core)

- **Tagless-Final architecture**: define capabilities as abstract records of functions, parameterized over the effect type. Wire concrete implementations at the composition root. This applies to all provider abstractions (email, extraction, embedding, storage, search).
    ```fsharp
    // Example: capabilities as records of functions
    type EmailProvider<'F> = {
        ListMessages: DateTimeOffset option -> 'F<EmailMessage list>
        GetAttachments: string -> 'F<EmailAttachment list>
    }
    // Concrete: GmailProvider : EmailProvider<Task>
    // Test:     FakeProvider  : EmailProvider<Id>
    ```
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
- **Coverage target**: 85% line coverage, 60% branch coverage. Branch target is lower because F# `task {}` / `async {}` computation expressions generate synthetic IL state machine branches that aren't testable business logic. New code must maintain or improve coverage. Run `dotnet test --collect:"XPlat Code Coverage"` to check.
- **Coverage is mandatory**: Do not mark a task as complete if it drops coverage below the current level. Write tests for every new function.

## Key Files

| File                                      | Purpose                              |
| ----------------------------------------- | ------------------------------------ |
| `.project/design/03-architecture.md`      | Architecture overview with diagrams  |
| `.project/design/04-data-model.md`        | SQLite schema, config YAML format    |
| `.project/design/05-mcp-server-design.md` | MCP tools with JSON schemas          |
| `.project/design/07-open-questions.md`    | All decisions (resolved)             |
| `.project/specs/phase-*.md`               | Phase specs with acceptance criteria |
| `.project/testing-register.md`            | Test catalog — keep in sync          |

## Idiom Standards (Always Active)

All AI-generated code in this project **must** conform to the language idiom standards. Full standards are in `devex-toolkit` (multi-root workspace peer). Key rules summarised here for when devex-toolkit is unavailable:

- **F# (Hermes.Core):** Small functions (≤20 lines), `|>` pipelines, no `mutable`, DUs over strings, `Option.map`/`bind`/`defaultValue` over explicit match, `task {}` blocks ≤15 lines, Tagless-Final with records-of-functions, partial application (stable params first), active patterns for complex matching.
- **C# (Hermes.App):** Records over classes, `sealed` by default, pattern matching over `if/else`, LINQ for transformations, non-nullable, `CancellationToken` on all async methods, Tagless-Final with capability interfaces, list/relational/property patterns.
- **Architecture:** Tagless-Final as default architecture, fakes over mocks, capability records parameterised over effect type.

For dedicated write/review/refactor workflows, invoke the `@fsharp-dev` or `@csharp-dev` agents.

## Agent Workflow Requirements

### Language agent delegation (mandatory)

When writing, reviewing, or refactoring code:

- **F# code** (`Hermes.Core`, `Hermes.Tests`): delegate to `@fsharp-dev`. Do not write F# without it.
- **C# code** (`Hermes.App`): delegate to `@csharp-dev`. Do not write C# without it.
- The language agents enforce idiom standards, catch anti-patterns, and produce higher quality code than unguided generation.

### Silver thread principle (mandatory)

Every feature must be implemented as a **silver thread** — an unbroken chain from UI to backend and back:

```
User action (button click, query, trigger)
  → Processing (F# Core logic, DB queries, API calls)
    → State change (database, config, in-memory)
      → Presentation (ViewModel property update)
        → UI response (user sees the result)
```

**Before marking any task complete, trace the full thread:**

1. **Input**: What triggers this feature? (file drop, button click, sync cycle, MCP call)
2. **Processing**: What backend code runs? (extraction, classification, DB update)
3. **Presentation**: What data flows to the UI? (ViewModel property, collection update)
4. **Output**: What does the user see? (document in list, activity log entry, badge update, chat response)

**If ANY link in this chain is broken, the task is NOT done.** Common failures:

- XAML exists but code-behind not wired (dead buttons)
- Backend processes data but UI never reads it (invisible feature)
- Tests pass but feature doesn't work end-to-end (integration gap)
- Config saved but never reloaded (settings don't take effect)

### UI integration: definition of done

A UI task is **not done** until all of the following are true:

1. **XAML exists** — controls are laid out and styled.
2. **Code-behind is wired** — every named control (`x:Name`) is referenced in `.axaml.cs` with correct event handlers, data population, and state updates.
3. **Buttons do something** — every `Button`, `ToggleButton`, and interactive control has a working `Click`/event handler that performs its intended action. No dead buttons.
4. **Data is live** — status panels, lists, and stats display real data from Core/Bridge, not placeholder text that never updates.
5. **Build clean** — `dotnet build` with 0 errors, 0 warnings.
6. **Smoke tested** — `dotnet run --project src/Hermes.App` launches, the window renders, and the agent has verified interactable elements respond (or documented which require external dependencies like Ollama/Gmail).

**Do not mark a UI task as complete if controls exist in XAML but are not connected to behaviour.** A button that does nothing is worse than no button — it erodes user trust. If wiring requires infrastructure not yet built, either stub it with a visible "not yet implemented" message or defer the entire control to a later phase.

### General workflow

1. Read the current phase spec before implementing.
2. Check `.project/design/07-open-questions.md` for resolved decisions.
3. Update the testing register when tests change.
4. Use the commit prompt (`.github/prompts/commit.prompt.md`) for clean commits.
5. Run `dotnet build` and `dotnet test` before committing.
6. Check code against the idiom standards before presenting — fix violations first.
7. When a phase includes UI work, verify the full definition of done above before marking complete.
