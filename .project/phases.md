# Hermes — Phase Dependency Graph

> Machine-readable phase definitions for automated planning.
> An agent reads this file to determine what can run in parallel and what must go first.

## Phases

| Phase | Name | Branch | Spec | Depends On | Status |
|-------|------|--------|------|-----------|--------|
| 0 | Project Skeleton | `feat/0-project-skeleton` | [phase-0](specs/phase-0-project-skeleton.md) | — | Not Started |
| 1 | Email Sync | `feat/1-email-sync` | [phase-1](specs/phase-1-email-sync.md) | 0 | Not Started |
| 2 | Classification Pipeline | `feat/2-classification` | [phase-2](specs/phase-2-classification.md) | 0 | Not Started |
| 3 | Text Extraction | `feat/3-text-extraction` | [phase-3](specs/phase-3-text-extraction.md) | 2 | Not Started |
| 4 | Full-Text Search | `feat/4-fulltext-search` | [phase-4](specs/phase-4-fulltext-search.md) | 3 | Not Started |
| 5 | Embeddings & Semantic Search | `feat/5-embeddings` | [phase-5](specs/phase-5-embeddings.md) | 3 | Not Started |
| 6 | MCP Server | `feat/6-mcp-server` | [phase-6](specs/phase-6-mcp-server.md) | 4, 5 | Not Started |
| 7 | Background Service | `feat/7-background-service` | [phase-7](specs/phase-7-background-service.md) | 1, 2, 3 | Not Started |
| 8 | Avalonia UI & Installer | `feat/8-ui-installer` | [phase-8](specs/phase-8-ui-and-installer.md) | 7 | Not Started |
| 9 | Folder Watching | `feat/9-folder-watching` | [phase-9](specs/phase-9-folder-watching.md) | 2 | Not Started |

## Execution Waves

Phases within a wave can be implemented in parallel. Each wave requires all previous waves to be merged.

```
Wave 1:  [0]                          ← foundation, must go first
Wave 2:  [1] [2] [9]                  ← independent producers + intake
Wave 3:  [3]                          ← needs classifier from 2
Wave 4:  [4] [5]                      ← both need extracted text from 3
Wave 5:  [6]                          ← needs search from 4+5
Wave 6:  [7]                          ← needs 1+2+3 running
Wave 7:  [8]                          ← needs service from 7
```

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
