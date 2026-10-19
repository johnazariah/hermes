module Hermes.Tests.CanonicalUiTestSupport

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Hermes.Service
open Hermes.Tests.HttpRegressionFixture

type Fixture =
    { BaseDirectory: string
      WebRoot: string }

type Response =
    { StatusCode: int
      MediaType: string
      Body: string }

let indexName = "index.html"
let manifestName = "asset-manifest.json"
let scriptName = "assets/main-synthetic.js"
let styleName = "assets/main-synthetic.css"
let script = "export const canonicalBundle = 'synthetic-only';"
let index =
    """<!doctype html><html><head><link rel="stylesheet" href="/assets/main-synthetic.css"></head><body><div id="root"></div><script type="module" src="/assets/main-synthetic.js"></script></body></html>"""

let manifest names =
    JsonSerializer.Serialize({| schemaVersion = 1; assets = names |})

let write fixture relative (text: string) =
    let path = Path.Combine(fixture.WebRoot, relative)
    let parent =
        Path.GetDirectoryName(path)
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Synthetic asset requires a parent directory")
    Directory.CreateDirectory(parent) |> ignore
    File.WriteAllText(path, text, UTF8Encoding(false))

let create baseDirectory =
    let fixture = { BaseDirectory = baseDirectory; WebRoot = Path.Combine(baseDirectory, "wwwroot") }
    [ indexName, index
      manifestName, manifest [| scriptName; styleName; "favicon.svg"; "icons.svg" |]
      scriptName, script
      styleName, "body { color: black; }"
      "favicon.svg", """<svg xmlns="http://www.w3.org/2000/svg"></svg>"""
      "icons.svg", """<svg xmlns="http://www.w3.org/2000/svg"><symbol id="synthetic-icon"></symbol></svg>"""
      "assets/undeclared.js", "throw new Error('must not be served');"
      "_framework/blazor.web.js", "throw new Error('legacy framework must not be served');"
      "api/unlisted.json", """{"mustNotBeServed":true}""" ]
    |> List.iter (fun (name, text) -> write fixture name text)
    fixture

let withDirectory (verify: string -> Task<unit>) =
    task {
        let root = Path.Combine(Path.GetTempPath(), "hermes-canonical-ui-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try do! verify root
        finally Directory.Delete(root, true)
    }

let withFixture verify =
    withDirectory (fun root -> verify (create root))

let withOrigins origins verify =
    withFixture (fun fixture ->
        let bundle = CanonicalUi.preflight fixture.BaseDirectory |> Result.defaultWith invalidOp
        withUiServer bundle origins (verify fixture))

let withServer verify = withOrigins [] verify

let assertError (fragment: string) result =
    let message: string =
        result
        |> Result.map (fun _ -> invalidOp "Expected canonical UI preflight to fail")
        |> Result.defaultWith id
    Assert.Contains(fragment, message)
    Assert.Contains("npm run build --prefix src/Hermes.Web", message)

let request (server: Server) (method: HttpMethod) (path: string) =
    task {
        use message = new HttpRequestMessage(method, path)
        use! response = server.Client.SendAsync(message)
        let! body = response.Content.ReadAsStringAsync()
        let mediaType =
            response.Content.Headers.ContentType
            |> Option.ofObj
            |> Option.bind (fun value -> Option.ofObj value.MediaType)
            |> Option.defaultValue ""
        return { StatusCode = int response.StatusCode; MediaType = mediaType; Body = body }
    }

let get server path = request server HttpMethod.Get path

let assertNotHtml response =
    Assert.NotEqual<string>("text/html", response.MediaType)
    Assert.DoesNotContain("<!doctype", response.Body, StringComparison.OrdinalIgnoreCase)
