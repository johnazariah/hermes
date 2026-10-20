module Hermes.Tests.LocalAccessHttpTests

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Logging
open Xunit
open Hermes.Core
open Hermes.Core.LocalAccess
open Hermes.Service
open Hermes.Tests.LocalAccessDomainTests
open Hermes.Tests.LocalAccessHttpFixture

module Protocol =
    [<Fact>]
    let ``anonymous status has the exact public identity and effective archive capability`` () =
        withServer true true (fun server -> task {
            let! reply = get server "/api/access" []
            Assert.Equal(200, reply.Status)
            assertFields [ "version"; "instanceId"; "processId"; "processStartTimeUtc"; "origin"
                           "state"; "capabilities"; "expiresAt"; "pairingMethod" ] reply
            Assert.Equal(server.Origin, text "origin" reply)
            Assert.Equal("anonymous", text "state" reply)
            Assert.Equal(JsonValueKind.Null, (field "expiresAt" reply).ValueKind)
            Assert.Equal("ownerApproval", text "pairingMethod" reply)
            Assert.Equal("no-store", responseHeader "Cache-Control" reply)
        })

    [<Fact>]
    let ``creating a pairing supplies no authority and polling remains pending without owner approval`` () =
        withServer true true (fun server -> task {
            let! pairing = pair server Fixture.all
            assertFields [ "version"; "instanceId"; "pairingId"; "userCode"; "browserOrigin"
                           "apiOrigin"; "requestedCapabilities"; "expiresAt"; "status" ] pairing
            let! denied = post server "/probe" [] "not admitted"
            assertError 401 "authentication_required" denied
            let! pending = exchange server pairing
            Assert.Equal(202, pending.Status)
            Assert.Equal("2", responseHeader "Retry-After" pending)
            assertFields [ "version"; "instanceId"; "status"; "expiresAt" ] pending
            Assert.Empty(server.Effects.Work)
        })

    [<Fact>]
    let ``owner approval and verifier exchange admit a persisted protected outcome`` () =
        withServer true true (fun server -> task {
            let! token = issue server (Set.singleton Capability.Mutate)
            let! accepted = post server "/probe" [ bearer token ] "synthetic persisted outcome"
            Assert.Equal(204, accepted.Status)
            Assert.Equal("synthetic persisted outcome", File.ReadAllText(Path.Combine(server.Directory, "accepted.txt")))
            Assert.Single(server.Effects.Work) |> ignore
            let! status = get server "/api/access" [ bearer token ]
            Assert.Equal("authenticated", text "state" status)
            Assert.Equal("no-store", responseHeader "Cache-Control" accepted)
        })

    [<Fact>]
    let ``concurrent exchanges issue exactly one session`` () =
        withServer true true (fun server -> task {
            let! pairing = pair server Fixture.all
            do! approve server pairing Fixture.all
            let! replies = Task.WhenAll([| exchange server pairing; exchange server pairing |])
            Assert.Equal<int array>([| 201; 409 |], replies |> Array.map (fun value -> value.Status) |> Array.sort)
            let issued = replies |> Array.find (fun value -> value.Status = 201)
            assertFields [ "version"; "instanceId"; "tokenType"; "accessToken"; "capabilities"; "expiresAt" ] issued
            let credential = text "accessToken" issued |> SessionCredential.tryParse |> Fixture.ok
            Assert.True(SessionCredential.text credential = text "accessToken" issued)
            Assert.Equal("<redacted access value>", string credential)
            Assert.Equal("<redacted access value>", sprintf "%A" credential)
        })

    [<Fact>]
    let ``preview browser origin remains distinct from API audience and binds the session`` () =
        withServer true true (fun server -> task {
            let preview = BrowserOrigin.text Fixture.preview
            let! pairing = pairAt server preview Fixture.all
            Assert.Equal(preview, text "browserOrigin" pairing)
            Assert.Equal(server.Origin, text "apiOrigin" pairing)
            do! approve server pairing Fixture.all
            let! wrong = exchange server pairing
            assertError 403 "session_origin_mismatch" wrong
            let! issued = exchangeAt server pairing preview (Verifier.text Fixture.verifier)
            let token = text "accessToken" issued
            let! denied = get server "/api/access" [ origin server.Origin; bearer token ]
            assertError 403 "session_origin_mismatch" denied
        })

    [<Fact>]
    let ``revocation is not idempotent with a revoked credential`` () =
        withServer true true (fun server -> task {
            let! token = issue server Fixture.all
            let revoke = request HttpMethod.Delete "/api/access/session" [ bearer token ] None
            let! first = send server revoke
            Assert.Equal(204, first.Status)
            let! second = send server revoke
            assertError 401 "invalid_credential" second
            let! denied = post server "/probe" [ bearer token ] "never persisted"
            assertError 401 "invalid_credential" denied
            Assert.Empty(server.Effects.Work)
        })

    [<Fact>]
    let ``absolute session expiry uses the runtime clock`` () =
        withServer true true (fun server -> task {
            let! token = issue server Fixture.all
            at 1800 server
            let! denied = post server "/probe" [ bearer token ] "never persisted"
            assertError 401 "credential_expired" denied
            Assert.Empty(server.Effects.Work)
            Assert.False(File.Exists(Path.Combine(server.Directory, "accepted.txt")))
        })

    [<Fact>]
    let ``wrong instance credentials are rejected before protected work`` () =
        withServer true true (fun server -> task {
            let! token = issue server Fixture.all
            let foreign = token.Replace(InstanceId.text Fixture.instance, InstanceId.text Fixture.foreign)
            let! denied = post server "/probe" [ bearer foreign ] "never persisted"
            assertError 401 "wrong_instance" denied
            Assert.Empty(server.Effects.Work)
        })

