# Phase 0: Project Skeleton

**Status**: Not Started  
**Depends On**: —  
**Deliverable**: `hermes --version` works. `hermes init` creates config and database.

---

## Objective

Establish the .NET solution structure, build tooling, configuration loading, database initialisation, and test harness. Everything downstream depends on this phase.

---

## Solution Structure

```
hermes/
├── src/
│   ├── Hermes.Core/              # F# library — domain types, pipeline, DB, config
│   │   ├── Hermes.Core.fsproj
│   │   ├── Domain.fs             # Core domain types (Document, Message, Category, etc.)
│   │   ├── Config.fs             # YAML config loading (config.yaml, rules.yaml)
│   │   ├── Database.fs           # SQLite schema init, migrations, queries
│   │   └── Logging.fs            # Structured logging setup
│   ├── Hermes.App/               # Avalonia entry point — tray, shell window, service host
│   │   ├── Hermes.App.csproj     # C# for XAML bindings
│   │   └── Program.cs
│   └── Hermes.Cli/               # CLI entry point — thin wrapper calling Core
│       ├── Hermes.Cli.fsproj
│       └── Program.fs
├── tests/
│   └── Hermes.Tests/
│       ├── Hermes.Tests.fsproj
│       ├── ConfigTests.fs
│       └── DatabaseTests.fs
├── docs/
│   └── design/                   # Existing design docs
├── hermes.sln
├── .gitignore
├── README.md
└── Directory.Build.props         # Shared build properties
```

## Tasks

### 0.1 — Solution & Project Scaffolding
- [x] Create `hermes.sln` with projects: `Hermes.Core` (F#), `Hermes.App` (C#/Avalonia), `Hermes.Cli` (F#), `Hermes.Tests` (F#)
- [x] `Directory.Build.props` targeting `net10.0`, nullable enabled, warnings as errors
- [x] `.gitignore` for .NET, JetBrains, VS Code

### 0.2 — Configuration
- [x] Define F# record types for configuration: `HermesConfig`, `AccountConfig`, `OllamaConfig`, `WatchFolderConfig`
- [x] Load `config.yaml` via YamlDotNet, with defaults for missing fields
- [x] Cross-platform path resolution: `~/.config/hermes/` (macOS), `%APPDATA%\hermes\` (Windows)
- [x] `hermes init` command creates default `config.yaml` and `rules.yaml` in the config directory

### 0.3 — Database Initialisation
- [x] `Microsoft.Data.Sqlite` connection with WAL mode and foreign keys enabled
- [x] Schema creation: `messages`, `documents`, `sync_state` tables
- [x] FTS5 virtual table: `documents_fts` with triggers
- [x] Indexes on category, date, sender, sha256, account, source_type, extracted_at, embedded_at
- [x] Schema versioning (store version in a `schema_version` table for future migrations)
- [x] `hermes init` creates `db.sqlite` in the archive directory

### 0.4 — Logging
- [x] Serilog with file + console sinks
- [x] Structured JSON log format for file sink
- [x] Log directory: `{config_dir}/logs/`
- [x] Log rotation (daily, keep 14 days)

### 0.5 — CLI Entry Point
- [x] `System.CommandLine` or `Argu` (F#) for CLI parsing
- [x] `hermes --version` prints version
- [x] `hermes init` subcommand
- [x] Skeleton subcommands for future phases (sync, search, etc.) returning "not implemented"

### 0.6 — Testing
- [x] xUnit test project with FsCheck for property-based tests
- [x] Config loading tests (valid YAML, missing file, defaults)
- [x] Database init tests (schema created, tables exist, FTS5 works)
- [x] CI: `dotnet build` + `dotnet test` (GitHub Actions for macOS + Windows)

---

## NuGet Packages

| Package | Purpose |
|---------|---------|
| `Microsoft.Data.Sqlite` | SQLite access |
| `YamlDotNet` | YAML config parsing |
| `Serilog` + `Serilog.Sinks.File` + `Serilog.Sinks.Console` | Structured logging |
| `System.CommandLine` or `Argu` | CLI argument parsing |
| `Microsoft.Extensions.Hosting` | Host builder, DI, BackgroundService |
| `Microsoft.Extensions.DependencyInjection` | DI container |
| `xunit` + `FsCheck.Xunit` | Testing |

---

## Acceptance Criteria

- [x] `dotnet build` succeeds on macOS and Windows
- [x] `dotnet test` passes all tests
- [x] `hermes --version` prints version string
- [x] `hermes init` creates `config.yaml`, `rules.yaml`, `db.sqlite`
- [x] Database has all tables, indexes, FTS5 virtual table, and triggers
- [x] Config loads correctly from YAML with cross-platform paths
