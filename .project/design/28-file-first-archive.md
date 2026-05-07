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
