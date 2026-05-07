# 28 — File-First Archive Architecture

> Supersedes: current flat `unclassified/` archive model
> Status: Design — not yet implemented
> Origin: Discussion 2026-05-07, notes 1–14 from stage-by-stage review

## Problem

Hermes stores document content (email bodies, extracted text, comprehension JSON) in SQLite columns. This creates three problems:

1. **Scale** — a large mailbox (100K+ messages over 10 years) will bloat SQLite with TEXT columns containing full email bodies and extracted text. SQLite works well for structured metadata but poorly as a blob store.

2. **Fragility** — the DB is the only copy of content. Corruption or accidental deletion loses everything. There's no way to browse, backup, or verify content without the app.

3. **Locked in** — content trapped in SQLite can't be shared across machines, browsed in a file explorer, or processed by external tools. It locks out future deployment models (multi-machine, hosted, multi-tenant).

## Solution

**Files are data, SQLite is metadata + indexes.**

All human/LLM-readable content lives on the filesystem in a structured folder hierarchy. SQLite stores only:
- Workflow state (stage, timestamps, status)
- Derived indexes (FTS5, sqlite-vec embeddings)
- Operational data (learned patterns, suggestions, sync state)

The archive becomes self-describing and the DB becomes disposable — rebuildable from the files.

## Archive Folder Structure

```
{archive_root}/
├── {account}/                          # email account label
│   ├── {YYYY-MM-DD}/                   # date of receipt
│   │   ├── {HHmmss}/                   # time of receipt (disambiguates same-day)
│   │   │   ├── thread-{thread_id}.md   # email body as markdown
│   │   │   ├── {original_name}         # raw attachment (PDF, DOCX, etc.)
│   │   │   ├── {original_name}.extracted.md      # extraction output
│   │   │   ├── {original_name}.comprehension.json # comprehension output
│   │   │   └── .hermes.json            # sidecar metadata (replaces .meta.json)
│   │   └── {HHmmss}/
│   │       └── ...
│   └── {YYYY-MM-DD}/
│       └── ...
├── local/                              # watched folder / manual drops
│   ├── {YYYY-MM-DD}/
│   │   ├── {HHmmss}/
│   │   │   ├── document.pdf
│   │   │   ├── document.pdf.extracted.md
│   │   │   ├── document.pdf.comprehension.json
│   │   │   └── .hermes.json
│   │   └── ...
│   └── ...
└── .hermes/                            # system directory (not content)
    ├── hermes.db                       # SQLite (metadata + indexes)
    ├── preferences.txt                 # user preferences
    └── config.yaml                     # (optional local copy)
```

### Naming conventions

- **Account folders** use the account label from config (e.g. `john.azariah@gmail.com`)
- **Date folders** use ISO 8601 date: `YYYY-MM-DD`
- **Time folders** use `HHmmss` from the email received timestamp (UTC)
- **Thread files** use the provider's thread ID: `thread-{id}.md`
- **Attachments** keep their original filename, sanitised for filesystem safety
- **Stage outputs** use the convention `{original}.{stage}.{ext}`:
  - `.extracted.md` — text extraction output
  - `.comprehension.json` — LLM comprehension output
- **Local drops** (watch folders, manual) go under `local/` with the same date/time structure

### Thread handling

Email threads span multiple messages arriving at different times. Each message gets its own time folder. The `thread-{id}.md` file contains only that message's body. Thread reconstruction for display uses the thread ID to find all related folders.

If a reply arrives to an existing thread:
- New folder: `account/2026-05-08/093000/thread-abc123.md`
- Same thread ID as the original message
- DB links them: `SELECT * FROM messages WHERE thread_id = 'abc123' ORDER BY date`

We do NOT append to an existing `thread.md` file — each message is immutable once written.

### Sidecar metadata (.hermes.json)

Replaces the current `.meta.json`. One per folder, covers all files in that folder:

```json
{
  "version": 2,
  "source_type": "email_attachment",
  "account": "john.azariah@gmail.com",
  "provider_id": "msg-abc123",
  "thread_id": "thread-abc123",
  "sender": "noreply@telstra.com.au",
  "subject": "Your March 2026 bill",
  "received_at": "2026-03-15T14:30:22+11:00",
  "files": [
    {
      "name": "telstra-bill-march-2026.pdf",
      "mime_type": "application/pdf",
      "size_bytes": 145230,
      "sha256": "a1b2c3..."
    }
  ]
}
```

## SQLite Schema Changes

### What stays in SQLite

