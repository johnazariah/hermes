namespace Hermes.Tests

open System.Collections.Concurrent
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.AsyncCapabilityControl
open Hermes.Tests.McpAsyncConformanceFixture
open Hermes.Tests.McpAsyncConformanceControl

module McpAsyncConformanceReadMutationTests =
    let private readBoundary (fixture: Fixture) =
        task {
            let! before = snapshot fixture.Db fixture.Files
            let trace = ConcurrentQueue<string>()
            let gate = Gate("archive text read")
            let db = database trace Open fixture.Db
            let fs = files trace (Some gate) fixture
            let! reply = exercise [ gate ]
                            (fun () -> call McpServer.readOnlyCapabilities db fs None "hermes_read_file" """{"path":"async/source.txt"}""")
                            (resume fixture before trace [ "fs.exists"; "fs.read" ] gate)
            let expected = JsonSerializer.Serialize({| path = "async/source.txt"; content = sourceText; size = Encoding.UTF8.GetByteCount(sourceText) |})
            equalJson expected (payload false reply)
            Assert.Equal<string list>([ "fs.exists"; "fs.read"; "fs.size" ], List.ofSeq trace)
            let! after = snapshot fixture.Db fixture.Files
            Assert.Equal<Snapshot>(before, after)
        }

    [<Fact; Trait("Category", "Integration")>]
    let ``MCP default archive read awaits its actual file capability`` () = withFixture readBoundary

    let private denied (db: Algebra.Database) (fs: Algebra.FileSystem)
                       (trace: ConcurrentQueue<string>) (gate: Gate)
                       () (capabilities: McpServer.Capabilities) =
        task {
            let! reply = call capabilities db fs None "hermes_reclassify" """{"document_id":1,"new_category":"reviewed"}"""
            equalJson """{"jsonrpc":"2.0","id":1,"error":{"code":-32602,"message":"Tool is not available under this MCP capability policy"}}""" (json reply)
            Assert.Empty(trace)
            Assert.False(gate.WasEntered)
        }

    let private verifyTags (rows: Map<string, obj> list) =
        Assert.Equal(2, rows.Length)
        let user = Prelude.RowReader(rows[0])
        let generated = Prelude.RowReader(rows[1])
        Assert.Equal("keep-user", user.String "tag" "")
        Assert.Equal("user", user.String "source" "")
        Assert.Equal<string option>(None, user.OptString "created_by")
        Assert.Equal("reviewed", generated.String "tag" "")
        Assert.Equal("user", generated.String "source" "")
        Assert.Equal<string option>(Some "reclassification", generated.OptString "created_by")

    let private verifyReclassified (fixture: Fixture) before (reply: string) =
        task {
            equalJson ($"""{{"status":"reclassified","document_id":1,"previous_category":"records","category":"reviewed","new_category":"reviewed","changed":true,"saved_path":"async/source.txt","sha256":"{sha}"}}""") (payload false reply)
            let! categories = call McpServer.readOnlyCapabilities fixture.Db fixture.Files.Fs None "hermes_list_categories" "{}"
            equalJson """{"categories":[{"category":"reviewed","count":1}]}""" (payload false categories)
            let! document = call McpServer.readOnlyCapabilities fixture.Db fixture.Files.Fs None "hermes_get_document" """{"id":1}"""
            let metadata = payload false document
            Assert.Equal("verified synthetic", text "extractedVendor" metadata)
            Assert.Equal(sha, text "sha256" metadata)
            Assert.Equal("async/source.txt", text "savedPath" metadata)
            let! tags = fixture.Db.execReader "SELECT tag,source,created_by FROM tags ORDER BY tag" []
            verifyTags tags
            let! after = snapshot fixture.Db fixture.Files
            sameFiles before after
            sameRows before after (historyTables @ [ "stage_completions"; "extraction"; "triage"; "comprehension"; "embedding"; "document_generations" ])
        }

    let private mutationBoundary (fixture: Fixture) =
        task {
            let! before = snapshot fixture.Db fixture.Files
            let trace = ConcurrentQueue<string>()
            let gate = Gate("metadata UPDATE")
            let db = database trace (Write gate) fixture.Db
            let fs = files trace None fixture
            let wrong: McpServer.Capabilities =
                { McpServer.readOnlyCapabilities with
                    Access = McpAccess.authenticate McpTestPolicy.policy (Some "Bearer synthetic-wrong-mcp-token-not-a-credential") }
            do! [ McpServer.readOnlyCapabilities; wrong ] |> Prelude.foldTask (denied db fs trace gate) ()
            let! reply = exercise [ gate ]
                            (fun () -> call McpTestPolicy.capabilities db fs None "hermes_reclassify" """{"document_id":1,"new_category":"reviewed"}""")
                            (resume fixture before trace [ "db.read"; "fs.exists"; "db.write" ] gate)
            do! verifyReclassified fixture before reply
        }

    [<Theory; InlineData(false); InlineData(true); Trait("Category", "Integration")>]
    let ``MCP mutation authenticates before effects and awaits the real atomic UPDATE`` (yieldEffects: bool) =
        withYieldEffects yieldEffects mutationBoundary
