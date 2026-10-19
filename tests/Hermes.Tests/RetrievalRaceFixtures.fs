module Hermes.Tests.RetrievalRaceFixtures

open System
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture
open Hermes.Tests.RetrievalHttpFixtures

type Transport = Http | Mcp

type Reply =
    { StatusCode: int
      Body: JsonElement
      ToolFailed: bool }

type Gate =
    { Armed: TaskCompletionSource<unit>
      Entered: TaskCompletionSource<unit>
      Resume: TaskCompletionSource<unit> }

type Checkpoint =
    { SqlHash: string
      FileHashes: Map<string, string> }

type Interleaving<'state> =
    { Reply: Reply
      State: 'state }

let private signal () =
    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

let createGate () : Gate =
    { Armed = signal (); Entered = signal (); Resume = signal () }

let arm (gate: Gate) =
    gate.Armed.TrySetResult(()) |> ignore

let hold (gate: Gate) =
    task {
        if gate.Armed.Task.IsCompleted && gate.Entered.TrySetResult(()) then
            do! gate.Resume.Task.WaitAsync(TimeSpan.FromSeconds 30.0)
    }

let transport useMcp =
    if useMcp then Mcp else Http

let text (name: string) (value: JsonElement) =
    value.GetProperty(name).GetString()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failwith $"Missing string property: {name}")

let private http (server: Server) (path: string) =
    task {
        use! response = server.Client.GetAsync(path)
        let! content = response.Content.ReadAsStringAsync()
        let reply: Reply =
            { StatusCode = int response.StatusCode; Body = json content; ToolFailed = false }
        return reply
    }

let private mcp (pending: Task<JsonElement>) =
    task {
        let! envelope = pending
        Assert.False(envelope.TryGetProperty("error") |> fst)
        let result = envelope.GetProperty("result")
        let failed =
            match result.TryGetProperty("isError") with
            | true, value -> value.GetBoolean()
            | false, _ -> false
        let reply: Reply =
            { StatusCode = 200; Body = payload envelope; ToolFailed = failed }
        return reply
    }

let search selected mode server =
    let modeName = SearchContract.modeName mode
    match selected with
    | Http -> http server $"/api/search?q=quartz&mode={modeName}&limit=100"
    | Mcp ->
        mcpCall server "hermes_search"
            (box {| query = "quartz"; mode = modeName; limit = 100 |})
        |> mcp

let comprehension selected server documentId =
    match selected with
    | Http -> http server $"/api/documents/{documentId}/comprehension"
    | Mcp ->
        mcpCall server "hermes_get_comprehension" (box {| document_id = documentId |})
        |> mcp

let searchResults selected (reply: Reply) =
    match selected with
    | Http -> reply.Body
    | Mcp -> reply.Body.GetProperty("results")

let assertReply selected httpStatus toolFailed (reply: Reply) =
    Assert.Equal((if selected = Http then httpStatus else 200), reply.StatusCode)
    Assert.Equal(selected = Mcp && toolFailed, reply.ToolFailed)

let checkpoint server =
    task {
        let! sql = databaseHash server
        let! files = FileKeywordFixtures.fileHashes server
        let value: Checkpoint = { SqlHash = sql; FileHashes = files }
        return value
    }

let private captureTransition (gate: Gate) (transition: unit -> Task<'state>) =
    task {
        try
            do! gate.Entered.Task.WaitAsync(timeout)
            let! state = (transition ()).WaitAsync(timeout)
            return Ok state
        with error ->
            return Error error
    }

/// Release and observe the pending request even when a transition assertion
/// fails. Neither failed assertions nor deadlines leave a paused request behind.
let interleave (gate: Gate) (pending: Task<Reply>) (transition: unit -> Task<'state>) =
    task {
        let! captured = captureTransition gate transition
        gate.Resume.TrySetResult(()) |> ignore
        let! reply = pending.WaitAsync(timeout)
        return captured
               |> Result.map (fun state ->
                   let completed: Interleaving<'state> = { Reply = reply; State = state }
                   completed)
               |> Result.defaultWith raise
    }