module StrictInputs =
    let private checkBody (server: Server) (body: string) (expected: int) : Task<unit> =
        task {
            let! reply = post server "/api/access/pairings" [ origin server.Origin ] body
            assertError expected "invalid_request" reply
            return ()
        }

    [<Fact>]
    let ``duplicate unknown version BOM trailing JSON and authority-array violations fail closed`` () =
        withServer true true (fun server -> task {
            let valid = creation Fixture.all
            let bodies =
                [ valid.Replace("\"version\":1", "\"version\":1,\"version\":1")
                  valid.Replace("\"version\":1", "\"version\":2")
                  valid.Replace("\"version\":1", "\"Version\":1")
                  valid.Replace("\"version\":1", "\"unknown\":{},\"version\":1")
                  "\uFEFF" + valid; valid + "{}"
                  valid.Replace("[\"config.read\",\"model.invoke\",\"mutate\"]", "[\"mutate\",\"config.read\"]")
                  valid.Replace("[\"config.read\",\"model.invoke\",\"mutate\"]", "[\"mutate\",\"mutate\"]") ]
            do! bodies |> Prelude.foldTask (fun () body -> checkBody server body 400) ()
        })

    [<Fact>]
    let ``body size is bounded before JSON parsing`` () =
        withServer true true (fun server -> task {
            let! reply = post server "/api/access/pairings" [ origin server.Origin ] (String.replicate 4097 " ")
            assertError 413 "request_too_large" reply
            Assert.Empty(server.Effects.Work)
        })

    let private rawContent (bytes: byte array) (mediaType: string) : HttpContent =
        let content = new ByteArrayContent(bytes)
        content.Headers.TryAddWithoutValidation("Content-Type", mediaType) |> ignore
        content

    let private rawRequest (server: Server) (content: HttpContent) : Request =
        { Method = HttpMethod.Post; Path = "/api/access/pairings"
          Headers = [ origin server.Origin ]; Content = Some content }

    [<Fact>]
    let ``media type invalid UTF8 and excessive depth are rejected`` () =
        withServer true true (fun server -> task {
            let! media = rawContent (Encoding.UTF8.GetBytes(creation Fixture.all)) "text/plain"
                         |> rawRequest server |> send server
            assertError 415 "unsupported_media_type" media
            let! utf8 = rawContent [| 0xC3uy; 0x28uy |] "application/json"
                        |> rawRequest server |> send server
            assertError 400 "invalid_request" utf8
            let deep = "{\"unknown\":" + String.replicate 9 "[" + "0" + String.replicate 9 "]" + "}"
            let! depth = post server "/api/access/pairings" [ origin server.Origin ] deep
            assertError 400 "invalid_request" depth
        })

    [<Fact>]
    let ``missing or noncanonical pairing origins cannot create a pairing`` () =
        withServer true true (fun server -> task {
            let before = server.Effects.Reads.Count
            let! missing = post server "/api/access/pairings" [] (creation Fixture.all)
            assertError 400 "invalid_request" missing
            Assert.Equal(before, server.Effects.Reads.Count)
            let! invalid = post server "/api/access/pairings" [ origin "null" ] (creation Fixture.all)
            Assert.Equal(403, invalid.Status)
            Assert.Equal(before, server.Effects.Reads.Count)
            let! legacy = post server "/api/access/pairings" [ origin "http://localhost:21741" ] (creation Fixture.all)
            assertError 400 "invalid_request" legacy
        })

    [<Fact>]
    let ``malformed present Authorization never becomes anonymous`` () =
        withServer true true (fun server -> task {
            let! token = issue server Fixture.all
            let headers =
                [ [ header "Authorization" ("Bearer  " + token) ]
                  [ header "Authorization" ("Bearer\t" + token) ]
                  [ header "Authorization" ("Bearer " + token + ",Bearer " + token) ]
                  [ { Name = "Authorization"; Values = [ "Bearer " + token; "Bearer " + token ] } ]
                  [ header "Authorization" (String.replicate 1025 "x") ] ]
            do!
                headers
                |> Prelude.foldTask (fun () supplied ->
                    task {
                        let! reply = get server "/api/access" supplied
                        assertError 401 "invalid_authorization" reply
                    }) ()
        })

    [<Fact>]
    let ``scheme is case insensitive but token canonicality is not repaired`` () =
        withServer true true (fun server -> task {
            let! token = issue server Fixture.all
            let! good = get server "/api/access" [ header "Authorization" ("bEaReR " + token) ]
            Assert.Equal(200, good.Status)
            let! bad = get server "/api/access" [ bearer (token + "=") ]
            assertError 401 "invalid_credential" bad
        })

    [<Fact>]
    let ``cookies URLs and request bodies never supply authority`` () =
        withServer true true (fun server -> task {
            let! token = issue server Fixture.all
            let headers = [ header "Cookie" ("accessToken=" + token) ]
            let! denied = post server ("/probe?accessToken=" + token) headers token
            assertError 401 "authentication_required" denied
            Assert.Empty(server.Effects.Work)
        })

