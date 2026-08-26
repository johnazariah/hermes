# Hermes — Learnings

> Patterns and gotchas discovered during development. Updated as we go.

## Categories

### Architecture

#### Parallel agents need separate worktrees
- **Learning**: Spawning multiple Copilot background agents that build the same worktree causes catastrophic performance — multiple `fsc.dll` processes (500MB+ each) compete for CPU and file locks, turning a 14-second build into 12+ minutes.
- **Pattern**: Only spawn a fleet of parallel agents when each agent has its own `git worktree`. If agents share a worktree, run them sequentially.
- **Rationale**: The F# compiler (`fsc`) does not handle concurrent compilation of the same project. MSBuild's obj/bin directories become contention points. Even `--no-restore` builds hang when another `fsc` is writing to the same output.
- **Source**: Phases 1–9 parallel implementation, 2026-03-27/28

#### Shared test infrastructure prevents compile-time explosion
- **Learning**: Duplicating mock algebra construction (records-of-functions) across 12 test files caused ~930 lines of identical boilerplate. While not the primary cause of slow builds (that was concurrency), it made files harder to maintain and increased type inference work.
- **Pattern**: Create a single `TestHelpers.fs` with typed mock factories (`memFs()`, `createDb()`, `silentLogger`, etc.) as the first file in the test project's compile order. All tests import from it.
- **Rationale**: F# compile order is linear — shared helpers at the top benefit every downstream file. Explicit type annotations on algebra record construction help `fsc` resolve types faster.
- **Source**: Test refactor, 2026-03-29

### Pipeline

#### Pipeline V5 compatibility must stay file-backed
- **Learning**: Reusing V4 processors inside the V5 DAG is safe only when compatibility documents hydrate metadata and processors read extracted/comprehension content from archive artifacts.
- **Pattern**: Keep V5 output tables metadata-only, use `saved_path` to resolve artifacts, and treat legacy `documents` updates as API/UI projections.
- **Rationale**: Restoring dropped content columns would make SQLite the content source again and break file-first recovery.
- **Source**: Pipeline V5 and file-first integration, 2026-05-08

### Ollama / AI
_No entries yet._

### SQLite / FTS5 / sqlite-vec
_No entries yet._

### Cross-Platform

#### CI runner toolchains must be selected explicitly
- **Learning**: Hosted macOS runner defaults can move ahead of the Xcode version supported by the installed MAUI workload.
- **Pattern**: Select the required Xcode installation explicitly before installing/building the MacCatalyst workload.
- **Rationale**: Pinning Xcode 26.5 made the shell build deterministic when the runner default changed to 26.6.
- **Source**: PR #14, 2026-08-11

#### Binary fixtures need tracked allow-list rules
- **Learning**: Broad document ignore rules hid positive PPTX fixtures, so tests passed locally but failed in clean CI checkouts.
- **Pattern**: Keep generated documents ignored, then add narrow negation rules for required synthetic fixtures and mark binary formats in `.gitattributes`.
- **Rationale**: A fixture-dependent test is reproducible only when the fixture is versioned.
- **Source**: PR #14, 2026-08-11

### Gmail API
_No entries yet._

### .NET / F# Gotchas

#### Nullable warnings are errors
- **Learning**: With nullable annotations enabled and `TreatWarningsAsErrors`, F# interop with nullable BCL APIs must be explicit. `box x` returns `obj | null`, `Path.GetDirectoryName()` returns `string | null`, and `Assembly.GetEntryAssembly()` returns nullable.
- **Pattern**: Use `Database.boxVal` helper instead of raw `box`. Pattern match on nullable returns. Use `Option.ofObj` for BCL methods that return nullable strings.
- **Rationale**: Without this, every interaction with .NET BCL APIs produces a compile error.
- **Source**: Phase 0 implementation, 2026-03-27_

<!-- Entry format:
### Short Title
- **Learning**: What we discovered
- **Pattern**: The rule or practice that follows
- **Rationale**: Why this matters
- **Source**: Where/when this was discovered (issue, PR, date)
-->
