# Hermes — Document Feed & Consumer Model

> Design doc for making Hermes a platform that serves a canonical document feed to downstream consumers.  
> Created: 2026-04-01

---

## 1. Problem

Hermes was designed as a self-contained document intelligence service. Now it needs to be a **platform** — Osprey (tax processing) is the first consumer, but insurance trackers, expense managers, and other apps will follow.

The naive approach — tagging documents with consumer-specific state (`"tax-processed"`, `"insurance-reviewed"`) — has fundamental problems:

- Hermes must know about every consumer
- Consumers fight over shared mutable state on documents they don't own
- Adding a new consumer requires Hermes schema changes
- No way to replay or reprocess — once tagged, it's invisible
- Consumer failures corrupt platform state

### The right model: event log with consumer cursors

Hermes produces a **canonical, append-only document feed** ordered by a monotonically increasing sequence number. Each consumer maintains its own cursor — a high-water mark of the last document it successfully processed. Hermes never knows or cares about consumer state.

```
Hermes (producer)                 Consumers
┌──────────────┐                  ┌─────────────────┐
│ doc #1       │                  │ Osprey           │
│ doc #2       │  ←── poll ───    │ cursor: #12,487  │
│ ...          │                  │ (my state, my DB)│
│ doc #12,487  │                  └─────────────────┘
│ doc #12,488  │                  ┌─────────────────┐
│ doc #12,489  │  ←── poll ───    │ Insurance App    │
│ ...          │                  │ cursor: #11,203  │
└──────────────┘                  └─────────────────┘
```

---

## 2. Design

### 2.1 — Document sequence number

The `documents.id` column (INTEGER PRIMARY KEY AUTOINCREMENT) is already a monotonically increasing sequence. Documents are never deleted, only soft-archived. This means `id` is a natural, gap-free, stable sequence number.

**No schema change needed.** The existing `documents.id` is the feed position.

### 2.2 — Feed semantics

The document feed has **at-least-once** delivery semantics:

- **Producer guarantee**: Every ingested document gets an `id`. IDs never decrease. Documents are never removed.
- **Consumer guarantee**: Consumers poll for documents with `id > cursor`. They may see the same document twice if they crash between processing and saving their cursor. Consumers must be idempotent.
- **Ordering**: By `id` ascending — which is insertion order. This is the order documents were ingested, not the order they were classified or extracted.

### 2.3 — Document states visible to consumers

Consumers need to know what stage a document has reached. The feed includes processing state:

| State | Meaning | How consumer knows |
|-------|---------|-------------------|
| **Ingested** | File saved to archive, basic metadata recorded | `ingested_at IS NOT NULL` |
| **Classified** | Category assigned, moved to category folder | `category != 'unclassified'` |
| **Extracted** | Text extracted, structured fields populated | `extracted_at IS NOT NULL` |
| **Embedded** | Vector embeddings generated | `embedded_at IS NOT NULL` |

A consumer like Osprey only cares about **extracted** documents (it needs `extracted_text`, `extracted_amount`, etc.). It can filter: `WHERE extracted_at IS NOT NULL AND id > @cursor`.

A consumer that needs full text search might only care about **ingested** documents and does its own extraction.

### 2.4 — Consumer cursor storage

Cursors live in the **consumer's** database/config, not in Hermes. Examples:

**Osprey** (Python, SQLite):
```sql
-- In Osprey's own database
CREATE TABLE hermes_cursor (
    consumer_id  TEXT PRIMARY KEY,
    last_doc_id  INTEGER NOT NULL,
    updated_at   TEXT NOT NULL
);
-- Single row: ('osprey-tax', 12487, '2026-04-01T10:30:00Z')
```

**Any MCP client** (stored however the client wants):
```json
{ "hermes_cursor": 12487 }
```

### 2.5 — Replay and reprocessing

Because Hermes never mutates or deletes feed state:

- **New consumer**: Starts at cursor `0`, processes the entire archive from the beginning
- **Reprocess**: Consumer resets its cursor to `0` (or any earlier position)
- **Catch up after downtime**: Consumer resumes from its last saved cursor — no data loss
- **Schema migration in consumer**: Consumer resets cursor, reprocesses everything with new logic

This is the same pattern as Kafka consumer groups, database replication, and event sourcing — proven at scale.

### 2.6 — Document updates

When a document is re-extracted or re-classified, the existing row is updated (same `id`). Consumers that only poll by `id > cursor` won't see the update.

Two options:

**Option A (simple, recommended for v1)**: Consumers are expected to be idempotent and process documents once. If extraction improves, consumer resets cursor.

**Option B (future)**: Add a `version` or `updated_at` column. Feed query becomes: `WHERE id > @cursor OR (id <= @cursor AND updated_at > @last_poll)`. More complex but handles re-extraction without full replay.

**Decision**: Option A for now. Option B when a consumer needs it.

---

## 3. MCP Feed Tools

