# Hermes — Documentation Governance

> Rules for keeping project documentation coherent. Read by all agents before creating or modifying docs.

## Directory Structure (enforced)

```
.project/
├── STATUS.md              ← Hub: current state + wave roadmap (~50 lines max)
├── waves/                 ← One file per wave, append-only, newest-on-top
│   └── wave-{N}-{name}.md
├── design/                ← Reference architecture docs (WHAT and WHY, not status)
│   └── {NN}-{topic}.md
├── specs/                 ← Historical phase specs (phases 0–11, read-only)
│   └── phase-{N}-{name}.md
├── testing-register.md    ← Test catalog
└── archive/               ← Superseded files (never deleted, just moved here)

.github/prompts/           ← Agent instruction files (HOW, not status)
├── {name}.prompt.md       ← Active prompts only — archive when wave completes
├── commit.prompt.md       ← Reusable workflow prompts (permanent)
├── pr-prep.prompt.md
├── debug-test-failure.prompt.md
└── release.prompt.md
```

## Rules

### 1. STATUS.md is the single source of truth for project state

- Maximum ~50 lines
- Contains: current metrics, active wave, wave roadmap table, key design doc pointers, blockers
- Updated once per wave completion (not per commit)
- Agents read this FIRST to understand where the project is

### 2. Wave files are append-only logs

- One file per wave: `.project/waves/wave-{N}-{name}.md`
- Structure: header (status, links) → tasks table → log (newest on top)
- Agents append log entries as they work — never edit old entries
- Status transitions: `Not started` → `⏳ Active` → `✅ Done`
- When a wave completes, update STATUS.md roadmap table

### 3. Design docs are reference, not status

- Never put task status, completion checkboxes, or "done/not done" in design docs
- Design docs describe WHAT the system should do and WHY
- If a design is superseded, move to `archive/` — don't delete
- Number sequentially: `{NN}-{topic}.md`

### 4. Prompts are instructions, not status

- Agent prompts go in `.github/prompts/`
- A prompt tells an agent WHAT TO DO — it references wave files for status
- Prompts should say: "Read `.project/waves/wave-X.md` for status" — not duplicate it
- When a wave's prompt is fully executed, archive it to `.project/archive/`
- Reusable workflow prompts (`commit`, `pr-prep`, `release`, `debug-test-failure`) are permanent

### 5. Specs are historical — don't modify

- Phase specs (0–11) are historical records of what was planned
- Don't update checkboxes retroactively — that's what wave files are for
- New work uses waves, not specs

### 6. Never duplicate status across files

- If you're tempted to write "current state" in a prompt → point to STATUS.md
- If you're tempted to write "what's done" in a design doc → point to the wave file
- If you're tempted to create a new plan file → create a wave file instead

### 7. Archive, don't delete

- Superseded files go to `.project/archive/`
- Git history preserves everything, but archive/ makes it explicit what's current vs legacy
- Archive trigger: design doc superseded by newer doc, prompt's wave completed, plan replaced by waves

## For Agents

Before creating ANY new markdown file in `.project/`, check:

1. Does this belong in an existing wave file? → Append to it
2. Does this belong in STATUS.md? → Update it (keep it short)
3. Is this a new architectural concept? → Create a design doc in `design/`
4. Is this a new phase of work? → Create a wave file in `waves/`
5. Is this agent instructions? → Create a prompt in `.github/prompts/` that references wave files

**Never create files in**: `.project/plans/` (archived), root of `.project/` (except STATUS.md, GOVERNANCE.md, and testing-register.md)

## Maintenance Rhythm

After each wave is reviewed and approved, run `.github/prompts/post-wave-update.prompt.md`:

- **Wave file**: mark ✅ Done, append log entry
- **STATUS.md**: update metrics + roadmap (30 seconds)
- **README badges**: refresh test count + coverage if changed
- **Archive**: move completed wave prompt to `.project/archive/`
- **Push**: `git push`

This keeps everything in sync. Skip it and entropy wins.
