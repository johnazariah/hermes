module Hermes.Cli.Program

open System
open System.IO
open System.Threading
open Argu
open Hermes.Core

type InitArgs =
    | [<Hidden>] Placeholder
    interface IArgParserTemplate with
        member _.Usage = ""

type SearchArgs =
    | [<MainCommand>] Query of string
    | [<AltCommandLine("-s")>] Semantic
    | [<AltCommandLine("-H")>] Hybrid
    | [<AltCommandLine("-c")>] Category of string
    | Sender of string
    | From of string
    | To of string
    | Account of string
    | [<AltCommandLine("-n")>] Limit of int
    | Json
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Query _ -> "search query"
            | Semantic -> "use semantic (embedding) search"
            | Hybrid -> "combine keyword + semantic search"
            | Category _ -> "filter by category"
            | Sender _ -> "filter by sender"
            | From _ -> "filter results from date (YYYY-MM-DD)"
            | To _ -> "filter results to date (YYYY-MM-DD)"
            | Account _ -> "filter by email account"
            | Limit _ -> "max results (default 20)"
            | Json -> "output results as JSON"

type SyncArgs =
    | [<Hidden>] Placeholder
    interface IArgParserTemplate with
        member _.Usage = ""

type ReconcileArgs =
    | [<AltCommandLine("-n")>] Dry_Run
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Dry_Run -> "show what would be done without making changes"

type SuggestRulesArgs =
    | [<Hidden>] Placeholder
    interface IArgParserTemplate with
        member _.Usage = ""

type EmbedArgs =
    | [<AltCommandLine("-f")>] Force
    | [<AltCommandLine("-n")>] Limit of int
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Force -> "re-embed documents that already have embeddings"
            | Limit _ -> "max documents to embed"

type McpArgs =
    | [<Hidden>] Placeholder
    interface IArgParserTemplate with
        member _.Usage = ""

type ServiceArgs =
    | [<CliPrefix(CliPrefix.None)>] Install
    | [<CliPrefix(CliPrefix.None)>] Uninstall
    | [<CliPrefix(CliPrefix.None)>] Start
    | [<CliPrefix(CliPrefix.None)>] Stop
    | [<CliPrefix(CliPrefix.None)>] Status
    | [<CliPrefix(CliPrefix.None)>] Run
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Install -> "register for auto-start on login"
            | Uninstall -> "remove auto-start registration"
            | Start -> "start the background service"
            | Stop -> "stop the background service"
            | Status -> "show service status"
            | Run -> "run in foreground with console logging"

type BackfillArgs =
    | [<AltCommandLine("-a")>] Account of string
    | Reset
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Account _ -> "account label"
            | Reset -> "reset backfill progress (re-scan from start)"

type ReextractArgs =
    | [<AltCommandLine("-c")>] Category of string
    | [<AltCommandLine("-n")>] Limit of int
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Category _ -> "only reextract documents in this category"
            | Limit _ -> "max documents to reextract (default: all)"

type ReclassifyArgs =
    | [<AltCommandLine("-n")>] Limit of int
    | No_Llm
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Limit _ -> "max documents to reclassify (default: 50)"
            | No_Llm -> "skip Tier 3 LLM classification"

