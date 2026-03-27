# Phase 10: Email Body Indexing

**Status**: In Progress (schema + search changes partially applied)  
**Depends On**: Phase 1 (Email Sync), Phase 4 (Full-Text Search)  
**Deliverable**: All emails indexed — "who did we invite to the birthday party" finds the answer.

---

## Objective

Index the full text of every email (not just attachments). The `hermes_search` MCP tool returns email body matches alongside document matches in a single unified result set.

---

## Tasks

### 10.1 — Schema Changes (DONE — partially applied)
- [x] Add `body_text TEXT` column to `messages` table
- [x] Add `thread_id TEXT` column to `messages` table
- [x] Create `messages_fts` FTS5 virtual table (sender, subject, body_text)
- [x] Create insert/update triggers to keep `messages_fts` in sync
- [x] Bump schema version to 2

### 10.2 — Domain + Algebra Changes (DONE — partially applied)
- [x] Add `BodyText: string option` to `Domain.EmailMessage`
- [x] Add `getMessageBody: string -> Task<string option>` to `Algebra.EmailProvider`

### 10.3 — EmailSync Body Download
- [ ] In `syncAccount`, after recording message metadata, fetch body text via `provider.getMessageBody msg.ProviderId`
- [ ] Strip HTML to plain text (use regex or a simple HTML-to-text function)
- [ ] Store body text in `messages.body_text` column
- [ ] Alternatively: populate `EmailMessage.BodyText` during `listNewMessages` if the provider can return it efficiently (Gmail API can include body in the message.get call with `format=full`)
- [ ] Messages without body text (e.g. empty forwarded messages) → store empty string, not NULL

### 10.4 — Gmail Provider Implementation
- [ ] Update the Gmail provider to fetch message body during `listNewMessages` or via `getMessageBody`
- [ ] Gmail API: `messages.get` with `format=full` returns `payload.parts` containing body
- [ ] Extract `text/plain` part first; fall back to `text/html` → strip tags
- [ ] Simple HTML stripping: remove all tags, decode entities, collapse whitespace
- [ ] Handle multipart MIME messages (walk the parts tree)

### 10.5 — Update Test Mocks
- [x] Add `getMessageBody` field to all mock `EmailProvider` records in tests
- [x] Add `BodyText` field to all sample `EmailMessage` records in tests

### 10.6 — Unified Search (DONE — partially applied)
- [x] Add `ResultType: string` ("document" or "email") to `Search.SearchResult`
- [x] Add `buildEmailQuery` function querying `messages_fts`
- [x] Add `executeEmailSearch` function
- [x] Add `executeUnified` function that merges document + email results by BM25 rank
- [x] Update `McpTools.hermes_search` to call `executeUnified` and include `resultType` in response

### 10.7 — Email Search via MCP
- [ ] `hermes_search` with query "birthday party" returns email body matches with:
  - `resultType: "email"`, `sender`, `subject`, `date`, `snippet`, `relevanceScore`
  - `documentId: 0`, `savedPath: ""`, `category: "email"` (no file on disk)
- [ ] Mixed results: email body matches interleaved with document matches, sorted by relevance
- [ ] Account and date filters work for email results too

### 10.8 — Embedding for Email Bodies (Optional, deferred)
- [ ] Email body text could also be chunked and embedded for semantic search
- [ ] This is optional — FTS5 keyword search covers most email body queries
- [ ] If implemented: chunk body text, embed via Ollama, store in `vec_chunks` with a sentinel `document_id` (e.g. negative row IDs or a new `email_chunks` table)

---

## Acceptance Criteria

- [ ] `hermes sync` downloads and stores body text for all emails (not just those with attachments)
- [ ] `hermes search "birthday party"` finds emails mentioning birthday parties
- [ ] MCP `hermes_search` returns mixed results: documents + emails ranked together
- [ ] Email results include sender, subject, date, snippet from body text
- [ ] Re-syncing doesn't duplicate body text (INSERT OR IGNORE still works)
- [ ] Emails without body text don't cause errors
