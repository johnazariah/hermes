namespace Hermes.Core

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

    let private forward (output: ChannelWriter<Document.T> option) (ct: CancellationToken) (document: Document.T) : Task =
        output
        |> Option.map (fun writer -> writer.WriteAsync(document, ct).AsTask())
        |> Option.defaultValue Task.CompletedTask

    let private failDocument (stage: StageDefinition) (db: Algebra.Database) (logger: Algebra.Logger)
        (document: Document.T) (error: exn) : Task<unit> =
        task {
            let failed =
                document
                |> Document.encode "stage" ("failed" :> obj)
                |> Document.encode "error" (error.Message :> obj)
            do! Document.persist db failed
            logger.warn $"Stage '{stage.Name}' failed doc {Document.id document}: {error.Message}"
        }

    let private enrichDocument (stage: StageDefinition) (db: Algebra.Database) (logger: Algebra.Logger)
        (output: ChannelWriter<Document.T> option) (ct: CancellationToken) (document: Document.T) : Task<unit> =
        task {
            try
                let! enriched = stage.Process document
                do! Document.persist db enriched
                logger.debug $"Stage '{stage.Name}' processed doc {Document.id document}"
                do! forward output ct enriched
            with
            | :? OperationCanceledException as error when ct.IsCancellationRequested -> return raise error
            | error -> return! failDocument stage db logger document error
        }

    /// Process one document, preserving idempotency and dependency pass-through.
    let private processOne (stage: StageDefinition) (db: Algebra.Database) (logger: Algebra.Logger)
        (output: ChannelWriter<Document.T> option) (ct: CancellationToken) (document: Document.T) : Task<unit> =
        task {
            let docId = Document.id document
            let missing = stage.RequiredKeys |> List.filter (fun key -> not (Document.hasKey key document))
            if Document.hasKey stage.OutputKey document then
                logger.debug $"Stage '{stage.Name}' skip doc {docId}: already has '{stage.OutputKey}'"
                do! forward output ct document
            elif not missing.IsEmpty then
                logger.debug $"Stage '{stage.Name}' skip doc {docId}: missing keys {missing}"
                do! forward output ct document
            else
                do! enrichDocument stage db logger output ct document
        }

    /// Observe task failures before Async.AwaitTask can introduce an aggregate wrapper.
    let private awaitTask (work: Task<'T>) : Async<'T> =
        let observed: Task<Result<'T, exn>> =
            task {
                try
                    let! value = work
                    return Ok value
                with error -> return Error error
            }
        async {
            let! outcome = observed |> Async.AwaitTask
            return outcome |> Result.defaultWith raise
        }

    let private waitForResource (ct: CancellationToken) (resource: SemaphoreSlim) : Task<unit> =
        task { do! resource.WaitAsync(ct) }

    let private processDocument (stage: StageDefinition) (db: Algebra.Database) (logger: Algebra.Logger)
        (output: ChannelWriter<Document.T> option) (ct: CancellationToken) (document: Document.T) : Async<unit> =
        async {
            try
                do! processOne stage db logger output ct document |> awaitTask
            with
            | :? OperationCanceledException as error when ct.IsCancellationRequested -> return raise error
            | error -> logger.error $"Stage '{stage.Name}' loop error: {error.Message}"
        }

    let rec private processBurst (stage: StageDefinition) (db: Algebra.Database) (logger: Algebra.Logger)
        (input: ChannelReader<Document.T>) (output: ChannelWriter<Document.T> option)
        (ct: CancellationToken) (started: DateTime) : Async<unit> =
        async {
            if not ct.IsCancellationRequested then
                match input.TryRead() with
                | true, document ->
                    do! processDocument stage db logger output ct document
                    let expired =
                        stage.ResourceLock.IsSome && DateTime.UtcNow - started > stage.MaxHoldTime
                    if expired then
                        logger.debug $"Stage '{stage.Name}' yielding resource lock after {stage.MaxHoldTime.TotalSeconds:F0}s burst"
                    else
                        return! processBurst stage db logger input output ct started
                | false, _ -> ()
        }

    let private runBurst (stage: StageDefinition) (db: Algebra.Database) (logger: Algebra.Logger)
        (input: ChannelReader<Document.T>) (output: ChannelWriter<Document.T> option)
        (ct: CancellationToken) : Async<unit> =
        async {
            do!
                stage.ResourceLock
                |> Option.map (waitForResource ct >> awaitTask)
                |> Option.defaultValue (async.Return ())
            try
                stage.ResourceLock
                |> Option.iter (fun _ -> logger.debug $"Stage '{stage.Name}' acquired resource lock")
                return! processBurst stage db logger input output ct DateTime.UtcNow
            finally
                stage.ResourceLock
                |> Option.iter (fun resource ->
                    resource.Release() |> ignore
                    logger.debug $"Stage '{stage.Name}' released resource lock")
        }

    let rec private consume (stage: StageDefinition) (db: Algebra.Database) (logger: Algebra.Logger)
        (input: ChannelReader<Document.T>) (output: ChannelWriter<Document.T> option)
        (ct: CancellationToken) : Async<unit> =
        async {
            if not ct.IsCancellationRequested then
                let! canRead = input.WaitToReadAsync(ct).AsTask() |> awaitTask
                if canRead then
                    do! runBurst stage db logger input output ct
                    return! consume stage db logger input output ct
        }

    /// Stops when the input is completed and drained, or cancellation is requested.
    /// Resource ownership is per burst; output completion remains the caller's responsibility.
    let runStage (stage: StageDefinition) (db: Algebra.Database) (logger: Algebra.Logger)
        (input: ChannelReader<Document.T>) (output: ChannelWriter<Document.T> option)
        (ct: CancellationToken) : Task<unit> =
        task {
            logger.info $"Stage '{stage.Name}' consumer started"
            try
                try
                    do! Async.StartImmediateAsTask(
                        consume stage db logger input output ct, cancellationToken = CancellationToken.None)
                with
                | :? OperationCanceledException when ct.IsCancellationRequested -> ()
                | error ->
                    logger.error $"Stage '{stage.Name}' loop error: {error.Message}"
                    return raise error
            finally
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
    let launchConsumers (stage: StageDefinition) (db: Algebra.Database) (logger: Algebra.Logger)
        (concurrency: int) (input: ChannelReader<Document.T>) (output: ChannelWriter<Document.T> option)
        (ct: CancellationToken) : Task<unit> list =
        [ for i in 1..concurrency do
            task {
                logger.info $"Stage '{stage.Name}' consumer {i}/{concurrency} starting"
                do! runStage stage db logger input output ct
            } ]

    /// Hydrate a channel from the database (for recovery on startup).
    let hydrate (db: Algebra.Database) (logger: Algebra.Logger)
        (writer: ChannelWriter<Document.T>) : Task<int> =
        task {
            let! docs = Document.hydrate db
            for doc in docs do
                do! writer.WriteAsync(doc)
            logger.info $"Hydrated {docs.Length} documents into pipeline"
            return docs.Length
        }