module Admission =
    [<Fact>]
    let ``starting and stopped runtimes reject ordinary work without reading the body`` () =
        withServer false true (fun server -> task {
            let! starting = post server "/probe" [] "not read"
            assertError 503 "access_unavailable" starting
            Assert.Empty(server.Effects.Reads)
            Assert.Empty(server.Effects.Work)
            do! server.Runtime.Lifecycle.Stop()
            let! stopped = get server "/archive" []
            assertError 503 "access_unavailable" stopped
        })

    [<Fact>]
    let ``capability denial occurs before mutation parsing or persistence`` () =
        withServer true true (fun server -> task {
            let! token = issue server (Set.singleton Capability.ConfigRead)
            let before = server.Effects.Reads.Count
            let! denied = post server "/probe" [ bearer token ] "malformed mutation body"
            assertError 403 "insufficient_capability" denied
            Assert.Equal(before, server.Effects.Reads.Count)
            Assert.Empty(server.Effects.Work)
            Assert.False(File.Exists(Path.Combine(server.Directory, "accepted.txt")))
        })

    [<Fact>]
    let ``anonymous explicit archive chat succeeds without model work and remains no-store SSE`` () =
        withServer true true (fun server -> task {
            let! reply = post server "/interactive/chat" [] """{"query":"synthetic","aiEnabled":false}"""
            Assert.Equal(200, reply.Status)
            Assert.StartsWith("text/event-stream", responseHeader "Content-Type" reply)
            Assert.Equal("no-store", responseHeader "Cache-Control" reply)
            Assert.Empty(server.Effects.Models)
            Assert.Single(server.Effects.Work) |> ignore
        })

    [<Fact>]
    let ``chat requires explicit Boolean AI selection and denies AI before SSE`` () =
        withServer true true (fun server -> task {
            let! invalid = post server "/interactive/chat" [] """{"query":"synthetic"}"""
            assertError 400 "invalid_request" invalid
            let! denied = post server "/interactive/chat" [] """{"query":"synthetic","aiEnabled":true}"""
            assertError 401 "authentication_required" denied
            Assert.StartsWith("application/json", responseHeader "Content-Type" denied)
            Assert.Empty(server.Effects.Work)
            Assert.Empty(server.Effects.Models)
        })

    [<Fact>]
    let ``semantic search needs model authority while keyword search does not`` () =
        withServer true true (fun server -> task {
            let! keyword = get server "/interactive/search?q=synthetic&mode=keyword" []
            Assert.Equal(200, keyword.Status)
            let! denied = get server "/interactive/search?q=synthetic&mode=semantic" []
            assertError 401 "authentication_required" denied
            Assert.Empty(server.Effects.Models)
            let! token = issue server (Set.singleton Capability.ModelInvoke)
            let! allowed = get server "/interactive/search?q=synthetic&mode=hybrid" [ bearer token ]
            Assert.Equal(200, allowed.Status)
            Assert.Single(server.Effects.Models) |> ignore
        })

    [<Fact>]
    let ``enabled but unavailable model returns a truthful pre-stream error`` () =
        withServer true false (fun server -> task {
            let! token = issue server (Set.singleton Capability.ModelInvoke)
            let! reply = post server "/interactive/chat" [ bearer token ] """{"query":"synthetic","aiEnabled":true}"""
            assertError 503 "model_unavailable" reply
            Assert.StartsWith("application/json", responseHeader "Content-Type" reply)
            Assert.Empty(server.Effects.Work)
            Assert.Empty(server.Effects.Models)
        })

    [<Fact>]
    let ``MCP remains independent of HTTP bearer interpretation`` () =
        withServer true true (fun server -> task {
            let! token = issue server Fixture.all
            let! denied = post server "/independent" [ bearer token ] "{}"
            Assert.Equal(403, denied.Status)
            let! accepted = post server "/independent" [ bearer McpTestPolicy.token ] "{}"
            Assert.Equal(204, accepted.Status)
        })

    [<Fact>]
    let ``unknown paths and methods are terminal and owner operations are never HTTP routes`` () =
        withServer true true (fun server -> task {
            let! unknown = get server "/does-not-exist" []
            Assert.Equal(404, unknown.Status)
            let! methodValue = post server "/api/access" [] "{}"
            Assert.Equal(405, methodValue.Status)
            let! owner = post server "/api/access/approve" [] "{}"
            Assert.Equal(404, owner.Status)
            Assert.Empty(server.Effects.Work)
        })

    [<Fact>]
    let ``CORS preserves search exposure and exposes authentication headers`` () =
        withServer true true (fun server -> task {
            let! reply = get server "/config" [ origin server.Origin ]
            assertError 401 "authentication_required" reply
            Assert.Equal(server.Origin, responseHeader "Access-Control-Allow-Origin" reply)
            let exposed = responseHeader "Access-Control-Expose-Headers" reply
            Assert.Contains("X-Hermes-Search-Mode", exposed)
            Assert.Contains("X-Hermes-Vector-Coverage", exposed)
            Assert.Contains("Retry-After", exposed)
            Assert.Contains("WWW-Authenticate", exposed)
        })

