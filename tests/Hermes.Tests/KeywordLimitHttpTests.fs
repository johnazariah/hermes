module Hermes.Tests.KeywordLimitHttpTests

open System
open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture
open Hermes.Tests.RetrievalHttpFixtures
open Hermes.Tests.RetrievalRaceFixtures

let private seedOne server documents index =
    task {
        let! id = seedSource server $"/archive/large-limit/{index}.png" $"immutable synthetic {index}"
        do! executeSql server "UPDATE documents SET subject='quartz' WHERE id=@id"
                [ "@id", Database.boxVal id ]
        return id :: documents
    }

let private seed server =
    [ 1 .. 125 ] |> Prelude.foldTask (seedOne server) []

let private modeSuffix mode =
    if String.IsNullOrEmpty mode then "" else "&mode=" + mode

let private modeArgument mode =
    if String.IsNullOrEmpty mode then "" else $""","mode":"{mode}" """

let private large server mode documents count =
    task {
        let! http = get server $"/api/search?q=quartz{modeSuffix mode}&limit={count}"
        let! mcp = rawCall server "hermes_search"
                        $"""{{"query":"quartz","limit":{count}{modeArgument mode}}}"""
        let results = (payload mcp).GetProperty("results")
        Assert.Equal(125, http.GetArrayLength())
        Assert.Equal<int64 list>(List.sort documents, ids http)
        Assert.Equal<int64 list>(ids http, ids results)
    }

let private defaults server mode =
    task {
        let! http = get server $"/api/search?q=quartz{modeSuffix mode}"
        let! mcp = rawCall server "hermes_search" $"""{{"query":"quartz"{modeArgument mode}}}"""
        Assert.Equal(50, http.GetArrayLength())
        Assert.Equal(20, (payload mcp).GetProperty("results").GetArrayLength())
    }

[<Theory>]
[<InlineData("")>]
[<InlineData("keyword")>]
[<Trait("Category", "Integration")>]
let ``Default and explicit HTTP MCP keyword requests retain large positive limits without capping`` mode =
    withServer None (fun server ->
        task {
            let! documents = seed server
            let! before = checkpoint server
            do! defaults server mode
            do! large server mode documents 200
            do! large server mode documents Int32.MaxValue
            let! after = checkpoint server
            Assert.Equal<Checkpoint>(before, after)
        })

[<Theory>]
[<InlineData("")>]
[<InlineData("0")>]
[<InlineData("-1")>]
[<InlineData("2147483648")>]
[<InlineData("-2147483649")>]
[<InlineData("1.5")>]
[<InlineData("not-an-integer")>]
[<InlineData("50&limit=2")>]
[<Trait("Category", "Integration")>]
let ``HTTP rejects invalid nonpositive overflow and repeated limits instead of defaulting or clamping`` value =
    withServer None (fun server ->
        task {
            let! result = response server $"/api/search?q=quartz&limit={value}" 400
            Assert.Equal("invalid_request", text "status" result)
        })

[<Theory>]
[<InlineData("0")>]
[<InlineData("-1")>]
[<InlineData("2147483648")>]
[<InlineData("-2147483649")>]
[<InlineData("9223372036854775808")>]
[<InlineData("1.5")>]
[<InlineData("\"20\"")>]
[<InlineData("null")>]
[<InlineData("false")>]
[<InlineData("[]")>]
[<InlineData("{}")>]
[<Trait("Category", "Integration")>]
let ``MCP rejects invalid nonpositive and overflowing limits without narrowing or fallback`` value =
    withServer None (fun server ->
        task {
            let! result = rawCall server "hermes_search" $"""{{"query":"quartz","limit":{value}}}"""
            Assert.Equal(-32602, result.GetProperty("error").GetProperty("code").GetInt32())
        })

[<Theory>]
[<InlineData("semantic")>]
[<InlineData("hybrid")>]
[<Trait("Category", "Integration")>]
let ``Vector-only limit bounds reject before encoder lookup through HTTP and MCP`` mode =
    withServer None (fun server ->
        task {
            let! http = response server $"/api/search?q=quartz&mode={mode}&limit=101" 400
            Assert.Equal("invalid_request", text "status" http)
            let! mcp = mcpCall server "hermes_search" (box {| query = "quartz"; mode = mode; limit = 101 |})
            Assert.Equal(-32602, mcp.GetProperty("error").GetProperty("code").GetInt32())
        })
