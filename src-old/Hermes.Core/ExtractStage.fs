namespace Hermes.Core

open System
open System.Threading.Channels

/// Pipeline Stage 2: Extract text from documents.
/// Drains the channel for newly-classified docs, then processes DB backlog.
/// Permanent failures route to the dead letter channel.
[<RequireQualifiedAccess>]
module ExtractStage =

    /// Extract a single doc, routing failures to dead letter channel.
    let private extractOne
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (extractor: Algebra.TextExtractor) (archiveDir: string)
        (deadLetterCh: ChannelWriter<Domain.DeadLetter>)
        (docId: int64) (path: string) =
        task {
            let! result = Extraction.processDocument fs db logger clock extractor archiveDir docId path false
            match result with
            | Ok _ -> ()
            | Error err ->
                let name = System.IO.Path.GetFileName(path) |> Option.ofObj |> Option.defaultValue "unknown"
                do! deadLetterCh.WriteAsync({
                    DocId = docId; Stage = "extract"; Error = err
                    Retryable = false; FailedAt = clock.utcNow ()
                    RetryCount = 0; OriginalName = name
                })
        }

    /// Process newly-classified docs from channel, then DB backlog.
    /// Permanent failures go to deadLetterCh.
    let run
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (extractor: Algebra.TextExtractor) (archiveDir: string)
        (extractChannel: ChannelReader<int64>) (deadLetterCh: ChannelWriter<Domain.DeadLetter>) =
        task {
            // Phase 1: drain channel — newly classified docs from this cycle
            let mutable channelCount = 0
            let mutable item = Unchecked.defaultof<int64>
            while extractChannel.TryRead(&item) do
                let! rows = db.execReader "SELECT saved_path FROM documents WHERE id = @id" [ ("@id", Database.boxVal item) ]
                match rows |> List.tryHead |> Option.bind (fun r -> Prelude.RowReader(r).OptString "saved_path") with
                | Some path ->
                    do! extractOne fs db logger clock extractor archiveDir deadLetterCh item path
                    channelCount <- channelCount + 1
                | None -> ()
            if channelCount > 0 then
                logger.info $"Extracted {channelCount} newly-classified document(s) from channel"

            // Phase 2: DB backlog — unextracted docs from previous cycles
            let! docs = Extraction.getDocumentsForExtraction db None false 500
            for (docId, path) in docs do
                do! extractOne fs db logger clock extractor archiveDir deadLetterCh docId path
        }
