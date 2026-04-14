module Hermes.Tests.PromptLoaderTests

open System
open System.IO
open Xunit
open Hermes.Core

// ─── Helpers ─────────────────────────────────────────────────────────

let private validContent =
    "---SYSTEM---\nYou are a document classifier.\n---USER---\nClassify this: {{document_text}} with {{context}}"

let private validPrompt : PromptLoader.ParsedPrompt =
    { System = "You are a document classifier."
      UserTemplate = "Classify this: {{document_text}} with {{context}}" }

let private mkContent system user =
    $"---SYSTEM---\n{system}\n---USER---\n{user}"

// ─── PromptLoader.parse ──────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PromptLoader_Parse_ValidContent_ReturnsBothSections`` () =
    let result = PromptLoader.parse validContent

    match result with
    | Ok p ->
        Assert.Equal("You are a document classifier.", p.System)
        Assert.Equal("Classify this: {{document_text}} with {{context}}", p.UserTemplate)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PromptLoader_Parse_MissingSystemDelimiter_ReturnsError`` () =
    let content = "No system here\n---USER---\nSome user text"
    let result = PromptLoader.parse content

    match result with
    | Error msg -> Assert.Contains("---SYSTEM---", msg)
    | Ok _ -> failwith "Expected Error for missing SYSTEM delimiter"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PromptLoader_Parse_MissingUserDelimiter_ReturnsError`` () =
    let content = "---SYSTEM---\nSystem text but no user section"
    let result = PromptLoader.parse content

    match result with
    | Error msg -> Assert.Contains("---USER---", msg)
    | Ok _ -> failwith "Expected Error for missing USER delimiter"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PromptLoader_Parse_EmptySystemSection_ReturnsError`` () =
    let content = "---SYSTEM---\n   \n---USER---\nSome user text"
    let result = PromptLoader.parse content

    match result with
    | Error msg -> Assert.Contains("System section is empty", msg)
    | Ok _ -> failwith "Expected Error for empty system section"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PromptLoader_Parse_EmptyUserSection_ReturnsError`` () =
    let content = "---SYSTEM---\nSystem prompt\n---USER---\n   "
    let result = PromptLoader.parse content

    match result with
    | Error msg -> Assert.Contains("User section is empty", msg)
    | Ok _ -> failwith "Expected Error for empty user section"

// ─── PromptLoader.render ─────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PromptLoader_Render_SubstitutesTemplateMarkers`` () =
    let rendered = PromptLoader.render validPrompt "My document" "extra context"

    Assert.Contains("My document", rendered)
    Assert.Contains("extra context", rendered)
    Assert.DoesNotContain("{{document_text}}", rendered)
    Assert.DoesNotContain("{{context}}", rendered)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PromptLoader_Render_TruncatesLongText`` () =
    let longText = String.replicate 4000 "x"
    let rendered = PromptLoader.render validPrompt longText "ctx"

    Assert.Contains("[... truncated]", rendered)
    Assert.True(rendered.Length < longText.Length)

