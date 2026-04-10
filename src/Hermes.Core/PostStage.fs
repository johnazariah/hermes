namespace Hermes.Core

/// Pipeline Stage 3: Post-extraction processing.
/// Runs registered PostProcessor plugins after extraction completes.
[<RequireQualifiedAccess>]
module PostStage =

    /// Reminder post-processor: evaluates due dates on extracted documents.
    let reminderPlugin : Algebra.PostProcessor =
        { Name = "Reminders"
          Process = fun db _fs logger clock _docId ->
            task {
                let! n = Reminders.evaluateNewDocuments db logger (clock.utcNow ())
                if n > 0 then
                    logger.info $"Created {n} new reminder(s)"
                    do! ActivityLog.logInfo db "reminder" $"Created {n} new reminder(s)" None
                let! u = Reminders.unsnoozeExpired db (clock.utcNow ())
                if u > 0 then
                    logger.info $"Un-snoozed {u} reminder(s)"
                    do! ActivityLog.logInfo db "reminder" $"Un-snoozed {u} reminder(s)" None
            } }

    /// Embedding post-processor: generates vectors for semantic search.
    let embeddingPlugin (embedder: Algebra.EmbeddingClient option) : Algebra.PostProcessor =
        { Name = "Embedding"
          Process = fun db _fs logger clock _docId ->
            task {
                match embedder with
                | None -> ()
                | Some client ->
                    let! avail = client.isAvailable ()
                    if avail then
                        use cts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromMinutes(5.0))
                        try
                            let! _ = Embeddings.batchEmbed db logger clock client false (Some 50) None
                            ()
                        with
                        | :? System.OperationCanceledException ->
                            logger.warn "Embedding batch timed out after 5 minutes — will resume next cycle"
                        | ex ->
                            logger.warn $"Embedding batch failed: {ex.Message}"
            } }

    /// LLM extraction enhancer: re-processes raw extracted text into structured markdown.
    let llmExtractPlugin (chatProvider: Algebra.ChatProvider option) : Algebra.PostProcessor =
        { Name = "LLM-Extract"
          Process = fun db _fs logger _clock _docId ->
            task {
                match chatProvider with
                | None -> ()
                | Some chat ->
                    // Find docs with extracted text but no LLM-enhanced markdown
                    let! rows =
                        db.execReader
                            """SELECT id, extracted_text, original_name FROM documents
                               WHERE extracted_text IS NOT NULL
                                 AND (extracted_markdown IS NULL OR extracted_markdown = extracted_text)
                                 AND extraction_method != 'failed'
                               ORDER BY id ASC LIMIT 10"""
                            []
                    let mutable enhanced = 0
                    for row in rows do
                        let r = Prelude.RowReader(row)
                        match r.OptInt64 "id", r.OptString "extracted_text" with
                        | Some docId, Some text when text.Length > 50 ->
                            let truncated = if text.Length > 3000 then text.[..2999] + "\n[...truncated]" else text
                            let prompt = $"""Convert this document text to clean, well-structured markdown.
Include:
- A descriptive title as H1
- Key fields as a metadata table (date, amount, vendor, account, reference numbers)
- The main content with proper headings, lists, and tables where appropriate
- Keep it concise — omit boilerplate and legal disclaimers

Document text:
{truncated}"""
                            try
                                let! result = chat.complete "You are a document formatting assistant. Output only markdown, no explanations." prompt
                                match result with
                                | Ok markdown when markdown.Length > 20 ->
                                    let! _ =
                                        db.execNonQuery
                                            "UPDATE documents SET extracted_markdown = @md WHERE id = @id"
                                            [ ("@md", Database.boxVal markdown); ("@id", Database.boxVal docId) ]
                                    enhanced <- enhanced + 1
                                    logger.debug $"LLM enhanced doc {docId}"
                                | Ok _ -> ()
                                | Error e -> logger.debug $"LLM enhance failed for doc {docId}: {e}"
                            with ex -> logger.debug $"LLM enhance error for doc {docId}: {ex.Message}"
                        | _ -> ()
                    if enhanced > 0 then
                        logger.info $"LLM enhanced {enhanced} document(s) to structured markdown"
            } }

    /// Default post-processors.
    let defaultPlugins (embedder: Algebra.EmbeddingClient option) (chatProvider: Algebra.ChatProvider option) : Algebra.PostProcessor list =
        [ reminderPlugin; llmExtractPlugin chatProvider; embeddingPlugin embedder ]

    /// Run all post-processors. Each runs independently — one failure doesn't block others.
    let run (db: Algebra.Database) (fs: Algebra.FileSystem) (logger: Algebra.Logger) (clock: Algebra.Clock) (plugins: Algebra.PostProcessor list) =
        task {
            for plugin in plugins do
                try
                    do! plugin.Process db fs logger clock 0L
                with ex ->
                    logger.warn $"Post-processor '{plugin.Name}' failed: {ex.Message}"
        }
