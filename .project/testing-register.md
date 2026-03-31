# Hermes — Testing Register

> **Rule**: Update this file whenever tests are added, removed, or modified.
> The pre-commit hook will warn if test files change but this register doesn't.

## Summary

| Category | Count |
|----------|-------|
| Unit     | 250+  |
| Property | 1     |
| Integration | 7  |
| **Total** | **258** |

---

## Tests

### Config_ParseYaml_ValidYaml_ReturnsConfig
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/ConfigTests.fs`
- **Intent**: Valid YAML parses into correct config values

### Config_ParseYaml_EmptyYaml_ReturnsDefaults
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/ConfigTests.fs`
- **Intent**: Empty YAML returns default config values

### Config_ParseYaml_WithAccounts_ParsesAccountList
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/ConfigTests.fs`
- **Intent**: YAML with accounts section parses into account list

### Config_ParseYaml_WithWatchFolders_ParsesPatterns
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/ConfigTests.fs`
- **Intent**: YAML with watch_folders section parses patterns correctly

### Config_Load_MissingFile_ReturnsError
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/ConfigTests.fs`
- **Intent**: Loading from non-existent path returns error (uses in-memory FS)

### Config_Load_ValidFile_ReturnsConfig
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/ConfigTests.fs`
- **Intent**: Loading from valid file returns parsed config (uses in-memory FS)

### Config_Init_CreatesConfigAndRules
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/ConfigTests.fs`
- **Intent**: Init creates config.yaml and rules.yaml (uses in-memory FS)

### Config_Init_SkipsExistingFiles
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/ConfigTests.fs`
- **Intent**: Init does not overwrite existing config files

### Config_ExpandHome_TildePath_ExpandsToUserHome
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/ConfigTests.fs`
- **Intent**: ~/path expands to user home directory

### Config_ExpandHome_AbsolutePath_ReturnsUnchanged
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/ConfigTests.fs`
- **Intent**: Absolute paths pass through unchanged

### Config_ParseYaml_NeverThrows
- **Kind**: Property
- **File**: `tests/Hermes.Tests/ConfigTests.fs`
- **Intent**: parseYaml never throws for any string input

### Database_InitSchema_CreatesAllTables
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/DatabaseTests.fs`
- **Intent**: Schema init creates messages, documents, sync_state, schema_version, documents_fts

### Database_InitSchema_SetsSchemaVersion
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/DatabaseTests.fs`
- **Intent**: Schema init records the current version number

### Database_InitSchema_IsIdempotent
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/DatabaseTests.fs`
- **Intent**: Calling initSchema twice succeeds without error

### Database_SchemaVersion_BeforeInit_ReturnsZero
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/DatabaseTests.fs`
- **Intent**: Schema version is 0 before any initialisation

### Database_TableExists_NonexistentTable_ReturnsFalse
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/DatabaseTests.fs`
- **Intent**: tableExists returns false for missing tables

### Database_FTS5_InsertTrigger_PopulatesFtsOnInsert
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/DatabaseTests.fs`
- **Intent**: FTS5 trigger populates search index on document insert

### Database_FTS5_SearchByVendor_FindsDocument
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/DatabaseTests.fs`
- **Intent**: FTS5 search by vendor name finds matching document

### Database_InitArchive_CreatesDirectoriesAndDatabase
- **Kind**: Integration
- **File**: `tests/Hermes.Tests/DatabaseTests.fs`
- **Intent**: Archive init creates category directories and db.sqlite on real filesystem

### Database_FromPath_CreatesParentDirectories
- **Kind**: Integration
- **File**: `tests/Hermes.Tests/DatabaseTests.fs`
- **Intent**: fromPath creates parent directories for database file

### Database_InitSchema_CreatesAllIndexes
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/DatabaseTests.fs`
- **Intent**: Schema init creates all 11 expected indexes

### Database_InitSchema_V3_CreatesRemindersTable
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/DatabaseTests.fs`
- **Intent**: Schema v3 creates reminders table

### Database_InitSchema_V3_SyncStateHasBackfillColumns
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/DatabaseTests.fs`
- **Intent**: Schema v3 adds backfill columns to sync_state

### Database_InitSchema_V3_SchemaVersionIs3
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/DatabaseTests.fs`
- **Intent**: Schema version is 3 after init

### Database_InitSchema_V3_IdempotentRunTwice
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/DatabaseTests.fs`
- **Intent**: Running initSchema twice is safe

### Database_InitSchema_V3_ReminderIndexesExist
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/DatabaseTests.fs`
- **Intent**: Reminder indexes created correctly

### Reminders_DetectBill_InvoiceWithDueDate_CreatesReminder
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/ReminderTests.fs`
- **Intent**: Invoice with amount + due date in range → creates reminder

### Reminders_DetectBill_WrongCategory_ReturnsNone
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/ReminderTests.fs`
- **Intent**: Non-bill category → no reminder

### Reminders_DetectBill_OldDate_ReturnsNone
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/ReminderTests.fs`
- **Intent**: Due date >30 days ago → no reminder

### Reminders_DetectBill_NoAmount_ReturnsNone
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/ReminderTests.fs`
- **Intent**: No extracted amount → no reminder

### Reminders_EvaluateNew_InsertsReminders
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/ReminderTests.fs`
- **Intent**: Extracted docs with amounts create reminders

### Reminders_EvaluateNew_DeduplicatesExisting
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/ReminderTests.fs`
- **Intent**: Running evaluate twice doesn't duplicate reminders

### Reminders_MarkCompleted_ChangesStatus
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/ReminderTests.fs`
- **Intent**: Mark paid removes from active list

### Reminders_Snooze_HidesUntilExpiry
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/ReminderTests.fs`
- **Intent**: Snoozed reminders hidden until expiry, then reappear

### Reminders_Dismiss_PermanentlyRemoves
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/ReminderTests.fs`
- **Intent**: Dismissed reminders permanently removed from active and completed

### Reminders_GetSummary_CorrectCounts
- **Kind**: Unit
- **File**: `tests/Hermes.Tests/ReminderTests.fs`
- **Intent**: Summary correctly counts overdue, upcoming, and total amount
