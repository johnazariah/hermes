---
description: "Implementation plan for Doc 13: Document Feed & Consumer Model. Four phases (F1–F4), each a silver-thread vertical slice."
design-doc: ".project/design/13-document-feed-and-consumers.md"
depends-on:
  - "Doc 17 Phase P8 (MCP integration) — for hermes_get_document_content"
---

# Hermes — Implementation Plan: Document Feed & Consumers

## Prerequisites

```
dotnet build hermes.slnx --nologo
dotnet test tests/Hermes.Tests/Hermes.Tests.fsproj --nologo
```

**Expected**: 0 errors, 0 warnings, all tests pass.

**Critical rules**:
- **F# code** must go through `@fsharp-dev` agent
- **Every phase has a PROOF** — do not mark complete until the proof passes

## Dependency Map

```
F1: hermes_list_documents MCP tool       (uses existing documents.id as feed position)
 └─ F2: hermes_get_document_content       (needs existing extraction + Doc 17 for structured markdown)
F3: Consumer protocol documentation       (needs F1 + F2 defined — documentation only)
F4: Osprey first consumer                 (Osprey-side — see Doc 16 plan instead)
```

F1 and F2 are independent. F3 is documentation after F1+F2 stabilise. F4 is covered by Doc 16 plan.

---

## Phase F1: hermes_list_documents MCP Tool

**Silver thread**: MCP client calls `hermes_list_documents(since_id=0, limit=100)` → backend queries documents table with `WHERE id > @since_id ORDER BY id ASC LIMIT @limit` → returns array of document records → client saves `max(id)` as cursor → next poll returns only new documents.

### What to build

**File: `src/Hermes.Core/DocumentFeed.fs`** (new module)

```fsharp
[<RequireQualifiedAccess>]
module DocumentFeed

type FeedDocument = {
    Id: int64; OriginalName: string; Category: string; FilePath: string;
    Sender: string option; Subject: string option; Account: string option;
    ExtractedDate: string option; ExtractedAmount: decimal option;
    ExtractedVendor: string option;
    IngestedAt: string; ClassifiedAt: string option;
    ExtractedAt: string option; EmbeddedAt: string option;
    ClassificationTier: string option; ClassificationConfidence: float option
}

type FeedState = | Any | Ingested | Classified | Extracted | Embedded

/// Query documents by cursor position with optional state and category filters
let listDocuments (db: Database) (sinceId: int64) (state: FeedState) (category: string option) (limit: int) : FeedDocument list

/// Query feed statistics: total, max id, counts by state and category
let getFeedStats (db: Database) : FeedStats

type FeedStats = {
    TotalDocuments: int; MaxDocumentId: int64;
    ByState: Map<string, int>;    // ingested, classified, extracted, embedded
    ByCategory: Map<string, int>; // invoices: 239, payslips: 15, ...
    OldestDocument: string option; NewestDocument: string option
}
```

**File: `src/Hermes.Core/McpTools.fs`** (extend)

- Add `hermes_list_documents` tool registration with schema:
  - `since_id`: integer (default 0)
  - `state`: enum [ingested, classified, extracted, embedded, any] (default "any")
  - `category`: string (optional)
  - `limit`: integer (default 100)
- Add `hermes_get_feed_stats` tool registration (no parameters)

**File: `src/Hermes.Core/McpServer.fs`** (extend)

- Wire tool handlers to `DocumentFeed.listDocuments` and `DocumentFeed.getFeedStats`
- Serialize results as JSON arrays

### What to test

**F# tests:**
- `DocumentFeed_ListDocuments_SinceId0_ReturnsAll` — insert 5 docs → query since_id=0 → returns all 5
- `DocumentFeed_ListDocuments_SinceId3_ReturnsOnlyNewer` — query since_id=3 → returns docs 4, 5
- `DocumentFeed_ListDocuments_FilterByState_OnlyExtracted` — 3 extracted, 2 not → returns 3
- `DocumentFeed_ListDocuments_FilterByCategory_ReturnsMatchingOnly`
- `DocumentFeed_ListDocuments_Limit_RespectsLimit`
- `DocumentFeed_GetFeedStats_ReturnsCorrectCounts`
- `McpTools_ListDocuments_ReturnsJsonArray` — end-to-end tool call test

### PROOF

Start Hermes MCP server → call `hermes_list_documents(since_id=0, limit=10)` → returns first 10 documents with all metadata fields → note `max(id)` = 10 → call again with `since_id=10` → returns next batch → call `hermes_get_feed_stats` → shows total count, max id, state breakdown, category breakdown.

**Consumer simulation**: Write a test script that polls with cursor:
```
cursor = 0
loop:
  docs = hermes_list_documents(since_id=cursor, state="extracted", limit=50)
  if empty: break
  process(docs)
  cursor = max(doc.id for doc in docs)
# Reaches end of archive
```

### Commit

```
feat(mcp): hermes_list_documents and hermes_get_feed_stats cursor-based feed tools
```

---

## Phase F2: hermes_get_document_content MCP Tool

**Silver thread**: MCP client calls `hermes_get_document_content(document_id, format="markdown")` → backend fetches document → returns structured markdown with frontmatter, tables, headings → consumer parses text, no PDF library needed.

### What to build

**File: `src/Hermes.Core/DocumentFeed.fs`** (extend)

