namespace Hermes.Core

open System
open System.Threading.Channels

/// Observable pipeline state — read from channels + DB, no polling.
/// The HTTP API and React UI subscribe to this.
[<RequireQualifiedAccess>]
module PipelineObserver =

    type PipelineState =
        { IngestQueueDepth: int
          ExtractQueueDepth: int
          DeadLetterCount: int
          TotalDocuments: int64
          TotalExtracted: int64
          TotalEmbedded: int64
          CurrentDoc: string option
          LastUpdated: DateTimeOffset }

    type T =
        { mutable State: PipelineState
          mutable Subscribers: (PipelineState -> unit) list }

    let empty () =
        { State =
            { IngestQueueDepth = 0; ExtractQueueDepth = 0; DeadLetterCount = 0
              TotalDocuments = 0L; TotalExtracted = 0L; TotalEmbedded = 0L
              CurrentDoc = None; LastUpdated = DateTimeOffset.UtcNow }
          Subscribers = [] }

    let subscribe (observer: T) (handler: PipelineState -> unit) =
        observer.Subscribers <- handler :: observer.Subscribers

    let private notify (observer: T) =
        for handler in observer.Subscribers do
            try handler observer.State with _ -> ()

    /// Refresh state from DB counters. Call periodically or after stage completion.
    let refresh (observer: T) (db: Algebra.Database) (fs: Algebra.FileSystem) (archiveDir: string) =
        task {
            let! stats = Stats.getIndexStats db fs (System.IO.Path.Combine(archiveDir, "db.sqlite"))
            let! counts = Stats.getPipelineCounts db fs archiveDir
            observer.State <-
                { observer.State with
                    IngestQueueDepth = counts.IntakeCount
                    ExtractQueueDepth = counts.ExtractingCount
                    TotalDocuments = stats.DocumentCount
                    TotalExtracted = stats.ExtractedCount
                    TotalEmbedded = stats.EmbeddedCount
                    LastUpdated = DateTimeOffset.UtcNow }
            notify observer
        }

    let setCurrentDoc (observer: T) (doc: string option) =
        observer.State <- { observer.State with CurrentDoc = doc; LastUpdated = DateTimeOffset.UtcNow }
        notify observer

    let setDeadLetterCount (observer: T) (count: int) =
        observer.State <- { observer.State with DeadLetterCount = count; LastUpdated = DateTimeOffset.UtcNow }
        notify observer