[<RequireSubcommand>]
type CliArgs =
    | Version
    | [<CliPrefix(CliPrefix.None)>] Init of ParseResults<InitArgs>
    | [<CliPrefix(CliPrefix.None)>] Search of ParseResults<SearchArgs>
    | [<CliPrefix(CliPrefix.None)>] Sync of ParseResults<SyncArgs>
    | [<CliPrefix(CliPrefix.None)>] Reconcile of ParseResults<ReconcileArgs>
    | [<CliPrefix(CliPrefix.None)>] Suggest_Rules of ParseResults<SuggestRulesArgs>
    | [<CliPrefix(CliPrefix.None)>] Embed of ParseResults<EmbedArgs>
    | [<CliPrefix(CliPrefix.None)>] Mcp of ParseResults<McpArgs>
    | [<CliPrefix(CliPrefix.None)>] Service of ParseResults<ServiceArgs>
    | [<CliPrefix(CliPrefix.None)>] Backfill of ParseResults<BackfillArgs>
    | [<CliPrefix(CliPrefix.None)>] Reextract of ParseResults<ReextractArgs>
    | [<CliPrefix(CliPrefix.None)>] Reclassify of ParseResults<ReclassifyArgs>
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Version -> "print version and exit"
            | Init _ -> "initialise config, rules, and database"
            | Search _ -> "search documents"
            | Sync _ -> "sync email accounts (not yet implemented)"
            | Reconcile _ -> "walk archive, find moved/deleted/new files"
            | Suggest_Rules _ -> "analyse unsorted patterns and suggest rules"
            | Embed _ -> "generate embeddings for documents"
            | Mcp _ -> "start MCP server (JSON-RPC over stdio)"
            | Service _ -> "manage the background service"
            | Backfill _ -> "manage email backfill (reset progress)"
            | Reextract _ -> "re-extract documents (force refresh of extracted text and markdown)"
            | Reclassify _ -> "reclassify unsorted documents using content rules and LLM"

let private version () =
    let asm = System.Reflection.Assembly.GetEntryAssembly()
    match asm with
    | null -> printfn "hermes 0.0.0"; 0
    | a ->
        let ver = a.GetName().Version
        printfn $"hermes {ver}"
        0

let private initCmd () =
    let fs = Interpreters.realFileSystem
    let env = Interpreters.systemEnvironment
    let logger = Logging.configureDefault ()
    logger.info "Initialising Hermes..."
    let configResult = Config.init fs env |> Async.AwaitTask |> Async.RunSynchronously
    match configResult with
    | Error e ->
        logger.error $"Config init failed: {e}"
        1
    | Ok created ->
        for path in created do
            logger.info $"Created: {path}"
        let configPath = Path.Combine(Config.configDir env, "config.yaml")
        let loadResult = Config.load fs env configPath |> Async.AwaitTask |> Async.RunSynchronously
        match loadResult with
        | Error e ->
            logger.error $"Failed to load config: {e}"
            1
        | Ok config ->
            let archiveResult =
                Database.initArchive fs config.ArchiveDir
                |> Async.AwaitTask
                |> Async.RunSynchronously
            match archiveResult with
            | Error e ->
                logger.error $"Archive init failed: {e}"
                1
            | Ok db ->
                db.dispose ()
                logger.info $"Archive initialised at: {config.ArchiveDir}"
                logger.info "Hermes is ready."
                0

let private loadConfigAndDb () =
    let fs = Interpreters.realFileSystem
    let env = Interpreters.systemEnvironment
    let logger = Logging.configureDefault ()
    let configPath = Path.Combine(Config.configDir env, "config.yaml")
    let loadResult = Config.load fs env configPath |> Async.AwaitTask |> Async.RunSynchronously
    match loadResult with
    | Error e ->
        logger.error $"Failed to load config: {e}"
        None
    | Ok config ->
        let dbPath = Path.Combine(config.ArchiveDir, "db.sqlite")
        if not (File.Exists(dbPath)) then
            logger.error "Database not found. Run 'hermes init' first."
            None
        else
            let db = Database.fromPath dbPath
            Some(fs, logger, config, db)

let private reconcileCmd (args: ParseResults<ReconcileArgs>) =
    let dryRun = args.Contains Dry_Run
    match loadConfigAndDb () with
    | None -> 1
    | Some(fs, logger, config, db) ->
        try
            let actions =
                Classifier.reconcile fs db logger config.ArchiveDir dryRun
                |> Async.AwaitTask
                |> Async.RunSynchronously
            if actions.IsEmpty then
                logger.info "Archive is in sync - no discrepancies found."
            else
                for action in actions do
                    match action with
                    | Classifier.NewOnDisk(path, cat) ->
                        let prefix = if dryRun then "[DRY-RUN] " else ""
                        printfn $"{prefix}New file on disk (not in DB): {path} (category: {cat})"
                    | Classifier.MissingFromDisk(path, docId) ->
                        let prefix = if dryRun then "[DRY-RUN] " else ""
                        printfn $"{prefix}Missing from disk: {path} (doc ID: {docId})"
                    | Classifier.MovedOnDisk(path, docId) ->
                        let prefix = if dryRun then "[DRY-RUN] " else ""
                        printfn $"{prefix}Moved on disk: {path} (doc ID: {docId})"
                printfn $"Total discrepancies: {actions.Length}"
            0
        finally
            db.dispose ()

