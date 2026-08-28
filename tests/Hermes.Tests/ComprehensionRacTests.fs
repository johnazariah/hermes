module Hermes.Tests.ComprehensionRacTests

#nowarn "3261"

open System.Threading.Tasks
open System
open Xunit
open Hermes.Core

// ─── extractSenderDomain ─────────────────────────────────────────────

[<Theory>]
[<InlineData("noreply@telstra.com", "telstra.com")>]
[<InlineData("John Smith <john@example.org>", "example.org")>]
[<InlineData("support@ato.gov.au", "ato.gov.au")>]
[<Trait("Category", "Unit")>]
let ``Stages_ExtractSenderDomain_ValidEmail_ReturnsDomain`` (sender: string, expected: string) =
    let result = Stages.extractSenderDomain sender
    Assert.Equal(Some expected, result)

[<Theory>]
[<InlineData("no-email-here")>]
[<InlineData("")>]
[<InlineData("just a name")>]
[<Trait("Category", "Unit")>]
let ``Stages_ExtractSenderDomain_NoAt_ReturnsNone`` (sender: string) =
    Assert.Equal(None, Stages.extractSenderDomain sender)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Stages_ExtractSenderDomain_AngleBrackets_StripsThem`` () =
    let result = Stages.extractSenderDomain "HR <payroll@microsoft.com>"
    Assert.Equal(Some "microsoft.com", result)

// ─── compactSchemaHint ───────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Stages_CompactSchemaHint_ValidJson_ReturnsTypeAndFieldNames`` () =
    let json = """{"document_type":"invoice","confidence":0.9,"summary":"test","fields":{"vendor":"Telstra","amount":89.5,"date":"2026-03-15"}}"""
    let result = Stages.compactSchemaHint json
    Assert.True(result.IsSome, "Expected Some")
    let hint = result.Value
    Assert.Contains("invoice", hint)
    Assert.Contains("vendor", hint)
    Assert.Contains("amount", hint)
    Assert.Contains("date", hint)
    // Must NOT contain actual values
    Assert.DoesNotContain("Telstra", hint)
    Assert.DoesNotContain("89.5", hint)
    Assert.DoesNotContain("2026-03-15", hint)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Stages_CompactSchemaHint_NoFields_ReturnsTypeOnly`` () =
    let json = """{"document_type":"letter","confidence":0.8,"summary":"A letter"}"""
    let result = Stages.compactSchemaHint json
    Assert.True(result.IsSome)
    Assert.Contains("letter", result.Value)
    Assert.Contains("field_names", result.Value)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Stages_CompactSchemaHint_InvalidJson_ReturnsNone`` () =
    Assert.Equal(None, Stages.compactSchemaHint "not json at all")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Stages_CompactSchemaHint_EmptyFields_ReturnsEmptyArray`` () =
    let json = """{"document_type":"report","fields":{}}"""
    let result = Stages.compactSchemaHint json
    Assert.True(result.IsSome)
    Assert.Contains("field_names", result.Value)
    Assert.Contains("[]", result.Value)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Stages_CompactSchemaHint_CapsAt300Chars`` () =
    // Create JSON with many fields to exceed 300 chars
    let manyFields =
        [ for i in 1..50 -> $"\"very_long_field_name_{i}\":\"value\"" ]
        |> String.concat ","
    let json = $"""{{"document_type":"huge","fields":{{{manyFields}}}}}"""
    let result = Stages.compactSchemaHint json
    Assert.True(result.IsSome)
    Assert.True(result.Value.Length <= 300, $"Hint too long: {result.Value.Length}")

let private racDocumentId (value: obj | null) : int64 =
    match value with
    | :? int64 as value -> value
    | value -> failwith $"Expected document ID, got {value}"

let private insertRacDocument
    (db: Algebra.Database)
    (folder: string)
    (tier: string)
    : Task<int64> =
    task {
        let! value =
            db.execScalar
                """INSERT INTO documents
                     (source_type, saved_path, folder_path, category, sha256,
                      sender, classification_tier, classification_confidence,
                      extracted_at)
                   VALUES
                     ('email_attachment', @path, @folder, 'invoices', @sha,
                      'billing@example.com', @tier, 0.95, datetime('now'))
                   RETURNING id"""
                [ ("@path", Database.boxVal $"{folder}/source.pdf")
                  ("@folder", Database.boxVal folder)
                  ("@sha", Database.boxVal folder)
                  ("@tier", Database.boxVal tier) ]
        return racDocumentId value
    }

