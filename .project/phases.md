# Hermes — Phase Dependency Graph

> Machine-readable phase definitions for automated planning.
> An agent reads this file to determine what can run in parallel and what must go first.

## Phases

| Phase | Name | Branch | Spec | Depends On | Status |
|-------|------|--------|------|-----------|--------|
| 0 | Project Skeleton | `feat/0-project-skeleton` | [phase-0](specs/phase-0-project-skeleton.md) | — | Done |
| 1 | Email Sync | `feat/1-email-sync` | [phase-1](specs/phase-1-email-sync.md) | 0 | Done |
| 2 | Classification Pipeline | `feat/2-classification` | [phase-2](specs/phase-2-classification.md) | 0 | Done |
| 3 | Text Extraction | `feat/3-text-extraction` | [phase-3](specs/phase-3-text-extraction.md) | 2 | Done |
| 4 | Full-Text Search | `feat/4-fulltext-search` | [phase-4](specs/phase-4-fulltext-search.md) | 3 | Done |
| 5 | Embeddings & Semantic Search | `feat/5-embeddings` | [phase-5](specs/phase-5-embeddings.md) | 3 | Done |
| 6 | MCP Server | `feat/6-mcp-server` | [phase-6](specs/phase-6-mcp-server.md) | 4, 5 | Done |
| 7 | Background Service | `feat/7-background-service` | [phase-7](specs/phase-7-background-service.md) | 1, 2, 3 | Done |
| 8 | Avalonia UI & Installer | `feat/8-ui-installer` | [phase-8](specs/phase-8-ui-and-installer.md) | 7 | Done |
| 9 | Folder Watching | `feat/9-folder-watching` | [phase-9](specs/phase-9-folder-watching.md) | 2 | Done |
| 10 | Email Body Indexing | `feat/10-email-bodies` | [phase-10](specs/phase-10-email-body-indexing.md) | 1, 4 | Done |
| 11 | Document-to-Markdown | `feat/11-doc-to-markdown` | [phase-11](specs/phase-11-document-to-markdown.md) | 3, 5 | Done |

## Execution Waves

Phases within a wave can be implemented in parallel. Each wave requires all previous waves to be merged.

```
Wave 1:  [0]                          ← foundation, must go first
Wave 2:  [1] [2] [9]                  ← independent producers + intake
Wave 3:  [3]                          ← needs classifier from 2
Wave 4:  [4] [5]                      ← both need extracted text from 3
Wave 5:  [6]                          ← needs search from 4+5
Wave 6:  [7]                          ← needs 1+2+3 running
Wave 7:  [8] [10] [11]               ← UI, email bodies, doc-to-markdown (parallel)
```

## Future Work (designed, not yet phased)

| Feature | Design Doc | Depends On | Status |
|---------|-----------|------------|--------|
| UI Redesign | [09-ui-redesign.md](design/09-ui-redesign.md) | 8 | Designing |
| Agent Evolution | [10-agent-evolution.md](design/10-agent-evolution.md) | 6, 8 | Designing |
| Email Backfill | [11-email-backfill.md](design/11-email-backfill.md) | 1 | Designing |
| Bills & Reminders | [12-bills-and-reminders.md](design/12-bills-and-reminders.md) | 3, Backfill | Designing |
| Azure OpenAI Chat | — (in Chat.fs) | — | Done |

## Agent Instructions

To implement a phase:

1. Read this file to understand dependencies and confirm all prerequisites are merged
2. Read the phase spec linked in the table above
3. Read `.github/copilot-instructions.md` for conventions
4. Create the feature branch listed in the table
5. Implement all tasks in the spec
6. Run `dotnet build` and `dotnet test`
7. Update `.project/testing-register.md` with any new tests
8. Update `.project/STATUS.md` with phase status
9. Commit using the commit prompt (`.github/prompts/commit.prompt.md`)
10. Open a PR using the PR prep prompt (`.github/prompts/pr-prep.prompt.md`)
