module Hermes.Tests.CanonicalUiPreflightTests

open System.IO
open Xunit
open Hermes.Service
open Hermes.Tests.CanonicalUiTestSupport

[<Fact>]
let ``A complete deployed bundle passes preflight`` () =
    withFixture (fun fixture ->
        task { Assert.True(CanonicalUi.preflight fixture.BaseDirectory |> Result.isOk) })

[<Theory>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData(" ")>]
[<InlineData("relative-directory")>]
let ``Preflight rejects missing or relative application bases`` (directory: string | null) =
    CanonicalUi.preflight directory |> assertError "absolute application base"

[<Fact>]
let ``Preflight does not search another base for a bundle`` () =
    withFixture (fun fixture ->
        task {
            CanonicalUi.preflight (Path.Combine(fixture.BaseDirectory, "elsewhere"))
            |> assertError "wwwroot"
        })

[<Fact>]
let ``Missing deployed root fails with an actionable diagnostic`` () =
    withFixture (fun fixture ->
        task {
            Directory.Delete(fixture.WebRoot, true)
            CanonicalUi.preflight fixture.BaseDirectory |> assertError "wwwroot"
        })

[<Theory>]
[<InlineData("index.html")>]
[<InlineData("asset-manifest.json")>]
[<InlineData("assets/main-synthetic.js")>]
[<InlineData("assets/main-synthetic.css")>]
[<InlineData("favicon.svg")>]
[<InlineData("icons.svg")>]
let ``Every declared file must exist`` name =
    withFixture (fun fixture ->
        task {
            File.Delete(Path.Combine(fixture.WebRoot, name))
            CanonicalUi.preflight fixture.BaseDirectory |> assertError name
        })

[<Theory>]
[<InlineData("index.html")>]
[<InlineData("asset-manifest.json")>]
[<InlineData("assets/main-synthetic.js")>]
let ``Required files cannot be empty`` name =
    withFixture (fun fixture ->
        task {
            write fixture name ""
            CanonicalUi.preflight fixture.BaseDirectory |> assertError "empty"
        })

[<Theory>]
[<InlineData("{")>]
[<InlineData("null")>]
[<InlineData("[]")>]
[<InlineData("{}")>]
[<InlineData("""{"schemaVersion":2,"assets":[]}""")>]
[<InlineData("""{"schemaVersion":"1","assets":[]}""")>]
[<InlineData("""{"schemaVersion":1.0,"assets":[]}""")>]
[<InlineData("""{"schemaVersion":1,"schemaVersion":1,"assets":[]}""")>]
[<InlineData("""{"schemaVersion":1,"assets":[],"assets":[]}""")>]
[<InlineData("""{"schemaVersion":1,"assets":null}""")>]
[<InlineData("""{"schemaVersion":1,"assets":[null]}""")>]
[<InlineData("""{"schemaVersion":1,"assets":[42]}""")>]
let ``Malformed manifest structure is rejected`` text =
    withFixture (fun fixture ->
        task {
            write fixture manifestName text
            CanonicalUi.preflight fixture.BaseDirectory |> assertError "Manifest"
        })

[<Theory>]
[<InlineData("../outside.js")>]
[<InlineData("/assets/main-synthetic.js")>]
[<InlineData("assets/../outside.js")>]
[<InlineData("assets/a\\outside.js")>]
[<InlineData("assets/%2e%2e/outside.js")>]
[<InlineData("assets/route{value}.js")>]
[<InlineData("assets/file.js?query=1")>]
[<InlineData("api/unlisted.json")>]
[<InlineData("_framework/blazor.web.js")>]
[<InlineData("index.html")>]
[<InlineData("")>]
[<InlineData("other.svg")>]
[<InlineData("icons.svg.bak")>]
let ``Manifest paths cannot escape the public asset namespace`` name =
    withFixture (fun fixture ->
        task {
            write fixture manifestName (manifest [| scriptName; name |])
            CanonicalUi.preflight fixture.BaseDirectory |> assertError "Invalid asset path"
        })

[<Theory>]
[<InlineData("assets/main-synthetic.js")>]
[<InlineData("assets/MAIN-synthetic.js")>]
let ``Manifest duplicates are rejected consistently across platforms`` duplicate =
    withFixture (fun fixture ->
        task {
            write fixture manifestName (manifest [| scriptName; duplicate |])
            CanonicalUi.preflight fixture.BaseDirectory |> assertError "duplicate"
        })

[<Fact>]
let ``Unknown content types cannot become public assets`` () =
    withFixture (fun fixture ->
        task {
            let unknown = "assets/unrecognized.hermesunknown"
            write fixture unknown "synthetic"
            write fixture manifestName (manifest [| scriptName; unknown |])
            CanonicalUi.preflight fixture.BaseDirectory |> assertError "Unsupported asset content type"
        })

[<Theory>]
[<InlineData("not HTML")>]
[<InlineData("""<!doctype html><html><div id="root"></div></html>""")>]
[<InlineData("""<!doctype html><html><script type="module" src="/assets/main-synthetic.js"></script></html>""")>]
[<InlineData("""<!doctype html><html><div id="root"></div><script type="module" src="/src/main.tsx"></script></html>""")>]
[<InlineData("""<!doctype html><html><div id="root"></div><script type="module" src="/assets/not-declared.js"></script></html>""")>]
let ``Index must be a built React shell with a declared module entry`` html =
    withFixture (fun fixture ->
        task {
            write fixture indexName html
            CanonicalUi.preflight fixture.BaseDirectory |> assertError "Invalid index.html"
        })

[<Fact>]
let ``An empty asset list cannot validate the index entry`` () =
    withFixture (fun fixture ->
        task {
            write fixture manifestName (manifest [||])
            CanonicalUi.preflight fixture.BaseDirectory |> assertError "Invalid index.html"
        })
