# Hermes — Copilot Instructions

## Working Style

**Default: discuss, don't code.** Propose changes and wait for approval before implementing. When asked to implement, proceed with full context.

## Documentation

Before creating or modifying any project documentation, read `.project/GOVERNANCE.md`.

- **Project status**: `.project/STATUS.md` (read first — 50 lines)
- **Active journal**: `.project/waves/*.md` (exactly one `⏳ Active` wave during implementation)
- **Architecture**: `.project/design/30-pipeline-v5-architecture.md` (canonical)
- **Comprehension**: `.project/design/24-comprehension-stage.md`
- **Design reference**: `.project/design/*.md` (what/why, not status)

### Daily journal (mandatory)

At the end of every coding session, run `.github/prompts/daily-journal.prompt.md`.

- Add one new dated entry directly below `## Log` in the active wave.
- Never edit or delete older journal entries.
- Do not update `.project/STATUS.md` during routine session sync.
- Update `STATUS.md` only during an explicit wave transition via `.github/prompts/post-wave-update.prompt.md`.
- If no wave is active, stop and surface that governance gap instead of writing to a completed wave.

## Project Overview

Hermes is a local-first document intelligence service. It ingests documents from email and local folders, understands them through LLM comprehension, and exposes structured knowledge via MCP server and web UI.

| Concept       | Description                                                               |
| ------------- | ------------------------------------------------------------------------- |
| **Archive**   | File-first account/thread and local folders; content stays readable       |
| **Pipeline**  | V5 DAG: extract → triage/deep-comprehend and extract → embed              |
| **Document**  | SQLite metadata plus file-backed extraction/comprehension artifacts       |
| **Workflow**  | `stage_completions` ledger, declared dependencies, gates, GPU scheduling  |
| **Index**     | SQLite + FTS5 (keyword) + sqlite-vec (semantic)                           |
| **MCP**       | Streamable HTTP on `localhost:21741` (prod) / `21742` (dev)               |

## Architecture

```
Gmail ───────┐                                ┌─→ REST / preview React UI
Outlook ─────┼─→ file-first archive → V5 DAG ┼─→ MCP / Osprey
Folder watch ┘                  │             └─→ FTS5 + vector indexes
                               └─ extract → triage → deep-comprehend
                                          └──────→ embed
```

## Technology Stack