let private suggestRulesCmd () =
    match loadConfigAndDb () with
    | None -> 1
    | Some(fs, logger, config, db) ->
        try
            let suggestions =
                Classifier.suggestRules fs db logger config.ArchiveDir
                |> Async.AwaitTask
                |> Async.RunSynchronously
            if suggestions.IsEmpty then
                printfn "No rule suggestions found."
            else
                printfn "Suggested rules (add to rules.yaml):"
                printfn ""
                for s in suggestions do
                    printfn $"  - name: {s.SuggestedName}"
                    printfn $"    match:"
                    printfn $"      {s.MatchType}: \"{s.Pattern}\""
                    printfn $"    category: {s.Category}"
                    printfn $"    # based on: {s.ExampleFile}"
                    printfn ""
            0
        finally
            db.dispose ()

let private searchCmd (args: ParseResults<SearchArgs>) =
    match args.TryGetResult Query with
    | None ->
        printfn "Usage: hermes search <query>"
        1
    | Some query ->
        match loadConfigAndDb () with
        | None -> 1
        | Some(_fs, logger, config, db) ->
            try
                if args.Contains Hybrid || args.Contains Semantic then
                    // Semantic / hybrid mode via embeddings
                    let mode =
                        if args.Contains Hybrid then SemanticSearch.Hybrid
                        else SemanticSearch.Semantic

                    let limit = args.TryGetResult SearchArgs.Limit |> Option.defaultValue 10

                    let client =
                        Embeddings.ollamaClient
                            (new System.Net.Http.HttpClient())
                            config.Ollama.BaseUrl
                            config.Ollama.EmbeddingModel
                            768

                    let results =
                        SemanticSearch.search db client logger mode query limit
                        |> Async.AwaitTask
                        |> Async.RunSynchronously

                    if results.IsEmpty then
                        printfn "No results found."
                    else
                        for r in results do
                            printfn $"[{r.Score:F3}] {r.Title} ({r.Category})"
                            if r.Snippet.Length > 0 then
                                printfn $"  {r.Snippet}"
                            printfn ""
                    0
                else
                    // Default: FTS5 keyword search
                    let filter : Search.SearchFilter =
                        { Query = query
                          Category = args.TryGetResult SearchArgs.Category
                          Sender = args.TryGetResult SearchArgs.Sender
                          DateFrom = args.TryGetResult SearchArgs.From
                          DateTo = args.TryGetResult SearchArgs.To
                          Account = args.TryGetResult SearchArgs.Account
                          SourceType = None
                          Limit = args.TryGetResult SearchArgs.Limit |> Option.defaultValue 20 }

                    let results =
                        Search.execute db filter
                        |> Async.AwaitTask
                        |> Async.RunSynchronously

                    if args.Contains Json then
                        let jsonItems =
                            results
                            |> List.map (fun r ->
                                let dict = System.Collections.Generic.Dictionary<string, obj>()
                                dict.["id"] <- Database.boxVal r.DocumentId
                                dict.["path"] <- Database.boxVal r.SavedPath
                                dict.["category"] <- Database.boxVal r.Category
                                dict.["score"] <- Database.boxVal r.RelevanceScore
                                r.OriginalName |> Option.iter (fun v -> dict.["originalName"] <- Database.boxVal v)
                                r.Sender |> Option.iter (fun v -> dict.["sender"] <- Database.boxVal v)
                                r.Subject |> Option.iter (fun v -> dict.["subject"] <- Database.boxVal v)
                                r.EmailDate |> Option.iter (fun v -> dict.["emailDate"] <- Database.boxVal v)
                                r.ExtractedVendor |> Option.iter (fun v -> dict.["vendor"] <- Database.boxVal v)
                                r.ExtractedAmount |> Option.iter (fun v -> dict.["amount"] <- Database.boxVal v)
                                r.Snippet |> Option.iter (fun v -> dict.["snippet"] <- Database.boxVal v)
                                dict)

                        let options = System.Text.Json.JsonSerializerOptions(WriteIndented = true)
                        printfn "%s" (System.Text.Json.JsonSerializer.Serialize(jsonItems, options))
                    else
                        if results.IsEmpty then
                            printfn "No results found."
                        else
                            printfn "%-6s %-20s %-20s %-30s %s" "ID" "Category" "Sender" "Subject" "Score"
                            printfn "%s" (String.replicate 85 "-")
                            for r in results do
                                let sender =
                                    r.Sender
                                    |> Option.defaultValue "-"
                                    |> fun s -> if s.Length > 20 then s.[..19] else s
                                let subject =
                                    r.Subject
                                    |> Option.defaultValue "-"
                                    |> fun s -> if s.Length > 30 then s.[..29] else s
                                printfn "%-6d %-20s %-20s %-30s %.4f"
                                    r.DocumentId r.Category sender subject r.RelevanceScore
                            printfn ""
                            printfn $"{results.Length} result(s) found."
                    0
            finally
                db.dispose ()

