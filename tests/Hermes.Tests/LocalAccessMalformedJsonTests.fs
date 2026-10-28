module Hermes.Tests.LocalAccessMalformedJsonTests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Xunit
open Hermes.Core
open Hermes.Service
open Hermes.Tests.LocalAccessDomainTests
open Hermes.Tests.LocalAccessHttpFixture

type private Input =
    { Path: string
      Field: string
      Body: string }

let private exchangeInput () : Input =
    { Path = "/api/access/pairings/" + String.replicate 22 "A" + "/exchange"
      Field = "verifier"; Body = exchangeBody (String.replicate 43 "A") }

let private inputs () : Input list =
    [ { Path = "/api/access/pairings"; Field = "challenge"; Body = creation Fixture.all }
      exchangeInput ()
      { Path = "/interactive/chat"; Field = "query"
        Body = """{"query":"synthetic","aiEnabled":false}""" } ]

let private replaceValue (literal: string) (input: Input) : string =
    let key = "\"" + input.Field + "\""
    let original = (json input.Body).GetProperty(input.Field).GetRawText()
    input.Body.Replace(key + ":" + original, key + ":" + literal)

let private damage (propertyName: bool) (escape: string) (input: Input) : string =
    if propertyName then
        input.Body.Replace("\"" + input.Field + "\":", "\"" + input.Field + escape + "\":")
    else replaceValue ("\"" + escape + "\"") input

let private noEffects (server: Server) : unit =
    Assert.Empty(server.Effects.Work)
    Assert.Empty(server.Effects.Models)
    Assert.False(File.Exists(Path.Combine(server.Directory, "accepted.txt")))

let private invalid (server: Server) (input: Input) (body: string) : Task<unit> =
    task {
        let! reply = post server input.Path [ origin server.Origin ] body
        assertError 400 "invalid_request" reply
        Assert.Equal("Local access request failed.", text "message" reply)
    }

[<Theory>]
[<InlineData("\\uD800", false)>]
[<InlineData("\\uDC00", false)>]
[<InlineData("\\uD800", true)>]
[<InlineData("\\uDC00", true)>]
let ``escaped lone surrogates return fixed errors through mapped protocol and interactive parsers``
    (escape: string) (propertyName: bool) : Task =
    withServer true true (fun server -> task {
        do!
            inputs ()
            |> Prelude.foldTask (fun () input ->
                invalid server input (damage propertyName escape input)) ()
        noEffects server
    })

[<Fact>]
let ``wrong typed string fields remain typed invalid request errors`` () : Task =
    withServer true true (fun server -> task {
        do!
            inputs ()
            |> Prelude.foldTask (fun () input -> invalid server input (replaceValue "17" input)) ()
        noEffects server
    })

[<Fact>]
let ``escaped surrogate exchange failures commit the invalid proof rate window`` () : Task =
    withServer true true (fun server -> task {
        let input = exchangeInput ()
        let bodies =
            [| damage false "\\uD800" input; damage false "\\uDC00" input
               damage true "\\uD800" input; damage true "\\uDC00" input |]
        do!
            [ 0 .. 119 ]
            |> Prelude.foldTask (fun () index -> invalid server input bodies[index % bodies.Length]) ()
        let! limited = post server input.Path [ origin server.Origin ] bodies[0]
        assertError 429 "access_rate_limited" limited
        at 60 server
        do! invalid server input bodies[1]
        noEffects server
    })

[<Theory>]
[<InlineData("\\uD83D\\uDE80", "🚀")>]
[<InlineData("café 日本語", "café 日本語")>]
let ``valid surrogate pairs and ordinary Unicode remain accepted queries``
    (jsonQuery: string) (query: string) : Task =
    withServer true true (fun server -> task {
        let body = "{\"query\":\"" + jsonQuery + "\",\"aiEnabled\":false}"
        let! chat = post server "/interactive/chat" [] body
        Assert.Equal(200, chat.Status)
        Assert.Equal("no-store", responseHeader "Cache-Control" chat)
        let path = "/interactive/search?q=" + Uri.EscapeDataString(query) + "&mode=keyword"
        let! search = get server path []
        Assert.Equal(200, search.Status)
        Assert.Equal("no-store", responseHeader "Cache-Control" search)
        Assert.Equal(2, server.Effects.Work.Count)
        Assert.Empty(server.Effects.Models)
    })

[<Theory>]
[<InlineData("\\uD800")>]
[<InlineData("\\uDC00")>]
let ``GET search treats backslash escapes as query text rather than JSON`` (escape: string) : Task =
    withServer true true (fun server -> task {
        let! accepted =
            get server ("/interactive/search?q=" + Uri.EscapeDataString(escape) + "&mode=keyword") []
        Assert.Equal(200, accepted.Status)
        let unknownName = Uri.EscapeDataString("q" + escape)
        let! rejected = get server ("/interactive/search?" + unknownName + "=synthetic&mode=keyword") []
        assertError 400 "invalid_request" rejected
        Assert.Equal(1, server.Effects.Work.Count)
        Assert.Empty(server.Effects.Models)
    })

[<Fact>]
let ``materialisation handling does not swallow supplied parser programming failures`` () : Task =
    task {
        let payload = Encoding.UTF8.GetBytes("{}")
        use stream = new MemoryStream(payload, false)
        let context = DefaultHttpContext()
        context.Request.ContentType <- "application/json"
        context.Request.ContentLength <- Nullable(int64 payload.Length)
        context.Request.Body <- stream
        let parser (_: JsonElement) : Result<unit, HttpAccessFailure> =
            invalidOp "Synthetic parser bug."
        let! error = Assert.ThrowsAsync<InvalidOperationException>(Func<Task>(fun () ->
            HttpAccessJson.read parser context :> Task))
        Assert.Equal("Synthetic parser bug.", error.Message)
    }
