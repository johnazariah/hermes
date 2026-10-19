# 30 - Pipeline V5 Architecture

> Supersedes the Pipeline V4 architecture in [`archive/23-pipeline-v4-architecture.md`](../archive/23-pipeline-v4-architecture.md).

## Purpose

Hermes is a local-first document intelligence service. It ingests documents from email and watched folders, stores human-readable content in a portable archive, enriches documents through a declarative pipeline, and exposes the resulting knowledge through HTTP, MCP, web, and desktop surfaces.

## System Shape

```text
Gmail --------\
Outlook -------+--> documents metadata --> Pipeline V5 DAG --> REST/MCP
Folder watch --/             |                    |              |
                             v                    v              v
                    file-first archive      SQLite indexes   React/desktop
```

Producers write archive files and insert document metadata. Pipeline V5 discovers ready work from SQLite; consumers read metadata and file-backed artifacts through the service APIs.

## Pipeline DAG

```text
                         +--> triage --> deep-comprehend (financial gate)
ingest --> extract ------+
                         +--> embed
```

Ingest is performed by producers rather than a registered stage. The standard registered stages are:

| Stage | Dependencies | Output | Execution | Resource |
|-------|--------------|--------|-----------|----------|
| `extract` | none | `extraction` table + `.extracted.md` | channel, concurrency 8 | CPU |
| `triage` | `extract` | `triage` table + initial comprehension artifact | channel, concurrency 1 | configurable LLM |
| `deep-comprehend` | `extract`, `triage` | `comprehension` table + thread JSON | one-minute batches | configurable LLM |
| `embed` | `extract` | `embedding` table + vector index | channel, concurrency 1 | embedding model |

Deep comprehension is gated to financial and other high-value categories. Embedding depends only on extraction, so semantic indexing is not blocked by deep comprehension.

## Declarative Stage Contract

Each `StageDefinition` declares:

- a unique name and dependency list;
- an output table and idempotent schema;
- a processor and optional document gate;
- an optional GPU model;
- channel or batch execution mode; and
- maximum concurrency.

`PipelineV5.buildDag` rejects duplicate stages, unknown dependencies, and cycles. It topologically orders stages and groups them into model phases.

## Scheduling and Recovery

- `stage_completions` is the idempotency ledger.
- A stage is ready when all dependencies are complete and its own completion row is absent.
- `GpuScheduler` serializes model use so one model occupies constrained local GPU memory at a time.
- Failed work is recorded in `dead_letters`; successful or gated work is marked complete.
- The pipeline polls for new ready work, so restart recovery comes from durable metadata rather than an in-memory queue.

## Storage Boundary

Files are the source of truth for human- and LLM-readable content:

```text
archive/
  account/sender-domain/subject--thread/
    YYYY-MM-DD-message-<id>.md
    YYYY-MM-DD-attachment-<hash>.pdf
    YYYY-MM-DD-attachment-<hash>.pdf.extracted.md
    thread.comprehension.json
    .hermes.json
  local/YYYY-MM-DD.filename/
    ...
```

SQLite stores document identity, workflow state, per-stage metadata, learned patterns, suggestions, contacts, FTS5 data, and vector indexes. Content columns removed by the file-first migration are not pipeline inputs.

The rebuildable file-content FTS projection is populated from immutable body
files and generation-fenced extraction artifacts. Search reads only metadata
and current index rows; keyword reads do not invoke providers or mutate the archive.
Metadata/content matches share stable document identity, with ranking and
deduplication before pagination.

See [28-file-first-archive.md](28-file-first-archive.md) for archive details.

## Compatibility Boundary

Pipeline V5 currently reuses the established `Stages` processors. `StagesV5` hydrates a compatibility `Document` property bag from metadata, invokes those processors, writes the V5 output table, and updates selected legacy `documents` columns used by existing APIs and UI.

This boundary allows the DAG and per-stage schema to operate without a simultaneous rewrite of every consumer. New code must read content from archive artifacts rather than reintroducing content columns.

