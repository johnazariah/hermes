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

    /// Run one sync cycle: scan watch folders, classify unclassified files.
    let runSyncCycle
        (fs: Algebra.FileSystem)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (clock: Algebra.Clock)
        (rules: Algebra.RulesEngine)
        (config: Domain.HermesConfig)
        : Task<Result<unit, string>> =
        task {
            try
                // 1. Scan watch folders → unclassified/
                logger.debug "Scanning watch folders..."

                let! _watchResults =
                    FolderWatcher.scanAll fs db logger clock config

                // 2. Classify unclassified files → archive categories
                let unclassifiedDir = Path.Combine(config.ArchiveDir, "unclassified")

                if fs.directoryExists unclassifiedDir then
                    let files =
                        fs.getFiles unclassifiedDir "*"
                        |> Array.filter (fun f ->
                            not (f.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)))

                    for file in files do
                        let! _ = Classifier.processFile fs db logger clock rules config.ArchiveDir file
                        ()

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
                runSyncCycle fs db logger clock rules liveConfig

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
                            runSyncCycle fs db logger clock rules liveConfig

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
