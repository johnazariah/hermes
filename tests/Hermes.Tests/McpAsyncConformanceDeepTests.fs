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

module McpAsyncConformanceDeepTests =
    let arguments = """{"document_id":1,"force":true}"""
    let providerPrefix =
        [ "db.read"; "db.scalar"; "fs.exists"; "fs.read"; "fs.exists"; "fs.read" ]
        @ List.replicate 5 "db.scalar" @ [ "model" ]
    let private validation = [ "begin"; "db.write" ] @ List.replicate 4 "db.scalar" @ [ "db.write"; "end" ]
    let private publicationPrefix =
        providerPrefix @ validation @ [ "fs.exists"; "fs.read" ] @ validation @ [ "begin"; "db.read" ]

    let dependencies (fixture: Fixture) (trace: ConcurrentQueue<string>) (gate: Gate)
                     (outcome: Result<string, string>) : McpTools.DeepExtractionDeps =
        let chat: Algebra.ChatProvider =
            { complete = fun (system: string) (user: string) ->
                Assert.Equal("Synthetic structured extraction.", system)
                Assert.Equal(extractedText + "\n", user)
                trace.Enqueue("model")
                gate.Invoke fixture.Db (fun () -> Task.FromResult(outcome)) }
        let prompt: PromptLoader.ParsedPrompt =
            { System = "Synthetic structured extraction."; UserTemplate = "{{document_text}}\n{{context}}" }
        { Chat = chat; Registry = Map.ofList [ "payslip", prompt ]; Provider = "synthetic"; Model = "in-memory" }

    let modelOnly (fixture: Fixture) before (outcome: Result<string, string>) =
        let trace = ConcurrentQueue<string>()
        let gate = Gate("deep provider")
        let deps = dependencies fixture trace gate outcome
        exercise [ gate ]
            (fun () -> call McpTestPolicy.capabilities (database trace Open fixture.Db)
                            (files trace None fixture) (Some deps) "hermes_deep_extract" arguments)
            (resume fixture before trace providerPrefix gate)

    let private verifySuccess fixture before trace provider publication pending =
        task {
            do! resume fixture before trace providerPrefix provider pending
            do! resumeOwned fixture before trace publicationPrefix publication pending
        }

    let private verifyDeepShape (comprehension: JsonElement) =
        keys [ "document_type"; "summary"; "deep_extraction" ] comprehension
        Assert.Equal("payslip", text "document_type" comprehension)
        Assert.Equal("opaque record", text "summary" comprehension)
        let deep = comprehension.GetProperty("deep_extraction")
        keys [ "metadata"; "fields" ] deep
        equalJson """{"marker":"new"}""" (deep.GetProperty("fields"))
        let metadata = deep.GetProperty("metadata")
        keys [ "generated_at"; "provider"; "model"; "prompt_version"; "source_hash"; "schema_version" ] metadata
        Assert.Equal(TimeSpan.Zero, (DateTimeOffset.Parse(text "generated_at" metadata)).Offset)
        Assert.Equal("synthetic", text "provider" metadata)
        Assert.Equal("in-memory", text "model" metadata)
        Assert.Equal("deep-v1", text "schema_version" metadata)
        Assert.Equal(DeepExtraction.computeHash extractedText, text "source_hash" metadata)
        Assert.Equal(DeepExtraction.computeHash "Synthetic structured extraction.", text "prompt_version" metadata)

    let private verifyPublished (fixture: Fixture) before (reply: string) =
        task {
            let body = payload false reply
            keys [ "status"; "document_id"; "comprehension" ] body
            Assert.Equal("extracted", text "status" body)
            Assert.Equal(1L, body.GetProperty("document_id").GetInt64())
            verifyDeepShape (body.GetProperty("comprehension"))
            let saved = fixture.Files.Get sidecar |> Option.defaultWith (fun () -> failwith "Missing published artifact")
            equalJson saved (body.GetProperty("comprehension"))
            let! read = call McpServer.readOnlyCapabilities fixture.Db fixture.Files.Fs None "hermes_read_file" """{"path":"async/thread.comprehension.json"}"""
            equalJson (JsonSerializer.Serialize({| path = "async/thread.comprehension.json"; content = saved; size = Encoding.UTF8.GetByteCount(saved) |})) (payload false read)
            let! after = snapshot fixture.Db fixture.Files
            sameRows before after (historyTables @ [ "documents"; "corrections"; "tags"; "stage_completions"; "extraction"; "triage"; "comprehension"; "embedding"; "document_generations"; "reflow_operations" ])
            Assert.Equal<Map<string, string>>(Map.add (fixture.Files.Norm sidecar) saved before.Text, after.Text)
            Assert.Equal<Map<string, string>>(Map.add (fixture.Files.Norm sidecar) (Convert.ToHexString(Encoding.UTF8.GetBytes(saved))) before.Bytes, after.Bytes)
            Assert.Equal<Map<string, bool>>(before.Directories, after.Directories)
        }

    let private successfulRetry (fixture: Fixture) before =
        task {
            let trace = ConcurrentQueue<string>()
            let provider = Gate("retry provider")
            let publication = Gate("source ownership before artifact write")
            let deps = dependencies fixture trace provider (Ok """{"marker":"new"}""")
            let! reply = exercise [ provider; publication ]
                            (fun () -> call McpTestPolicy.capabilities (database trace (Ownership publication) fixture.Db)
                                            (files trace None fixture) (Some deps) "hermes_deep_extract" arguments)
                            (verifySuccess fixture before trace provider publication)
            do! verifyPublished fixture before reply
        }

    let private invalidJsonError () =
        try
            use _ = JsonDocument.Parse("not-json")
            failwith "The deliberately malformed provider result parsed"
        with :? JsonException as error ->
            "LLM returned invalid JSON: " + error.Message

    let private failedThenRetried (malformed: bool) (fixture: Fixture) =
        task {
            let! before = snapshot fixture.Db fixture.Files
            let outcome = if malformed then Ok "not-json" else Error "synthetic provider failure"
            let expected = if malformed then invalidJsonError () else "LLM call failed: synthetic provider failure"
            let! reply = modelOnly fixture before outcome
            equalJson (JsonSerializer.Serialize({| error = expected |})) (payload true reply)
            let! after = snapshot fixture.Db fixture.Files
            Assert.Equal<Snapshot>(before, after)
            do! successfulRetry fixture before
        }

    [<Theory;
      InlineData(false, false);
      InlineData(false, true);
      InlineData(true, false);
      InlineData(true, true);
      Trait("Category", "Integration")>]
    let ``MCP pending provider failures retain old proof and a pending retry publishes exactly once``
        (yieldEffects: bool) (malformed: bool) =
        withYieldEffects yieldEffects (failedThenRetried malformed)

    let private unavailable (missing: bool) (fixture: Fixture) =
        task {
            do! execute fixture.Db "DELETE FROM stage_completions WHERE document_id=1 AND stage_name='extract'" []
            let! before = snapshot fixture.Db fixture.Files
            let trace = ConcurrentQueue<string>()
            let read = Gate("deep target SELECT")
            let provider = Gate("provider must remain uncalled")
            let deps = dependencies fixture trace provider (Ok """{"marker":"unexpected"}""")
            let args = if missing then """{"document_id":9999}""" else arguments
            let! reply = exercise [ read; provider ]
                            (fun () -> call McpTestPolicy.capabilities (database trace (Read read) fixture.Db)
                                            (files trace None fixture) (Some deps) "hermes_deep_extract" args)
                            (resume fixture before trace [ "db.read" ] read)
            let expected = if missing then "Document 9999 not found" else "Document 1 extract and deep-comprehend artifacts are not current"
            equalJson (JsonSerializer.Serialize({| error = expected |})) (payload true reply)
            Assert.False(provider.WasEntered)
            let! after = snapshot fixture.Db fixture.Files
            Assert.Equal<Snapshot>(before, after)
        }

    [<Theory; InlineData(false); InlineData(true); Trait("Category", "Integration")>]
    let ``MCP pending target reads report missing and unextracted documents without invoking a model`` missing =
        withFixture (unavailable missing)

    let private capture (pending: Task<'T>) =
        task {
            try
                let! value = pending
                return Ok value
            with error ->
                return Error error
        }

    let private stagedSourceConflict (fixture: Fixture) =
        task {
            do! SourceFiles.writeNew fixture.Db fixture.Files.Fs sidecar (Encoding.UTF8.GetBytes(oldProof))
            let! before = snapshot fixture.Db fixture.Files
            let! outcome = capture (modelOnly fixture before (Ok """{"marker":"new"}"""))
            let! after = snapshot fixture.Db fixture.Files
            Assert.Equal<Snapshot>(before, after)
            let reply = outcome |> Result.defaultWith (fun error ->
                failwith $"Expected an MCP tool failure; publication faulted its task instead: {error}")
            equalJson (JsonSerializer.Serialize({| error = $"Saved path ownership conflict with a staged source: {sidecar}" |})) (payload true reply)
        }

    [<Fact; Trait("Category", "Integration")>]
    let ``MCP pending deep publication cannot overwrite a staged source or escape as a faulted task`` () =
        withFixture stagedSourceConflict
