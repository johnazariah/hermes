namespace Hermes.Core

open System
open System.Threading.Tasks

/// Semantic and hybrid search over embedded documents.
/// Uses in-memory cosine similarity (no sqlite-vec dependency).
[<RequireQualifiedAccess>]
module SemanticSearch =

    // ─── Types ──────────────────────────────────────────────────────

    /// A search result with score and source information.
    type SearchResult =
        { DocumentId: int64
          Score: float
          Title: string
          Category: string
          Snippet: string }

    /// Search mode for the CLI.
    type SearchMode =
        | Keyword
        | Semantic
        | Hybrid

    // ─── Cosine similarity ──────────────────────────────────────────

    type private Moments = { Dot: float; Left: float; Right: float }

    /// Compute cosine similarity between two vectors.
    let cosineSimilarity (a: float32[]) (b: float32[]) : float =
        if a.Length <> b.Length || a.Length = 0 then 0.0
        else
            let moments =
                Array.zip a b
                |> Array.fold (fun state (left, right) ->
                    let x, y = float left, float right
                    { Dot = state.Dot + x * y
                      Left = state.Left + x * x
                      Right = state.Right + y * y })
                    { Dot = 0.0; Left = 0.0; Right = 0.0 }
            let denominator = sqrt moments.Left * sqrt moments.Right
            if denominator < 1e-10 then 0.0 else moments.Dot / denominator

    // ─── FTS5 keyword search ────────────────────────────────────────

    /// Search using FTS5 full-text index. Returns (docId, rank) pairs.
    let keywordSearch (db: Algebra.Database) (query: string) (limit: int) =
        task {
            // Escape FTS5 special characters by quoting each term
            let terms =
                query.Split([| ' '; '\t'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun t -> $"\"{t}\"")
                |> String.concat " "

            if String.IsNullOrWhiteSpace(terms) then
                return []
            else
                // Use GROUP_CONCAT to collect results via execScalar
                let sql =
                    $"""SELECT GROUP_CONCAT(rowid || ':' || rank, '|')
                        FROM (
                            SELECT rowid, rank
                            FROM documents_fts
                            WHERE documents_fts MATCH @q
                            ORDER BY rank
                            LIMIT @lim
                        )"""

                let! result =
                    db.execScalar sql [ ("@q", Database.boxVal terms); ("@lim", Database.boxVal limit) ]

                match result with
                | null -> return []
                | v ->
                    let s = string v

                    if String.IsNullOrEmpty(s) then
                        return []
                    else
                        return
                            s.Split('|')
                            |> Array.choose (fun pair ->
                                let parts = pair.Split(':')

                                if parts.Length >= 2 then
                                    match Int64.TryParse(parts.[0]), Double.TryParse(parts.[1]) with
                                    | (true, docId), (true, rank) -> Some(docId, -rank) // FTS5 rank is negative
                                    | _ -> None
                                else
                                    None)
                            |> Array.toList
        }

    // ─── Semantic search (in-memory cosine) ─────────────────────────

    type VectorEvidence =
        { DocumentId: int64
          ChunkId: int64
          Vector: float32 array }

    type RankedDocument =
        { DocumentId: int64
          ChunkId: int64
          Score: float }

    type RankedEvidence<'snapshot> =
        { Snapshot: 'snapshot
          Hits: RankedDocument list }

    let private rankEvidence queryVector limit (evidence: VectorEvidence list) =
        evidence
        |> List.map (fun chunk ->
            { DocumentId = chunk.DocumentId; ChunkId = chunk.ChunkId
              Score = cosineSimilarity queryVector chunk.Vector })
        |> List.groupBy (fun hit -> hit.DocumentId)
        |> List.map (snd >> List.minBy (fun hit -> -hit.Score, hit.ChunkId))
        |> List.sortBy (fun hit -> -hit.Score, hit.DocumentId)
        |> List.truncate (max 0 limit)

    let private rankLoaded load evidenceOf limit queryVector =
        load ()
        |> Prelude.TaskResult.map (fun snapshot ->
            { Snapshot = snapshot
              Hits = rankEvidence queryVector limit (evidenceOf snapshot) })

    /// Shared low-level semantic engine. Evidence is loaded AFTER encoding, so
    /// slow query encoding cannot retain chunks invalidated while it was running.
    let semanticSearchWithEvidence embed load evidenceOf query limit =
        embed query
        |> Prelude.TaskResult.bind (rankLoaded load evidenceOf limit)

    let private legacyEvidence (row: Map<string, obj>) : VectorEvidence =
        let reader = Prelude.RowReader row
        { DocumentId = reader.Int64 "document_id" 0L
          ChunkId = reader.Int64 "id" 0L
          Vector =
            reader.String "vector_hex" ""
            |> Convert.FromHexString |> Embeddings.blobToEmbedding }

    let private loadLegacyEvidence (db: Algebra.Database) () =
        task {
            let! rows =
                db.execReader
                    """SELECT id, document_id, hex(embedding) AS vector_hex
                       FROM document_chunks WHERE embedding IS NOT NULL ORDER BY id""" []
            return Ok(rows |> List.map legacyEvidence)
        }

    /// Compatibility API; production retrieval supplies current, filtered evidence.
    let semanticSearch db (client: Algebra.EmbeddingClient) query limit =
        task {
            let! result =
                semanticSearchWithEvidence client.embed (loadLegacyEvidence db) id query limit
            return result
                   |> Result.map (fun ranked -> ranked.Hits |> List.map (fun hit -> hit.DocumentId, hit.Score))
                   |> Result.mapError (fun error -> $"Failed to embed query: {error}")
        }

    // ─── Reciprocal rank fusion ─────────────────────────────────────

    /// Merge two ranked lists using reciprocal rank fusion (RRF).
    /// k is the smoothing constant (typically 60).
    let reciprocalRankFusion (k: int) (listA: (int64 * float) list) (listB: (int64 * float) list) : (int64 * float) list =
        let rrfScore (rank: int) = 1.0 / float (k + rank + 1)

        let scoresA =
            listA
            |> List.mapi (fun rank (docId, _) -> (docId, rrfScore rank))

        let scoresB =
            listB
            |> List.mapi (fun rank (docId, _) -> (docId, rrfScore rank))

        let combined =
            scoresA @ scoresB
            |> List.groupBy fst
            |> List.map (fun (docId, entries) ->
                let totalScore = entries |> List.sumBy snd
                (docId, totalScore))
            |> List.sortByDescending snd

        combined

    // ─── Hybrid search ──────────────────────────────────────────────

    /// Combine FTS5 keyword search and semantic search via RRF.
    let hybridSearch
        (db: Algebra.Database)
        (client: Algebra.EmbeddingClient)
        (query: string)
        (limit: int)
        =
        task {
            // Run keyword and semantic search in parallel
            let keywordTask = keywordSearch db query (limit * 2)
            let semanticTask = semanticSearch db client query (limit * 2)

            let! kwResults = keywordTask
            let! semResults = semanticTask

            match semResults with
            | Error _ ->
                // Fall back to keyword-only
                return kwResults |> List.truncate limit
            | Ok semList ->
                let fused = reciprocalRankFusion 60 kwResults semList
                return fused |> List.truncate limit
        }

    // ─── Result enrichment ──────────────────────────────────────────

    /// Load document metadata for search results.
    let enrichResult (db: Algebra.Database) (docId: int64) (score: float) =
        task {
            let! titleResult =
                db.execScalar
                    "SELECT COALESCE(original_name, saved_path) FROM documents WHERE id = @id"
                    [ ("@id", Database.boxVal docId) ]

            let! catResult =
                db.execScalar
                    "SELECT category FROM documents WHERE id = @id"
                    [ ("@id", Database.boxVal docId) ]

            let! snippetResult =
                db.execScalar
                    "SELECT COALESCE(original_name, saved_path) FROM documents WHERE id = @id"
                    [ ("@id", Database.boxVal docId) ]

            let asString (v: obj | null) =
                match v with
                | null -> ""
                | v -> string v

            return
                { DocumentId = docId
                  Score = score
                  Title = asString titleResult
                  Category = asString catResult
                  Snippet = asString snippetResult }
        }

    /// Run a search and return enriched results.
    let search
        (db: Algebra.Database)
        (client: Algebra.EmbeddingClient)
        (logger: Algebra.Logger)
        (mode: SearchMode)
        (query: string)
        (limit: int)
        =
        task {
            let! ranked =
                match mode with
                | Keyword -> keywordSearch db query limit
                | Semantic ->
                    task {
                        let! result = semanticSearch db client query limit

                        match result with
                        | Ok results -> return results
                        | Error e ->
                            logger.error $"Semantic search failed: {e}"
                            return []
                    }
                | Hybrid -> hybridSearch db client query limit

            let results = ResizeArray<SearchResult>()

            for (docId, score) in ranked do
                let! result = enrichResult db docId score
                results.Add(result)

            return results |> Seq.toList
        }
