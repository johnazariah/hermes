---
description: "Gather context about the project and recommend the next best thing to work on."
---

# Pick Next Work

## Steps

1. **Gather Context**:
    - Read `.project/phases.md` — check the dependency graph and phase status
    - Run `git log --oneline -20` — see recent activity
    - Check `gh issue list --repo johnazariah/hermes` — open issues and their status
    - Run `dotnet build` and `dotnet test` — is the build healthy?
    - Check `.project/testing-register.md` — test coverage gaps

2. **Categorise Open Work**:
    - **Blocking**: build failures, test failures, broken pipeline
    - **Momentum**: current phase tasks that are in-progress
    - **Strategic**: next phase that should start once current is done
    - **Debt**: test gaps, doc staleness, refactoring opportunities

3. **Recommend Top 3**:
    - Present 3 recommended next actions, prioritised
    - For each: what to do, why now, estimated scope (small/medium/large)
    - Always fix Blocking items first
