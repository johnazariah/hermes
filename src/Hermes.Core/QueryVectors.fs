namespace Hermes.Core

open System

/// Read-only projection of current document_chunks; never creates an index.
[<RequireQualifiedAccess>]
module internal QueryVectors =

    type Snapshot =
        { Evidence: SemanticSearch.VectorEvidence list
          Details: Map<int64, Search.SearchResult>
          Coverage: SearchContract.Coverage }

    type private Chunk =
        { Evidence: SemanticSearch.VectorEvidence
          Detail: Search.SearchResult }

    let private checkTable (db: Algebra.Database) found table =
        task {
            let! exists = db.tableExists table
            return found && exists
        }

    let private tablesReady db =
        [ "document_chunks"; "embedding"; "extraction"; "stage_completions" ]
        |> Prelude.foldTask (checkTable db) true

    let private indexReady (db: Algebra.Database) =
        task {
            let! rows =
                db.execReader
                    $"""SELECT EXISTS(
                          SELECT 1 FROM documents d
                          JOIN document_chunks c ON c.document_id = d.id
                          WHERE c.embedding IS NOT NULL AND {KeywordSql.currentVector}
                        ) AS ready""" []
            return rows |> List.exists (fun row -> (Prelude.RowReader row).Int64 "ready" 0L = 1L)
        }

    let private decode dimensions (row: Map<string, obj>) =
        let reader = Prelude.RowReader row
        reader.OptInt64 "chunk_id"
        |> Option.map (fun chunkId ->
            let bytes = reader.String "vector_hex" "" |> Convert.FromHexString
            if bytes.Length % 4 <> 0 then Error(SearchContract.Failed SearchContract.InvalidIndex)
            else
                let vector = Embeddings.blobToEmbedding bytes
                if not (SearchContract.validVector dimensions vector) then
                    Error(SearchContract.Failed SearchContract.InvalidIndex)
                else
                    let evidence: SemanticSearch.VectorEvidence =
                        { DocumentId = reader.Int64 "id" 0L; ChunkId = chunkId; Vector = vector }
                    Ok(Some { Evidence = evidence; Detail = Search.mapRow row }))
        |> Option.defaultValue (Ok None)

    let private append dimensions state row =
        state |> Result.bind (fun chunks ->
            decode dimensions row |> Result.map (fun chunk ->
                chunk |> Option.map (fun value -> value :: chunks) |> Option.defaultValue chunks))

    let private snapshot rows chunks : Snapshot =
        let coverage: SearchContract.Coverage =
            { IndexedDocuments =
                chunks |> List.map (fun chunk -> chunk.Evidence.DocumentId) |> List.distinct |> List.length
              EligibleDocuments =
                rows |> List.map (fun row -> (Prelude.RowReader row).Int64 "id" 0L)
                |> List.distinct |> List.length }
        { Evidence = chunks |> List.map (fun chunk -> chunk.Evidence)
          Details = chunks |> List.map (fun chunk -> chunk.Evidence.ChunkId, chunk.Detail) |> Map.ofList
          Coverage = coverage }

    let private readRows (db: Algebra.Database) filter dimensions =
        task {
            let query = filter |> Search.criteria |> KeywordSql.vectorRows
            let! rows = db.execReader query.Sql query.Parameters
            return rows |> List.fold (append dimensions) (Ok []) |> Result.map (snapshot rows)
        }

    let load (db: Algebra.Database) filter dimensions () =
        task {
            let! tables = tablesReady db
            if not tables then return Error(SearchContract.Unavailable SearchContract.IndexUnavailable)
            else
                let! ready = indexReady db
                if not ready then return Error(SearchContract.Unavailable SearchContract.IndexUnavailable)
                else return! readRows db filter dimensions
        }
