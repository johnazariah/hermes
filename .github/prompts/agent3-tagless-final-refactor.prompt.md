---
description: "Surgical refactor: fix 8 Tagless-Final violations — move all I/O behind algebras, composition to root."
---

# Tagless-Final Refactor — Surgical Fix of 8 Violating Files

**Branch**: `feat/tagless-final-cleanup`

**IMPORTANT: Use a git worktree — do NOT work in the main checkout.**
```
cd c:\work\hermes
git worktree add ..\hermes-tagless feat/tagless-final-cleanup 2>/dev/null || git worktree add ..\hermes-tagless -b feat/tagless-final-cleanup
cd c:\work\hermes-tagless
```

All commands run in `c:\work\hermes-tagless`.

**Rules**:
- Read `.github/copilot-instructions.md` — especially idiomatic F# standards and Tagless-Final architecture
- Use `@fsharp-dev` for ALL F# code. Do not write F# without it.
- Build + test after EVERY file: `dotnet build hermes.slnx --nologo && dotnet test tests/Hermes.Tests/Hermes.Tests.fsproj --nologo --no-build`
- Expected baseline: 418 tests, 0 failures. Must stay green after each refactor.
- Commit after each file refactor.
- **Do NOT change public function signatures** unless necessary to add an algebra parameter. Callers must be updated in the same commit.

---

## The Problem

8 files in `Hermes.Core` bypass the Tagless-Final pattern by using concrete I/O directly instead of going through `Algebra.fs` records. This makes them untestable with fakes. The principle: **Core modules must NEVER perform I/O directly. All effects go through algebra parameters.**

---

## Pre-work: Add Missing Algebras

Before refactoring the files, add 2 missing algebra types to `src/Hermes.Core/Algebra.fs`:

```fsharp
// After the existing FileSystem type
type Environment =
    { homeDirectory: unit -> string
      configDirectory: unit -> string
      documentsDirectory: unit -> string }

// After the existing OllamaClient type  
type TokenStore =
    { save: string -> string -> Task<unit>
      load: string -> Task<string option>
      exists: string -> Task<bool> }
```

Add production interpreters to `Interpreters` module:
```fsharp
let systemEnvironment : Algebra.Environment =
    { homeDirectory = fun () -> System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)
      configDirectory = fun () -> 
          if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
              Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "hermes")
          else
              Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".config", "hermes")
      documentsDirectory = fun () -> System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments) }
```

Add fakes to `tests/Hermes.Tests/TestHelpers.fs`:
```fsharp
let fakeEnvironment (home: string) (config: string) (docs: string) : Algebra.Environment =
    { homeDirectory = fun () -> home
      configDirectory = fun () -> config
      documentsDirectory = fun () -> docs }
```

**Commit**: `refactor: add Environment and TokenStore algebras with production + test interpreters`

---

## Refactor 1: Stats.fs — Direct filesystem → FileSystem algebra

**Current**: `File.Exists`, `Directory.Exists`, `Directory.GetDirectories`, `Directory.GetFiles` called directly.

**Fix**: Add `fs: Algebra.FileSystem` parameter to functions that do I/O:
- `getCategoryCounts` needs `fs` parameter — replace `Directory.Exists` → `fs.directoryExists`, `Directory.GetDirectories` → needs new `getDirectories` on FileSystem algebra (add it: `getDirectories: string -> string array`).
- `getIndexStats` needs `fs.fileExists` instead of `File.Exists` for `dbPath`.

Update all callers (ShellViewModel.cs, ServiceHost.fs) to pass `fs`.

**Proof**: Existing Stats tests still pass. New test: `getCategoryCounts` with memFs returns expected counts.

**Commit**: `refactor: Stats.fs — replace direct filesystem calls with FileSystem algebra`

---

## Refactor 2: Config.fs — Environment.GetFolderPath → Environment algebra

**Current**: 5× `Environment.GetFolderPath(SpecialFolder.*)` hardcoded in `configDir`, `defaultArchiveDir`, `expandHome`.

**Fix**: 
- `configDir` becomes `configDir (env: Algebra.Environment) = env.configDirectory()`
- `defaultArchiveDir` becomes `defaultArchiveDir (env: Algebra.Environment) = Path.Combine(env.documentsDirectory(), "hermes")`
- `expandHome` takes `env` parameter for home directory lookup

Update all callers to pass `env` (or use `Interpreters.systemEnvironment` at composition roots).

**Proof**: `Config_ExpandHome` tests pass with fakeEnvironment.

**Commit**: `refactor: Config.fs — replace Environment.GetFolderPath with Environment algebra`

---

## Refactor 3: Embeddings.fs — Clock + HttpClient

**Current**: `DateTimeOffset.UtcNow` on lines 168, 263. `new HttpClient()` on line 179.

