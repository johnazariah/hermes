namespace Hermes.Core

open System
open System.IO
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

/// Channel-driven pipeline: continuous stage consumers connected by bounded channels.
/// Replaces the polling-loop model in ServiceHost.runSyncCycle.
[<RequireQualifiedAccess>]
module Pipeline =

    /// Pipeline channels connecting the stages.
    type Channels =
        { /// Files to classify (path in unclassified/)
          Ingest: Channel<string>
          /// Doc IDs to extract
          Extract: Channel<int64>
          /// Doc IDs for post-processing (reclassify, enhance, embed)
          Post: Channel<int64>
          /// Permanent failures
          DeadLetter: Channel<Domain.DeadLetter> }

    let createChannels () =
        { Ingest = Channel.CreateBounded<string>(BoundedChannelOptions(1000, FullMode = BoundedChannelFullMode.Wait))
          Extract = Channel.CreateBounded<int64>(BoundedChannelOptions(500, FullMode = BoundedChannelFullMode.Wait))
          Post = Channel.CreateBounded<int64>(BoundedChannelOptions(500, FullMode = BoundedChannelFullMode.Wait))
          DeadLetter = Channel.CreateUnbounded<Domain.DeadLetter>() }

    // ─── Stage 1: Classify ──────────────────────────────────────────

    /// Continuous consumer: reads file paths from ingest channel, classifies, writes docIds to extract channel.
    let classifyConsumer
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (rules: Algebra.RulesEngine) (archiveDir: string)
        (input: ChannelReader<string>) (output: ChannelWriter<int64>)
        (ct: CancellationToken) =
        task {
            try
                while not ct.IsCancellationRequested do
                    let! filePath = input.ReadAsync(ct)
                    try
                        let! result = Classifier.processFile fs db logger clock rules archiveDir filePath
                        match result with
                        | Ok (Some docId) ->
                            do! output.WriteAsync(docId, ct)
                        | _ -> ()
                    with ex ->
                        logger.warn $"Classify failed for {Path.GetFileName(filePath)}: {ex.Message}"
            with :? OperationCanceledException -> ()
            logger.debug "Classify consumer stopped"
        }

    // ─── Stage 2: Extract ───────────────────────────────────────────

    /// Continuous consumer: reads docIds from extract channel, extracts text, writes to post channel.
    let extractConsumer
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (extractor: Algebra.TextExtractor) (archiveDir: string)
        (input: ChannelReader<int64>) (output: ChannelWriter<int64>)
        (deadLetter: ChannelWriter<Domain.DeadLetter>)
        (ct: CancellationToken) =
        task {
            try
                while not ct.IsCancellationRequested do
                    let! docId = input.ReadAsync(ct)
                    try
                        let! rows = db.execReader "SELECT saved_path FROM documents WHERE id = @id" [ ("@id", Database.boxVal docId) ]
                        match rows |> List.tryHead |> Option.bind (fun r -> Prelude.RowReader(r).OptString "saved_path") with
                        | Some path ->
                            let! result = Extraction.processDocument fs db logger clock extractor archiveDir docId path false
                            match result with
                            | Ok _ -> do! output.WriteAsync(docId, ct)
                            | Error err ->
                                let name = Path.GetFileName(path) |> Option.ofObj |> Option.defaultValue "unknown"
                                do! deadLetter.WriteAsync({
                                    DocId = docId; Stage = "extract"; Error = err
                                    Retryable = false; FailedAt = clock.utcNow ()
                                    RetryCount = 0; OriginalName = name }, ct)
                        | None -> ()
                    with ex ->
                        logger.warn $"Extract failed for doc {docId}: {ex.Message}"
            with :? OperationCanceledException -> ()
            logger.debug "Extract consumer stopped"
        }

    // ─── Stage 3: Post-process ──────────────────────────────────────

    /// Continuous consumer: reads docIds from post channel, runs LLM classification + post-processors.
    let postConsumer
        (db: Algebra.Database) (fs: Algebra.FileSystem) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (chatProvider: Algebra.ChatProvider option)
        (contentRules: Domain.ContentRule list) (archiveDir: string)
        (plugins: Algebra.PostProcessor list)
        (input: ChannelReader<int64>)
        (ct: CancellationToken) =
        task {
            let mutable batchCount = 0
            let processBacklog () =
                task {
                    try
                        do! ClassifyStage.reclassifyUnsorted fs db logger chatProvider contentRules archiveDir
                    with ex -> logger.warn $"Reclassify failed: {ex.Message}"
                    try
                        do! PostStage.run db fs logger clock plugins
                    with ex -> logger.warn $"Post-processing failed: {ex.Message}"
                    batchCount <- 0
                }
            try
                while not ct.IsCancellationRequested do
                    let! _docId = input.ReadAsync(ct)
                    batchCount <- batchCount + 1
                    if batchCount >= 50 then
                        do! processBacklog ()
            with :? OperationCanceledException ->
                if batchCount > 0 then
                    do! processBacklog ()
            logger.debug "Post consumer stopped"
        }

    // ─── Producers ──────────────────────────────────────────────────

    /// Email producer: syncs all accounts, writes file paths to ingest channel.
    /// Runs on a timer, pauses between cycles.
    let emailProducer
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (config: Domain.HermesConfig) (configDir: string)
        (createProvider: string -> string -> Task<Algebra.EmailProvider>)
        (syncInterval: TimeSpan) (startupDelay: TimeSpan)
        (ct: CancellationToken) =
        task {
            // Startup delay to avoid slamming Gmail on restart
            try do! Task.Delay(startupDelay, ct) with :? OperationCanceledException -> ()

            while not ct.IsCancellationRequested do
                // Sync all accounts concurrently
                let tasks =
                    config.Accounts
                    |> List.map (fun account ->
                        task {
                            try
                                let! provider = createProvider configDir account.Label
                                let! _ = EmailSync.syncAccount fs db logger clock provider config account.Label
                                ()
                            with ex -> logger.warn $"Email sync failed for {account.Label}: {ex.Message}"
                        } :> Task)
                do! Task.WhenAll(tasks |> List.toArray)

                try do! Task.Delay(syncInterval, ct) with :? OperationCanceledException -> ()
        }

    /// Folder watcher producer: scans watch folders, writes file paths to ingest channel.
    let folderProducer
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (config: Domain.HermesConfig)
        (ingest: ChannelWriter<string>)
        (scanInterval: TimeSpan)
        (ct: CancellationToken) =
        task {
            while not ct.IsCancellationRequested do
                let! _ = FolderWatcher.scanAll fs db logger clock config

                // Push any files in unclassified/ to the ingest channel
                let unclDir = Path.Combine(config.ArchiveDir, "unclassified")
                if fs.directoryExists unclDir then
                    let files =
                        fs.getFiles unclDir "*"
                        |> Array.filter (fun f -> not (f.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)))
                    for file in files do
                        try do! ingest.WriteAsync(file, ct)
                        with :? OperationCanceledException -> ()

                try do! Task.Delay(scanInterval, ct) with :? OperationCanceledException -> ()
        }

    /// Recovery: on startup, push unextracted docs to extract channel.
    let recover
        (db: Algebra.Database) (logger: Algebra.Logger)
        (extractCh: ChannelWriter<int64>) (postCh: ChannelWriter<int64>)
        (ct: CancellationToken) =
        task {
            // Unextracted docs → extract channel
            let! docs = Extraction.getDocumentsForExtraction db None false 10000
            if not docs.IsEmpty then
                logger.info $"Recovery: {docs.Length} unextracted doc(s) queued"
                for (docId, _) in docs do
                    try do! extractCh.WriteAsync(docId, ct)
                    with :? OperationCanceledException -> ()

            // Extracted but unclassified → post channel (for LLM classification)
            let! unclObj =
                db.execScalar
                    "SELECT COUNT(*) FROM documents WHERE (category = 'unsorted' OR category = 'unclassified') AND extracted_text IS NOT NULL"
                    []
            let pending = match unclObj with :? int64 as i -> i | _ -> 0L
            if pending > 0L then
                logger.info $"Recovery: {pending} doc(s) awaiting classification"
        }

    // ─── Orchestrator ───────────────────────────────────────────────

    /// Start the full pipeline: producers + stage consumers + recovery.
    /// Returns a Task that completes when the cancellation token fires.
    let start
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (rules: Algebra.RulesEngine) (deps: ServiceHost.SyncDeps)
        (config: Domain.HermesConfig) (configDir: string)
        (onBusy: unit -> unit) (onIdle: unit -> unit)
        (ct: CancellationToken) : Task<unit> =
        task {
            let ch = createChannels ()
            let archiveDir = config.ArchiveDir
            let syncInterval = TimeSpan.FromMinutes(float config.SyncIntervalMinutes)
            let postProcessors = PostStage.defaultPlugins deps.Embedder deps.ChatProvider

            logger.info "Pipeline starting (channel-driven)"
            onBusy ()

            // Recovery: seed channels from DB state
            do! recover db logger ch.Extract.Writer ch.Post.Writer ct

            // Start all tasks
            let tasks = [|
                // Producers
                emailProducer fs db logger clock config configDir deps.CreateEmailProvider syncInterval (TimeSpan.FromSeconds(30.0)) ct :> Task
                folderProducer fs db logger clock config ch.Ingest.Writer (TimeSpan.FromSeconds(30.0)) ct :> Task

                // Stage consumers
                classifyConsumer fs db logger clock rules archiveDir ch.Ingest.Reader ch.Extract.Writer ct :> Task
                extractConsumer fs db logger clock deps.Extractor archiveDir ch.Extract.Reader ch.Post.Writer ch.DeadLetter.Writer ct :> Task
                postConsumer db fs logger clock deps.ChatProvider deps.ContentRules archiveDir postProcessors ch.Post.Reader ct :> Task
            |]

            // Wait for all to complete (they run until cancelled)
            try
                do! Task.WhenAll(tasks)
            with _ -> ()

            onIdle ()
            logger.info "Pipeline stopped"
        }
