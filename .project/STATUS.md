# Hermes — Project Status

**Phase**: Pre-development (design complete, specs filed)  
**Health**: 🟢 Green  
**Last Updated**: 2026-03-27

## Quick Stats

| Metric | Value |
|--------|-------|
| Documents (design) | 8 |
| Phase specs | 10 (Phases 0–9) |
| GitHub issues | 10 |
| Tests | 0 (Phase 0 not started) |
| Lines of code | 0 |

## Phase Status

| Phase | Name | Status | Issue |
|-------|------|--------|-------|
| 0 | Project Skeleton | 📝 Not Started | [#1](https://github.com/johnazariah/hermes/issues/1) |
| 1 | Email Sync | 📝 Not Started | [#2](https://github.com/johnazariah/hermes/issues/2) |
| 2 | Classification | 📝 Not Started | [#3](https://github.com/johnazariah/hermes/issues/3) |
| 3 | Text Extraction | 📝 Not Started | [#4](https://github.com/johnazariah/hermes/issues/4) |
| 4 | Full-Text Search | 📝 Not Started | [#5](https://github.com/johnazariah/hermes/issues/5) |
| 5 | Embeddings | 📝 Not Started | [#6](https://github.com/johnazariah/hermes/issues/6) |
| 6 | MCP Server | 📝 Not Started | [#7](https://github.com/johnazariah/hermes/issues/7) |
| 7 | Background Service | 📝 Not Started | [#8](https://github.com/johnazariah/hermes/issues/8) |
| 8 | Avalonia UI & Installer | 📝 Not Started | [#9](https://github.com/johnazariah/hermes/issues/9) |
| 9 | Folder Watching | 📝 Not Started | [#10](https://github.com/johnazariah/hermes/issues/10) |

## Key Decisions

All 13 open questions resolved — see `.project/design/07-open-questions.md`.

- **Runtime**: .NET 9 / F# + C# (Avalonia)
- **AI**: Ollama (auto-installed) + ONNX Runtime fallback + Azure Document Intelligence
- **MCP**: Service IS the MCP server (streamable HTTP on localhost)
- **UI**: Avalonia tray icon + shell window
- **Archive**: `~/Documents/Hermes/`
