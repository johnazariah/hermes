module Hermes.Tests.LocalAccessTerminalTests

open System.Net.Http
open System.Threading.Tasks
open Xunit
open Hermes.Core.LocalAccess
open Hermes.Tests.LocalAccessHttpFixture

let private secured (reply: Reply) : unit =
    Assert.Equal("no-store", responseHeader "Cache-Control" reply)
    Assert.Equal("nosniff", responseHeader "X-Content-Type-Options" reply)
    let exposed = responseHeader "Access-Control-Expose-Headers" reply
    Assert.Contains("Retry-After", exposed)
    Assert.Contains("WWW-Authenticate", exposed)

let private terminal (status: int) (reply: Reply) : unit =
    Assert.Equal(status, reply.Status)
    secured reply
    Assert.Equal("", reply.Raw)
    Assert.DoesNotContain("text/html", responseHeader "Content-Type" reply)

[<Theory>]
[<InlineData("POST", "/api/access", "GET")>]
[<InlineData("GET", "/api/access/pairings", "POST")>]
[<InlineData("GET", "/api/access/pairings/AAAAAAAAAAAAAAAAAAAAAA/exchange", "POST")>]
[<InlineData("POST", "/api/access/session", "DELETE")>]
let ``known protocol wrong methods retain terminal 405 and Allow with secure headers``
    (methodName: string) (path: string) (allowed: string) : Task =
    withServer true true (fun server -> task {
        let value = request (HttpMethod(methodName)) path [ origin server.Origin ] (Some "{}")
        let! reply = send server value
        terminal 405 reply
        Assert.Contains(allowed, responseHeader "Allow" reply)
        Assert.Equal(server.Origin, responseHeader "Access-Control-Allow-Origin" reply)
        Assert.Empty(server.Effects.Reads)
        Assert.Empty(server.Effects.Work)
        Assert.Empty(server.Effects.Models)
    })

[<Theory>]
[<InlineData("/api/access/approve")>]
[<InlineData("/api/access/not-a-protocol-operation")>]
[<InlineData("/api/not-an-application-endpoint")>]
[<InlineData("/does-not-exist")>]
let ``unknown protocol API and ordinary paths remain secured terminal 404`` (path: string) : Task =
    withServer true true (fun server -> task {
        let! reply = get server path [ origin server.Origin ]
        terminal 404 reply
        Assert.Equal("", responseHeader "Allow" reply)
        Assert.Empty(server.Effects.Reads)
        Assert.Empty(server.Effects.Work)
        Assert.Empty(server.Effects.Models)
    })

[<Fact>]
let ``status and protected GET denials retain no store and security headers`` () : Task =
    withServer true true (fun server -> task {
        let! status = get server "/api/access" []
        Assert.Equal(200, status.Status)
        secured status
        let! anonymous = get server "/config" []
        assertError 401 "authentication_required" anonymous
        secured anonymous
        let! credential = issue server (Set.singleton Capability.Mutate)
        let! denied = get server "/config" [ bearer credential ]
        assertError 403 "insufficient_capability" denied
        secured denied
        Assert.Empty(server.Effects.Work)
    })
