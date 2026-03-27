# Hermes — MCP Server Design

## Overview

Hermes exposes a **read-only** MCP server that lets AI agents (Claude, Copilot, etc.) search, filter, and retrieve documents from the archive. The MCP server is **integrated into the Hermes service process** — it runs as a streamable HTTP endpoint on `localhost:{port}`. A thin stdio shim (`hermes mcp`) is provided for MCP clients that only support stdio transport.

The MCP server is the primary way AI interacts with Hermes. It is **the** reason Hermes exists.

### Design Goal: "Find Me Everything About X"

The MCP server presents a **unified search surface**. When an AI agent asks "find me everything related to the Manorwoods renovation", `hermes_search` returns results across all content types — email attachments, locally-watched documents, manual drops — ranked by relevance. Today that's documents only; as email body indexing and other content types are added, the same tool returns results from all sources without the agent needing to change its queries.

---

## Transport

### Primary: Streamable HTTP on localhost

The Hermes service hosts the MCP server on `http://localhost:{port}/mcp`. AI tools configure it as a remote MCP server:

```json
{
  "mcpServers": {
    "hermes": {
      "url": "http://localhost:21740/mcp"
    }
  }
}
```

Port `21740` is the default (configurable). The service must be running.

### Secondary: stdio shim

For MCP clients that only support stdio transport, `hermes mcp` launches a thin proxy:

```json
{
  "mcpServers": {
    "hermes": {
      "command": "hermes",
      "args": ["mcp"]
    }
  }
}
```

The shim connects to the running service's HTTP endpoint and bridges stdin/stdout ↔ HTTP. If the service isn't running, it starts it.

---

## Tools

### `hermes_search`

Search emails and documents by keyword, metadata filters, or semantic similarity.

```json
{
  "name": "hermes_search",
  "description": "Search the email and document archive. Supports keyword search (default), semantic search, or both. Returns matching documents with metadata.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "query": {
        "type": "string",
        "description": "Search query. For keyword mode: FTS5 query syntax. For semantic mode: natural language description."
      },
      "mode": {
        "type": "string",
        "enum": ["keyword", "semantic", "hybrid"],
        "default": "keyword",
        "description": "Search mode. 'keyword' uses FTS5, 'semantic' uses vector similarity, 'hybrid' combines both."
      },
      "category": {
        "type": "string",
        "description": "Filter by category folder (e.g. 'invoices', 'property/manorwoods'). Supports prefix matching."
      },
      "account": {
        "type": "string",
        "description": "Filter by email account label."
      },
      "sender": {
        "type": "string",
        "description": "Filter by sender email address or domain (substring match)."
      },
      "from_date": {
        "type": "string",
        "description": "Filter documents dated on or after this date (ISO 8601)."
      },
      "to_date": {
        "type": "string",
        "description": "Filter documents dated on or before this date (ISO 8601)."
      },
      "source_type": {
        "type": "string",
        "enum": ["email_attachment", "watched_folder", "manual_drop"],
        "description": "Filter by how the document entered the archive."
      },
      "limit": {
        "type": "integer",
        "default": 20,
        "description": "Maximum number of results to return."
      }
    },
    "required": ["query"]
  }
}
```

**Returns**: Array of results, each containing:
- `document_id`, `saved_path`, `original_name`
- `category`, `sender`, `subject`, `email_date`
- `extracted_vendor`, `extracted_amount`, `extracted_date`
- `relevance_score` (FTS rank or vector distance)
- `snippet` (highlighted text excerpt for keyword matches)

---

### `hermes_get_document`

Retrieve full details and extracted text for a specific document.

```json
{
  "name": "hermes_get_document",
  "description": "Get full metadata and extracted text for a document by ID or path.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "document_id": {
        "type": "integer",
        "description": "Document ID from a search result."
      },
      "path": {
        "type": "string",
        "description": "Relative path within the archive (alternative to document_id)."
      },
      "include_text": {
        "type": "boolean",
        "default": true,
        "description": "Include the full extracted text in the response."
      }
    }
  }
}
```

**Returns**: Full document record including all extracted fields and text.

---

### `hermes_get_email`

Retrieve the email that a document was attached to (provenance lookup).

```json
{
  "name": "hermes_get_email",
  "description": "Get the email message associated with a document. Returns sender, subject, date, labels, and list of all documents from that email. Useful for understanding the context in which a document was received.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "document_id": {
        "type": "integer",
        "description": "Document ID — returns the email this document was attached to."
      },
      "account": {
        "type": "string",
        "description": "Account label (required with gmail_id)."
      },
      "gmail_id": {
        "type": "string",
        "description": "Gmail message ID (alternative to document_id)."
      }
    }
  }
}
```

**Note**: Email body search (`hermes_search_emails`) is deferred to a future version. When added, it will feed into the unified `hermes_search` tool rather than being a separate tool — supporting the "find me everything about X" vision.

---

### `hermes_list_categories`

List all document categories with counts.

```json
{
  "name": "hermes_list_categories",
  "description": "List all document categories in the archive with file counts and total sizes.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "account": {
        "type": "string",
        "description": "Filter by account label, or omit for all accounts."
      }
    }
  }
}
```

---

### `hermes_stats`

Get archive statistics.

```json
{
  "name": "hermes_stats",
  "description": "Get overall archive statistics: total documents, emails, categories, extraction coverage, embedding coverage, disk usage, last sync times.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "account": {
        "type": "string",
        "description": "Filter stats to a specific account."
      }
    }
  }
}
```

---

### `hermes_list_accounts`

List configured email accounts and their sync status.

```json
{
  "name": "hermes_list_accounts",
  "description": "List all configured email accounts with sync status, last sync time, and message counts.",
  "inputSchema": {
    "type": "object",
    "properties": {}
  }
}
```

---

### `hermes_read_file`

Read the raw content of a file from the archive (for when the AI needs to see the actual PDF text or image).

```json
{
  "name": "hermes_read_file",
  "description": "Read the extracted text content of a document from the archive. For PDFs, returns the extracted text. Does not return binary file content.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "path": {
        "type": "string",
        "description": "Relative path within the archive."
      },
      "document_id": {
        "type": "integer",
        "description": "Document ID (alternative to path)."
      }
    }
  }
}
```

---

## Resources (Optional, Future)

MCP resources could expose the archive as browseable content:

| Resource URI | Description |
|-------------|-------------|
| `hermes://categories` | List of categories |
| `hermes://categories/{name}` | Documents in a category |
| `hermes://documents/{id}` | Single document details |
| `hermes://accounts` | Account list |

Resources are lower priority than tools — tools cover the primary use cases.

---

## Security Considerations

- **Read-only**: The MCP server never modifies the archive, moves files, or triggers syncs.
- **Local only**: stdio transport — no network exposure.
- **No credentials exposed**: MCP tools never return OAuth tokens or credential paths.
- **Path sandboxing**: `hermes_read_file` only reads from within the archive directory. Parent directory traversal (`../`) is rejected.
- **No binary content**: File reading returns extracted text only, never raw binary data.
