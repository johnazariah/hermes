---
description: "Agent 1: Push test coverage from 62% to 85% line/branch. Tests only — no src/ changes."
---

# Coverage Push — Agent 1

**Branch**: `feat/coverage-push`

**IMPORTANT: Use a git worktree — do NOT work in the main checkout.**
Another agent is running in parallel on the same repo. You MUST use a separate worktree to avoid conflicts.

```
cd c:\work\hermes
git worktree add ..\hermes-coverage feat/coverage-push 2>/dev/null || git worktree add ..\hermes-coverage -b feat/coverage-push
cd c:\work\hermes-coverage
```

All commands below run in `c:\work\hermes-coverage`, NOT `c:\work\hermes`.

**Scope**: Only files in `tests/Hermes.Tests/`. Do NOT modify any files in `src/`.

**Goal**: Raise `Hermes.Core` coverage from 62.1% line / 27.5% branch to ≥85% / ≥85%.

**Rules**:

- Read `.github/copilot-instructions.md` — especially testing conventions
- Use `@fsharp-dev` for all F# test code
- Run `dotnet test tests/Hermes.Tests/Hermes.Tests.fsproj --nologo --collect:"XPlat Code Coverage" --results-directory TestResults` after each batch of tests
- Parse coverage: `$xml = [xml](Get-Content TestResults/*/coverage.cobertura.xml); Write-Host "Line: $([math]::Round([float]$xml.coverage.'line-rate' * 100, 1))%  Branch: $([math]::Round([float]$xml.coverage.'branch-rate' * 100, 1))%"`
- Remove old TestResults before each run: `Remove-Item -Recurse -Force TestResults -ErrorAction SilentlyContinue`
- Test naming: `Module_Function_Condition_ExpectedResult`
- Commit after each module reaches 85%+

**Starting state**: 418 tests, 0 failures, 2 skipped. 62.1% line / 27.5% branch.

---

## Modules needing tests (priority order by impact)

### 1. ExcelExtraction.fs — NO TESTS

Create `tests/Hermes.Tests/ExcelExtractionTests.fs`. Add to fsproj.

Test: `extractExcel` with:

- Simple 2-column sheet → correct markdown table
- Multi-sheet workbook → headings per sheet
- Empty sheet → skipped (no heading)
- Sheet with formulas → reads calculated values
- Sheet with dates → ISO 8601 format
- Sheet with > 100 rows → cap and produce table

### 2. WordExtraction.fs — NO TESTS

Create `tests/Hermes.Tests/WordExtractionTests.fs`. Add to fsproj.

Test: `extractWord` with:

- Paragraphs → markdown paragraphs
- Heading1/2/3 styles → `#`/`##`/`###`
- Tables → markdown tables
- Lists → `- item` format
- Empty document → empty content
- Note: generating test .docx in-memory with Open XML SDK

### 3. CsvExtraction.fs — NO TESTS

Create `tests/Hermes.Tests/CsvExtractionTests.fs`. Add to fsproj.

Test: `extractCsv`, `parseCsvLine`, `detectDelimiter` with:

- Comma-delimited → correct columns
- Semicolon-delimited → auto-detected
- Tab-delimited → auto-detected
- Quoted fields with commas inside → correct parsing
- Empty rows → skipped
- Header-only CSV → table with no data rows
- Malformed (unbalanced quotes) → graceful handling

### 4. DocumentBrowser.fs — NO TESTS

Create `tests/Hermes.Tests/DocumentBrowserTests.fs`. Add to fsproj.

Test: `listCategories`, `listDocuments`, `getDocumentDetail` with:

- Empty DB → empty lists
- Multiple categories → correct counts
- Filter by category → only matching docs
- Document detail → all fields populated
- Pipeline status logic (extracted_at null vs not null)

### 5. ActivityLog.fs — NO TESTS

Create `tests/Hermes.Tests/ActivityLogTests.fs`. Add to fsproj.

Test: `logEvent`, `getRecent`, `purgeOld` with:

- Log an event → getRecent returns it
- Multiple events → ordered by timestamp DESC
- Limit parameter → respects limit
- Purge old → removes events older than threshold
- Empty log → empty list

### 6. Threads.fs — NO TESTS

Create `tests/Hermes.Tests/ThreadsTests.fs`. Add to fsproj.

Test: `listThreads`, `getThreadDetail` with:

- Messages with same thread_id → grouped as one thread
- Thread with 0 attachments → attachment_count = 0
- Thread with multiple senders → participants list
- Single-message thread → still returned
- Thread detail → messages in chronological order

### 7. DocumentManagement.fs — NO TESTS

Create `tests/Hermes.Tests/DocumentManagementTests.fs`. Add to fsproj.

Test: `reclassifyDocument`, `queueReextract`, `getProcessingQueue` with:

- Reclassify → category changes in DB
- Reclassify → file moves on disk (mock filesystem)
- Reextract → clears extracted_at
- Processing queue → correct counts per stage
- Empty queue → zeros

### 8. DocumentFeed.fs — HAS TESTS, NEEDS BRANCH COVERAGE

Expand `tests/Hermes.Tests/DocumentFeedTests.fs` with:

- Filter by multiple categories
- Filter by state (ingested vs extracted vs embedded)
- Empty feed (since_id > max)
- Content format "raw" for CSV file
- Content format "markdown" for PDF
- Feed stats with empty DB

### 9. ContentClassifier.fs — HAS TESTS, NEEDS BRANCH COVERAGE

Expand `tests/Hermes.Tests/ContentClassifierTests.fs` with:

- Each of the 7 ContentMatch variants individually
- Multiple conditions (AND logic)
- LLM response missing "category" key → None
- LLM response with confidence < 0.4 → None
- LLM response with invalid JSON → None
- Empty markdown input → no match

### 10. PdfStructure.fs — HAS 25 TESTS, NEEDS BRANCH COVERAGE

Expand `tests/Hermes.Tests/PdfStructureTests.fs` with:

- Empty page (0 letters) → empty blocks
- Single line (no table, no heading) → paragraph
- Right-aligned numeric column detection
- CID text detection (>30% CID sequences → low confidence)
- Multi-page table continuation with mismatched headers → no merge
- Key-value with large gap (>30pt) → detected
- Key-value with colon separator → detected

### 11. Existing modules — BRANCH COVERAGE

The existing test files cover happy paths but miss branches. Add edge cases:

**SearchTests.fs**: Empty query, all filters set, no results, malformed FTS5 query
**ReminderTests.fs**: Null vendor, null amount, category not in billCategories, amount = 0, date exactly on boundary (-30d, +60d)
**EmailSyncTests.fs**: Attachment below min size, hash collision (same hash exists), message already exists
**EmbeddingTests.fs**: Empty text input, embedding dimension mismatch, batch with mix of success/failure
**ConfigTests.fs**: Malformed backfill section, missing chat section with Azure fields set

---

## Completion criteria

- All existing 418 tests still pass
- Coverage ≥ 85% line AND ≥ 85% branch on Hermes.Core
- No tests depend on external services (Ollama, Gmail, Azure)
- All test files use `TestHelpers.memFs`, `TestHelpers.inMemoryDb`, `TestHelpers.fakeChatProvider` for isolation

## Final commit

```
git add -A
git commit -m "test: coverage push 62% → 85%+ — tests for all untested modules + branch coverage for existing"
git push -u origin feat/coverage-push
```

Do NOT merge to master.
