module internal Hermes.Tests.ServiceAccessHostTestSupport

open System
open System.Collections.Concurrent
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Hosting
open Xunit
open Hermes.Core.LocalAccess
open Hermes.Core.OwnerControlPipe
open Hermes.Service

[<RequireQualifiedAccess>]
type Effect = Archive | Model | Mutation

type Probe =
    { Root: string
      Effects: ConcurrentQueue<Effect>
      ControlStarted: TaskCompletionSource<unit>
      Listening: TaskCompletionSource<unit>
      Release: TaskCompletionSource<Result<unit, PipeFailure>>
      Fatal: TaskCompletionSource<PipeFailure -> unit>
      WorkerStarted: TaskCompletionSource<unit>
      WorkerDraining: TaskCompletionSource<unit>
      JoinRelease: TaskCompletionSource<unit>
      WorkerStopped: TaskCompletionSource<unit>
      QueuesClosed: TaskCompletionSource<unit> }

type CapturedHost =
    { App: WebApplication
      Lifetime: IHostApplicationLifetime
      Context: HostContext }

type Server =
    { Probe: Probe
      Client: HttpClient
      Origin: string
      Captured: Task<CapturedHost>
      Run: Task<AccessHostResult> }

type Reply =
    { Status: int
      Body: string
      CacheControl: string }

let healthPath: string = "/health"
let archivePath: string = "/api/host/archive"
let modelPath: string = "/api/host/model"
let mutationPath: string = "/api/host/mutation"
let accessPath: string = "/api/access"
let pairingsPath: string = "/api/access/pairings"

let cell<'Value> () : TaskCompletionSource<'Value> =
    TaskCompletionSource<'Value>(
        TaskCreationOptions.RunContinuationsAsynchronously)

let wait (pending: Task<'Value>) : Task<'Value> =
    pending.WaitAsync(TimeSpan.FromSeconds 20.0)

let ok (value: Result<'Value, 'Error>) : 'Value =
    value |> Result.defaultWith (fun error -> failwithf "Unexpected failure: %A" error)

let json (reply: Reply) : JsonElement =
    use document = JsonDocument.Parse(reply.Body)
    document.RootElement.Clone()

let field (name: string) (value: JsonElement) : string =
    value.GetProperty(name).GetString()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failwith "Expected a JSON string.")

let private send (server: Server) (request: HttpRequestMessage) : Task<Reply> =
    task {
        use request = request
        use! response = server.Client.SendAsync(request)
        let! body = response.Content.ReadAsStringAsync()
        return
            { Status = int response.StatusCode; Body = body
              CacheControl = string response.Headers.CacheControl }
    }

let get (server: Server) (path: string) : Task<Reply> =
    send server (new HttpRequestMessage(HttpMethod.Get, path))

let post
    (server: Server)
    (path: string)
    (credential: string option)
    (body: string)
    : Task<Reply> =
    if Option.isSome credential && not (path.StartsWith("/api/", StringComparison.Ordinal)) then
        invalidArg (nameof path) "Session credentials are restricted to the owned API."
    let request = new HttpRequestMessage(HttpMethod.Post, path)
    request.Headers.Add("Origin", server.Origin)
    credential
    |> Option.map (fun token ->
        request.Headers.Add("Authorization", "Bearer " + token))
    |> Option.defaultValue ()
    request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
    send server request

let assertUnavailable (reply: Reply) : unit =
    Assert.Equal(503, reply.Status)
    Assert.Equal("access_unavailable", json reply |> field "error")
    Assert.Equal("no-store", reply.CacheControl)

let private mutate (probe: Probe) (context: HttpContext) : Task =
    task {
        probe.Effects.Enqueue(Effect.Mutation)
        use reader = new StreamReader(context.Request.Body)
        let! body = reader.ReadToEndAsync(context.RequestAborted)
        do! File.WriteAllTextAsync(
            Path.Combine(probe.Root, "accepted.txt"), body,
            context.RequestAborted)
        context.Response.StatusCode <- 204
    } :> Task

let mapRoutes (probe: Probe) (app: WebApplication) : unit =
    app.MapGet(archivePath, Func<IResult>(fun () ->
        probe.Effects.Enqueue(Effect.Archive)
        Results.NoContent()))
    |> HttpAccessPolicy.classify HttpAccessClassification.AnonymousArchiveRead
    |> ignore
    app.MapPost(modelPath, Func<IResult>(fun () ->
        probe.Effects.Enqueue(Effect.Model)
        Results.NoContent()))
    |> HttpAccessPolicy.classify
        (HttpAccessClassification.RequiredCaps(Set.singleton Capability.ModelInvoke))
    |> ignore
    app.MapPost(mutationPath, Func<HttpContext, Task>(mutate probe))
    |> HttpAccessPolicy.classify
        (HttpAccessClassification.RequiredCaps(Set.singleton Capability.Mutate))
    |> ignore

let worker (probe: Probe) (token: CancellationToken) : Task =
    task {
        probe.WorkerStarted.TrySetResult(()) |> ignore
        try
            do! Task.Delay(Timeout.Infinite, token)
        with :? OperationCanceledException when token.IsCancellationRequested -> ()
        probe.WorkerDraining.TrySetResult(()) |> ignore
        do! probe.JoinRelease.Task
        probe.WorkerStopped.TrySetResult(()) |> ignore
    } :> Task

let assertPortReleased (server: Server) : unit =
    let port = Uri(server.Origin).Port
    use listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port)
    listener.Start()
