# Squad Identity — hermes

## Origin
This squad was ported from the Pelican project (C:\work\pelican) on 2026-07-10.
Same team, same process rules, fresh domain context.

## What We Already Know (from decisions.md)
- **Silver-Thread Protocol:** Every new feature starts with a minimal vertical slice proving the full stack works before expanding.
- **F# Coding Principles:** Inside-out development, functions ≤10 lines, pipeline composition, Result CE for errors, modules over classes, no mutable state, explicit data flow.
- **Testing Strategy:** xUnit + FsCheck (700 tests), Playwright (9 UI tests). 85% line / 60% branch coverage targets.
- **Type Discipline:** No primitive obsession — Document property bags with typed decode<'T>, DUs for stages and document types.
- **Error Handling:** Result types over exceptions. invalidArg only for programmer errors.

## Domain Context — Loaded ✅

All agent charters updated to reflect the Hermes domain:
- **Pipeline:** ingest → Extract → Comprehend → Embed (Channel<Document> stages)
- **Document:** Map<string, obj> property bag — stages add keys, never remove
- **Workflow:** runStage monad — idempotency, write-aside, GPU lock, error handling
- **Index:** SQLite + FTS5 (keyword) + sqlite-vec (semantic)
- **MCP:** Streamable HTTP on localhost:21741 (prod) / 21742 (dev)
- **Archive:** ~/Documents/Hermes/ — files in unclassified/, never moved
- **Tech:** .NET 10, F#, React 19, SQLite, Ollama (llama3:8b + nomic-embed-text), PdfPig, Serilog

## Team
| Name | Role | Focus |
|------|------|-------|
| 🏗️ Keaton | Lead / Architect | Architecture, domain compliance, code review |
| 🔧 McManus | Backend Dev | F# pipeline, extraction, comprehension, MCP tools |
| ⚛️ Verbal | Frontend Dev | React 19 web UI |
| 🧪 Hockney | Tester | All test layers, coverage |
| ⚙️ Fenster | DevOps / Infra | CI/CD, infrastructure |
| 📊 Kobayashi | Product Manager | Priorities, milestones, user stories |

## Current Focus
Domain context loaded. Charters updated. Ready for work on the hermes roadmap — comprehension stage implementation is the critical path.
