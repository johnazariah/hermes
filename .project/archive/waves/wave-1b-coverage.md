# Wave 1b: Coverage Push

> Status: ✅ **Done**  
> Agent prompt: `.github/prompts/agent1-coverage-push.prompt.md`

## Summary

Raised Hermes.Core test coverage from 55% to 85.5% line coverage. Added tests for all untested modules + branch coverage for existing modules.

## Result

- 258 → 706 tests
- 55% → 85.5% line coverage
- 0 failures, 0 skipped
- Tests added for: PdfStructure, DocumentFeed, ContentClassifier, ExcelExtraction, WordExtraction, CsvExtraction, DocumentBrowser, ActivityLog, Threads, DocumentManagement, Chat, Embeddings, ServiceHost

## Log

### April 2, 2026
- Coverage push in multiple batches (test files only — no src changes)
- Property tests added via FsCheck
- Placeholder assertions replaced with real assertions
- Integration tests re-labelled with proper Trait categories
