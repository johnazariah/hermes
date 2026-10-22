module Hermes.Tests.ExistingRouteAccessHttpTests

open System
open System.Collections.Concurrent
open System.Net.Http
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Routing
open Xunit
open Hermes.Core
open Hermes.Core.LocalAccess
open Hermes.Service
open Hermes.Tests.HttpRegressionFixture
open Hermes.Tests.RetrievalHttpFixtures

type private Reply =
    { Status: int
      Body: string
      Cache: string }

let private send
    (client: HttpClient) (method: string) (path: string) (body: string) : Task<Reply> =
    task {
        use request = new HttpRequestMessage(HttpMethod(method), path)
        if method <> "GET" then
            request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        use! response = client.SendAsync(request)
        let! text = response.Content.ReadAsStringAsync()
        return
            { Status = int response.StatusCode; Body = text
              Cache =
                if response.Headers.Contains("Cache-Control") then
                    response.Headers.GetValues("Cache-Control") |> String.concat ", "
                else "" }
    }

let private trackedDatabase (calls: ConcurrentQueue<unit>) () : Algebra.Database =
    let db = TestHelpers.createDb ()
    { db with
        execReader = fun sql parameters -> calls.Enqueue(()); db.execReader sql parameters
        execScalar = fun sql parameters -> calls.Enqueue(()); db.execScalar sql parameters
        execNonQuery = fun sql parameters -> calls.Enqueue(()); db.execNonQuery sql parameters
        transaction =
            { new Algebra.Transaction with
                member _.Run<'Value>(work: Algebra.Database -> Task<'Value>) : Task<'Value> =
                    calls.Enqueue(())
                    db.transaction.Run(work) } }

let private assertNoWork
    (server: Server) (calls: ConcurrentQueue<unit>) (reply: Reply) expected : unit =
    Assert.Equal(expected, reply.Status)
    Assert.Contains("no-store", reply.Cache)
    Assert.Empty(calls)
    Assert.Empty(server.BodyReads)
    let error = (json reply.Body).GetProperty("error").GetString()
    Assert.Equal(
        (if expected = 401 then "authentication_required" else "insufficient_capability"), error)

let private deny (method: string) (path: string) (capabilities: Set<Capability> option) =
    let calls = ConcurrentQueue<unit>()
    withServerUsing (trackedDatabase calls) None McpAccess.readOnly [] None (fun server ->
        task {
            use! client =
                capabilities |> Option.map (fun grant -> newScopedClient grant server)
                |> Option.defaultWith (fun () -> newGuestClient server |> Task.FromResult)
            let! before = databaseHash server
            let! sourceBefore = FileKeywordFixtures.fileHashes server
            calls.Clear()
            server.BodyReads.Clear()
            let! reply = send client method path "{"
            assertNoWork server calls reply (if Option.isSome capabilities then 403 else 401)
            let! after = databaseHash server
            let! sourceAfter = FileKeywordFixtures.fileHashes server
            Assert.Equal(before, after)
            Assert.Equal<Map<string, string>>(sourceBefore, sourceAfter)
        })

[<Theory>]
[<InlineData("POST", "/api/sync")>]
[<InlineData("PUT", "/api/settings")>]
[<InlineData("PUT", "/api/preferences")>]
[<InlineData("POST", "/api/dead-letters/dismiss")>]
[<InlineData("PUT", "/api/documents/999999/category")>]
[<InlineData("POST", "/api/documents/999999/move")>]
[<InlineData("POST", "/api/documents/999999/correct")>]
[<InlineData("POST", "/api/documents/999999/reflow")>]
[<InlineData("POST", "/api/documents/999999/reextract")>]
[<InlineData("POST", "/api/documents/999999/reembed")>]
[<InlineData("POST", "/api/documents/999999/recomprehend")>]
[<InlineData("POST", "/api/documents/999999/star")>]
[<InlineData("POST", "/api/documents/999999/tags")>]
[<InlineData("DELETE", "/api/documents/999999/tags/synthetic")>]
[<InlineData("POST", "/api/documents/batch")>]
[<InlineData("POST", "/api/sync/reset")>]
[<InlineData("POST", "/api/suggestions/999999/approve")>]
[<InlineData("POST", "/api/suggestions/999999/reject")>]
[<Trait("Category", "Integration")>]
let ``Published mutation routes reject guests before body database or queue work`` method path =
    deny method path None

[<Theory>]
[<InlineData("/api/settings")>]
[<InlineData("/api/preferences")>]
[<InlineData("/api/sync/accounts")>]
[<Trait("Category", "Integration")>]
let ``Published configuration reads reject guests before database or filesystem work`` path =
    deny "GET" path None

[<Theory>]
[<InlineData("POST", "/api/documents/999999/correct", true)>]
[<InlineData("POST", "/api/documents/999999/reflow", true)>]
[<InlineData("GET", "/api/preferences", false)>]
[<InlineData("GET", "/api/sync/accounts", false)>]
[<Trait("Category", "Integration")>]
let ``A genuine session cannot substitute an unrelated capability`` method path configOnly =
    let grant =
        Set.singleton (if configOnly then Capability.ConfigRead else Capability.Mutate)
    deny method path (Some grant)

let private validateRoutes (routes: IEndpointRouteBuilder) : unit =
    let endpoints = routes.DataSources |> Seq.collect (fun source -> source.Endpoints) |> Seq.toList
    Assert.NotEmpty(endpoints)
    match HttpAccessPolicy.validateEndpoints endpoints with
    | Ok () -> ()
    | Error _ -> Assert.Fail("A registered real route lacks exactly one valid classification.")
    endpoints |> List.iter (fun endpoint ->
        Assert.Single(endpoint.Metadata.GetOrderedMetadata<HttpAccessClassification>()) |> ignore)

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Every registered REST MCP and access endpoint has exactly one classification`` () =
    withServer None (fun server ->
        validateRoutes server.App
        Task.FromResult(()))

[<Theory>]
[<InlineData("/api/categories", 200)>]
[<InlineData("/api/missing-route", 404)>]
[<InlineData("/mcp", 405)>]
[<Trait("Category", "Integration")>]
let ``Guest archive reads and framework terminal responses retain their meanings`` path expected =
    withServer None (fun server ->
        task {
            use guest = newGuestClient server
            let! reply = send guest "GET" path ""
            Assert.Equal(expected, reply.Status)
            Assert.DoesNotContain("<!doctype", reply.Body, StringComparison.OrdinalIgnoreCase)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Starting authority rejects ordinary API before body and database work`` () =
    withStartingServer (fun server ->
        task {
            use guest = newGuestClient server
            let! reply = send guest "POST" "/api/documents/batch" "{"
            Assert.Equal(503, reply.Status)
            Assert.Equal("access_unavailable", (json reply.Body).GetProperty("error").GetString())
            Assert.Empty(server.BodyReads)
            let! count = server.Db.execScalar "SELECT COUNT(*) FROM reflow_operations" []
            Assert.Equal(0L, unbox<int64> count)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Invalid local boundary is rejected before invalid authorization`` () =
    withServer None (fun server ->
        task {
            use guest = newGuestClient server
            use request = new HttpRequestMessage(HttpMethod.Post, "/api/documents/batch")
            request.Headers.Add("Origin", "https://foreign.example")
            request.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", "invalid")
            request.Content <- new StringContent("{", Encoding.UTF8, "application/json")
            use! response = guest.SendAsync(request)
            Assert.Equal(403, int response.StatusCode)
            Assert.Empty(server.BodyReads)
            Assert.False(response.Headers.Contains("WWW-Authenticate"))
        })