module BoundsAndMetadata =
    [<Fact>]
    let ``invalid exchange rejection commits the runtime rate window`` () =
        withServer true true (fun server -> task {
            let path = "/api/access/pairings/" + String.replicate 22 "A" + "/exchange"
            let body = exchangeBody (String.replicate 42 "A" + "B")
            do!
                [ 1 .. 120 ]
                |> Prelude.foldTask (fun () _ ->
                    task {
                        let! reply = post server path [ origin server.Origin ] body
                        assertError 401 "invalid_pairing_proof" reply
                    }) ()
            let! limited = post server path [ origin server.Origin ] body
            assertError 429 "access_rate_limited" limited
        })

    let private createMany (server: Server) (count: int) : Task<unit> =
        [ 1 .. count ] |> Prelude.foldTask (fun () _ -> task {
            let! _ = pair server Fixture.all
            return ()
        }) ()

    [<Fact>]
    let ``pairing capacity and creation rate are runtime decisions exposed truthfully`` () =
        withServer true true (fun server -> task {
            do! createMany server 30
            let! rate = post server "/api/access/pairings" [ origin server.Origin ] (creation Fixture.all)
            assertError 429 "access_rate_limited" rate
            at 60 server
            do! createMany server 2
            let! capacity = post server "/api/access/pairings" [ origin server.Origin ] (creation Fixture.all)
            assertError 429 "access_capacity_reached" capacity
        })

    let private invalidApplication (classifications: HttpAccessClassification list) : WebApplication =
        let builder = WebApplication.CreateBuilder()
        builder.Logging.ClearProviders() |> ignore
        builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
        HttpAccessPolicy.addStartupValidation builder.Services
        let app = builder.Build()
        let endpoint = app.MapGet("/unclassified", Func<string>(fun () -> "never"))
        classifications |> List.iter (fun classification ->
            endpoint |> HttpAccessPolicy.classify classification |> ignore)
        app

    let private invalidStartup (classifications: HttpAccessClassification list) : Task =
        task {
            let app = invalidApplication classifications
            try
                let! _ = Assert.ThrowsAsync<InvalidOperationException>(Func<Task>(fun () -> app.StartAsync()))
                ()
            finally app.DisposeAsync().AsTask().GetAwaiter().GetResult()
        }

    [<Fact>]
    let ``missing application classification fails startup`` () : Task =
        invalidStartup []

    [<Fact>]
    let ``conflicting application classification fails startup`` () : Task =
        invalidStartup
            [ HttpAccessClassification.AnonymousArchiveRead
              HttpAccessClassification.McpIndependent ]

    [<Fact>]
    let ``empty required capability classification fails startup`` () : Task =
        invalidStartup [ HttpAccessClassification.RequiredCaps Set.empty ]
