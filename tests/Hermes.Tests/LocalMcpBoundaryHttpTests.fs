module Hermes.Tests.LocalMcpBoundaryHttpTests

open System.Net.Http
open System.Net.Http.Headers
open System.Net.Http.Json
open System.Text.Json
open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture
open Hermes.Tests.RetrievalHttpFixtures

let private discovery server =
    post server "/mcp" (box {| jsonrpc = "2.0"; id = 1; method = "tools/list" |})

let private names (value: JsonElement) =
    value.GetProperty("result").GetProperty("tools").EnumerateArray()
    |> Seq.map (fun tool -> tool.GetProperty("name").GetString() |> Option.ofObj |> Option.defaultValue "")
    |> Seq.toList

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Default repeated discovery excludes every explicitly classified mutation`` () =
    withServer None (fun server ->
        task {
            let expected =
                Hermes.Core.McpServer.toolDefinitions
                |> List.filter (fun tool -> tool.Capability = McpAccess.Read)
                |> List.map (fun tool -> tool.Name)
            let! first = discovery server
            let! second = discovery server
            Assert.Equal<string list>(expected, names first)
            Assert.Equal<string list>(expected, names second)
            Assert.Contains("hermes_get_comprehension", names first)
        })

let private assertDenied server token =
    task {
        server.Client.DefaultRequestHeaders.Authorization <-
            AuthenticationHeaderValue("Bearer", token)
        let! id = seedSource server "/archive/policy/source.png" "immutable"
        let! before = databaseHash server
        let! result = mcpCall server "hermes_reclassify" (box {| document_id = id; new_category = "tax" |})
        Assert.Equal(-32602, result.GetProperty("error").GetProperty("code").GetInt32())
        Assert.Equal("Tool is not available under this MCP capability policy",
            result.GetProperty("error").GetProperty("message").GetString())
        let! after = databaseHash server
        Assert.Equal(before, after)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Default policy rejects mutation even with a bearer token`` () =
    withServer None (fun server -> assertDenied server McpTestPolicy.token)

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Explicit enabled policy rejects a wrong token`` () =
    withConfiguredServer None McpTestPolicy.policy [] None (fun server ->
        assertDenied server "synthetic-wrong-token-not-a-real-credential")

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Explicit enabled authenticated MCP mutation changes metadata without moving source`` () =
    withAuthenticatedServer None (fun server ->
        task {
            let path = "/archive/auth/source.png"
            let! id = seedSource server path "immutable synthetic source"
            let! before = FileKeywordFixtures.fileHashes server
            let! result = mcpCall server "hermes_reclassify" (box {| document_id = id; new_category = "tax" |})
            Assert.False(result.TryGetProperty("error") |> fst)
            Assert.False(result.GetProperty("result").TryGetProperty("isError") |> fst)
            let! document = readDocument server id
            Assert.Equal("tax", (Prelude.RowReader document).String "category" "")
            let! after = FileKeywordFixtures.fileHashes server
            Assert.Equal<Map<string, string>>(before, after)
        })

let private sendBoundary (server: Server) (origin: string option) (host: string) (preflight: bool) =
    task {
        use request = new HttpRequestMessage(
            (if preflight then HttpMethod.Options else HttpMethod.Post), "/mcp")
        request.Headers.Host <- host
        origin |> Option.iter (fun value -> request.Headers.Add("Origin", value))
        if preflight then request.Headers.Add("Access-Control-Request-Method", "POST")
        else request.Content <- JsonContent.Create(box {| jsonrpc = "2.0"; id = 1; method = "tools/list" |})
        use! result = server.Client.SendAsync(request)
        Assert.Equal(403, int result.StatusCode)
        Assert.False(result.Headers.Contains("Access-Control-Allow-Origin"))
    }

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("Category", "Integration")>]
let ``Foreign origin calls and preflights are rejected by the actual host`` preflight =
    withServer None (fun server ->
        let baseAddress =
            server.Client.BaseAddress
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "HTTP test server has no base address")
        sendBoundary server (Some "https://foreign.example") baseAddress.Authority preflight)

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("Category", "Integration")>]
let ``Foreign Host calls and preflights are rejected`` preflight =
    withServer None (fun server ->
        sendBoundary server None "foreign.example" preflight)

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Local no-Origin reads remain usable and do not authenticate mutations`` () =
    withConfiguredServer None McpTestPolicy.policy [] None (fun server ->
        task {
            let! list = discovery server
            Assert.Contains("hermes_search", names list)
            Assert.DoesNotContain("hermes_reclassify", names list)
            let! rejected = mcpCall server "hermes_reclassify" (box {| document_id = 1; new_category = "tax" |})
            Assert.Equal(-32602, rejected.GetProperty("error").GetProperty("code").GetInt32())
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Explicit configured preview origin gets exact non-wildcard preflight permission`` () =
    withConfiguredServer None McpAccess.readOnly [ "http://localhost:5173" ] None (fun server ->
        task {
            use request = new HttpRequestMessage(HttpMethod.Options, "/mcp")
            request.Headers.Add("Origin", "http://localhost:5173")
            request.Headers.Add("Access-Control-Request-Method", "POST")
            request.Headers.Add("Access-Control-Request-Headers", "content-type")
            use! result = server.Client.SendAsync(request)
            Assert.Equal(204, int result.StatusCode)
            Assert.Equal("http://localhost:5173",
                result.Headers.GetValues("Access-Control-Allow-Origin") |> Seq.exactlyOne)
        })
