namespace Hermes.Core

/// Pipeline Stage 3: Post-extraction processing — reminders, embedding.
/// Runs after extraction completes. Each sub-step is independent.
[<RequireQualifiedAccess>]
module PostStage =

    /// Evaluate reminder rules on newly-extracted documents.
    let evaluateReminders (db: Algebra.Database) (logger: Algebra.Logger) (clock: Algebra.Clock) =
        task {
            let! n = Reminders.evaluateNewDocuments db logger (clock.utcNow ())
            if n > 0 then
                logger.info $"Created {n} new reminder(s)"
                do! ActivityLog.logInfo db "reminder" $"Created {n} new reminder(s)" None
            let! u = Reminders.unsnoozeExpired db (clock.utcNow ())
            if u > 0 then
                logger.info $"Un-snoozed {u} reminder(s)"
                do! ActivityLog.logInfo db "reminder" $"Un-snoozed {u} reminder(s)" None
        }

    /// Generate embeddings for documents with extracted text.
    let runEmbedding (db: Algebra.Database) (logger: Algebra.Logger) (clock: Algebra.Clock) (embedder: Algebra.EmbeddingClient option) =
        task {
            match embedder with
            | None -> ()
            | Some client ->
                let! avail = client.isAvailable ()
                if avail then
                    logger.info "Embedding service available — embedding..."
                    let! _ = Embeddings.batchEmbed db logger clock client false (Some 50) None
                    ()
        }

    /// Run all post-processing steps.
    let run (db: Algebra.Database) (logger: Algebra.Logger) (clock: Algebra.Clock) (embedder: Algebra.EmbeddingClient option) =
        task {
            do! evaluateReminders db logger clock
            do! runEmbedding db logger clock embedder
        }
