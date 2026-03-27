# Phase 9: Folder Watching

**Status**: Not Started  
**Depends On**: Phase 2 (Classification Pipeline)  
**Deliverable**: Save a bank statement PDF to `~/Downloads` → automatically classified and indexed within seconds.

---

## Objective

Extend the intake pipeline to watch user-configured local folders (Downloads, Desktop, etc.) for new documents matching configurable patterns, copy them into `unclassified/`, and let the existing classification pipeline handle the rest.

---

## Tasks

### 9.1 — Folder Watcher Task
- [ ] Long-running `BackgroundService` task managing multiple `FileSystemWatcher` instances
- [ ] One watcher per configured folder in `config.yaml`:
  ```yaml
  watch_folders:
    - path: ~/Downloads
      patterns: ["*.pdf", "*statement*", "*invoice*", "*receipt*", "*payslip*"]
    - path: ~/Desktop
      patterns: ["*.pdf"]
  ```
- [ ] Watch for `Created` events matching any configured glob pattern
- [ ] Debounce: wait 500ms + stable-size check (same as Phase 2 classifier)
- [ ] Ignore files in subdirectories if configured (optional, default: root only)

### 9.2 — Copy to `unclassified/`
- [ ] **Copy** (not move) matched files from the watched folder to `{archive_dir}/unclassified/`
- [ ] Preserving the original file in the source folder — user might need it
- [ ] Standardised filename: `{date_today}_{source_folder_name}_{original_name}.{ext}`
- [ ] Write `.meta.json` sidecar:
  ```json
  {
    "source_type": "watched_folder",
    "source_path": "~/Downloads/Statement-Jan-2025.pdf",
    "original_name": "Statement-Jan-2025.pdf",
    "sha256": "abc123..."
  }
  ```
- [ ] No email metadata (sender, subject, gmail_id) — those are null for watched folder documents

### 9.3 — Deduplication
- [ ] Before copying: compute SHA256 of the source file
- [ ] Check if this SHA256 already exists in the `documents` table
- [ ] If duplicate: skip the copy, log "Already in archive: {saved_path}"
- [ ] Also check against files currently in `unclassified/` (not yet classified)
- [ ] This prevents the same file from being re-ingested every time the watcher restarts

### 9.4 — Pattern Matching
- [ ] Glob patterns (not regex) for user-friendliness: `*.pdf`, `*statement*`, `invoice*.pdf`
- [ ] Case-insensitive matching
- [ ] Multiple patterns per folder (OR logic — match any)
- [ ] Configurable minimum file size (reuse `min_attachment_size` setting, default 20KB)

### 9.5 — CLI Commands
- [ ] `hermes watch list` — show all watched folders with their patterns and status
  ```
  Path              Patterns                                       Status
  ────────────────  ─────────────────────────────────────────────  ──────
  ~/Downloads       *.pdf, *statement*, *invoice*, *receipt*       Active
  ~/Desktop         *.pdf                                          Active
  ```
- [ ] `hermes watch add ~/Downloads --patterns "*.pdf" "*statement*"` — add a watched folder
- [ ] `hermes watch remove ~/Downloads` — remove a watched folder
- [ ] Changes persisted to `config.yaml`

### 9.6 — Avalonia Settings Integration
- [ ] Watched folders section in the Settings tab of the shell window
- [ ] List of current watched folders with patterns
- [ ] Add button → folder picker + pattern input
- [ ] Remove button for each entry
- [ ] Changes persisted and watchers restarted immediately

### 9.7 — Edge Cases
- [ ] Large files: copy in a temp name (`.hermes_copying`), rename after complete
- [ ] Watcher restart: on config reload or service restart, re-scan watched folders for files that might have been added while the service was stopped
- [ ] Folder doesn't exist: log warning, skip, retry periodically (user might create it later)
- [ ] Permissions: log error if Hermes can't read from the watched folder

---

## Acceptance Criteria

- [ ] Saving a PDF to `~/Downloads` → it appears in `unclassified/` within 2 seconds → gets classified
- [ ] Original file in `~/Downloads` is untouched (copy, not move)
- [ ] Same PDF saved twice → second copy is deduplicated (skipped)
- [ ] Glob patterns correctly match: `*.pdf` matches `invoice.pdf`, `*statement*` matches `Q1-statement-2025.xlsx`
- [ ] `hermes watch add/list/remove` CLI commands work correctly
- [ ] Watched folders configurable in Avalonia settings panel
- [ ] Service restart → watchers resume on all configured folders
- [ ] Files added while the service was stopped are picked up on restart
- [ ] Missing or inaccessible folders are handled gracefully (warning, not crash)