let private embedCmd (args: ParseResults<EmbedArgs>) =
    match loadConfigAndDb () with
    | None -> 1
    | Some(_fs, logger, config, db) ->
        try
            let force = args.Contains Force
            let limit = args.TryGetResult EmbedArgs.Limit

            let client =
                Embeddings.ollamaClient
                    (new System.Net.Http.HttpClient())
                    config.Ollama.BaseUrl
                    config.Ollama.EmbeddingModel
                    768

            let progress : Embeddings.ProgressCallback =
                fun completed total ->
                    printf $"\rEmbedding: {completed}/{total}"

            let result =
                Embeddings.batchEmbed db logger Interpreters.systemClock client force limit (Some progress)
                |> Async.AwaitTask
                |> Async.RunSynchronously

            printfn ""

            match result with
            | Ok count ->
                printfn $"Done: {count} documents embedded."
                0
            | Error e ->
                logger.error $"Embedding failed: {e}"
                1
        finally
            db.dispose ()

let private notImplemented (name: string) =
    printfn $"hermes {name}: not yet implemented"
    0

let private reextractCmd (args: ParseResults<ReextractArgs>) =
    match loadConfigAndDb () with
    | None -> 1
    | Some(fs, logger, config, db) ->
        try
            let category = args.TryGetResult ReextractArgs.Category
            let limit = args.TryGetResult ReextractArgs.Limit |> Option.defaultValue 10000
            let extractor = Interpreters.nullTextExtractor
            let clock = Interpreters.systemClock
            let catLabel = category |> Option.defaultValue "all"
            printfn $"Re-extracting documents (category={catLabel}, limit={limit})..."
            let succeeded, failed =
                Extraction.extractBatch fs db logger clock extractor config.ArchiveDir category true limit
                |> Async.AwaitTask
                |> Async.RunSynchronously
            printfn $"Done: {succeeded} succeeded, {failed} failed."
            0
        finally
            db.dispose ()

