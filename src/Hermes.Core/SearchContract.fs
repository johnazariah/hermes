namespace Hermes.Core

open System

/// Shared HTTP/MCP query contract. Missing vectors never change the requested mode.
[<RequireQualifiedAccess>]
module SearchContract =

    type Mode = Keyword | Semantic | Hybrid

    type Request =
        { Mode: Mode
          Filter: Search.SearchFilter }

    type Problem =
        | EncoderMissing
        | EncoderUnavailable
        | IndexUnavailable
        | EmbeddingFailed
        | InvalidQueryVector
        | InvalidIndex
        | StorageFailed

    type Failure =
        | InvalidRequest of string
        | Unavailable of Problem
        | Failed of Problem

    type Coverage =
        { IndexedDocuments: int
          EligibleDocuments: int }

    type Success =
        { Mode: Mode
          Results: Search.SearchResult list
          Coverage: Coverage option }

    let modeName = function
        | Keyword -> "keyword"
        | Semantic -> "semantic"
        | Hybrid -> "hybrid"

    let parseMode = function
        | None | Some "keyword" -> Ok Keyword
        | Some "semantic" -> Ok Semantic
        | Some "hybrid" -> Ok Hybrid
        | Some _ -> Error "mode must be keyword, semantic, or hybrid"

    let problemCode = function
        | EncoderMissing -> "query_encoder_not_configured"
        | EncoderUnavailable -> "query_encoder_unavailable"
        | IndexUnavailable -> "vector_index_unavailable"
        | EmbeddingFailed -> "query_embedding_failed"
        | InvalidQueryVector -> "invalid_query_vector"
        | InvalidIndex -> "invalid_vector_index"
        | StorageFailed -> "search_storage_failed"

    let failureStatus = function
        | InvalidRequest _ -> "invalid_request"
        | Unavailable _ -> "unavailable"
        | Failed _ -> "error"

    let failureMessage = function
        | InvalidRequest message -> message
        | Unavailable problem | Failed problem -> problemCode problem

    let httpStatus = function
        | InvalidRequest _ -> 400
        | Unavailable _ | Failed _ -> 503

    let validVector dimensions (vector: float32 array) =
        dimensions > 0
        && vector.Length = dimensions
        && (vector |> Array.forall Single.IsFinite)
        && (vector |> Array.exists ((<>) 0.0f))

    let validate (request: Request) =
        if request.Filter.Limit < 1 then
            Error(InvalidRequest "limit must be a positive 32-bit integer")
        elif request.Mode <> Keyword && request.Filter.Limit > 100 then
            Error(InvalidRequest "limit must be between 1 and 100")
        elif request.Mode <> Keyword && String.IsNullOrWhiteSpace request.Filter.Query then
            Error(InvalidRequest "query is required for semantic and hybrid search")
        else Ok request