| Table | Content | Reason |
|-------|---------|--------|
| `documents` | id, folder_path, stage, category, confidence, timestamps | Workflow state |
| `messages` | id, account, provider_id, thread_id, folder_path, date | Message index (no body_text) |
| `documents_fts` | FTS5 virtual table | Keyword search index |
| `vec_documents` | sqlite-vec embeddings | Semantic search index |
| `learned_patterns` | sender_domain → document_type | Accumulation |
| `suggestions` | review queue | Operational |
| `sync_state` | per-account sync cursors | Operational |
| `contacts` | address book | Derived |

### What leaves SQLite

| Column | Moves to |
|--------|----------|
| `messages.body_text` | `thread-{id}.md` file |
| `documents.extracted_text` | `{name}.extracted.md` file |
| `documents.extracted_markdown` | `{name}.extracted.md` file |
| `documents.comprehension` | `{name}.comprehension.json` file |

### New columns

| Column | Purpose |
|--------|---------|
| `documents.folder_path` | Relative path to the folder containing this document |
| `messages.folder_path` | Relative path to the folder containing this message |

### Dropped columns (content moved to files)

- `messages.body_text`
- `documents.extracted_text`
- `documents.extracted_markdown`
- `documents.comprehension`
- `documents.comprehension_schema`

## Pipeline Changes

### Ingest (changed)

Before:
```
Download attachment → save to unclassified/ → INSERT body into messages table
```

After:
```
Download attachment → create account/date/time/ folder
                    → write thread-{id}.md (email body)
                    → save attachment to folder
                    → write .hermes.json sidecar
                    → INSERT into messages (folder_path, no body_text)
                    → INSERT into documents (folder_path)
```

### Extract (changed)

Before:
```
Read bytes from saved_path → extract → UPDATE documents SET extracted_text
```

After:
```
Read bytes from folder_path/{filename} → extract → write {filename}.extracted.md
                                                  → UPDATE documents SET stage = 'extracted'
                                                  → INSERT into FTS5 index
```

### Comprehend (changed)

Before:
```
Read extracted_text from DB → LLM → UPDATE documents SET comprehension
```

After:
```
Read {filename}.extracted.md from disk → LLM → write {filename}.comprehension.json
                                              → UPDATE documents SET stage, category, confidence
                                              → upsert learned_patterns, suggestions
```

### Embed (unchanged)

```
Read extracted text (from .extracted.md file) → embed → store vector in sqlite-vec
```

Embeddings stay in SQLite — they're only useful as a searchable index.

### Search (changed)

- **FTS5**: still works, but populated from files at extract time (not from a DB column)
- **sqlite-vec**: unchanged
- **Field search**: `json_extract()` on comprehension JSON — now needs to read the `.comprehension.json` file, OR we keep a thin indexed copy of key fields in the DB

### DB rebuild

If the SQLite file is lost or corrupted:
```
Scan archive folders → read .hermes.json sidecars → rebuild messages + documents tables
                     → read .extracted.md files → rebuild FTS5 index
                     → re-embed text → rebuild sqlite-vec
```

This is slow but possible. The archive is the source of truth.

## Categories

Categories are **emergent, not predefined**:

1. Comprehension produces `document_type` freely — the LLM decides
2. The `document_type` becomes the initial category
3. `learned_patterns` tracks which types appear and how often
4. The category list in the UI is `SELECT DISTINCT category FROM documents`
5. Users can rename, merge, or delete categories
6. User corrections feed back into preferences, which guide future comprehension
7. The hardcoded `canonicalCategories` map in `ComprehensionSchema.fs` is removed

## LLM Escalation

Local Ollama first, cloud fallback for hard documents:

```
Comprehend with local Ollama (llama3:8b)
  → confidence >= threshold? → done
  → confidence < threshold? → re-try with cloud LLM (Claude, GPT-4o)
                             → user opts in per document or globally
```

The `ChatProvider` algebra already supports multiple backends. The escalation decision is new logic in the `understand` stage.

## Migration

Existing archives (4,000+ docs in `unclassified/`) need migration:

1. **Phase 1**: New code writes to new structure. Old files stay in `unclassified/`.
2. **Phase 2**: Migration tool reads `.meta.json` sidecars, reconstructs account/date/time structure, moves files.
3. **Phase 3**: Remove old `unclassified/` code paths.

The migration can be incremental — new docs go to the new structure immediately, old docs are migrated in the background.

## Encryption (future door)

The file-based structure makes per-tenant encryption straightforward:

- Each account folder could be encrypted with a per-tenant key
- Key management is separate from file storage
- Hermes decrypts on read, encrypts on write
- Not building this now, but the folder-per-account structure makes it easy to add

