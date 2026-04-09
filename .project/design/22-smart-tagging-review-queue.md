# Hermes — Smart Tagging & Review Queue

## Status: DESIGN — next wave after v2 polish

## Problem

Documents currently get one category (`invoices`, `tax`, `bank-statements`). But:
- A Costco Visa statement is also: `credit-card`, `monthly`, `FY2024-25`, `tax-deductible-expenses`
- A land tax assessment is also: `property`, `annual`, `QLD-government`, `FY2024-25`
- 6,350 documents sit in `unsorted/` with no classification at all
- Users have no way to correct bad classifications or teach the system

## Goals

| # | Goal | Measure |
|---|------|---------|
| T1 | Multi-tag documents (additive, not exclusive) | A document can have category + N tags |
| T2 | User can tag/untag documents individually or in batch | UI supports both single and group operations |
| T3 | System learns from user corrections | Next similar document gets suggested tags |
| T4 | Review queue surfaces low-confidence + unclassified docs | User reviews grouped batches, not individual docs |
| T5 | Tags queryable via API/MCP | "find all documents tagged tax-deductible for FY2025" |

## Data Model

### New: `tags` table

```sql
CREATE TABLE tags (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    document_id INTEGER NOT NULL REFERENCES documents(id),
    tag         TEXT NOT NULL,
    source      TEXT NOT NULL DEFAULT 'user',  -- 'user', 'rule', 'llm', 'propagated'
    confidence  REAL,
    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
    created_by  TEXT  -- 'user', rule name, or LLM model
);

CREATE INDEX idx_tags_doc ON tags(document_id);
CREATE INDEX idx_tags_tag ON tags(tag);
CREATE UNIQUE INDEX idx_tags_unique ON tags(document_id, tag);
```

### New: `tag_rules` table (learned patterns)

```sql
CREATE TABLE tag_rules (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    tag         TEXT NOT NULL,
    match_type  TEXT NOT NULL,  -- 'sender', 'vendor', 'filename_pattern', 'content_regex'
    match_value TEXT NOT NULL,
    confidence  REAL NOT NULL DEFAULT 0.9,
    source      TEXT NOT NULL DEFAULT 'learned',  -- 'learned', 'manual'
    example_doc INTEGER REFERENCES documents(id),
    created_at  TEXT NOT NULL DEFAULT (datetime('now'))
);
```

When a user tags 3 Costco statements as `credit-card-statement`, the system creates:
```
tag_rules: tag='credit-card-statement', match_type='vendor', match_value='Costco', confidence=0.95
```

Next time a document from Costco arrives, it automatically gets `credit-card-statement` tag.

## Tag Taxonomy

Tags are free-form strings, but the UI suggests from existing tags (typeahead). Common dimensions:

| Dimension | Examples |
|-----------|----------|
| Document type | `invoice`, `statement`, `receipt`, `notice`, `letter`, `contract` |
| Financial year | `FY2024-25`, `FY2023-24` |
| Tax relevance | `tax-deductible`, `income`, `capital-gains`, `not-tax-relevant` |
| Vendor | `costco`, `ato`, `qld-revenue`, `cba` |
| Recurrence | `monthly`, `quarterly`, `annual`, `one-off` |
| Status | `needs-action`, `paid`, `filed`, `archived` |
| Project/purpose | `rental-property`, `home-renovation`, `medical` |

Category (the folder) remains as the primary organizational axis. Tags are secondary metadata. They coexist — a document in `bank-statements/` can be tagged `credit-card`, `monthly`, `FY2024-25`, `tax-deductible`.

## Review Queue

### How it works

1. **Auto-triage**: After extraction, documents with classification confidence < 0.6 or in `unsorted/` enter the review queue
2. **Grouping**: The queue groups documents by similarity:
   - Same sender → group
   - Same extracted vendor → group
   - Similar filenames → group
   - LLM embedding similarity → group
3. **Batch review**: User sees "5 Costco statements" not "5 individual documents"
4. **Actions per group**:
   - ✅ Confirm category + auto-assign tags
   - 🔄 Change category (moves files, updates DB)
   - 🏷️ Add tags to all in group
   - ❌ Dismiss (mark as reviewed, leave as-is)
5. **Learning**: Every confirmation/correction creates tag_rules for future docs

### Priority ordering

The queue is ordered by:
1. Documents with extracted amounts (financial documents are higher priority)
2. Documents with due dates (time-sensitive)
3. Larger groups (reviewing 10 docs at once is more efficient than 10 singletons)
4. Most recent first

## API

```
GET  /api/tags/:docId                    → tags for a document
POST /api/tags/:docId                    → add tag(s): { tags: ["FY2024-25", "tax-deductible"] }
DELETE /api/tags/:docId/:tag             → remove a tag

GET  /api/tags/search?tag=FY2024-25      → documents with this tag
GET  /api/tags/suggest?docId=123         → suggested tags based on similar docs

GET  /api/review-queue                   → grouped documents needing review
POST /api/review-queue/confirm           → confirm a group: { docIds: [...], tags: [...] }
POST /api/review-queue/reclassify        → change category: { docIds: [...], category: "tax" }
POST /api/review-queue/dismiss           → dismiss group: { docIds: [...] }
```

