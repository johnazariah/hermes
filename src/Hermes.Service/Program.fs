module Hermes.Service.Program

#nowarn "3261"

open System
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Hermes.Core
open Serilog.Events

type private ServiceComponents =
    { Db: Algebra.Database
      ReflowDb: Algebra.Database
      Fs: Algebra.FileSystem
      Logger: Algebra.Logger
      Clock: Algebra.Clock
      Config: Domain.HermesConfig
      ConfigDirectory: string
      Dag: PipelineV5.Dag
      Gpu: PipelineV5.GpuScheduler
      Embedder: Algebra.EmbeddingClient option
      ChatProvider: Algebra.ChatProvider option
      DeepDependencies: McpTools.DeepExtractionDeps option
      Control: OwnedControl.Input option
      CreateEmailProvider: Domain.AccountConfig -> Task<Algebra.EmailProvider> }

type internal DatabaseScope(path: string) =
    let abandoned =
        TaskCompletionSource<unit>(
            TaskCreationOptions.RunContinuationsAsynchronously)
    let database = Database.fromPath path
    let reflow =
        try Database.fromPath path
        with _ ->
            database.dispose ()
            reraise ()

    member _.Database: Algebra.Database = database
    member _.Reflow: Algebra.Database = reflow
    member _.Abandon() : unit = abandoned.TrySetResult(()) |> ignore

    member owner.Complete(outcome: AccessHostResult) : int =
        match outcome.Disposition with
        | Lifetime.Quiesced -> ()
        | Lifetime.Abandoned _ -> owner.Abandon()
        outcome.ExitCode

    interface IDisposable with
        member _.Dispose() =
            if not abandoned.Task.IsCompleted then
                try reflow.dispose ()
                finally database.dispose ()

let private createProducers
    (components: ServiceComponents)
    (access: HostContext)
    : ProducerLoop.Handle =
    let context: ProducerCycle.Context =
        { Fs = components.Fs; Db = components.Db
          Logger = components.Logger; Clock = components.Clock
          Config = components.Config
          CreateEmailProvider = components.CreateEmailProvider }
    ProducerLoop.create
        { InitialDelay = TimeSpan.FromSeconds 5.0
          Interval = TimeSpan.FromMinutes(float components.Config.SyncIntervalMinutes)
          Logger = components.Logger
          Cycle = ProducerCycle.run context }
        access.Cancellation

let private mapDag (components: ServiceComponents) (app: WebApplication) : unit =
    app.MapGet("/api/pipeline/dag", Func<Task<IResult>>(fun () ->
        task {
            let! mermaid = PipelineV5.toMermaid components.Dag components.Db
            return Results.Text(mermaid, "text/plain")
        }))
    |> HttpAccessPolicy.classify HttpAccessClassification.AnonymousArchiveRead
    |> ignore

let private mapApplication
    (components: ServiceComponents)
    (ui: CanonicalUi.Bundle)
    (policy: ServicePolicy.Settings)
    (producers: ProducerLoop.Handle)
    (app: WebApplication)
    : unit =
    CanonicalUi.attach app ui
    ApiServer.mapRoutesWithCapabilities producers.Manual components.Embedder
        app components.Db components.ReflowDb components.Fs components.Logger
        components.Clock components.Dag components.ChatProvider
        components.Config.ArchiveDir components.ConfigDirectory
    mapDag components app
    ApiServer.mapMcpRouteWithPolicy components.Embedder policy.Mcp
        app components.Db components.ReflowDb components.Fs components.Logger
        components.Clock components.Dag components.Config.ArchiveDir
        components.DeepDependencies

let private workers
    (components: ServiceComponents)
    (app: WebApplication)
    (producers: ProducerLoop.Handle)
    : AccessHostWorker list =
    let pipeline: AccessHostWorker =
        { Name = "pipeline-v5"
          Start = fun token ->
              PipelineV5.run components.Dag components.Db components.Logger
                  components.Gpu 200 (TimeSpan.FromMinutes 5.0)
                  (TimeSpan.FromSeconds 10.0) token :> Task }
    let producer: AccessHostWorker =
        { Name = "producers"; Start = fun _ -> producers.Run () }
    let control =
        components.Control
        |> Option.map (fun input ->
            { Name = "owned-stdin"
              Start = fun token ->
                  OwnedControl.run components.Logger input
                      app.Lifetime.StopApplication token })
        |> Option.toList
    pipeline :: producer :: control

let private hostOptions
    (components: ServiceComponents)
    (policy: ServicePolicy.Settings)
    : AccessHostOptions =
    { ConfigDirectory = components.ConfigDirectory
      Port = policy.Port
      ExtraOrigins = policy.ExtraOrigins
      Models =
        { IsAvailable = function
            | ModelOperation.Chat -> Option.isSome components.ChatProvider
            | ModelOperation.Search -> Option.isSome components.Embedder } }