```fsharp
type ContentFormat = Text | Markdown | Raw

/// Get document content in specified format
/// Text: extracted_text stripped of frontmatter
/// Markdown: full extracted_text with frontmatter (structured markdown from Doc 17)
/// Raw: original file content (text-based files only: .csv, .txt, .md)
let getDocumentContent (db: Database) (fs: FileSystem) (documentId: int64) (format: ContentFormat) : Result<string, string>
```

**File: `src/Hermes.Core/McpTools.fs`** (extend)

- Add `hermes_get_document_content` tool registration:
  - `document_id`: integer (required)
  - `format`: enum [text, markdown, raw] (default "markdown")

**File: `src/Hermes.Core/McpServer.fs`** (wire handler)

### What to test

- `DocumentFeed_GetContent_Markdown_ReturnsFullExtractedText`
- `DocumentFeed_GetContent_Text_ReturnsFrontmatterStripped`
- `DocumentFeed_GetContent_Raw_CsvFile_ReturnsOriginalContent`
- `DocumentFeed_GetContent_Raw_PdfFile_ReturnsError` (can't return raw PDF as text)
- `DocumentFeed_GetContent_InvalidId_ReturnsError`
- `McpTools_GetDocumentContent_ReturnsContent` — end-to-end tool call test

### PROOF

Start Hermes MCP server → pick a PDF document with known extracted content → call `hermes_get_document_content(id, format="markdown")` → response contains structured markdown with `---` frontmatter, `##` headings, `| table |` rows → call with `format="text"` → same content without frontmatter → for a CSV document: call with `format="raw"` → returns original CSV content.

### Commit

```
feat(mcp): hermes_get_document_content with text, markdown, and raw formats
```

---

## Phase F3: Consumer Protocol Documentation

**Silver thread**: Documentation enables external teams to build consumers. Not code — but the silver thread is: developer reads docs → implements consumer → successfully polls Hermes → processes documents.

### What to build

**File: `.project/design/13-consumer-protocol.md`** (new)

Document the following:

1. **Connection**: MCP endpoint URL, transport (HTTP on localhost:21740/mcp)
2. **Poll loop**: `hermes_list_documents(since_id=cursor, state="extracted", limit=100)`
3. **Cursor management**: Store `max(id)` in consumer's own DB, resume on restart
4. **Content retrieval**: `hermes_get_document_content(id, format="markdown")` for PDFs, `format="raw"` for CSVs
5. **Idempotency requirement**: Consumer must handle duplicate processing (upsert, not insert)
6. **Replay**: Reset cursor to 0 to reprocess everything
7. **State filtering**: Use `state="extracted"` to only get documents with content
8. **Category filtering**: Use `category` param for domain-specific consumers
9. **Error handling**: Skip documents that can't be parsed, advance cursor
10. **Example implementations**: Python (Osprey), F# template

### What to test

- Review by using the docs to build a trivial consumer script
- Consumer script successfully polls and processes 10 documents

### PROOF

A developer unfamiliar with Hermes reads the protocol doc → writes a Python script in < 30 minutes that polls the feed, prints document summaries, and saves a cursor to a JSON file.

### Commit

```
docs: consumer protocol documentation for Hermes document feed
```

---

## Phase F4: Osprey First Consumer

**Covered in Doc 16 (Osprey Integration) plan, phases I3–I5.** This phase is listed here for completeness but implementation is in the Osprey plan.

---

## Silver Thread Integrity Check

| Phase | Input Trigger | Processing | Backend | Presentation | Consumer Response |
|-------|--------------|------------|---------|-------------|-------------------|
| F1 | MCP tool call | SQL query with cursor | DocumentFeed.fs | JSON array of documents | Consumer saves max(id) as cursor |
| F2 | MCP tool call | Fetch content + format | DocumentFeed.fs + FileSystem | Text/markdown/raw string | Consumer parses markdown or CSV |
| F3 | Developer reads docs | — | — | Documentation | Developer builds working consumer |
| F4 | Osprey polls | Tax parsers on content | Osprey code | Tax events posted to Pelican | Dashboard shows tax data |

**No orphaned tools**: F1 + F2 together form a complete, usable feed API. F1 alone is insufficient (can list but not read). F2 alone is insufficient (need to discover documents first).

**Silver thread verification**: The complete thread for a consumer is:
1. `hermes_get_feed_stats` → learn total count, plan catch-up
2. `hermes_list_documents(since_id=0, state="extracted")` → get batch of documents
3. `hermes_get_document_content(id, format="markdown")` → get content for each relevant doc
4. Process content → extract domain data
5. Save cursor → resume next time

Every step produces usable output. No step depends on future work (assuming Doc 17 P7 for structured markdown is done, or falls back to existing flat text extraction).

---

## Flags & Risks

| Risk | Impact | Mitigation |
|------|--------|------------|
| Structured markdown not ready (Doc 17 P7 pending) | `format="markdown"` returns flat text instead of structured | F1+F2 still work — consumers get flat text. Feature degrades gracefully. Upgrade when P7 ships. |
| Large result sets | Consumer requests limit=10000 → slow query | Default limit=100, max 1000. Return count in response for pagination awareness. |
| MCP transport overhead | JSON-RPC adds latency per document | Consumers fetch content only for relevant docs (filter by category first) |
| No auth | Any local process can poll the feed | Acceptable for v1 (localhost only). Add API key auth for remote access later. |
