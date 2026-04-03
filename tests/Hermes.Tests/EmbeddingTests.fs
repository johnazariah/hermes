module Hermes.Tests.EmbeddingTests

open System
open System.Threading.Tasks
open Xunit
open Hermes.Core

open FsCheck
open FsCheck.Xunit
// ─── Helpers ─────────────────────────────────────────────────────────

/// Create an in-memory test database with schema initialised.
let createTestDbWithSchema () : Task<Algebra.Database> =
    task {
        let db = TestHelpers.createRawDb ()
        let! _ = db.initSchema ()
        do! Embeddings.initSchema db
        return db
    }

// ─── Text chunking tests ─────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_ChunkText_EmptyString_ReturnsEmpty`` () =
    let result = Embeddings.chunkText 500 100 ""
    Assert.Empty(result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_ChunkText_WhitespaceOnly_ReturnsEmpty`` () =
    let result = Embeddings.chunkText 500 100 "   \n\t  "
    Assert.Empty(result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_ChunkText_ShortText_ReturnsSingleChunk`` () =
    let text = "This is a short text."
    let result = Embeddings.chunkText 500 100 text
    Assert.Equal(1, result.Length)
    Assert.Equal(text, result.[0].Text)
    Assert.Equal(0, result.[0].Index)
    Assert.Equal(0, result.[0].StartChar)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_ChunkText_ExactlyChunkSize_ReturnsSingleChunk`` () =
    let text = String.replicate 500 "a"
    let result = Embeddings.chunkText 500 100 text
    Assert.Equal(1, result.Length)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_ChunkText_LongText_ProducesOverlappingChunks`` () =
    // Create text with sentences
    let sentences =
        [| for i in 1..20 do
               $"This is sentence number {i} in the document. " |]

    let text = String.concat "" sentences
    let result = Embeddings.chunkText 500 100 text

    // Should produce multiple chunks
    Assert.True(result.Length > 1, $"Expected multiple chunks, got {result.Length}")

    // Each chunk should be ≤ chunk size + a small margin for sentence alignment
    for chunk in result do
        Assert.True(chunk.Text.Length <= 600, $"Chunk too large: {chunk.Text.Length}")

    // Chunks should be indexed sequentially
    let indices = result |> List.map (fun c -> c.Index)
    let expected = [ 0 .. result.Length - 1 ]
    Assert.True((expected = indices), $"Expected indices {expected}, got {indices}")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_ChunkText_Overlap_ChunksShareContent`` () =
    let text =
        String.concat ""
            [| for i in 1..30 do
                   $"Word{i} " |]

    let result = Embeddings.chunkText 100 30 text
    Assert.True(result.Length >= 2, $"Expected at least 2 chunks to test overlap, got {result.Length}")

    // The end of chunk N should overlap with the start of chunk N+1
    let chunk0End = result.[0].Text
    let chunk1Start = result.[1].Text

    // Extract last ~30 chars of chunk0
    let overlapRegion =
        if chunk0End.Length > 30 then
            chunk0End.Substring(chunk0End.Length - 30)
        else
            chunk0End

    // The overlap region should appear somewhere in chunk1
    Assert.True(
        chunk1Start.Contains(overlapRegion.Trim()),
        $"Expected overlap between chunks. Chunk0 ends with: '{overlapRegion}', Chunk1 starts with: '{chunk1Start.Substring(0, min 50 chunk1Start.Length)}'"
    )

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_ChunkText_SentenceBoundary_SplitsOnSentence`` () =
    // Create text where a sentence ends near the chunk boundary
    let text = String.replicate 45 "word " + "end. " + String.replicate 45 "more "
    let result = Embeddings.chunkText 250 50 text
    // The first chunk should ideally end at a sentence boundary
    if result.Length > 1 then
        let firstChunk = result.[0].Text
        Assert.True(
            firstChunk.EndsWith(". ") || firstChunk.EndsWith(".") || firstChunk.TrimEnd().EndsWith("."),
            $"Expected sentence boundary split, got: ...'{firstChunk.[max 0 (firstChunk.Length - 20) ..]}'"
        )

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_ChunkText_AllContent_IsCovered`` () =
    let text =
        String.concat ". "
            [| for i in 1..15 do
                   $"Sentence number {i} with some extra content" |]

    let result = Embeddings.chunkText 200 50 text

    // All content from original text should appear in at least one chunk
    let allChunkText = result |> List.map (fun c -> c.Text) |> String.concat ""

    // Check a few key phrases exist
    Assert.Contains("Sentence number 1", allChunkText)
    Assert.Contains("Sentence number 15", allChunkText)

// ─── Embedding serialisation tests ───────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_BlobRoundTrip_PreservesData`` () =
    let original = [| 1.0f; -2.5f; 0.0f; 3.14159f; -0.001f |]
    let blob = Embeddings.embeddingToBlob original
    let restored = Embeddings.blobToEmbedding blob
    Assert.Equal<float32[]>(original, restored)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_BlobRoundTrip_EmptyArray`` () =
    let original = Array.empty<float32>
    let blob = Embeddings.embeddingToBlob original
    let restored = Embeddings.blobToEmbedding blob
    Assert.Empty(restored)

// ─── Cosine similarity tests ─────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SemanticSearch_CosineSimilarity_IdenticalVectors_ReturnsOne`` () =
    let v = [| 1.0f; 2.0f; 3.0f |]
    let sim = SemanticSearch.cosineSimilarity v v
    Assert.InRange(sim, 0.999, 1.001)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SemanticSearch_CosineSimilarity_OrthogonalVectors_ReturnsZero`` () =
    let a = [| 1.0f; 0.0f; 0.0f |]
    let b = [| 0.0f; 1.0f; 0.0f |]
    let sim = SemanticSearch.cosineSimilarity a b
    Assert.InRange(sim, -0.001, 0.001)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SemanticSearch_CosineSimilarity_OppositeVectors_ReturnsNegOne`` () =
    let a = [| 1.0f; 2.0f; 3.0f |]
    let b = [| -1.0f; -2.0f; -3.0f |]
    let sim = SemanticSearch.cosineSimilarity a b
    Assert.InRange(sim, -1.001, -0.999)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SemanticSearch_CosineSimilarity_EmptyVectors_ReturnsZero`` () =
    let sim = SemanticSearch.cosineSimilarity [||] [||]
    Assert.Equal(0.0, sim)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SemanticSearch_CosineSimilarity_DifferentLengths_ReturnsZero`` () =
    let a = [| 1.0f; 2.0f |]
    let b = [| 1.0f; 2.0f; 3.0f |]
    let sim = SemanticSearch.cosineSimilarity a b
    Assert.Equal(0.0, sim)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SemanticSearch_CosineSimilarity_ZeroVector_ReturnsZero`` () =
    let a = [| 0.0f; 0.0f; 0.0f |]
    let b = [| 1.0f; 2.0f; 3.0f |]
    let sim = SemanticSearch.cosineSimilarity a b
    Assert.Equal(0.0, sim)

// ─── RRF merging tests ───────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SemanticSearch_RRF_EmptyLists_ReturnsEmpty`` () =
    let result = SemanticSearch.reciprocalRankFusion 60 [] []
    Assert.Empty(result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SemanticSearch_RRF_SingleList_ScoresCorrectly`` () =
    let listA = [ (1L, 0.9); (2L, 0.8); (3L, 0.7) ]
    let result = SemanticSearch.reciprocalRankFusion 60 listA []

    // First item should have highest score
    Assert.Equal(1L, fst result.[0])
    Assert.True(snd result.[0] > snd result.[1])
    Assert.True(snd result.[1] > snd result.[2])

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SemanticSearch_RRF_OverlappingLists_BothContribute`` () =
    let listA = [ (1L, 0.9); (2L, 0.8) ]
    let listB = [ (2L, 0.95); (3L, 0.7) ]
    let result = SemanticSearch.reciprocalRankFusion 60 listA listB

    // Doc 2 appears in both lists, so should get boosted
    let doc2Score =
        result |> List.find (fun (id, _) -> id = 2L) |> snd

    let doc1Score =
        result |> List.find (fun (id, _) -> id = 1L) |> snd

    let doc3Score =
        result |> List.find (fun (id, _) -> id = 3L) |> snd

    // Doc 2 should score higher than doc 1 and doc 3 individually
    Assert.True(doc2Score > doc1Score, "Doc 2 (in both lists) should beat doc 1 (in one list)")
    Assert.True(doc2Score > doc3Score, "Doc 2 (in both lists) should beat doc 3 (in one list)")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SemanticSearch_RRF_PreservesAllDocuments`` () =
    let listA = [ (1L, 0.9); (2L, 0.8) ]
    let listB = [ (3L, 0.95); (4L, 0.7) ]
    let result = SemanticSearch.reciprocalRankFusion 60 listA listB
    Assert.Equal(4, result.Length)
    let ids = result |> List.map fst |> Set.ofList
    Assert.True(ids.Contains(1L) && ids.Contains(2L) && ids.Contains(3L) && ids.Contains(4L))

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SemanticSearch_RRF_TopRankedInBothLists_WinsOverall`` () =
    let listA = [ (10L, 0.9); (20L, 0.5) ]
    let listB = [ (10L, 0.95); (30L, 0.4) ]
    let result = SemanticSearch.reciprocalRankFusion 60 listA listB
    Assert.Equal(10L, fst result.[0])

// ─── Deduplication / integration tests ───────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Embeddings_EmbedDocument_ChunksAndStores`` () =
    task {
        let! db = createTestDbWithSchema ()

        try
            let client = TestHelpers.fakeEmbedder 4

            // Insert a test document
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text)
                       VALUES ('manual_drop', 'test/doc.pdf', 'invoices', 'sha1', 'Short text for embedding test.')"""
                    []

            let! result = Embeddings.embedDocument db Logging.silent TestHelpers.defaultClock client 1L "Short text for embedding test."

            match result with
            | Ok count ->
                Assert.True(count > 0)

                // Verify chunks stored
                let! chunkCount =
                    db.execScalar "SELECT COUNT(*) FROM document_chunks WHERE document_id = 1" []

                let count = match chunkCount with null -> 0L | v -> v :?> int64
                Assert.True(count > 0L)
            | Error e ->
                failwith $"Expected Ok, got Error: {e}"
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Embeddings_EmbedDocument_UpdatesDocumentMetadata`` () =
    task {
        let! db = createTestDbWithSchema ()

        try
            let client = TestHelpers.fakeEmbedder 4

            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text)
                       VALUES ('manual_drop', 'test/doc.pdf', 'invoices', 'sha2', 'Some text here.')"""
                    []

            let! _ = Embeddings.embedDocument db Logging.silent TestHelpers.defaultClock client 1L "Some text here."

            let! embeddedAt =
                db.execScalar "SELECT embedded_at FROM documents WHERE id = 1" []

            Assert.NotNull(embeddedAt)

            let! chunkCount =
                db.execScalar "SELECT chunk_count FROM documents WHERE id = 1" []

            Assert.NotNull(chunkCount)
            let count = match chunkCount with null -> 0L | v -> v :?> int64
            Assert.True(count > 0L)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Embeddings_EmbedDocument_EmptyText_ReturnsZero`` () =
    task {
        let! db = createTestDbWithSchema ()

        try
            let client = TestHelpers.fakeEmbedder 4
            let! result = Embeddings.embedDocument db Logging.silent TestHelpers.defaultClock client 1L ""

            match result with
            | Ok count -> Assert.Equal(0, count)
            | Error e -> failwith $"Expected Ok 0, got Error: {e}"
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Embeddings_EmbedDocument_FailingClient_ReportsErrors`` () =
    task {
        let! db = createTestDbWithSchema ()

        try
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text)
                       VALUES ('manual_drop', 'test/doc.pdf', 'invoices', 'sha3', 'Some text.')"""
                    []

            let! result = Embeddings.embedDocument db Logging.silent TestHelpers.defaultClock TestHelpers.failingEmbedder 1L "Some text."

            match result with
            | Error msg -> Assert.Contains("failed", msg)
            | Ok _ -> failwith "Expected Error due to failing client"
        finally
            db.dispose ()
    }

// ─── Keyword search integration ──────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``SemanticSearch_KeywordSearch_FindsMatchingDocuments`` () =
    task {
        let! db = createTestDbWithSchema ()

        try
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (source_type, saved_path, category, sha256, sender, subject, original_name, extracted_text)
                       VALUES ('manual_drop', 'invoices/plumber.pdf', 'invoices', 'sha100', 'bob@example.com', 'Plumbing Invoice', 'invoice.pdf', 'Plumbing services rendered')"""
                    []

            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (source_type, saved_path, category, sha256, sender, subject, original_name, extracted_text)
                       VALUES ('manual_drop', 'receipts/grocery.pdf', 'receipts', 'sha101', 'shop@store.com', 'Grocery Receipt', 'receipt.pdf', 'Milk bread eggs')"""
                    []

            let! results = SemanticSearch.keywordSearch db "plumbing" 10
            Assert.True(results.Length > 0, "Should find plumbing document")
            let docId = fst results.[0]
            Assert.Equal(1L, docId)
        finally
            db.dispose ()
    }

// ─── Additional chunk tests ──────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_ChunkText_ShortText_SingleChunk`` () =
    let chunks = Embeddings.chunkText 500 100 "Short text"
    Assert.Equal(1, chunks.Length)
    Assert.Equal(0, chunks.[0].Index)
    Assert.Equal("Short text", chunks.[0].Text)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_ChunkText_LongText_MultipleChunks`` () =
    let text = String.replicate 200 "word "
    let chunks = Embeddings.chunkText 100 20 text
    Assert.True(chunks.Length > 1)
    // Indexes should be sequential
    for i in 0 .. chunks.Length - 1 do
        Assert.Equal(i, chunks.[i].Index)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_ChunkText_OverlapPresent`` () =
    let text = String.replicate 100 "word "
    let chunks = Embeddings.chunkText 50 10 text
    if chunks.Length >= 2 then
        // Second chunk should start before first chunk ends
        let firstEnd = chunks.[0].StartChar + chunks.[0].Text.Length
        Assert.True(chunks.[1].StartChar < firstEnd)

// ─── Blob round-trip ─────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_BlobRoundTrip_PreservesValues`` () =
    let original = [| 1.0f; -2.5f; 3.14f; 0.0f |]
    let blob = Embeddings.embeddingToBlob original
    let restored = Embeddings.blobToEmbedding blob
    Assert.Equal(original.Length, restored.Length)
    for i in 0 .. original.Length - 1 do
        Assert.Equal(original.[i], restored.[i])

// ─── Store chunk + initSchema ────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Embeddings_InitSchema_CreatesChunkTable`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! Embeddings.initSchema db
            let! exists = db.tableExists "document_chunks"
            Assert.True(exists)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Embeddings_StoreChunk_InsertsRow`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! Embeddings.initSchema db
            // Need a document row for FK constraint
            let! _ = db.execNonQuery
                        "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('manual_drop', 'test.pdf', 'invoices', 'sha1')"
                        []
            let embedding = [| 1.0f; 2.0f; 3.0f |]
            do! Embeddings.storeChunk db TestHelpers.defaultClock 1L 0 "test chunk" (Some embedding)
            let! count = db.execScalar "SELECT COUNT(*) FROM document_chunks WHERE document_id = 1" []
            let c = match count with null -> 0L | v -> v :?> int64
            Assert.Equal(1L, c)
        finally db.dispose ()
    }

// ─── embedDocument integration ───────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Embeddings_EmbedDocument_StoresChunksAndUpdatesDoc`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! Embeddings.initSchema db
            let! _ = db.execNonQuery
                        "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text) VALUES ('manual_drop', 'test.pdf', 'invoices', 'sha1', 'Test doc')"
                        []
            let client = TestHelpers.fakeEmbedder 768
            let! result = Embeddings.embedDocument db TestHelpers.silentLogger TestHelpers.defaultClock client 1L "This is a test document."
            Assert.True(Result.isOk result)
            let! count = db.execScalar "SELECT COUNT(*) FROM document_chunks WHERE document_id = 1" []
            Assert.True((match count with null -> 0L | v -> v :?> int64) > 0L)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Embeddings_EmbedDocument_EmptyText_ReturnsOkZero`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! Embeddings.initSchema db
            let! result = Embeddings.embedDocument db TestHelpers.silentLogger TestHelpers.defaultClock (TestHelpers.fakeEmbedder 768) 1L ""
            Assert.Equal(Ok 0, result)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Embeddings_EmbedDocument_FailingClient_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! Embeddings.initSchema db
            let! _ = db.execNonQuery
                        "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('manual_drop', 'test.pdf', 'invoices', 'sha2')"
                        []
            let! result = Embeddings.embedDocument db TestHelpers.silentLogger TestHelpers.defaultClock TestHelpers.failingEmbedder 1L "Some text"
            Assert.True(Result.isError result)
        finally db.dispose ()
    }

// ─── batchEmbed tests ────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Embeddings_BatchEmbed_UnavailableClient_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! Embeddings.initSchema db
            let! result = Embeddings.batchEmbed db TestHelpers.silentLogger TestHelpers.defaultClock TestHelpers.failingEmbedder false None None
            Assert.True(Result.isError result)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Embeddings_BatchEmbed_NoDocs_ReturnsOkZero`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! Embeddings.initSchema db
            let client = TestHelpers.fakeEmbedder 768
            let! result = Embeddings.batchEmbed db TestHelpers.silentLogger TestHelpers.defaultClock client false None None
            Assert.Equal(Ok 0, result)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Embeddings_BatchEmbed_WithDocs_EmbedsSuccessfully`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! Embeddings.initSchema db
            let! _ = db.execNonQuery
                        "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text) VALUES ('manual_drop', 'a.pdf', 'invoices', 'sha1', 'test document content for embedding')"
                        []
            let client = TestHelpers.fakeEmbedder 4
            let! result = Embeddings.batchEmbed db TestHelpers.silentLogger TestHelpers.defaultClock client false None None
            match result with
            | Ok count -> Assert.True(count > 0, $"Expected >0, got {count}")
            | Error e -> failwith $"Expected Ok, got Error: {e}"
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Embeddings_BatchEmbed_WithLimit_RespectsLimit`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! Embeddings.initSchema db
            let! _ = db.execNonQuery
                        "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text) VALUES ('manual_drop', 'a.pdf', 'invoices', 'sha1', 'first document')"
                        []
            let! _ = db.execNonQuery
                        "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text) VALUES ('manual_drop', 'b.pdf', 'invoices', 'sha2', 'second document')"
                        []
            let client = TestHelpers.fakeEmbedder 4
            let! result = Embeddings.batchEmbed db TestHelpers.silentLogger TestHelpers.defaultClock client false (Some 1) None
            match result with
            | Ok count -> Assert.Equal(1, count)
            | Error e -> failwith $"Expected Ok, got Error: {e}"
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Embeddings_BatchEmbed_Force_ReEmbedsAlreadyEmbedded`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! Embeddings.initSchema db
            let! _ = db.execNonQuery
                        "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text, embedded_at) VALUES ('manual_drop', 'a.pdf', 'invoices', 'sha1', 'already embedded doc', datetime('now'))"
                        []
            let client = TestHelpers.fakeEmbedder 4
            // Without force: should find 0 docs (already embedded)
            let! resultNoForce = Embeddings.batchEmbed db TestHelpers.silentLogger TestHelpers.defaultClock client false None None
            Assert.Equal(Ok 0, resultNoForce)
            // With force: should re-embed
            let! resultForce = Embeddings.batchEmbed db TestHelpers.silentLogger TestHelpers.defaultClock client true None None
            match resultForce with
            | Ok count -> Assert.True(count > 0, "Force should re-embed already embedded docs")
            | Error e -> failwith $"Expected Ok, got Error: {e}"
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Embeddings_BatchEmbed_ProgressCallback_Called`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! Embeddings.initSchema db
            let! _ = db.execNonQuery
                        "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text) VALUES ('manual_drop', 'a.pdf', 'invoices', 'sha1', 'test doc for progress')"
                        []
            let client = TestHelpers.fakeEmbedder 4
            let mutable callCount = 0
            let progress : Embeddings.ProgressCallback = fun _completed _total -> callCount <- callCount + 1
            let! _ = Embeddings.batchEmbed db TestHelpers.silentLogger TestHelpers.defaultClock client false None (Some progress)
            Assert.True(callCount > 0, "Progress callback should have been called")
        finally db.dispose ()
    }

// ─── storeChunk edge cases ───────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Embeddings_StoreChunk_NoEmbedding_InsertsNullBlob`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! Embeddings.initSchema db
            let! _ = db.execNonQuery
                        "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('manual_drop', 'test.pdf', 'invoices', 'sha1')"
                        []
            do! Embeddings.storeChunk db TestHelpers.defaultClock 1L 0 "test chunk no embedding" None
            let! count = db.execScalar "SELECT COUNT(*) FROM document_chunks WHERE document_id = 1" []
            Assert.Equal(1L, count :?> int64)
            let! embResult = db.execScalar "SELECT embedding FROM document_chunks WHERE document_id = 1" []
            Assert.True(embResult = null || embResult :? DBNull, "Expected null embedding")
        finally db.dispose ()
    }

// ─── Additional blob roundtrip tests ─────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_BlobRoundTrip_SingleElement_Preserves`` () =
    let original = [| 3.14f |]
    let blob = Embeddings.embeddingToBlob original
    let result = Embeddings.blobToEmbedding blob
    Assert.Equal(1, result.Length)
    Assert.Equal(original.[0], result.[0])

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_BlobRoundTrip_768Dims_Preserves`` () =
    let original = Array.init 768 (fun i -> float32 i / 100.0f)
    let blob = Embeddings.embeddingToBlob original
    let result = Embeddings.blobToEmbedding blob
    Assert.Equal(768, result.Length)
    for i in 0 .. 767 do
        Assert.Equal(original.[i], result.[i])

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_BlobRoundTrip_NegativeValues_Preserves`` () =
    let original = [| -1.5f; 0.0f; 1.5f; -0.001f |]
    let blob = Embeddings.embeddingToBlob original
    let result = Embeddings.blobToEmbedding blob
    Assert.Equal(original.Length, result.Length)
    for i in 0 .. original.Length - 1 do
        Assert.Equal(original.[i], result.[i])

// ─── Additional chunking tests ───────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_ChunkText_OnlyWhitespace_ReturnsEmpty`` () =
    let chunks = Embeddings.chunkText 100 20 "   \t\n   "
    Assert.Empty(chunks)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_ChunkText_VerySmallChunkSize_Works`` () =
    let chunks = Embeddings.chunkText 5 2 "Hello world, this is a test"
    Assert.True(chunks.Length > 1)
    for chunk in chunks do
        Assert.True(chunk.Text.Length > 0)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_ChunkText_ZeroOverlap_NoOverlap`` () =
    let text = String.replicate 10 "abcdefghij "
    let chunks = Embeddings.chunkText 20 0 text
    Assert.True(chunks.Length > 1)
    for i in 0 .. chunks.Length - 1 do
        Assert.Equal(i, chunks.[i].Index)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_ChunkText_LargeOverlap_StillProgresses`` () =
    let text = String.replicate 20 "word "
    let chunks = Embeddings.chunkText 20 15 text
    Assert.True(chunks.Length > 1)
    let mutable lastStart = -1
    for chunk in chunks do
        Assert.True(chunk.StartChar > lastStart || chunk.StartChar = 0)
        lastStart <- chunk.StartChar

// ─── Additional coverage tests ───────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_BlobRoundTrip_1536Dims_Preserves`` () =
    let original = Array.init 1536 (fun i -> float32 i * 0.01f)
    let blob = Embeddings.embeddingToBlob original
    let result = Embeddings.blobToEmbedding blob
    Assert.Equal(1536, result.Length)
    for i in 0 .. 1535 do
        Assert.Equal(original.[i], result.[i])

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_ChunkText_SingleChar_ReturnsSingleChunk`` () =
    let chunks = Embeddings.chunkText 500 100 "a"
    Assert.Equal(1, chunks.Length)
    Assert.Equal("a", chunks.[0].Text)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_ChunkText_ExactlyAtBoundaryPlusOne_ProducesTwoChunks`` () =
    let text = String.replicate 501 "x"
    let chunks = Embeddings.chunkText 500 100 text
    Assert.True(chunks.Length >= 2, $"Expected >=2 chunks, got {chunks.Length}")

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Embeddings_BatchEmbed_WithProgressCallback_ReportsCorrectTotal`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! Embeddings.initSchema db
            let! _ =
                db.execNonQuery
                    "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text) VALUES ('manual_drop', 'a.pdf', 'invoices', 'sha10', 'first document text')"
                    []
            let! _ =
                db.execNonQuery
                    "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text) VALUES ('manual_drop', 'b.pdf', 'invoices', 'sha11', 'second document text')"
                    []
            let client = TestHelpers.fakeEmbedder 4
            let mutable reportedTotal = 0
            let progress : Embeddings.ProgressCallback =
                fun _completed total -> reportedTotal <- total
            let! _ = Embeddings.batchEmbed db TestHelpers.silentLogger TestHelpers.defaultClock client false None (Some progress)
            Assert.Equal(2, reportedTotal)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Embeddings_BatchEmbed_SkipsDocsWithNullText`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! Embeddings.initSchema db
            let! _ =
                db.execNonQuery
                    "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text) VALUES ('manual_drop', 'a.pdf', 'invoices', 'sha20', NULL)"
                    []
            let client = TestHelpers.fakeEmbedder 4
            let! result = Embeddings.batchEmbed db TestHelpers.silentLogger TestHelpers.defaultClock client false None None
            Assert.Equal(Ok 0, result)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Embeddings_BatchEmbed_SkipsDocsWithEmptyText`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! Embeddings.initSchema db
            let! _ =
                db.execNonQuery
                    "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text) VALUES ('manual_drop', 'a.pdf', 'invoices', 'sha21', '')"
                    []
            let client = TestHelpers.fakeEmbedder 4
            let! result = Embeddings.batchEmbed db TestHelpers.silentLogger TestHelpers.defaultClock client false None None
            Assert.Equal(Ok 0, result)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Embeddings_EmbedDocument_WhitespaceOnlyText_ReturnsOkZero`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! Embeddings.initSchema db
            let client = TestHelpers.fakeEmbedder 4
            let! result = Embeddings.embedDocument db TestHelpers.silentLogger TestHelpers.defaultClock client 1L "   \n\t   "
            Assert.Equal(Ok 0, result)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Embeddings_EmbeddingToBlob_ByteLength_IsCorrect`` () =
    let embedding = [| 1.0f; 2.0f; 3.0f; 4.0f |]
    let blob = Embeddings.embeddingToBlob embedding
    Assert.Equal(16, blob.Length)

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Embeddings_StoreChunk_WithEmbedding_SetsEmbeddedAt`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! Embeddings.initSchema db
            let! _ =
                db.execNonQuery
                    "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('manual_drop', 'test.pdf', 'invoices', 'sha30')"
                    []
            let embedding = [| 1.0f; 2.0f; 3.0f; 4.0f |]
            do! Embeddings.storeChunk db TestHelpers.defaultClock 1L 0 "test chunk text" (Some embedding)
            let! rows =
                db.execReader
                    "SELECT embedded_at FROM document_chunks WHERE document_id = 1 AND chunk_index = 0"
                    []
            Assert.Equal(1, rows.Length)
            let embeddedAt = rows.[0] |> Map.find "embedded_at"
            Assert.False(embeddedAt :? DBNull, "embedded_at should not be DBNull")
            Assert.IsType<string>(embeddedAt) |> ignore
        finally db.dispose ()
    }

// ─── Additional embed document tests ─────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Embeddings_EmbedDocument_ShortText_SingleChunk`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! Embeddings.initSchema db
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('manual_drop', 'a.pdf', 'invoices', 'sha1')" []
            let client = TestHelpers.fakeEmbedder 4
            let! result = Embeddings.embedDocument db TestHelpers.silentLogger TestHelpers.defaultClock client 1L "Short text"
            match result with
            | Ok count -> Assert.Equal(1, count)
            | Error e -> failwith $"Unexpected error: {e}"
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Embeddings_StoreChunk_WithEmbedding_StoresBlob`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! Embeddings.initSchema db
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('manual_drop', 'test.pdf', 'invoices', 'sha1')" []
            let embedding = [| 1.0f; 2.0f; 3.0f; 4.0f |]
            do! Embeddings.storeChunk db TestHelpers.defaultClock 1L 0 "test chunk with embedding" (Some embedding)
            let! rows = db.execReader "SELECT embedding FROM document_chunks WHERE document_id = 1" []
            Assert.Equal(1, rows.Length)
            let emb = rows.[0] |> Map.tryFind "embedding"
            Assert.True(emb.IsSome)
        finally db.dispose ()
    }

// ─── Property-based tests ────────────────────────────────────────────

[<Property>]
[<Trait("Category", "Property")>]
let ``Embeddings_BlobRoundtrip_PreservesValues`` (values: float32 list) =
    let arr = values |> List.toArray
    let blob = Embeddings.embeddingToBlob arr
    let restored = Embeddings.blobToEmbedding blob
    restored.Length = arr.Length
    && Embeddings.embeddingToBlob restored = blob
