# 25 — Address Book

## Summary

Hermes automatically builds an address book from document comprehension.
Every email sender and document issuer becomes a **contact** — enriched
over time as more documents arrive. The address book is shared
infrastructure for Osprey (tax: who issued this?) and Pelican (ledger:
supplier or customer?).

## Motivation

| Consumer | Needs                                                        |
| -------- | ------------------------------------------------------------ |
| Osprey   | Agent ABN for rental deductions, employer for PAYG summaries |
| Pelican  | Supplier/customer cards to post invoices and payments against |
| Hermes   | "Show me everything from Ray White" — browse by contact      |

Pass 1 comprehension already extracts sender name, ABN, and often
address/phone. We just need to harvest, deduplicate, and expose it.

## Schema

```sql
CREATE TABLE contacts (
    id              TEXT PRIMARY KEY,   -- deterministic: sha256(canonical_key)[:16]
    name            TEXT NOT NULL,      -- display name (e.g. "Ray White Southbank")
    canonical_name  TEXT NOT NULL,      -- lowercase normalised for dedup
    email           TEXT,               -- primary email address
    abn             TEXT,               -- Australian Business Number (11 digits)
    phone           TEXT,
    address         TEXT,
    contact_type    TEXT NOT NULL,      -- supplier | customer | employer | government | utility | insurance | unknown
    source_sender   TEXT,               -- original email sender string
    first_seen_at   TEXT NOT NULL,
    last_seen_at    TEXT NOT NULL,
    document_count  INTEGER DEFAULT 1,
    metadata        TEXT                -- JSON blob for extensible fields
);

CREATE UNIQUE INDEX idx_contacts_abn ON contacts(abn) WHERE abn IS NOT NULL;
CREATE INDEX idx_contacts_type ON contacts(contact_type);
CREATE INDEX idx_contacts_name ON contacts(canonical_name);

CREATE TABLE document_contacts (
    document_id TEXT NOT NULL REFERENCES documents(id),
    contact_id  TEXT NOT NULL REFERENCES contacts(id),
    role        TEXT NOT NULL,          -- issuer | sender | recipient | payee | employer
    confidence  REAL DEFAULT 1.0,
    PRIMARY KEY (document_id, contact_id, role)
);
```

### Identity resolution

A contact is the same entity if any of these match:
1. **ABN** — strongest signal (unique per business)
2. **Email domain** — same org (commbank.com.au = CommBank)
3. **Canonical name** — normalised lowercase, stripped of Pty Ltd / Inc etc.

Priority: ABN > email domain > name. On conflict, prefer the record with
the most documents (higher confidence).

### Contact type mapping

Derived from `SenderClassification.SenderType` + document type:

| SenderType      | Default contact_type | Override by document_type           |
| --------------- | -------------------- | ----------------------------------- |
| Bank            | supplier             | —                                   |
| PropertyManager | supplier             | —                                   |
| Employer        | employer             | —                                   |
| Government      | government           | —                                   |
| Utility         | supplier             | —                                   |
| Insurance       | supplier             | —                                   |
| Unknown         | unknown              | invoice → supplier, payment → customer |

## Extraction Logic

### When: post-comprehension hook

After Pass 1 writes `comprehension` JSON, a lightweight function scrapes
contact fields — no extra LLM call.

```
comprehension JSON
  → extract: sender_name, abn, sender email, address, phone
  → normalise name (strip "Pty Ltd", lowercase)
  → resolve identity (ABN > domain > name)
  → upsert contact + link document_contacts
```

### Implementation option A — pipeline stage

Add a `ContactExtract` stage after `Comprehend` in the pipeline channel.
Pro: every document gets processed. Con: adds pipeline complexity.

### Implementation option B — post-processing in Comprehend stage

At the end of `Stages.comprehendDocument`, call `ContactExtraction.harvest`
inline. Pro: simpler, no new stage. Con: couples contact logic to comprehension.

**Recommendation: Option B** — it's a lightweight scrape, not a separate
processing step. If it grows complex, promote to a stage later.

## MCP Tools

```
hermes_contacts         — list/search contacts (by name, type, ABN)
hermes_contact_detail   — get contact with all linked documents
hermes_contact_merge    — manually merge two contacts (dedup fix)
```

## Web UI

- **Contacts page** — searchable list, grouped by type
- **Contact detail** — name, ABN, email, all linked documents
- **Document detail** — shows linked contacts with role badges

## Pelican Integration (future)

```
Contact (contact_type = supplier)
  → Pelican: POST /api/suppliers { name, abn, email }
  → Returns supplier_id
  → Store as contacts.metadata.pelican_supplier_id

Contact (contact_type = customer)
  → Pelican: POST /api/customers { name, abn, email }
  → Returns customer_id
```

Sync is one-way push from Hermes → Pelican. Pelican owns the ledger;
Hermes owns the document intelligence.

## Osprey Integration (future)

Osprey queries contacts via MCP:
```
hermes_contact_detail { abn: "12345678901" }
  → { name: "Ray White Southbank", contact_type: "supplier",
      documents: [ { id: "...", type: "agent-statement", ... } ] }
```

This gives Osprey the agent ABN for rental deductions and the employer
details for PAYG summaries without re-parsing documents.

## Tax Relevance & Learning

Contacts gain a `tax_relevant` flag (nullable boolean — null = unknown,
true = tax-relevant, false = not). User sets this once per contact;
it applies to all future documents from that sender.

```sql
ALTER TABLE contacts ADD COLUMN tax_relevant INTEGER; -- null/0/1
```

### Automatic behaviour based on tax relevance

| tax_relevant | Effect                                                    |
| ------------ | --------------------------------------------------------- |
| true         | Auto-trigger Pass 2 deep extraction; queue for Osprey     |
| false        | Standard Pass 1 only; skip deep extraction                |
| null         | Default — behave as today, user hasn't decided yet        |

### Training loop

User marks contacts as tax-relevant via UI or MCP tool. This creates a
feedback signal:

1. **Immediate** — new docs from tax-relevant contacts auto-queue for
   deep extraction. No manual MCP call needed.
2. **Sender hints** — Pass 1 prompt gets "This sender is marked as
   tax-relevant" alongside the sender classification.
3. **Pattern learning** (future) — aggregate tax-relevant contacts to
   discover new patterns. E.g., if user marks 3 property managers as
   tax-relevant, auto-suggest for new property managers.

### MCP tool

```
hermes_contact_set_tax_relevant { contact_id, tax_relevant: true|false }
```

### Osprey integration

Osprey queries only tax-relevant contacts and their documents:
```
hermes_contacts { tax_relevant: true }
  → all contacts that matter for tax
  → each with deep-extracted documents ready for processing
```

This eliminates Osprey's need to scan all documents — it gets a
pre-filtered, pre-enriched feed of tax-relevant material.

## Open Questions

1. **Should contacts be editable?** User might want to correct a name or
   merge duplicates. Start read-only, add edit via MCP tool later.
2. **Multi-entity contacts?** "Commonwealth Bank" has credit cards, home
   loans, and savings — same ABN, different products. One contact with
   document_type breakdown, or separate contacts? Recommend: one contact,
   filter by document type.
3. **Privacy:** Contact data stays local (SQLite). No cloud sync unless
   explicitly pushed to Pelican.
