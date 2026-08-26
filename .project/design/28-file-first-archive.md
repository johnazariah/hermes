# 28 — File-First Archive Architecture

> Supersedes: current flat `unclassified/` archive model
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

## Decisions (confirmed 2026-05-07)

1. **Multi-label tags are the long-term categorisation model** — documents can have multiple tags (e.g. `utility-bill` + `avalon-property` + `tax-deductible`). A single category remains as a compatibility projection for current API and UI consumers.
2. **Thread is the unit of comprehension** — the LLM comprehends the entire thread (email conversation + all attachments), not individual attachments in isolation.
3. **One folder per thread** — all messages and attachments in a thread live in one folder.
4. **Folder path: `account/first-sender-domain/subject-slug--thread-prefix/`** — sender domain is the primary grouping; the thread suffix prevents collisions.
5. **Local drops: `local/date.filename-slug/`** — simpler structure for non-email documents.
6. **Files are date-prefixed** — `2026-03-15-message.md`, `2026-03-15-invoice.pdf` — disambiguates duplicate filenames.
7. **Default indexed fields + user favourites** — amount, vendor, date, property_address indexed by default. Users save favourite queries to trigger additional indexing.
8. **Categories are emergent** — no hardcoded category list. LLM produces tags freely, system accumulates. Users rename/merge/delete.

## Archive Folder Structure

```
{archive_root}/
├── {account}/
│   └── {sender-domain}/
│       └── {subject-slug}--{thread-prefix}/
│           ├── 2026-03-15-message-{message-prefix}.md
│           ├── 2026-03-15-invoice-{hash}.pdf
│           ├── 2026-03-15-invoice-{hash}.pdf.extracted.md
│           ├── thread.comprehension.json
│           └── .hermes.json
├── local/
│   └── 2026-04-01.bank-statement-q1/
│       ├── 2026-04-01-bank-statement-{hash}.pdf
│       ├── 2026-04-01-bank-statement-{hash}.pdf.extracted.md
│       └── .hermes.json
└── db.sqlite
```

### Naming conventions

- **Path segments** are lowercase filesystem-safe slugs.
- **Account folders** use the configured account label.
- **Thread folders** combine subject and a short thread ID to avoid recurring-subject collisions.
- **Messages** include a short provider message ID.
- **Attachments** retain their extension and include a short content hash.
- **Stage outputs** use the convention `{original}.{stage}.{ext}`:
  - `.extracted.md` — text extraction output
  - `thread.comprehension.json` — latest thread-level LLM output
- **Local drops** use `local/{date}.{filename-slug}/`.

### Thread handling

All messages and attachments in a provider thread share one folder. Each message is an immutable date-prefixed Markdown file; thread reconstruction reads message files in filename order. `.hermes.json` merges new file entries without replacing prior thread metadata.

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
| `documents` | id, saved_path, stage, compatibility category/confidence, timestamps | Workflow state and API compatibility |
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
| `messages.body_text` | Date-prefixed message Markdown file |
| `documents.extracted_text` | `{name}.extracted.md` file |
| `documents.extracted_markdown` | `{name}.extracted.md` file |
| `documents.comprehension` | `thread.comprehension.json` file |

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
Download message → create account/domain/subject--thread/ folder
                 → write date-prefixed message Markdown
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
Read thread messages + extracted files → LLM → write thread.comprehension.json
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

1. Comprehension produces tags freely.
2. Tags accumulate in the `tags` table and can represent type, property, purpose, and tax relevance at once.
3. User corrections feed preferences and learned patterns.
4. The current single `category` remains a compatibility projection while API and UI consumers move to tags.

### Real-world example folder structure

```
~/Documents/Hermes/
├── john.azariah@gmail.com/
│   ├── telstra.com.au/
│   │   ├── your-march-2026-bill--thread-a/
│   │   │   ├── 2026-03-15-message.md
│   │   │   ├── 2026-03-15-telstra-bill-march.pdf
│   │   │   ├── 2026-03-15-telstra-bill-march.pdf.extracted.md
│   │   │   ├── thread.comprehension.json
│   │   │   └── .hermes.json
│   │   └── your-april-2026-bill--thread-b/
│   │       └── ...
│   ├── raywhite.com.au/
│   │   └── flooding-fix-1-avalon--thread-c/
│   │       ├── 2026-03-15-ray-initial-report.md
│   │       ├── 2026-03-16-bob-plumber-quote.pdf
│   │       ├── 2026-03-18-nrma-claim-form.pdf
│   │       ├── 2026-03-20-john-reply.md
│   │       ├── thread.comprehension.json
│   │       └── .hermes.json
│   └── microsoft.com/
│       └── your-march-payslip--thread-d/
│           └── ...
├── local/
│   └── 2026-04-01.bank-statement-q1/
│       └── ...
└── db.sqlite
```

