# Hermes — Data Model

## Storage Layout

```
~/Documents/Hermes/                  (archive root — configurable)
├── db.sqlite                      (single database file)
├── unclassified/                  (intake queue — files land here first)
├── bank-statements/
├── insurance/
├── invoices/
├── legal/
├── medical/
├── donations/
├── payslips/
├── property/
│   ├── manorwoods/                (user-created subcategory)
│   └── avalon/
├── rates-and-tax/
├── receipts/
├── subscriptions/
├── tax/
├── utilities/
└── unsorted/                      (classified as "don't know" — still indexed)
```

## Configuration Files

```
~/.config/hermes/                  (macOS) or %APPDATA%\hermes\ (Windows)
├── config.yaml                    (accounts, archive dir, settings)
├── rules.yaml                     (classification rules)
└── tokens/
    ├── gmail_john-personal.json   (per-account OAuth tokens)
    ├── gmail_john-work.json
    └── gmail_smitha.json
```

### config.yaml

```yaml
archive_dir: ~/Documents/Hermes

# Gmail OAuth client credential (shared across accounts)
credentials: ~/.config/hermes/gmail_credentials.json

accounts:
  - label: john-personal
    provider: gmail
  - label: john-work
    provider: gmail
  - label: smitha
    provider: gmail

# Sync settings
sync_interval_minutes: 15
min_attachment_size: 20480          # skip inline images < 20KB

# Watched folders (extension of the same intake pipeline)
watch_folders:
  - path: ~/Downloads
    patterns: ["*.pdf", "*statement*", "*invoice*", "*receipt*", "*payslip*"]
  - path: ~/Desktop
    patterns: ["*.pdf"]

# Ollama settings
ollama:
  enabled: true
  base_url: http://localhost:11434
  embedding_model: nomic-embed-text     # 768-dim
  vision_model: llava                    # for scanned doc OCR
  instruct_model: llama3.2               # for field extraction

# Fallbacks when Ollama unavailable
fallback:
  embedding: onnx                        # ONNX Runtime with bundled model (CPU)
  ocr: azure-document-intelligence       # Azure AI Document Intelligence (cloud, key required)

# Azure Document Intelligence (optional, for machines without GPU)
azure:
  document_intelligence_endpoint: ""
  document_intelligence_key: ""
```

---

## SQLite Schema

Single file: `~/Documents/Hermes/db.sqlite`

### Core Tables

```sql
PRAGMA journal_mode = WAL;           -- concurrent reads during writes
PRAGMA foreign_keys = ON;

-- ============================================================
-- MESSAGES: one row per email processed (dedup boundary)
-- ============================================================
CREATE TABLE messages (
    gmail_id        TEXT NOT NULL,
    account         TEXT NOT NULL,
    sender          TEXT,
    subject         TEXT,
    date            TEXT,            -- ISO 8601 from email Date header
    label_ids       TEXT,            -- JSON array of Gmail label IDs
    has_attachments INTEGER NOT NULL DEFAULT 0,
    processed_at    TEXT NOT NULL DEFAULT (datetime('now')),
    PRIMARY KEY (account, gmail_id)
);

-- Future: add body_text TEXT column when email body indexing is implemented.
-- This will feed into a unified FTS5 index alongside document extracted text.

CREATE INDEX idx_msg_date    ON messages(date);
CREATE INDEX idx_msg_sender  ON messages(sender);
CREATE INDEX idx_msg_account ON messages(account);

-- ============================================================
-- DOCUMENTS: one row per file in the archive
-- Covers email attachments AND locally-watched files.
-- ============================================================
CREATE TABLE documents (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,

    -- Source tracking (nullable — local files have no gmail_id)
    source_type     TEXT NOT NULL,   -- 'email_attachment', 'watched_folder', 'manual_drop'
    gmail_id        TEXT,            -- NULL for non-email sources
    account         TEXT,            -- NULL for non-email sources

    -- Email context (populated for email attachments)
    sender          TEXT,
    subject         TEXT,
    email_date      TEXT,            -- from email Date header

    -- File metadata
    original_name   TEXT,
    saved_path      TEXT NOT NULL,   -- relative to archive root
    category        TEXT NOT NULL,   -- derived from folder path
    mime_type       TEXT,
    size_bytes      INTEGER,
    sha256          TEXT NOT NULL,

    -- Source folder (for watched-folder documents)
    source_path     TEXT,            -- original path before copy to unclassified/

    -- Phase 2: extraction (NULL until extracted)
    extracted_text  TEXT,            -- raw text from PDF/OCR
    extracted_date  TEXT,            -- document date (may differ from email date)
    extracted_amount REAL,           -- dollar amount if found
    extracted_vendor TEXT,           -- vendor/payee name
    extracted_abn   TEXT,            -- ABN/ACN if found
    ocr_confidence  REAL,            -- 0.0–1.0, NULL if native text
    extraction_method TEXT,          -- 'pdfplumber', 'tesseract', 'ollama_vision'
    extracted_at    TEXT,

    -- Phase 3: embedding
    embedded_at     TEXT,            -- NULL until embeddings generated
    chunk_count     INTEGER,         -- number of chunks in vec_chunks

    -- Timestamps
    ingested_at     TEXT NOT NULL DEFAULT (datetime('now')),

    FOREIGN KEY (account, gmail_id) REFERENCES messages(account, gmail_id)
);

CREATE INDEX idx_doc_category   ON documents(category);
CREATE INDEX idx_doc_date       ON documents(email_date);
CREATE INDEX idx_doc_sender     ON documents(sender);
CREATE INDEX idx_doc_sha256     ON documents(sha256);
CREATE INDEX idx_doc_account    ON documents(account);
CREATE INDEX idx_doc_source     ON documents(source_type);
CREATE INDEX idx_doc_extracted  ON documents(extracted_at);
CREATE INDEX idx_doc_embedded   ON documents(embedded_at);

-- ============================================================
-- SYNC STATE: track last sync per account
-- ============================================================
CREATE TABLE sync_state (
    account         TEXT PRIMARY KEY,
    last_history_id TEXT,            -- Gmail history ID for incremental sync
    last_sync_at    TEXT,
    message_count   INTEGER NOT NULL DEFAULT 0
);
```