let private markCurrentExample
    (db: Algebra.Database)
    (documentId: int64)
    : Task<unit> =
    task {
        let! _ =
            db.execNonQuery
                """INSERT INTO stage_completions (document_id, stage_name)
                   VALUES (@doc, 'deep-comprehend')"""
                [ ("@doc", Database.boxVal documentId) ]
        let! _ =
            db.execNonQuery
                """INSERT INTO comprehension
                     (document_id, document_type, category, confidence)
                   VALUES (@doc, 'current-example', 'invoices', 0.95)"""
                [ ("@doc", Database.boxVal documentId) ]
        return ()
    }

let private readRacDocument
    (db: Algebra.Database)
    (documentId: int64)
    : Task<Document.T> =
    task {
        let! rows =
            db.execReader
                "SELECT * FROM documents WHERE id = @doc"
                [ ("@doc", Database.boxVal documentId) ]
        return rows |> List.exactlyOne |> Document.fromRow
    }

let private prepareRacDocuments db (mem: TestHelpers.MemFs) =
    task {
        let! _ = insertRacDocument db "/archive/stale" "manual"
        let! currentId =
            insertRacDocument db "/archive/current" "comprehension"
        let! targetId = insertRacDocument db "/archive/target" "triage"
        do! markCurrentExample db currentId
        mem.Put
            "/archive/stale/thread.comprehension.json"
            """{"document_type":"stale-manual","fields":{"stale_field":"old"}}"""
        mem.Put
            "/archive/current/thread.comprehension.json"
            """{"document_type":"current-example","fields":{"current_field":"ok"}}"""
        mem.Put
            "/archive/target/source.pdf.extracted.md"
            "Target invoice text"
        let! target = readRacDocument db targetId
        return target, currentId
    }

let private capturingProvider
    (captured: TaskCompletionSource<string>)
    : Algebra.ChatProvider =
    let response =
        """{"document_type":"invoice","confidence":0.95,"summary":"target invoice","tags":[],"fields":{"amount":10}}"""
    { complete =
        fun _ prompt ->
            captured.TrySetResult(prompt) |> ignore
            Task.FromResult(Ok response) }

let private racDeps db fs provider : Stages.Deps =
    { Fs = fs
      Db = db
      Logger = TestHelpers.silentLogger
      Clock = TestHelpers.defaultClock
      Extractor = Interpreters.nullTextExtractor
      Embedder = None
      ChatProvider = Some provider
      TriageProvider = None
      ContentRules = []
      ComprehensionPrompt = None
      TriagePrompt = None
      Preferences = ""
      ArchiveDir = "/archive" }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Stages_RacExamples_StaleManualSidecarWithoutCurrentOutputIsExcluded`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! TestHelpers.initV5 db
            let mem = TestHelpers.memFs ()
            let! target, _ = prepareRacDocuments db mem
            let captured =
                TaskCompletionSource<string>(
                    TaskCreationOptions.RunContinuationsAsynchronously)
            let! _ =
                Stages.deepComprehend
                    (racDeps db mem.Fs (capturingProvider captured))
                    target
            let! prompt = captured.Task
            Assert.Contains("current-example", prompt)
            Assert.DoesNotContain("stale-manual", prompt)
        finally db.dispose ()
    }

let private invalidateAfterCurrencyCheck
    (db: Algebra.Database)
    (exampleId: int64)
    : Algebra.Database =
    let fired =
        TaskCompletionSource<unit>(
            TaskCreationOptions.RunContinuationsAsynchronously)
    { db with
        execScalar =
            fun sql parameters ->
                task {
                    let! value = db.execScalar sql parameters
                    if
                        sql.Contains(
                            "JOIN comprehension c",
                            StringComparison.Ordinal)
                        && fired.TrySetResult(())
                    then
                        let! result =
                            Reflow.request
                                db TestHelpers.silentLogger
                                (TestHelpers.standardV5Dag ())
                                exampleId
                                Reflow.Recomprehend
                                Reflow.Apply
                        match result with
                        | Error error -> failwith error
                        | Ok _ -> ()
                    return value
                } }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Stages_RacRead_InvalidationBeforeUse_ExcludesSupersededHint`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! TestHelpers.initV5 db
            let mem = TestHelpers.memFs ()
            let! target, exampleId =
                prepareRacDocuments db mem
            let prompt =
                TaskCompletionSource<string>(
                    TaskCreationOptions.RunContinuationsAsynchronously)
            let racingDb =
                invalidateAfterCurrencyCheck db exampleId
            let deps =
                racDeps
                    racingDb mem.Fs
                    (capturingProvider prompt)
            let! _ = Stages.deepComprehend deps target
            let! captured = prompt.Task
            Assert.DoesNotContain("current-example", captured)
        finally
            db.dispose ()
    }

