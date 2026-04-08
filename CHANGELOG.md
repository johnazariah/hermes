# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- Design documentation (8 docs covering vision, requirements, architecture, data model, MCP server, phases, decisions, delta analysis)
- Phase specifications (10 phases with task checklists and acceptance criteria)
- GitHub issues for each phase
- Copilot instructions and prompt library
- Git hooks (pre-commit, pre-push)
- CI workflow (GitHub Actions, macOS + Windows matrix)
- Testing register
- Project status tracking

## [2026-04-08] Pipeline bug fixes + v2 architecture

### Fixed
- **Extraction queue poisoning**: Failed documents (encrypted PDFs, missing files) now marked as `extraction_method = 'failed'` and skipped in future batches — previously they blocked the front of the queue forever
- **Extraction progress reporting**: "Extract now" button now reports per-document progress (filename, count, rate, ETA) instead of showing 0/N until the entire batch completed
- **Reclassify progress reporting**: Same per-document live progress for the "Reclassify now" button
- **Missing `classification_tier` column**: Added unconditional column-ensure step in DB migrations — handles DBs where migration guards were skipped
- **Background batch size**: Increased from 50 to 500 per sync cycle (7,000-doc backlog now clears in ~2.5 hours instead of ~24)

### Added
- **v2 Pipeline & UI End-State Spec** (`.project/design/20-pipeline-v2-endstate.md`): Channel-driven pipeline with plugin registry, dead letter handling, and React frontend replacing Avalonia
- **v2 Implementation Plan** (`.project/design/21-v2-implementation-plan.md`): 16-phase migration plan — Track A (pipeline refactoring) + Track B (React UI)

### Changed
- `Classifier.getUnsortedWithText` made public for per-document progress in reclassification
- `Extraction.getDocumentsForExtraction` skips docs with `extraction_method = 'failed'` when not in force mode