### Thread-level comprehension output

The `thread.comprehension.json` covers the entire thread — conversation context + per-attachment fields:

```json
{
  "thread_summary": "Ray White reported flooding at 1 Avalon St. Bob the plumber quoted $2,400. NRMA claim filed.",
  "participants": ["ray@raywhite.com.au", "bob@plumbing.com.au", "claims@nrma.com.au"],
  "tags": ["property", "avalon-property", "insurance-claim"],
  "documents": [
    {
      "file": "2026-03-16-bob-plumber-quote.pdf",
      "document_type": "invoice",
      "confidence": 0.92,
      "fields": { "vendor": "Bob's Plumbing", "amount": 2400.00, "date": "2026-03-16" }
    },
    {
      "file": "2026-03-18-nrma-claim-form.pdf",
      "document_type": "insurance-claim",
      "confidence": 0.88,
      "fields": { "provider": "NRMA", "claim_number": "CLM-2026-1234", "property_address": "1 Avalon St" }
    }
  ]
}
```

`document_type` seeds the compatibility category, while the richer tag set is persisted separately. `learned_patterns` tracks sender/type evidence and user corrections guide future prompts.

## LLM Escalation

Local Ollama first, cloud fallback for hard documents:

```
Comprehend with the configured local provider
  → confidence >= threshold? → done
  → confidence < threshold? → re-try with cloud LLM (Claude, GPT-4o)
                             → user opts in per document or globally
```

The `ChatProvider` algebra supports multiple backends. Escalation must remain an explicit opt-in policy rather than a silent fallback.

## Legacy Archive Compatibility

New ingestion writes the structured layout. Existing `unclassified/` files remain readable through `saved_path` compatibility. A migration tool can incrementally read legacy `.meta.json` sidecars, reconstruct the structured destination, verify hashes, and then retire the old paths.

Legacy `DocumentManagement.reclassify` still moves files by category and `reextract` still resets V4 projection fields. File-first behavior requires those operations to become metadata/tag updates and V5 stage-completion invalidation.

## Encryption (future door)

The file-based structure makes per-tenant encryption straightforward:

- Each account folder could be encrypted with a per-tenant key
- Key management is separate from file storage
- Hermes decrypts on read, encrypts on write
- Not building this now, but the folder-per-account structure makes it easy to add

## Open Questions

1. **Dedup across accounts**: should identical attachments be stored once, copied, or linked?
2. **Comprehension field indexing**: which fields deserve dedicated SQLite projections?
3. **FTS5 rebuild performance**: how long does a full archive rebuild take at production scale?
4. **Migration verification**: what audit manifest proves every legacy file moved safely?

---

## Appendix A — Lessons from Email Clients

What 35 years of email UI teaches us about Hermes.

### Pine/Maildir (1989–1995): Files as truth

Pine stored mail as text files. Maildir used one-file-per-message with no locking, safe for concurrent access across processes. When the index broke, you rebuilt from files.

**Applied**: The account/domain/thread archive is this pattern with richer hierarchy. Multiple Hermes instances can write distinct immutable files without using SQLite as a content lock.

### Outlook/PST (1996): The monolithic blob anti-pattern

Everything in one `.pst` file. Corrupt it, lose everything. Grows without bound. Search index breaks constantly.

**Applied**: SQLite-as-content-store is our PST. Design doc 28 moves us away from this. SQLite stays for indexes and metadata — things that are derived and rebuildable.

### Gmail (2004): Labels, not folders

A message can have multiple labels. "This is a receipt AND avalon-property AND tax-deductible." Search-first, not folder-first.

**Applied**: The `tags` table supports multi-label categorisation. The single category remains only as a compatibility projection while consumers transition to tags.

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
