module Hermes.Tests.DeepExtractionTests

#nowarn "3261"

open Xunit
open Hermes.Core
open System.Text.Json.Nodes
open System
open System.Threading.Tasks

// ─── promptFileForType tests ─────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DeepExtraction_promptFileForType_Payslip_ReturnsSome`` () =
    let result = DeepExtraction.promptFileForType "payslip"
    Assert.Equal(Some "payslip.md", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DeepExtraction_promptFileForType_PayrollStatementAlias_ReturnsSome`` () =
    let result = DeepExtraction.promptFileForType "payroll-statement"
    Assert.Equal(Some "payslip.md", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DeepExtraction_promptFileForType_AgentStatement_ReturnsSome`` () =
    let result = DeepExtraction.promptFileForType "agent-statement"
    Assert.Equal(Some "agent-statement.md", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DeepExtraction_promptFileForType_RentalStatementAlias_ReturnsSome`` () =
    let result = DeepExtraction.promptFileForType "rental-statement"
    Assert.Equal(Some "agent-statement.md", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DeepExtraction_promptFileForType_BankStatement_ReturnsSome`` () =
    let result = DeepExtraction.promptFileForType "bank-statement"
    Assert.Equal(Some "bank-statement.md", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DeepExtraction_promptFileForType_CreditCardAlias_ReturnsSome`` () =
    let result = DeepExtraction.promptFileForType "credit-card-statement"
    Assert.Equal(Some "bank-statement.md", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DeepExtraction_promptFileForType_Unknown_ReturnsNone`` () =
    let result = DeepExtraction.promptFileForType "invoice"
    Assert.True(result.IsNone)

// ─── computeHash tests ──────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DeepExtraction_computeHash_SameInput_SameOutput`` () =
    let hash1 = DeepExtraction.computeHash "hello world"
    let hash2 = DeepExtraction.computeHash "hello world"
    Assert.Equal(hash1, hash2)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DeepExtraction_computeHash_DifferentInput_DifferentOutput`` () =
    let hash1 = DeepExtraction.computeHash "hello world"
    let hash2 = DeepExtraction.computeHash "goodbye world"
    Assert.NotEqual<string>(hash1, hash2)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DeepExtraction_computeHash_Returns16Chars`` () =
    let hash = DeepExtraction.computeHash "test input"
    Assert.Equal(16, hash.Length)

// ─── mergeIntoComprehension tests ───────────────────────────────────

let private testMetadata : DeepExtraction.ExtractionMetadata =
    { GeneratedAt = "2025-01-01T00:00:00Z"
      Provider = "ollama"
      Model = "llama3"
      PromptVersion = "1.0"
      SourceHash = "abc123"
      SchemaVersion = "1.0" }

let private testDeepResult : DeepExtraction.DeepResult =
    { Fields = """{"gross_pay": 5000}"""
      Metadata = testMetadata }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DeepExtraction_mergeIntoComprehension_AddsDeepExtraction`` () =
    let existing = """{"document_type":"payslip"}"""

    match DeepExtraction.mergeIntoComprehension existing testDeepResult with
    | Error e -> failwith $"Expected Ok but got Error: {e}"
    | Ok merged ->
        let node = JsonNode.Parse(merged)
        let deep = node["deep_extraction"]
        Assert.NotNull(deep)
        let meta = deep["metadata"]
        let fields = deep["fields"]
        Assert.NotNull(meta)
        Assert.NotNull(fields)
        Assert.Equal("abc123", meta["source_hash"].GetValue<string>())
        Assert.Equal("ollama", meta["provider"].GetValue<string>())
        Assert.Equal("llama3", meta["model"].GetValue<string>())
        Assert.Equal(5000, fields["gross_pay"].GetValue<int>())

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DeepExtraction_mergeIntoComprehension_InvalidJson_ReturnsError`` () =
    let result = DeepExtraction.mergeIntoComprehension "not json" testDeepResult
    Assert.True(Result.isError result)

// ─── hasValidDeepExtraction tests ───────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DeepExtraction_hasValidDeepExtraction_MatchingHash_ReturnsTrue`` () =
    let json =
        """{"document_type":"payslip","deep_extraction":{"metadata":{"source_hash":"abc123"},"fields":{}}}"""
    Assert.True(DeepExtraction.hasValidDeepExtraction json "abc123")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DeepExtraction_hasValidDeepExtraction_DifferentHash_ReturnsFalse`` () =
    let json =
        """{"document_type":"payslip","deep_extraction":{"metadata":{"source_hash":"abc123"},"fields":{}}}"""
    Assert.False(DeepExtraction.hasValidDeepExtraction json "different-hash")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DeepExtraction_hasValidDeepExtraction_NoDeepExtraction_ReturnsFalse`` () =
    let json = """{"document_type":"payslip"}"""
    Assert.False(DeepExtraction.hasValidDeepExtraction json "abc123")

// ─── getDocumentType tests ──────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DeepExtraction_getDocumentType_Present_ReturnsSome`` () =
    let json = """{"document_type":"bank-statement"}"""
    Assert.Equal(Some "bank-statement", DeepExtraction.getDocumentType json)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DeepExtraction_getDocumentType_Missing_ReturnsNone`` () =
    let json = """{"summary":"hi"}"""
    Assert.True((DeepExtraction.getDocumentType json).IsNone)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DeepExtraction_getDocumentType_InvalidJson_ReturnsNone`` () =
    Assert.True((DeepExtraction.getDocumentType "not valid json").IsNone)

// ─── DeepExtraction.extract tests ────────────────────────────────────

let private mkPrompt sys user : PromptLoader.ParsedPrompt =
    { PromptLoader.ParsedPrompt.System = sys
      PromptLoader.ParsedPrompt.UserTemplate = user }

let private testRegistry : Map<string, PromptLoader.ParsedPrompt> =
    [ "payslip", mkPrompt "Extract payslip fields." "Document:\n{{document_text}}\n\nContext: {{context}}"
      "bank-statement", mkPrompt "Extract bank statement fields." "Document:\n{{document_text}}\n\nContext: {{context}}" ]
    |> Map.ofList

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DeepExtraction_extract_ValidPayslip_ReturnsOkWithMetadata`` () =
    task {
        let chat = TestHelpers.fakeChatProvider """{"gross_pay": 5000, "net_pay": 4000}"""
        let! result = DeepExtraction.extract chat testRegistry "ollama" "llama3" "payslip" "Employee: John" ""
        match result with
        | Ok deep ->
            Assert.Equal("ollama", deep.Metadata.Provider)
            Assert.Equal("llama3", deep.Metadata.Model)
            Assert.Equal("deep-v1", deep.Metadata.SchemaVersion)
            Assert.Contains("gross_pay", deep.Fields)
        | Error e -> failwith $"Expected Ok, got Error: {e}"
    }

// ─── Shared-folder revision fencing ──────────────────────────────────

let private readDocumentRow
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

let private siblingTriageDeps db fs provider : Stages.Deps =
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

let private payslipFence
    (savedPath: string)
    : PublicationFence.ArtifactFolder =
    PublicationFence.ArtifactFolder.tryFromMetadata
        "/archive" savedPath None
    |> Option.defaultWith (fun () -> failwith "Expected an artifact folder")

let private insertSiblingPayslips (db: Algebra.Database) : Task<unit> =
    task {
        let! _ =
            db.execNonQuery
                """INSERT INTO documents
                     (original_name, source_type, saved_path, category, sha256)
                   VALUES
                     ('a.pdf', 'manual_drop', 'payslips/a.pdf', 'payslips', 'sha-rev-a'),
                     ('b.pdf', 'manual_drop', 'payslips/b.pdf', 'payslips', 'sha-rev-b')"""
                []
        return ()
    }

let private markRevisionInputsCurrent db documentId =
    task {
        do! TestHelpers.initV5 db
        let! _ =
            db.execNonQuery
                """INSERT OR IGNORE INTO comprehension
                       (document_id, document_type, category, confidence)
                   VALUES (@doc, 'payslip', 'payslips', 1.0)"""
                [ ("@doc", Database.boxVal documentId) ]
        let! _ =
            db.execNonQuery
                """INSERT OR IGNORE INTO stage_completions
                       (document_id, stage_name)
                   VALUES (@doc, 'extract'), (@doc, 'deep-comprehend')"""
                [ ("@doc", Database.boxVal documentId) ]
        return ()
    }

let private revisionDeepDeps
    (chatResponse: string)
    : McpTools.DeepExtractionDeps =
    { Chat = TestHelpers.fakeChatProvider chatResponse
      Registry = testRegistry
      Provider = "ollama"
      Model = "llama3" }

let private revisionGatedProvider
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

/// Sibling A captures the shared folder revision before its slow model call.
/// Sibling B republishes the same sidecar through hermes_deep_extract while A
/// is parked, so A must be rejected on resume instead of overwriting B.
[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_deepExtract_RepublishBump_BlocksStaleSiblingComprehension`` () =
    task {
        let db = TestHelpers.createDb ()
        let mem = TestHelpers.memFs ()
        let entered =
            TaskCompletionSource<unit>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        let release =
            TaskCompletionSource<unit>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        try
            do! insertSiblingPayslips db
            do! markRevisionInputsCurrent db 2L
            let folder = "/archive/payslips"
            let sidecar = folder + "/thread.comprehension.json"
            mem.Put (folder + "/a.pdf.extracted.md") "Employee payslip A"
            mem.Put (folder + "/b.pdf.extracted.md") "Employee payslip B"
            mem.Put sidecar """{"document_type":"payslip","owner":"shared"}"""

            // Sibling A captures the folder revision, then parks in its model call.
            let! staleGeneration = Generation.current db 1L
            let! staleDoc = readDocumentRow db 1L
            let staleDeps =
                siblingTriageDeps db mem.Fs
                    (revisionGatedProvider entered release
                        """{"document_type":"letter","confidence":0.9,"summary":"stale-sibling-a","tags":["alpha"]}""")
            let staleWork =
                Stages.triageAt staleGeneration staleDeps staleDoc
            do! entered.Task

            // Sibling B republishes the shared sidecar through deep extraction.
            let args = JsonObject()
            args["document_id"] <- JsonValue.Create(2L)
            args["force"] <- JsonValue.Create(true)
            let! published =
                McpTools.deepExtract
                    db mem.Fs "/archive"
                    (revisionDeepDeps """{"gross_pay":5000}""")
                    (args :> JsonNode)
            Assert.Equal(
                "extracted", published["status"].GetValue<string>())
            let merged =
                mem.Get sidecar
                |> Option.defaultWith (fun () ->
                    failwith "Deep extraction wrote no shared artifact")
            Assert.Contains("gross_pay", merged)

            // Sibling A resumes last against a revision that has moved on.
            release.TrySetResult() |> ignore
            let! staleFailure =
                task {
                    try
                        let! _ = staleWork
                        return None
                    with error ->
                        return Some error.Message
                }
            Assert.True(
                staleFailure.IsSome,
                "Stale sibling must not report a successful publication")
            let settled =
                mem.Get sidecar
                |> Option.defaultWith (fun () ->
                    failwith "Shared artifact was deleted")
            Assert.Equal(merged, settled)
            Assert.DoesNotContain("stale-sibling-a", settled)
            let! stalePublications =
                db.execScalar
                    """SELECT count(*) FROM stage_publications
                       WHERE document_id = 1 AND stage_name = 'triage'"""
                    []
            Assert.Equal(0L, stalePublications :?> int64)
        finally
            release.TrySetResult() |> ignore
            db.dispose ()
    }

/// The republish must advance the folder revision transactionally, so a token
/// a sibling captured before it can no longer publish, and no stale bytes
/// reach the shared artifact.
[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_deepExtract_Republish_AdvancesRevisionAndVoidsSiblingToken`` () =
    task {
        let db = TestHelpers.createDb ()
        let mem = TestHelpers.memFs ()
        try
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents
                         (original_name, source_type, saved_path, category, sha256)
                       VALUES
                         ('test.pdf', 'manual_drop', 'payslips/test.pdf',
                          'payslips', 'sha-rev-bump')"""
                    []
            do! markRevisionInputsCurrent db 1L
            let folder = "/archive/payslips"
            let sidecar = folder + "/thread.comprehension.json"
            mem.Put (folder + "/test.pdf.extracted.md") "Employee payslip"
            mem.Put sidecar """{"document_type":"payslip","owner":"shared"}"""

            // A sibling captures the folder revision before the slow work.
            let fence = payslipFence "payslips/test.pdf"
            let! captured = ArtifactRevision.current db fence

            let args = JsonObject()
            args["document_id"] <- JsonValue.Create(1L)
            args["force"] <- JsonValue.Create(true)
            let! result =
                McpTools.deepExtract
                    db mem.Fs "/archive"
                    (revisionDeepDeps """{"gross_pay":5000}""")
                    (args :> JsonNode)
            Assert.Equal("extracted", result["status"].GetValue<string>())

            let! advanced = ArtifactRevision.current db fence
            Assert.True(
                advanced.Value > captured.Value,
                "Republishing the shared artifact must advance its folder revision")

            let merged =
                mem.Get sidecar
                |> Option.defaultWith (fun () -> failwith "Sidecar missing")
            Assert.Contains("gross_pay", merged)

            // The pre-captured token can no longer claim or write.
            let! generation = Generation.current db 1L
            let! stale =
                Generation.publishCanonical
                    db generation fence captured
                    (fun _ -> Task.FromResult "stale-canonical")
                    (fun _ ->
                        ArchiveWriter.writeComprehension
                            mem.Fs folder
                            """{"document_type":"payslip","owner":"stale"}""")
                    (fun _ _ -> Task.FromResult ())
            match stale with
            | Generation.Superseded -> ()
            | Generation.Published value ->
                failwith $"Stale sibling token must be rejected, got {value}"
            Assert.Equal(Some merged, mem.Get sidecar)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DeepExtraction_extract_UnsupportedType_ReturnsError`` () =
    task {
        let chat = TestHelpers.fakeChatProvider "{}"
        let! result = DeepExtraction.extract chat testRegistry "ollama" "llama3" "invoice" "Text" ""
        match result with
        | Error msg -> Assert.Contains("No deep extraction prompt", msg)
        | Ok _ -> failwith "Expected Error"
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DeepExtraction_extract_MissingFromRegistry_ReturnsError`` () =
    task {
        let chat = TestHelpers.fakeChatProvider "{}"
        let! result = DeepExtraction.extract chat Map.empty "ollama" "llama3" "payslip" "Text" ""
        match result with
        | Error msg -> Assert.Contains("not loaded", msg)
        | Ok _ -> failwith "Expected Error"
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DeepExtraction_extract_ChatFailure_ReturnsError`` () =
    task {
        let! result = DeepExtraction.extract TestHelpers.failingChatProvider testRegistry "ollama" "llama3" "payslip" "Text" ""
        match result with
        | Error msg -> Assert.Contains("LLM", msg)
        | Ok _ -> failwith "Expected Error"
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DeepExtraction_extract_CodeFencedJson_StripsAndSucceeds`` () =
    task {
        let chat = TestHelpers.fakeChatProvider "```json\n{\"gross_pay\": 3000}\n```"
        let! result = DeepExtraction.extract chat testRegistry "ollama" "llama3" "payslip" "Text" ""
        match result with
        | Ok deep -> Assert.Contains("gross_pay", deep.Fields)
        | Error e -> failwith $"Expected Ok, got Error: {e}"
    }

// ─── McpTools.deepExtract integration tests ──────────────────────────

let private mkDeepDeps (chatResponse: string) : McpTools.DeepExtractionDeps =
    { Chat = TestHelpers.fakeChatProvider chatResponse
      Registry = testRegistry
      Provider = "ollama"
      Model = "llama3" }

let private markDeepExtractionInputsCurrent db documentId =
    task {
        do! TestHelpers.initV5 db
        let! _ =
            db.execNonQuery
                """INSERT OR IGNORE INTO comprehension
                       (document_id, document_type, category, confidence)
                   VALUES (@doc, 'payslip', 'payslips', 1.0)"""
                [ ("@doc", Database.boxVal documentId) ]
        let! _ =
            db.execNonQuery
                """INSERT OR IGNORE INTO stage_completions (document_id, stage_name)
                   VALUES (@doc, 'extract'), (@doc, 'deep-comprehend')"""
                [ ("@doc", Database.boxVal documentId) ]
        return ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_deepExtract_ValidDocument_ReturnsMergedResult`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            let text = "Employee: John payslip data"
            let sourceHash = DeepExtraction.computeHash text
            let comp =
                sprintf
                    """{"document_type":"payslip","summary":"test payslip","deep_extraction":{"metadata":{"source_hash":"%s"},"fields":{"gross_pay":1000}}}"""
                    sourceHash
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (original_name, source_type, saved_path, category, sha256, extracted_at)
                       VALUES ('test.pdf', 'manual_drop', 'payslips/test.pdf', 'payslips', 'sha-deep-1', datetime('now'))"""
                    [  ]
            do! markDeepExtractionInputsCurrent db 1L
            m.Put "/archive/payslips/test.pdf.extracted.md" text
            m.Put "/archive/payslips/thread.comprehension.json" comp
            let deps = mkDeepDeps """{"gross_pay": 5000}"""
            let args = JsonObject()
            args["document_id"] <- JsonValue.Create(1L)
            args["force"] <- JsonValue.Create(true)
            let! result = McpTools.deepExtract db m.Fs "/archive" deps (args :> JsonNode)
            Assert.Equal("extracted", result["status"].GetValue<string>())
            let comprehension: JsonNode = result.["comprehension"]
            let deepExtraction: JsonNode = comprehension.["deep_extraction"]
            let fields: JsonNode = deepExtraction.["fields"]
            let grossPay = fields.["gross_pay"].GetValue<int>()
            Assert.Equal(5000, grossPay)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_deepExtract_InvalidatedComprehension_ReturnsErrorEvenWhenForced`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (original_name, source_type, saved_path, category, sha256)
                       VALUES ('test.pdf', 'manual_drop', 'payslips/test.pdf', 'payslips', 'sha-stale')"""
                    []
            do! markDeepExtractionInputsCurrent db 1L
            let! _ =
                db.execNonQuery
                    """DELETE FROM stage_completions
                       WHERE document_id = 1 AND stage_name = 'deep-comprehend'"""
                    []
            m.Put "/archive/payslips/test.pdf.extracted.md" "stale extracted text"
            m.Put "/archive/payslips/thread.comprehension.json" """{"document_type":"payslip"}"""
            let args = JsonObject()
            args["document_id"] <- JsonValue.Create(1L)
            args["force"] <- JsonValue.Create(true)
            let! result =
                McpTools.deepExtract db m.Fs "/archive" (mkDeepDeps "{}") (args :> JsonNode)
            Assert.Contains("not current", result["error"].GetValue<string>())
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_deepExtract_MissingComprehensionOutput_ReturnsNotCurrentEvenWhenForced`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (original_name, source_type, saved_path, category, sha256)
                       VALUES ('test.pdf', 'manual_drop', 'payslips/test.pdf', 'payslips', 'sha-stale-output')"""
                    []
            do! markDeepExtractionInputsCurrent db 1L
            let! _ = db.execNonQuery "DELETE FROM comprehension WHERE document_id = 1" []
            let comprehensionPath = "/archive/payslips/thread.comprehension.json"
            let staleComprehension = """{"document_type":"payslip"}"""
            m.Put "/archive/payslips/test.pdf.extracted.md" "stale extracted text"
            m.Put comprehensionPath staleComprehension
            let args = JsonObject()
            args["document_id"] <- JsonValue.Create(1L)
            args["force"] <- JsonValue.Create(true)
            let! result = McpTools.deepExtract db m.Fs "/archive" (mkDeepDeps "{}") args
            Assert.Contains("not current", result["error"].GetValue<string>())
            Assert.Equal(Some staleComprehension, m.Get comprehensionPath)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_deepExtract_MissingDocument_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! TestHelpers.initV5 db
            let args = JsonObject()
            args["document_id"] <- JsonValue.Create(999L)
            let! result = McpTools.deepExtract db (TestHelpers.memFs().Fs) "/archive" (mkDeepDeps "{}") (args :> JsonNode)
            Assert.Contains("not found", result["error"].GetValue<string>())
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_deepExtract_NoComprehension_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (original_name, source_type, saved_path, category, sha256, extracted_at)
                       VALUES ('test.pdf', 'manual_drop', 'payslips/test.pdf', 'payslips', 'sha-deep-2', datetime('now'))"""
                    []
            do! markDeepExtractionInputsCurrent db 1L
            let args = JsonObject()
            args["document_id"] <- JsonValue.Create(1L)
            let! result = McpTools.deepExtract db (TestHelpers.memFs().Fs) "/archive" (mkDeepDeps "{}") (args :> JsonNode)
            Assert.Contains("no comprehension", result["error"].GetValue<string>())
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_deepExtract_MissingDocumentId_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let emptyArgs = JsonObject() :> JsonNode
            let! result = McpTools.deepExtract db (TestHelpers.memFs().Fs) "/archive" (mkDeepDeps "{}") emptyArgs
            Assert.Contains("document_id is required", result["error"].GetValue<string>())
        finally db.dispose ()
    }

/// Parks the caller in the LLM call — before the publication fence is taken —
/// so a test can order a reflow and a newer publication ahead of a stale write.
let private gatedChatProvider
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
let ``McpTools_deepExtract_ReflowThenNewerPublication_CannotOverwriteNewerSidecar`` () =
    task {
        let db = TestHelpers.createDb ()
        let mem = TestHelpers.memFs ()
        let entered =
            TaskCompletionSource<unit>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        let release =
            TaskCompletionSource<unit>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        try
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents
                         (original_name, source_type, saved_path,
                          category, sha256)
                       VALUES
                         ('test.pdf', 'manual_drop',
                          'payslips/test.pdf', 'payslips',
                          'sha-deep-race')"""
                    []
            do! markDeepExtractionInputsCurrent db 1L
            let folder = "/archive/payslips"
            let sidecar = folder + "/thread.comprehension.json"
            let original = """{"document_type":"payslip","epoch":"old"}"""
            mem.Put (folder + "/test.pdf.extracted.md") "Employee payslip"
            mem.Put sidecar original
            let deps =
                { mkDeepDeps """{"gross_pay":5000}""" with
                    Chat =
                        gatedChatProvider entered release """{"gross_pay":5000}""" }
            let args = JsonObject()
            args["document_id"] <- JsonValue.Create(1L)
            args["force"] <- JsonValue.Create(true)
            // OLD publisher: captured generation 0, parked before its write.
            let stale =
                McpTools.deepExtract db mem.Fs "/archive" deps (args :> JsonNode)
            do! entered.Task

            // Reflow accepts and bumps the generation, then the new generation
            // republishes the shared sidecar — both complete before the old
            // write is released.
            let! reflow =
                Reflow.request
                    db TestHelpers.silentLogger
                    (TestHelpers.standardV5Dag ())
                    1L Reflow.Recomprehend Reflow.Apply
            match reflow with
            | Error error -> failwith error
            | Ok _ -> ()
            do! markDeepExtractionInputsCurrent db 1L
            let! current = Generation.current db 1L
            let newest = """{"document_type":"payslip","epoch":"new"}"""
            let artifactFolder =
                PublicationFence.ArtifactFolder.tryFromMetadata
                    "payslips/test.pdf" None
                |> Option.defaultWith (fun () ->
                    failwith "Expected an artifact folder")
            let! republished =
                Generation.publishEffect db current artifactFolder (fun () ->
                    ArchiveWriter.writeComprehension mem.Fs folder newest)

            release.TrySetResult() |> ignore
            let! result = stale
            let! outputs =
                db.execScalar
                    "SELECT count(*) FROM comprehension WHERE document_id = 1"
                    []
            match republished with
            | Generation.Published () -> ()
            | Generation.Superseded ->
                failwith "New-generation publication was rejected"
            Assert.Contains(
                "reflowed",
                result["error"].GetValue<string>())
            // The stale publisher never wrote: newer bytes survive intact and
            // the shared sidecar is never deleted.
            Assert.Equal(Some newest, mem.Get sidecar)
            Assert.True(mem.Fs.fileExists sidecar)
            Assert.Equal(1L, outputs :?> int64)
        finally
            release.TrySetResult() |> ignore
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_deepExtract_SiblingPublishesDuringLlm_MergesAgainstLatestSidecar`` () =
    task {
        let db = TestHelpers.createDb ()
        let mem = TestHelpers.memFs ()
        let entered =
            TaskCompletionSource<unit>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        let release =
            TaskCompletionSource<unit>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        try
            // Two documents in ONE folder, described with different metadata
            // forms, so they only contend correctly once folder identity is
            // resolved against the archive root.
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents
                         (original_name, source_type, saved_path, category, sha256)
                       VALUES
                         ('a.pdf', 'manual_drop', 'payslips/a.pdf', 'payslips', 'sha-sib-a'),
                         ('b.pdf', 'manual_drop', '/archive/payslips/b.pdf', 'payslips', 'sha-sib-b')"""
                    []
            do! markDeepExtractionInputsCurrent db 1L
            do! markDeepExtractionInputsCurrent db 2L
            let folder = "/archive/payslips"
            let sidecar = folder + "/thread.comprehension.json"
            mem.Put (folder + "/a.pdf.extracted.md") "Employee payslip A"
            mem.Put sidecar """{"document_type":"payslip","owner":"a"}"""
            let deps =
                { mkDeepDeps "{}" with
                    Chat =
                        gatedChatProvider entered release """{"gross_pay":5000}""" }
            let args = JsonObject()
            args["document_id"] <- JsonValue.Create(1L)
            args["force"] <- JsonValue.Create(true)
            let running =
                McpTools.deepExtract db mem.Fs "/archive" deps (args :> JsonNode)
            do! entered.Task

            // The sibling publishes while the LLM runs. Document 1's
            // generation never changes, so only the folder fence plus a
            // re-read inside it can protect the sibling's bytes.
            let! siblingGeneration = Generation.current db 2L
            let siblingFolder =
                PublicationFence.ArtifactFolder.tryFromMetadata
                    "/archive" "/archive/payslips/b.pdf" None
                |> Option.defaultWith (fun () -> failwith "Expected a folder")
            let siblingJson =
                """{"document_type":"payslip","owner":"a","sibling":"published"}"""
            let! published =
                Generation.publishEffect db siblingGeneration siblingFolder (fun () ->
                    ArchiveWriter.writeComprehension mem.Fs folder siblingJson)
            match published with
            | Generation.Published () -> ()
            | Generation.Superseded -> failwith "Sibling publication was rejected"

            release.TrySetResult() |> ignore
            let! result = running
            Assert.Equal("extracted", result["status"].GetValue<string>())
            let committed = result["comprehension"]
            // Sibling fields survive …
            Assert.Equal("published", committed["sibling"].GetValue<string>())
            // … and this document's own delta is present.
            Assert.Equal(
                5000,
                (committed["deep_extraction"].["fields"].["gross_pay"]).GetValue<int>())
            // The returned JSON is exactly what was committed to disk.
            let onDisk =
                mem.Get sidecar
                |> Option.defaultWith (fun () -> failwith "Sidecar missing")
            Assert.Contains("\"sibling\"", onDisk)
            Assert.Contains("gross_pay", onDisk)
            Assert.Equal(
                JsonNode.Parse(onDisk).ToJsonString(),
                committed.ToJsonString())
        finally
            release.TrySetResult() |> ignore
            db.dispose ()
    }
