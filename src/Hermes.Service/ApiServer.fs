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
        (clock: Algebra.Clock) (observer: PipelineObserver.T)
        (chatProvider: Algebra.ChatProvider option)
        (archiveDir: string) (configDir: string) =

        // ── Pipeline state (SSE) ────────────────────────────────────
        app.MapGet("/api/pipeline/state", Func<HttpContext, Task>(fun ctx ->
            task {
                ctx.Response.ContentType <- "text/event-stream"
                ctx.Response.Headers.Append("Cache-Control", "no-cache")
                ctx.Response.Headers.Append("Connection", "keep-alive")

                let opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

                // Send current state immediately
                let data = JsonSerializer.Serialize(observer.State, opts)
                do! ctx.Response.WriteAsync($"data: {data}\n\n")
                do! ctx.Response.Body.FlushAsync()

                // Then push updates via subscription
                let mutable running = true
                let handler (state: PipelineObserver.PipelineState) =
                    try
                        let d = JsonSerializer.Serialize(state, opts)
                        ctx.Response.WriteAsync($"data: {d}\n\n").Wait()
                        ctx.Response.Body.FlushAsync().Wait()
                    with _ -> running <- false

                PipelineObserver.subscribe observer handler

                // Keep connection open until client disconnects
                while running && not ctx.RequestAborted.IsCancellationRequested do
                    try do! Task.Delay(1000, ctx.RequestAborted)
                    with :? OperationCanceledException -> running <- false
            })) |> ignore

        // ── Categories ──────────────────────────────────────────────
        app.MapGet("/api/categories", Func<IResult>(fun () ->
            let counts = Stats.getCategoryCounts fs archiveDir
            json counts)) |> ignore

        // ── Documents ───────────────────────────────────────────────
        app.MapGet("/api/documents", Func<HttpContext, Task<IResult>>(fun ctx ->
            task {
                let category = ctx.Request.Query["category"].ToString()
                let offset = ctx.Request.Query["offset"].ToString() |> fun s -> if String.IsNullOrEmpty s then 0 else int s
                let limit = ctx.Request.Query["limit"].ToString() |> fun s -> if String.IsNullOrEmpty s then 50 else int s
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
                    let fullPath = Path.Combine(archiveDir, d.FilePath)
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
                        return Results.Bytes(bytes, contentType, name)
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
        app.MapPost("/api/sync", Func<Task<IResult>>(fun () ->
            task {
                do! ServiceHost.requestSync fs clock archiveDir
                return json {| triggered = true |}
            })) |> ignore

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