**Fix**:
- Add `clock: Algebra.Clock` parameter to functions that use `UtcNow`. Replace `DateTimeOffset.UtcNow` → `clock.utcNow()`.
- The `HttpClient` construction is inside the `EmbeddingClient` factory. This is OK at the composition root level — but verify the factory is only called once (not per-request). If it creates a new client per call, refactor to capture a shared client.

Update callers to pass `clock`.

**Proof**: Existing embedding tests pass. Time-dependent behaviour testable with fake clock.

**Commit**: `refactor: Embeddings.fs — use Clock algebra instead of DateTimeOffset.UtcNow`

---

## Refactor 4: McpTools.fs — Clock

**Current**: `DateTimeOffset.UtcNow` on lines 314, 349.

**Fix**: Add `clock: Algebra.Clock` parameter to `listReminders` and `updateReminder` tool handlers. Pass from `McpServer.handleToolCall`.

**Proof**: MCP tests pass. Time in reminder operations comes from clock.

**Commit**: `refactor: McpTools.fs — inject Clock algebra for time-dependent operations`

---

## Refactor 5: Rules.fs — Async.RunSynchronously

**Current**: Line 325 — `loadRules () |> Async.AwaitTask |> Async.RunSynchronously |> ignore` blocks during initialization.

**Fix**: Make `fromFile` return `Task<Algebra.RulesEngine>` instead of doing sync initialization. The caller (`ServiceHost` or `HermesServiceBridge`) awaits it during async startup.

```fsharp
// Before: 
let fromFile (fs: Algebra.FileSystem) (logger: Algebra.Logger) (rulesPath: string) : Algebra.RulesEngine =
    // ...sync load...

// After:
let fromFile (fs: Algebra.FileSystem) (logger: Algebra.Logger) (rulesPath: string) : Task<Algebra.RulesEngine> =
    task {
        let! rules = loadRules fs rulesPath
        // ... build engine ...
        return engine
    }
```

Update callers to `let! rules = Rules.fromFile ...` in their async context.

**Proof**: Tests pass. No `Async.RunSynchronously` anywhere in Core.

**Commit**: `refactor: Rules.fs — async initialization, remove Async.RunSynchronously`

---

## Refactor 6: Chat.fs — HttpClient lifetime

**Current**: `use client = new HttpClient(...)` inside `ollamaProvider` and `azureOpenAIProvider` factories — creates + disposes a client per API call.

**Fix**: The `ChatProvider` algebra is correct. The factories should capture a long-lived `HttpClient`:

```fsharp
let ollamaProvider (client: HttpClient) (baseUrl: string) (model: string) : Algebra.ChatProvider =
    { complete = fun systemMsg userMsg ->
        task {
            // use the passed-in client, don't create a new one
        } }
```

Create the shared `HttpClient` at the composition root and pass it to the factory.

**Proof**: Chat tests pass with `fakeChatProvider`. Production code shares one client per provider.

**Commit**: `refactor: Chat.fs — accept shared HttpClient instead of constructing per-request`

---

## Refactor 7: SemanticSearch.fs — eprintfn

**Current**: Line 279 — `eprintfn $"Semantic search failed: {e}"` bypasses logging.

**Fix**: Add `logger: Algebra.Logger` parameter. Replace `eprintfn` → `logger.error`.

**Proof**: No `eprintfn` or `printfn` in Core (except Domain types `ToString` which is OK).

**Commit**: `refactor: SemanticSearch.fs — use Logger algebra instead of eprintfn`

---

## Refactor 8: ServiceHost.fs — Move composition to root

**Current**: `buildProductionDeps` constructs algebras by pattern-matching on config (embedder based on `config.Ollama.Enabled`, HttpClient creation, etc.). This is composition logic living inside Core.

**Fix**: 
- Delete `buildProductionDeps` from ServiceHost.fs
- Move all algebra construction to `HermesServiceBridge.cs` (App) and `Program.fs` (CLI)
- `ServiceHost.runSyncCycle` and `createServiceHost` receive fully-constructed algebras as parameters — they never see `HermesConfig` except for domain values (archive dir, sync interval)

This is the biggest refactor. Do it last because it touches the most callers.

**Proof**: All tests pass. `ServiceHost` has zero `new HttpClient`, zero `Environment.GetFolderPath`, zero `DateTimeOffset.UtcNow`. It only uses algebras.

**Commit**: `refactor: ServiceHost.fs — move all algebra construction to composition roots`

---

## Final Verification

```
dotnet build hermes.slnx --nologo    # 0 errors
dotnet test --nologo --no-build       # 418+ tests, 0 failures
```

Verify no Tagless-Final violations remain:
```
grep -rn "Environment.GetFolderPath\|DateTimeOffset.UtcNow\|DateTime.Now\|new HttpClient\|File.Exists\|Directory.Exists\|Async.RunSynchronously\|eprintfn" src/Hermes.Core/ --include="*.fs" | grep -v "Algebra.fs\|Interpreters\|Prelude.fs"
```

Should return zero matches.

```
git push -u origin feat/tagless-final-cleanup
```

Do NOT merge to master — await review.
