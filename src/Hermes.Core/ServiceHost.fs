namespace Hermes.Core

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Background service orchestration: periodic sync, backlog processing,
/// heartbeat status file, and graceful shutdown.
/// Parameterised over algebras for testability.
[<RequireQualifiedAccess>]
module ServiceHost =

    // ─── Configuration ──────────────────────────────────────────────

    /// All settings needed to run the background service.
    type HermesServiceConfig =
        { ArchiveDir: string
          SyncIntervalMinutes: int
          HeartbeatIntervalSeconds: int
          Config: Domain.HermesConfig }

    let defaultServiceConfig (config: Domain.HermesConfig) : HermesServiceConfig =
        { ArchiveDir = config.ArchiveDir
          SyncIntervalMinutes = config.SyncIntervalMinutes
          HeartbeatIntervalSeconds = 60
          Config = config }

    // ─── Heartbeat / status ─────────────────────────────────────────

    /// Runtime status of the service.
    type ServiceStatus =
        { Running: bool
          StartedAt: DateTimeOffset option
          LastSyncAt: DateTimeOffset option
          LastSyncOk: bool
          DocumentCount: int64
          UnclassifiedCount: int
          ErrorMessage: string option }

    let private statusFileName = "hermes-status.json"
    let private syncTriggerFileName = "hermes-sync-now"

    /// Drop a trigger file to request an immediate sync.
    let requestSync (archiveDir: string) : unit =
        let path = Path.Combine(archiveDir, syncTriggerFileName)
        File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O"))

    let private jsonOptions =
        JsonSerializerOptions(WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    /// Build the path to the status file within the archive directory.
    let statusFilePath (archiveDir: string) : string =
        Path.Combine(archiveDir, statusFileName)

    /// Write the heartbeat status file as JSON.
    let writeHeartbeat
        (fs: Algebra.FileSystem)
        (archiveDir: string)
        (status: ServiceStatus)
        : Task<unit> =
        task {
            let json = JsonSerializer.Serialize(status, jsonOptions)
            do! fs.writeAllText (statusFilePath archiveDir) json
        }

    /// Read the heartbeat status file, returning None if missing or malformed.
    let readHeartbeat
        (fs: Algebra.FileSystem)
        (archiveDir: string)
        : Task<ServiceStatus option> =
        task {
            let path = statusFilePath archiveDir

            if not (fs.fileExists path) then
                return None
            else
                try
                    let! json = fs.readAllText path
                    let result: ServiceStatus | null = JsonSerializer.Deserialize<ServiceStatus>(json, jsonOptions)

                    match result with
                    | null -> return None
                    | v -> return Some v
                with _ ->
                    return None
        }

    // ─── Backlog detection ──────────────────────────────────────────

    /// Count files in the unclassified/ directory.
    let countUnclassified (fs: Algebra.FileSystem) (archiveDir: string) : int =
        let dir = Path.Combine(archiveDir, "unclassified")

        if fs.directoryExists dir then
            let files = fs.getFiles dir "*"
            files |> Array.filter (fun f -> not (f.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))) |> Array.length
        else
            0

    /// Count documents in the database.
    let countDocuments (db: Algebra.Database) : Task<int64> =
        task {
            let! result = db.execScalar "SELECT COUNT(*) FROM documents" []

            match result with
            | null -> return 0L
            | v ->
                match v with
                | :? int64 as i -> return i
                | :? int as i -> return int64 i
                | _ -> return 0L
        }

    /// Get documents that have no extracted text (backlog for extraction).
    let countUnextracted (db: Algebra.Database) : Task<int64> =
        task {
            let! result =
                db.execScalar "SELECT COUNT(*) FROM documents WHERE extracted_text IS NULL" []

            match result with
            | null -> return 0L
            | v ->
                match v with
                | :? int64 as i -> return i
                | :? int as i -> return int64 i
                | _ -> return 0L
        }

    // ─── Sync cycle ─────────────────────────────────────────────────

    /// Run one sync cycle: email sync, watch folders, classify, extract, embed.
    let runSyncCycle
        (fs: Algebra.FileSystem)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (clock: Algebra.Clock)
        (rules: Algebra.RulesEngine)
        (config: Domain.HermesConfig)
        (configDir: string)
        : Task<Result<unit, string>> =
        task {
            try
                // 1. Email sync → unclassified/
                for account in config.Accounts do
                    try
                        logger.debug $"Syncing email account: {account.Label}"
                        let! provider = GmailProvider.create configDir account.Label logger
                        let! _result = EmailSync.syncAccount fs db logger clock provider config account.Label
                        ()
                    with ex ->
                        logger.warn $"Email sync failed for {account.Label}: {ex.Message}"

                // 2. Scan watch folders → unclassified/
                logger.debug "Scanning watch folders..."
                let! _watchResults = FolderWatcher.scanAll fs db logger clock config

                // 3. Extract text from un-extracted documents (BEFORE classify)
                logger.debug "Running extraction on backlog (including unclassified)..."
                let extractor : Algebra.TextExtractor =
                    { extractPdf = fun bytes -> task { return Extraction.extractPdfText bytes }
                      extractImage = fun _ -> task { return Error "Ollama not configured" } }
                let! _extractResult =
                    Extraction.extractBatch fs db logger clock extractor config.ArchiveDir None false 50

                // 4. Classify unclassified files → archive categories
                let unclassifiedDir = Path.Combine(config.ArchiveDir, "unclassified")

                if fs.directoryExists unclassifiedDir then
                    let files =
                        fs.getFiles unclassifiedDir "*"
                        |> Array.filter (fun f ->
                            not (f.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)))

                    for file in files do
                        let! _ = Classifier.processFile fs db logger clock rules config.ArchiveDir file
                        ()

                // 4.5. Backfill historical email
                for account in config.Accounts do
                    if account.Backfill.Enabled then
                        try
                            let! provider = GmailProvider.create configDir account.Label logger
                            let! _newCount, _completed = EmailSync.backfillAccount fs db logger clock provider config account
                            ()
                        with ex ->
                            logger.warn $"Backfill failed for {account.Label}: {ex.Message}"

                // 4.6. Evaluate bill reminders
                logger.debug "Evaluating bill reminders..."
                let! newReminders = Reminders.evaluateNewDocuments db logger (clock.utcNow ())
                if newReminders > 0 then logger.info $"Created {newReminders} new reminder(s)"

                // 4.6. Un-snooze expired reminders
                let! unsnoozed = Reminders.unsnoozeExpired db (clock.utcNow ())
                if unsnoozed > 0 then logger.info $"Un-snoozed {unsnoozed} reminder(s)"

                // 5. Embed un-embedded documents
                logger.debug "Running embedding on backlog..."
                let ollamaUrl = config.Ollama.BaseUrl.TrimEnd('/')
                let embedModel = config.Ollama.EmbeddingModel
                let embedder : Algebra.EmbeddingClient =
                    { embed = fun text ->
                        task {
                            try
                                use client = new System.Net.Http.HttpClient(Timeout = TimeSpan.FromSeconds(30.0))
                                let payload = System.Text.Json.JsonSerializer.Serialize({| model = embedModel; input = text |})
                                let content = new System.Net.Http.StringContent(payload, Text.Encoding.UTF8, "application/json")
                                let! response = client.PostAsync($"{ollamaUrl}/api/embed", content)
                                let! body = response.Content.ReadAsStringAsync()
                                if not response.IsSuccessStatusCode then
                                    return Error $"Ollama embed failed: {response.StatusCode}"
                                else
                                    let doc = System.Text.Json.JsonDocument.Parse(body)
                                    let arr = doc.RootElement.GetProperty("embeddings").[0]
                                    let vec = [| for i in 0 .. arr.GetArrayLength() - 1 -> arr.[i].GetSingle() |]
                                    return Ok vec
                            with ex ->
                                return Error $"Ollama embed error: {ex.Message}"
                        }
                      dimensions = 768
                      isAvailable = fun () ->
                        task {
                            try
                                use client = new System.Net.Http.HttpClient(Timeout = TimeSpan.FromSeconds(2.0))
                                let! response = client.GetAsync($"{ollamaUrl}/api/tags")
                                return response.IsSuccessStatusCode
                            with _ -> return false
                        } }

                if config.Ollama.Enabled then
                    let! embedAvailable = embedder.isAvailable ()
                    if embedAvailable then
                        logger.info "Ollama available — embedding documents..."
                        let! _embedResult = Embeddings.batchEmbed db logger embedder false (Some 50) None
                        ()
                    else
                        logger.debug "Ollama not available, skipping embedding"

                logger.debug "Sync cycle completed."
                return Ok()
            with ex ->
                logger.error $"Sync cycle failed: {ex.Message}"
                return Error ex.Message
        }

    // ─── Service host ───────────────────────────────────────────────

    /// Run the background service loop until cancellation is requested.
    /// configPath is re-read before each sync so folder/account changes are picked up live.
    let createServiceHost
        (fs: Algebra.FileSystem)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (clock: Algebra.Clock)
        (rules: Algebra.RulesEngine)
        (serviceConfig: HermesServiceConfig)
        (configPath: string)
        (ct: CancellationToken)
        : Task<unit> =
        task {
            let startedAt = clock.utcNow ()
            let configDir =
                Path.GetDirectoryName(configPath)
                |> Option.ofObj
                |> Option.defaultValue (Config.configDir ())
            let mutable lastSyncAt: DateTimeOffset option = None
            let mutable lastSyncOk = true
            let mutable lastError: string option = None
            let mutable syncRunning = false
            let mutable liveConfig = serviceConfig.Config

            logger.info $"Hermes service starting (sync every {serviceConfig.SyncIntervalMinutes}m, heartbeat every {serviceConfig.HeartbeatIntervalSeconds}s)"

            let writeStatus (running: bool) =
                task {
                    try
                        let! docCount = countDocuments db
                        let unclassified = countUnclassified fs serviceConfig.ArchiveDir

                        let status: ServiceStatus =
                            { Running = running
                              StartedAt = Some startedAt
                              LastSyncAt = lastSyncAt
                              LastSyncOk = lastSyncOk
                              DocumentCount = docCount
                              UnclassifiedCount = unclassified
                              ErrorMessage = lastError }

                        do! writeHeartbeat fs serviceConfig.ArchiveDir status
                    with ex ->
                        logger.warn $"Failed to write heartbeat: {ex.Message}"
                }

            /// Reload config from disk; fall back to current liveConfig on failure.
            let reloadConfig () =
                task {
                    let! result = Config.load fs configPath
                    match result with
                    | Ok cfg ->
                        liveConfig <- cfg
                        logger.debug "Config reloaded."
                    | Error e ->
                        logger.warn $"Config reload failed, using previous config: {e}"
                }

            // Write initial heartbeat
            do! writeStatus true

            // Run initial backlog processing
            logger.info "Processing startup backlog..."

            let! initialResult =
                runSyncCycle fs db logger clock rules liveConfig configDir

            match initialResult with
            | Ok() ->
                lastSyncAt <- Some(clock.utcNow ())
                lastSyncOk <- true
                lastError <- None
                logger.info "Startup backlog processed."
            | Error e ->
                lastSyncOk <- false
                lastError <- Some e
                logger.warn $"Startup backlog processing had errors: {e}"

            do! writeStatus true

            let heartbeatInterval = TimeSpan.FromSeconds(float serviceConfig.HeartbeatIntervalSeconds)
            let mutable lastHeartbeat = clock.utcNow ()

            // Main loop
            while not ct.IsCancellationRequested do
                try
                    do! Task.Delay(TimeSpan.FromSeconds(5.0), ct)
                with :? OperationCanceledException ->
                    ()

                if ct.IsCancellationRequested then
                    ()
                else

                let now = clock.utcNow ()

                // Heartbeat
                if now - lastHeartbeat >= heartbeatInterval then
                    do! writeStatus true
                    lastHeartbeat <- now

                // Sync — either timer fired or trigger file was dropped
                let triggerPath = Path.Combine(serviceConfig.ArchiveDir, syncTriggerFileName)
                let triggered = fs.fileExists triggerPath

                if triggered then
                    try fs.deleteFile triggerPath with _ -> ()

                let syncInterval = TimeSpan.FromMinutes(float liveConfig.SyncIntervalMinutes)

                let shouldSync =
                    triggered ||
                    match lastSyncAt with
                    | None -> true
                    | Some last -> now - last >= syncInterval

                if shouldSync && not syncRunning then
                    syncRunning <- true

                    try
                        // Always reload config before syncing so new watch folders are picked up
                        do! reloadConfig ()

                        let! result =
                            runSyncCycle fs db logger clock rules liveConfig configDir

                        match result with
                        | Ok() ->
                            lastSyncAt <- Some(clock.utcNow ())
                            lastSyncOk <- true
                            lastError <- None
                        | Error e ->
                            lastSyncAt <- Some(clock.utcNow ())
                            lastSyncOk <- false
                            lastError <- Some e
                    finally
                        syncRunning <- false

            // Shutdown
            logger.info "Hermes service stopping..."
            do! writeStatus false
            logger.info "Hermes service stopped."
        }
