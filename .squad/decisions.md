# Squad Decisions

## Active Decisions

### 2026-07-10T17:19:34+10:00: Hermes domain context loaded — charters updated
**By:** Keaton
**What:** All agent charters updated from Pelican accounting domain to Hermes document intelligence domain. Routing table updated. Identity updated.
**References:** .squad/agents/keaton/charter.md, .squad/agents/mcmanus/charter.md, .squad/agents/verbal/charter.md, .squad/agents/hockney/charter.md, .squad/agents/fenster/charter.md, .squad/agents/kobayashi/charter.md, .squad/routing.md, .squad/identity/now.md
**Why:** Squad was ported from Pelican; all charters had stale domain references (GST, AccountCode, BAS, banking, journal lines). Now reflect the actual hermes architecture: document intelligence pipeline (ingest → Extract → Comprehend → Embed), Document property bag model, SQLite + FTS5 + sqlite-vec index, MCP Streamable HTTP server, React web UI.

### 2026-07-10T13:56:11+10:00: User directive — Silver-Thread Protocol
**By:** John Azariah (via Copilot)
**What:** Every new feature MUST follow the silver-thread protocol: build a minimal vertical slice proving the full stack works before expanding scope.
**Why:** User request — this was the founding development methodology and should remain the team's standard approach.

## Silver-Thread Protocol

When implementing any new feature or capability, the first deliverable is a **minimal vertical slice** that proves the full stack end-to-end:

1. **API endpoint** — A single route that accepts input and returns a response
2. **Domain logic** — The core F# handler with proper types (Document property bags, typed decode<'T>)
3. **Storage** — Persistence through SQLite (document store, FTS5 index, vector embeddings)
4. **Integration test** — At least one test proving the slice works against real storage
5. **Benchmark** (optional but preferred) — Baseline latency measurement

**Only after the silver thread is green** should the feature be expanded (additional endpoints, edge cases, UI, etc.).

**Team implications:**
- McManus: First commit for any feature is the silver thread, not the full implementation
- Hockney: Tests are part of the thread, not a follow-up — write them alongside implementation
- Verbal: Frontend comes AFTER the silver thread proves the API works
- Keaton: Review silver threads before approving expansion

### 2026-07-10T13:59:14+10:00: User directive — Coding Principles (formalized from archived guidelines)
**By:** John Azariah (via Copilot)
**What:** The F# coding principles from `.project/archive/analysis/11-coding-principles.md` are active team rules, not just historical documentation.
**Why:** User confirmed these archived guidelines should be formalized as team constraints.

## F# Coding Principles — MUST FOLLOW

### 1. Inside-Out Development
Build from the **Iron Core outward**. The innermost layer is pure, idiomatic F# — immutable types, small functions, pipeline composition, monadic error handling. Each successive layer adds one concern (validation, security, I/O) without contaminating the core.

### 2. Small Functions, Clean Pipelines
- Functions do ONE thing. ~10 lines of logic max.
- If a function has two responsibilities, split it.
- Compose with `|>`, `>>`, and `Result.bind`.
- Pipeline direction reflects data flow — read top-to-bottom.

### 3. Monadic Composition with Result CE
- Business logic uses `Result<'a, Error>` for all expected failures.
- Use `result {}` computation expression (and `task {}` for I/O boundaries).
- **Exceptions are for infrastructure faults ONLY** — network failures, serialisation bugs, corrupted state.
- Never for business rules.

### 4. Modules Over Classes
- F# modules with `let` bindings. No classes unless required by framework interop (ASP.NET, host builders).
- No inheritance. No mutable state.

### 5. Explicit Data Flow — No Hidden State
- Functions take inputs and return outputs.
- No ambient context, no thread-local storage, no mutable singletons.
- Dependencies flow through function parameters or Tagless-Final capability records.

### 6. Performance Targets
- Pipeline throughput: documents processed without bottlenecks at each stage
- GPU lock contention: burst-hold pattern keeps Ollama responsive
- Search latency: FTS5 and vector queries return quickly for interactive use

