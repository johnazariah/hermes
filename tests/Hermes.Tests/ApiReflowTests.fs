module Hermes.Tests.ApiReflowTests

open System
open System.Net.Http
open System.Net.Http.Json
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

type private TestServer =
    { App: WebApplication; Client: HttpClient; Db: Algebra.Database }

let private startServer () =
    task {
        let db = TestHelpers.createDb ()
        do! TestHelpers.initV5 db
        let mem = TestHelpers.memFs ()
        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
        let app = builder.Build()
        ApiServer.mapRoutes app db mem.Fs TestHelpers.silentLogger TestHelpers.defaultClock
            (TestHelpers.standardV5Dag ()) None "/archive" "/config"
        do! app.StartAsync()
        let server: IServer = app.Services.GetRequiredService<IServer>()
        let addresses =
            server.Features.Get<IServerAddressesFeature>()
            |> Option.ofObj
            |> Option.defaultWith (fun () -> failwith "No server addresses")
        let address = addresses.Addresses |> Seq.head
        return { App = app; Client = new HttpClient(BaseAddress = Uri address); Db = db }
    }

let private withServer (body: TestServer -> Task<unit>) : Task<unit> =
    task {
        let! server = startServer ()
        try do! body server
        finally
            server.Client.Dispose()
            server.App.StopAsync().GetAwaiter().GetResult()
            server.App.DisposeAsync().AsTask().GetAwaiter().GetResult()
            server.Db.dispose ()
    }

let private insertDoc (db: Algebra.Database) (sha: string) : Task<int64> =
    task {
        let! _ =
            db.execNonQuery
                "INSERT INTO documents(source_type,saved_path,category,sha256) VALUES ('manual_drop','test.pdf','unclassified',@sha)"
                [ ("@sha", Database.boxVal sha) ]
        let! id = db.execScalar "SELECT last_insert_rowid()" []
        return id :?> int64
    }

let private post<'a> (client: HttpClient) (url: string) (body: 'a) : Task<int * string> =
    task {
        let! response = client.PostAsJsonAsync(url, body)
        let! text = response.Content.ReadAsStringAsync()
        return int response.StatusCode, text
    }

let private postEmpty (client: HttpClient) (url: string) : Task<int * string> =
    task {
        let! response = client.PostAsync(url, new StringContent(""))
        let! text = response.Content.ReadAsStringAsync()
        return int response.StatusCode, text
    }

let private root (body: string) : JsonElement = (JsonDocument.Parse(body).RootElement).Clone()

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Api_Reflow_DryRun_IsSafeByDefault`` () =
    withServer (fun server ->
        task {
            let! id = insertDoc server.Db "dry"
            let! code, body = post server.Client $"/api/documents/{id}/reflow" {| operation = "reembed" |}
            Assert.Equal(200, code)
            let json = root body
            let stages = json.GetProperty("plan").GetProperty("invalidatedStages")
            Assert.Equal(1, stages.GetArrayLength())
            Assert.Equal("embed", stages.[0].GetString())
            let! count = server.Db.execScalar "SELECT count(*) FROM reflow_operations" []
            Assert.Equal(0L, count :?> int64)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Api_Reflow_Apply_StatusAndPipelineAreObservable`` () =
    withServer (fun server ->
        task {
            let! id = insertDoc server.Db "apply"
            let! code, body =
                post server.Client $"/api/documents/{id}/reflow" {| operation = "reextract"; mode = "apply" |}
            Assert.Equal(200, code)
            let status = root body |> fun value -> value.GetProperty("status")
            let opId = status.GetProperty("operationId").GetInt64()
            Assert.Equal("running", status.GetProperty("lifecycle").GetString())
            let! fetched = server.Client.GetStringAsync($"/api/reflow/{opId}")
            Assert.Equal("running", (root fetched).GetProperty("lifecycle").GetString())
            let! pipeline = server.Client.GetStringAsync("/api/pipeline")
            let reflow = (root pipeline).GetProperty("reflow")
            Assert.Equal(1L, reflow.GetProperty("running").GetInt64())
            Assert.Equal(4L, reflow.GetProperty("stagesPending").GetInt64())
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Api_Reflow_InvalidKindAndMissingDocument_ReturnBadRequest`` () =
    withServer (fun server ->
        task {
            let! id = insertDoc server.Db "invalid"
            let! invalidCode, _ = post server.Client $"/api/documents/{id}/reflow" {| operation = "bogus" |}
            let! missingCode, _ = post server.Client "/api/documents/999999/reflow" {| operation = "reembed" |}
            Assert.Equal(400, invalidCode)
            Assert.Equal(400, missingCode)
        })

[<Theory>]
[<InlineData("reextract")>]
[<InlineData("recomprehend")>]
[<InlineData("reembed")>]
[<Trait("Category", "Integration")>]
let ``Api_LegacyReflow_ResponseRetainsRequeuedOperationAndDuplicate`` endpoint =
    withServer (fun server ->
        task {
            let! id = insertDoc server.Db $"legacy-{endpoint}"
            let url = $"/api/documents/{id}/{endpoint}"
            let! firstCode, firstBody = postEmpty server.Client url
            let! secondCode, secondBody = postEmpty server.Client url
            let first = root firstBody
            let second = root secondBody
            Assert.Equal(200, firstCode)
            Assert.Equal(200, secondCode)
            Assert.True(first.GetProperty("requeued").GetBoolean())
            Assert.True(second.GetProperty("requeued").GetBoolean())
            Assert.False(first.GetProperty("duplicate").GetBoolean())
            Assert.True(second.GetProperty("duplicate").GetBoolean())
            Assert.Equal(
                first.GetProperty("operationId").GetInt64(),
                second.GetProperty("operationId").GetInt64())
        })
