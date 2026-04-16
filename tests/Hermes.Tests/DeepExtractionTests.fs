module Hermes.Tests.DeepExtractionTests

#nowarn "3261"

open Xunit
open Hermes.Core
open System.Text.Json.Nodes

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

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_deepExtract_ValidDocument_ReturnsMergedResult`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let comp = """{"document_type":"payslip","summary":"test payslip"}"""
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (original_name, source_type, saved_path, category, sha256, extracted_text, comprehension)
                       VALUES ('test.pdf', 'manual_drop', 'payslips/test.pdf', 'payslips', 'sha-deep-1', @text, @comp)"""
                    [ ("@text", Database.boxVal "Employee: John"); ("@comp", Database.boxVal comp) ]
            let deps = mkDeepDeps """{"gross_pay": 5000}"""
            let args = JsonObject()
            args["document_id"] <- JsonValue.Create(1L)
            let! result = McpTools.deepExtract db deps (args :> JsonNode)
            Assert.Equal("extracted", result["status"].GetValue<string>())
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_deepExtract_MissingDocument_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let args = JsonObject()
            args["document_id"] <- JsonValue.Create(999L)
            let! result = McpTools.deepExtract db (mkDeepDeps "{}") (args :> JsonNode)
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
                    """INSERT INTO documents (original_name, source_type, saved_path, category, sha256, extracted_text)
                       VALUES ('test.pdf', 'manual_drop', 'payslips/test.pdf', 'payslips', 'sha-deep-2', 'Text')"""
                    []
            let args = JsonObject()
            args["document_id"] <- JsonValue.Create(1L)
            let! result = McpTools.deepExtract db (mkDeepDeps "{}") (args :> JsonNode)
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
            let! result = McpTools.deepExtract db (mkDeepDeps "{}") emptyArgs
            Assert.Contains("document_id is required", result["error"].GetValue<string>())
        finally db.dispose ()
    }
