module Hermes.Tests.HttpRegressionFixture

open System
open System.Collections.Concurrent
open System.IO
open System.Net.Http
open System.Net.Http.Json
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Xunit
open Hermes.Core
open Hermes.Service
open Hermes.Core.LocalAccess

type Server =
    { App: WebApplication
      Client: HttpClient
      Db: Algebra.Database
      Files: TestHelpers.MemFs
      Access: SyntheticAuthenticatedFixtureSupport.Running
      BodyReads: ConcurrentQueue<unit>
      McpAuthorization: Headers.AuthenticationHeaderValue option }

type private Hosted =
    { App: WebApplication
      Authority: SyntheticAuthenticatedFixtureSupport.Authority
      BodyReads: ConcurrentQueue<unit> }

type private Prepared =
    { Hosted: Hosted
      Db: Algebra.Database
      Files: TestHelpers.MemFs }

let timeout = TimeSpan.FromSeconds 10.0

let private mapUi (app: WebApplication) (ui: CanonicalUi.Bundle option) : unit =
    ui |> Option.iter (CanonicalUi.attach app)

let private mapRoutes encoder chat policy (app: WebApplication) db (files: TestHelpers.MemFs) deepDeps =
    let dag = TestHelpers.standardV5Dag ()
    ApiServer.mapRoutesWithSearch encoder app db db files.Fs TestHelpers.silentLogger
        TestHelpers.defaultClock dag chat "/archive" "/config"
    ApiServer.mapMcpRouteWithPolicy encoder policy app db db files.Fs TestHelpers.silentLogger
        TestHelpers.defaultClock dag "/archive" deepDeps

let private middleware
    (hosted: Hosted) origins encoder chat (ui: CanonicalUi.Bundle option) : unit =
    let availability: ModelAvailability =
        { IsAvailable = function
            | ModelOperation.Chat -> Option.isSome chat
            | ModelOperation.Search -> Option.isSome encoder }
    LocalBoundary.attach hosted.App origins
    HttpRequestReadProbe.attach hosted.App hosted.BodyReads
    hosted.App.UseRouting() |> ignore
    SyntheticAuthenticatedFixtureSupport.attach hosted.App hosted.Authority availability
    mapUi hosted.App ui

let private createApp encoder chat policy origins ui db files deepDeps : Hosted =
    let builder = WebApplication.CreateBuilder()
    builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
    let authority = SyntheticAuthenticatedFixtureSupport.create ()
    builder.Services.AddSingleton<SyntheticAuthenticatedFixtureSupport.Authority>(authority) |> ignore
    HttpAccessPolicy.addStartupValidation builder.Services
    let app = builder.Build()
    let hosted: Hosted =
        { App = app; Authority = authority; BodyReads = ConcurrentQueue<unit>() }
    middleware hosted origins encoder chat ui
    mapRoutes encoder chat policy app db files deepDeps
    hosted

let private prepare createDb encoder chat policy origins deepDeps ui : Task<Prepared> =
    task {
        let db = createDb ()
        try
            do! TestHelpers.initV5 db
            let files = TestHelpers.memFs ()
            return
                { Hosted = createApp encoder chat policy origins ui db files deepDeps
                  Db = db; Files = files }
        with error ->
            db.dispose ()
            return raise error
    }

let private defaultClient ready access : Task<HttpClient> =
    if ready then
        SyntheticAuthenticatedFixtureSupport.client access SyntheticAuthenticatedFixtureSupport.all
    else
        SyntheticAuthenticatedFixtureSupport.guest access.Audience |> Task.FromResult

let private start (prepared: Prepared) origins ready : Task<Server> =
    task {
        let hosted = prepared.Hosted
        do! hosted.App.StartAsync()
        let access = SyntheticAuthenticatedFixtureSupport.activate hosted.App hosted.Authority origins
        if ready then do! SyntheticAuthenticatedFixtureSupport.startReady access
        let! client = defaultClient ready access
        return
            { App = hosted.App; Db = prepared.Db; Files = prepared.Files; Client = client
              Access = access; BodyReads = hosted.BodyReads; McpAuthorization = None }
    }

let private cleanup (prepared: Prepared) : unit =
    try SyntheticAuthenticatedFixtureSupport.stop prepared.Hosted.Authority
    finally
        try prepared.Hosted.App.StopAsync().GetAwaiter().GetResult()
        finally
            try prepared.Hosted.App.DisposeAsync().AsTask().GetAwaiter().GetResult()
            finally prepared.Db.dispose ()

let private withHostingServer createDb encoder chat policy origins deepDeps ui ready verify =
    task {
        let! prepared = prepare createDb encoder chat policy origins deepDeps ui
        try
            let! server = start prepared origins ready
            use client = server.Client
            do! verify server
        finally cleanup prepared
    }

