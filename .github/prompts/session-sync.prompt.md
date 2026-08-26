---
description: "End-of-session sync: append the active-wave journal and refresh only triggered derived docs."
---

# Session Sync

Run this at the end of each coding session.

## 1. Journal the session

Run `.github/prompts/daily-journal.prompt.md`.

This is mandatory for substantive coding, testing, architecture, or repository-maintenance work. If no wave is active, stop and report the governance gap.

## 2. Refresh triggered documents only

- If tests changed, update `.project/testing-register.md`.
- If public commands, supported platforms, or repository structure changed, update `README.md`.
- Do not update `.project/STATUS.md` during routine session sync.
- Do not create a new status, plan, or journal file.

## 3. Check consistency

- Active wave contains the new dated entry directly below `## Log`.
- Older journal entries are unchanged.
- Design docs contain no task status.
- Test counts match the register when tests changed.
- Links to active design docs resolve.

## 4. Leave commit control to the user

Show the resulting diff. Do not commit or push unless the user explicitly asks.

## Checklist

- [ ] Daily journal entry added to the active wave
- [ ] No older journal entry changed
- [ ] `STATUS.md` unchanged unless this is an explicit wave transition
- [ ] Testing register updated only if tests changed
- [ ] README updated only if its public facts changed
