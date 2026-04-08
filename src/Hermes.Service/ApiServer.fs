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
                return json active
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
                let query =
                    try
                        let doc = JsonDocument.Parse(bodyText)
                        doc.RootElement.GetProperty("query").GetString() |> Option.ofObj |> Option.defaultValue ""
                    with _ -> ""

                ctx.Response.ContentType <- "text/event-stream"

                // Keyword search
                let filter = Search.defaultFilter query
                let! results = Search.execute db filter
                let opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
                let data = JsonSerializer.Serialize({| results = results |}, opts)
                do! ctx.Response.WriteAsync($"data: {data}\n\n")
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
