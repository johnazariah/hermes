module Hermes.Tests.CanonicalUiBoundaryTests

open System
open System.Net.Http
open Xunit
open Hermes.Tests.HttpRegressionFixture
open Hermes.Tests.CanonicalUiTestSupport

let private reject (server: Server) (path: string) (host: string option) (origin: string option) =
    task {
        use request = new HttpRequestMessage(HttpMethod.Get, path)
        host |> Option.iter (fun value -> request.Headers.Host <- value)
        origin |> Option.iter (fun value -> request.Headers.Add("Origin", value))
        use! response = server.Client.SendAsync(request)
        Assert.Equal(403, int response.StatusCode)
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"))
        let! body = response.Content.ReadAsStringAsync()
        Assert.DoesNotContain("<!doctype", body, StringComparison.OrdinalIgnoreCase)
    }

[<Theory>]
[<InlineData("/")>]
[<InlineData("/documents")>]
[<InlineData("/index.html")>]
[<InlineData("/assets/main-synthetic.js")>]
[<InlineData("/asset-manifest.json")>]
[<InlineData("/_framework/blazor.web.js")>]
[<Trait("Category", "Integration")>]
let ``Static and browser routes cannot evade Host validation`` path =
    withServer (fun _ server -> reject server path (Some "foreign.example") None)

[<Theory>]
[<InlineData("/")>]
[<InlineData("/documents")>]
[<InlineData("/assets/main-synthetic.js")>]
[<InlineData("/asset-manifest.json")>]
[<Trait("Category", "Integration")>]
let ``Static and browser routes cannot evade Origin validation`` path =
    withServer (fun _ server -> reject server path None (Some "https://foreign.example"))

[<Fact>]
[<Trait("Category", "Integration")>]
let ``A loopback Host with the wrong port is still rejected`` () =
    withServer (fun _ server -> reject server "/" (Some "localhost:1") None)

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Opaque null origins cannot read the UI`` () =
    withServer (fun _ server -> reject server "/" None (Some "null"))

[<Fact>]
[<Trait("Category", "Integration")>]
let ``An explicitly allowed preview origin gets only its exact CORS origin`` () =
    let origin = "http://localhost:5173"
    withOrigins [ origin ] (fun _ server ->
        task {
            use request = new HttpRequestMessage(HttpMethod.Get, "/")
            request.Headers.Add("Origin", origin)
            use! response = server.Client.SendAsync(request)
            Assert.Equal(200, int response.StatusCode)
            Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin") |> Seq.exactlyOne)
            let! body = response.Content.ReadAsStringAsync()
            Assert.Equal(index, body)
        })

[<Theory>]
[<InlineData("/")>]
[<InlineData("/assets/main-synthetic.js")>]
[<Trait("Category", "Integration")>]
let ``Foreign preflights are rejected before UI delivery`` (path: string) =
    withServer (fun _ server ->
        task {
            use request = new HttpRequestMessage(HttpMethod.Options, path)
            request.Headers.Add("Origin", "https://foreign.example")
            request.Headers.Add("Access-Control-Request-Method", "GET")
            use! response = server.Client.SendAsync(request)
            Assert.Equal(403, int response.StatusCode)
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"))
        })
