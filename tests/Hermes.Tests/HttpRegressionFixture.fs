module Hermes.Tests.HttpRegressionFixture

open System
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
open Microsoft.Extensions.DependencyInjection
open Xunit
open Hermes.Core
open Hermes.Service

type Server =
    { App: WebApplication
      Client: HttpClient
      Db: Algebra.Database
      Files: TestHelpers.MemFs }

let timeout = TimeSpan.FromSeconds 10.0

let private createApp encoder policy origins ui db (files: TestHelpers.MemFs) deepDeps =
    let builder = WebApplication.CreateBuilder()
    builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
    let app = builder.Build()
    LocalBoundary.attach app origins
    ui |> Option.iter (CanonicalUi.attach app)
    let dag = TestHelpers.standardV5Dag ()
    ApiServer.mapRoutesWithSearch encoder app db db files.Fs TestHelpers.silentLogger
        TestHelpers.defaultClock dag None "/archive" "/config"
    ApiServer.mapMcpRouteWithPolicy encoder policy app db db files.Fs TestHelpers.silentLogger
        TestHelpers.defaultClock dag "/archive" deepDeps
    app

let private address (app: WebApplication) =
    let server = app.Services.GetRequiredService<IServer>()
    server.Features.Get<IServerAddressesFeature>()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failwith "No HTTP server addresses")
    |> fun feature -> feature.Addresses |> Seq.head

let private start createDb encoder policy origins deepDeps ui =
    task {
        let db = createDb ()
        do! TestHelpers.initV5 db
        let files = TestHelpers.memFs ()
        let app = createApp encoder policy origins ui db files deepDeps
        do! app.StartAsync()
        return
            { App = app; Db = db; Files = files
              Client = new HttpClient(BaseAddress = Uri(address app), Timeout = timeout) }
    }

let private withHostingServer createDb encoder policy origins deepDeps ui verify =
    task {
        let! server = start createDb encoder policy origins deepDeps ui
        try do! verify server
        finally
            server.Client.Dispose()
            server.App.StopAsync().GetAwaiter().GetResult()
            server.App.DisposeAsync().AsTask().GetAwaiter().GetResult()
            server.Db.dispose ()
    }

let withServerUsing createDb encoder policy origins deepDeps verify =
    withHostingServer createDb encoder policy origins deepDeps None verify

let withUiServer bundle origins verify =
    withHostingServer TestHelpers.createDb None McpAccess.readOnly origins None (Some bundle) verify

let withConfiguredServer encoder policy origins deepDeps verify =
    withServerUsing TestHelpers.createDb encoder policy origins deepDeps verify

let withServer deepDeps verify =
    withConfiguredServer None McpAccess.readOnly [] deepDeps verify

let withAuthenticatedServer deepDeps verify =
    withConfiguredServer None McpTestPolicy.policy [] deepDeps (fun server ->
        server.Client.DefaultRequestHeaders.Authorization <-
            Headers.AuthenticationHeaderValue("Bearer", McpTestPolicy.token)
        verify server)

let json (text: string) =
    use document = JsonDocument.Parse(text)
    document.RootElement.Clone()

let post (server: Server) (path: string) (body: obj | null) =
    task {
        use! response = server.Client.PostAsJsonAsync(path, body)
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
