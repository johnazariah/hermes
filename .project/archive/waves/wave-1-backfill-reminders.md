# Wave 1: Backfill + Reminders

> Status: ✅ **Done**  
> Design docs: [11-email-backfill.md](../design/11-email-backfill.md), [12-bills-and-reminders.md](../design/12-bills-and-reminders.md)

## Summary

Email backfill engine (paginated Gmail sync, batch size configurable, page token resume) + bill detection with TODO panel (Mark Paid / Snooze / Dismiss). MCP tools for listing and managing reminders. Settings dialog expanded with chat provider picker, account management, backfill config.

## Log

### April 1, 2026 — Review fixes
- Fixed backfill attachment dedup (SHA256 + hashExists before download)
- Fixed backfill error handling (try-catch per attachment)
- Fixed reminder query (NOT IN → NOT EXISTS)
- Built TODO panel with reminder cards and action buttons
- 258 tests passing

### March 31, 2026 — Implementation
- Schema migration v3 (backfill columns + reminders table)
- BackfillConfig + Reminder domain types
- EmailSync.backfillAccount with page token resume
- Reminders.fs (detection, CRUD, queries)
- MCP tools: hermes_list_reminders, hermes_update_reminder
- Settings dialog: chat provider, account backfill config
- CLI: hermes backfill reset
