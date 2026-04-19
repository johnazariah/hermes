namespace Hermes.Core

#nowarn "3261"

open System
open System.IO
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

/// Pipeline orchestrator: channels, workflow monad, producers, dashboard.
/// Stage processors are pure functions wired via Workflow.runStage.
[<RequireQualifiedAccess>]
module Pipeline =

    /// Dependencies injected from the composition root.
    type Deps =
        { Extractor: Algebra.TextExtractor
          Embedder: Algebra.EmbeddingClient option
          ChatProvider: Algebra.ChatProvider option
          TriageProvider: Algebra.ChatProvider option
          ContentRules: Domain.ContentRule list
          ComprehensionPrompt: PromptLoader.ParsedPrompt option
          CreateEmailProvider: string -> string -> Task<Algebra.EmailProvider> }

    /// The channels connecting pipeline stages.
    type Channels =
        { Extract: Channel<Document.T>
          Understand: Channel<Document.T>
          Embed: Channel<Document.T> }

    /// Create unbounded channels for the pipeline.
    let createChannels () : Channels =
        { Extract = Channel.CreateUnbounded<Document.T>()
          Understand = Channel.CreateUnbounded<Document.T>()
          Embed = Channel.CreateUnbounded<Document.T>() }

    /// Insert a new document into the DB and write it to the extract channel.
    /// Used by all producers (email sync, folder watcher).
    let ingest
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (extractWriter: ChannelWriter<Document.T>)
        (doc: Document.T)
        : Task<int64> =
        task {
            // The producer has already INSERTed the row; read it back as a Document
            let docId = Document.id doc
            let! rows = db.execReader "SELECT * FROM documents WHERE id = @id" [ ("@id", Database.boxVal docId) ]
            match rows |> List.tryHead with
            | Some row ->
                let fullDoc = Document.fromRow row
                do! extractWriter.WriteAsync(fullDoc)
                logger.debug $"Ingested doc {docId} into pipeline"
                return docId
            | None ->
                logger.warn $"Ingest: doc {docId} not found after INSERT"
                return docId
        }

    // ─── Folder watcher producer ─────────────────────────────────

    /// Scan watch folders, process new files in unclassified/, ingest into pipeline.
    let private folderWatcherLoop
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (config: Domain.HermesConfig)
        (extractWriter: ChannelWriter<Document.T>)
        (ct: CancellationToken) : Task<unit> =
        task {
            let archiveDir = config.ArchiveDir
            while not ct.IsCancellationRequested do
                // Copy from watch folders into unclassified/
                let! _ = FolderWatcher.scanAll fs db logger clock config

                // Process new files in unclassified/
                let unclDir = Path.Combine(archiveDir, "unclassified")
                if fs.directoryExists unclDir then
                    let files =
                        fs.getFiles unclDir "*"
                        |> Array.filter (fun f ->
                            not (f.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)))
                    for filePath in files do
                        try
                            let fileName = Path.GetFileName(filePath)
                            // SHA256 dedup
                            let! sha256 = FolderWatcher.computeSha256 fs filePath
                            let! dupResult = db.execScalar "SELECT COUNT(*) FROM documents WHERE sha256 = @sha" [ ("@sha", Database.boxVal sha256) ]
                            let isDup = match dupResult with :? int64 as i -> i > 0L | _ -> false
                            if isDup then
                                logger.info $"Duplicate detected (sha256={sha256.[..7]}), removing: {fileName}"
                                fs.deleteFile filePath
                                let metaPath = filePath + ".meta.json"
                                if fs.fileExists metaPath then fs.deleteFile metaPath
                            else
                                // Insert document record
                                let relativePath = Path.Combine("unclassified", fileName)
                                let fileSize = fs.getFileSize filePath
                                let now = clock.utcNow().ToString("o")
                                let! idObj =
                                    db.execScalar
                                        """INSERT INTO documents
                                           (source_type, original_name, saved_path, category, size_bytes, sha256, source_path, ingested_at, stage)
                                           VALUES ('watched_folder', @name, @path, 'unclassified', @size, @sha, @src, @now, 'received')
                                           RETURNING id"""
                                        [ ("@name", Database.boxVal fileName)
                                          ("@path", Database.boxVal relativePath)
                                          ("@size", Database.boxVal fileSize)
                                          ("@sha", Database.boxVal sha256)
                                          ("@src", Database.boxVal filePath)
                                          ("@now", Database.boxVal now) ]
                                let docId = match idObj with :? int64 as i -> i | _ -> 0L
                                if docId > 0L then
                                    // Clean up sidecar
                                    let metaPath = filePath + ".meta.json"
                                    if fs.fileExists metaPath then fs.deleteFile metaPath
                                    // Ingest into pipeline
                                    let! _ = ingest db logger extractWriter (Map.ofList [ "id", box docId ])
                                    logger.info $"Folder watcher: ingested '{fileName}' as doc {docId}"
                        with ex ->
                            logger.warn $"Folder intake failed for {Path.GetFileName(filePath)}: {ex.Message}"

                try do! Task.Delay(TimeSpan.FromSeconds(30.0), ct)
                with :? OperationCanceledException -> ()
        }

    // ─── Email producer ──────────────────────────────────────────

    /// Run email sync for one account perpetually.
    let private emailAccountLoop
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (deps: Deps) (config: Domain.HermesConfig) (configDir: string)
        (account: Domain.AccountConfig)
        (extractWriter: ChannelWriter<Document.T>)
        (ct: CancellationToken) : Task<unit> =
        task {
            let emailConcurrency = max 1 config.Pipeline.EmailConcurrency
            let syncInterval = TimeSpan.FromMinutes(float config.SyncIntervalMinutes)

            // Initial delay to let hydration complete first
            try do! Task.Delay(TimeSpan.FromSeconds(30.0), ct)
            with :? OperationCanceledException -> ()

            while not ct.IsCancellationRequested do
                try
                    let! provider = deps.CreateEmailProvider configDir account.Label
                    let! _ = EmailSync.syncAccountWithChannel fs db logger clock provider config account.Label extractWriter emailConcurrency ct
                    ()
                with ex -> logger.warn $"Email sync failed for {account.Label}: {ex.Message}"
                try do! Task.Delay(syncInterval, ct)
                with :? OperationCanceledException -> ()
        }

    // ─── Dashboard ───────────────────────────────────────────────

    /// Log pipeline stage counts periodically.
    let private dashboardLoop
        (db: Algebra.Database) (logger: Algebra.Logger)
        (channels: Channels)
        (interval: TimeSpan) (ct: CancellationToken) : Task<unit> =
        task {
            try
                while not ct.IsCancellationRequested do
                    try do! Task.Delay(interval, ct)
                    with :? OperationCanceledException -> ()
                    if ct.IsCancellationRequested then () else

                    let! counts = Document.stageCounts db
                    let total = counts |> Map.values |> Seq.sum
                    let summary =
                        [ "received", "reading"; "understood", "understood"; "embedded", "memorised"; "failed", "failed" ]
                        |> List.map (fun (key, label) -> $"{label}:{counts |> Map.tryFind key |> Option.defaultValue 0L}")
                        |> String.concat " "

                    logger.info $"pipeline: {summary} | {total} total"
            with :? OperationCanceledException -> ()
        }

    // ─── Pipeline start ──────────────────────────────────────────

    /// Start the pipeline: hydrate, launch consumers, start producers.
    let start
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (_rules: Algebra.RulesEngine) (deps: Deps)
        (config: Domain.HermesConfig) (configDir: string)
        (ct: CancellationToken) : Task<unit> =
        task {
            let archiveDir = config.ArchiveDir
            let extractConcurrency =
                if config.Pipeline.ExtractConcurrency > 0 then config.Pipeline.ExtractConcurrency
                else max 1 (Environment.ProcessorCount / 2)
            let llmConcurrency = max 1 config.Pipeline.LlmConcurrency

            logger.info $"Pipeline starting: extract={extractConcurrency}, understand={llmConcurrency}"

            // Create channels
            let channels = createChannels ()

            // Build stage deps
            let stageDeps : Stages.Deps =
                { Fs = fs; Db = db; Logger = logger; Clock = clock
                  Extractor = deps.Extractor; Embedder = deps.Embedder
                  ChatProvider = deps.ChatProvider; TriageProvider = deps.TriageProvider
                  ContentRules = deps.ContentRules
                  ComprehensionPrompt = deps.ComprehensionPrompt
                  ArchiveDir = archiveDir }

            // GPU resource lock: shared semaphore when Ollama serves both understand and embed
            let resourceLock =
                if config.Ollama.Enabled && config.Ollama.SharedGpu then
                    logger.info $"GPU mutex enabled: burst hold {config.Ollama.MaxHoldSeconds}s"
                    Some (new SemaphoreSlim(1, 1))
                else None
            let maxHoldTime = TimeSpan.FromSeconds(float config.Ollama.MaxHoldSeconds)

            // Get stage definitions
            let stages = Stages.standardStages stageDeps resourceLock maxHoldTime

            // ── Step 1: Hydrate channels from DB ─────────────────
            let! hydrated = Workflow.hydrate db logger channels.Extract.Writer
            logger.info $"Hydrated {hydrated} incomplete documents"

            // ── Step 2: Launch consumers ─────────────────────────
            let tasks = ResizeArray<Task>()

            // Extract consumers: extractCh → understandCh
            let extractStage = stages |> List.find (fun s -> s.Name = "extract")
            for t in Workflow.launchConsumers extractStage db logger extractConcurrency channels.Extract.Reader (Some channels.Understand.Writer) ct do
                tasks.Add(t)

            // Understand consumers: understandCh → embedCh
            let understandStage = stages |> List.find (fun s -> s.Name = "understand")
            for t in Workflow.launchConsumers understandStage db logger llmConcurrency channels.Understand.Reader (Some channels.Embed.Writer) ct do
                tasks.Add(t)

            // Embed consumers: embedCh → done
            let embedStage = stages |> List.find (fun s -> s.Name = "embed")
            for t in Workflow.launchConsumers embedStage db logger 1 channels.Embed.Reader None ct do
                tasks.Add(t)

            // ── Step 3: Start producers ──────────────────────────
            tasks.Add(folderWatcherLoop fs db logger clock config channels.Extract.Writer ct)
            // One perpetual sync task per email account
            for account in config.Accounts do
                tasks.Add(emailAccountLoop fs db logger clock deps config configDir account channels.Extract.Writer ct)

            // ── Dashboard ────────────────────────────────────────
            tasks.Add(dashboardLoop db logger channels (TimeSpan.FromSeconds(30.0)) ct)

            // Wait for all tasks
            try do! Task.WhenAll(tasks.ToArray())
            with _ -> ()

            logger.info "Pipeline stopped"
        }
