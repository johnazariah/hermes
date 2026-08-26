---
description: "Run quality gate, create branch, group commits, open PR, monitor CI."
---

# PR Preparation

## Steps

1. **Journal Gate**: Ensure the branch contains the current session's active-wave entry using `.github/prompts/daily-journal.prompt.md`.

2. **Quality Gate**: Run `dotnet build` and `dotnet test`. Fix any failures before proceeding.

3. **Branch**: If on `main`/`master`, create a feature branch:

    ```
    git checkout -b feat/<short-description>
    ```

4. **Review Changes**: Run `git diff main --stat` to see all changes vs main.

5. **Commit**: Use the commit prompt (`.github/prompts/commit.prompt.md`) to create logical conventional commits.

6. **Push**: `git push -u origin HEAD`

7. **Open PR**:

    ```
    gh pr create --title "<type>: <description>" --body "<summary of changes>"
    ```

8. **Monitor CI**: Watch the CI run:
    ```
    gh pr checks
    ```
    If CI fails, diagnose from the logs and fix.

## Rules

- PRs should be focused — one feature, one fix, or one refactor
- PR title follows conventional commit format
- All tests must pass before merging
- Update `.project/testing-register.md` if tests changed
- Include an active-wave journal entry for substantive work
