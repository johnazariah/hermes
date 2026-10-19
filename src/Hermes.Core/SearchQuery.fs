namespace Hermes.Core

open System

/// Production query orchestration. The legacy hybrid fallback is not used.
[<RequireQualifiedAccess>]
module SearchQuery =

    let private encode (client: Algebra.EmbeddingClient) query =
        task {
            try
                let! available = client.isAvailable ()
                if not available then
                    return Error(SearchContract.Unavailable SearchContract.EncoderUnavailable)
                else
                    let! result = client.embed query
                    return result
                           |> Result.mapError (fun _ -> SearchContract.Failed SearchContract.EmbeddingFailed)
                           |> Result.bind (fun vector ->
                               if SearchContract.validVector client.dimensions vector then Ok vector
                               else Error(SearchContract.Failed SearchContract.InvalidQueryVector))
            with _ -> return Error(SearchContract.Failed SearchContract.EmbeddingFailed)
        }

    let private detail (snapshot: QueryVectors.Snapshot) (hit: SemanticSearch.RankedDocument) =
        { snapshot.Details.[hit.ChunkId] with RelevanceScore = hit.Score }

    let private semantic db client filter =
        SemanticSearch.semanticSearchWithEvidence
            (encode client)
            (QueryVectors.load db filter client.dimensions)
            (fun (snapshot: QueryVectors.Snapshot) -> snapshot.Evidence)
            filter.Query Int32.MaxValue

    let private pairs (hits: Search.SearchResult list) =
        hits |> List.map (fun hit -> hit.DocumentId, hit.RelevanceScore)

    let private fuse limit (keyword: Search.SearchResult list) (vectors: Search.SearchResult list) =
        let details =
            vectors @ keyword
            |> List.map (fun hit -> hit.DocumentId, hit)
            |> Map.ofList
        SemanticSearch.reciprocalRankFusion 60 (pairs keyword) (pairs vectors)
        |> List.sortBy (fun (id, score) -> -score, id)
        |> List.truncate limit
        |> List.map (fun (id, score) -> { details.[id] with RelevanceScore = score })

    let private hybrid db (request: SearchContract.Request) vectors =
        task {
            let! keyword = Search.executeUnified db { request.Filter with Limit = Int32.MaxValue }
            return fuse request.Filter.Limit keyword vectors
        }

    let private complete db (request: SearchContract.Request)
        (ranked: SemanticSearch.RankedEvidence<QueryVectors.Snapshot>) =
        task {
            let vectors = ranked.Hits |> List.map (detail ranked.Snapshot)
            let! results =
                if request.Mode = SearchContract.Semantic then
                    System.Threading.Tasks.Task.FromResult(List.truncate request.Filter.Limit vectors)
                else hybrid db request vectors
            let success: SearchContract.Success =
                { Mode = request.Mode; Results = results; Coverage = Some ranked.Snapshot.Coverage }
            return Ok success
        }

    let private requestedVectors db encoder (request: SearchContract.Request) =
        encoder
        |> Option.map (fun client ->
            semantic db client request.Filter
            |> Prelude.TaskResult.bind (complete db request))
        |> Option.defaultWith (fun () ->
            System.Threading.Tasks.Task.FromResult(
                Error(SearchContract.Unavailable SearchContract.EncoderMissing)))

    let private run db encoder (request: SearchContract.Request) =
        task {
            match request.Mode with
            | SearchContract.Keyword ->
                let! hits = Search.executeUnified db request.Filter
                let success: SearchContract.Success =
                    { Mode = request.Mode; Results = hits; Coverage = None }
                return Ok success
            | SearchContract.Semantic | SearchContract.Hybrid ->
                return! requestedVectors db encoder request
        }

    let execute db encoder request =
        task {
            try
                return!
                    SearchContract.validate request
                    |> Result.map (run db encoder)
                    |> Result.defaultWith (Error >> System.Threading.Tasks.Task.FromResult)
            with _ -> return Error(SearchContract.Failed SearchContract.StorageFailed)
        }
