module internal Hermes.Tests.LocalAccessHttpFixture

open System
open System.Collections.Concurrent
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Primitives
open Xunit
open Hermes.Core
open Hermes.Core.LocalAccess
open Hermes.Service
open Hermes.Tests.LocalAccessDomainTests

let timeout: TimeSpan = TimeSpan.FromSeconds 10.0
let cancellation: CancellationToken = CancellationToken.None

type Effects =
    { Reads: ConcurrentQueue<unit>
      Work: ConcurrentQueue<unit>
      Models: ConcurrentQueue<ModelOperation>
      Moments: ConcurrentQueue<Moment> }

type private ReadingStream(inner: Stream, reads: ConcurrentQueue<unit>) =
    inherit Stream()
    override _.CanRead = inner.CanRead
    override _.CanSeek = inner.CanSeek
    override _.CanWrite = inner.CanWrite
    override _.Length = inner.Length
    override _.Position with get () = inner.Position and set value = inner.Position <- value
    override _.Flush() = inner.Flush()
    override _.Seek(offset, origin) = inner.Seek(offset, origin)
    override _.SetLength(value) = inner.SetLength(value)
    override _.Write(buffer, offset, count) = inner.Write(buffer, offset, count)
    override _.Read(buffer, offset, count) =
        reads.Enqueue(())
        inner.Read(buffer, offset, count)
    override _.ReadAsync(buffer: Memory<byte>, token: CancellationToken) =
        reads.Enqueue(())
        inner.ReadAsync(buffer, token)

type Header =
    { Name: string
      Values: string list }

type Request =
    { Method: HttpMethod
      Path: string
      Headers: Header list
      Content: HttpContent option }

type Reply =
    { Status: int
      Raw: string
      Headers: Map<string, string> }

type Server =
    { App: WebApplication
      Client: HttpClient
      Identity: Identity
      Runtime: LocalAccessRuntime
      Origin: string
      Directory: string
      Effects: Effects }

type private Host =
    { App: WebApplication
      Runtime: TaskCompletionSource<LocalAccessRuntime>
      Directory: string
      Effects: Effects }

let header (name: string) (value: string) : Header = { Name = name; Values = [ value ] }
let origin (value: string) : Header = header "Origin" value
let bearer (token: string) : Header = header "Authorization" ("Bearer " + token)

let json (text: string) : JsonElement =
    use document = JsonDocument.Parse(text)
    document.RootElement.Clone()

let field (name: string) (reply: Reply) : JsonElement = (json reply.Raw).GetProperty(name)

let text (name: string) (reply: Reply) : string =
    (field name reply).GetString() |> Option.ofObj
    |> Option.defaultWith (fun () -> failwith "Expected a string field.")

let responseHeader (name: string) (reply: Reply) : string =
    reply.Headers |> Map.tryFind (name.ToLowerInvariant()) |> Option.defaultValue ""

let private responseHeaders (response: HttpResponseMessage) : Map<string, string> =
    Seq.append response.Headers response.Content.Headers
    |> Seq.map (fun pair -> pair.Key.ToLowerInvariant(), String.concat ", " pair.Value)
    |> Map.ofSeq

let request (methodValue: HttpMethod) (path: string) (headers: Header list)
    (body: string option) : Request =
    { Method = methodValue; Path = path; Headers = headers
      Content = body |> Option.map (fun value ->
          new StringContent(value, Encoding.UTF8, "application/json") :> HttpContent) }

let send (server: Server) (value: Request) : Task<Reply> =
    task {
        use message = new HttpRequestMessage(value.Method, value.Path)
        value.Headers |> List.iter (fun item ->
            message.Headers.TryAddWithoutValidation(item.Name, item.Values) |> ignore)
        value.Content |> Option.iter (fun content -> message.Content <- content)
        use! response = server.Client.SendAsync(message)
        let! body = response.Content.ReadAsStringAsync()
        return { Status = int response.StatusCode; Raw = body; Headers = responseHeaders response }
    }

let get (server: Server) (path: string) (headers: Header list) : Task<Reply> =
    request HttpMethod.Get path headers None |> send server

let post (server: Server) (path: string) (headers: Header list) (body: string) : Task<Reply> =
    request HttpMethod.Post path headers (Some body) |> send server

let assertError (status: int) (code: string) (reply: Reply) : unit =
    Assert.Equal(status, reply.Status)
    Assert.Equal(code, text "error" reply)
    Assert.Equal("no-store", responseHeader "Cache-Control" reply)
    if status = 401 then
        let expected = System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "realm=\"Hermes\"").ToString()
        Assert.Equal(expected, responseHeader "WWW-Authenticate" reply)

let assertFields (expected: string list) (reply: Reply) : unit =
    let actual = (json reply.Raw).EnumerateObject() |> Seq.map (fun property -> property.Name) |> Set.ofSeq
    Assert.Equal<Set<string>>(Set.ofList expected, actual)

