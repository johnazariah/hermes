module Hermes.Tests.DeepExtractionHttpTests

open System.Collections.Concurrent
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture

let private sourcePath = "/archive/deep/source.png"
let private sidecarPath = "/archive/deep/thread.comprehension.json"
let private originalSidecar = """{"document_type":"payslip","summary":"original current comprehension"}"""
let private providerResponse = """{"gross_pay":9999}"""

type private Pause =
    { Entered: TaskCompletionSource<unit>
      Release: TaskCompletionSource<unit> }

let private pause () =
    { Entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
      Release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously) }

let private gatedProvider barrier : Algebra.ChatProvider =
    { complete = fun _ _ ->
        task {
            barrier.Entered.TrySetResult() |> ignore
            do! barrier.Release.Task
            return Ok providerResponse
        } }

let private dependencies provider : McpTools.DeepExtractionDeps =
    let prompt: PromptLoader.ParsedPrompt =
        { System = "Extract payslip fields as JSON."
          UserTemplate = "{{document_text}}\n{{context}}" }
    { Chat = provider; Registry = Map.ofList [ "payslip", prompt ]
      Provider = "fake-http-regression"; Model = "fake-deep-extractor" }

let private seedCurrent server =
    task {
        let! documentId = seedSource server sourcePath "immutable payslip image"
        do! seedCompletions server documentId
        let! _ =
            server.Db.execNonQuery
                """INSERT INTO comprehension(document_id,document_type,category,confidence)
                   VALUES (@id,'payslip','payslips',0.95)"""
                [ "@id", Database.boxVal documentId ]
        server.Files.Put (sourcePath + ".extracted.md") "Current payslip extracted text"
        server.Files.Put sidecarPath originalSidecar
        return documentId
    }

let private snapshot (values: ConcurrentDictionary<string, 'value>) =
    values |> Seq.map (fun entry -> entry.Key, entry.Value) |> Map.ofSeq

let private assertToolError (response: JsonElement) =
    Assert.Equal("2.0", response.GetProperty("jsonrpc").GetString())
    Assert.Equal(1, response.GetProperty("id").GetInt32())
    Assert.False(response.TryGetProperty("error") |> fst)
    let result = response.GetProperty("result")
    Assert.True(result.GetProperty("isError").GetBoolean())
    Assert.Equal(1, result.GetProperty("content").GetArrayLength())
    Assert.Equal("text", result.GetProperty("content").[0].GetProperty("type").GetString())
    let error = (payload response).GetProperty("error").GetString()
    Assert.Contains("reflowed", error)
    Assert.Contains("retry", error)

let private rejectPausedCall server barrier operation documentId =
    task {
        let beforeFiles = snapshot server.Files.Files
        let beforeBytes = snapshot server.Files.Bytes
        let pending = mcpCall server "hermes_deep_extract" (box {| document_id = documentId; force = true |})
        try
            do! barrier.Entered.Task.WaitAsync(timeout)
            do! applyReflow server documentId operation
        finally
            barrier.Release.TrySetResult() |> ignore
        let! response = pending.WaitAsync(timeout)
        assertToolError response
        Assert.Equal<Map<string, string>>(beforeFiles, snapshot server.Files.Files)
        Assert.Equal<Map<string, byte array>>(beforeBytes, snapshot server.Files.Bytes)
    }

[<Theory>]
[<InlineData("recomprehend")>]
[<InlineData("reextract")>]
[<Trait("Category", "Integration")>]
let ``HTTP MCP marks superseded deep extraction as a tool error without changing bytes`` operation =
    let barrier = pause ()
    withServer (Some(dependencies (gatedProvider barrier))) (fun server ->
        task {
            let! documentId = seedCurrent server
            do! rejectPausedCall server barrier operation documentId
        })

let private assertSuccessfulDeepResult server documentId =
    task {
        let! response = mcpCall server "hermes_deep_extract" (box {| document_id = documentId; force = true |})
        let result = response.GetProperty("result")
        Assert.False(result.TryGetProperty("isError") |> fst)
        let content = payload response
        Assert.Equal("extracted", content.GetProperty("status").GetString())
        Assert.Equal(documentId, content.GetProperty("document_id").GetInt64())
        let deep = content.GetProperty("comprehension").GetProperty("deep_extraction")
        Assert.Equal(9999, deep.GetProperty("fields").GetProperty("gross_pay").GetInt32())
        let saved = server.Files.Get sidecarPath |> Option.defaultWith (fun () -> failwith "Missing published sidecar")
        Assert.Equal(9999, (json saved).GetProperty("deep_extraction").GetProperty("fields").GetProperty("gross_pay").GetInt32())
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``HTTP MCP configured fake deep provider retains the successful payload contract`` () =
    withServer (Some(dependencies (TestHelpers.fakeChatProvider providerResponse))) (fun server ->
        task {
            let! documentId = seedCurrent server
            do! assertSuccessfulDeepResult server documentId
        })