let private reclassifyCmd (args: ParseResults<ReclassifyArgs>) =
    match loadConfigAndDb () with
    | None -> 1
    | Some(fs, logger, config, db) ->
        try
            let env = Interpreters.systemEnvironment
            let limit = args.TryGetResult ReclassifyArgs.Limit |> Option.defaultValue 50
            let noLlm = args.Contains No_Llm
            let configDir = Config.configDir env

            // Load content rules
            let rulesPath = Path.Combine(configDir, "rules.yaml")
            let contentRules =
                if fs.fileExists rulesPath then
                    let yaml = (fs.readAllText rulesPath).Result
                    Rules.parseContentRules yaml
                else []

            printfn $"Reclassifying unsorted documents (limit={limit}, content_rules={contentRules.Length}, llm={not noLlm})..."

            // Tier 2: content rules
            let reclassified, remaining =
                Classifier.reclassifyUnsortedBatch db fs contentRules config.ArchiveDir limit
                |> Async.AwaitTask
                |> Async.RunSynchronously
            printfn $"Tier 2 (content rules): {reclassified} reclassified, {remaining} still unsorted."

            // Tier 3: LLM (if not disabled)
            if not noLlm && remaining > 0 then
                try
                    let chatProvider =
                        Chat.providerFromConfig
                            (new System.Net.Http.HttpClient())
                            config.Chat config.Ollama.BaseUrl config.Ollama.InstructModel
                    let llmLimit = min (limit - reclassified) 10
                    if llmLimit > 0 then
                        printfn $"Tier 3 (LLM): classifying up to {llmLimit} remaining documents..."
                        let catRows =
                            db.execReader "SELECT DISTINCT category FROM documents WHERE category NOT IN ('unsorted','unclassified')" []
                            |> Async.AwaitTask |> Async.RunSynchronously
                        let categories =
                            catRows |> List.choose (fun r -> Prelude.RowReader(r).OptString "category")
                        let mutable llmClassified = 0
                        let unsortedRows =
                            db.execReader
                                """SELECT id, extracted_text FROM documents
                                   WHERE (category = 'unsorted' OR category = 'unclassified')
                                     AND extracted_text IS NOT NULL
                                   ORDER BY id ASC LIMIT @lim"""
                                [ ("@lim", Database.boxVal (int64 llmLimit)) ]
                            |> Async.AwaitTask |> Async.RunSynchronously
                        for row in unsortedRows do
                            let r = Prelude.RowReader(row)
                            match r.OptInt64 "id", r.OptString "extracted_text" with
                            | Some docId, Some text ->
                                let prompt = ContentClassifier.buildClassificationPrompt text categories
                                let llmResult =
                                    chatProvider.complete "You are a document classifier." prompt
                                    |> Async.AwaitTask |> Async.RunSynchronously
                                match llmResult with
                                | Ok response ->
                                    match ContentClassifier.parseClassificationResponse response with
                                    | Some (cat, conf, reasoning) when conf >= 0.4 && categories |> List.contains cat ->
                                        let moveResult =
                                            DocumentManagement.reclassify db fs config.ArchiveDir docId cat
                                            |> Async.AwaitTask |> Async.RunSynchronously
                                        match moveResult with
                                        | Ok () ->
                                            let tier = if conf >= 0.7 then "llm" else "llm_review"
                                            db.execNonQuery
                                                """UPDATE documents SET classification_tier = @tier,
                                                   classification_confidence = @conf WHERE id = @id"""
                                                [ ("@tier", Database.boxVal tier)
                                                  ("@conf", Database.boxVal conf)
                                                  ("@id", Database.boxVal docId) ]
                                            |> Async.AwaitTask |> Async.RunSynchronously |> ignore
                                            llmClassified <- llmClassified + 1
                                            printfn $"  doc {docId} → {cat} (LLM {conf:F0}%%: {reasoning})"
                                        | Error e -> printfn $"  doc {docId}: move failed — {e}"
                                    | _ -> ()
                                | Error e -> printfn $"  LLM error: {e}"
                            | _ -> ()
                        printfn $"Tier 3 (LLM): {llmClassified} reclassified."
                with ex ->
                    printfn $"LLM classification unavailable: {ex.Message}"
            printfn "Done."
            0
        finally
            db.dispose ()