// ─── Comprehension retry is exactly-once ─────────────────────────────

let private sequencedProvider (responses: string list) : Algebra.ChatProvider =
    let remaining = System.Collections.Concurrent.ConcurrentQueue<string>(responses)
    { complete =
        fun _ _ ->
            match remaining.TryDequeue() with
            | true, response -> Task.FromResult(Ok response)
            | _ -> Task.FromResult(Error "no scripted response left") }

let private countOf (db: Algebra.Database) (sql: string) (docId: int64) : Task<int64> =
    task {
        let! value = db.execScalar sql [ ("@doc", Database.boxVal docId) ]
        return match value with :? int64 as v -> v | _ -> 0L
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Stages_TriageRetry_DivergentResponse_KeepsCanonicalPublicationExactlyOnce`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! TestHelpers.initV5 db
            let mem = TestHelpers.memFs ()
            let! docId = insertRacDocument db "/archive/retry" "triage"
            mem.Put "/archive/retry/source.pdf.extracted.md" "Retry document text"
            let provider =
                sequencedProvider
                    [ """{"document_type":"letter","confidence":0.5,"summary":"first","tags":["alpha"]}"""
                      """{"document_type":"personal","confidence":0.4,"summary":"second","tags":["beta"]}""" ]
            let deps = racDeps db mem.Fs provider
            let! generation = Generation.current db docId
            let! before = readRacDocument db docId
            let! first = Stages.triageAt generation deps before
            Assert.Contains("first", first |> Document.decode<string> "comprehension" |> Option.defaultValue "")

            // The first attempt's stage output changed the live category.
            // The finalisation then faulted, so the same generation is retried.
            let! _ =
                db.execNonQuery
                    "UPDATE documents SET category = 'unsorted' WHERE id = @doc"
                    [ ("@doc", Database.boxVal docId) ]
            let! contactsAfterFirst =
                countOf db "SELECT count(*) FROM document_contacts WHERE document_id = @doc" docId
            let! reread = readRacDocument db docId
            let! second = Stages.triageAt generation deps reread

            // The canonical response stays the one that was published.
            let committed =
                second |> Document.decode<string> "comprehension" |> Option.defaultValue ""
            Assert.Contains("first", committed)
            Assert.DoesNotContain("second", committed)
            let! publications =
                countOf db
                    "SELECT count(*) FROM stage_publications WHERE document_id = @doc AND stage_name = 'triage'"
                    docId
            Assert.Equal(1L, publications)

            // Exactly one of every derived effect.
            let! suggestions =
                countOf db
                    "SELECT count(*) FROM suggestions WHERE document_id = @doc AND status = 'pending'"
                    docId
            Assert.Equal(1L, suggestions)
            let! displaced =
                db.execScalar
                    "SELECT current_category FROM suggestions WHERE document_id = @doc"
                    [ ("@doc", Database.boxVal docId) ]
            Assert.Equal("invoices", string displaced)
            let! tags =
                countOf db
                    "SELECT count(*) FROM tags WHERE document_id = @doc AND source = 'comprehension'"
                    docId
            Assert.Equal(1L, tags)
            let! evidence =
                countOf db
                    "SELECT count(*) FROM learned_pattern_evidence WHERE document_id = @doc AND stage_name = 'triage'"
                    docId
            Assert.Equal(1L, evidence)
            let! contactsAfterSecond =
                countOf db "SELECT count(*) FROM document_contacts WHERE document_id = @doc" docId
            Assert.Equal(contactsAfterFirst, contactsAfterSecond)

            // The shared sidecar still exists and holds the canonical bytes.
            let sidecar =
                mem.Get "/archive/retry/thread.comprehension.json"
                |> Option.defaultWith (fun () -> failwith "Sidecar was deleted")
            Assert.Contains("first", sidecar)

            // A DISTINCT document with the same sender still accumulates.
            let! otherId = insertRacDocument db "/archive/retry-other" "triage"
            mem.Put "/archive/retry-other/source.pdf.extracted.md" "Another document"
            let otherDeps =
                racDeps db mem.Fs
                    (sequencedProvider
                        [ """{"document_type":"letter","confidence":0.9,"summary":"third","tags":["alpha"]}""" ])
            let! otherGeneration = Generation.current db otherId
            let! other = readRacDocument db otherId
            let! _ = Stages.triageAt otherGeneration otherDeps other
            let! learned =
                db.execScalar
                    "SELECT count FROM learned_patterns WHERE sender_domain = 'example.com' AND document_type = 'letter'"
                    []
            Assert.Equal(2L, (match learned with :? int64 as v -> v | _ -> 0L))
        finally db.dispose ()
    }

// ─── Shared-folder sibling ordering ──────────────────────────────────

/// Blocks inside the model call so the slow sibling holds no fence and no
/// database gate while the fast sibling publishes.
let private gatedProvider
    (entered: TaskCompletionSource<unit>)
    (release: TaskCompletionSource<unit>)
    (response: string)
    : Algebra.ChatProvider =
    { complete =
        fun _ _ ->
            task {
                entered.TrySetResult() |> ignore
                do! release.Task
                return Ok response
            } }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Stages_SharedFolderSiblings_StaleSibling_CannotOverwriteNewerSibling`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! TestHelpers.initV5 db
            let mem = TestHelpers.memFs ()

            // Two documents in ONE thread folder: they share thread.comprehension.json
            // but own independent document generations.
            let! slowId = insertRacDocument db "/archive/shared-thread" "triage"
            let! fastId = insertRacDocument db "/archive/shared-thread" "triage"
            mem.Put "/archive/shared-thread/source.pdf.extracted.md" "Shared thread text"

            let entered =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously)
            let release =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously)

            // Sibling A starts FIRST and blocks inside the model call.
            let! slowGeneration = Generation.current db slowId
            let! slowDoc = readRacDocument db slowId
            let slowDeps =
                racDeps db mem.Fs
                    (gatedProvider entered release
                        """{"document_type":"letter","confidence":0.9,"summary":"stale-sibling-a","tags":["alpha"]}""")
            let slowWork = Stages.triageAt slowGeneration slowDeps slowDoc
            do! entered.Task

            // Sibling B publishes newer state for the same shared artifact.
            let! fastGeneration = Generation.current db fastId
            let! fastDoc = readRacDocument db fastId
            let fastDeps =
                racDeps db mem.Fs
                    (sequencedProvider
                        [ """{"document_type":"letter","confidence":0.9,"summary":"newer-sibling-b","tags":["beta"]}""" ])
            let! _ = Stages.triageAt fastGeneration fastDeps fastDoc

            let published =
                mem.Get "/archive/shared-thread/thread.comprehension.json"
                |> Option.defaultWith (fun () ->
                    failwith "Sibling B never wrote the shared artifact")
            Assert.Contains("newer-sibling-b", published)

            // Sibling A now completes LAST. It must not write any bytes.
            release.TrySetResult() |> ignore
            let! staleFailure =
                task {
                    try
                        let! _ = slowWork
                        return None
                    with error ->
                        return Some error.Message
                }

            let settled =
                mem.Get "/archive/shared-thread/thread.comprehension.json"
                |> Option.defaultWith (fun () ->
                    failwith "Shared artifact was deleted")
            Assert.Contains("newer-sibling-b", settled)
            Assert.DoesNotContain("stale-sibling-a", settled)
            Assert.True(
                staleFailure.IsSome,
                "Stale sibling must not report a successful publication")

            // The stale sibling published nothing derived either.
            let! stalePublications =
                countOf db
                    "SELECT count(*) FROM stage_publications WHERE document_id = @doc AND stage_name = 'triage'"
                    slowId
            Assert.Equal(0L, stalePublications)
            let! freshPublications =
                countOf db
                    "SELECT count(*) FROM stage_publications WHERE document_id = @doc AND stage_name = 'triage'"
                    fastId
            Assert.Equal(1L, freshPublications)
        finally db.dispose ()
    }