let private persist (directory: string) (effects: Effects) (context: HttpContext) : Task =
    task {
        effects.Work.Enqueue(())
        use reader = new StreamReader(context.Request.Body, Encoding.UTF8, false, 1024, true)
        let! body = reader.ReadToEndAsync(context.RequestAborted)
        do! File.WriteAllTextAsync(Path.Combine(directory, "accepted.txt"), body, context.RequestAborted)
        context.Response.StatusCode <- 204
    }

let private interactive (effects: Effects) (context: HttpContext) : Task =
    task {
        let prepared = HttpAccessPolicy.preparsed context |> Fixture.ok
        match prepared.Policy with
        | ModelWorkPolicy.ArchiveOnly -> ()
        | ModelWorkPolicy.InvokeModel operation -> effects.Models.Enqueue(operation)
        effects.Work.Enqueue(())
        context.Response.ContentType <- "text/event-stream"
        context.Response.Headers["Cache-Control"] <- StringValues("no-cache")
        do! context.Response.WriteAsync("event: done\ndata: {}\n\n", context.RequestAborted)
    }

let private mcp (context: HttpContext) : Task =
    let supplied =
        if context.Request.Headers["Authorization"].Count = 1 then
            Some(context.Request.Headers["Authorization"].ToString())
        else None
    let access = McpAccess.authenticate McpTestPolicy.policy supplied
    context.Response.StatusCode <- if McpAccess.permits access McpAccess.Mutate then 204 else 403
    Task.CompletedTask

let private mapOrdinary (app: WebApplication) (directory: string) (effects: Effects) : unit =
    app.MapPost("/probe", Func<HttpContext, Task>(persist directory effects))
    |> HttpAccessPolicy.classify (HttpAccessClassification.RequiredCaps(Set.singleton Capability.Mutate))
    |> ignore
    app.MapGet("/config", Func<IResult>(fun () -> Results.Ok()))
    |> HttpAccessPolicy.classify (HttpAccessClassification.RequiredCaps(Set.singleton Capability.ConfigRead))
    |> ignore
    app.MapGet("/archive", Func<IResult>(fun () -> Results.Ok()))
    |> HttpAccessPolicy.classify HttpAccessClassification.AnonymousArchiveRead
    |> ignore

let private mapInteractive (app: WebApplication) (effects: Effects) : unit =
    app.MapPost("/interactive/chat", Func<HttpContext, Task>(interactive effects))
    |> HttpAccessPolicy.classify (HttpAccessClassification.ParsedChatOrSearch InteractiveKind.Chat)
    |> ignore
    app.MapGet("/interactive/search", Func<HttpContext, Task>(interactive effects))
    |> HttpAccessPolicy.classify (HttpAccessClassification.ParsedChatOrSearch InteractiveKind.Search)
    |> ignore
    app.MapPost("/independent", Func<HttpContext, Task>(mcp))
    |> HttpAccessPolicy.classify HttpAccessClassification.McpIndependent
    |> ignore

let private effects () : Effects =
    let moments = ConcurrentQueue<Moment>()
    moments.Enqueue({ Elapsed = TimeSpan.Zero; Utc = Fixture.epoch })
    { Reads = ConcurrentQueue<unit>(); Work = ConcurrentQueue<unit>()
      Models = ConcurrentQueue<ModelOperation>(); Moments = moments }

let private middleware (app: WebApplication) (slot: TaskCompletionSource<LocalAccessRuntime>)
    (effects: Effects) (available: bool) : unit =
    let publicAccess () : PublicAccess option =
        if slot.Task.IsCompletedSuccessfully then Some slot.Task.Result.Public else None
    LocalBoundary.attach app [ BrowserOrigin.text Fixture.preview ]
    app.Use(Func<HttpContext, RequestDelegate, Task>(fun context next ->
        context.Request.Body <- new ReadingStream(context.Request.Body, effects.Reads)
        next.Invoke(context))) |> ignore
    app.UseRouting() |> ignore
    HttpAccessPolicy.attach app Fixture.instance publicAccess { IsAvailable = fun _ -> available }

