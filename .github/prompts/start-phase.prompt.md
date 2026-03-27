---
description: "Plan and execute the next available phase from the dependency graph."
---

# Start Phase

## Steps

1. **Read the dependency graph**: Open `.project/phases.md` and parse the phases table.

2. **Find executable phases**: A phase is executable when:
   - Its status is "Not Started"
   - All phases listed in its "Depends On" column have status "Done"

3. **Pick the best candidate**:
   - If multiple phases are executable, pick the one that unblocks the most downstream work
   - Show the user which phases are available and recommend one

4. **Confirm**: Ask the user:
   > "Phase N ({name}) is ready. Its dependencies are all merged. Shall I implement it on branch `{branch}`?"

5. **Read the spec**: Open the linked spec file and review all tasks and acceptance criteria.

6. **Read conventions**: Read `.github/copilot-instructions.md` for code style, testing, and project conventions.

7. **Create branch**: `git checkout -b {branch}` from main/master.

8. **Implement**: Work through every task in the spec. After each major section:
   - Run `dotnet build` (or the project's build command)
   - Run `dotnet test` (or the project's test command)
   - Fix any failures before continuing

9. **Update tracking**:
   - Update `.project/testing-register.md` with any new tests
   - Update `.project/phases.md` — set the phase status to "Done"
   - Update `.project/STATUS.md` with current state

10. **Commit**: Use `.github/prompts/commit.prompt.md` to create logical conventional commits.

11. **PR**: Use `.github/prompts/pr-prep.prompt.md` to open a pull request.

12. **Report**: Summarise what was built, what tests were added, and what the next executable phases are.
