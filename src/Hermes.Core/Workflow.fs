namespace Hermes.Core

#nowarn "3261"

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

/// Generic pipeline workflow — stage definitions, channel wiring, and the runStage monad.
/// Stage processors are pure functions: Document -> Task<Document>.
/// All boilerplate (idempotency, write-aside, error handling) lives here.
[<RequireQualifiedAccess>]
module Workflow =

    /// A stage definition: what it's called, how to tell if it's done,
    /// what it needs, and the pure processing function.
    type StageDefinition =
        { /// Human-readable name for logging.
          Name: string
          /// Key whose presence means this stage is already done.
          OutputKey: string
          /// Keys that must be present (non-null) before this stage can process.
          RequiredKeys: string list
          /// The pure processing function: enrich the document and return it.
          Process: Document.T -> Task<Document.T>
          /// Optional shared resource lock (e.g. Ollama GPU mutex).
          /// When present, the consumer acquires before processing a burst and
          /// holds until the channel drains or MaxHoldTime elapses.
          ResourceLock: SemaphoreSlim option
          /// Maximum time to hold the resource lock before yielding.
          MaxHoldTime: TimeSpan }

    /// Process a single document: idempotency check, process, write-aside, forward.
    /// Returns true if the doc was actually processed (not skipped/passed-through).
    let private processOne
        (stage: StageDefinition)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (output: ChannelWriter<Document.T> option)
        (ct: CancellationToken)
        (doc: Document.T)
        : Task<bool> =
        task {
            let docId = Document.id doc

            // Idempotency: already done?
            if Document.hasKey stage.OutputKey doc then
                logger.debug $"Stage '{stage.Name}' skip doc {docId}: already has '{stage.OutputKey}'"
                match output with
                | Some w -> do! w.WriteAsync(doc, ct)
                | None -> ()
                return false

            // Missing dependencies: pass through
            elif stage.RequiredKeys |> List.exists (fun k -> not (Document.hasKey k doc)) then
                let missing = stage.RequiredKeys |> List.filter (fun k -> not (Document.hasKey k doc))
                logger.debug $"Stage '{stage.Name}' skip doc {docId}: missing keys {missing}"
                match output with
                | Some w -> do! w.WriteAsync(doc, ct)
                | None -> ()
                return false

            else
                // Process
                try
                    let! enriched = stage.Process doc
                    do! Document.persist db enriched
                    logger.debug $"Stage '{stage.Name}' processed doc {docId}"
                    match output with
                    | Some w -> do! w.WriteAsync(enriched, ct)
                    | None -> ()
                    return true
                with ex ->
                    let failed =
                        doc
                        |> Document.encode "stage" (box "failed")
                        |> Document.encode "error" (box ex.Message)
                    do! Document.persist db failed
                    logger.warn $"Stage '{stage.Name}' failed doc {docId}: {ex.Message}"
                    return false
        }

    /// Run a single consumer loop for a stage.
    /// If a ResourceLock is configured, acquires per-burst and holds until
    /// the channel drains or MaxHoldTime elapses, then releases for the other stage.
    let runStage
        (stage: StageDefinition)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (input: ChannelReader<Document.T>)
        (output: ChannelWriter<Document.T> option)
        (ct: CancellationToken)
        : Task<unit> =
        task {
            logger.info $"Stage '{stage.Name}' consumer started"
            try
                while not ct.IsCancellationRequested do
                    let! canRead = input.WaitToReadAsync(ct)
                    if canRead then
                        // Acquire resource lock if configured (start of burst)
                        let hasLock = ref false
                        match stage.ResourceLock with
                        | Some sem ->
                            do! sem.WaitAsync(ct)
                            hasLock.Value <- true
                            logger.debug $"Stage '{stage.Name}' acquired resource lock"
                        | None -> ()

                        try
                            let burstStart = DateTime.UtcNow
                            let mutable item = Unchecked.defaultof<Document.T>

                            // Process docs in a burst until channel empty or hold time exceeded
                            let mutable keepGoing = true
                            while keepGoing && not ct.IsCancellationRequested do
                                if input.TryRead(&item) then
                                    let! _ = processOne stage db logger output ct item
                                    // Check hold time
                                    match stage.ResourceLock with
                                    | Some _ when (DateTime.UtcNow - burstStart) > stage.MaxHoldTime ->
                                        logger.debug $"Stage '{stage.Name}' yielding resource lock after {stage.MaxHoldTime.TotalSeconds:F0}s burst"
                                        keepGoing <- false
                                    | _ -> ()
                                else
                                    keepGoing <- false
                        finally
                            // Release resource lock (end of burst)
                            if hasLock.Value then
                                match stage.ResourceLock with
                                | Some sem ->
                                    sem.Release() |> ignore
                                    logger.debug $"Stage '{stage.Name}' released resource lock"
                                | None -> ()
            with
            | :? OperationCanceledException -> ()
            | ex -> logger.error $"Stage '{stage.Name}' loop error: {ex.Message}"
            logger.info $"Stage '{stage.Name}' consumer stopped"
        }

    /// Create a ChannelWriter that routes documents to different writers based on a selector function.
    let routingWriter (selector: Document.T -> ChannelWriter<Document.T>) : ChannelWriter<Document.T> =
        { new ChannelWriter<Document.T>() with
            override _.TryWrite(item) =
                let target = selector item
                target.TryWrite(item)
            override _.WaitToWriteAsync(ct) =
                ValueTask<bool>(true) }

    /// Launch N consumers for a stage, all reading from the same channel.
    let launchConsumers
        (stage: StageDefinition)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (concurrency: int)
        (input: ChannelReader<Document.T>)
        (output: ChannelWriter<Document.T> option)
        (ct: CancellationToken)
        : Task<unit> list =
        [ for i in 1..concurrency do
            task {
                logger.info $"Stage '{stage.Name}' consumer {i}/{concurrency} starting"
                do! runStage stage db logger input output ct
            } ]

    /// Hydrate a channel from the database (for recovery on startup).
    let hydrate
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (writer: ChannelWriter<Document.T>)
        : Task<int> =
        task {
            let! docs = Document.hydrate db
            for doc in docs do
                do! writer.WriteAsync(doc)
            logger.info $"Hydrated {docs.Length} documents into pipeline"
            return docs.Length
        }
