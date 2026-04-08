namespace Hermes.Core

open System.Threading.Channels

/// Pipeline Stage 2: Extract text from documents.
/// Drains the channel for newly-classified docs, then processes DB backlog.
[<RequireQualifiedAccess>]
module ExtractStage =

    /// Process newly-classified docs from channel, then DB backlog.
    let run
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (extractor: Algebra.TextExtractor) (archiveDir: string)
        (extractChannel: ChannelReader<int64>) =
        task {
            // Phase 1: drain channel — newly classified docs from this cycle
            let mutable channelCount = 0
            let mutable item = Unchecked.defaultof<int64>
            while extractChannel.TryRead(&item) do
                let! rows = db.execReader "SELECT saved_path FROM documents WHERE id = @id" [ ("@id", Database.boxVal item) ]
                match rows |> List.tryHead |> Option.bind (fun r -> Prelude.RowReader(r).OptString "saved_path") with
                | Some path ->
                    let! _ = Extraction.processDocument fs db logger clock extractor archiveDir item path false
                    channelCount <- channelCount + 1
                | None -> ()
            if channelCount > 0 then
                logger.info $"Extracted {channelCount} newly-classified document(s) from channel"

            // Phase 2: DB backlog — unextracted docs from previous cycles
            let! _ = Extraction.extractBatch fs db logger clock extractor archiveDir None false 500
            ()
        }
