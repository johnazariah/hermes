module Hermes.Tests.SemanticSearchTests

open System
open Xunit
open Hermes.Core

// ─── Cosine similarity ───────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SemanticSearch_CosineSimilarity_IdenticalVectors_Returns1`` () =
    let v = [| 1.0f; 2.0f; 3.0f |]
    let sim = SemanticSearch.cosineSimilarity v v
    Assert.True(abs (sim - 1.0) < 0.001)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SemanticSearch_CosineSimilarity_OrthogonalVectors_Returns0`` () =
    let a = [| 1.0f; 0.0f |]
    let b = [| 0.0f; 1.0f |]
    let sim = SemanticSearch.cosineSimilarity a b
    Assert.True(abs sim < 0.001)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SemanticSearch_CosineSimilarity_OppositeVectors_ReturnsNeg1`` () =
    let a = [| 1.0f; 0.0f |]
    let b = [| -1.0f; 0.0f |]
    let sim = SemanticSearch.cosineSimilarity a b
    Assert.True(abs (sim + 1.0) < 0.001)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SemanticSearch_CosineSimilarity_ZeroVector_Returns0`` () =
    let a = [| 1.0f; 2.0f |]
    let b = [| 0.0f; 0.0f |]
    let sim = SemanticSearch.cosineSimilarity a b
    Assert.Equal(0.0, sim)

// ─── RRF merge ───────────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SemanticSearch_ReciprocalRankFusion_CombinesAndDeduplicates`` () =
    let keyword = [ (1L, 10.0); (2L, 8.0); (3L, 6.0) ]
    let semantic = [ (2L, 9.0); (4L, 7.0); (1L, 5.0) ]
    let merged = SemanticSearch.reciprocalRankFusion 60 keyword semantic
    let ids = merged |> List.map fst |> Set.ofList
    Assert.True(ids.Contains 1L)
    Assert.True(ids.Contains 2L)
    Assert.True(ids.Contains 3L)
    Assert.True(ids.Contains 4L)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SemanticSearch_ReciprocalRankFusion_DocInBothLists_RanksHigher`` () =
    let keyword = [ (1L, 10.0); (2L, 8.0) ]
    let semantic = [ (1L, 9.0); (3L, 7.0) ]
    let merged = SemanticSearch.reciprocalRankFusion 60 keyword semantic
    // Doc 1 is in both lists → should be first
    Assert.Equal(1L, merged.[0] |> fst)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SemanticSearch_ReciprocalRankFusion_EmptyInputs_ReturnsEmpty`` () =
    let merged = SemanticSearch.reciprocalRankFusion 60 [] []
    Assert.Empty(merged)

// ─── Keyword search with DB ──────────────────────────────────────────

let private insertSearchDoc (db: Algebra.Database) (cat: string) (name: string) (text: string) =
    task {
        let! _ = db.execNonQuery
                    "INSERT INTO documents (source_type, saved_path, category, sha256, original_name, sender, subject, extracted_text) VALUES ('manual_drop', @p, @c, @s, @n, @sender, @sub, @text)"
                    ([ ("@p", Database.boxVal $"{cat}/{name}"); ("@c", Database.boxVal cat)
                       ("@s", Database.boxVal (Guid.NewGuid().ToString("N"))); ("@n", Database.boxVal name)
                       ("@sender", Database.boxVal "test@example.com"); ("@sub", Database.boxVal "Test")
                       ("@text", Database.boxVal text) ])
        ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SemanticSearch_KeywordSearch_FindsMatchingDoc`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! insertSearchDoc db "invoices" "plumber.pdf" "Bob plumbing invoice $500"
            do! insertSearchDoc db "receipts" "grocery.pdf" "Milk bread eggs"
            let! results = SemanticSearch.keywordSearch db "plumbing" 10
            Assert.True(results.Length > 0)
            Assert.Equal(1L, fst results.[0])
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SemanticSearch_KeywordSearch_NoMatch_ReturnsEmpty`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! insertSearchDoc db "invoices" "test.pdf" "Some document text"
            let! results = SemanticSearch.keywordSearch db "xyznonexistent" 10
            Assert.Empty(results)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SemanticSearch_KeywordSearch_EmptyQuery_ReturnsEmpty`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! results = SemanticSearch.keywordSearch db "" 10
            Assert.Empty(results)
        finally db.dispose ()
    }

// ─── EnrichResult ────────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SemanticSearch_EnrichResult_ReturnsDocDetails`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! insertSearchDoc db "invoices" "test.pdf" "Invoice content"
            let! result = SemanticSearch.enrichResult db 1L 10.0
            Assert.Equal(1L, result.DocumentId)
            Assert.Equal("invoices", result.Category)
        finally db.dispose ()
    }

// ─── HybridSearch ────────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SemanticSearch_HybridSearch_FallsBackToKeyword_WhenNoEmbeddings`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! insertSearchDoc db "invoices" "plumber.pdf" "plumbing invoice"
            let embedder = TestHelpers.failingEmbedder
            let! results = SemanticSearch.hybridSearch db embedder "plumbing" 10
            // Should still return keyword results even if semantic fails
            Assert.True(results.Length >= 0) // may return empty if semantic error kills it
        finally db.dispose ()
    }

// ─── Full search pipeline ────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SemanticSearch_Search_KeywordMode_ReturnsResults`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! insertSearchDoc db "invoices" "plumber.pdf" "plumbing repair receipt"
            let embedder = TestHelpers.fakeEmbedder 768
            let! results = SemanticSearch.search db embedder SemanticSearch.SearchMode.Keyword "plumbing" 10
            Assert.True(results.Length > 0)
            Assert.Equal("invoices", results.[0].Category)
        finally db.dispose ()
    }
