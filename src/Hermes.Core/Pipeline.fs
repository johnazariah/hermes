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

    /// Live pipeline status — updated every tick, readable by API.
    type PipelineStatus =
        { mutable InboxDepth: int
          mutable ReadingDepth: int
          mutable FilingDepth: int
          mutable FailedDepth: int
          mutable TotalReceived: int64
          mutable TotalRead: int64
          mutable TotalMemorised: int64
          mutable EmailsQueued: int
          mutable EmailsProcessed: int }

    let createStatus () =
        { InboxDepth = 0; ReadingDepth = 0; FilingDepth = 0; FailedDepth = 0
          TotalReceived = 0L; TotalRead = 0L; TotalMemorised = 0L
          EmailsQueued = 0; EmailsProcessed = 0 }

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

    // ─── Stage 3a: LLM Classify (per-doc, N concurrent) ────────────

    /// Continuous consumer: reads docIds, classifies one at a time via LLM.
    /// Multiple instances can run concurrently on the same channel.
    let llmClassifyConsumer
        (db: Algebra.Database) (fs: Algebra.FileSystem) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (chatProvider: Algebra.ChatProvider)
        (contentRules: Domain.ContentRule list) (archiveDir: string)
        (input: ChannelReader<int64>)
        (consumerId: int)
        (ct: CancellationToken) =
        task {
            try
                while not ct.IsCancellationRequested do
                    let! docId = input.ReadAsync(ct)
                    try
                        // Check if already classified
                        let! rows = db.execReader "SELECT category, extracted_text FROM documents WHERE id = @id" [ ("@id", Database.boxVal docId) ]
                        match rows |> List.tryHead with
                        | Some row ->
                            let r = Prelude.RowReader(row)
                            let cat = r.String "category" ""
                            let text = r.OptString "extracted_text"
                            if (cat = "unsorted" || cat = "unclassified") && text.IsSome then
                                // Try content rules first (fast, no LLM)
                                let! classified =
                                    match ContentClassifier.classify text.Value [] None contentRules with
                                    | Some (newCat, conf) ->
                                        task {
                                            let! moveResult = DocumentManagement.reclassify db fs archiveDir docId newCat
                                            match moveResult with
                                            | Ok () ->
                                                let! _ = db.execNonQuery "UPDATE documents SET classification_tier = 'content', classification_confidence = @conf WHERE id = @id" [ ("@conf", Database.boxVal conf); ("@id", Database.boxVal docId) ]
                                                return true
                                            | Error _ -> return false
                                        }
                                    | None -> Task.FromResult false

                                // If content rules didn't match, try LLM
                                if not classified then
                                    let! catRows = db.execReader "SELECT DISTINCT category FROM documents WHERE category NOT IN ('unsorted','unclassified') LIMIT 50" []
                                    let categories =
                                        catRows |> List.choose (fun r2 -> Prelude.RowReader(r2).OptString "category")
                                    let seedCats = [ "invoices"; "bank-statements"; "receipts"; "tax"; "payslips"; "insurance"; "real-estate"; "travel"; "medical"; "utilities"; "legal"; "donations"; "contracts"; "correspondence" ]
                                    let allCats = (categories @ seedCats) |> List.distinct
                                    let prompt = ContentClassifier.buildClassificationPrompt text.Value allCats
                                    let! llmResult = chatProvider.complete "You are a document classifier." prompt
                                    match llmResult with
                                    | Ok response ->
                                        match ContentClassifier.parseClassificationResponse response with
                                        | Some (newCat, conf, reasoning) when conf >= 0.4 ->
                                            let! moveResult = DocumentManagement.reclassify db fs archiveDir docId newCat
                                            match moveResult with
                                            | Ok () ->
                                                let tier = if conf >= 0.7 then "llm" else "llm_review"
                                                let! _ = db.execNonQuery "UPDATE documents SET classification_tier = @tier, classification_confidence = @conf WHERE id = @id" [ ("@tier", Database.boxVal tier); ("@conf", Database.boxVal conf); ("@id", Database.boxVal docId) ]
                                                logger.info $"[LLM-{consumerId}] Classified doc {docId} as {newCat} (conf={conf:F2}): {reasoning}"
                                            | Error e -> logger.warn $"[LLM-{consumerId}] Move failed for doc {docId}: {e}"
                                        | _ -> ()
                                    | Error e -> logger.warn $"[LLM-{consumerId}] Classification failed for doc {docId}: {e}"
                        | None -> ()
                    with ex ->
                        logger.warn $"[LLM-{consumerId}] Error classifying doc {docId}: {ex.Message}"
            with :? OperationCanceledException -> ()
            logger.debug $"LLM classify consumer {consumerId} stopped"
        }

    // ─── Stage 3b: Post-process (periodic, idle-aware) ──────────────

    /// Periodic post-processor: runs reminders, embedding when pipeline is idle.
    /// Also logs channel depths as a pipeline dashboard.
    let postProcessRunner
        (db: Algebra.Database) (fs: Algebra.FileSystem) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (plugins: Algebra.PostProcessor list)
        (status: PipelineStatus)
        (ingestChannel: ChannelReader<string>)
        (extractChannel: ChannelReader<int64>) (postChannel: ChannelReader<int64>)
        (deadLetterChannel: ChannelReader<Domain.DeadLetter>)
        (interval: TimeSpan)
        (ct: CancellationToken) =
        task {
            try
                while not ct.IsCancellationRequested do
                    try do! Task.Delay(interval, ct) with :? OperationCanceledException -> ()
                    if ct.IsCancellationRequested then () else

                    let ingestDepth = ingestChannel.Count
                    let extractDepth = extractChannel.Count
                    let postDepth = postChannel.Count
                    let deadDepth = deadLetterChannel.Count

                    // Pipeline dashboard
                    let! docCount = db.execScalar "SELECT COUNT(*) FROM documents" []
                    let! extractedCount = db.execScalar "SELECT COUNT(*) FROM documents WHERE extracted_at IS NOT NULL" []
                    let! embeddedCount = db.execScalar "SELECT COUNT(*) FROM documents WHERE embedded_at IS NOT NULL" []
                    let dc = match docCount with null -> 0L | v -> v :?> int64
                    let ec = match extractedCount with null -> 0L | v -> v :?> int64
                    let bc = match embeddedCount with null -> 0L | v -> v :?> int64

                    // Update shared status for API
                    status.InboxDepth <- ingestDepth
                    status.ReadingDepth <- extractDepth
                    status.FilingDepth <- postDepth
                    status.FailedDepth <- deadDepth
                    status.TotalReceived <- dc
                    status.TotalRead <- ec
                    status.TotalMemorised <- bc

                    logger.info $"⚡ inbox:{ingestDepth} → reading:{extractDepth} → filing:{postDepth} | {dc} received, {ec} read, {bc} memorised, {deadDepth} failed"

                    if extractDepth > 0 || postDepth > 0 then
                        logger.debug $"Post-process deferred — extract:{extractDepth} classify:{postDepth} pending"
                    else
                        try
                            do! PostStage.run db fs logger clock plugins
                        with ex -> logger.warn $"Post-processing failed: {ex.Message}"
            with :? OperationCanceledException -> ()
            logger.debug "Post-process runner stopped"
        }

    // ─── Producers ──────────────────────────────────────────────────

    /// Email producer: syncs all accounts using channel-based enumeration.
    /// Pushes downloaded file paths directly to the ingest channel.
    /// Runs on a timer, pauses between cycles.
    let emailProducer
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (config: Domain.HermesConfig) (configDir: string)
        (createProvider: string -> string -> Task<Algebra.EmailProvider>)
        (ingest: ChannelWriter<string>)
        (status: PipelineStatus)
        (concurrency: int)
        (syncInterval: TimeSpan) (startupDelay: TimeSpan)
        (ct: CancellationToken) =
        task {
            // Startup delay to avoid slamming Gmail on restart
            try do! Task.Delay(startupDelay, ct) with :? OperationCanceledException -> ()

            while not ct.IsCancellationRequested do
                let mutable totalQueued = 0
                let mutable totalProcessed = 0

                let syncOneAccount (account: Domain.AccountConfig) : Task =
                    task {
                        try
                            let! provider = createProvider configDir account.Label
                            let! result = EmailSync.syncAccountChanneled fs db logger clock provider config account.Label (Some ingest) concurrency ct
                            System.Threading.Interlocked.Add(&totalQueued, result.DuplicatesSkipped + result.MessagesProcessed) |> ignore
                            System.Threading.Interlocked.Add(&totalProcessed, result.MessagesProcessed) |> ignore
                        with ex -> logger.warn $"Email sync failed for {account.Label}: {ex.Message}"
                    }

                do! config.Accounts |> List.map syncOneAccount |> List.toArray |> Task.WhenAll
                status.EmailsQueued <- totalQueued
                status.EmailsProcessed <- totalProcessed

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

    /// Recovery: on startup, query DB for all incomplete work and seed channels.
    /// The DB is the source of truth — channels are just acceleration.
    let recover
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (archiveDir: string)
        (ingestCh: ChannelWriter<string>) (extractCh: ChannelWriter<int64>) (postCh: ChannelWriter<int64>)
        (ct: CancellationToken) =
        task {
            logger.info "Recovery: checking for incomplete work..."

            // 1. Files in unclassified/ → ingest channel
            let unclDir = Path.Combine(archiveDir, "unclassified")
            if fs.directoryExists unclDir then
                let files =
                    fs.getFiles unclDir "*"
                    |> Array.filter (fun f -> not (f.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)))
                if files.Length > 0 then
                    logger.info $"Recovery: {files.Length} file(s) in unclassified/ → classify"
                    for file in files do
                        try do! ingestCh.WriteAsync(file, ct)
                        with :? OperationCanceledException -> ()

            // 2. Unextracted docs → extract channel
            let! docs = Extraction.getDocumentsForExtraction db None false 10000
            if not docs.IsEmpty then
                logger.info $"Recovery: {docs.Length} unextracted doc(s) → extract"
                for (docId, _) in docs do
                    try do! extractCh.WriteAsync(docId, ct)
                    with :? OperationCanceledException -> ()

            // 3. Extracted but unclassified → post channel
            let! unclRows =
                db.execReader
                    """SELECT id FROM documents
                       WHERE (category = 'unsorted' OR category = 'unclassified')
                         AND extracted_text IS NOT NULL
                       ORDER BY id ASC LIMIT 10000"""
                    []
            let unclDocIds = unclRows |> List.choose (fun r -> Prelude.RowReader(r).OptInt64 "id")
            if not unclDocIds.IsEmpty then
                logger.info $"Recovery: {unclDocIds.Length} extracted doc(s) awaiting classification → post"
                for docId in unclDocIds do
                    try do! postCh.WriteAsync(docId, ct)
                    with :? OperationCanceledException -> ()

            logger.info "Recovery complete"
        }

    // ─── Orchestrator ───────────────────────────────────────────────

    /// Start the full pipeline: producers + stage consumers + recovery.
    /// Extraction scales to half the CPU cores. GPU stages are single-consumer.
    let start
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (rules: Algebra.RulesEngine) (deps: ServiceHost.SyncDeps)
        (config: Domain.HermesConfig) (configDir: string)
        (onBusy: unit -> unit) (onIdle: unit -> unit)
        (status: PipelineStatus)
        (ct: CancellationToken) : Task<unit> =
        task {
            let ch = createChannels ()
            let archiveDir = config.ArchiveDir
            let syncInterval = TimeSpan.FromMinutes(float config.SyncIntervalMinutes)
            let postProcessors = PostStage.defaultPlugins deps.Embedder deps.ChatProvider

            // Scale extraction to available CPU cores
            let extractConcurrency =
                if config.Pipeline.ExtractConcurrency > 0 then config.Pipeline.ExtractConcurrency
                else max 1 (Environment.ProcessorCount / 2)
            let llmConcurrency = max 1 config.Pipeline.LlmConcurrency
            logger.info $"Pipeline starting: {extractConcurrency} extract, {llmConcurrency} LLM classify"
            onBusy ()

            // Build task list — start consumers BEFORE recovery so channels can drain
            let tasks = ResizeArray<Task>()

            // Producers — email producer now pushes file paths directly to ingest channel
            let emailConcurrency = max 1 (config.Pipeline.LlmConcurrency) // reuse LLM concurrency as a proxy for network-bound work
            tasks.Add(emailProducer fs db logger clock config configDir deps.CreateEmailProvider ch.Ingest.Writer status emailConcurrency syncInterval (TimeSpan.FromSeconds(30.0)) ct)
            tasks.Add(folderProducer fs db logger clock config ch.Ingest.Writer (TimeSpan.FromSeconds(30.0)) ct)

            // Stage 1: Classify (single consumer — rules only, fast)
            tasks.Add(classifyConsumer fs db logger clock rules archiveDir ch.Ingest.Reader ch.Extract.Writer ct)

            // Stage 2: Extract (N concurrent consumers — CPU-bound)
            for i in 1..extractConcurrency do
                tasks.Add(extractConsumer fs db logger clock deps.Extractor archiveDir ch.Extract.Reader ch.Post.Writer ch.DeadLetter.Writer ct)

            // Stage 3a: LLM Classify (M concurrent consumers — network or GPU-bound)
            match deps.ChatProvider with
            | Some chat ->
                for i in 1..llmConcurrency do
                    tasks.Add(llmClassifyConsumer db fs logger clock chat deps.ContentRules archiveDir ch.Post.Reader i ct)
            | None ->
                logger.warn "No chat provider — LLM classification disabled"

            // Stage 3b: Post-process (periodic — reminders, embedding, deferred until idle)
            tasks.Add(postProcessRunner db fs logger clock postProcessors status ch.Ingest.Reader ch.Extract.Reader ch.Post.Reader ch.DeadLetter.Reader (TimeSpan.FromSeconds(30.0)) ct)

            // Recovery: seed channels from DB state (consumers are already running to drain)
            do! recover fs db logger archiveDir ch.Ingest.Writer ch.Extract.Writer ch.Post.Writer ct

            // Wait for all to complete (they run until cancelled)
            try
                do! Task.WhenAll(tasks.ToArray())
            with _ -> ()

            onIdle ()
            logger.info "Pipeline stopped"
        }
