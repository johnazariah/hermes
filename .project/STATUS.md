# Hermes — Project Status

**Phase**: Phase 0 complete, Phase 1+ ready  
**Health**: 🟢 Green  
**Last Updated**: 2026-03-27

## Quick Stats

| Metric | Value |
|--------|-------|
| Documents (design) | 8 |
| Phase specs | 10 (Phases 0–9) |
| GitHub issues | 10 |
| Tests | 21 (11 config, 10 database) |
| Lines of code | ~900 |

## Phase Status

| Phase | Name | Status | Issue |
|-------|------|--------|-------|
| 0 | Project Skeleton | ✅ Done | [#1](https://github.com/johnazariah/hermes/issues/1) |
| 1 | Email Sync | ✅ Done | [#2](https://github.com/johnazariah/hermes/issues/2) |
| 2 | Classification | ✅ Done | [#3](https://github.com/johnazariah/hermes/issues/3) |
| 3 | Text Extraction | ✅ Done | [#4](https://github.com/johnazariah/hermes/issues/4) |
| 4 | Full-Text Search | 📝 Not Started | [#5](https://github.com/johnazariah/hermes/issues/5) |
| 5 | Embeddings | 📝 Not Started | [#6](https://github.com/johnazariah/hermes/issues/6) |
| 6 | MCP Server | 📝 Not Started | [#7](https://github.com/johnazariah/hermes/issues/7) |
| 7 | Background Service | 📝 Not Started | [#8](https://github.com/johnazariah/hermes/issues/8) |
| 8 | Avalonia UI & Installer | 📝 Not Started | [#9](https://github.com/johnazariah/hermes/issues/9) |
| 9 | Folder Watching | ✅ Done | [#10](https://github.com/johnazariah/hermes/issues/10) |

## Key Decisions

All 13 open questions resolved — see `.project/design/07-open-questions.md`.

- **Runtime**: .NET 10 / F# + C# (Avalonia)
- **AI**: Ollama (auto-installed) + ONNX Runtime fallback + Azure Document Intelligence
- **MCP**: Service IS the MCP server (streamable HTTP on localhost)
- **UI**: Avalonia tray icon + shell window
- **Archive**: `~/Documents/Hermes/`
