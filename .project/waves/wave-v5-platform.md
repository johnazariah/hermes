# Wave V5 - Pipeline, File-First Archive, and Product Surface

> **Status:** ✅ Done
> **Range:** 2026-04-19 to 2026-08-11
> **Architecture:** [30-pipeline-v5-architecture.md](../design/30-pipeline-v5-architecture.md)

## Goal

Replace the linear V4 pipeline and database-backed content model with a DAG-driven, file-first platform, then expose the resulting workflow through complete web and desktop surfaces.

## Tasks

| Area | Outcome | Status |
|------|---------|--------|
| Pipeline V5 | Declarative DAG, dependency validation, per-stage tables, completion ledger | ✅ Done |
| Comprehension | Fast triage plus gated deep extraction and schema-safe RAC | ✅ Done |
| Providers | Gmail and Outlook account-aware ingestion | ✅ Done |
| Extraction | PDF, DOCX, XLSX, PPTX, CSV, text, and image paths | ✅ Done |
| File-first archive | Structured email/local folders, extraction and comprehension artifacts | ✅ Done |
| Learning loop | Preferences, learned patterns, suggestions, corrections, tags | ✅ Done |
| Product UI | Home triage, document browser, search, chat, settings, onboarding | ✅ Done |
| Delivery | Cross-platform CI, tray, MAUI shells, publish jobs | ✅ Done |
| Agent workflow | Hermes-tailored Squad scaffolding and state bridge | ✅ Done |

## Log

### 2026-08-12 - Documentation reconciliation

- Reconstructed this journal from the linear `main` history.
- Added the Pipeline V5 architecture reference and refreshed status and test metrics.
- Recorded the remaining coverage, live-archive validation, migration, household-profile, and Osprey work in `STATUS.md`.

### 2026-08-11 - CI repair and Squad enablement

- PR #14 committed the required PPTX fixtures and selected Xcode 26.5 for MacCatalyst CI.
- PR #13 added Hermes-specific Squad 0.11.0 agents, skills, workflows, templates, and MCP state bridge.
- All test and platform build steps passed; the pre-existing 65% versus 75% line-coverage gate remains.

### 2026-05-09 - Product surface completed

- Added the Gmail-style shell, command palette, onboarding, Home triage, Documents, Search, and rebuilt Settings experiences.

### 2026-05-07 to 2026-05-08 - File-first archive completed

- Added structured account/sender/thread and local archive layouts.
- Moved extraction and comprehension content to sidecar artifacts.
- Added thread comprehension, multi-label tags, preferences, learned patterns, and suggestion review.
- Removed database content columns while preserving metadata/API compatibility.

### 2026-05-06 to 2026-05-07 - Sources and comprehension expanded

- Added Outlook/Graph ingestion, PPTX extraction, retrieval-augmented comprehension, deep prompts, and the full suggestion silver thread.

### 2026-04-19 to 2026-04-20 - Pipeline V5 established

- Split comprehension into triage and deep passes.
- Added the DAG framework, stage schemas, dependency validation, phase scheduling, and DAG visualization.