`DocumentManagement` exposes metadata-only reclassification through a detailed
outcome API and a compatibility adapter. Category, provenance, generated category
tags and FTS projections change atomically; source bytes, `saved_path`, hashes and
V5 completions remain unchanged. Canonical-path guards apply to every database
writer, while source claims protect files before document registration.

Email acknowledgement and body/attachment registration share one write
transaction. Provider calls precede that transaction and callbacks follow its
commit. Earlier email-body documents, source files, V5 outputs and user
annotations remain immutable history when another message arrives; new messages
must not delete or repurpose previous document identities. A failed new ingestion
remains unprocessed and retryable without advancing its watermark. Existing
duplicate owners are not reconciled by guessing or deleting rows.

Successful empty provider responses are distinct from transport failures.
Provider and pagination failures must remain observable through ingestion so an
incomplete enumeration cannot be mistaken for a completed sync.

Public HTTP/MCP replay requests use an audited reflow operation rather than
clearing legacy projections alone. Reextract invalidates extract, triage,
deep-comprehend and embed; recomprehend invalidates triage and deep-comprehend;
reembed invalidates embed only. Each requested closure is checked against the
declared DAG and fails closed if topology changes.

Reflow operations, generations and attempt leases persist independently of
in-memory scheduling. Publication fences and folder revisions reject
superseded work, including shared thread artifacts. Responses expose operation
identity and observed lifecycle outcomes rather than treating acceptance as
successful processing. Connection-scoped transactions share one execution gate;
nested result callbacks roll back their savepoint on errors, exceptions and
cancellation before propagation.

User corrections remain effective through extraction invalidation and replay.
The latest correction for each date, amount or vendor field overrides generated
metadata without freezing unrelated extraction fields. Source identity,
correction history and user annotations are preserved. Legacy saved-path
recovery remains separate from ordinary reclassification.

Changing a document's source path, hash or source type advances its existing
generation and invalidates current completion/index markers in the same SQL
transaction. Historical publications, user corrections, source claims and
source bytes remain intact; stale stage work cannot certify the old source
under the new identity. Metadata-only category changes do not trigger this
source-identity transition.

## Comprehension

Triage runs for every extracted document and produces a fast type/category/confidence result. Deep comprehension runs only when the triage category passes its gate, using the full thread and type-specific prompt registry. Both stages can write `thread.comprehension.json`; the deep result supersedes the triage artifact.

Read-only structured retrieval uses an attributable document-scoped canonical
publication, not an arbitrary shared-folder JSON file. It returns the complete
persisted JSON, source identity and publication digest alongside effective
metadata, including user corrections. Unattributed shared enrichment is
explicitly unavailable. This accessor has no generative-provider capability
and does not repair missing state.

Stage currentness and publication origin are distinct from the global document
generation used to fence a read. Reembedding preserves current comprehension
and its original provenance; invalidating comprehension or changing source
identity makes those fields unavailable until a valid publication exists.
Concurrent source/generation changes cannot turn a historical or sibling
publication into a successful current read.

An accepted embedding-only reflow atomically records an additive
`comprehension_provenance` origin link with its generation and operation audit.
It neither copies nor relabels canonical publications. Responses distinguish
`generation` (the current document read fence) from `publicationGeneration`
(the original canonical stage generation). Missing links across historical
generation gaps are not inferred or repaired during a read.

See [24-comprehension-stage.md](24-comprehension-stage.md).

## Interfaces

- **REST:** document browsing, file/content retrieval, pipeline status, corrections, recomprehension, tags, suggestions, preferences, sync, chat, and activity.
- **MCP:** streamable HTTP at `/mcp` for downstream consumers such as Osprey.
- **Web:** React 19/Vite application with Home, Documents, Search, Settings,
  onboarding, Pipeline and Chat routes. Named chat consumers share one SSE
  decoder for `results`, optional `answer` and `done`. Document-backed and
  metadata-only email identities remain distinct.
