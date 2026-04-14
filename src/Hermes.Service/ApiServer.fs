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

    /// Map all API routes onto the endpoint builder.
    let mapRoutes
        (app: IEndpointRouteBuilder)
        (db: Algebra.Database) (fs: Algebra.FileSystem) (logger: Algebra.Logger)
        (clock: Algebra.Clock)
        (chatProvider: Algebra.ChatProvider option)
        (archiveDir: string) (configDir: string) =

        // ── Categories ──────────────────────────────────────────────
        app.MapGet("/api/categories", Func<IResult>(fun () ->
            let counts = Stats.getCategoryCounts fs archiveDir
            json counts)) |> ignore

        // ── Documents ───────────────────────────────────────────────
        app.MapGet("/api/documents", Func<HttpContext, Task<IResult>>(fun ctx ->
            task {
                let category = ctx.Request.Query["category"].ToString()
                let stage = ctx.Request.Query["stage"].ToString()
                let offset = ctx.Request.Query["offset"].ToString() |> fun s -> if String.IsNullOrEmpty s then 0 else int s
                let limit = ctx.Request.Query["limit"].ToString() |> fun s -> if String.IsNullOrEmpty s then 50 else int s
                if not (String.IsNullOrEmpty stage) then
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
                    let! docs = DocumentBrowser.listDocuments db category offset limit
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
                    // Fallback: try category folder if saved_path is stale
                    let fileName = Path.GetFileName(d.FilePath) |> Option.ofObj |> Option.defaultValue ""
                    let categoryPath = Path.Combine(archiveDir, d.Summary.Category, fileName)
                    let fullPath =
                        if File.Exists(primaryPath) then primaryPath
                        elif File.Exists(categoryPath) then categoryPath
                        else primaryPath
                    if File.Exists(fullPath) then
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
                        let! bytes = File.ReadAllBytesAsync(fullPath)
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
                return json {| received = get "received"
                               extracted = get "extracted"
                               classified = get "classified"
                               embedded = get "embedded"
                               failed = get "failed" |}
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
                    let! results = Search.execute db filter
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

        // ── Move document to category ───────────────────────────────
        app.MapPut("/api/documents/{id:long}/category", Func<int64, HttpContext, Task<IResult>>(fun id ctx ->
            task {
                use sr = new StreamReader(ctx.Request.Body)
                let! bodyText = sr.ReadToEndAsync()
                let category =
                    try
                        let doc = JsonDocument.Parse(bodyText)
                        doc.RootElement.GetProperty("category").GetString() |> Option.ofObj |> Option.defaultValue ""
                    with _ -> ""
                if category = "" then return json {| error = "category required" |}
                else
                    let! result = DocumentManagement.reclassify db fs archiveDir id category
                    match result with
                    | Ok () -> return json {| moved = true; category = category |}
                    | Error e -> return json {| error = e |}
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
                                    """SELECT d.id, d.original_name, d.category, d.extracted_date, d.extracted_amount, d.sender, d.extracted_vendor
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
                    let! rows = db.execReader "SELECT tag, COUNT(*) as cnt FROM tags GROUP BY tag ORDER BY cnt DESC" []
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
                        let mutable moved = 0
                        for docId in docIds do
                            let! result = DocumentManagement.reclassify db fs archiveDir docId value
                            match result with Ok () -> moved <- moved + 1 | _ -> ()
                        return json {| action = "move"; count = moved |}
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
