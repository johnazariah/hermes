module Hermes.Tests.CanonicalUiHttpTests

open System.IO
open System.Net.Http
open Xunit
open Hermes.Tests.CanonicalUiTestSupport

[<Theory>]
[<InlineData("/")>]
[<InlineData("/index.html")>]
[<InlineData("/documents")>]
[<InlineData("/search")>]
[<InlineData("/settings")>]
[<InlineData("/onboarding")>]
[<InlineData("/pipeline")>]
[<InlineData("/chat")>]
[<InlineData("/documents?category=synthetic")>]
[<Trait("Category", "Integration")>]
let ``Canonical browser routes serve the identical physical bundle`` path =
    withServer (fun _ server ->
        task {
            let! response = get server path
            Assert.Equal(200, response.StatusCode)
            Assert.Equal("text/html", response.MediaType)
            Assert.Equal(index, response.Body)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Declared JavaScript is delivered as JavaScript rather than HTML`` () =
    withServer (fun _ server ->
        task {
            let! response = get server ("/" + scriptName)
            Assert.Equal(200, response.StatusCode)
            Assert.Contains(response.MediaType, [ "text/javascript"; "application/javascript" ])
            Assert.Equal(script, response.Body)
            assertNotHtml response
        })

[<Theory>]
[<InlineData("/assets/main-synthetic.css", "text/css")>]
[<InlineData("/favicon.svg", "image/svg+xml")>]
[<InlineData("/icons.svg", "image/svg+xml")>]
[<Trait("Category", "Integration")>]
let ``Other declared assets retain their content types`` path contentType =
    withServer (fun _ server ->
        task {
            let! response = get server path
            Assert.Equal(200, response.StatusCode)
            Assert.Equal(contentType, response.MediaType)
            Assert.NotEmpty(response.Body)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``The delivered manifest is the preflighted artifact manifest`` () =
    withServer (fun fixture server ->
        task {
            let! response = get server ("/" + manifestName)
            Assert.Equal(200, response.StatusCode)
            Assert.Equal("application/json", response.MediaType)
            Assert.Equal(File.ReadAllText(Path.Combine(fixture.WebRoot, manifestName)), response.Body)
            let json = HttpRegressionFixture.json response.Body
            Assert.Equal(1, json.GetProperty("schemaVersion").GetInt32())
        })

[<Theory>]
[<InlineData("/")>]
[<InlineData("/documents")>]
[<InlineData("/assets/main-synthetic.js")>]
[<InlineData("/asset-manifest.json")>]
[<Trait("Category", "Integration")>]
let ``HEAD serves file metadata without an HTML or asset body`` path =
    withServer (fun _ server ->
        task {
            let! response = request server HttpMethod.Head path
            Assert.Equal(200, response.StatusCode)
            Assert.NotEmpty(response.MediaType)
            Assert.Equal("", response.Body)
        })

[<Theory>]
[<InlineData("/unknown-browser-route")>]
[<InlineData("/documents/42")>]
[<InlineData("/documents/")>]
[<InlineData("/api")>]
[<InlineData("/api/missing-route")>]
[<InlineData("/api/documents/999999")>]
[<InlineData("/api/unlisted.json")>]
[<InlineData("/mcp/missing-route")>]
[<InlineData("/health/missing-route")>]
[<InlineData("/assets/missing.js")>]
[<InlineData("/assets/undeclared.js")>]
[<InlineData("/_framework/blazor.web.js")>]
[<InlineData("/_content/Hermes.UI/index.html")>]
[<Trait("Category", "Integration")>]
let ``Unknown reserved routes and absent or undeclared assets remain nonHTML 404`` path =
    withServer (fun _ server ->
        task {
            let! response = get server path
            Assert.Equal(404, response.StatusCode)
            assertNotHtml response
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Removing a declared asset after startup still yields nonHTML 404`` () =
    withServer (fun fixture server ->
        task {
            File.Delete(Path.Combine(fixture.WebRoot, scriptName))
            let! response = get server ("/" + scriptName)
            Assert.Equal(404, response.StatusCode)
            assertNotHtml response
        })

[<Theory>]
[<InlineData("POST")>]
[<InlineData("PUT")>]
[<InlineData("DELETE")>]
[<Trait("Category", "Integration")>]
let ``Nonread methods do not receive the SPA shell`` method =
    withServer (fun _ server ->
        task {
            let! response = request server (HttpMethod(method)) "/documents"
            Assert.Equal(404, response.StatusCode)
            assertNotHtml response
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Existing API and MCP method failures are not replaced by HTML`` () =
    withServer (fun _ server ->
        task {
            let! api = get server "/api/stats"
            Assert.Equal(200, api.StatusCode)
            Assert.Equal("application/json", api.MediaType)
            let! mcp = get server "/mcp"
            Assert.Equal(405, mcp.StatusCode)
            assertNotHtml mcp
        })
