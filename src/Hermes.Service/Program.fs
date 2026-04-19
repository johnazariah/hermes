module Hermes.Service.Program

open System
open System.IO
open System.Net.Http
open System.Threading
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Hermes.Core
open Serilog.Events

[<EntryPoint>]
let main args =
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
    let db = Database.fromPath (Path.Combine(archiveDir, "db.sqlite"))
    let _ = db.initSchema () |> Async.AwaitTask |> Async.RunSynchronously
    Embeddings.initSchema db |> Async.AwaitTask |> Async.RunSynchronously

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

    let deps : Pipeline.Deps =
        { Extractor = extractor
          Embedder = embedder
          ChatProvider = chatProvider
          TriageProvider = triageProvider
          ContentRules = contentRules
          ComprehensionPrompt = comprehensionPrompt
          CreateEmailProvider = fun cfgDir label ->
            task {
                let credPath = Config.resolveCredentials fs env config.Credentials
                let! credBytes = fs.readAllBytes credPath
                let tokenDir = Path.Combine(cfgDir, "tokens")
                return! GmailProvider.create credBytes tokenDir label logger
            } }

    // Start pipeline in background
    use cts = new CancellationTokenSource()
    let _ = System.Threading.Tasks.Task.Run(fun () ->
        task {
            do! Pipeline.start fs db logger clock rules deps config configDir cts.Token
        } :> System.Threading.Tasks.Task)

    // Build HTTP API
    let builder = WebApplication.CreateBuilder()
    builder.Services.AddCors() |> ignore

    // Blazor Server services
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents() |> ignore
    builder.Services.AddScoped<Hermes.UI.Services.IHermesClient>(fun sp ->
        let http = new System.Net.Http.HttpClient()
        let blazorPort =
            match System.Environment.GetEnvironmentVariable("HERMES_PORT") with
            | null | "" -> "21741"
            | p -> p
        http.BaseAddress <- System.Uri($"http://localhost:{blazorPort}")
        Hermes.UI.Services.HttpHermesClient(http) :> Hermes.UI.Services.IHermesClient) |> ignore

    let wwwrootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
    let srcWwwroot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "wwwroot"))
    if Directory.Exists(srcWwwroot) then builder.Environment.WebRootPath <- srcWwwroot
    elif Directory.Exists(wwwrootPath) then builder.Environment.WebRootPath <- wwwrootPath
    let app = builder.Build()

    app.UseCors(fun policy ->
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader() |> ignore) |> ignore
    app.UseDefaultFiles() |> ignore
    app.UseStaticFiles() |> ignore
    app.UseAntiforgery() |> ignore

    // Map API routes
    ApiServer.mapRoutes app db fs logger clock chatProvider archiveDir configDir

    // Health endpoint for tray app / orchestrator polling
    app.MapGet("/health", Func<IResult>(fun () ->
        Results.Json({| status = "healthy"; service = "hermes" |}))) |> ignore

    // MCP endpoint — Streamable HTTP (JSON-RPC over POST)
    app.MapPost("/mcp", Func<HttpContext, System.Threading.Tasks.Task<IResult>>(fun ctx ->
        task {
            use reader = new IO.StreamReader(ctx.Request.Body)
            let! body = reader.ReadToEndAsync()
            let! response = McpServer.processMessage db fs logger clock archiveDir deepDeps body
            return Results.Text(response, "application/json")
        })) |> ignore

    // Blazor Server — serves the UI at /
    app.MapRazorComponents<Hermes.UI.App>()
        .AddInteractiveServerRenderMode() |> ignore

    let port =
        match System.Environment.GetEnvironmentVariable("HERMES_PORT") with
        | null | "" -> "21741"
        | p -> p
    logger.info $"Hermes service starting on http://localhost:{port}"
    app.Run($"http://localhost:{port}")
    cts.Cancel()
    db.dispose ()
    0