## Team Implications
- **McManus:** Every function ≤ 10 lines, pipeline-first, Result types for errors, no classes
- **Keaton:** Review for these principles — reject code with classes, mutable state, or long functions
- **Hockney:** Test pure core functions in isolation; property tests for invariants

### 2026-07-10T13:59:14+10:00: User directive — Testing Strategy (formalized from archived guidelines)
**By:** John Azariah (via Copilot)
**What:** The Hermes testing strategy is the active standard: xUnit + FsCheck for F# tests, Playwright for UI end-to-end coverage, and repository-native commands (`dotnet build`, `dotnet test`, `npx playwright test`) instead of script wrappers.
**Why:** User confirmed these guidelines should be formalized as team constraints and aligned to Hermes' actual toolchain.

## Testing Strategy — MUST FOLLOW

### Layer Architecture

```
Playwright E2E        (browser → UI → API)                        src/Hermes.Web
Integration           (F# → SQLite, real service boundaries)      dotnet test
Unit/Property         (pure F# — xUnit + FsCheck, no I/O)         dotnet test
```

### Rules

1. **Unit/property tests have NO I/O** — pure F#, no storage, no network.
2. **Integration tests** validate F# code against real SQLite and concrete integration seams; external services are faked or stubbed in CI.
3. **Playwright E2E** tests verify browser → UI → API flows from `src/Hermes.Web`.
4. **85% line / 60% branch coverage** targets enforced.
5. **Run `dotnet build` and `dotnet test`** before declaring work complete.
6. **No script runners** (`test.sh`, `pr-check.sh`) — use repository commands directly.

### Test Commands

| Command | Purpose |
|---------|---------|
| `dotnet build` | Build all Hermes projects |
| `dotnet test` | Run F# unit/property and integration tests |
| `dotnet test --collect:"XPlat Code Coverage"` | Run F# tests with coverage |
| `cd src/Hermes.Web && npx playwright test tests/smoke.spec.ts` | Run Playwright smoke / E2E tests |

### Test Naming Convention
- `Module_Function_Condition_ExpectedResult` style
- Framework: xUnit with `[<Fact>]` and `[<Theory>]`
- Property-based: FsCheck.Xunit with `[<Property>]`

## Team Implications
- **Hockney:** Tests MUST be in the correct layer. New domain tests → Unit/Property (pure, no I/O). Integration → real SQLite where appropriate.
- **McManus:** Run `dotnet build` and `dotnet test` before declaring work complete.
- **Fenster:** CI must maintain the layer separation. No shortcuts that collapse layers.
- **Keaton:** Review test placement — reject integration tests masquerading as unit tests.

## Archived (from Pelican)

> These decisions applied to the Pelican accounting project. Retained for historical reference.

### 2026-07-09T22-57-27: Architectural constraints (Pelican-era)
**By:** Keaton
**What:** Pelican architectural constraints — dependency rules, money types, Australian tax, event sourcing, journal engine, tenant isolation.
**References:** .project/mvp/00-index.md through .project/mvp/11-decisions-register.md

### 2026-07-10T09-01-05+10-00: Bank feeds & BAS infrastructure (Pelican-era)
**By:** Keaton, McManus
**What:** Assessment of bank feeds (E069) and BAS lodgement (E063) readiness in the Pelican accounting engine.

### 2026-07-10: BAS G-field disaggregation implementation (Pelican-era)
**By:** McManus
**What:** Wired MonthlyTaxProjection data into activity statement G-fields via `Bas.compute`.

### 2026-07-10: BAS G-field test coverage — 18 new tests (Pelican-era)
**By:** Hockney
**What:** Added 18 tests to BasTests.fs for Pelican BAS reporting.

### 2026-07-10: macOS installer fixes for Issue #134 (Pelican-era)
**By:** Fenster
**What:** Fixed 5 bugs in macOS .pkg installer for Pelican.

### 2026-07-09T23-06-09: Contacts list page enhanced (Pelican-era)
**By:** Verbal
**What:** Enhanced Contacts.tsx with AR/AP amounts for Pelican UI.

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
