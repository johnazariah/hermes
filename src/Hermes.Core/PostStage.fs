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
                        let! _ = Embeddings.batchEmbed db logger clock client false (Some 50) None
                        ()
            } }

    /// Default post-processors.
    let defaultPlugins (embedder: Algebra.EmbeddingClient option) : Algebra.PostProcessor list =
        [ reminderPlugin; embeddingPlugin embedder ]

    /// Run all post-processors. Each runs independently — one failure doesn't block others.
    let run (db: Algebra.Database) (fs: Algebra.FileSystem) (logger: Algebra.Logger) (clock: Algebra.Clock) (plugins: Algebra.PostProcessor list) =
        task {
            for plugin in plugins do
                try
                    do! plugin.Process db fs logger clock 0L
                with ex ->
                    logger.warn $"Post-processor '{plugin.Name}' failed: {ex.Message}"
        }
