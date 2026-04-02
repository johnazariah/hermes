# Consumer Protocol: Hermes Document Feed

## Overview

Hermes exposes a cursor-based document feed via MCP (Model Context Protocol) tools. External consumers poll for new documents, retrieve content, and track their position using a simple integer cursor.

## Connection

- **Transport**: Streamable HTTP on `localhost:21740/mcp`
- **Protocol**: JSON-RPC 2.0 (MCP standard)
- **Stdio shim**: Available for CLI-based consumers via `hermes mcp-stdio`

## Poll Loop

```
cursor = load_cursor() or 0

while True:
    docs = hermes_list_documents(since_id=cursor, state="extracted", limit=100)
    if not docs:
        sleep(60)
        continue

    for doc in docs:
        content = hermes_get_document_content(document_id=doc.id, format="markdown")
        process(doc, content)
        cursor = doc.id

    save_cursor(cursor)
```

## MCP Tools

### hermes_list_documents

Cursor-based pagination. Returns documents with `id > since_id`.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `since_id` | integer | 0 | Return docs with id > this value |
| `category` | string | — | Filter by category |
| `limit` | integer | 100 | Max results per call |

### hermes_get_document_content

Retrieve document content in multiple formats.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `document_id` | integer | required | Document ID |
| `format` | string | "markdown" | `text`, `markdown`, or `raw` |

- **markdown**: Structured content with headings, tables, KV pairs (from PdfStructure pipeline)
- **text**: Same content with YAML frontmatter stripped
- **raw**: Original file content (text-based files only: CSV, TXT)

### hermes_get_feed_stats

No parameters. Returns total count, max ID, and category breakdown.

## Cursor Management

- Store `max(doc.id)` from each batch as your cursor
- Persist cursor in your own database/file — survives restarts
- Cursor is monotonically increasing — documents are never reordered
- Set cursor to 0 to replay the entire archive

## Idempotency

Consumers **must** handle duplicate processing gracefully:
- Use document `sha256` or `id` as a dedup key
- Prefer upsert over insert in your processing pipeline
- Network failures may cause the same batch to be delivered twice

## Category Filtering

Use `category` parameter for domain-specific consumers:
- `hermes_list_documents(since_id=0, category="payslips")` → only payslip documents
- Combine with `state` to get only fully processed documents

## Error Handling

- If `hermes_get_document_content` returns an error, skip the document and advance cursor
- Log errors but don't stop polling — transient failures are expected
- Retry with exponential backoff on connection failures

## Rate Limiting

- No server-side rate limit on feed tools
- Recommended: poll every 30–60 seconds for new documents
- For bulk replay: use `limit=100` with no delay between batches
