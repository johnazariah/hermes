---
description: "Run quality gate, create branch, group commits, open PR, monitor CI."
---

# PR Preparation

## Steps

1. **Quality Gate**: Run `dotnet build` and `dotnet test`. Fix any failures before proceeding.

2. **Branch**: If on `main`/`master`, create a feature branch:

    ```
    git checkout -b feat/<short-description>
    ```

3. **Review Changes**: Run `git diff main --stat` to see all changes vs main.

4. **Commit**: Use the commit prompt (`.github/prompts/commit.prompt.md`) to create logical conventional commits.

5. **Push**: `git push -u origin HEAD`

6. **Open PR**:

    ```
    gh pr create --title "<type>: <description>" --body "<summary of changes>"
    ```

7. **Monitor CI**: Watch the CI run:
    ```
    gh pr checks
    ```
    If CI fails, diagnose from the logs and fix.

## Rules

- PRs should be focused — one feature, one fix, or one refactor
- PR title follows conventional commit format
- All tests must pass before merging
- Update `.project/testing-register.md` if tests changed