// ─── PromptLoader.loadFromFile ───────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PromptLoader_LoadFromFile_FileExists_ParsesContent`` () =
    task {
        let mem = TestHelpers.memFs ()
        let path = "C:/test/prompts/comprehension.md"
        mem.Put path validContent

        let! result = PromptLoader.loadFromFile mem.Fs path

        match result with
        | Ok p ->
            Assert.Equal("You are a document classifier.", p.System)
            Assert.Equal("Classify this: {{document_text}} with {{context}}", p.UserTemplate)
        | Error e -> failwith $"Expected Ok, got Error: {e}"
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PromptLoader_LoadFromFile_FileMissing_ReturnsError`` () =
    task {
        let mem = TestHelpers.memFs ()

        let! result = PromptLoader.loadFromFile mem.Fs "C:/nowhere/missing.md"

        match result with
        | Error msg -> Assert.Contains("not found", msg)
        | Ok _ -> failwith "Expected Error for missing file"
    }

// ─── PromptLoader.loadWithFallback ───────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PromptLoader_LoadWithFallback_ConfigDirExists_UsesConfigDir`` () =
    task {
        let mem = TestHelpers.memFs ()
        let configDir = "C:/config"
        let assemblyDir = "C:/assembly"
        let configPath = Path.Combine(configDir, "prompts", "comprehension.md") |> mem.Norm
        mem.Put configPath validContent

        let! result = PromptLoader.loadWithFallback mem.Fs configDir assemblyDir

        match result with
        | Ok p -> Assert.Equal("You are a document classifier.", p.System)
        | Error e -> failwith $"Expected Ok from config dir, got Error: {e}"
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PromptLoader_LoadWithFallback_OnlyAssemblyDir_UsesFallback`` () =
    task {
        let mem = TestHelpers.memFs ()
        let configDir = "C:/config"
        let assemblyDir = "C:/assembly"
        let assemblyPath = Path.Combine(assemblyDir, "prompts", "comprehension.md") |> mem.Norm
        let assemblyContent = mkContent "Assembly system" "Assembly user {{document_text}} {{context}}"
        mem.Put assemblyPath assemblyContent

        let! result = PromptLoader.loadWithFallback mem.Fs configDir assemblyDir

        match result with
        | Ok p -> Assert.Equal("Assembly system", p.System)
        | Error e -> failwith $"Expected Ok from assembly dir, got Error: {e}"
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PromptLoader_LoadWithFallback_NeitherExists_ReturnsError`` () =
    task {
        let mem = TestHelpers.memFs ()

        let! result = PromptLoader.loadWithFallback mem.Fs "C:/config" "C:/assembly"

        match result with
        | Error msg -> Assert.Contains("not found", msg)
        | Ok _ -> failwith "Expected Error when neither directory has prompt file"
    }

// ─── ComprehensionSchema.normaliseCategory ───────────────────────────

[<Theory>]
[<InlineData("payslip", "payslips")>]
[<InlineData("agent-statement", "property")>]
[<InlineData("bank-statement", "bank-statements")>]
[<Trait("Category", "Unit")>]
let ``ComprehensionSchema_NormaliseCategory_KnownType_MapsCorrectly`` (input: string, expected: string) =
    Assert.Equal(expected, ComprehensionSchema.normaliseCategory input)

[<Theory>]
[<InlineData("invoice", "invoices")>]
[<InlineData("receipt", "receipts")>]
[<InlineData("rental-statement", "property")>]
[<Trait("Category", "Unit")>]
let ``ComprehensionSchema_NormaliseCategory_Alias_MapsCorrectly`` (input: string, expected: string) =
    Assert.Equal(expected, ComprehensionSchema.normaliseCategory input)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ComprehensionSchema_NormaliseCategory_Unknown_ReturnsUnclassified`` () =
    Assert.Equal("unclassified", ComprehensionSchema.normaliseCategory "banana-document")

[<Theory>]
[<InlineData("PAYSLIP", "payslips")>]
[<InlineData("Bank-Statement", "bank-statements")>]
[<InlineData("INVOICE", "invoices")>]
[<Trait("Category", "Unit")>]
let ``ComprehensionSchema_NormaliseCategory_CaseInsensitive`` (input: string, expected: string) =
    Assert.Equal(expected, ComprehensionSchema.normaliseCategory input)

// ─── ComprehensionSchema.normaliseResponse ───────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ComprehensionSchema_NormaliseResponse_ValidJson_ReturnsNormalised`` () =
    let json = """{"document_type":"payslip","confidence":0.95,"summary":"Monthly payslip"}"""
    let result = ComprehensionSchema.normaliseResponse json

    match result with
    | Ok r ->
        Assert.Equal("payslip", r.DocumentType)
        Assert.Equal("payslips", r.CanonicalCategory)
        Assert.Equal(0.95, r.Confidence)
        Assert.Equal("Monthly payslip", r.Summary)
        Assert.Equal(json, r.RawJson)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ComprehensionSchema_NormaliseResponse_WithCodeFences_StripsThem`` () =
    let inner = """{"document_type":"bank-statement","confidence":0.8,"summary":"Statement"}"""
    let fenced = $"```json\n{inner}\n```"
    let result = ComprehensionSchema.normaliseResponse fenced

    match result with
    | Ok r ->
        Assert.Equal("bank-statement", r.DocumentType)
        Assert.Equal("bank-statements", r.CanonicalCategory)
        Assert.Equal(inner, r.RawJson)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ComprehensionSchema_NormaliseResponse_InvalidJson_ReturnsError`` () =
    let result = ComprehensionSchema.normaliseResponse "not json at all"

    match result with
    | Error msg -> Assert.Contains("JSON parse failed", msg)
    | Ok _ -> failwith "Expected Error for invalid JSON"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ComprehensionSchema_NormaliseResponse_ConfidenceOutOfRange_Clamped`` () =
    let json = """{"document_type":"payslip","confidence":1.5,"summary":"Over-confident"}"""
    let result = ComprehensionSchema.normaliseResponse json

    match result with
    | Ok r -> Assert.Equal(1.0, r.Confidence)
    | Error e -> failwith $"Expected Ok with clamped confidence, got Error: {e}"
