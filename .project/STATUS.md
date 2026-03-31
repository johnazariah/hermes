# Hermes — Project Status

**Phase**: All phases + features complete. Settings dialog in progress.  
**Health**: 🟢 Green  
**Last Updated**: 2026-03-31

## Quick Stats

| Metric | Value |
|--------|-------|
| Documents (design) | 12 |
| Phase specs | 12 (Phases 0–11) |
| GitHub issues | 10 |
| Tests | 258 |
| Lines of code | ~12,000+ |

## Phase Status

| Phase | Name | Status | Issue |
|-------|------|--------|-------|
| 0 | Project Skeleton | ✅ Done | [#1](https://github.com/johnazariah/hermes/issues/1) |
| 1 | Email Sync | ✅ Done | [#2](https://github.com/johnazariah/hermes/issues/2) |
| 2 | Classification | ✅ Done | [#3](https://github.com/johnazariah/hermes/issues/3) |
| 3 | Text Extraction | ✅ Done | [#4](https://github.com/johnazariah/hermes/issues/4) |
| 4 | Full-Text Search | ✅ Done | [#5](https://github.com/johnazariah/hermes/issues/5) |
| 5 | Embeddings | ✅ Done | [#6](https://github.com/johnazariah/hermes/issues/6) |
| 6 | MCP Server | ✅ Done | [#7](https://github.com/johnazariah/hermes/issues/7) |
| 7 | Background Service | ✅ Done | [#8](https://github.com/johnazariah/hermes/issues/8) |
| 8 | Avalonia UI & Installer | ✅ Done (functional) | [#9](https://github.com/johnazariah/hermes/issues/9) |
| 9 | Folder Watching | ✅ Done | [#10](https://github.com/johnazariah/hermes/issues/10) |
| 10 | Email Body Indexing | ✅ Done | — |
| 11 | Document-to-Markdown | ✅ Done | — |
| — | UI Redesign | 📐 Designing | See [09-ui-redesign.md](design/09-ui-redesign.md) |
| — | Agent Evolution | 📐 Designing | See [10-agent-evolution.md](design/10-agent-evolution.md) |
| — | Email Backfill | ✅ Done | Paginated Gmail sync, resume from page token, progress tracking |
| — | Bills & Reminders | ✅ Done | Bill detection, reminder lifecycle, MCP tools, CLI reset |
| — | Azure OpenAI Chat | ✅ Done | Chat provider abstraction + Azure OpenAI implementation |

## Key Decisions

All 13 open questions resolved — see `.project/design/07-open-questions.md`.

- **Runtime**: .NET 10 / F# + C# (Avalonia)
- **AI**: Ollama (auto-installed) + ONNX Runtime fallback + Azure Document Intelligence
- **MCP**: Service IS the MCP server (streamable HTTP on localhost)
- **UI**: Avalonia tray icon + shell window
- **Archive**: `~/Documents/Hermes/`
