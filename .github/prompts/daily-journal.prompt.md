---
description: "Append an evidence-based daily entry to the active wave without changing canonical status."
---

# Daily Journal

Capture the current session in the active wave log.

## 1. Find the active wave

Read `.project/STATUS.md` and `.project/GOVERNANCE.md`, then inspect `.project/waves/*.md`.

Exactly one wave must contain:

```text
> **Status:** ⏳ Active
```

- If none is active, stop and report that a wave must be created or activated.
- If more than one is active, stop and report the governance conflict.
- Never write routine work into a `✅ Done` wave.

## 2. Gather evidence

Use repository evidence rather than conversation recollection:

- `git status --short`
- `git log --since=midnight --oneline`
- current diff/stat
- PRs or issues touched
- tests, builds, or checks run
- unresolved blockers and the next concrete step

Do not include secrets, personal data, speculative claims, or raw command output.

## 3. Add one entry

Insert one new section immediately below `## Log`. Do not modify or remove existing log sections.

```markdown
### YYYY-MM-DD - Short outcome

- **Changed:** User-visible or architectural change.
- **Evidence:** Commits, PRs, issues, or key files.
- **Validation:** Tests/build/checks and their result.
- **Blockers:** None, or the concrete blocker.
- **Next:** One concrete next step.
```

Omit empty bullets except `Blockers`, which should say `None` when clear.

Before writing, compare today's existing entries with the gathered evidence:

- If an entry already captures the same outcome and evidence, do not add a duplicate; report that the session is already journaled.
- If the session produced a distinct outcome, add a separate entry; do not rewrite the earlier entry.

## 4. Protect canonical status

Do not modify:

- `.project/STATUS.md`;
- older wave-log entries;
- design docs;
- README metrics; or
- `.project/testing-register.md`.

Those files have separate triggers. Wave completion uses `.github/prompts/post-wave-update.prompt.md`.

## 5. Report

Show the wave file and heading added. Do not commit or push unless explicitly asked.