### FTS5 Full-Text Search

```sql
-- Keyword search across extracted text and metadata (documents only in v1)
CREATE VIRTUAL TABLE documents_fts USING fts5(
    sender,
    subject,
    original_name,
    category,
    extracted_text,
    extracted_vendor,
    content='documents',
    content_rowid='id'
);

-- Future: when email body indexing is added, either extend this FTS table
-- or create a unified content_fts that spans both documents and messages.

-- Triggers to keep documents_fts in sync
CREATE TRIGGER doc_fts_insert AFTER INSERT ON documents BEGIN
    INSERT INTO documents_fts(rowid, sender, subject, original_name, category, extracted_text, extracted_vendor)
    VALUES (new.id, new.sender, new.subject, new.original_name, new.category, new.extracted_text, new.extracted_vendor);
END;

CREATE TRIGGER doc_fts_update AFTER UPDATE ON documents BEGIN
    INSERT INTO documents_fts(documents_fts, rowid, sender, subject, original_name, category, extracted_text, extracted_vendor)
    VALUES ('delete', old.id, old.sender, old.subject, old.original_name, old.category, old.extracted_text, old.extracted_vendor);
    INSERT INTO documents_fts(rowid, sender, subject, original_name, category, extracted_text, extracted_vendor)
    VALUES (new.id, new.sender, new.subject, new.original_name, new.category, new.extracted_text, new.extracted_vendor);
END;
```

### sqlite-vec Vector Store

```sql
-- Embeddings for semantic search
-- Using sqlite-vec extension (https://github.com/asg017/sqlite-vec)
CREATE VIRTUAL TABLE vec_chunks USING vec0(
    document_id   INTEGER,
    chunk_index   INTEGER,           -- for multi-page docs split into chunks
    embedding     float[768]         -- nomic-embed-text = 768 dimensions
);
```

### Chunking Strategy
- Split extracted text into ~500-char chunks with 100-char overlap.
- One embedding per chunk.
- Store `document_id + chunk_index` so results map back to the source file.
- Typical document = 1–5 chunks.

---

## Deduplication

SHA256 of file content is the dedup key.

| Scenario | Behaviour |
|----------|-----------|
| Same file from two email accounts | One file on disk. Two `documents` rows, both pointing to the same `saved_path`. |
| Same file re-downloaded from `~/Downloads` | Detected by SHA256. No new file or DB row. |
| Slightly different version of same document | Different SHA256 → stored as separate file. (Future: near-duplicate detection.) |

---

## Query Patterns

```sql
-- Keyword: "CBA statements from 2025"
SELECT d.saved_path, d.email_date, d.extracted_amount
FROM documents d
JOIN documents_fts f ON d.id = f.rowid
WHERE documents_fts MATCH 'CBA statement'
  AND d.email_date >= '2025-01-01'
ORDER BY d.email_date DESC;

-- Semantic: "plumbing repair receipt for the rental property"
SELECT d.saved_path, d.email_date, d.category, v.distance
FROM vec_chunks v
JOIN documents d ON d.id = v.document_id
WHERE v.embedding MATCH ?query_embedding
  AND k = 20
ORDER BY v.distance
LIMIT 10;

-- Combined: semantic within a category
SELECT d.saved_path, d.email_date, v.distance
FROM vec_chunks v
JOIN documents d ON d.id = v.document_id
WHERE v.embedding MATCH ?query_embedding
  AND k = 50
  AND d.category LIKE 'property/%'
ORDER BY v.distance
LIMIT 10;

-- Future: when email body indexing is added, a unified query will search
-- across both documents and email bodies in a single result set.

-- Stats
SELECT category, COUNT(*) as files, SUM(size_bytes) as total_bytes,
       SUM(CASE WHEN extracted_at IS NOT NULL THEN 1 ELSE 0 END) as extracted,
       SUM(CASE WHEN embedded_at IS NOT NULL THEN 1 ELSE 0 END) as embedded
FROM documents
GROUP BY category
ORDER BY files DESC;
```
