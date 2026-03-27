# Phase 6: MCP Server

**Status**: Not Started  
**Depends On**: Phase 4 (Full-Text Search), Phase 5 (Embeddings & Semantic Search)  
**Deliverable**: Add Hermes to Claude's MCP config, ask "find my electricity bills from last quarter" and get answers.

---

## Objective

Expose the Hermes index to AI agents via the Model Context Protocol (MCP). The MCP server is integrated into the Hermes service process, serving streamable HTTP on localhost. A stdio shim is provided for MCP clients that only support stdio.

---

## Tasks

### 6.1 — MCP Server Infrastructure
- [ ] Implement MCP server using streamable HTTP transport on `http://localhost:{port}/mcp`
- [ ] Default port: `21740` (configurable in `config.yaml`)
- [ ] Integrate into the `BackgroundService` host (same process as the pipeline tasks)
- [ ] Handle MCP protocol: `initialize`, `tools/list`, `tools/call`
- [ ] Read-only: no MCP tool modifies the archive, database, or configuration
- [ ] Use an existing .NET MCP SDK library if available, or implement the JSON-RPC protocol directly

### 6.2 — Stdio Shim
- [ ] `hermes mcp` command: thin proxy that bridges stdio ↔ `http://localhost:{port}/mcp`
- [ ] Reads JSON-RPC from stdin, forwards to HTTP endpoint, writes response to stdout
- [ ] If the Hermes service isn't running, attempt to start it (or report error)
- [ ] For MCP clients that only support stdio transport (e.g. some Claude Desktop configurations)

### 6.3 — Tool: `hermes_search`
- [ ] Unified search across all document types
- [ ] Input parameters:
  - `query` (required): search text
  - `mode`: `keyword` (default), `semantic`, `hybrid`
  - `category`: filter by category prefix
  - `account`: filter by email account label
  - `sender`: filter by sender email/domain (substring)
  - `from_date`, `to_date`: ISO 8601 date range
  - `source_type`: `email_attachment`, `watched_folder`, `manual_drop`
  - `limit`: max results (default 20)
- [ ] Returns array of results: `document_id`, `saved_path`, `original_name`, `category`, `sender`, `subject`, `email_date`, `extracted_vendor`, `extracted_amount`, `extracted_date`, `relevance_score`, `snippet`

### 6.4 — Tool: `hermes_get_document`
- [ ] Retrieve full metadata and extracted text for a document
- [ ] Input: `document_id` or `path` (relative to archive root)
- [ ] Optional: `include_text` (default true)
- [ ] Returns: all document fields including full `extracted_text`

### 6.5 — Tool: `hermes_get_email`
- [ ] Provenance lookup: get the email a document was attached to
- [ ] Input: `document_id`, or `account` + `gmail_id`
- [ ] Returns: email metadata (sender, subject, date, labels) + list of all documents from that email

### 6.6 — Tool: `hermes_list_categories`
- [ ] List all categories with file counts and total sizes
- [ ] Input: optional `account` filter
- [ ] Returns: array of `{ category, file_count, total_bytes, extracted_count, embedded_count }`

### 6.7 — Tool: `hermes_stats`
- [ ] Overall archive statistics
- [ ] Returns: total documents, total emails, category count, extraction coverage %, embedding coverage %, total disk usage, last sync time per account, Ollama availability

### 6.8 — Tool: `hermes_list_accounts`
- [ ] List configured email accounts with sync status
- [ ] Returns: array of `{ label, email, provider, last_sync_at, message_count, token_status }`

### 6.9 — Tool: `hermes_read_file`
- [ ] Read extracted text content of a document
- [ ] Input: `path` (relative) or `document_id`
- [ ] **Path sandboxing**: reject any path containing `..` or absolute paths. Only serve files within the archive directory.
- [ ] Returns extracted text only — never raw binary content

### 6.10 — MCP Config Documentation
- [ ] Document Claude Desktop MCP config (streamable HTTP):
  ```json
  { "mcpServers": { "hermes": { "url": "http://localhost:21740/mcp" } } }
  ```
- [ ] Document VS Code MCP config
- [ ] Document stdio mode for clients that need it:
  ```json
  { "mcpServers": { "hermes": { "command": "hermes", "args": ["mcp"] } } }
  ```

---

## Security

- [ ] **Read-only**: no tool modifies the archive, moves files, or triggers syncs
- [ ] **Local only**: HTTP bound to `localhost` — no network exposure
- [ ] **No credentials**: MCP tools never return OAuth tokens or credential paths
- [ ] **Path sandboxing**: `hermes_read_file` rejects parent traversal (`../`)
- [ ] **No binary content**: file tools return extracted text only

---

## Acceptance Criteria

- [ ] `hermes_search` with keyword query returns relevant documents with snippets
- [ ] `hermes_search` with `mode: semantic` returns semantically relevant results
- [ ] `hermes_search` with `mode: hybrid` combines both result sets
- [ ] Category, date, sender, and account filters work correctly
- [ ] `hermes_get_document` returns full document details including text
- [ ] `hermes_get_email` returns the source email for a document
- [ ] `hermes_list_categories` and `hermes_stats` return accurate counts
- [ ] `hermes_read_file` with `../` in the path is rejected
- [ ] Claude Desktop can connect via streamable HTTP and execute search queries
- [ ] The stdio shim correctly proxies requests and responses
- [ ] MCP server starts and stops cleanly with the Hermes service
