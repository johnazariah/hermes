module Hermes.Tests.RetrievalPolicyTests

open System
open Xunit
open Hermes.Core
open Hermes.Service

let private settings values key : string | null =
    values |> Map.tryFind key |> Option.toObj

[<Fact>]
let ``Production settings default to disabled MCP mutations`` () =
    let policy = ServicePolicy.load (settings Map.empty) |> Result.defaultWith failwith
    let session = McpAccess.authenticate policy.Mcp (Some("Bearer " + McpTestPolicy.token))
    Assert.Equal(21741, policy.Port)
    Assert.True(McpAccess.permits session McpAccess.Read)
    Assert.False(McpAccess.permits session McpAccess.Mutate)

[<Theory>]
[<InlineData("true", "")>]
[<InlineData("true", "short")>]
[<InlineData("maybe", "")>]
let ``Unsafe mutation settings fail closed`` enabled token =
    let values = Map.ofList [ "HERMES_MCP_MUTATIONS", enabled; "HERMES_MCP_TOKEN", token ]
    Assert.True(ServicePolicy.load (settings values) |> Result.isError)

[<Theory>]
[<InlineData("https://foreign.example")>]
[<InlineData("http://localhost:5173/path")>]
[<InlineData("http://user@localhost:5173")>]
[<InlineData("null")>]
let ``Configured origins must be canonical loopback origins`` origin =
    Assert.True(LocalBoundary.configuredOrigins origin |> Result.isError)

[<Fact>]
let ``Configured port and preview origins are explicit and validated`` () =
    let values =
        Map.ofList [ "HERMES_PORT", "21742"
                     "HERMES_ALLOWED_ORIGINS", "http://localhost:5173;http://127.0.0.1:4173" ]
    let policy = ServicePolicy.load (settings values) |> Result.defaultWith failwith
    Assert.Equal(21742, policy.Port)
    Assert.Equal(2, policy.ExtraOrigins.Length)

[<Theory>]
[<InlineData("0")>]
[<InlineData("65536")>]
[<InlineData("not-a-port")>]
let ``Invalid listener ports fail before service startup`` port =
    Assert.True(ServicePolicy.load (settings (Map.ofList [ "HERMES_PORT", port ])) |> Result.isError)

[<Fact>]
let ``Nonfinite zero and dimension-mismatched vectors are invalid`` () =
    Assert.False(SearchContract.validVector 2 [| Single.NaN; 1.0f |])
    Assert.False(SearchContract.validVector 2 [| Single.PositiveInfinity; 1.0f |])
    Assert.False(SearchContract.validVector 2 [| 0.0f; 0.0f |])
    Assert.False(SearchContract.validVector 2 [| 1.0f |])
    Assert.True(SearchContract.validVector 2 [| 1.0f; 0.0f |])

[<Theory>]
[<InlineData(200)>]
[<InlineData(Int32.MaxValue)>]
let ``Shared validation preserves every positive keyword Int32 limit`` count =
    let request: SearchContract.Request =
        { Mode = SearchContract.Keyword
          Filter = { Search.defaultFilter "quartz" with Limit = count } }
    Assert.True(SearchContract.validate request |> Result.isOk)

[<Theory>]
[<InlineData("keyword", 0)>]
[<InlineData("keyword", -1)>]
[<InlineData("semantic", 0)>]
[<InlineData("hybrid", -1)>]
[<InlineData("semantic", 101)>]
[<InlineData("hybrid", 101)>]
let ``Shared validation rejects nonpositive limits and bounds only vector modes`` mode count =
    let selected = SearchContract.parseMode (Some mode) |> Result.defaultWith failwith
    let request: SearchContract.Request =
        { Mode = selected
          Filter = { Search.defaultFilter "quartz" with Limit = count } }
    Assert.True(SearchContract.validate request |> Result.isError)
