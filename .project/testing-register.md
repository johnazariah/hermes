# Hermes — Testing Register

> **Rule**: Update this file whenever tests are added, removed, or modified.
> The pre-commit hook will warn if test files change but this register doesn't.

## Summary

| Category | Count |
|----------|-------|
| Unit     | 18    |
| Property | 1     |
| Integration | 2  |
| **Total** | **21** |

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
