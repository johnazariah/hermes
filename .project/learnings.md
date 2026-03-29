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
_No entries yet._

### Ollama / AI
_No entries yet._

### SQLite / FTS5 / sqlite-vec
_No entries yet._

### Cross-Platform
_No entries yet._

### Gmail API
_No entries yet._

### .NET 10 F# Gotchas

#### Nullable warnings are errors
- **Learning**: .NET 10 F# treats nullable reference type warnings as errors by default (via `TreatWarningsAsErrors`). `box x` returns `obj | null`, `Path.GetDirectoryName()` returns `string | null`, `Assembly.GetEntryAssembly()` returns nullable.
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
