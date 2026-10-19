module Hermes.Tests.UnknownToolHttpTests

open System.Net.Http.Headers
open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture
open Hermes.Tests.RetrievalHttpFixtures
open Hermes.Tests.RetrievalRaceFixtures

let private verify server name =
    task {
        let! before = checkpoint server
        let! result = mcpCall server name (box {| document_id = 1 |})
        Assert.Equal(-32602, result.GetProperty("error").GetProperty("code").GetInt32())
        Assert.Equal($"Unknown tool: {name}", result.GetProperty("error").GetProperty("message").GetString())
        let! listing = post server "/mcp" (box {| jsonrpc = "2.0"; id = 2; method = "tools/list" |})
        let names =
            listing.GetProperty("result").GetProperty("tools").EnumerateArray()
            |> Seq.map (text "name") |> Seq.toList
        Assert.DoesNotContain(name, names)
        let! after = checkpoint server
        Assert.Equal<Checkpoint>(before, after)
    }

let private session setting name server =
    if setting >= 2 then
        let token = if setting = 3 then McpTestPolicy.token else "synthetic-invalid-token"
        server.Client.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", token)
    verify server name

[<Theory>]
[<InlineData("hermes_legacy_reclassify_page", 0)>]
[<InlineData("hermes_legacy_reclassify_page", 1)>]
[<InlineData("hermes_legacy_reclassify_page", 2)>]
[<InlineData("hermes_legacy_reclassify_page", 3)>]
[<InlineData("hermes_unknown_issue6_control", 0)>]
[<InlineData("hermes_unknown_issue6_control", 1)>]
[<InlineData("hermes_unknown_issue6_control", 2)>]
[<InlineData("hermes_unknown_issue6_control", 3)>]
[<Trait("Category", "Integration")>]
let ``Unknown and removed tools stay unknown under readonly anonymous invalid and authenticated policies`` name setting =
    let policy = if setting = 0 then McpAccess.readOnly else McpTestPolicy.policy
    withConfiguredServer None policy [] None (session setting name)