## UI Components

### Review Queue Panel (new default view when items need attention)

```
┌────────────────────────────────────────────────────────────┐
│ 📋 REVIEW QUEUE                                    23 docs │
│                                                            │
│ ┌────────────────────────────────────────────────────────┐ │
│ │ 📧 Costco / Citi Cards (5 documents)                   │ │
│ │ Suggested: bank-statements · credit-card · monthly     │ │
│ │                                                        │ │
│ │ • 2026-03-30_file_January 22.pdf    $564.03            │ │
│ │ • 2026-03-30_file_February 24.pdf   $1,470.52          │ │
│ │ • 2026-03-30_file_March 24.pdf      $1,579.49          │ │
│ │ • 2026-03-30_file_April 22.pdf      $1,022.41          │ │
│ │ • 2026-03-30_file_May 22.pdf        $1,233.71          │ │
│ │                                                        │ │
│ │ Category: [bank-statements ▾]                          │ │
│ │ Tags: [credit-card] [monthly] [FY2024-25] [+ add]     │ │
│ │                                                        │ │
│ │ [✅ Confirm All]  [Skip]                               │ │
│ └────────────────────────────────────────────────────────┘ │
│                                                            │
│ ┌────────────────────────────────────────────────────────┐ │
│ │ 📧 State Revenue Office (4 documents)                  │ │
│ │ Suggested: tax · property · annual                     │ │
│ │                                                        │ │
│ │ • 2022_Land_Tax_Assessment...    $2,847.00             │ │
│ │ • 2023_Land_Tax_Assessment...    $3,156.00             │ │
│ │ • 2024_Land_Tax_Assessment...    $3,421.00             │ │
│ │ • 2025_Land_Tax_Assessment...    $3,689.00             │ │
│ │                                                        │ │
│ │ [✅ Confirm All]  [Skip]                               │ │
│ └────────────────────────────────────────────────────────┘ │
│                                                            │
│ ┌────────────────────────────────────────────────────────┐ │
│ │ 🔍 Ungrouped (14 documents)                            │ │
│ │ [Review individually →]                                │ │
│ └────────────────────────────────────────────────────────┘ │
└────────────────────────────────────────────────────────────┘
```

### Tag Bar (on document detail)

```
Category: bank-statements
Tags: [credit-card ×] [monthly ×] [FY2024-25 ×] [+ add tag...]
```

Clicking `×` removes a tag. `+ add tag` opens a typeahead over existing tags.

### Library Enhancement

The sidebar library gains a tag filter:

```
LIBRARY (7,563)
  unsorted (6,350)
  invoices (528)
  bank-statements (335)
  ...

TAGS
  FY2024-25 (234)
  tax-deductible (89)
  monthly (67)
  credit-card (45)
  ...
```

Clicking a tag filters the document list. Tags and categories compose: "bank-statements tagged FY2024-25" shows the intersection.

## Learning Pipeline

When a user confirms/corrects:

1. **Immediate**: Tags applied to the selected documents
2. **Rule generation**: System creates `tag_rules` entries:
   - If all docs in group share a sender → `match_type='sender'`
   - If all share a vendor → `match_type='vendor'`
   - If filenames match a pattern → `match_type='filename_pattern'`
3. **Propagation**: Run new rules against existing untagged docs → auto-tag with `source='propagated'`
4. **LLM fallback**: For ungrouped docs, ask LLM to suggest tags (same as Tier 3 classification but for tags)

This creates a flywheel: more user confirmations → more rules → fewer docs in the review queue → less user effort.

## Integration with Osprey/Pelican

Tags are the query surface for downstream consumers:

```fsharp
// Osprey: "Give me all tax-relevant documents for FY2024-25"
let! taxDocs = api.getDocumentsByTags ["tax-deductible"; "FY2024-25"]

// Pelican: "Give me all invoices tagged as paid this month"
let! paidInvoices = api.getDocumentsByTags ["invoice"; "paid"; "2026-04"]
```

This is far richer than querying by category alone.

## Implementation Phases

| Phase | What | Depends on |
|-------|------|-----------|
| **R1** | `tags` + `tag_rules` tables, migration | — |
| **R2** | Tag CRUD API endpoints | R1 |
| **R3** | Tag bar on document detail (add/remove tags) | R2 |
| **R4** | Review queue grouping logic (F# module) | R1 |
| **R5** | Review queue API endpoints | R4 |
| **R6** | Review queue UI panel | R5 |
| **R7** | Learning pipeline (rule generation from confirmations) | R4 |
| **R8** | Tag propagation (apply learned rules to existing docs) | R7 |
| **R9** | Sidebar tag filter | R2 |
| **R10** | LLM tag suggestion for ungrouped docs | R7 |
