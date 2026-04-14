# Phase 5: Embeddings & Semantic Search

**Status**: Not Started  
**Depends On**: Phase 3 (Text Extraction)  
**Deliverable**: `hermes search --semantic "plumbing invoice for the rental"` finds relevant documents via vector similarity.

---

## Objective

Generate vector embeddings for extracted document text using Ollama (primary) or ONNX Runtime (CPU fallback), store in sqlite-vec, and enable semantic search and hybrid search modes.

---

## Tasks

### 5.1 — Embedding Pipeline Task
- [x] Long-running `BackgroundService` task reading from `Channel<DocumentId>`
- [x] Picks up documents posted by the extractor (Phase 3)
- [x] Also supports polling: query DB for `extracted_at IS NOT NULL AND embedded_at IS NULL` on startup
- [x] Process one document at a time
- [x] On success: update `documents.embedded_at` and `documents.chunk_count`
- [x] On failure: log error, leave `embedded_at = NULL`, continue

### 5.2 — Text Chunking
- [x] Split `extracted_text` into chunks for embedding:
  - **Chunk size**: ~500 characters
  - **Overlap**: 100 characters between consecutive chunks
  - **Split on**: sentence boundaries (`. `, `! `, `? `), then word boundaries
  - **Min chunk**: 50 characters (skip tiny trailing chunks)
- [x] Each chunk has: `document_id`, `chunk_index` (0-based), `chunk_text`
- [x] Typical document = 1–5 chunks

### 5.3 — Ollama Embedding Generation
- [x] HTTP POST to `http://localhost:11434/api/embed` with model `nomic-embed-text`
- [x] Send one chunk at a time (batch support if Ollama adds it)
- [x] Response: 768-dimensional float vector
- [x] Detect Ollama availability on startup → set a flag for fallback mode
- [x] Rate limiting: configurable max embeddings per second (default: 10)

### 5.4 — ONNX Runtime Fallback
- [x] If Ollama unavailable: use `Microsoft.ML.OnnxRuntime` with a bundled ONNX embedding model
- [x] Bundle `all-MiniLM-L6-v2` (384-dim) or equivalent small model as an embedded resource
- [x] CPU inference — slower but works everywhere
- [x] Set vector dimensions dynamically based on which backend is used:
  - Ollama `nomic-embed-text` → 768d
  - ONNX `all-MiniLM-L6-v2` → 384d
- [x] Handle dimension mismatch if user switches between backends (re-embed flag)

### 5.5 — sqlite-vec Storage
- [x] Load sqlite-vec native extension on database open (`Microsoft.Data.Sqlite` `EnableExtensions()`)
- [x] Bundle pre-built sqlite-vec binaries for: macOS arm64, macOS x64, Windows x64
- [x] Create `vec_chunks` virtual table (if not exists):
  ```sql
  CREATE VIRTUAL TABLE IF NOT EXISTS vec_chunks USING vec0(
      document_id   INTEGER,
      chunk_index   INTEGER,
      embedding     float[768]
  );
  ```
- [x] Insert embedding rows: `(document_id, chunk_index, embedding_blob)`
- [x] On re-embed (`--force`): delete existing chunks for the document, then re-insert

### 5.6 — Semantic Search
- [x] Convert user query to an embedding vector using the same model
- [x] Query sqlite-vec:
  ```sql
  SELECT document_id, chunk_index, distance
  FROM vec_chunks
  WHERE embedding MATCH ?query_embedding
    AND k = ?limit
  ORDER BY distance
  ```
- [x] Join results back to `documents` table for full metadata
- [x] Deduplicate: if multiple chunks from the same document match, keep the best-scoring one

### 5.7 — Hybrid Search
- [x] `mode = "hybrid"`: run both FTS5 keyword search and semantic search
- [x] Normalise scores from both (FTS5 bm25 rank and vector distance to 0.0–1.0 range)
- [x] Combine using reciprocal rank fusion (RRF) or weighted average
- [x] Return merged, deduplicated result list
- [x] Support category/date/sender filters in both modes

### 5.8 — CLI Commands
- [x] `hermes embed` — process backlog of un-embedded documents
- [x] `hermes embed --force` — re-embed all documents (e.g. after model change)
- [x] `hermes embed --limit 100` — cap at N documents
- [x] `hermes search --semantic QUERY` — semantic search mode
- [x] `hermes search --hybrid QUERY` — hybrid search mode (keyword + semantic)
- [x] Progress output for embed: `[42/350] Embedding invoices/Invoice-2025-001.pdf (3 chunks)...`

---

## NuGet Packages

| Package | Purpose |
|---------|---------|
| `Microsoft.ML.OnnxRuntime` | ONNX CPU inference fallback |
| Pre-built sqlite-vec native lib | Vector search extension |

---

## Acceptance Criteria

- [x] Documents with extracted text get embeddings generated automatically (Ollama or ONNX)
- [x] `hermes search --semantic "plumbing work receipt"` finds relevant documents by meaning
- [x] `hermes search --hybrid "CBA March statement"` combines keyword + semantic results
- [x] Semantic search within a category works: `--semantic --category property`
- [x] `hermes embed` processes the backlog with progress output
- [x] `hermes embed --force` re-embeds all documents
- [x] If Ollama is unavailable, ONNX fallback produces embeddings (slower, lower dimensionality)
- [x] Switching embedding backends and re-embedding works cleanly
- [x] Results correctly deduplicate multiple chunk hits from the same document