let private host (available: bool) : Host =
    let directory = Path.Combine(Path.GetTempPath(), "hermes-access-http-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(directory) |> ignore
    let builder = WebApplication.CreateBuilder()
    builder.Logging.ClearProviders() |> ignore
    builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
    HttpAccessPolicy.addStartupValidation builder.Services
    let app = builder.Build()
    let slot = TaskCompletionSource<LocalAccessRuntime>(TaskCreationOptions.RunContinuationsAsynchronously)
    let probe = effects ()
    middleware app slot probe available
    LocalAccessHttp.mapEndpoints app Fixture.instance
    mapOrdinary app directory probe
    mapInteractive app probe
    { App = app; Runtime = slot; Directory = directory; Effects = probe }

let private address (app: WebApplication) : Uri =
    let server = app.Services.GetRequiredService<IServer>()
    let addresses =
        server.Features.Get<IServerAddressesFeature>() |> Option.ofObj
        |> Option.defaultWith (fun () -> failwith "Missing owned HTTP listener address.")
    addresses.Addresses |> Seq.exactlyOne |> Uri

let private runtimeFactory (effects: Effects) (identity: Identity) : LocalAccessRuntime =
    let clock: Clock = { Read = fun () -> effects.Moments.ToArray() |> Array.last }
    let entropy: Entropy = { Bytes = fun (size: int) -> RandomNumberGenerator.GetBytes(size) }
    LocalAccessRuntime.createWith clock entropy { Unavailable = fun () -> () }
        identity Fixture.all (Set.singleton Fixture.preview) |> Fixture.ok

let private activate (host: Host) (ready: bool) : Task<Server> =
    task {
        let uri = address host.App
        Assert.DoesNotContain(uri.Port, [ 21741; 21742 ])
        let identity = { Fixture.identity with ApiOrigin = ApiAudience.create uri.Port |> Fixture.ok }
        let runtime = runtimeFactory host.Effects identity
        host.Runtime.SetResult(runtime)
        if ready then
            let! result = runtime.Lifecycle.StartReady cancellation
            Fixture.ok result |> ignore
        return
            { App = host.App; Identity = identity; Runtime = runtime
              Client = new HttpClient(BaseAddress = uri, Timeout = timeout)
              Origin = ApiAudience.text identity.ApiOrigin; Directory = host.Directory; Effects = host.Effects }
    }

let private cleanup (host: Host) : unit =
    if host.Runtime.Task.IsCompletedSuccessfully then
        host.Runtime.Task.Result.Lifecycle.Stop().WaitAsync(timeout).GetAwaiter().GetResult()
    host.App.StopAsync().GetAwaiter().GetResult()
    host.App.DisposeAsync().AsTask().GetAwaiter().GetResult()
    Directory.Delete(host.Directory, true)

let withServer (ready: bool) (available: bool) (verify: Server -> Task<unit>) : Task =
    task {
        let owned = host available
        try
            do! owned.App.StartAsync()
            let! server = activate owned ready
            use client = server.Client
            do! verify server
        finally cleanup owned
    }

let at (seconds: int) (server: Server) : unit =
    server.Effects.Moments.Enqueue(
        { Elapsed = TimeSpan.FromSeconds(float seconds)
          Utc = Fixture.epoch.AddSeconds(float seconds) })

let creation (capabilities: Set<Capability>) : string =
    JsonSerializer.Serialize
        {| version = 1; instanceId = InstanceId.text Fixture.instance
           challenge = Challenge.text Fixture.challenge
           requestedCapabilities = capabilities |> Set.toArray |> Array.map Capability.name |}

let pairAt (server: Server) (browserOrigin: string) (capabilities: Set<Capability>) : Task<Reply> =
    task {
        let! reply = post server "/api/access/pairings" [ origin browserOrigin ] (creation capabilities)
        Assert.Equal(201, reply.Status)
        return reply
    }

let pair (server: Server) (capabilities: Set<Capability>) : Task<Reply> =
    pairAt server server.Origin capabilities

let approve (server: Server) (pairing: Reply) (capabilities: Set<Capability>) : Task<unit> =
    task {
        let code = text "userCode" pairing |> DisplayCode.tryParse |> Fixture.ok
        let! resolved = server.Runtime.Owner.Resolve server.Identity.InstanceId code cancellation
        match Fixture.ok resolved with
        | Response.PairingResolved view ->
            Assert.True(Challenge.equal Fixture.challenge view.Challenge)
            let request: ApprovalRequest =
                { InstanceId = server.Identity.InstanceId; PairingId = view.PairingId
                  UserCode = view.UserCode; Challenge = view.Challenge; Capabilities = capabilities }
            let! approved = server.Runtime.Owner.Approve request cancellation
            Fixture.ok approved |> ignore
        | _ -> failwith "Expected an owner-resolved pairing."
    }

let exchangeBody (verifier: string) : string =
    JsonSerializer.Serialize
        {| version = 1; instanceId = InstanceId.text Fixture.instance; verifier = verifier |}

let exchangeAt (server: Server) (pairing: Reply) (browserOrigin: string) (verifier: string)
    : Task<Reply> =
    let path = "/api/access/pairings/" + text "pairingId" pairing + "/exchange"
    post server path [ origin browserOrigin ] (exchangeBody verifier)

let exchange (server: Server) (pairing: Reply) : Task<Reply> =
    exchangeAt server pairing server.Origin (Verifier.text Fixture.verifier)

let issue (server: Server) (capabilities: Set<Capability>) : Task<string> =
    task {
        let! pairing = pair server capabilities
        do! approve server pairing capabilities
        let! issued = exchange server pairing
        Assert.Equal(201, issued.Status)
        return text "accessToken" issued
    }
