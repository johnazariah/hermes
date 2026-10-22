module Hermes.Tests.ServiceSyncHttpTests

open System
open System.Net.Http
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.Extensions.DependencyInjection
open Xunit
open Hermes.Core
open Hermes.Service
open Hermes.Tests.ServiceSyncTestSupport

type private Response =
    { Status: int
      Json: System.Text.Json.JsonElement }

let private mapRoutes manual db (host: WebApplication) : unit =
    let files = TestHelpers.memFs ()
    let dag = TestHelpers.standardV5Dag ()
    match manual with
    | Some capability ->
        ApiServer.mapRoutesWithCapabilities capability None host db db files.Fs
            TestHelpers.silentLogger TestHelpers.defaultClock dag None "/archive" "/config"
    | None ->
        ApiServer.mapRoutesWithSearch None host db db files.Fs
            TestHelpers.silentLogger TestHelpers.defaultClock dag None "/archive" "/config"

let private app manual db : WebApplication =
    let builder = WebApplication.CreateBuilder()
    builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
    let authority = SyntheticAuthenticatedFixtureSupport.create ()
    builder.Services.AddSingleton<SyntheticAuthenticatedFixtureSupport.Authority>(authority) |> ignore
    HttpAccessPolicy.addStartupValidation builder.Services
    let host = builder.Build()
    LocalBoundary.attach host []
    host.UseRouting() |> ignore
    SyntheticAuthenticatedFixtureSupport.attach host authority { IsAvailable = fun _ -> false }
    mapRoutes manual db host
    host

let private client (host: WebApplication) : Task<HttpClient> =
    task {
        let authority =
            host.Services.GetRequiredService<SyntheticAuthenticatedFixtureSupport.Authority>()
        let running = SyntheticAuthenticatedFixtureSupport.activate host authority []
        do! SyntheticAuthenticatedFixtureSupport.startReady running
        return! SyntheticAuthenticatedFixtureSupport.client
            running SyntheticAuthenticatedFixtureSupport.all
    }

let private cleanup (host: WebApplication) (db: Algebra.Database) : unit =
    try
        host.Services.GetRequiredService<SyntheticAuthenticatedFixtureSupport.Authority>()
        |> SyntheticAuthenticatedFixtureSupport.stop
    finally
        try host.StopAsync().GetAwaiter().GetResult()
        finally
            try host.DisposeAsync().AsTask().GetAwaiter().GetResult()
            finally db.dispose ()

let private withApi manual verify =
    task {
        let db = TestHelpers.createDb ()
        do! TestHelpers.initV5 db
        let host = app manual db
        try
            do! host.StartAsync()
            use! authorized = client host
            do! verify authorized
        finally cleanup host db
    }

let private post (client: HttpClient) =
    task {
        use content = new StringContent("{}")
        use! response = client.PostAsync("/api/sync", content)
        let! text = response.Content.ReadAsStringAsync()
        return { Status = int response.StatusCode; Json = HttpRegressionFixture.json text }
    }

let private assertAck expected response =
    Assert.Equal(202, response.Status)
    Assert.Equal(expected, response.Json.GetProperty("status").GetString())

let private assertUnavailable reason response =
    Assert.Equal(503, response.Status)
    Assert.Equal("unavailable", response.Json.GetProperty("status").GetString())
    Assert.Equal("manual_sync_unavailable", response.Json.GetProperty("error").GetString())
    Assert.Equal(reason, response.Json.GetProperty("reason").GetString())

[<Fact>]
let ``Legacy API-only hosts explicitly report unavailable`` () =
    withApi None (fun client ->
        task {
            let! response = post client
            assertUnavailable "unsupported_host" response
        })

[<Fact>]
let ``HTTP accepted requests wake real owned loop and coalesce during its cycle`` () =
    withOwner distant distant Gated (fun harness ->
        withApi (Some harness.Owner.Manual) (fun client ->
            task {
                let! first = post client
                assertAck "queued" first
                let! started = next harness.Recording.Started.Reader
                Assert.Equal(1, started)
                let! pending = post client
                let! coalesced = post client
                assertAck "queued" pending
                assertAck "coalesced" coalesced
                release harness
                let! followUp = next harness.Recording.Started.Reader
                Assert.Equal(2, followUp)
            }))

[<Fact>]
let ``Stopping producer is unavailable over actual HTTP`` () =
    withOwner distant distant Immediate (fun harness ->
        withApi (Some harness.Owner.Manual) (fun client ->
            task {
                harness.Owner.Stop ()
                let! response = post client
                assertUnavailable "stopping" response
            }))

[<Fact>]
let ``Host and origin rejection happen before sync admission`` () =
    withOwner distant distant Immediate (fun harness ->
        withApi (Some harness.Owner.Manual) (fun client ->
            task {
                use request = new HttpRequestMessage(HttpMethod.Post, "/api/sync")
                request.Headers.Add("Origin", "https://not-local.example")
                use! response = client.SendAsync(request)
                Assert.Equal(403, int response.StatusCode)
                Assert.Empty(harness.Recording.Runs)
            }))
