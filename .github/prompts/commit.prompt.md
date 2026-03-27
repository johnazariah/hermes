---
description: "Analyze staged changes, group into logical conventional commits, and commit them."
---

# Commit Workflow

## Steps

1. **Quality Gate**: Run `dotnet build` and `dotnet test`. Stop if either fails.

2. **Analyze Changes**: Run `git diff --cached --stat` and `git diff --cached` to understand all staged changes.

3. **Group by Concern**: Organize changes into logical commits. Each commit should be one coherent change:
    - Feature addition → `feat: ...`
    - Bug fix → `fix: ...`
    - Test additions → `test: ...`
    - Documentation → `docs: ...`
    - Refactoring → `refactor: ...`
    - Build/config → `chore: ...`

4. **Stage & Commit Each Group**:
    - `git reset HEAD` to unstage everything
    - For each group: `git add <files>` then `git commit -m "<type>: <imperative description>"`

5. **Testing Register**: If any test files changed, verify `.project/testing-register.md` is also updated. If not, update it before committing.

## Commit Message Format

```
<type>: <imperative short description>

[optional body — what and why, not how]

[optional footer]
Co-authored-by: GitHub Copilot <noreply@github.com>
agent: github-copilot
model: <model-name>
```

## Rules

- Subject line: imperative mood, lowercase, no period, max 72 chars
- One logical change per commit
- Never commit failing builds or tests
- Always include AI attribution trailers for AI-assisted commits:
    - `Co-authored-by:` — who assisted
    - `agent:` — which AI tool (github-copilot, claude, etc.)
    - `model:` — which model was used
    - All-or-none: if any AI trailer is present, all three must be
