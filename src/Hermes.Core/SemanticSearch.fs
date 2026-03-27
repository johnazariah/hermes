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

    /// Compute cosine similarity between two vectors.
    let cosineSimilarity (a: float32[]) (b: float32[]) : float =
        if a.Length <> b.Length || a.Length = 0 then
            0.0
        else
            let mutable dot = 0.0f
            let mutable normA = 0.0f
            let mutable normB = 0.0f

            for i in 0 .. a.Length - 1 do
                dot <- dot + a.[i] * b.[i]
                normA <- normA + a.[i] * a.[i]
                normB <- normB + b.[i] * b.[i]

            let denom = sqrt (float normA) * sqrt (float normB)

            if denom < 1e-10 then 0.0
            else float dot / denom

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

    /// Search using embedding similarity. Returns (docId, similarity) pairs.
    let semanticSearch
        (db: Algebra.Database)
        (client: Algebra.EmbeddingClient)
        (query: string)
        (limit: int)
        =
        task {
            let! queryEmbedding = client.embed query

            match queryEmbedding with
            | Error e -> return Error $"Failed to embed query: {e}"
            | Ok qEmb ->
                // Load all chunks that have embeddings
                let! chunkData =
                    db.execScalar
                        """SELECT GROUP_CONCAT(document_id || ':' || id, '|')
                           FROM document_chunks
                           WHERE embedding IS NOT NULL"""
                        []

                match chunkData with
                | null -> return Ok []
                | v ->
                    let s = string v

                    if String.IsNullOrEmpty(s) then
                        return Ok []
                    else
                        let chunkRefs =
                            s.Split('|')
                            |> Array.choose (fun pair ->
                                let parts = pair.Split(':')

                                if parts.Length >= 2 then
                                    match Int64.TryParse(parts.[0]), Int64.TryParse(parts.[1]) with
                                    | (true, docId), (true, chunkId) -> Some(docId, chunkId)
                                    | _ -> None
                                else
                                    None)

                        // Score each chunk
                        let! scores =
                            task {
                                let results = ResizeArray<int64 * float>()

                                for (docId, chunkId) in chunkRefs do
                                    let! embResult =
                                        db.execScalar
                                            "SELECT embedding FROM document_chunks WHERE id = @id"
                                            [ ("@id", Database.boxVal chunkId) ]

                                    match embResult with
                                    | null -> ()
                                    | v ->
                                        let blob = v :?> byte[]
                                        let embedding = Embeddings.blobToEmbedding blob
                                        let sim = cosineSimilarity qEmb embedding
                                        results.Add(docId, sim)

                                return results |> Seq.toList
                            }

                        // Deduplicate: keep best score per document
                        let deduped =
                            scores
                            |> List.groupBy fst
                            |> List.map (fun (docId, hits) ->
                                let bestScore = hits |> List.map snd |> List.max
                                (docId, bestScore))
                            |> List.sortByDescending snd
                            |> List.truncate limit

                        return Ok deduped
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
                    "SELECT SUBSTR(COALESCE(extracted_text, ''), 1, 200) FROM documents WHERE id = @id"
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
                            eprintfn $"Semantic search failed: {e}"
                            return []
                    }
                | Hybrid -> hybridSearch db client query limit

            let! results =
                task {
                    let items = ResizeArray<SearchResult>()

                    for (docId, score) in ranked do
                        let! result = enrichResult db docId score
                        items.Add(result)

                    return items |> Seq.toList
                }

            return results
        }
