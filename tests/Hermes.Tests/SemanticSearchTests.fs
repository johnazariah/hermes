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
