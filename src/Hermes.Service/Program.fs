module Hermes.Service.Program

open System
open System.IO
open System.Net.Http
open System.Threading
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.FSharp.Core
open Hermes.Core
open Serilog.Events

[<EntryPoint>]
let main _args =
    let fs = Interpreters.realFileSystem
    let env = Interpreters.systemEnvironment
    let clock = Interpreters.systemClock
    let configDir = Config.configDir env
    let configPath = Path.Combine(configDir, "config.yaml")

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

    let contentRules =
        let rulesPath = Path.Combine(configDir, "rules.yaml")
        if fs.fileExists rulesPath then
            let yaml = fs.readAllText rulesPath |> Async.AwaitTask |> Async.RunSynchronously
            Rules.parseContentRules yaml
        else []

    let rules = Rules.fromFile fs logger (Path.Combine(configDir, "rules.yaml")) |> Async.AwaitTask |> Async.RunSynchronously

    let deps : ServiceHost.SyncDeps =
        { Extractor = extractor
          Embedder = embedder
          ChatProvider = chatProvider
          ContentRules = contentRules
          CreateEmailProvider = fun cfgDir label ->
            task {
                let credPath = Path.Combine(cfgDir, "gmail_credentials.json")
                let! credBytes = fs.readAllBytes credPath
                let tokenDir = Path.Combine(cfgDir, "tokens")
                return! GmailProvider.create credBytes tokenDir label logger
            } }

    let serviceConfig = ServiceHost.defaultServiceConfig config
    let observer = PipelineObserver.empty ()

    // Start pipeline in background
    use cts = new CancellationTokenSource()
    let _ = System.Threading.Tasks.Task.Run(fun () ->
        task {
            do! ServiceHost.createServiceHost fs db logger clock env rules deps serviceConfig configPath cts.Token SleepGuard.preventSleep SleepGuard.allowSleep
        } :> System.Threading.Tasks.Task)

    // Build HTTP API
    let builder = WebApplication.CreateBuilder()
    builder.Services.AddCors() |> ignore
    // Ensure web root points to wwwroot (React build output)
    let wwwrootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
    let srcWwwroot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "wwwroot"))
    if Directory.Exists(srcWwwroot) then builder.Environment.WebRootPath <- srcWwwroot
    elif Directory.Exists(wwwrootPath) then builder.Environment.WebRootPath <- wwwrootPath
    let app = builder.Build()

    // CORS for Vite dev server
    app.UseCors(fun policy ->
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader() |> ignore) |> ignore

    // Serve React static files from wwwroot/ (default Web SDK convention)
    app.UseDefaultFiles() |> ignore
    app.UseStaticFiles() |> ignore

    // Map API routes
    ApiServer.mapRoutes app db fs logger clock observer chatProvider archiveDir configDir

    // SPA fallback: serve index.html for non-API, non-file routes
    app.MapFallbackToFile("index.html") |> ignore

    logger.info "Hermes service starting on http://localhost:21741"
    app.Run("http://localhost:21741")

    cts.Cancel()
    db.dispose ()
    0
