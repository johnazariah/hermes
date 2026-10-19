module Hermes.Tests.CanonicalUiModuleTests

open Xunit
open Hermes.Service
open Hermes.Tests.CanonicalUiTestSupport

[<Theory>]
[<InlineData("""<script type='module' src='/src/main.tsx'></script>""")>]
[<InlineData("""<script type='module' src='assets/missing.js'></script>""")>]
[<InlineData("""<script type='module' src='/assets/missing.js'></script>""")>]
[<InlineData("""<script type='module' src='/assets/undeclared.js'></script>""")>]
[<InlineData("""<script type='module' src='https://example.test/entry.js'></script>""")>]
[<InlineData("""<script type='module' src='//example.test/entry.js'></script>""")>]
[<InlineData("""<script type='module' src='/assets/main-synthetic.css'></script>""")>]
[<InlineData("""<script type='module' src='/assets/main-synthetic.js?query=1'></script>""")>]
[<InlineData("""<script type='module' src=''></script>""")>]
[<InlineData("""<script type='module'>export const inlineEntry = true;</script>""")>]
[<InlineData("""<script type='module' data-src='/assets/main-synthetic.js'></script>""")>]
[<InlineData("""<script src='/src/main.tsx' type='module'></script>""")>]
[<InlineData("""<script type=module src=/src/main.tsx></script>""")>]
[<InlineData("""<script type='mod&#117;le' src='/src/main.tsx'></script>""")>]
[<InlineData("""<script type='module' src='/assets/main-synthetic.js' src='/src/main.tsx'></script>""")>]
[<InlineData("""<script type='module' type='text/javascript' src='/assets/main-synthetic.js'></script>""")>]
let ``A valid entry cannot hide another invalid module`` extra =
    withFixture (fun fixture ->
        task {
            write fixture indexName (index.Replace("</body>", extra + "</body>"))
            CanonicalUi.preflight fixture.BaseDirectory |> assertError "Invalid index.html"
        })

[<Theory>]
[<InlineData("""<script src='/assets/main-synthetic.js' crossorigin type='module'></script>""")>]
[<InlineData("""<script type="module" crossorigin src="/assets/main-synthetic.js"></script>""")>]
[<InlineData("""<SCRIPT data-note="quoted > marker" SRC='/assets/main-synthetic.js' TYPE='module'></SCRIPT>""")>]
[<InlineData("""<script data-type='not-module' type='module' src='/assets/main-synthetic.js'></script>""")>]
[<InlineData("""<script src='/assets/main&#45;synthetic.js' type='module'></script>""")>]
let ``Built module attributes retain ordering quoting and HTML value semantics`` entry =
    withFixture (fun fixture ->
        task {
            let html =
                "<!doctype html><html><body><div id='root'></div>"
                + entry + "</body></html>"
            write fixture indexName html
            Assert.True(CanonicalUi.preflight fixture.BaseDirectory |> Result.isOk)
        })

[<Fact>]
let ``Every module must be declared even when both script files exist`` () =
    withFixture (fun fixture ->
        task {
            let extra = """<script src="/assets/second.js" type="module"></script>"""
            write fixture "assets/second.js" "export const second = true;"
            write fixture indexName (index.Replace("</body>", extra + "</body>"))
            CanonicalUi.preflight fixture.BaseDirectory |> assertError "Invalid index.html"
            write fixture manifestName
                (manifest [| scriptName; styleName; "favicon.svg"; "icons.svg"; "assets/second.js" |])
            Assert.True(CanonicalUi.preflight fixture.BaseDirectory |> Result.isOk)
        })
