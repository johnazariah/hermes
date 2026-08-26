---
description: "Complete an approved wave and update canonical project state once."
---

# Post-Wave Update

Run only after the active wave has been reviewed and approved for completion.

## 1. Verify the transition

- Read `.project/GOVERNANCE.md`, `.project/STATUS.md`, and the active wave.
- Confirm acceptance criteria are met and blockers are either resolved or explicitly carried forward.
- Confirm exactly one wave is `⏳ Active`.
- Stop if completion has not been approved.

## 2. Complete the wave

- Change the wave status from `⏳ Active` to `✅ Done`.
- Add a new completion entry directly below `## Log`.
- Do not rewrite older entries.

## 3. Update canonical state

Update `.project/STATUS.md` once:

- current date and verified metrics;
- active work;
- roadmap transitions and carried blockers;
- completed-wave reference; and
- active design-document pointers.

Keep `STATUS.md` at approximately 50 lines.

## 4. Refresh derived surfaces

- Update `.project/testing-register.md` if tests changed.
- Update README badges, commands, structure, or documentation links if their facts changed.
- Archive a wave-specific prompt under `.project/archive/` when it is no longer active.
- Never modify historical specs.

## 5. Check and report

- Verify Markdown links resolve.
- Verify no task status was copied into design docs.
- Show the diff and any carried blockers.
- Do not commit or push unless the user explicitly asks.