let withServerUsing createDb encoder policy origins deepDeps verify =
    withHostingServer createDb encoder None policy origins deepDeps None true verify

let withUiServer (bundle: CanonicalUi.Bundle) origins verify =
    withHostingServer TestHelpers.createDb None None McpAccess.readOnly
        origins None (Some bundle) true verify

let withConfiguredServer encoder policy origins deepDeps verify =
    withServerUsing TestHelpers.createDb encoder policy origins deepDeps verify

let withServer deepDeps verify =
    withConfiguredServer None McpAccess.readOnly [] deepDeps verify

let withChatServer chat encoder verify =
    withHostingServer TestHelpers.createDb encoder chat McpAccess.readOnly [] None None true verify

let withStartingServer verify =
    withHostingServer TestHelpers.createDb None None McpAccess.readOnly [] None None false verify

let newGuestClient (server: Server) : HttpClient =
    SyntheticAuthenticatedFixtureSupport.guest server.Access.Audience

let newScopedClient (capabilities: Set<Capability>) (server: Server) : Task<HttpClient> =
    SyntheticAuthenticatedFixtureSupport.client server.Access capabilities

let withAuthenticatedServer deepDeps verify =
    withConfiguredServer None McpTestPolicy.policy [] deepDeps (fun server ->
        verify
            { server with
                McpAuthorization = Some(Headers.AuthenticationHeaderValue("Bearer", McpTestPolicy.token)) })

let json (text: string) =
    use document = JsonDocument.Parse(text)
    document.RootElement.Clone()

let post (server: Server) (path: string) (body: obj | null) =
    task {
        use request = new HttpRequestMessage(HttpMethod.Post, path)
        request.Content <- JsonContent.Create(body)
        match path, server.McpAuthorization with
        | "/mcp", Some authorization
            when not (server.Client.DefaultRequestHeaders.Contains("Authorization")) ->
            request.Headers.Authorization <- authorization
        | _ -> ()
        use! response = server.Client.SendAsync(request)
        Assert.Equal(200, int response.StatusCode)
        let! text = response.Content.ReadAsStringAsync()
        return json text
    }

let get (server: Server) (path: string) =
    task {
        use! response = server.Client.GetAsync(path)
        Assert.Equal(200, int response.StatusCode)
        let! text = response.Content.ReadAsStringAsync()
        return json text
    }

let mcpCall server name (arguments: obj | null) =
    post server "/mcp"
        (box {| jsonrpc = "2.0"; id = 1; method = "tools/call"
                ``params`` = {| name = name; arguments = arguments |} |})

let payload (response: JsonElement) =
    response.GetProperty("result").GetProperty("content").[0].GetProperty("text").GetString()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failwith "Missing MCP text payload")
    |> json

let applyReflow server documentId operation =
    task {
        let! response =
            post server $"/api/documents/{documentId}/reflow"
                (box {| operation = operation; mode = "apply" |})
        Assert.Equal("running", response.GetProperty("status").GetProperty("lifecycle").GetString())
    }

let seedSource (server: Server) (path: string) (content: string) =
    let folder: string =
        Path.GetDirectoryName(path)
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultWith (fun () -> invalidArg (nameof path) "Source path must have a parent folder")
    task {
        let bytes = Encoding.UTF8.GetBytes(content)
        do! SourceFiles.writeNew server.Db server.Files.Fs path bytes
        let! id =
            server.Db.execScalar
                """INSERT INTO documents
                     (source_type,saved_path,folder_path,original_name,source_path,category,sha256,
                      extracted_date,extracted_amount,extracted_vendor,extracted_abn,
                      extraction_method,ocr_confidence,extracted_at,stage)
                   VALUES ('manual_drop',@path,@folder,'source.png','/original/source.png','invoices',@sha,
                           '2020-01-01',10,'Generated before','old-abn','old-method',0.1,'2000-01-01','embedded')
                   RETURNING id"""
                [ "@path", Database.boxVal path; "@folder", Database.boxVal folder
                  "@sha", Database.boxVal (SourceFiles.hash bytes) ]
        return unbox<int64> id
    }

let readDocument (server: Server) documentId =
    task {
        let! rows = server.Db.execReader "SELECT * FROM documents WHERE id=@id" [ "@id", Database.boxVal documentId ]
        return rows |> List.exactlyOne
    }

let seedCompletions (server: Server) documentId =
    task {
        let! _ =
            server.Db.execNonQuery
                """INSERT INTO stage_completions(document_id,stage_name)
                   VALUES (@id,'extract'),(@id,'triage'),(@id,'deep-comprehend'),(@id,'embed')"""
                [ "@id", Database.boxVal documentId ]
        return ()
    }
