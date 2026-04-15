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