### `hermes_list_documents`

The primary feed tool. Returns documents in `id` order.

```json
{
  "name": "hermes_list_documents",
  "description": "List documents from the archive in canonical order. Use since_id for cursor-based pagination. Consumers should store the highest returned id as their cursor.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "since_id": {
        "type": "integer",
        "default": 0,
        "description": "Return documents with id > since_id. Use 0 to start from the beginning."
      },
      "state": {
        "type": "string",
        "enum": ["ingested", "classified", "extracted", "embedded", "any"],
        "default": "any",
        "description": "Filter by processing state. 'extracted' = has extracted text and fields."
      },
      "category": {
        "type": "string",
        "description": "Filter by category (e.g. 'invoices', 'payslips'). Optional."
      },
      "limit": {
        "type": "integer",
        "default": 100,
        "description": "Maximum documents to return per page."
      }
    }
  }
}
```

**Returns**: Array of document records including all metadata + extracted fields, ordered by `id ASC`. The consumer saves `max(id)` from the response as its new cursor.

### `hermes_get_document_content`

Retrieve the full content of a document — extracted text, structured markdown, or raw file content for text-based formats.

```json
{
  "name": "hermes_get_document_content",
  "description": "Get the full content of a document. Returns extracted text, structured markdown, and for text-based files (CSV, TXT) the raw file content.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "document_id": { "type": "integer" },
      "format": {
        "type": "string",
        "enum": ["text", "markdown", "raw"],
        "default": "markdown",
        "description": "'text' = plain extracted text. 'markdown' = structured with frontmatter and tables. 'raw' = original file content (text-based files only)."
      }
    },
    "required": ["document_id"]
  }
}
```

---

## 4. SQL Implementation

### Feed query

```sql
-- Basic feed: all documents after cursor
SELECT * FROM documents
WHERE id > @since_id
ORDER BY id ASC
LIMIT @limit;

-- Filtered feed: only extracted documents in specific categories
SELECT * FROM documents
WHERE id > @since_id
  AND extracted_at IS NOT NULL
  AND category IN ('invoices', 'payslips', 'bank-statements')
ORDER BY id ASC
LIMIT @limit;
```

No new tables. No new indexes (primary key index on `id` already handles this). Just a new query path exposed via MCP.

---

## 5. Consumer Protocol

Any downstream app that uses Hermes as a document platform follows this protocol:

```
1. CONNECT to Hermes MCP (localhost:21740)
2. LOAD cursor from own storage (default: 0)
3. POLL: hermes_list_documents(since_id=cursor, state="extracted", limit=100)
4. For each document:
   a. Decide if relevant (filter by category, sender, etc.)
   b. If relevant: hermes_get_document_content(id, format="markdown")
   c. Process (extract tax events, check insurance, etc.)
   d. Post results to own backend
5. SAVE cursor = max(id) from response
6. SLEEP interval, then goto 3
```

**Idempotency requirement**: Step 4d must be safe to repeat. If the consumer crashes between processing and saving the cursor, it will re-receive the same documents on the next poll. POST endpoints should handle duplicates (upsert by document hash or source ID).

---

## 6. What This Replaces

| Old pattern | New pattern |
|-------------|-------------|
| `hermes_tag_document("tax-processed")` | Consumer stores cursor in own DB |
| Hermes knows about consumer state | Hermes is stateless w.r.t. consumers |
| One consumer per document | N consumers, each with own cursor |
| Replay = reset tags on all docs | Replay = reset cursor to 0 |
| New consumer = add tag logic to Hermes | New consumer = start polling at id=0 |

---

## 7. Implementation Phases

| Phase | What | Effort |
|-------|------|--------|
| **F1** | `hermes_list_documents` MCP tool + `McpTools` handler | Medium |
| **F2** | `hermes_get_document_content` MCP tool (text + markdown + raw) | Medium |
| **F3** | Consumer protocol documentation (for Osprey team) | Small |
| **F4** | Osprey `OspreyTaxProcessor` — first consumer implementation | Large (Osprey-side) |

F1 + F2 are Hermes-side. F3 + F4 are consumer-side.

---

## 8. Open Questions

| # | Question | Leaning |
|---|----------|---------|
| 1 | Should Hermes offer a "subscription" model (push via webhook/SSE) instead of polling? | Polling first — simpler, works for all MCP clients. Push is future optimisation |
| 2 | Should `hermes_list_documents` support multiple category filters? | Yes — `categories: ["invoices", "payslips"]` as an array parameter |
| 3 | Should we add a `hermes_get_feed_stats` tool (total docs, max id, counts by state)? | Yes — helps consumers estimate catch-up time |
| 4 | Should document deletions be tracked (tombstone records)? | Not for v1 — documents are never deleted |
| 5 | Should we offer a `hermes_register_consumer` tool for discoverability? | No — consumers are anonymous. Hermes doesn't need to know. |
