# Hermes — Project Status

**Phase**: Phases 0-7, 9 complete. Only Phase 8 (UI) remains.  
**Health**: 🟢 Green  
**Last Updated**: 2026-03-27

## Quick Stats

| Metric | Value |
|--------|-------|
| Documents (design) | 8 |
| Phase specs | 10 (Phases 0–9) |
| GitHub issues | 10 |
| Tests | 191 |
| Lines of code | ~8,000+ |

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
| 8 | Avalonia UI & Installer | 📝 Not Started | [#9](https://github.com/johnazariah/hermes/issues/9) |
| 9 | Folder Watching | ✅ Done | [#10](https://github.com/johnazariah/hermes/issues/10) |

## Key Decisions

All 13 open questions resolved — see `.project/design/07-open-questions.md`.

- **Runtime**: .NET 10 / F# + C# (Avalonia)
- **AI**: Ollama (auto-installed) + ONNX Runtime fallback + Azure Document Intelligence
- **MCP**: Service IS the MCP server (streamable HTTP on localhost)
- **UI**: Avalonia tray icon + shell window
- **Archive**: `~/Documents/Hermes/`
