# Hermes — Vision & Goals

## Mission Statement

Hermes is a quiet, always-on, local-first document intelligence service for macOS and Windows. It connects to your email accounts, watches local folders, and continuously ingests, classifies, and indexes every document that passes through your digital life — emails, attachments, downloaded bank statements, receipts, payslips, invoices. Everything stays on your machine, processed by local AI (Ollama), and exposed through an MCP server so AI agents can search and reason over your documents.

Install it once, forget it's there, and never lose a document again.

## Problem Statement

- Important documents arrive via email attachments *and* browser downloads, scattering across inboxes and folders.
- Finding "that invoice from last year" means searching multiple email accounts and rummaging through Downloads.
- There is no unified, private, AI-queryable index across email + local files.
- Existing tools either require cloud upload (privacy concern) or developer expertise to operate.

## Design Principles

| Principle | What it means |
|-----------|---------------|
| **Local-first** | All data stays on your machine. Ollama for AI. No cloud unless you opt in. |
| **Quiet** | Runs as a background service. No windows, no interruptions. Survives reboots. |
| **Mum-friendly** | One-click install. System tray icon + Avalonia shell window for settings. No terminal needed for daily use. |
| **Folders are truth** | Documents land in category folders. Users correct by moving files. Hermes follows. |
| **Incrementally useful** | Phase 1 (sync + classify) is valuable before extraction or embeddings are running. |
| **Unified index** | Eventually, email bodies, PDFs, Word docs, and any text content converge into one searchable index. "Find me everything related to X" queries across all content types. |

## Goals

| # | Goal | Measure of Success |
|---|------|--------------------|
| G1 | Connect to multiple email accounts | Gmail API (v1); IMAP + Microsoft Graph (v2) |
| G2 | Incremental email sync | Detects new messages without full re-download; safe to interrupt and resume |
| G3 | Watch local folders for new documents | Monitors Downloads, Desktop, or user-configured paths for PDFs, images, CSVs |
| G4 | Download, deduplicate, and store attachments | SHA256 dedup; stored locally with metadata linking back to source |
| G5 | Classify into category folders | Rules cascade (sender domain → filename → subject → unsorted); user-correctable |
| G6 | Extract text and structured fields | PDF text, OCR for scans, parse dates/amounts/vendors — via Ollama or heuristics |
| G7 | Full-text + semantic search | FTS5 keyword search + Ollama embeddings in sqlite-vec |
| G8 | Unified MCP search — "find me everything about X" | Single query surface that searches across all content types, synthesises results |
| G9 | Cross-platform background service | Single .NET process: launchd (macOS) / Windows Service; survives reboots |
| G10 | Self-contained installer | .dmg (macOS) / .msi (Windows); .NET self-contained, no prerequisites |
| G11 | Ollama integration for local AI | Embeddings + OCR + extraction via local GPU; auto-installed; Azure Doc Intelligence fallback |

## Non-Goals (v1)

- Not a full email client (no compose/send/reply).
- Not a real-time push notification system.
- No mobile support.
- No cloud/hosted deployment — runs locally.
- No IMAP or Microsoft Graph yet — Gmail API only in v1.
- No document editing or annotation.
- No built-in chat UI in v1 (future Avalonia addition).

## Target Users

1. **Everyone** — designed for non-technical users who just want their documents organised and findable.
2. **AI/LLM users** — structured MCP access to email and document history for AI agents.
3. **Power users** — CLI available for those who want direct control, scripting, and automation.

## Heritage

Adapted from a mail-archive spec built for a tax-preparation pipeline (Osprey/Spec 12). Hermes generalises the concept: provider-agnostic, local folder watching, Ollama-powered AI, mum-friendly packaging, and an MCP server as the primary query interface.

## Long-Term Vision

Hermes evolves toward a **unified content index**. Today it focuses on email attachments and local documents. Tomorrow, email bodies, Word documents, spreadsheets, and any text-bearing content feed into the same index. When you ask "find me everything related to the Manorwoods renovation", Hermes returns the plumber's invoice PDF, the body corporate email thread, the council approval letter from Downloads, and the quote spreadsheet — all from one query.