let private serve
    (components: ServiceComponents)
    (ui: CanonicalUi.Bundle)
    (policy: ServicePolicy.Settings)
    : AccessHostResult =
    let configure (app: WebApplication) (access: HostContext) : AccessHostWorkload =
        let producers = createProducers components access
        // The host token owns this registration, including mapping-failure cleanup.
        access.Cancellation.Register(Action(producers.Stop)) |> ignore
        mapApplication components ui policy producers app
        { Workers = workers components app producers
          StopQueues = producers.Stop }
    ServiceAccessHost.run (hostOptions components policy)
        components.Logger configure

let private runService owned args =
    let servicePolicy =
        ServicePolicy.load System.Environment.GetEnvironmentVariable
        |> Result.defaultWith invalidOp
    let ui =
        CanonicalUi.preflight AppContext.BaseDirectory
        |> Result.defaultWith invalidOp
    let control =
        if owned then Some(StdinControl.create ())
        else None
    let fs = Interpreters.realFileSystem
    let env = Interpreters.systemEnvironment
    let clock = Interpreters.systemClock
    let configDir = Config.configDir env
    let configPath = Path.Combine(configDir, "config.yaml")

    // Parse --initial-sync-days N (limits first email sync to N days ago)
    let initialSyncDays =
        args
        |> Array.tryFindIndex (fun a -> a = "--initial-sync-days")
        |> Option.bind (fun i ->
            if i + 1 < args.Length then
                match Int32.TryParse(args.[i + 1]) with
                | true, n when n > 0 -> Some n
                | _ -> None
            else None)

    // Load config
    let config =
        let result = Config.load fs env configPath |> Async.AwaitTask |> Async.RunSynchronously
        match result with
        | Ok c -> c
        | Error _ -> Config.defaultConfig env

    let archiveDir = config.ArchiveDir
    Directory.CreateDirectory(archiveDir) |> ignore
    Directory.CreateDirectory(Path.Combine(archiveDir, "unclassified")) |> ignore

    let logger = Logging.configure configDir LogEventLevel.Information
    let dbPath = Path.Combine(archiveDir, "db.sqlite")
    use databases = new DatabaseScope(dbPath)
    let db = databases.Database
    db.initSchema ()
    |> Async.AwaitTask
    |> Async.RunSynchronously
    |> Lifetime.requireSchema
    Embeddings.initSchema db |> Async.AwaitTask |> Async.RunSynchronously
    let reflowDb = databases.Reflow

    // Seed sync_state for --initial-sync-days (only if account has no existing state)
    match initialSyncDays with
    | Some days ->
        let watermark = DateTimeOffset.UtcNow.AddDays(float -days)
        let wmStr = watermark.ToString("yyyy-MM-dd")
        logger.info $"--initial-sync-days {days}: seeding sync_state with watermark {wmStr}"
        for account in config.Accounts do
            let existing = EmailSync.loadSyncState db account.Label |> Async.AwaitTask |> Async.RunSynchronously
            if existing.IsNone then
                db.execNonQuery
                    """INSERT INTO sync_state (account, last_sync_at, message_count)
                       VALUES (@acc, @ts, 0)
                       ON CONFLICT(account) DO NOTHING"""
                    [ ("@acc", Database.boxVal account.Label)
                      ("@ts", Database.boxVal (watermark.ToString("o"))) ]
                |> Async.AwaitTask |> Async.RunSynchronously |> ignore
                logger.info $"  Seeded {account.Label} -> {wmStr}"
            else
                logger.info $"  {account.Label} already has sync state, skipping"
    | None -> ()

    // Build pipeline deps
    let extractor : Algebra.TextExtractor =
        { extractPdf = fun bytes -> task { return Extraction.extractPdfText bytes }
          extractImage = fun _ -> task { return Error "OCR not configured" } }

    let embedder =
        if config.Ollama.Enabled then
            Some (Embeddings.ollamaClient (new HttpClient()) config.Ollama.BaseUrl config.Ollama.EmbeddingModel 768)
        else None

    let chatProvider =
        try Some (Chat.providerFromConfig (new HttpClient()) config.Chat config.Ollama.BaseUrl config.Ollama.InstructModel)
        with _ -> None

    let triageProvider =
        let triageModel = config.Ollama.TriageModel
        if config.Ollama.Enabled && triageModel <> "" && triageModel <> config.Ollama.InstructModel then
            try
                logger.info $"Triage model: {triageModel} (instruct: {config.Ollama.InstructModel})"
                Some (Chat.ollamaProvider (new HttpClient()) config.Ollama.BaseUrl triageModel)
            with _ -> None
        else None

    let contentRules =
        let rulesPath = Path.Combine(configDir, "rules.yaml")
        if fs.fileExists rulesPath then
            let yaml = fs.readAllText rulesPath |> Async.AwaitTask |> Async.RunSynchronously
            let rules = Rules.parseContentRules yaml
            logger.info $"Loaded {rules.Length} classification rules from {rulesPath}"
            rules
        else []

    let rules = Rules.fromFile fs logger (Path.Combine(configDir, "rules.yaml")) |> Async.AwaitTask |> Async.RunSynchronously

    // Load comprehension prompt from config dir, falling back to bundled copy
    let assemblyDir =
        match Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) with
        | null -> "."
        | dir -> dir
    let comprehensionPrompt =
        PromptLoader.loadWithFallback fs configDir assemblyDir
        |> Async.AwaitTask |> Async.RunSynchronously
        |> function
           | Ok p ->
               logger.info "Loaded comprehension prompt"
               Some p
           | Error e ->
               logger.warn $"Comprehension prompt not loaded: {e} — using fallback"
               None

    let triagePrompt =
        PromptLoader.loadTriagePrompt fs configDir assemblyDir
        |> Async.AwaitTask |> Async.RunSynchronously
        |> function
           | Ok p ->
               logger.info "Loaded triage prompt"
               Some p
           | Error e ->
               logger.warn $"Triage prompt not loaded: {e} — using inline fallback"
               None

    // Load deep extraction prompt registry
    let deepPromptDir = Path.Combine(assemblyDir, "prompts", "deep")
    let deepRegistry =
        DeepExtraction.loadPromptRegistry fs deepPromptDir
        |> Async.AwaitTask |> Async.RunSynchronously
    if deepRegistry.Count > 0 then
        logger.info $"Loaded {deepRegistry.Count} deep extraction prompts"

    // Build deep extraction deps (reuses the same ChatProvider as comprehension)
    let deepDeps : McpTools.DeepExtractionDeps option =
        chatProvider |> Option.map (fun chat ->
            { McpTools.DeepExtractionDeps.Chat = chat
              Registry = deepRegistry
              Provider = if config.Ollama.Enabled then "ollama" else "azure"
              Model = config.Ollama.InstructModel })

    // Build Stages.Deps for v4-compatible stage processors
    let stageDeps : Stages.Deps =
        { Fs = fs; Db = db; Logger = logger; Clock = clock
          Extractor = extractor; Embedder = embedder
          ChatProvider = chatProvider; TriageProvider = triageProvider
          ContentRules = contentRules
          ComprehensionPrompt = comprehensionPrompt
          TriagePrompt = triagePrompt
          Preferences = config.Preferences
          ArchiveDir = archiveDir }

    let createEmailProvider cfgDir (account: Domain.AccountConfig) =
        task {
            match account.Provider with
            | "outlook"
            | "microsoft" ->
                return!
                    OutlookProvider.create
                        account.ClientId
                        account.TenantId
                        account.RedirectPort
                        (Path.Combine(cfgDir, "tokens"))
                        account.Label
                        logger
            | _ ->
                let credPath =
                    Config.resolveCredentials fs env config.Credentials

                let! credBytes = fs.readAllBytes credPath
                let tokenDir = Path.Combine(cfgDir, "tokens")
                return! GmailProvider.create credBytes tokenDir account.Label logger
        }

    // ── Pipeline v5: DAG-based with phase scheduling ──
    let v5Stages = StagesV5.standardStages stageDeps
    PipelineV5.initSchema db v5Stages |> Async.AwaitTask |> Async.RunSynchronously
    let dag =
        match PipelineV5.buildDag v5Stages with
        | Ok d ->
            logger.info $"Pipeline v5 DAG: {d.Order.Length} stages, {d.Phases.Length} phases"
            d
        | Error e ->
            failwith $"Pipeline DAG error: {e}"
    let gpu = PipelineV5.createGpuScheduler logger

    // Recovery is synchronous so no API or worker can observe stale leases.
    let recovered =
        PipelineV5.recoverStaleAttempts db v5Stages
        |> Async.AwaitTask
        |> Async.RunSynchronously
    if recovered > 0 then
        logger.warn
            $"Recovered {recovered} stale pipeline stage attempt lease(s)"

    let components: ServiceComponents =
        { Db = db
          ReflowDb = reflowDb
          Fs = fs
          Logger = logger
          Clock = clock
          Config = config
          ConfigDirectory = configDir
          Dag = dag
          Gpu = gpu
          Embedder = embedder
          ChatProvider = chatProvider
          DeepDependencies = deepDeps
          Control = control
          CreateEmailProvider = createEmailProvider configDir }

    logger.info
        $"Hermes service starting on http://127.0.0.1:{servicePolicy.Port}"
    let outcome = serve components ui servicePolicy
    databases.Complete(outcome)

let private validateInvocation (invocation: OwnedControl.Invocation) =
    match ReindexCommand.route invocation.Arguments with
    | ReindexCommand.Service -> Ok invocation
    | _ when invocation.Owned ->
        Error "--control-stdin is valid only for an owned service child"
    | _ -> Ok invocation

let private runApplication args =
    OwnedControl.parse args
    |> Result.bind validateInvocation
    |> Result.map (fun invocation ->
        ServiceEntry.run invocation.Arguments (runService invocation.Owned))
    |> Result.defaultWith (fun error ->
        Console.Error.WriteLine(error: string)
        2)

[<EntryPoint>]
let main args =
    StartupAdmission.run
        (fun () -> fun arguments -> OwnerUiCommand.run arguments runApplication)
        args
