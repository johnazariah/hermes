module Hermes.Tests.TypedSearchHttpTests

open System.Net.Http
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture
open Hermes.Tests.RetrievalHttpFixtures

let private expected mode documents =
    match documents with
    | [ first; second; third ] ->
        match mode with
        | "keyword" -> [ first; third ]
        | "semantic" -> [ first; second ]
        | _ -> [ first; second; third ]
    | _ -> failwith "Expected three seeded identities"

let private assertMode server mode documents =
    task {
        use! http = server.Client.GetAsync($"/api/search?q=quartz&mode={mode}&limit=100")
        Assert.Equal(200, int http.StatusCode)
        Assert.Equal(mode, http.Headers.GetValues("X-Hermes-Search-Mode") |> Seq.exactlyOne)
        let coverage = if mode = "keyword" then "not_applicable" else "2/3"
        Assert.Equal(coverage, http.Headers.GetValues("X-Hermes-Vector-Coverage") |> Seq.exactlyOne)
        let! text = http.Content.ReadAsStringAsync()
        let! mcp = mcpCall server "hermes_search" (box {| query = "quartz"; mode = mode; limit = 100 |})
        let result = payload mcp
        Assert.Equal(mode, result.GetProperty("mode").GetString())
        Assert.Equal<int64 list>(expected mode documents |> List.sort, ids (json text))
        Assert.Equal<int64 list>(ids (json text), ids (result.GetProperty("results")))
    }

[<Theory>]
[<InlineData("keyword")>]
[<InlineData("semantic")>]
[<InlineData("hybrid")>]
[<Trait("Category", "Integration")>]
let ``Actual HTTP and MCP use the same typed mode and stable IDs`` mode =
    configured (Some encoder) (fun server ->
        task {
            let! documents = seedSearch server
            do! assertMode server mode documents
        })

[<Theory>]
[<InlineData("semantic")>]
[<InlineData("hybrid")>]
[<Trait("Category", "Integration")>]
let ``Missing query encoder is explicit and never keyword fallback`` mode =
    configured None (fun server ->
        task {
            let! _ = seedSearch server
            let! http = response server $"/api/search?q=quartz&mode={mode}" 503
            Assert.Equal("query_encoder_not_configured", http.GetProperty("error").GetString())
            let! mcp = mcpCall server "hermes_search" (box {| query = "quartz"; mode = mode |})
            Assert.True(mcp.GetProperty("result").GetProperty("isError").GetBoolean())
            Assert.Equal("unavailable", (payload mcp).GetProperty("status").GetString())
        })

[<Theory>]
[<InlineData("semantic")>]
[<InlineData("hybrid")>]
[<Trait("Category", "Integration")>]
let ``Empty vector index is explicit through both transports`` mode =
    configured (Some encoder) (fun server ->
        task {
            let! _ = seedSource server "/archive/search/no-index.png" "immutable"
            let! http = response server $"/api/search?q=quartz&mode={mode}" 503
            Assert.Equal("vector_index_unavailable", http.GetProperty("error").GetString())
            let! mcp = mcpCall server "hermes_search" (box {| query = "quartz"; mode = mode |})
            Assert.Equal("vector_index_unavailable", (payload mcp).GetProperty("error").GetString())
        })

[<Theory>]
[<InlineData("semantic", false)>]
[<InlineData("hybrid", false)>]
[<InlineData("semantic", true)>]
[<InlineData("hybrid", true)>]
[<Trait("Category", "Integration")>]
let ``Failed or invalid query embeddings never return keyword evidence`` mode invalid =
    let client =
        { encoder with
            embed = fun _ ->
                Task.FromResult(if invalid then Ok [| 0.0f |] else Error "synthetic encoder failure") }
    configured (Some client) (fun server ->
        task {
            let! _ = seedSearch server
            let code = if invalid then "invalid_query_vector" else "query_embedding_failed"
            let! http = response server $"/api/search?q=quartz&mode={mode}" 503
            Assert.Equal(code, http.GetProperty("error").GetString())
            let! mcp = mcpCall server "hermes_search" (box {| query = "quartz"; mode = mode |})
            Assert.Equal(code, (payload mcp).GetProperty("error").GetString())
        })

[<Theory>]
[<InlineData("null")>]
[<InlineData("false")>]
[<InlineData("[]")>]
[<InlineData("{}")>]
[<InlineData("1")>]
[<InlineData("\"unknown\"")>]
[<Trait("Category", "Integration")>]
let ``MCP mode wrong shapes and unknown values are invalid params`` mode =
    configured (Some encoder) (fun server ->
        task {
            let! result = rawCall server "hermes_search" $"""{{"query":"quartz","mode":{mode}}}"""
            Assert.Equal(-32602, result.GetProperty("error").GetProperty("code").GetInt32())
        })

[<Theory>]
[<InlineData("unknown")>]
[<InlineData("semantic&mode=keyword")>]
[<InlineData("")>]
[<Trait("Category", "Integration")>]
let ``HTTP rejects unknown empty and repeated mode values`` mode =
    configured (Some encoder) (fun server ->
        task {
            let! result = response server $"/api/search?q=quartz&mode={mode}" 400
            Assert.Equal("invalid_request", result.GetProperty("status").GetString())
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Staged chunks without current output and completion are not evidence`` () =
    configured (Some encoder) (fun server ->
        task {
            let! documents = seedSearch server
            let target = documents |> List.head
            do!
                executeSql server
                    "DELETE FROM stage_completions WHERE document_id=@id AND stage_name='embed'"
                    [ "@id", Database.boxVal target ]
            let! http = get server "/api/search?q=quartz&mode=semantic"
            let! mcp = mcpCall server "hermes_search" (box {| query = "quartz"; mode = "semantic" |})
            Assert.DoesNotContain(target, ids http)
            Assert.Equal<int64 list>(ids http, ids ((payload mcp).GetProperty("results")))
            Assert.Equal(1, (payload mcp).GetProperty("coverage").GetProperty("indexedDocuments").GetInt32())
        })
