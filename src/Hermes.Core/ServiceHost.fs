namespace Hermes.Core

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Background service orchestration: periodic sync, heartbeat, graceful shutdown.
/// Each sync step is a named function ≤20 lines; the main loop delegates to them.
[<RequireQualifiedAccess>]
module ServiceHost =

    // ─── Configuration ──────────────────────────────────────────────

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

    /// All capabilities needed for a sync cycle, parameterized for testability.
    type SyncDeps =
        { Extractor: Algebra.TextExtractor
          Embedder: Algebra.EmbeddingClient option
          ChatProvider: Algebra.ChatProvider option
          ContentRules: Domain.ContentRule list
          CreateEmailProvider: string -> string -> Task<Algebra.EmailProvider> }

    // ─── Heartbeat ──────────────────────────────────────────────────

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

    let private jsonOptions =
        JsonSerializerOptions(WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    let statusFilePath (archiveDir: string) = Path.Combine(archiveDir, statusFileName)

    let requestSync (fs: Algebra.FileSystem) (clock: Algebra.Clock) (archiveDir: string) =
        fs.writeAllText (Path.Combine(archiveDir, syncTriggerFileName)) (clock.utcNow().ToString("O"))

    let writeHeartbeat (fs: Algebra.FileSystem) (archiveDir: string) (status: ServiceStatus) =
        task { do! fs.writeAllText (statusFilePath archiveDir) (JsonSerializer.Serialize(status, jsonOptions)) }

    let readHeartbeat (fs: Algebra.FileSystem) (archiveDir: string) : Task<ServiceStatus option> =
        task {
            let path = statusFilePath archiveDir
            if not (fs.fileExists path) then return None
            else
                try
                    let! json = fs.readAllText path
                    return JsonSerializer.Deserialize<ServiceStatus>(json, jsonOptions) |> Option.ofObj
                with _ -> return None
        }

    // ─── Backlog detection ──────────────────────────────────────────

    let countUnclassified (fs: Algebra.FileSystem) (archiveDir: string) : int =
        let dir = Path.Combine(archiveDir, "unclassified")
        if not (fs.directoryExists dir) then 0
        else fs.getFiles dir "*" |> Array.filter (fun f -> not (f.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))) |> Array.length

    let private scalarInt64 (db: Algebra.Database) (sql: string) : Task<int64> =
        task {
            let! v = db.execScalar sql []
            return match v with null -> 0L | :? int64 as i -> i | :? int as i -> int64 i | _ -> 0L
        }

    let countDocuments (db: Algebra.Database) = scalarInt64 db "SELECT COUNT(*) FROM documents"
    let countUnextracted (db: Algebra.Database) = scalarInt64 db "SELECT COUNT(*) FROM documents WHERE extracted_text IS NULL"

    // ─── Sync pipeline steps ────────────────────────────────────────

    let private syncEmails
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (createProvider: string -> string -> Task<Algebra.EmailProvider>)
        (config: Domain.HermesConfig) (configDir: string) =
        task {
            for account in config.Accounts do
                try
                    let! provider = createProvider configDir account.Label
                    let! _ = EmailSync.syncAccount fs db logger clock provider config account.Label
                    ()
                with ex -> logger.warn $"Email sync failed for {account.Label}: {ex.Message}"
        }

    let private syncWatchFolders (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger) (clock: Algebra.Clock) (config: Domain.HermesConfig) =
        task { let! _ = FolderWatcher.scanAll fs db logger clock config in () }

    let private classifyUnclassified (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger) (clock: Algebra.Clock) (rules: Algebra.RulesEngine) (archiveDir: string) =
        task {
            let dir = Path.Combine(archiveDir, "unclassified")
            if fs.directoryExists dir then
                let files = fs.getFiles dir "*" |> Array.filter (fun f -> not (f.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)))
                for file in files do
                    let! _ = Classifier.processFile fs db logger clock rules archiveDir file
                    ()
        }

    let private runBackfill
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (createProvider: string -> string -> Task<Algebra.EmailProvider>)
        (config: Domain.HermesConfig) (configDir: string) =
        task {
            for account in config.Accounts do
                if account.Backfill.Enabled then
                    try
                        let! provider = createProvider configDir account.Label
                        let! _ = EmailSync.backfillAccount fs db logger clock provider config account
                        ()
                    with ex -> logger.warn $"Backfill failed for {account.Label}: {ex.Message}"
        }

    let private reclassifyUnsorted
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (chatProvider: Algebra.ChatProvider option) (contentRules: Domain.ContentRule list)
        (archiveDir: string) =
        task {
            // Tier 2: content rules reclassification
            let! reclassified, remaining =
                Classifier.reclassifyUnsortedBatch db fs contentRules archiveDir 50
            if reclassified > 0 then
                logger.info $"Tier 2 reclassified {reclassified} documents ({remaining} still unsorted)"

            // Tier 3: LLM classification for remaining unsorted
            match chatProvider with
            | Some provider when remaining > 0 ->
                try
                    let! unsortedRows =
                        db.execReader
                            """SELECT id, extracted_text FROM documents
                               WHERE (category = 'unsorted' OR category = 'unclassified')
                                 AND extracted_text IS NOT NULL
                               ORDER BY id ASC LIMIT 10"""
                            []
                    let! catRows =
                        db.execReader "SELECT DISTINCT category FROM documents WHERE category NOT IN ('unsorted','unclassified')" []
                    let categories =
                        catRows |> List.choose (fun r -> Prelude.RowReader(r).OptString "category")
                    let mutable llmClassified = 0
                    for row in unsortedRows do
                        let r = Prelude.RowReader(row)
                        match r.OptInt64 "id", r.OptString "extracted_text" with
                        | Some docId, Some text ->
                            let prompt = ContentClassifier.buildClassificationPrompt text categories
                            let! llmResult = provider.complete "You are a document classifier." prompt
                            match llmResult with
                            | Ok response ->
                                match ContentClassifier.parseClassificationResponse response with
                                | Some (cat, conf, reasoning) when conf >= 0.4 && categories |> List.contains cat ->
                                    let! moveResult = DocumentManagement.reclassify db fs archiveDir docId cat
                                    match moveResult with
                                    | Ok () ->
                                        let tier = if conf >= 0.7 then "llm" else "llm_review"
                                        let! _ =
                                            db.execNonQuery
                                                """UPDATE documents SET classification_tier = @tier,
                                                   classification_confidence = @conf WHERE id = @id"""
                                                [ ("@tier", Database.boxVal tier)
                                                  ("@conf", Database.boxVal conf)
                                                  ("@id", Database.boxVal docId) ]
                                        llmClassified <- llmClassified + 1
                                        if conf < 0.7 then
                                            logger.warn $"LLM classified doc {docId} as {cat} (conf={conf:F2}, needs review): {reasoning}"
                                        else
                                            logger.info $"LLM classified doc {docId} as {cat} (conf={conf:F2}): {reasoning}"
                                    | Error e -> logger.warn $"LLM reclassify move failed for doc {docId}: {e}"
                                | _ -> ()
                            | Error e ->
                                let docIdStr = r.OptInt64 "id" |> Option.map string |> Option.defaultValue "?"
                                logger.warn $"LLM classification failed for doc {docIdStr}: {e}"
                        | _ -> ()
                    if llmClassified > 0 then
                        logger.info $"Tier 3 LLM classified {llmClassified} documents"
                with ex ->
                    logger.debug $"LLM classification skipped: {ex.Message}"
            | _ -> ()
        }

    let private runExtraction
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (extractor: Algebra.TextExtractor) (archiveDir: string) =
        task {
            let! _ = Extraction.extractBatch fs db logger clock extractor archiveDir None false 50
            ()
        }

    let private evaluateReminders (db: Algebra.Database) (logger: Algebra.Logger) (clock: Algebra.Clock) =
        task {
            let! n = Reminders.evaluateNewDocuments db logger (clock.utcNow ())
            if n > 0 then
                logger.info $"Created {n} new reminder(s)"
                do! ActivityLog.logInfo db "reminder" $"Created {n} new reminder(s)" None
            let! u = Reminders.unsnoozeExpired db (clock.utcNow ())
            if u > 0 then
                logger.info $"Un-snoozed {u} reminder(s)"
                do! ActivityLog.logInfo db "reminder" $"Un-snoozed {u} reminder(s)" None
        }

    let private runEmbedding (db: Algebra.Database) (logger: Algebra.Logger) (clock: Algebra.Clock) (embedder: Algebra.EmbeddingClient option) =
        task {
            match embedder with
            | None -> ()
            | Some client ->
                let! avail = client.isAvailable ()
                if avail then
                    logger.info "Embedding service available — embedding..."
                    let! _ = Embeddings.batchEmbed db logger clock client false (Some 50) None
                    ()
        }

    // ─── Composed sync cycle ────────────────────────────────────────

    let runSyncCycle
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (rules: Algebra.RulesEngine) (deps: SyncDeps)
        (config: Domain.HermesConfig) (configDir: string)
        : Task<Result<unit, string>> =
        task {
            try
                do! ActivityLog.logInfo db "sync" "Sync cycle started" None
                do! syncEmails fs db logger clock deps.CreateEmailProvider config configDir
                do! syncWatchFolders fs db logger clock config
                do! runExtraction fs db logger clock deps.Extractor config.ArchiveDir
                do! classifyUnclassified fs db logger clock rules config.ArchiveDir
                do! reclassifyUnsorted fs db logger deps.ChatProvider deps.ContentRules config.ArchiveDir
                do! runBackfill fs db logger clock deps.CreateEmailProvider config configDir
                do! evaluateReminders db logger clock
                do! runEmbedding db logger clock deps.Embedder
                do! ActivityLog.logInfo db "sync" "Sync cycle completed" None
                logger.debug "Sync cycle completed."
                return Ok()
            with ex ->
                do! ActivityLog.logError db "sync" $"Sync cycle failed: {ex.Message}" None ex.Message
                logger.error $"Sync cycle failed: {ex.Message}"
                return Error ex.Message
        }

    // ─── Main service loop ──────────────────────────────────────────

    /// Mutable state for the service loop — isolated to this record.
    type private LoopState =
        { mutable LastSyncAt: DateTimeOffset option
          mutable LastSyncOk: bool
          mutable LastError: string option
          mutable SyncRunning: bool
          mutable LiveConfig: Domain.HermesConfig
          mutable LastHeartbeat: DateTimeOffset }

    let private buildStatus (startedAt: DateTimeOffset) (state: LoopState) (docCount: int64) (unclassified: int) : ServiceStatus =
        { Running = true; StartedAt = Some startedAt; LastSyncAt = state.LastSyncAt
          LastSyncOk = state.LastSyncOk; DocumentCount = docCount
          UnclassifiedCount = unclassified; ErrorMessage = state.LastError }

    let private writeStatusFromState (fs: Algebra.FileSystem) (db: Algebra.Database) (archiveDir: string) (startedAt: DateTimeOffset) (state: LoopState) (running: bool) =
        task {
            try
                let! docCount = countDocuments db
                let unclassified = countUnclassified fs archiveDir
                let status = { buildStatus startedAt state docCount unclassified with Running = running }
                do! writeHeartbeat fs archiveDir status
            with ex -> () // heartbeat failure is non-fatal
        }

    let private reloadConfig (fs: Algebra.FileSystem) (env: Algebra.Environment) (logger: Algebra.Logger) (configPath: string) (state: LoopState) =
        task {
            let! result = Config.load fs env configPath
            match result with
            | Ok cfg -> state.LiveConfig <- cfg; logger.debug "Config reloaded."
            | Error e -> logger.warn $"Config reload failed: {e}"
        }

    let private shouldSync (clock: Algebra.Clock) (fs: Algebra.FileSystem) (archiveDir: string) (interval: TimeSpan) (state: LoopState) =
        let triggerPath = Path.Combine(archiveDir, syncTriggerFileName)
        let triggered = fs.fileExists triggerPath
        if triggered then try fs.deleteFile triggerPath with _ -> ()
        let timerFired =
            match state.LastSyncAt with
            | None -> true
            | Some last -> clock.utcNow () - last >= interval
        triggered || timerFired

    let private runOneSyncCycle
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (rules: Algebra.RulesEngine) (deps: SyncDeps)
        (configDir: string) (state: LoopState) =
        task {
            state.SyncRunning <- true
            try
                let! result = runSyncCycle fs db logger clock rules deps state.LiveConfig configDir
                match result with
                | Ok() -> state.LastSyncAt <- Some(clock.utcNow()); state.LastSyncOk <- true; state.LastError <- None
                | Error e -> state.LastSyncAt <- Some(clock.utcNow()); state.LastSyncOk <- false; state.LastError <- Some e
            finally
                state.SyncRunning <- false
        }

    let createServiceHost
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (env: Algebra.Environment) (rules: Algebra.RulesEngine) (deps: SyncDeps)
        (serviceConfig: HermesServiceConfig) (configPath: string) (ct: CancellationToken)
        : Task<unit> =
        task {
            let startedAt = clock.utcNow ()
            let configDir = Path.GetDirectoryName(configPath) |> Option.ofObj |> Option.defaultValue (Config.configDir env)
            let state =
                { LastSyncAt = None; LastSyncOk = true; LastError = None
                  SyncRunning = false; LiveConfig = serviceConfig.Config; LastHeartbeat = clock.utcNow () }

            logger.info $"Hermes service starting (sync every {serviceConfig.SyncIntervalMinutes}m)"
            do! writeStatusFromState fs db serviceConfig.ArchiveDir startedAt state true

            // Initial backlog processing
            do! runOneSyncCycle fs db logger clock rules deps configDir state
            do! writeStatusFromState fs db serviceConfig.ArchiveDir startedAt state true

            let heartbeatInterval = TimeSpan.FromSeconds(float serviceConfig.HeartbeatIntervalSeconds)

            while not ct.IsCancellationRequested do
                try do! Task.Delay(TimeSpan.FromSeconds(5.0), ct) with :? OperationCanceledException -> ()
                if ct.IsCancellationRequested then ()
                else
                let now = clock.utcNow ()

                if now - state.LastHeartbeat >= heartbeatInterval then
                    do! writeStatusFromState fs db serviceConfig.ArchiveDir startedAt state true
                    state.LastHeartbeat <- now

                let syncInterval = TimeSpan.FromMinutes(float state.LiveConfig.SyncIntervalMinutes)
                if shouldSync clock fs serviceConfig.ArchiveDir syncInterval state && not state.SyncRunning then
                    do! reloadConfig fs env logger configPath state
                    do! runOneSyncCycle fs db logger clock rules deps configDir state

            logger.info "Hermes service stopping..."
            do! writeStatusFromState fs db serviceConfig.ArchiveDir startedAt state false
            logger.info "Hermes service stopped."
        }

    /// Build production SyncDeps — the ONLY place concrete implementations are constructed.
    let buildProductionDeps
        (config: Domain.HermesConfig) (configDir: string)
        (logger: Algebra.Logger) (fs: Algebra.FileSystem) : SyncDeps =
        let extractor : Algebra.TextExtractor =
            { extractPdf = fun bytes -> task { return Extraction.extractPdfText bytes }
              extractImage = fun _ -> task { return Error "Ollama vision not configured" } }
        let embedder =
            if config.Ollama.Enabled then
                Some (Embeddings.ollamaClient (new System.Net.Http.HttpClient()) config.Ollama.BaseUrl config.Ollama.EmbeddingModel 768)
            else None
        let chatProvider =
            try Some (Chat.providerFromConfig (new System.Net.Http.HttpClient()) config.Chat config.Ollama.BaseUrl config.Ollama.InstructModel)
            with _ -> None
        let contentRules =
            let rulesPath = Path.Combine(configDir, "rules.yaml")
            if fs.fileExists rulesPath then
                let yaml = (fs.readAllText rulesPath).Result
                Rules.parseContentRules yaml
            else []
        { Extractor = extractor
          Embedder = embedder
          ChatProvider = chatProvider
          ContentRules = contentRules
          CreateEmailProvider = fun cfgDir label -> GmailProvider.create cfgDir label logger }
