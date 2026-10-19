namespace Hermes.Service

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open Hermes.Core

/// HTTP API routes for the React frontend. Binds to localhost:21741.
[<RequireQualifiedAccess>]
module ApiServer =

    let private json v = Results.Json(v, JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))

    type private ReflowRequest =
        { Kind: Reflow.OperationKind
          Mode: Reflow.RequestMode }

    let private tryGetString
        (root: JsonElement) (name: string) : Result<string option, string> =
        if root.ValueKind <> JsonValueKind.Object then
            Error "Request body must be an object"
        else
            match root.TryGetProperty(name) with
            | false, _ -> Ok None
            | true, value when value.ValueKind = JsonValueKind.String ->
                value.GetString() |> Option.ofObj |> Ok
            | _ -> Error $"{name} must be a string"

    let private readCategory bodyText =
        try
            use document = JsonDocument.Parse(bodyText: string)
            tryGetString document.RootElement "category"
            |> Result.toOption
            |> Option.flatten
            |> Option.defaultValue ""
        with :? JsonException ->
            ""

    let private reclassifyRequest db fs archiveDir id (ctx: HttpContext) =
        task {
            use reader = new StreamReader(ctx.Request.Body)
            let! bodyText = reader.ReadToEndAsync()
            let category = readCategory bodyText
            if String.IsNullOrWhiteSpace category then
                return json {| error = "category required" |}
            else
                let! response =
                    ReclassificationApi.single db fs archiveDir id category
                return json response
        }

    let private parseReflowFields root =
        tryGetString root "operation"
        |> Result.bind (function
            | None -> Error "operation is required"
            | Some operation ->
                Reflow.OperationKind.parse operation
                |> Result.bind (fun kind ->
                    tryGetString root "mode"
                    |> Result.bind (fun mode ->
                        mode
                        |> Option.defaultValue "dry_run"
                        |> Reflow.RequestMode.parse
                        |> Result.map (fun value -> { Kind = kind; Mode = value }))))

    let private parseReflowRequest (bodyText: string) =
        try
            use document = JsonDocument.Parse(bodyText)
            parseReflowFields document.RootElement
        with :? JsonException as error ->
            Error error.Message

    let private stageOutcomeDto (s: Reflow.StageStatus) =
        {| stageName = s.StageName
           outcome = Reflow.StageOutcome.toString s.Outcome
           error = s.Error |}

    let private operationStatusDto (s: Reflow.OperationStatus) =
        {| operationId = s.OperationId
           documentId = s.DocumentId
           operation = Reflow.OperationKind.toString s.Kind
           mode = Reflow.RequestMode.toString s.Mode
           lifecycle = Reflow.Lifecycle.toString s.Lifecycle
           dagSignature = s.DagSignature
           createdAt = s.CreatedAt
           completedAt = s.CompletedAt
           error = s.Error
           stages = s.Stages |> List.map stageOutcomeDto |}

    let private planDto (p: Reflow.Plan) =
        {| documentId = p.DocumentId
           operation = Reflow.OperationKind.toString p.Kind
           invalidatedStages = p.InvalidatedStages
           currentStages = p.CurrentStages
           dagSignature = p.DagSignature |}

    let private requestResultDto (r: Reflow.RequestResult) =
        {| plan = planDto r.Plan
           duplicate = r.Duplicate
           status = r.Status |> Option.map operationStatusDto |}

    let private legacyOperationDto (r: Reflow.RequestResult) =
        {| operationId = r.Status |> Option.map (fun s -> s.OperationId)
           duplicate = r.Duplicate
           requeued = true |}

    let private applyLegacyReflow
        (db: Algebra.Database) (logger: Algebra.Logger) (dag: PipelineV5.Dag)
        (id: int64) (kind: Reflow.OperationKind) : Task<IResult> =
        task {
            let! result = Reflow.request db logger dag id kind Reflow.RequestMode.Apply
            match result with
            | Ok r -> return json (legacyOperationDto r)
            | Error e -> return Results.BadRequest({| error = e |})
        }

    let private countsByKey (rows: Map<string, obj> list) (keyCol: string) : Map<string, int64> =
        rows
        |> List.map (fun row ->
            let r = Prelude.RowReader(row)
            r.String keyCol "", r.Int64 "cnt" 0L)
        |> Map.ofList

    let private reflowLifecycleCounts (db: Algebra.Database) : Task<Map<string, int64>> =
        task {
            let! rows = db.execReader "SELECT lifecycle, COUNT(*) AS cnt FROM reflow_operations GROUP BY lifecycle" []
            return countsByKey rows "lifecycle"
        }

    let private reflowStageOutcomeCounts (db: Algebra.Database) : Task<Map<string, int64>> =
        task {
            let! rows =
                db.execReader
                    "SELECT outcome, COUNT(*) AS cnt FROM reflow_operation_stages WHERE outcome IN ('pending', 'failed') GROUP BY outcome"
                    []
            return countsByKey rows "outcome"
        }

    /// Map all API routes onto the endpoint builder.
    let mapRoutesWithSearch
        (queryEncoder: Algebra.EmbeddingClient option)
        (app: IEndpointRouteBuilder)
        (db: Algebra.Database)
        (reflowDb: Algebra.Database)
        (fs: Algebra.FileSystem)
        (logger: Algebra.Logger)
        (clock: Algebra.Clock)
        (dag: PipelineV5.Dag)
        (chatProvider: Algebra.ChatProvider option)
        (archiveDir: string) (configDir: string) =

        // ── Categories ──────────────────────────────────────────────
        app.MapGet("/api/categories", Func<Task<IResult>>(fun () ->
            task {
                let! rows =
                    db.execReader
                        "SELECT category, COUNT(*) AS doc_count FROM documents WHERE category IS NOT NULL AND category != '' GROUP BY category ORDER BY doc_count DESC"
                        []
                let results =
                    rows |> List.map (fun row ->
                        let r = Prelude.RowReader(row)
                        {| category = r.String "category" ""; count = r.Int64 "doc_count" 0L |})
                return json results
            })) |> ignore

        // ── Documents ───────────────────────────────────────────────
        app.MapGet("/api/search", Func<HttpContext, Task<IResult>>(fun ctx ->
            KeywordApi.searchWithEncoder db queryEncoder ctx)) |> ignore

        app.MapGet("/api/documents/{id:long}/comprehension",
            Func<int64, Task<IResult>>(fun id ->
                PersistedApi.get db fs archiveDir id)) |> ignore

        app.MapGet("/api/documents", Func<HttpContext, Task<IResult>>(fun ctx ->
            task {
                let category = ctx.Request.Query["category"].ToString()
                let stage = ctx.Request.Query["stage"].ToString()
                let offset = ctx.Request.Query["offset"].ToString() |> fun s -> if String.IsNullOrEmpty s then "0" else s
                let limit = ctx.Request.Query["limit"].ToString() |> fun s -> if String.IsNullOrEmpty s then "50" else s
                let query = ctx.Request.Query["q"].ToString()
                if not (String.IsNullOrWhiteSpace query) then
                    return! KeywordApi.documents db category query offset limit
                elif not (String.IsNullOrEmpty stage) then
                    // Filter by pipeline stage
                    let! rows =
                        db.execReader
                            """SELECT id, original_name, category, extracted_date, extracted_amount,
                               sender, extracted_vendor AS vendor, source_type, account, source_path,
                               classification_tier, classification_confidence, stage, extraction_method
                               FROM documents WHERE stage = @stage ORDER BY id DESC LIMIT @lim OFFSET @off"""
                            [ ("@stage", Database.boxVal stage); ("@lim", Database.boxVal (int64 limit)); ("@off", Database.boxVal (int64 offset)) ]
                    let docs =
                        rows |> List.map (fun row ->
                            let r = Prelude.RowReader(row)
                            {| id = r.Int64 "id" 0L
                               originalName = r.String "original_name" ""
                               category = r.String "category" ""
                               extractedDate = r.OptString "extracted_date"
                               extractedAmount = r.OptFloat "extracted_amount"
                               sender = r.OptString "sender"
                               vendor = r.OptString "vendor"
                               sourceType = r.OptString "source_type"
                               account = r.OptString "account"
                               sourcePath = r.OptString "source_path"
                               classificationTier = r.OptString "classification_tier"
                               classificationConfidence = r.OptFloat "classification_confidence"
                               stage = r.OptString "stage"
                               extractionMethod = r.OptString "extraction_method" |})
                    return json docs
                else
                    let! docs = DocumentBrowser.listDocuments db category (int offset) (int limit)
                    return json docs
            })) |> ignore

        app.MapGet("/api/documents/{id:long}", Func<int64, Task<IResult>>(fun id ->
            task {
                let! detail = DocumentBrowser.getDocumentDetail db id
                match detail with
                | Some d -> return json d
                | None -> return Results.NotFound()
            })) |> ignore

        app.MapGet("/api/documents/{id:long}/content", Func<int64, Task<IResult>>(fun id ->
            task {
                let! content = DocumentFeed.getDocumentContent db fs archiveDir id DocumentFeed.ContentFormat.Markdown
                match content with
                | Ok c -> return json {| markdown = c |}
                | Error e -> return Results.NotFound({| error = e |})
            })) |> ignore

        // ── Document file (serve original PDF/image) ───────────────
        app.MapGet("/api/documents/{id:long}/file", Func<int64, Task<IResult>>(fun id ->
            task {
                let! detail = DocumentBrowser.getDocumentDetail db id
                match detail with
                | Some d ->
                    let primaryPath = Path.Combine(archiveDir, d.FilePath)
                    if File.Exists(primaryPath) then
                        let name = d.Summary.OriginalName
                        let dot = name.LastIndexOf('.')
                        let ext = if dot >= 0 then name.Substring(dot).ToLowerInvariant() else ""
                        let contentType =
                            match ext with
                            | ".pdf" -> "application/pdf"
                            | ".png" -> "image/png"
                            | ".jpg" | ".jpeg" -> "image/jpeg"
                            | ".gif" -> "image/gif"
                            | ".webp" -> "image/webp"
                            | ".csv" -> "text/csv"
                            | ".txt" | ".md" | ".log" -> "text/plain"
                            | _ -> "application/octet-stream"
                        let! bytes = File.ReadAllBytesAsync(primaryPath)
                        return Results.Bytes(bytes, contentType)
                    else
                        return Results.NotFound({| error = "File not found on disk" |})
                | None -> return Results.NotFound()
            })) |> ignore

        // ── Stats ───────────────────────────────────────────────────
        app.MapGet("/api/stats", Func<Task<IResult>>(fun () ->
            task {
                let dbPath = Path.Combine(archiveDir, "db.sqlite")
                let! stats = Stats.getIndexStats db fs dbPath
                return json stats
            })) |> ignore

        // ── Pipeline dashboard ──────────────────────────────────────
        app.MapGet("/api/pipeline", Func<Task<IResult>>(fun () ->
            task {
                let! counts = Document.stageCounts db
                let get key = counts |> Map.tryFind key |> Option.defaultValue 0L
                let! lifecycleCounts = reflowLifecycleCounts reflowDb
                let! stageOutcomeCounts = reflowStageOutcomeCounts reflowDb
                let getLifecycle key = lifecycleCounts |> Map.tryFind key |> Option.defaultValue 0L
                let getStageOutcome key = stageOutcomeCounts |> Map.tryFind key |> Option.defaultValue 0L
                return json {| received = get "received"
                               extracted = get "extracted"
                               understood = get "understood"
                               embedded = get "embedded"
                               failed = get "failed"
                               reflow = {| pending = getLifecycle "pending"
                                           running = getLifecycle "running"
                                           completed = getLifecycle "completed"
                                           failed = getLifecycle "failed"
                                           stagesPending = getStageOutcome "pending"
                                           stagesFailed = getStageOutcome "failed" |} |}
            })) |> ignore

        // ── Reminders ───────────────────────────────────────────────
        app.MapGet("/api/reminders", Func<Task<IResult>>(fun () ->
            task {
                let! active = Reminders.getActive db (clock.utcNow ())
                let mapped =
                    active |> List.map (fun (r, vendor, fileName) ->
                        {| id = r.Id; documentId = r.DocumentId; vendor = vendor
                           amount = r.Amount; dueDate = r.DueDate; category = r.Category
                           status = r.Status.ToString(); fileName = fileName |})
                return json mapped
            })) |> ignore

        // ── Sync trigger ────────────────────────────────────────────
        app.MapPost("/api/sync", Func<IResult>(fun () ->
            // Sync runs automatically; this endpoint is a no-op acknowledgement
            json {| triggered = true |}
            )) |> ignore

        // ── Chat ────────────────────────────────────────────────────
        app.MapPost("/api/chat", Func<HttpContext, Task>(fun ctx ->
            task {
                use sr = new StreamReader(ctx.Request.Body)
                let! bodyText = sr.ReadToEndAsync()
                let query, useAi =
                    try
                        let doc = JsonDocument.Parse(bodyText)
                        let q = doc.RootElement.GetProperty("query").GetString() |> Option.ofObj |> Option.defaultValue ""
                        let ai = try doc.RootElement.GetProperty("aiEnabled").GetBoolean() with _ -> false
                        (q, ai)
                    with _ -> ("", false)

                ctx.Response.ContentType <- "text/event-stream"
                ctx.Response.Headers.Append("Cache-Control", "no-cache")
                let opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

                match chatProvider with
                | Some chat ->
                    let! response = Chat.query db chat useAi query
                    // Send search results immediately
                    let resultsData = JsonSerializer.Serialize({| results = response.Results |}, opts)
                    do! ctx.Response.WriteAsync($"event: results\ndata: {resultsData}\n\n")
                    do! ctx.Response.Body.FlushAsync()
                    // Send AI summary if available
                    match response.AiSummary with
                    | Some summary ->
                        let answerData = JsonSerializer.Serialize({| answer = summary |}, opts)
                        do! ctx.Response.WriteAsync($"event: answer\ndata: {answerData}\n\n")
                        do! ctx.Response.Body.FlushAsync()
                    | None -> ()
                | None ->
                    // No chat provider — just do keyword search
                    let filter = Search.defaultFilter query
                    let! results = Search.executeUnified db filter
                    let resultsData = JsonSerializer.Serialize({| results = results |}, opts)
                    do! ctx.Response.WriteAsync($"event: results\ndata: {resultsData}\n\n")
                    do! ctx.Response.Body.FlushAsync()

                do! ctx.Response.WriteAsync("event: done\ndata: {}\n\n")
                do! ctx.Response.Body.FlushAsync()
            })) |> ignore

        // ── Settings ────────────────────────────────────────────────
        app.MapGet("/api/settings", Func<Task<IResult>>(fun () ->
            task {
                let configPath = Path.Combine(configDir, "config.yaml")
                if File.Exists(configPath) then
                    let! yaml = File.ReadAllTextAsync(configPath)
                    return Results.Text(yaml, "text/yaml")
                else
                    return Results.NotFound()
            })) |> ignore

        app.MapPut("/api/settings", Func<HttpContext, Task<IResult>>(fun ctx ->
            task {
                use sr = new StreamReader(ctx.Request.Body)
                let! yaml = sr.ReadToEndAsync()
                let configPath = Path.Combine(configDir, "config.yaml")
                do! File.WriteAllTextAsync(configPath, yaml)
                return json {| saved = true |}
            })) |> ignore

        // ── Dead letters ────────────────────────────────────────────
        app.MapGet("/api/dead-letters", Func<Task<IResult>>(fun () ->
            task {
                let! rows =
                    db.execReader
                        "SELECT id, doc_id, stage, error, original_name, failed_at FROM dead_letters WHERE dismissed = 0 ORDER BY id DESC LIMIT 100"
                        []
                let letters =
                    rows |> List.map (fun row ->
                        let r = Prelude.RowReader(row)
                        {| id = r.Int64 "id" 0L; docId = r.Int64 "doc_id" 0L
                           stage = r.String "stage" ""; error = r.String "error" ""
                           originalName = r.String "original_name" ""; failedAt = r.String "failed_at" "" |})
                return json letters
            })) |> ignore

        app.MapPost("/api/dead-letters/dismiss", Func<Task<IResult>>(fun () ->
            task {
                let! _ = db.execNonQuery "UPDATE dead_letters SET dismissed = 1 WHERE dismissed = 0" []
                return json {| dismissed = true |}
            })) |> ignore

        // ── Activity log ────────────────────────────────────────────
        app.MapGet("/api/activity", Func<HttpContext, Task<IResult>>(fun ctx ->
            task {
                let limit = ctx.Request.Query["limit"].ToString() |> fun s -> if System.String.IsNullOrEmpty s then 50 else int s
                let! rows =
                    db.execReader
                        "SELECT id, timestamp, level, category, message, document_id FROM activity_log ORDER BY id DESC LIMIT @lim"
                        [ ("@lim", Database.boxVal (int64 limit)) ]
                let entries =
                    rows |> List.map (fun r ->
                        let rd = Prelude.RowReader(r)
                        {| id = rd.Int64 "id" 0L
                           timestamp = rd.String "timestamp" ""
                           level = rd.String "level" "info"
                           category = rd.String "category" ""
                           message = rd.String "message" ""
                           documentId = rd.OptInt64 "document_id" |})
                return json entries
            })) |> ignore

        // ── Reclassify document metadata ───────────────────────────
        let reclassifyHandler =
            Func<int64, HttpContext, Task<IResult>>(fun id ctx ->
                task {
                    return! reclassifyRequest db fs archiveDir id ctx
                })
        app.MapPut("/api/documents/{id:long}/category", reclassifyHandler) |> ignore
        app.MapPost("/api/documents/{id:long}/move",
            Func<int64, HttpContext, Task<IResult>>(fun id ctx ->
                ctx.Response.Headers["Deprecation"] <- "true"
                ctx.Response.Headers["Warning"] <-
                    "299 Hermes \"Deprecated move endpoint performs metadata-only reclassification; inspect reclassified and changed, not moved\""
                reclassifyRequest db fs archiveDir id ctx)) |> ignore

        // ── Correct document fields ──────────────────────────────────
        app.MapPost("/api/documents/{id:long}/correct", Func<int64, HttpContext, Task<IResult>>(fun id ctx ->
            task {
                use sr = new StreamReader(ctx.Request.Body)
                let! bodyText = sr.ReadToEndAsync()
                try
                    let doc = JsonDocument.Parse(bodyText)
                    let root = doc.RootElement
                    let mutable noteEl = JsonElement()
                    let note =
                        if root.TryGetProperty("note", &noteEl) then
                            noteEl.GetString() |> Option.ofObj
                        else None
                    let mutable errors = []
                    let corrections = root.GetProperty("corrections")
                    for item in corrections.EnumerateArray() do
                        let field = item.GetProperty("field").GetString() |> Option.ofObj |> Option.defaultValue ""
                        let value = item.GetProperty("value").GetString() |> Option.ofObj |> Option.defaultValue ""
                        let! result = DocumentManagement.correctField db id field value note
                        match result with
                        | Error e -> errors <- e :: errors
                        | Ok () -> ()
                    if errors.IsEmpty then return json {| corrected = true |}
                    else return json {| error = String.concat "; " errors |}
                with ex ->
                    return json {| error = ex.Message |}
            })) |> ignore

        // ── Reflow operations ────────────────────────────────────────
        app.MapPost("/api/documents/{id:long}/reflow", Func<int64, HttpContext, Task<IResult>>(fun id ctx ->
            task {
                use sr = new StreamReader(ctx.Request.Body)
                let! bodyText = sr.ReadToEndAsync()
                match parseReflowRequest bodyText with
                | Error e -> return Results.BadRequest({| error = e |})
                | Ok request ->
                    let! result =
                        Reflow.request reflowDb logger dag id request.Kind request.Mode
                    match result with
                    | Ok r -> return json (requestResultDto r)
                    | Error e -> return Results.BadRequest({| error = e |})
            })) |> ignore

        app.MapGet("/api/reflow/{id:long}", Func<int64, Task<IResult>>(fun id ->
            task {
                let! result = Reflow.getStatus dag reflowDb id
                match result with
                | Ok status -> return json (operationStatusDto status)
                | Error e when e.Contains "not found" -> return Results.NotFound({| error = e |})
                | Error e -> return Results.BadRequest({| error = e |})
            })) |> ignore

        app.MapPost("/api/documents/{id:long}/recomprehend", Func<int64, Task<IResult>>(fun id ->
            applyLegacyReflow reflowDb logger dag id Reflow.OperationKind.Recomprehend)) |> ignore

        app.MapPost("/api/documents/{id:long}/reextract", Func<int64, Task<IResult>>(fun id ->
            applyLegacyReflow reflowDb logger dag id Reflow.OperationKind.Reextract)) |> ignore

        app.MapPost("/api/documents/{id:long}/reembed", Func<int64, Task<IResult>>(fun id ->
            applyLegacyReflow reflowDb logger dag id Reflow.OperationKind.Reembed)) |> ignore

        // ── List corrections (for prompt tuning) ─────────────────────
        app.MapGet("/api/corrections", Func<HttpContext, Task<IResult>>(fun ctx ->
            task {
                let limit = ctx.Request.Query["limit"].ToString() |> fun s -> if String.IsNullOrEmpty s then 100 else int s
                let! corrections = DocumentManagement.listCorrections db limit
                return json corrections
            })) |> ignore

        // ── Star/unstar ─────────────────────────────────────────────
        app.MapPost("/api/documents/{id:long}/star", Func<int64, Task<IResult>>(fun id ->
            task {
                let! _ = db.execNonQuery "UPDATE documents SET starred = CASE WHEN starred = 1 THEN 0 ELSE 1 END WHERE id = @id" [ ("@id", Database.boxVal id) ]
                let! obj = db.execScalar "SELECT starred FROM documents WHERE id = @id" [ ("@id", Database.boxVal id) ]
                let starred = match obj with :? int64 as i -> i = 1L | _ -> false
                return json {| starred = starred |}
            })) |> ignore

        // ── Tags CRUD ───────────────────────────────────────────────
        app.MapGet("/api/documents/{id:long}/tags", Func<int64, Task<IResult>>(fun id ->
            task {
                let! rows = db.execReader "SELECT tag, source, confidence FROM tags WHERE document_id = @id" [ ("@id", Database.boxVal id) ]
                let tags =
                    rows |> List.map (fun r ->
                        let rd = Prelude.RowReader(r)
                        {| tag = rd.String "tag" ""; source = rd.String "source" ""; confidence = rd.OptFloat "confidence" |})
                return json tags
            })) |> ignore

        app.MapPost("/api/documents/{id:long}/tags", Func<int64, HttpContext, Task<IResult>>(fun id ctx ->
            task {
                use sr = new StreamReader(ctx.Request.Body)
                let! bodyText = sr.ReadToEndAsync()
                let tags =
                    try
                        let doc = JsonDocument.Parse(bodyText)
                        doc.RootElement.GetProperty("tags").EnumerateArray() |> Seq.map (fun e -> e.GetString() |> Option.ofObj |> Option.defaultValue "") |> Seq.filter (fun s -> s <> "") |> Seq.toList
                    with _ -> []
                for tag in tags do
                    let! _ = db.execNonQuery "INSERT OR IGNORE INTO tags (document_id, tag, source) VALUES (@id, @tag, 'user')" [ ("@id", Database.boxVal id); ("@tag", Database.boxVal tag) ]
                    ()
                return json {| added = tags |}
            })) |> ignore

        app.MapDelete("/api/documents/{id:long}/tags/{tag}", Func<int64, string, Task<IResult>>(fun id tag ->
            task {
                let! _ = db.execNonQuery "DELETE FROM tags WHERE document_id = @id AND tag = @tag" [ ("@id", Database.boxVal id); ("@tag", Database.boxVal tag) ]
                return json {| removed = tag |}
            })) |> ignore

        // ── Tag search ──────────────────────────────────────────────
        app.MapGet("/api/tags", Func<HttpContext, Task<IResult>>(fun ctx ->
            task {
                let tag = ctx.Request.Query["tag"].ToString()
                if tag <> "" then
                    let! rows = db.execReader
                                    """SELECT DISTINCT d.id, d.original_name, d.category, d.extracted_date, d.extracted_amount, d.sender, d.extracted_vendor
                                       FROM documents d JOIN tags t ON d.id = t.document_id WHERE t.tag = @tag ORDER BY d.id DESC LIMIT 100"""
                                    [ ("@tag", Database.boxVal tag) ]
                    let docs =
                        rows |> List.map (fun r ->
                            let rd = Prelude.RowReader(r)
                            {| id = rd.Int64 "id" 0L; originalName = rd.String "original_name" ""
                               category = rd.String "category" ""
                               extractedDate = rd.OptString "extracted_date"
                               extractedAmount = rd.OptFloat "extracted_amount"
                               sender = rd.OptString "sender"
                               vendor = rd.OptString "extracted_vendor" |})
                    return json docs
                else
                    let! rows = db.execReader "SELECT tag, COUNT(DISTINCT document_id) as cnt FROM tags GROUP BY tag ORDER BY cnt DESC" []
                    let tags =
                        rows |> List.map (fun r ->
                            let rd = Prelude.RowReader(r)
                            {| tag = rd.String "tag" ""; count = rd.Int64 "cnt" 0L |})
                    return json tags
            })) |> ignore

        // ── Batch operations ────────────────────────────────────────
        app.MapPost("/api/documents/batch", Func<HttpContext, Task<IResult>>(fun ctx ->
            task {
                use sr = new StreamReader(ctx.Request.Body)
                let! bodyText = sr.ReadToEndAsync()
                try
                    let doc = JsonDocument.Parse(bodyText)
                    let docIds = doc.RootElement.GetProperty("docIds").EnumerateArray() |> Seq.map (fun e -> e.GetInt64()) |> Seq.toList
                    let action = doc.RootElement.GetProperty("action").GetString() |> Option.ofObj |> Option.defaultValue ""
                    let value = try doc.RootElement.GetProperty("value").GetString() |> Option.ofObj |> Option.defaultValue "" with _ -> ""

                    match action with
                    | "move" ->
                        let! response =
                            ReclassificationApi.legacyBatch
                                db fs archiveDir docIds value
                        return json response
                    | "reclassify" ->
                        let! response =
                            Hermes.Core.ReclassificationApi.batch
                                db fs archiveDir docIds value
                        return json response
                    | "tag" ->
                        for docId in docIds do
                            let! _ = db.execNonQuery "INSERT OR IGNORE INTO tags (document_id, tag, source) VALUES (@id, @tag, 'user')" [ ("@id", Database.boxVal docId); ("@tag", Database.boxVal value) ]
                            ()
                        return json {| action = "tag"; count = docIds.Length |}
                    | "star" ->
                        for docId in docIds do
                            let! _ = db.execNonQuery "UPDATE documents SET starred = 1 WHERE id = @id" [ ("@id", Database.boxVal docId) ]
                            ()
                        return json {| action = "star"; count = docIds.Length |}
                    | _ -> return json {| error = "unknown action" |}
                with ex -> return json {| error = ex.Message |}
            })) |> ignore

        // ── Sync date config ────────────────────────────────────────
        app.MapGet("/api/sync/accounts", Func<Task<IResult>>(fun () ->
            task {
                // Get sync state from DB
                let! rows =
                    db.execReader "SELECT account, last_sync_at, message_count FROM sync_state ORDER BY account" []
                let syncState =
                    rows |> List.map (fun r ->
                        let rd = Prelude.RowReader(r)
                        let acc = rd.String "account" ""
                        let sync = rd.OptString "last_sync_at"
                        let count = rd.Int64 "message_count" 0L
                        (acc, (sync, count)))
                    |> Map.ofList

                // Get configured accounts from config file
                let configPath = Path.Combine(configDir, "config.yaml")
                let! configResult = Config.load fs (Interpreters.systemEnvironment) configPath
                let configAccounts =
                    match configResult with
                    | Ok cfg -> cfg.Accounts |> List.map (fun a -> a.Label)
                    | Error _ -> []

                // Merge: show all configured accounts with their sync state
                let accounts =
                    configAccounts |> List.map (fun label ->
                        match syncState |> Map.tryFind label with
                        | Some (lastSync, count) ->
                            {| account = label; lastSyncAt = lastSync; messageCount = count |}
                        | None ->
                            {| account = label; lastSyncAt = (None : string option); messageCount = 0L |})
                return json accounts
            })) |> ignore

        app.MapPost("/api/sync/reset", Func<HttpContext, Task<IResult>>(fun ctx ->
            task {
                use sr = new StreamReader(ctx.Request.Body)
                let! bodyText = sr.ReadToEndAsync()
                try
                    let doc = JsonDocument.Parse(bodyText)
                    let account = doc.RootElement.GetProperty("account").GetString() |> Option.ofObj |> Option.defaultValue ""
                    let fromDate = doc.RootElement.GetProperty("from").GetString() |> Option.ofObj |> Option.defaultValue ""
                    if account = "" then return json {| error = "account required" |}
                    else
                        if fromDate = "" then
                            // Reset to default (will use 2 FY + 1 month)
                            let! _ = db.execNonQuery "DELETE FROM sync_state WHERE account = @acc" [ ("@acc", Database.boxVal account) ]
                            return json {| reset = true; account = account; from = "default (2 FY + 1 month)" |}
                        else
                            // Set specific from-date by setting last_sync_at to that date
                            let! _ = db.execNonQuery
                                        """INSERT INTO sync_state (account, last_sync_at, message_count)
                                           VALUES (@acc, @ts, 0)
                                           ON CONFLICT(account) DO UPDATE SET last_sync_at = @ts"""
                                        [ ("@acc", Database.boxVal account); ("@ts", Database.boxVal fromDate) ]
                            return json {| reset = true; account = account; from = fromDate |}
                with ex -> return json {| error = ex.Message |}
            })) |> ignore

        // ── Preferences ─────────────────────────────────────────────
        app.MapGet("/api/preferences", Func<Task<IResult>>(fun () ->
            task {
                let prefsPath = Path.Combine(configDir, "preferences.txt")
                if fs.fileExists prefsPath then
                    let! text = fs.readAllText prefsPath
                    return Results.Text(text, "text/plain")
                else
                    // Fall back to config field
                    let configPath = Path.Combine(configDir, "config.yaml")
                    let! configResult = Config.load fs (Interpreters.systemEnvironment) configPath
                    let prefs =
                        match configResult with
                        | Ok cfg -> cfg.Preferences
                        | Error _ -> ""
                    return Results.Text(prefs, "text/plain")
            })) |> ignore

        app.MapPut("/api/preferences", Func<HttpContext, Task<IResult>>(fun ctx ->
            task {
                use sr = new StreamReader(ctx.Request.Body)
                let! prefsText = sr.ReadToEndAsync()
                let prefsPath = Path.Combine(configDir, "preferences.txt")
                do! fs.writeAllText prefsPath prefsText
                return json {| saved = true |}
            })) |> ignore

        // ── Learned patterns ────────────────────────────────────────
        app.MapGet("/api/learned-patterns", Func<Task<IResult>>(fun () ->
            task {
                let! rows =
                    db.execReader
                        """SELECT sender_domain, document_type, count, avg_confidence, last_seen
                           FROM learned_patterns
                           ORDER BY count DESC
                           LIMIT 100"""
                        []
                let results =
                    rows |> List.map (fun row ->
                        let r = Prelude.RowReader(row)
                        {| senderDomain = r.String "sender_domain" ""
                           documentType = r.String "document_type" ""
                           count = r.Int64 "count" 0L
                           avgConfidence = r.Float "avg_confidence" 0.0
                           lastSeen = r.OptString "last_seen" |})
                return json results
            })) |> ignore

        // ── Suggestions ─────────────────────────────────────────────
        app.MapGet("/api/suggestions", Func<HttpContext, Task<IResult>>(fun ctx ->
            task {
                let status = ctx.Request.Query["status"].ToString()
                let statusFilter = if String.IsNullOrEmpty status then "pending" else status
                let! rows =
                    db.execReader
                        """SELECT s.id, s.document_id, s.proposed_category, s.current_category,
                                  s.confidence, s.status, s.created_at,
                                  d.original_name, d.sender, d.subject
                           FROM suggestions s
                           LEFT JOIN documents d ON d.id = s.document_id
                           WHERE s.status = @status
                           ORDER BY s.created_at DESC
                           LIMIT 50"""
                        [("@status", Database.boxVal statusFilter)]
                let results =
                    rows |> List.map (fun row ->
                        let r = Prelude.RowReader(row)
                        {| id = r.Int64 "id" 0L
                           documentId = r.Int64 "document_id" 0L
                           proposedCategory = r.String "proposed_category" ""
                           currentCategory = r.String "current_category" ""
                           confidence = r.Float "confidence" 0.0
                           status = r.String "status" ""
                           createdAt = r.OptString "created_at"
                           originalName = r.OptString "original_name"
                           sender = r.OptString "sender"
                           subject = r.OptString "subject" |})
                return json results
            })) |> ignore

        app.MapPost("/api/suggestions/{id:long}/approve", Func<int64, Task<IResult>>(fun id ->
            task {
                let! result = Stages.approveSuggestion db id
                match result with
                | Ok () -> return json {| approved = true; id = id |}
                | Error e -> return json {| error = e |}
            })) |> ignore

        app.MapPost("/api/suggestions/{id:long}/reject", Func<int64, Task<IResult>>(fun id ->
            task {
                let! result = Stages.rejectSuggestion db id
                match result with
                | Ok () -> return json {| rejected = true; id = id |}
                | Error e -> return json {| error = e |}
            })) |> ignore

    let mapRoutesWithReflowDb app db reflowDb fs logger clock dag chatProvider archiveDir configDir =
        mapRoutesWithSearch None
            app db reflowDb fs logger clock dag chatProvider archiveDir configDir

    /// Backward-compatible route mapping for tests and single-connection hosts.
    let mapRoutes
        (app: IEndpointRouteBuilder)
        (db: Algebra.Database)
        (fs: Algebra.FileSystem)
        (logger: Algebra.Logger)
        (clock: Algebra.Clock)
        (dag: PipelineV5.Dag)
        (chatProvider: Algebra.ChatProvider option)
        (archiveDir: string)
        (configDir: string) =
        mapRoutesWithReflowDb
            app db db fs logger clock dag chatProvider archiveDir configDir

    let private authorization (context: HttpContext) =
        let values = context.Request.Headers["Authorization"]
        if values.Count = 1 then Some(values.ToString()) else None

    /// Shared by production and actual HTTP regression fixtures.
    let mapMcpRouteWithPolicy queryEncoder policy
        (app: IEndpointRouteBuilder) db reflowDb fs logger clock dag archiveDir deepDeps =
        app.MapPost("/mcp", Func<HttpContext, Task<IResult>>(fun ctx ->
            task {
                use reader = new StreamReader(ctx.Request.Body)
                let! body = reader.ReadToEndAsync()
                let capabilities: McpServer.Capabilities =
                    { Access = McpAccess.authenticate policy (authorization ctx)
                      QueryEncoder = queryEncoder }
                let! response =
                    McpServer.processMessageWithCapabilitiesAndReflowDb capabilities
                        db reflowDb fs logger clock dag archiveDir deepDeps body
                return Results.Text(response, "application/json")
            })) |> ignore

    let mapMcpRoute app db reflowDb fs logger clock dag archiveDir deepDeps =
        mapMcpRouteWithPolicy None McpAccess.readOnly
            app db reflowDb fs logger clock dag archiveDir deepDeps