## Open Questions

1. **Thread display**: How do we reconstruct thread view in the UI from separate message folders? Query by thread_id, load each `thread-{id}.md` in date order?

2. **Dedup across accounts**: Same attachment received in two accounts. Currently dedup by SHA256. With separate account folders, do we store twice or symlink?

3. **Watch folder structure**: Does `local/` use the same date/time nesting? Or just `local/{filename}` flat since there's no email context?

4. **Comprehension field indexing**: Do we keep a thin copy of key comprehension fields in SQLite for fast field-aware search, or always read from `.comprehension.json` files?

5. **FTS5 rebuild performance**: How long does it take to re-index 4,000+ `.extracted.md` files? Is this acceptable for a "rebuild from files" scenario?

---

## Appendix A — Lessons from Email Clients

What 35 years of email UI teaches us about Hermes.

### Pine/Maildir (1989–1995): Files as truth

Pine stored mail as text files. Maildir used one-file-per-message with no locking, safe for concurrent access across processes. When the index broke, you rebuilt from files.

**Applied**: Our file-first archive is exactly this pattern. The `account/date/time/` structure is essentially Maildir with richer hierarchy. Multiple Hermes instances could write to a shared archive safely.

### Outlook/PST (1996): The monolithic blob anti-pattern

Everything in one `.pst` file. Corrupt it, lose everything. Grows without bound. Search index breaks constantly.

**Applied**: SQLite-as-content-store is our PST. Design doc 28 moves us away from this. SQLite stays for indexes and metadata — things that are derived and rebuildable.

### Gmail (2004): Labels, not folders

A message can have multiple labels. "This is a receipt AND avalon-property AND tax-deductible." Search-first, not folder-first.

**Applied**: Categories should be **multi-label**, not single-category. A council rates bill for 1 Avalon St should be tagged with both `rates-and-tax` and `avalon-property`. The existing `tags` table already supports this — we should promote tags to be the primary categorisation mechanism instead of the single `category` column.

### Superhuman (2020s): Speed and command palette

Minimal interface built around keyboard shortcuts. Command palette (Cmd+K) for any action. Gamified inbox zero. Split inbox: Important vs Other.

**Applied**:
- **Command palette** — Hermes should have Cmd+K for quick actions: search, recategorise, navigate. React has good libraries for this (cmdk, kbar).
- **Speed** — document triage should feel instant. No loading spinners for the common case.
- **Inbox zero for documents** — celebrate when triage queue is empty.

### Hey (2020s): Screening and streams

First-time senders go through a screening step. Mail splits into three streams: Imbox (important), The Feed (newsletters), The Paper Trail (receipts/invoices).

**Applied**:
- **Screening** maps to our suggestion review — first time a new sender/type appears, the user validates. After that, auto-categorise.
- **Streams** map to our category groups — Hermes could present "Financial" (payslips, tax, bank statements), "Property" (agents, rates, insurance), "Household" (utilities, receipts) as high-level views, with individual categories inside.

### Spark (2025): Smart bundling and team features

Auto-groups notifications, newsletters, and personal mail. Custom swipe gestures. Inline calendar.

**Applied**:
- **Bundling** — group documents by sender or property or time period. "All 12 Telstra bills" as a collapsed group.
- **Swipe gestures** — for mobile (future), swipe to approve/reject suggestions.

### Common modern patterns

- **AI summaries** — thread/document summaries are now standard. Hermes already does this via comprehension.
- **Triage-first UX** — present decisions, not data. "Is this correct?" not "here's a list."
- **Focus modes** — review mode vs browse mode vs search mode.
- **Rich keyboard shortcuts** — power users live on the keyboard.

---

## Appendix B — UX Vision

### Core principle: Triage, not filing

The user should spend 30 seconds a day with Hermes, not 30 minutes. The system does the understanding; the user validates and refines.

### Confidence tiers drive the UX

```
High confidence (≥ 0.9)     → auto-categorised, appears in "Recent" feed
                               no user action required

Medium confidence (0.7–0.9) → shown with suggested category + fields
                               one-tap confirm or correct

Low confidence (< 0.7)      → queued for triage
                               user decides category, system learns
```

### Daily flow

```
User opens Hermes
  │
  ├── "3 need review" badge
  │     → triage panel: approve/reject/correct each
  │     → 30 seconds, done
  │
  ├── "15 new documents since yesterday"
  │     → scroll through recent feed
  │     → everything auto-categorised, just glance
  │
  └── Search / Browse when needed
        → "find Avalon property expenses"
        → field-aware search + faceted filtering
        → batch select → recategorise / export / tag
```