let private mcpCmd () =
    match loadConfigAndDb () with
    | None -> 1
    | Some(fs, logger, config, db) ->
        try
            logger.info "MCP server starting (stdio mode)..."
            let clock = Interpreters.systemClock
            let mutable running = true

            while running do
                let line = Console.ReadLine()

                match line with
                | null -> running <- false
                | "" -> ()
                | msg ->
                    let response =
                        McpServer.processMessage db fs logger clock config.ArchiveDir msg
                        |> Async.AwaitTask
                        |> Async.RunSynchronously

                    Console.Out.WriteLine(response)
                    Console.Out.Flush()

            logger.info "MCP server shutting down."
            0
        finally
            db.dispose ()

let private serviceCmd (args: ParseResults<ServiceArgs>) =
    if args.Contains ServiceArgs.Install then
        let fs = Interpreters.realFileSystem
        let logger = Logging.configureDefault ()
        let result =
            ServiceInstaller.install fs logger
            |> Async.AwaitTask
            |> Async.RunSynchronously
        printfn "%s" (ServiceInstaller.formatResult result)
        match result with
        | ServiceInstaller.Installed | ServiceInstaller.AlreadyInstalled -> 0
        | _ -> 1
    elif args.Contains ServiceArgs.Uninstall then
        let fs = Interpreters.realFileSystem
        let logger = Logging.configureDefault ()
        let result =
            ServiceInstaller.uninstall fs logger
            |> Async.AwaitTask
            |> Async.RunSynchronously
        printfn "%s" (ServiceInstaller.formatResult result)
        match result with
        | ServiceInstaller.Uninstalled | ServiceInstaller.NotInstalled -> 0
        | _ -> 1
    elif args.Contains ServiceArgs.Start then
        let logger = Logging.configureDefault ()
        let result =
            ServiceInstaller.start logger
            |> Async.AwaitTask
            |> Async.RunSynchronously
        printfn "%s" (ServiceInstaller.formatResult result)
        match result with
        | ServiceInstaller.Started | ServiceInstaller.AlreadyRunning -> 0
        | _ -> 1
    elif args.Contains ServiceArgs.Stop then
        let logger = Logging.configureDefault ()
        let result =
            ServiceInstaller.stop logger
            |> Async.AwaitTask
            |> Async.RunSynchronously
        printfn "%s" (ServiceInstaller.formatResult result)
        match result with
        | ServiceInstaller.Stopped | ServiceInstaller.NotRunning -> 0
        | _ -> 1
    elif args.Contains ServiceArgs.Status then
        match loadConfigAndDb () with
        | None ->
            // Fall back to platform status if config/db unavailable
            let logger = Logging.configureDefault ()
            let platformResult =
                ServiceInstaller.status logger
                |> Async.AwaitTask
                |> Async.RunSynchronously
            printfn "%s" (ServiceInstaller.formatResult platformResult)
            0
        | Some(fs, logger, config, db) ->
            try
                // Show heartbeat status
                let heartbeat =
                    ServiceHost.readHeartbeat fs config.ArchiveDir
                    |> Async.AwaitTask
                    |> Async.RunSynchronously

                match heartbeat with
                | Some status ->
                    let state = if status.Running then "running" else "stopped"
                    printfn $"Service: {state}"
                    status.StartedAt |> Option.iter (fun t -> printfn $"Started at: {t:u}")
                    status.LastSyncAt |> Option.iter (fun t ->
                        let syncState = if status.LastSyncOk then "ok" else "error"
                        printfn $"Last sync: {t:u} ({syncState})")
                    printfn $"Documents: {status.DocumentCount}"
                    printfn $"Unclassified: {status.UnclassifiedCount}"
                    status.ErrorMessage |> Option.iter (fun e -> printfn $"Last error: {e}")
                | None ->
                    printfn "Service: no status file found (not running or never started)"

                // Also show platform status
                let platformResult =
                    ServiceInstaller.status logger
                    |> Async.AwaitTask
                    |> Async.RunSynchronously
                printfn $"Platform: {ServiceInstaller.formatResult platformResult}"
                0
            finally
                db.dispose ()
    elif args.Contains ServiceArgs.Run then
        match loadConfigAndDb () with
        | None -> 1
        | Some(fs, logger, config, db) ->
            try
                let clock = Interpreters.systemClock
                let env = Interpreters.systemEnvironment
                let rulesPath = Path.Combine(Config.configDir env, "rules.yaml")
                let rules = Rules.fromFile fs logger rulesPath |> Async.AwaitTask |> Async.RunSynchronously

                let serviceConfig = ServiceHost.defaultServiceConfig config
                let cfgPath = Path.Combine(Config.configDir env, "config.yaml")
                let configDir = Config.configDir env
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
                let deps : ServiceHost.SyncDeps =
                    { Extractor = extractor
                      Embedder = embedder
                      ChatProvider = chatProvider
                      ContentRules = contentRules
                      CreateEmailProvider = fun cfgDir label ->
                        task {
                            let credPath = IO.Path.Combine(cfgDir, "gmail_credentials.json")
                            let! credBytes = fs.readAllBytes credPath
                            let tokenDir = IO.Path.Combine(cfgDir, "tokens")
                            return! GmailProvider.create credBytes tokenDir label logger
                        } }

                use cts = new CancellationTokenSource()

                Console.CancelKeyPress.Add(fun e ->
                    e.Cancel <- true
                    logger.info "Ctrl+C received, shutting down..."
                    cts.Cancel())

                ServiceHost.createServiceHost fs db logger clock env rules deps serviceConfig cfgPath cts.Token
                |> Async.AwaitTask
                |> Async.RunSynchronously
                0
            finally
                db.dispose ()
    else
        let parser = ArgumentParser.Create<ServiceArgs>(programName = "hermes service")
        printfn "%s" (parser.PrintUsage())
        0

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<CliArgs>(programName = "hermes")
    if argv |> Array.exists (fun a -> a = "--version" || a = "-v") then
        version ()
    else
    try
        let results = parser.ParseCommandLine(inputs = argv, raiseOnUsage = true)
        if results.Contains Version then
            version ()
        elif results.Contains Init then
            initCmd ()
        elif results.Contains Search then
            searchCmd (results.GetResult Search)
        elif results.Contains Sync then
            notImplemented "sync"
        elif results.Contains Reconcile then
            reconcileCmd (results.GetResult Reconcile)
        elif results.Contains Suggest_Rules then
            suggestRulesCmd ()
        elif results.Contains Embed then
            embedCmd (results.GetResult Embed)
        elif results.Contains Mcp then
            mcpCmd ()
        elif results.Contains Service then
            serviceCmd (results.GetResult Service)
        elif results.Contains Backfill then
            let args = results.GetResult Backfill
            if args.Contains <@ BackfillArgs.Reset @> then
                let label = args.TryGetResult <@ BackfillArgs.Account @>
                match label with
                | None ->
                    eprintfn "Error: --account is required for backfill reset"
                    1
                | Some account ->
                    match loadConfigAndDb () with
                    | None -> 1
                    | Some(_, logger, _, db) ->
                        try
                            db.execNonQuery
                                """UPDATE sync_state
                                   SET backfill_page_token = NULL, backfill_scanned = 0,
                                       backfill_completed = 0, backfill_started_at = NULL
                                   WHERE account = @acc"""
                                [ ("@acc", Database.boxVal account) ]
                            |> Async.AwaitTask |> Async.RunSynchronously |> ignore
                            printfn $"Backfill reset for account '{account}'. Will restart on next sync cycle."
                            0
                        finally
                            db.dispose ()
            else
                printfn "Usage: hermes backfill --reset --account <label>"
                0
        elif results.Contains Reextract then
            reextractCmd (results.GetResult Reextract)
        elif results.Contains Reclassify then
            reclassifyCmd (results.GetResult Reclassify)
        else
            printfn "%s" (parser.PrintUsage())
            0
    with
    | :? ArguParseException as ex ->
        printfn "%s" ex.Message
        if ex.ErrorCode = ErrorCode.HelpText then 0 else 1
    | ex ->
        eprintfn $"Error: {ex.Message}"
        1
