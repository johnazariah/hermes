namespace Hermes.Core

open System
open System.Threading
open System.Threading.Tasks

/// Pipeline orchestrator: creates stage queues, launches producers and processors.
/// No channels, no recovery logic — stage queue tables are the source of truth.
[<RequireQualifiedAccess>]
module Pipeline =

    /// Dependencies injected from the composition root.
    type Deps =
        { Extractor: Algebra.TextExtractor
          Embedder: Algebra.EmbeddingClient option
          ChatProvider: Algebra.ChatProvider option
          ContentRules: Domain.ContentRule list
          CreateEmailProvider: string -> string -> Task<Algebra.EmailProvider> }

    /// Start the pipeline: producers + stage processors + dashboard.
    let start
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (rules: Algebra.RulesEngine) (deps: Deps)
        (config: Domain.HermesConfig) (configDir: string)
        (ct: CancellationToken) : Task<unit> =
        task {
            // Stage queues (SQLite-backed, typed per stage)
            let extractQ = StageProcessors.extractQueue db
            let classifyQ = StageProcessors.classifyQueue db
            let embedQ = StageProcessors.embedQueue db

            let archiveDir = config.ArchiveDir
            let extractConcurrency =
                if config.Pipeline.ExtractConcurrency > 0 then config.Pipeline.ExtractConcurrency
                else max 1 (Environment.ProcessorCount / 2)
            let llmConcurrency = max 1 config.Pipeline.LlmConcurrency
            let emailConcurrency = max 1 config.Pipeline.EmailConcurrency
            let syncInterval = TimeSpan.FromMinutes(float config.SyncIntervalMinutes)

            logger.info $"Pipeline starting: extract={extractConcurrency}, classify={llmConcurrency}, email={emailConcurrency}"

            let tasks = ResizeArray<Task>()

            // ── Producers ────────────────────────────────────────────

            // Email: enumerate → N consumers → enqueue into stage_extract
            let enumeratedCounter = ref 0
            let processedCounter = ref 0
            tasks.Add(task {
                try do! Task.Delay(TimeSpan.FromSeconds(30.0), ct) with :? OperationCanceledException -> ()
                while not ct.IsCancellationRequested do
                    for account in config.Accounts do
                        try
                            let! provider = deps.CreateEmailProvider configDir account.Label
                            let! _ = EmailSync.syncAccountChanneled fs db logger clock provider config account.Label extractQ emailConcurrency enumeratedCounter processedCounter ct
                            ()
                        with ex -> logger.warn $"Email sync failed for {account.Label}: {ex.Message}"
                    try do! Task.Delay(syncInterval, ct) with :? OperationCanceledException -> ()
            })

            // Folder watcher: scan → processFile → enqueue into stage_extract
            tasks.Add(task {
                while not ct.IsCancellationRequested do
                    let! _ = FolderWatcher.scanAll fs db logger clock config
                    // Process files in unclassified/
                    let unclDir = System.IO.Path.Combine(archiveDir, "unclassified")
                    if fs.directoryExists unclDir then
                        let files =
                            fs.getFiles unclDir "*"
                            |> Array.filter (fun f -> not (f.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)))
                        for filePath in files do
                            try
                                let! result = Classifier.processFile fs db logger clock rules archiveDir filePath
                                match result with
                                | Ok (Some docId) ->
                                    let! rows = db.execReader "SELECT saved_path FROM documents WHERE id = @id" [ ("@id", Database.boxVal docId) ]
                                    let savedPath = rows |> List.tryHead |> Option.bind (fun r -> Prelude.RowReader(r).OptString "saved_path") |> Option.defaultValue ""
                                    do! StageProcessors.enqueueExtract extractQ docId savedPath
                                | _ -> ()
                            with ex -> logger.warn $"Folder intake failed for {System.IO.Path.GetFileName(filePath)}: {ex.Message}"
                    try do! Task.Delay(TimeSpan.FromSeconds(30.0), ct) with :? OperationCanceledException -> ()
            })

            // ── Stage processors ─────────────────────────────────────

            // Extract: read file → extract text → forward to classify
            let extractFn (item: Algebra.ExtractItem) =
                task {
                    // Read current saved_path from documents (not stale file_path from queue)
                    let! rows = db.execReader "SELECT saved_path FROM documents WHERE id = @id" [ ("@id", Database.boxVal item.DocId) ]
                    let currentPath =
                        rows |> List.tryHead
                        |> Option.bind (fun r -> Prelude.RowReader(r).OptString "saved_path")
                        |> Option.defaultValue item.FilePath
                    let! result = Extraction.processDocument fs db logger clock deps.Extractor archiveDir item.DocId currentPath false
                    return result |> Result.map (fun _ -> item.DocId)
                }
            // Single extract processor — SQLite doesn't support competing consumers safely
            tasks.Add(StageProcessors.runExtractLoop "extract" logger clock extractQ classifyQ extractFn 10 (TimeSpan.FromSeconds(2.0)) ct)

            // Classify: LLM/rules classification → forward to embed
            match deps.ChatProvider with
            | Some chat ->
                let classifyFn (item: Algebra.DocItem) =
                    task {
                        let! docRows = db.execReader "SELECT category, extracted_text FROM documents WHERE id = @id" [ ("@id", Database.boxVal item.DocId) ]
                        match docRows |> List.tryHead with
                        | Some docRow ->
                            let dr = Prelude.RowReader(docRow)
                            let cat = dr.String "category" ""
                            let text = dr.OptString "extracted_text"
                            if (cat = "unsorted" || cat = "unclassified") && text.IsSome then
                                // Try content rules first (fast, no LLM)
                                let mutable classified = false
                                match ContentClassifier.classify text.Value [] None deps.ContentRules with
                                | Some (newCat, conf) ->
                                    let! moveResult = DocumentManagement.reclassify db fs archiveDir item.DocId newCat
                                    match moveResult with
                                    | Ok () ->
                                        let! _ = db.execNonQuery "UPDATE documents SET classification_tier = 'content', classification_confidence = @conf WHERE id = @id" [ ("@conf", Database.boxVal conf); ("@id", Database.boxVal item.DocId) ]
                                        classified <- true
                                    | Error _ -> ()
                                | None -> ()
                                // LLM fallback
                                if not classified then
                                    let! catRows = db.execReader "SELECT DISTINCT category FROM documents WHERE category NOT IN ('unsorted','unclassified') LIMIT 50" []
                                    let categories = catRows |> List.choose (fun r2 -> Prelude.RowReader(r2).OptString "category")
                                    let seedCats = [ "invoices"; "bank-statements"; "receipts"; "tax"; "payslips"; "insurance"; "real-estate"; "travel"; "medical"; "utilities"; "legal"; "donations"; "contracts"; "correspondence" ]
                                    let allCats = (categories @ seedCats) |> List.distinct
                                    let prompt = ContentClassifier.buildClassificationPrompt text.Value allCats
                                    let! llmResult = chat.complete "You are a document classifier." prompt
                                    match llmResult with
                                    | Ok response ->
                                        match ContentClassifier.parseClassificationResponse response with
                                        | Some (newCat, conf, reasoning) when conf >= 0.4 ->
                                            let! moveResult = DocumentManagement.reclassify db fs archiveDir item.DocId newCat
                                            match moveResult with
                                            | Ok () ->
                                                let tier = if conf >= 0.7 then "llm" else "llm_review"
                                                let! _ = db.execNonQuery "UPDATE documents SET classification_tier = @tier, classification_confidence = @conf WHERE id = @id" [ ("@tier", Database.boxVal tier); ("@conf", Database.boxVal conf); ("@id", Database.boxVal item.DocId) ]
                                                logger.info $"Classified doc {item.DocId} as {newCat} (conf={conf:F2}): {reasoning}"
                                            | Error e -> logger.warn $"Classify move failed for doc {item.DocId}: {e}"
                                        | _ -> ()
                                    | Error e -> logger.warn $"LLM classification failed for doc {item.DocId}: {e}"
                            return Ok ()
                        | None -> return Ok ()
                    }
                for _ in 1..llmConcurrency do
                    tasks.Add(StageProcessors.runDocLoop "classify" logger clock classifyQ (Some embedQ) classifyFn 1 (TimeSpan.FromSeconds(2.0)) ct)
            | None ->
                logger.warn "No chat provider — classify stage disabled"

            // Embed: generate embeddings → done (defers while classify/extract has work)
            match deps.Embedder with
            | Some embedder ->
                tasks.Add(task {
                    logger.info "Stage processor 'embed' started"
                    try
                        while not ct.IsCancellationRequested do
                            let! classifyPending = classifyQ.count ()
                            let! extractPending = extractQ.count ()
                            if classifyPending > 0L || extractPending > 0L then
                                logger.debug $"Embed deferred — extract:{extractPending} classify:{classifyPending} pending"
                                try do! Task.Delay(TimeSpan.FromSeconds(30.0), ct) with :? OperationCanceledException -> ()
                            else
                                let! items = embedQ.dequeue 5
                                if items.IsEmpty then
                                    try do! Task.Delay(TimeSpan.FromSeconds(10.0), ct) with :? OperationCanceledException -> ()
                                else
                                    let! avail = embedder.isAvailable ()
                                    if avail then
                                        for item in items do
                                            try
                                                let! _ = Embeddings.batchEmbed db logger clock embedder false (Some 1) None
                                                do! embedQ.complete item
                                            with ex ->
                                                logger.warn $"Embed failed for doc {item.DocId}: {ex.Message}"
                                                let! _ = embedQ.fail item logger clock ex.Message
                                                ()
                                    else
                                        logger.debug "Embed deferred — embedding service unavailable"
                                        try do! Task.Delay(TimeSpan.FromSeconds(30.0), ct) with :? OperationCanceledException -> ()
                    with :? OperationCanceledException -> ()
                    logger.info "Stage processor 'embed' stopped"
                })
            | None ->
                logger.warn "No embedder — embed stage disabled"

            // ── Dashboard ────────────────────────────────────────────
            tasks.Add(StageProcessors.dashboardLoop db logger extractQ classifyQ embedQ (TimeSpan.FromSeconds(30.0)) ct)

            // Wait for all tasks (they run until cancelled)
            try do! Task.WhenAll(tasks.ToArray())
            with _ -> ()

            logger.info "Pipeline stopped"
        }