### Key UI components

**1. Command palette (Cmd+K)**
```
┌─────────────────────────────────────┐
│ 🔍 Search documents, actions...     │
│                                     │
│ Recent:                             │
│   📄 Telstra bill — March 2026     │
│   📄 Microsoft payslip — April     │
│                                     │
│ Actions:                            │
│   🔄 Sync email now                │
│   ⚙ Open settings                  │
│   📊 Show pipeline status          │
└─────────────────────────────────────┘
```

**2. Triage panel (the primary interaction)**
```
┌─────────────────────────────────────────────┐
│ Review (3)                                  │
│                                             │
│ ┌─────────────────────────────────────────┐ │
│ │ quarterly-report.pdf                    │ │
│ │ Hermes thinks: report (45%)             │ │
│ │ From: cfo@employer.com                  │ │
│ │                                         │ │
│ │ [✓ Accept]  [✎ Change to: ___]  [Skip] │ │
│ └─────────────────────────────────────────┘ │
│                                             │
│ ┌─────────────────────────────────────────┐ │
│ │ scan-001.pdf                            │ │
│ │ Hermes thinks: receipt (62%)            │ │
│ │ Detected: $45.00, 2026-04-15            │ │
│ │                                         │ │
│ │ [✓ Accept]  [✎ Change to: ___]  [Skip] │ │
│ └─────────────────────────────────────────┘ │
│                                             │
│ 🎉 All caught up!  (when empty)            │
└─────────────────────────────────────────────┘
```

**3. Document feed (browse recent)**
```
┌─────────────────────────────────────────────┐
│ Recent                          [Filter ▼]  │
│                                             │
│ Today                                       │
│   📄 Telstra bill   utility  $89.50   95%  │
│   📄 Payslip        payslip  $8,500   97%  │
│                                             │
│ Yesterday                                   │
│   📄 Council rates  avalon   $1,200   92%  │
│   📄 Water bill     utility  $65.00   88%  │
│                                             │
│ Last week                                   │
│   📄 Agent statement property $1,691  94%  │
│   ...                                       │
└─────────────────────────────────────────────┘
```

**4. Smart search with facets**
```
┌─────────────────────────────────────────────┐
│ 🔍 Avalon St                               │
│                                             │
│ Filters: [All types ▼] [2025-2026 ▼]       │
│          [All senders ▼]                    │
│                                             │
│ 8 results                                   │
│ ☑ Telstra bill — 1 Avalon St — $89.50      │
│ ☑ Council rates — 1 Avalon St — $1,200     │
│ ☐ Water bill — 1 Avalon St — $65.00        │
│ ☐ Insurance — 1 Avalon St — $850/yr        │
│                                             │
│ With selected:                              │
│ [Tag as... ▼] [Recategorise ▼] [Export ▼]  │
└─────────────────────────────────────────────┘
```

**5. First-run onboarding**
```
┌─────────────────────────────────────────────┐
│ 👋 Welcome to Hermes                        │
│                                             │
│ Tell me about yourself — I'll learn the     │
│ rest from your documents.                   │
│                                             │
│ ┌─────────────────────────────────────────┐ │
│ │ I have two investment properties:       │ │
│ │ - 1 Avalon St, Richmond                │ │
│ │ - 35 Manorwoods Dr, Wantirna           │ │
│ │                                         │ │
│ │ I work at Microsoft.                    │ │
│ │ Anything from ATO is tax-related.       │ │
│ └─────────────────────────────────────────┘ │
│                                             │
│ [Connect Gmail →]  [Connect Outlook →]      │
│           [Skip for now]                    │
└─────────────────────────────────────────────┘
```

### Interaction design principles

| Principle | Inspiration | Application |
|-----------|-------------|-------------|
| **Triage, not filing** | Superhuman, Hey | Suggestion panel is the primary interaction |
| **Labels, not folders** | Gmail | Multi-tag documents, emergent categories |
| **Screen first-timers** | Hey | First occurrence of a sender/type goes through review |
| **Command palette** | Superhuman, VS Code | Cmd+K for all actions |
| **Speed over features** | Superhuman | Keyboard shortcuts, instant transitions |
| **Celebrate completion** | Superhuman | "All caught up! 🎉" when triage is empty |
| **Smart bundling** | Spark | Group by sender, property, time period |
| **AI summaries are standard** | Everyone (2025+) | Comprehension summaries shown prominently |
| **Progressive disclosure** | Modern web | Simple default, detail on demand |
