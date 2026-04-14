# 04 — Data Model

## Storage

| Store | Type | Purpose |
|-------|------|---------|
| `db.sqlite` | SQLite (WAL mode) | Document metadata, comprehension, sync state, embeddings |
| `unclassified/` | Filesystem | All ingested files (never moved) |
| `document_chunks` | SQLite table | Vector embeddings for semantic search |
| `documents_fts` | FTS5 virtual table | Full-text keyword search |

## Documents Table

```sql
CREATE TABLE documents (
    id                        INTEGER PRIMARY KEY AUTOINCREMENT,
    stage                     TEXT NOT NULL DEFAULT 'received',

    -- Source identity (set by producer, immutable)
    source_type               TEXT NOT NULL,  -- 'email_attachment', 'email_body', 'watched_folder'
    gmail_id                  TEXT,
    thread_id                 TEXT,
    account                   TEXT,
    sender                    TEXT,
    subject                   TEXT,
    email_date                TEXT,

    -- File identity (set by producer, immutable)
    original_name             TEXT,
    saved_path                TEXT NOT NULL,   -- IMMUTABLE after ingest
    category                  TEXT NOT NULL,   -- metadata, not filesystem
    mime_type                 TEXT,
    size_bytes                INTEGER,
    sha256                    TEXT NOT NULL,
    source_path               TEXT,

    -- Extraction output (set by extract stage)
    extracted_text            TEXT,
    extracted_markdown        TEXT,
    extraction_method         TEXT,
    ocr_confidence            REAL,
    extraction_confidence     REAL,
    extracted_at              TEXT,

    -- Field extraction (set by extract stage, regex-based)
    extracted_date            TEXT,
    extracted_amount          REAL,
    extracted_vendor          TEXT,
    extracted_abn             TEXT,

    -- Comprehension output (set by comprehend stage)
    comprehension             TEXT,    -- structured JSON
    comprehension_schema      TEXT,    -- version identifier

    -- Classification (byproduct of comprehension)
    classification_tier       TEXT,    -- 'comprehension', 'content', 'llm'
    classification_confidence REAL,

    -- Embedding output (set by embed stage)
    embedded_at               TEXT,
    chunk_count               INTEGER,

    -- Metadata
    starred                   INTEGER NOT NULL DEFAULT 0,
    ingested_at               TEXT NOT NULL DEFAULT (datetime('now')),

    FOREIGN KEY (account, gmail_id) REFERENCES messages(account, gmail_id)
);
```

### Stage values

```
received → extracted → comprehended → embedded
                                   ↘ failed
```

### Indexes

```sql
CREATE INDEX idx_doc_stage     ON documents(stage);
CREATE INDEX idx_doc_category  ON documents(category);
CREATE INDEX idx_doc_sha256    ON documents(sha256);
CREATE INDEX idx_doc_sender    ON documents(sender);
CREATE INDEX idx_doc_account   ON documents(account);
CREATE INDEX idx_doc_date      ON documents(email_date);
```

## Property Bag Mapping

The `Document.T = Map<string, obj>` maps 1:1 to the documents table:
- `Document.fromRow` is identity (DB row IS the property bag)
- `Document.toUpdateParams` flattens all columns for a full-row UPDATE
- `Document.persist` writes the entire row (write-aside pattern)

## Sync State

```sql
CREATE TABLE sync_state (
    account             TEXT PRIMARY KEY,
    last_sync_at        TEXT,
    message_count       INTEGER NOT NULL DEFAULT 0,
    -- backfill tracking
    backfill_page_token TEXT,
    backfill_total_estimate INTEGER,
    backfill_scanned    INTEGER NOT NULL DEFAULT 0,
    backfill_completed  INTEGER NOT NULL DEFAULT 0,
    backfill_started_at TEXT
);
```

## Config (YAML)

```yaml
archive_dir: ~/Documents/Hermes/
credentials: ~/.hermes/gmail_credentials.json

accounts:
  - label: john@gmail.com
    provider: gmail
    backfill:
      enabled: true
      batch_size: 200

watch_folders:
  - path: ~/Downloads/
    patterns: ["*.pdf"]

sync_interval_minutes: 15
min_attachment_size: 20480

ollama:
  enabled: true
  base_url: http://localhost:11434
  instruct_model: llama3:8b
  embedding_model: nomic-embed-text
  shared_gpu: true
  max_hold_seconds: 180

pipeline:
  extract_concurrency: 4    # 0 = auto (ProcessorCount / 2)
  llm_concurrency: 1
  email_concurrency: 5
```
