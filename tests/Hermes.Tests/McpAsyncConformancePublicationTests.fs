namespace Hermes.Tests

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.AsyncCapabilityControl
open Hermes.Tests.McpAsyncConformanceFixture
open Hermes.Tests.McpAsyncConformanceControl
open Hermes.Tests.McpAsyncConformanceDeepTests

module McpAsyncConformancePublicationTests =
    let private registerSidecarSource (fixture: Fixture) =
        task {
            let bytes = Encoding.UTF8.GetBytes(oldProof)
            do! SourceFiles.writeNew fixture.Db fixture.Files.Fs sidecar bytes
            do! execute fixture.Db
                    """INSERT INTO documents(id,source_type,saved_path,category,sha256)
                       VALUES (2,'manual_drop','async/./thread.comprehension.json','records',@sha)"""
                    [ "@sha", Database.boxVal (SourceFiles.hash bytes) ]
        }

    let private registeredOwner (fixture: Fixture) =
        task {
            do! registerSidecarSource fixture
            let! before = snapshot fixture.Db fixture.Files
            let! reply = modelOnly fixture before (Ok """{"marker":"new"}""")
            let expected = $"Saved path ownership conflict for {sidecar}; owners: [2]"
            equalJson (JsonSerializer.Serialize({| error = expected |})) (payload true reply)
            let! legacy = Assert.ThrowsAsync<InvalidOperationException>(
                Func<Task>(fun () ->
                    SourceFiles.writeMetadata fixture.Db fixture.Files.Fs sidecar
                        (Encoding.UTF8.GetBytes("must not replace the source")) :> Task))
            Assert.Equal(expected, legacy.Message)
            let! after = snapshot fixture.Db fixture.Files
            Assert.Equal<Snapshot>(before, after)
        }

    [<Theory; InlineData(false); InlineData(true); Trait("Category", "Integration")>]
    let ``MCP pending deep publication respects canonical document ownership and legacy rejection``
        (yieldEffects: bool) =
        withYieldEffects yieldEffects registeredOwner

    let private failingFiles
        (fixture: Fixture) (trace: ConcurrentQueue<string>)
        (fault: InvalidOperationException) : Algebra.FileSystem =
        let observed = files trace None fixture
        { observed with
            writeAllBytes = fun (path: string) (_bytes: byte array) ->
                trace.Enqueue("fs.bytes")
                Assert.Equal(fixture.Files.Norm sidecar, fixture.Files.Norm path)
                Task.FromException<unit>(fault) }

    let private runWithFiles
        (fixture: Fixture) (before: Snapshot)
        (trace: ConcurrentQueue<string>) (gate: Gate)
        (fs: Algebra.FileSystem) : Task<string> =
        let deps = dependencies fixture trace gate (Ok """{"marker":"new"}""")
        exercise [ gate ]
            (fun () -> call McpTestPolicy.capabilities (database trace Open fixture.Db)
                            fs (Some deps) "hermes_deep_extract" arguments)
            (resume fixture before trace providerPrefix gate)

    let private programmingFault (fixture: Fixture) =
        task {
            let! before = snapshot fixture.Db fixture.Files
            let trace = ConcurrentQueue<string>()
            let gate = Gate("provider before unexpected metadata writer fault")
            let fault = InvalidOperationException("synthetic programming fault in metadata writer")
            let fs = failingFiles fixture trace fault
            let! actual = Assert.ThrowsAsync<InvalidOperationException>(
                Func<Task>(fun () -> runWithFiles fixture before trace gate fs :> Task))
            Assert.Same(fault, actual)
            Assert.Equal("fs.bytes", Seq.last trace)
            let! after = snapshot fixture.Db fixture.Files
            Assert.Equal<Snapshot>(before, after)
        }

    [<Fact; Trait("Category", "Integration")>]
    let ``MCP pending deep publication does not disguise an unrelated programming fault as a tool result`` () =
        withFixture programmingFault