| Component  | Choice                                               |
| ---------- | ---------------------------------------------------- |
| Runtime    | .NET 9, F#                                           |
| UI         | React 19 + Vite + Tailwind                           |
| Database   | SQLite via `Microsoft.Data.Sqlite`                   |
| Email      | `Google.Apis.Gmail.v1`                               |
| PDF        | PdfPig (`UglyToad.PdfPig`)                           |
| LLM        | Configurable Ollama/Azure triage and comprehension   |
| Embeddings | Ollama nomic-embed-text                              |
| Pipeline   | Declarative DAG with channel and batch stages        |
| Testing    | xUnit + FsCheck (F#), Playwright (UI)                |
| Logging    | Serilog                                              |

## Solution Structure

```
src/
├── Hermes.Core/          F# library — pipeline, extraction, comprehension, DB
├── Hermes.Service/       F# service — HTTP API, MCP server, pipeline host
├── Hermes.Web/           React 19 — canonical preview UI
├── Hermes.Tray/          Windows tray — preview native shell
├── Hermes.UI/            Blazor components — excluded from support
└── Hermes.Shell/         MAUI Windows/macOS — excluded from support
tests/
├── Hermes.Tests/         xUnit + FsCheck (867 discovered)
└── Hermes.Web/tests/     21 Playwright definitions; runner wiring pending
.project/
├── STATUS.md             Project dashboard
├── waves/                Active and completed work journals
├── design/               Current architecture references
└── archive/              Superseded design and planning material
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

### C# (Tray, Blazor components, and MAUI shell)

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

| File                                             | Purpose                                |
| ------------------------------------------------ | -------------------------------------- |
| `.project/STATUS.md`                             | Canonical project-state hub            |
| `.project/waves/wave-v5-stabilization.md`        | Active tasks, decisions, and journal   |
| `.project/design/30-pipeline-v5-architecture.md` | Current architecture                   |
| `.project/design/28-file-first-archive.md`       | Archive and SQLite storage boundary    |
| `.project/design/24-comprehension-stage.md`      | Triage and deep-comprehension design   |
| `.project/testing-register.md`                   | Test catalog and execution baseline    |

## Idiom Standards (Always Active)

All AI-generated code in this project **must** conform to the language idiom standards. Full standards are in `devex-toolkit` (multi-root workspace peer). Key rules summarised here for when devex-toolkit is unavailable:

- **F# (Hermes.Core):** Small functions (≤20 lines), `|>` pipelines, no `mutable`, DUs over strings, `Option.map`/`bind`/`defaultValue` over explicit match, `task {}` blocks ≤15 lines, Tagless-Final with records-of-functions, partial application (stable params first), active patterns for complex matching.
- **C# (Tray/UI/Shell):** Records over classes, `sealed` by default, pattern matching over `if/else`, LINQ for transformations, non-nullable, `CancellationToken` on all async methods, Tagless-Final with capability interfaces, list/relational/property patterns.
- **Architecture:** Tagless-Final as default architecture, fakes over mocks, capability records parameterised over effect type.

For dedicated write/review/refactor workflows, invoke the `@fsharp-dev` or `@csharp-dev` agents.

## Agent Workflow Requirements

### Language agent delegation (mandatory)

When writing, reviewing, or refactoring code:

- **F# code** (`Hermes.Core`, `Hermes.Tests`): delegate to `@fsharp-dev`. Do not write F# without it.
- **C# code** (`Hermes.Tray`, `Hermes.UI`, `Hermes.Shell`): delegate to `@csharp-dev`. Do not write C# without it.
- The language agents enforce idiom standards, catch anti-patterns, and produce higher quality code than unguided generation.

### Silver thread principle (mandatory)

Every feature must be implemented as a **silver thread** — an unbroken chain from UI to backend and back:

```
User action (button click, query, trigger)
  → Processing (F# Core logic, DB queries, API calls)
    → State change (database, config, in-memory)
      → Presentation (React query/component state or native-shell state)
        → UI response (user sees the result)
```

**Before marking any task complete, trace the full thread:**

1. **Input**: What triggers this feature? (file drop, button click, sync cycle, MCP call)
2. **Processing**: What backend code runs? (extraction, classification, DB update)
3. **Presentation**: What data flows to the UI? (query result, component state, native-shell status)
4. **Output**: What does the user see? (document in list, activity log entry, badge update, chat response)

**If ANY link in this chain is broken, the task is NOT done.** Common failures:

- A route or control exists but is not wired (dead buttons/links)
- Backend processes data but UI never reads it (invisible feature)
- Tests pass but feature doesn't work end-to-end (integration gap)
- Config saved but never reloaded (settings don't take effect)

### UI integration: definition of done

A UI task is **not done** until all of the following are true:

1. **Route/component exists** — the canonical React surface is laid out and styled.
2. **API/state is wired** — queries, mutations, loading, error, and empty states use the real Service contract.
3. **Controls work** — every interactive control performs its stated action; no dead buttons or links.
4. **Data is live** — status panels, lists, and stats display real Service data, not permanent placeholders.
5. **Build clean** — `npm ci`, React build/type-check, lint, and relevant .NET builds pass.
6. **Smoke tested** — Playwright runs against an isolated `Hermes.Service` and verifies root/deep routes and primary actions.

**Do not mark a UI task complete if controls render without behaviour.** If wiring requires infrastructure not yet built, show a visible unavailable state or defer the control.

### General workflow

1. Read `.project/STATUS.md`, then the active wave and its linked design docs before implementing.
2. Keep status in the wave journal, not in design docs or prompts.
3. Update the testing register when tests change.
4. Use the commit prompt (`.github/prompts/commit.prompt.md`) for clean commits.
5. Run `dotnet build` and `dotnet test` before committing.
6. Check code against the idiom standards before presenting — fix violations first.
7. When a phase includes UI work, verify the full definition of done above before marking complete.
8. Run `.github/prompts/daily-journal.prompt.md` before ending the session.
