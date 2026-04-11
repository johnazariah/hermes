namespace Hermes.Core

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Channels
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
            // Run all accounts concurrently — each is an independent producer
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
        }

    let private syncWatchFolders (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger) (clock: Algebra.Clock) (config: Domain.HermesConfig) =
        task { let! _ = FolderWatcher.scanAll fs db logger clock config in () }

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

                // Stage 1: Classify → Channel → Stage 2: Extract
                let extractCh = Channel.CreateUnbounded<int64>()
                let deadLetterCh = Channel.CreateUnbounded<Domain.DeadLetter>()
                do! ClassifyStage.classifyNew fs db logger clock rules config.ArchiveDir extractCh.Writer
                extractCh.Writer.Complete()
                do! ExtractStage.run fs db logger clock deps.Extractor config.ArchiveDir extractCh.Reader deadLetterCh.Writer

                // Log dead letters from this cycle
                let mutable dl = Unchecked.defaultof<Domain.DeadLetter>
                let mutable dlCount = 0
                while deadLetterCh.Reader.TryRead(&dl) do
                    logger.warn $"Dead letter: doc {dl.DocId} ({dl.OriginalName}) — {dl.Error}"
                    dlCount <- dlCount + 1
                if dlCount > 0 then
                    logger.info $"{dlCount} document(s) failed extraction (dead letter)"

                // Stage 1b: Reclassify unsorted (needs extracted text from Stage 2)
                do! ClassifyStage.reclassifyUnsorted fs db logger deps.ChatProvider deps.ContentRules config.ArchiveDir

                // Stage 3: Post-processing (reminders, embedding, plugins)
                let postProcessors = PostStage.defaultPlugins deps.Embedder deps.ChatProvider
                do! PostStage.run db fs logger clock postProcessors

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
        (configDir: string) (state: LoopState) (onBusy: unit -> unit) (onIdle: unit -> unit) =
        task {
            state.SyncRunning <- true
            onBusy ()
            try
                let! result = runSyncCycle fs db logger clock rules deps state.LiveConfig configDir
                match result with
                | Ok() -> state.LastSyncAt <- Some(clock.utcNow()); state.LastSyncOk <- true; state.LastError <- None
                | Error e -> state.LastSyncAt <- Some(clock.utcNow()); state.LastSyncOk <- false; state.LastError <- Some e
            finally
                state.SyncRunning <- false
                onIdle ()
        }

    let createServiceHost
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (env: Algebra.Environment) (rules: Algebra.RulesEngine) (deps: SyncDeps)
        (serviceConfig: HermesServiceConfig) (configPath: string) (ct: CancellationToken)
        (onBusy: unit -> unit) (onIdle: unit -> unit)
        : Task<unit> =
        task {
            let startedAt = clock.utcNow ()
            let configDir = Path.GetDirectoryName(configPath) |> Option.ofObj |> Option.defaultValue (Config.configDir env)
            let state =
                { LastSyncAt = None; LastSyncOk = true; LastError = None
                  SyncRunning = false; LiveConfig = serviceConfig.Config; LastHeartbeat = clock.utcNow () }

            logger.info $"Hermes service starting (sync every {serviceConfig.SyncIntervalMinutes}m)"
            do! writeStatusFromState fs db serviceConfig.ArchiveDir startedAt state true

            // Delay initial sync to avoid slamming Gmail on rapid restarts
            logger.info "Waiting 30s before first sync..."
            try do! Task.Delay(TimeSpan.FromSeconds(30.0), ct) with :? OperationCanceledException -> ()
            if ct.IsCancellationRequested then () else

            // Initial backlog processing
            do! runOneSyncCycle fs db logger clock rules deps configDir state onBusy onIdle
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
                    try
                        do! reloadConfig fs env logger configPath state
                        do! runOneSyncCycle fs db logger clock rules deps configDir state onBusy onIdle
                    with ex ->
                        logger.error $"Sync cycle crashed (will retry next interval): {ex.Message}"
                        state.LastSyncAt <- Some(clock.utcNow())
                        state.LastSyncOk <- false
                        state.LastError <- Some ex.Message

            logger.info "Hermes service stopping..."
            do! writeStatusFromState fs db serviceConfig.ArchiveDir startedAt state false
            logger.info "Hermes service stopped."
        }

