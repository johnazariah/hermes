namespace Hermes.Core

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks

/// Text chunking, embedding generation, and storage.
/// Parameterised over Database, Logger, and EmbeddingClient algebras.
[<RequireQualifiedAccess>]
module Embeddings =

    // ─── Text chunking ──────────────────────────────────────────────

    /// A chunk of text with its position in the original document.
    type TextChunk =
        { Text: string
          Index: int
          StartChar: int }

    let private sentenceEndings = [| ". "; "! "; "? "; ".\n"; "!\n"; "?\n" |]

    /// Find the best split point near the target position.
    let private findSplitPoint (text: string) (target: int) (windowStart: int) : int =
        // Look backwards from target for a sentence boundary
        let searchStart = max windowStart (target - 100)
        let mutable bestSentence = -1

        for i in target .. -1 .. searchStart do
            if bestSentence < 0 then
                for ending in sentenceEndings do
                    if i + ending.Length <= text.Length then
                        let sub = text.Substring(i, min ending.Length (text.Length - i))

                        if sub = ending then
                            bestSentence <- i + ending.Length

        if bestSentence > windowStart then
            bestSentence
        else
            // Fall back to word boundary
            let mutable bestWord = -1

            for i in target .. -1 .. searchStart do
                if bestWord < 0 && i < text.Length && Char.IsWhiteSpace(text.[i]) then
                    bestWord <- i + 1

            if bestWord > windowStart then
                bestWord
            else
                target

    /// Split text into overlapping chunks of approximately the target size.
    let chunkText (chunkSize: int) (overlap: int) (text: string) : TextChunk list =
        if String.IsNullOrWhiteSpace(text) then
            []
        else
            let text = text.Trim()

            if text.Length <= chunkSize then
                [ { Text = text
                    Index = 0
                    StartChar = 0 } ]
            else
                let chunks = ResizeArray<TextChunk>()
                let mutable pos = 0
                let mutable idx = 0

                while pos < text.Length do
                    let remaining = text.Length - pos

                    if remaining <= chunkSize then
                        chunks.Add(
                            { Text = text.Substring(pos)
                              Index = idx
                              StartChar = pos }
                        )

                        pos <- text.Length
                    else
                        let splitAt = findSplitPoint text (pos + chunkSize) pos

                        let endPos =
                            if splitAt <= pos then pos + chunkSize
                            else splitAt

                        let endPos = min endPos text.Length

                        chunks.Add(
                            { Text = text.Substring(pos, endPos - pos)
                              Index = idx
                              StartChar = pos }
                        )

                        let nextStart = endPos - overlap
                        pos <- if nextStart <= pos then endPos else nextStart

                    idx <- idx + 1

                chunks |> Seq.toList

    // ─── Embedding storage schema ───────────────────────────────────

    let private chunkTableSql =
        """CREATE TABLE IF NOT EXISTS document_chunks (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            document_id INTEGER NOT NULL,
            chunk_index INTEGER NOT NULL,
            chunk_text  TEXT NOT NULL,
            embedding   BLOB,
            embedded_at TEXT,
            FOREIGN KEY (document_id) REFERENCES documents(id),
            UNIQUE (document_id, chunk_index)
        )"""

    let private chunkIndexSql =
        "CREATE INDEX IF NOT EXISTS idx_chunks_doc ON document_chunks(document_id)"

    /// Ensure the embedding tables exist.
    let initSchema (db: Algebra.Database) =
        task {
            let! _ = db.execNonQuery chunkTableSql []
            let! _ = db.execNonQuery chunkIndexSql []
            return ()
        }

    // ─── Embedding serialisation ────────────────────────────────────

    /// Serialise a float32 array to a byte blob for storage.
    let embeddingToBlob (embedding: float32[]) : byte[] =
        let bytes = Array.zeroCreate<byte> (embedding.Length * 4)
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length)
        bytes

    /// Deserialise a byte blob back to a float32 array.
    let blobToEmbedding (blob: byte[]) : float32[] =
        let floats = Array.zeroCreate<float32> (blob.Length / 4)
        Buffer.BlockCopy(blob, 0, floats, 0, blob.Length)
        floats

    // ─── Store / load chunks ────────────────────────────────────────

    /// Store a chunk with its embedding in the database.
    let storeChunk
        (db: Algebra.Database)
        (clock: Algebra.Clock)
        (docId: int64)
        (chunkIndex: int)
        (chunkText: string)
        (embedding: float32[] option)
        =
        task {
            let embeddingBlob =
                match embedding with
                | Some e -> Database.boxVal (embeddingToBlob e)
                | None -> Database.boxVal DBNull.Value

            let! _ =
                db.execNonQuery
                    """INSERT OR REPLACE INTO document_chunks (document_id, chunk_index, chunk_text, embedding, embedded_at)
                       VALUES (@docId, @idx, @text, @emb, @at)"""
                    [ ("@docId", Database.boxVal docId)
                      ("@idx", Database.boxVal chunkIndex)
                      ("@text", Database.boxVal chunkText)
                      ("@emb", embeddingBlob)
                      ("@at",
                       (if embedding.IsSome then
                            Database.boxVal ((clock.utcNow ()).ToString("o"))
                        else
                            Database.boxVal DBNull.Value)) ]

            return ()
        }

    // ─── Ollama embedding client ────────────────────────────────────

    /// Create an EmbeddingClient that calls the Ollama REST API.
    let ollamaClient (client: HttpClient) (baseUrl: string) (model: string) (dims: int) : Algebra.EmbeddingClient =
        client.BaseAddress <- Uri(baseUrl)
        client.Timeout <- TimeSpan.FromSeconds(120.0)

        { embed =
            fun text ->
                task {
                    try
                        let payload =
                            JsonSerializer.Serialize({| model = model; input = text |})

                        let content = new StringContent(payload, Encoding.UTF8, "application/json")
                        let! response = client.PostAsync("/api/embed", content)

                        if not response.IsSuccessStatusCode then
                            let! body = response.Content.ReadAsStringAsync()
                            return Error $"Ollama API error {int response.StatusCode}: {body}"
                        else
                            let! json = response.Content.ReadAsStringAsync()

                            use doc = JsonDocument.Parse(json)
                            let root = doc.RootElement

                            match root.TryGetProperty("embeddings") with
                            | true, embeddings when embeddings.GetArrayLength() > 0 ->
                                let first = embeddings.[0]
                                let arr = Array.zeroCreate<float32> (first.GetArrayLength())

                                for i in 0 .. arr.Length - 1 do
                                    arr.[i] <- first.[i].GetSingle()

                                return Ok arr
                            | _ -> return Error "No embeddings in Ollama response"
                    with ex ->
                        return Error $"Ollama request failed: {ex.Message}"
                }
          dimensions = dims
          isAvailable =
            fun () ->
                task {
                    try
                        let! response = client.GetAsync("/")
                        return response.IsSuccessStatusCode
                    with _ ->
                        return false
                } }

    // ─── Batch embedding ────────────────────────────────────────────

    /// Progress callback: (completed, total)
    type ProgressCallback = int -> int -> unit

    /// Embed a single document: chunk text, embed each chunk, store results.
    let private embedChunk (db: Algebra.Database) (logger: Algebra.Logger) (clock: Algebra.Clock) (client: Algebra.EmbeddingClient) (docId: int64) (errors: int) (chunk: TextChunk) =
        task {
            let! result = client.embed chunk.Text
            match result with
            | Ok embedding ->
                do! storeChunk db clock docId chunk.Index chunk.Text (Some embedding)
                return errors
            | Error e ->
                logger.warn $"Document {docId}, chunk {chunk.Index}: embedding failed: {e}"
                do! storeChunk db clock docId chunk.Index chunk.Text None
                return errors + 1
        }

    let embedDocument
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (clock: Algebra.Clock)
        (client: Algebra.EmbeddingClient)
        (docId: int64)
        (text: string)
        =
        task {
            let chunks = chunkText 500 100 text
            if chunks.IsEmpty then
                logger.debug $"Document {docId}: no text to embed"
                return Ok 0
            else
            let! errors = Prelude.foldTask (embedChunk db logger clock client docId) 0 chunks

            let! _ =
                db.execNonQuery
                    "UPDATE documents SET embedded_at = @at, chunk_count = @cnt WHERE id = @id"
                    [ ("@at", Database.boxVal ((clock.utcNow ()).ToString("o")))
                      ("@cnt", Database.boxVal chunks.Length)
                      ("@id", Database.boxVal docId) ]

            if errors > 0 then return Error $"{errors} of {chunks.Length} chunks failed"
            else
                logger.debug $"Document {docId}: embedded {chunks.Length} chunks"
                return Ok chunks.Length
        }

    /// Batch-embed documents that have extracted text but no embeddings.
    type private EmbedAccum = { Completed: int; Failures: int }

    let private embedOne db logger clock client progress total (accum: EmbedAccum) (docId: int64, text: string) =
        task {
            let! result = embedDocument db logger clock client docId text
            let accum =
                match result with
                | Ok _ -> { accum with Completed = accum.Completed + 1 }
                | Error _ -> { Completed = accum.Completed + 1; Failures = accum.Failures + 1 }
            progress |> Option.iter (fun cb -> cb accum.Completed total)
            return accum
        }

    let batchEmbed
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (clock: Algebra.Clock)
        (client: Algebra.EmbeddingClient)
        (force: bool)
        (limit: int option)
        (progress: ProgressCallback option)
        =
        task {
            do! initSchema db

            let! available = client.isAvailable ()
            if not available then
                logger.error "Embedding service is not available"
                return Error "Embedding service unavailable"
            else

            let where =
                if force then "extracted_text IS NOT NULL AND extracted_text != ''"
                else "extracted_text IS NOT NULL AND extracted_text != '' AND embedded_at IS NULL"

            let limitClause = limit |> Option.map (sprintf " LIMIT %d") |> Option.defaultValue ""
            let sql = $"SELECT id, extracted_text FROM documents WHERE {where} ORDER BY id{limitClause}"

            let! rows = db.execReader sql []

            let docs =
                rows
                |> List.choose (fun row ->
                    match row |> Map.tryFind "id", row |> Map.tryFind "extracted_text" with
                    | Some (:? int64 as id), Some (:? string as text) when not (String.IsNullOrEmpty(text)) ->
                        Some (id, text)
                    | _ -> None)

            if docs.IsEmpty then
                logger.info "No documents to embed"
                return Ok 0
            else

            logger.info $"Embedding {docs.Length} documents..."

            let! accum =
                docs
                |> List.fold
                    (fun stateTask doc ->
                        task {
                            let! state = stateTask
                            return! embedOne db logger clock client progress docs.Length state doc
                        })
                    (task { return { Completed = 0; Failures = 0 } })

            logger.info $"Embedding complete: {accum.Completed} processed, {accum.Failures} with errors"
            return Ok accum.Completed
        }