- **Desktop:** Windows Tray hosts the canonical React app. MAUI Windows/macOS
  remain excluded experimental builds.

### Canonical UI delivery

`Hermes.Web/dist` is the only Service UI build input. A deterministic manifest
identifies every public asset, module entrypoints and byte digests. Build and
publish replace their owned output `wwwroot`; checked legacy bundles and
Blazor assets are excluded. A missing or malformed deployed bundle fails
startup before archive/provider/worker initialization.

The local Host/origin boundary precedes UI delivery. Only exact canonical
browser routes and manifest-listed files are served. API/MCP/health misses,
undeclared assets and `_framework` requests are never converted into SPA HTML.
The deployed bundle is independent of working directory and source checkout.

### Owned lifecycle and manual sync

Normal foreground mode uses host shutdown. Explicit `--control-stdin` enables
a redirected owner pipe: an exact `shutdown` line or EOF requests application
stop. The reader is cancellable and joined with the pipeline and producer
before their databases are disposed. No HTTP shutdown endpoint is added.
Health reports actual process identity; native ownership must not be inferred
from an arbitrary successful HTTP response.

Manual sync uses an injected capability bound to the existing single producer.
Its bounded inbox coalesces pending requests and serializes cycles with
periodic work. HTTP 202 reports queued/coalesced admission; HTTP 503 reports
unsupported, starting or stopping ownership. Completion/failure remains an
observed processing outcome, not a property of request acceptance.

### Typed search

HTTP and `hermes_search` use one keyword/semantic/hybrid request contract.
Keyword remains the default. Semantic retrieval uses the configured query
embedding capability and current `document_chunks` evidence; hybrid combines
keyword and vector rankings using reciprocal-rank fusion. Evidence is loaded
after query encoding so an intervening invalidation cannot leave a stale
pre-encoding snapshot eligible.

Results identify the requested mode and vector coverage. Missing or failed
query encoding and absent current vectors have explicit unavailable/error
outcomes. Partial indexes expose partial coverage; they do not silently turn a
semantic or hybrid request into keyword search. Persisted comprehension reads
require neither query encoding nor an LLM.

Keyword limits accept positive 32-bit integers, retaining established MCP
callers; semantic/hybrid limits are 1–100. Omitted limits retain HTTP 50 and
MCP 20 defaults. Explicit malformed, nonpositive or overflowing limits are
rejected, as are repeated HTTP limit parameters.

### Local MCP capability boundary

Every tool declares `Read` or `Mutate`. Default discovery and dispatch expose
only read capabilities. `HERMES_MCP_MUTATIONS=true` opts into mutations and
requires a configured `HERMES_MCP_TOKEN` of 32–4,096 non-whitespace characters.
Each mutation requires its bearer token; token comparison uses fixed-length
hashes and constant-time comparison. An absent Origin header is not
authentication. Unknown or removed tool names remain unknown independently of
the policy for recognised tools.

The Service validates loopback remote addresses, the requested Host/listener
port and browser origins before routing. Allowed origins are canonical
loopback HTTP(S) origins for production 21741, development 21742 and the
configured Service port. `HERMES_ALLOWED_ORIGINS` supplies additional explicit
semicolon-separated loopback preview origins; wildcard CORS is not allowed.
Invalid policy configuration fails startup. Local read-only requests without
an Origin remain usable.

This capability policy authenticates MCP mutations. Broader REST/browser/Tray
session authentication requires its own UI/session integration.

## Design Rules

1. Stages declare dependencies; no central hard-coded stage sequence.
2. Files hold content; SQLite holds metadata and rebuildable indexes.
3. Stage writes are idempotent and recovery is database-driven.
4. GPU scheduling is explicit and model-aware.
5. Tagless-Final capability records isolate providers and side effects.
6. Compatibility writes are transitional boundaries, not permission to restore removed content columns.
