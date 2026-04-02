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

    let requestSync (archiveDir: string) =
        File.WriteAllText(Path.Combine(archiveDir, syncTriggerFileName), DateTimeOffset.UtcNow.ToString("O"))

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

    let private syncEmails (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger) (clock: Algebra.Clock) (config: Domain.HermesConfig) (configDir: string) =
        task {
            for account in config.Accounts do
                try
                    let! provider = GmailProvider.create configDir account.Label logger
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

    let private runBackfill (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger) (clock: Algebra.Clock) (config: Domain.HermesConfig) (configDir: string) =
        task {
            for account in config.Accounts do
                if account.Backfill.Enabled then
                    try
                        let! provider = GmailProvider.create configDir account.Label logger
                        let! _ = EmailSync.backfillAccount fs db logger clock provider config account
                        ()
                    with ex -> logger.warn $"Backfill failed for {account.Label}: {ex.Message}"
        }

    let private reclassifyUnsorted
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (config: Domain.HermesConfig) (contentRules: Domain.ContentRule list) =
        task {
            // Tier 2: content rules reclassification
            let! reclassified, remaining =
                Classifier.reclassifyUnsortedBatch db fs contentRules config.ArchiveDir 50
            if reclassified > 0 then
                logger.info $"Tier 2 reclassified {reclassified} documents ({remaining} still unsorted)"

            // Tier 3: LLM classification for remaining unsorted
            if remaining > 0 then
                try
                    let chatProvider = Chat.providerFromConfig config.Chat config.Ollama.BaseUrl config.Ollama.InstructModel
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
                            let! llmResult = chatProvider.complete "You are a document classifier." prompt
                            match llmResult with
                            | Ok response ->
                                match ContentClassifier.parseClassificationResponse response with
                                | Some (cat, conf, reasoning) when conf >= 0.4 && categories |> List.contains cat ->
                                    let! moveResult = DocumentManagement.reclassify db fs config.ArchiveDir docId cat
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
        }

    let private runExtraction (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger) (clock: Algebra.Clock) (archiveDir: string) =
        task {
            let extractor : Algebra.TextExtractor =
                { extractPdf = fun bytes -> task { return Extraction.extractPdfText bytes }
                  extractImage = fun _ -> task { return Error "Ollama vision not configured" } }
            let! _ = Extraction.extractBatch fs db logger clock extractor archiveDir None false 50
            ()
        }

    let private evaluateReminders (db: Algebra.Database) (logger: Algebra.Logger) (clock: Algebra.Clock) =
        task {
            let! n = Reminders.evaluateNewDocuments db logger (clock.utcNow ())
            if n > 0 then logger.info $"Created {n} new reminder(s)"
            let! u = Reminders.unsnoozeExpired db (clock.utcNow ())
            if u > 0 then logger.info $"Un-snoozed {u} reminder(s)"
        }

    let private runEmbedding (db: Algebra.Database) (logger: Algebra.Logger) (config: Domain.HermesConfig) =
        task {
            if not config.Ollama.Enabled then ()
            else
            let url = config.Ollama.BaseUrl.TrimEnd('/')
            let model = config.Ollama.EmbeddingModel
            let embedder : Algebra.EmbeddingClient =
                { embed = fun text ->
                    task {
                        try
                            use client = new System.Net.Http.HttpClient(Timeout = TimeSpan.FromSeconds(30.0))
                            let payload = JsonSerializer.Serialize({| model = model; input = text |})
                            let content = new System.Net.Http.StringContent(payload, Text.Encoding.UTF8, "application/json")
                            let! resp = client.PostAsync($"{url}/api/embed", content)
                            let! body = resp.Content.ReadAsStringAsync()
                            if not resp.IsSuccessStatusCode then return Error $"Ollama: {resp.StatusCode}"
                            else
                                let doc = JsonDocument.Parse(body)
                                let arr = doc.RootElement.GetProperty("embeddings").[0]
                                return Ok [| for i in 0 .. arr.GetArrayLength() - 1 -> arr.[i].GetSingle() |]
                        with ex -> return Error $"Ollama: {ex.Message}"
                    }
                  dimensions = 768
                  isAvailable = fun () ->
                    task {
                        try
                            use client = new System.Net.Http.HttpClient(Timeout = TimeSpan.FromSeconds(2.0))
                            let! resp = client.GetAsync($"{url}/api/tags")
                            return resp.IsSuccessStatusCode
                        with _ -> return false
                    } }
            let! avail = embedder.isAvailable ()
            if avail then
                logger.info "Ollama available — embedding..."
                let! _ = Embeddings.batchEmbed db logger embedder false (Some 50) None
                ()
        }

    // ─── Composed sync cycle ────────────────────────────────────────

    let runSyncCycle
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (rules: Algebra.RulesEngine)
        (config: Domain.HermesConfig) (configDir: string)
        : Task<Result<unit, string>> =
        task {
            try
                do! syncEmails fs db logger clock config configDir
                do! syncWatchFolders fs db logger clock config
                do! runExtraction fs db logger clock config.ArchiveDir
                do! classifyUnclassified fs db logger clock rules config.ArchiveDir
                // Load content rules from rules.yaml for Tier 2+3
                let contentRules =
                    let rulesPath = Path.Combine(configDir, "rules.yaml")
                    if fs.fileExists rulesPath then
                        let yaml = (fs.readAllText rulesPath).Result
                        Rules.parseContentRules yaml
                    else []
                do! reclassifyUnsorted fs db logger config contentRules
                do! runBackfill fs db logger clock config configDir
                do! evaluateReminders db logger clock
                do! runEmbedding db logger config
                logger.debug "Sync cycle completed."
                return Ok()
            with ex ->
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

    let private reloadConfig (fs: Algebra.FileSystem) (logger: Algebra.Logger) (configPath: string) (state: LoopState) =
        task {
            let! result = Config.load fs configPath
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
        (clock: Algebra.Clock) (rules: Algebra.RulesEngine) (configDir: string) (state: LoopState) =
        task {
            state.SyncRunning <- true
            try
                let! result = runSyncCycle fs db logger clock rules state.LiveConfig configDir
                match result with
                | Ok() -> state.LastSyncAt <- Some(clock.utcNow()); state.LastSyncOk <- true; state.LastError <- None
                | Error e -> state.LastSyncAt <- Some(clock.utcNow()); state.LastSyncOk <- false; state.LastError <- Some e
            finally
                state.SyncRunning <- false
        }

    let createServiceHost
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (rules: Algebra.RulesEngine)
        (serviceConfig: HermesServiceConfig) (configPath: string) (ct: CancellationToken)
        : Task<unit> =
        task {
            let startedAt = clock.utcNow ()
            let configDir = Path.GetDirectoryName(configPath) |> Option.ofObj |> Option.defaultValue (Config.configDir ())
            let state =
                { LastSyncAt = None; LastSyncOk = true; LastError = None
                  SyncRunning = false; LiveConfig = serviceConfig.Config; LastHeartbeat = clock.utcNow () }

            logger.info $"Hermes service starting (sync every {serviceConfig.SyncIntervalMinutes}m)"
            do! writeStatusFromState fs db serviceConfig.ArchiveDir startedAt state true

            // Initial backlog processing
            do! runOneSyncCycle fs db logger clock rules configDir state
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
                    do! reloadConfig fs logger configPath state
                    do! runOneSyncCycle fs db logger clock rules configDir state

            logger.info "Hermes service stopping..."
            do! writeStatusFromState fs db serviceConfig.ArchiveDir startedAt state false
            logger.info "Hermes service stopped."
        }
