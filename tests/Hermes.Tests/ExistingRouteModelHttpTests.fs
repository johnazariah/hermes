module Hermes.Tests.ExistingRouteModelHttpTests

open System
open System.Collections.Concurrent
open System.Net.Http
open System.Net.Http.Json
open System.Text
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Core.LocalAccess
open Hermes.Tests.HttpRegressionFixture
open Hermes.Tests.RetrievalHttpFixtures

type private ChatReply =
    { Status: int
      Body: string
      MediaType: string
      Cache: string }

let private chat (client: HttpClient) (body: string) : Task<ChatReply> =
    task {
        use content = new StringContent(body, Encoding.UTF8, "application/json")
        use! response = client.PostAsync("/api/chat", content)
        let! text = response.Content.ReadAsStringAsync()
        return
            { Status = int response.StatusCode; Body = text
              MediaType =
                response.Content.Headers.ContentType |> Option.ofObj
                |> Option.bind (fun header -> header.MediaType |> Option.ofObj)
                |> Option.defaultValue ""
              Cache = response.Headers.GetValues("Cache-Control") |> String.concat ", " }
    }

let private seedChat (server: Server) : Task<int64> =
    task {
        let! id = seedSource server "/archive/http-chat/source.png" "immutable synthetic chat source"
        do!
            executeSql server "UPDATE documents SET subject='quartz' WHERE id=@id"
                [ "@id", Database.boxVal id ]
        return id
    }

let private provider (calls: ConcurrentQueue<unit>) (answer: Result<string, string>)
    : Algebra.ChatProvider =
    { complete = fun _ _ -> calls.Enqueue(()); Task.FromResult(answer) }

let private assertEvents (reply: ChatReply) (answer: bool) : unit =
    Assert.Equal(200, reply.Status)
    Assert.Equal("text/event-stream", reply.MediaType)
    Assert.Contains("no-store", reply.Cache)
    let results = reply.Body.IndexOf("event: results\n", StringComparison.Ordinal)
    let doneEvent = reply.Body.IndexOf("event: done\n", StringComparison.Ordinal)
    Assert.True(results >= 0 && doneEvent > results)
    if answer then
        let answerEvent = reply.Body.IndexOf("event: answer\n", StringComparison.Ordinal)
        Assert.True(answerEvent > results && answerEvent < doneEvent)
    else Assert.DoesNotContain("event: answer\n", reply.Body)

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Explicit anonymous archive chat uses the parsed request and never calls configured models`` () =
    let calls = ConcurrentQueue<unit>()
    withChatServer (Some(provider calls (Ok "must not be used"))) None (fun server ->
        task {
            let! id = seedChat server
            use guest = newGuestClient server
            let! reply = chat guest """{"query":"quartz","aiEnabled":false}"""
            assertEvents reply false
            Assert.Contains(string id, reply.Body)
            Assert.Empty(calls)
        })

[<Theory>]
[<InlineData("""{"query":"quartz"}""")>]
[<InlineData("""{"query":"quartz","aiEnabled":null}""")>]
[<InlineData("""{"query":"quartz","aiEnabled":"false"}""")>]
[<InlineData("""{"query":"quartz","aiEnabled":false,"aiEnabled":true}""")>]
[<Trait("Category", "Integration")>]
let ``Chat requires one explicit Boolean before starting SSE`` body =
    withServer None (fun server ->
        task {
            use guest = newGuestClient server
            let! reply = chat guest body
            Assert.Equal(400, reply.Status)
            Assert.Equal("application/json", reply.MediaType)
            Assert.DoesNotContain("event:", reply.Body)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Anonymous AI chat cannot invoke a configured provider`` () =
    let calls = ConcurrentQueue<unit>()
    withChatServer (Some(provider calls (Ok "must not be used"))) None (fun server ->
        task {
            let! _ = seedChat server
            use guest = newGuestClient server
            let! reply = chat guest """{"query":"quartz","aiEnabled":true}"""
            Assert.Equal(401, reply.Status)
            Assert.Equal("application/json", reply.MediaType)
            Assert.DoesNotContain("event:", reply.Body)
            Assert.Empty(calls)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Authorized AI without a configured provider fails truthfully before SSE`` () =
    withServer None (fun server ->
        task {
            let! reply = chat server.Client """{"query":"quartz","aiEnabled":true}"""
            Assert.Equal(503, reply.Status)
            Assert.Equal("application/json", reply.MediaType)
            Assert.Equal("model_unavailable", (json reply.Body).GetProperty("error").GetString())
            Assert.DoesNotContain("event:", reply.Body)
        })

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("Category", "Integration")>]
let ``Authorized AI preserves results answer done and real provider failure feedback`` failing =
    let calls = ConcurrentQueue<unit>()
    let answer = if failing then Error "synthetic provider failure" else Ok "synthetic answer"
    withChatServer (Some(provider calls answer)) None (fun server ->
        task {
            let! _ = seedChat server
            let! reply = chat server.Client """{"query":"quartz","aiEnabled":true}"""
            assertEvents reply true
            Assert.Single(calls) |> ignore
            Assert.Contains(
                (if failing then "AI unavailable" else "synthetic answer"), reply.Body)
        })

[<Theory>]
[<InlineData("semantic", false)>]
[<InlineData("hybrid", false)>]
[<InlineData("semantic", true)>]
[<InlineData("hybrid", true)>]
[<Trait("Category", "Integration")>]
let ``Real model search requires model invoke before availability or embedding work`` mode scoped =
    let calls = ConcurrentQueue<unit>()
    let model: Algebra.EmbeddingClient =
        { dimensions = 2
          isAvailable = fun () -> calls.Enqueue(()); Task.FromResult true
          embed = fun _ -> calls.Enqueue(()); Task.FromResult(Ok [| 1.0f; 0.0f |]) }
    withConfiguredServer (Some model) McpAccess.readOnly [] None (fun server ->
        task {
            use! client =
                if scoped then newScopedClient (Set.singleton Capability.Mutate) server
                else newGuestClient server |> Task.FromResult
            use! response = client.GetAsync($"/api/search?q=quartz&mode={mode}")
            Assert.Equal((if scoped then 403 else 401), int response.StatusCode)
            Assert.Empty(calls)
        })
