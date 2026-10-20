namespace Hermes.Tests

open System
open System.Collections.Concurrent
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.AsyncCapabilityControl
open Hermes.Tests.McpAsyncConformanceFixture
open Hermes.Tests.McpAsyncConformanceControl
open Hermes.Tests.McpAsyncConformanceDeepTests

module McpAsyncConformanceReflowTests =
    let private verifyStatus (fixture: Fixture) =
        task {
            let! reply = call McpServer.readOnlyCapabilities fixture.Db fixture.Files.Fs None "hermes_reflow_status" """{"operation_id":1}"""
            let status = payload false reply
            keys [ "operation_id"; "document_id"; "kind"; "mode"; "lifecycle"; "dag_signature"; "created_at"; "stages" ] status
            Assert.Equal(1L, status.GetProperty("operation_id").GetInt64())
            Assert.Equal(1L, status.GetProperty("document_id").GetInt64())
            Assert.Equal("reextract", text "kind" status)
            Assert.Equal("apply", text "mode" status)
            Assert.Equal("running", text "lifecycle" status)
            Assert.Equal(Reflow.dagSignature (TestHelpers.standardV5Dag ()), text "dag_signature" status)
            let! rows = fixture.Db.execReader "SELECT created_at FROM reflow_operations WHERE id=1" []
            Assert.Equal((Prelude.RowReader (Assert.Single<Map<string, obj>>(rows))).String "created_at" "", text "created_at" status)
            // MCP status is ordered by stage_name, not DAG scheduling order.
            let stages =
                [ "deep-comprehend"; "embed"; "extract"; "triage" ]
                |> List.map (fun name -> {| stage_name = name; outcome = "pending" |})
            equalJson (JsonSerializer.Serialize(stages)) (status.GetProperty("stages"))
        }

    let private verifyInvalidated (fixture: Fixture) before =
        task {
            let! after = snapshot fixture.Db fixture.Files
            sameFiles before after
            sameRows before after (historyTables @ [ "tags" ])
            [ "extraction"; "triage"; "comprehension"; "embedding"; "stage_completions" ]
            |> List.iter (fun name -> Assert.Empty(after.Sql[name]))
            let! generation = Generation.current fixture.Db 1L
            Assert.Equal<Generation.Token>(({ DocumentId = 1L; Value = 1L }: Generation.Token), generation)
            let! rows = fixture.Db.execReader "SELECT category,classification_tier,extracted_vendor,sha256,saved_path FROM documents WHERE id=1" []
            equalJson ($"""[{{"category":"records","classification_tier":"manual","extracted_vendor":"verified synthetic","sha256":"{sha}","saved_path":"async/source.txt"}}]""") (json (JsonSerializer.Serialize(rows)))
        }

    let private applyPending (fixture: Fixture) before =
        task {
            let trace = ConcurrentQueue<string>()
            let gate = Gate("reflow transaction callback")
            let db = database trace (Transaction gate) fixture.Db
            let! reply = exercise [ gate ]
                            (fun () -> call McpTestPolicy.capabilities db (files trace None fixture) None "hermes_reextract" """{"document_id":1}""")
                            (resumeOwned fixture before trace [ "db.read"; "begin" ] gate)
            equalJson """{"status":"queued_for_reextraction","document_id":1,"operation_id":1,"duplicate":false}""" (payload false reply)
            Assert.Equal(1, trace |> Seq.filter ((=) "begin") |> Seq.length)
            Assert.Equal(1, trace |> Seq.filter ((=) "end") |> Seq.length)
            do! verifyStatus fixture
            do! verifyInvalidated fixture before
        }

    let private reflowWhilePending fixture before trace (provider: Gate) pending =
        task {
            let! _ = checkpoint fixture before provider pending
            Assert.Equal<string list>(providerPrefix, List.ofSeq trace)
            do! applyPending fixture before
            provider.Release()
        }

    let private superseded (fixture: Fixture) =
        task {
            let! before = snapshot fixture.Db fixture.Files
            let trace = ConcurrentQueue<string>()
            let provider = Gate("provider overtaken by audited reflow")
            let deps = dependencies fixture trace provider (Ok """{"marker":"stale"}""")
            let! reply = exercise [ provider ]
                            (fun () -> call McpTestPolicy.capabilities (database trace Open fixture.Db)
                                            (files trace None fixture) (Some deps) "hermes_deep_extract" arguments)
                            (reflowWhilePending fixture before trace provider)
            equalJson """{"error":"Document 1 was reflowed, or its shared folder was republished, while deep extraction ran; retry after comprehension is current"}""" (payload true reply)
            do! verifyInvalidated fixture before
            let! accepted = snapshot fixture.Db fixture.Files
            let! duplicate = call McpTestPolicy.capabilities fixture.Db fixture.Files.Fs None "hermes_reextract" """{"document_id":1}"""
            equalJson """{"status":"queued_for_reextraction","document_id":1,"operation_id":1,"duplicate":true}""" (payload false duplicate)
            let! unchanged = snapshot fixture.Db fixture.Files
            Assert.Equal<Snapshot>(accepted, unchanged)
        }

    [<Theory; InlineData(false); InlineData(true); Trait("Category", "Integration")>]
    let ``MCP pending reflow owns its transaction and supersedes an older pending provider without erasing history``
        (yieldEffects: bool) =
        withYieldEffects yieldEffects superseded
