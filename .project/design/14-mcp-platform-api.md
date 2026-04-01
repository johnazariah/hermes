# Hermes — MCP Platform API

> Design doc for evolving the MCP server from read-only document search to a full platform API.  
> Builds on: doc 05 (MCP Server Design), doc 13 (Document Feed & Consumers).  
> Created: 2026-04-01

---

## 1. Current State

The MCP server exposes 5 read-only tools:

| Tool | Purpose | Status |
|------|---------|--------|
| `hermes_search` | FTS5 keyword search | ✅ Implemented |
| `hermes_get_document` | Document metadata by ID/path | ✅ Implemented |
| `hermes_list_categories` | Category list with counts | ✅ Implemented |
| `hermes_stats` | Archive statistics | ✅ Implemented |
| `hermes_read_file` | Extracted text by path | ✅ Implemented |

This covers "AI agent asks questions about documents." It does not cover:
- Platform consumers (Osprey) that need to process documents systematically
- Write operations (reminders, actions)
- Document content in structured formats
- Feed-based access patterns

---

## 2. Proposed Tool Inventory

### Tier 1 — Platform Feed (doc 13)

| Tool | Purpose | Read/Write |
|------|---------|------------|
| `hermes_list_documents` | Cursor-based document feed | Read |
| `hermes_get_document_content` | Full content: text, markdown, or raw | Read |
| `hermes_get_feed_stats` | Total docs, max id, counts by processing state | Read |

### Tier 2 — Reminders (doc 12)

| Tool | Purpose | Read/Write |
|------|---------|------------|
| `hermes_list_reminders` | Active/overdue/upcoming bill reminders | Read |
| `hermes_update_reminder` | Mark paid, snooze, dismiss | Write |

### Tier 3 — Document Management

| Tool | Purpose | Read/Write |
|------|---------|------------|
| `hermes_reclassify` | Move a document to a different category | Write |
| `hermes_reextract` | Queue a document for re-extraction | Write |
| `hermes_get_processing_queue` | Documents pending classify/extract/embed | Read |

### Tier 4 — Future (from doc 10)

| Tool | Purpose | Read/Write |
|------|---------|------------|
| `hermes_create_alert` | Surface a notification | Write |
| `hermes_send_email` | Draft + send via Gmail (confirmation required) | Write |
| `hermes_invoke_skill` | Generic skill dispatch | Write |

---

## 3. Tool Specifications

### `hermes_list_documents`

Specified in doc 13 section 3. Cursor-based pagination, state filtering, category filtering.

### `hermes_get_document_content`

Specified in doc 13 section 3. Three formats: `text` (plain extracted), `markdown` (structured with frontmatter, tables, headings), `raw` (original file bytes as text — CSV, TXT only).

### `hermes_get_feed_stats`

```json
{
  "name": "hermes_get_feed_stats",
  "description": "Get document feed statistics: total count, max document ID, and counts by processing state. Useful for consumers to estimate catch-up work.",
  "inputSchema": {
    "type": "object",
    "properties": {}
  }
}
```

**Returns**:
```json
{
  "total_documents": 2163,
  "max_document_id": 2163,
  "by_state": {
    "ingested": 2163,
    "classified": 2100,
    "extracted": 1890,
    "embedded": 1650
  },
  "by_category": {
    "invoices": 239,
    "bank-statements": 104,
    "unsorted": 1736,
    "receipts": 39,
    "tax": 34,
    "payslips": 2
  },
  "oldest_document": "2020-03-15T...",
  "newest_document": "2026-04-01T..."
}
```

### `hermes_reclassify`

```json
{
  "name": "hermes_reclassify",
  "description": "Move a document to a different category. Moves the physical file and updates the database.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "document_id": { "type": "integer", "description": "Document ID" },
      "new_category": { "type": "string", "description": "Target category (e.g. 'invoices', 'tax/2025')" }
    },
    "required": ["document_id", "new_category"]
  }
}
```

**Safety**: Safe — reversible, logged. Moves the file on disk and updates `documents.category`.

### `hermes_reextract`

```json
{
  "name": "hermes_reextract",
  "description": "Queue a document for re-extraction. Clears existing extracted fields and re-runs the extraction pipeline on next sync cycle.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "document_id": { "type": "integer" }
    },
    "required": ["document_id"]
  }
}
```

**Implementation**: Sets `extracted_at = NULL`, `extracted_text = NULL`, etc. The next `extractBatch` cycle picks it up.

### `hermes_get_processing_queue`

```json
{
  "name": "hermes_get_processing_queue",
  "description": "Get counts and sample documents for each processing stage queue.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "stage": {
        "type": "string",
        "enum": ["unclassified", "unextracted", "unembedded", "all"],
        "default": "all"
      },
      "limit": { "type": "integer", "default": 10, "description": "Sample documents per stage" }
    }
  }
}
```

**Returns**:
```json
{
  "unclassified": { "count": 5, "documents": [...] },
  "unextracted": { "count": 23, "documents": [...] },
  "unembedded": { "count": 156, "documents": [...] }
}
```

---

## 4. Transport

No changes to transport. All tools run over the existing:
- **Primary**: Streamable HTTP on `localhost:21740/mcp`
- **Secondary**: stdio shim (`hermes mcp`)

---

## 5. Safety Model

| Tool | Level | Policy |
|------|-------|--------|
| All read tools | **Safe** | Execute immediately |
| `hermes_reclassify` | **Safe** | Execute, log the move |
| `hermes_reextract` | **Safe** | Execute, document re-enters pipeline |
| `hermes_update_reminder` | **Safe** | Execute, update DB |
| `hermes_create_alert` | **Safe** | Execute, surface in UI |
| `hermes_send_email` | **Confirm** | Queue for user approval |
| `hermes_invoke_skill` | **Per-skill** | Depends on skill's risk level |

---

## 6. Implementation Phases

| Phase | Tools | Effort |
|-------|-------|--------|
| **M1** | `hermes_list_documents` + `hermes_get_document_content` + `hermes_get_feed_stats` | Medium |
| **M2** | `hermes_list_reminders` + `hermes_update_reminder` | Medium (part of doc 12 R4) |
| **M3** | `hermes_reclassify` + `hermes_reextract` + `hermes_get_processing_queue` | Medium |
| **M4** | `hermes_create_alert` + confirmation queue | Medium (part of doc 10 E4) |

M1 is the platform enabler — Osprey can't work without it. M2 is already in the reminders plan. M3 enables the rich UI. M4 is the agent evolution path.

---

## 7. Open Questions

| # | Question | Leaning |
|---|----------|---------|
| 1 | Should `hermes_list_documents` support full-text search in addition to cursor pagination? | No — use `hermes_search` for search, `hermes_list_documents` for feed. Different access patterns. |
| 2 | Should write tools require an API key or consumer identity? | Not for v1 — localhost only, single user. Add auth when remote access is needed. |
| 3 | Should `hermes_reclassify` also re-run extraction? | Optional parameter `reextract: true`. Default false. |
| 4 | Should we batch write operations (e.g. reclassify 50 documents at once)? | Not yet — single-document operations are clearer. Add batch variants when needed. |
