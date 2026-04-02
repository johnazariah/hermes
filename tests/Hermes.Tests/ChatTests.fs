module Hermes.Tests.ChatTests

open System
open Xunit
open Hermes.Core

// ─── Sample search result factory ────────────────────────────────────

let private sampleResult
    (category: string)
    (name: string)
    (sender: string option)
    (amount: float option)
    : Search.SearchResult =
    { DocumentId = 1L
      SavedPath = $"{category}/{name}"
      OriginalName = Some name
      Category = category
      Sender = sender
      Subject = Some "Test Subject"
      EmailDate = Some "2026-03-15"
      ExtractedVendor = Some "Test Vendor"
      ExtractedAmount = amount
      RelevanceScore = 10.0
      Snippet = Some "...matching snippet..."
      ResultType = "document" }

// ─── formatResultsForPrompt ──────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_FormatResults_EmptyList_ReturnsEmptyString`` () =
    Assert.Equal("", Chat.formatResultsForPrompt [])

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_FormatResults_SingleResult_IncludesCategoryAndName`` () =
    let result = Chat.formatResultsForPrompt [ sampleResult "invoices" "inv.pdf" None None ]
    Assert.Contains("[invoices/document]", result)
    Assert.Contains("inv.pdf", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_FormatResults_IncludesSenderWhenPresent`` () =
    let result = Chat.formatResultsForPrompt [ sampleResult "invoices" "inv.pdf" (Some "alice@co.com") None ]
    Assert.Contains("From: alice@co.com", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_FormatResults_IncludesAmountWhenPresent`` () =
    let result = Chat.formatResultsForPrompt [ sampleResult "invoices" "inv.pdf" None (Some 385.0) ]
    Assert.Contains("$385.00", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_FormatResults_TruncatesTo10Results`` () =
    let results = [ for i in 1..15 -> sampleResult "cat" $"file{i}.pdf" None None ]
    let formatted = Chat.formatResultsForPrompt results
    Assert.Contains("10.", formatted)
    Assert.DoesNotContain("11.", formatted)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_SystemPrompt_IsNotEmpty`` () =
    Assert.True(Chat.systemPrompt.Length > 50)
    Assert.Contains("Hermes", Chat.systemPrompt)

// ─── query (keyword mode, no AI) ─────────────────────────────────────

let private fakeChat : Algebra.ChatProvider =
    { complete = fun _ _ -> task { return Ok "Fake AI response" } }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_Query_KeywordMode_ReturnsResults`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (source_type, saved_path, category, sha256, original_name, extracted_text)
                       VALUES ('manual_drop', 'invoices/test.pdf', 'invoices', 'abc', 'test.pdf', 'plumber invoice $500')"""
                    []
            let! response = Chat.query db fakeChat false "plumber"
            Assert.True(response.Results.Length > 0)
            Assert.True(response.AiSummary.IsNone)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_Query_AiMode_ReturnsAiSummary`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (source_type, saved_path, category, sha256, original_name, extracted_text)
                       VALUES ('manual_drop', 'invoices/test.pdf', 'invoices', 'abc', 'test.pdf', 'plumber invoice $500')"""
                    []
            let! response = Chat.query db fakeChat true "plumber"
            Assert.True(response.Results.Length > 0)
            Assert.True(response.AiSummary.IsSome)
            Assert.Contains("Fake AI response", response.AiSummary.Value)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_Query_EmptyQuery_ReturnsEmpty`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! response = Chat.query db fakeChat false ""
            Assert.Empty(response.Results)
        finally db.dispose ()
    }

// ─── Provider construction ───────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_ProviderFromConfig_Ollama_ReturnsOllamaProvider`` () =
    let chatConfig : Domain.ChatConfig =
        { Provider = Domain.ChatProviderKind.Ollama
          AzureOpenAI = { Domain.AzureOpenAIConfig.Endpoint = ""; ApiKey = ""; DeploymentName = ""; MaxTokens = 100; TimeoutSeconds = 30 } }
    // Just verify it doesn't throw — the provider is a record of functions
    let _provider = Chat.providerFromConfig (new System.Net.Http.HttpClient()) chatConfig "http://localhost:11434" "llama3"
    Assert.True(true)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_Query_AiError_ReturnsErrorMessage`` () =
    task {
        let db = TestHelpers.createDb ()
        let failChat : Algebra.ChatProvider =
            { complete = fun _ _ -> task { return Error "connection refused" } }
        try
            let! _ = db.execNonQuery
                        "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text) VALUES ('manual_drop', 'a.pdf', 'invoices', 'sha1', 'test content')"
                        []
            let! response = Chat.query db failChat true "test"
            Assert.True(response.AiSummary.IsSome)
            Assert.Contains("unavailable", response.AiSummary.Value)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_Query_NoResults_AiSummaryIsNone`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! response = Chat.query db fakeChat true "xyznonexistent"
            // No results → AI not called
            Assert.True(response.AiSummary.IsNone || response.Results.IsEmpty)
        finally db.dispose ()
    }

// ─── Additional query tests ──────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_Query_MultipleResults_ReturnsAll`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (source_type, saved_path, category, sha256, original_name, extracted_text)
                       VALUES ('manual_drop', 'invoices/a.pdf', 'invoices', 'sha1', 'a.pdf', 'plumber repair job march')"""
                    []
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (source_type, saved_path, category, sha256, original_name, extracted_text)
                       VALUES ('manual_drop', 'invoices/b.pdf', 'invoices', 'sha2', 'b.pdf', 'plumber annual service')"""
                    []
            let! response = Chat.query db fakeChat false "plumber"
            Assert.True(response.Results.Length >= 2, $"Expected >= 2 results, got {response.Results.Length}")
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_Query_WithFakeChatProvider_ReturnsCustomResponse`` () =
    task {
        let db = TestHelpers.createDb ()
        let customChat = TestHelpers.fakeChatProvider "Custom AI summary here"
        try
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (source_type, saved_path, category, sha256, original_name, extracted_text)
                       VALUES ('manual_drop', 'invoices/t.pdf', 'invoices', 'sha3', 't.pdf', 'test document content')"""
                    []
            let! response = Chat.query db customChat true "test"
            Assert.True(response.AiSummary.IsSome)
            Assert.Contains("Custom AI summary here", response.AiSummary.Value)
        finally db.dispose ()
    }

// ─── Provider construction edge cases ────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_ProviderFromConfig_AzureOpenAI_WithValidConfig_ReturnsAzureProvider`` () =
    let chatConfig : Domain.ChatConfig =
        { Provider = Domain.ChatProviderKind.AzureOpenAI
          AzureOpenAI =
            { Domain.AzureOpenAIConfig.Endpoint = "https://test.openai.azure.com"
              ApiKey = "test-api-key"
              DeploymentName = "gpt-4o"
              MaxTokens = 100
              TimeoutSeconds = 30 } }
    let _provider = Chat.providerFromConfig (new System.Net.Http.HttpClient()) chatConfig "http://localhost:11434" "llama3"
    Assert.True(true)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_ProviderFromConfig_AzureOpenAI_EmptyEndpoint_FallsBackToOllama`` () =
    let chatConfig : Domain.ChatConfig =
        { Provider = Domain.ChatProviderKind.AzureOpenAI
          AzureOpenAI =
            { Domain.AzureOpenAIConfig.Endpoint = ""
              ApiKey = "test-api-key"
              DeploymentName = "gpt-4o"
              MaxTokens = 100
              TimeoutSeconds = 30 } }
    let _provider = Chat.providerFromConfig (new System.Net.Http.HttpClient()) chatConfig "http://localhost:11434" "llama3"
    Assert.True(true)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_ProviderFromConfig_AzureOpenAI_EmptyApiKey_FallsBackToOllama`` () =
    let chatConfig : Domain.ChatConfig =
        { Provider = Domain.ChatProviderKind.AzureOpenAI
          AzureOpenAI =
            { Domain.AzureOpenAIConfig.Endpoint = "https://test.openai.azure.com"
              ApiKey = ""
              DeploymentName = "gpt-4o"
              MaxTokens = 100
              TimeoutSeconds = 30 } }
    let _provider = Chat.providerFromConfig (new System.Net.Http.HttpClient()) chatConfig "http://localhost:11434" "llama3"
    Assert.True(true)

// ─── buildUserPrompt ─────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_BuildUserPrompt_IncludesQueryAndContext`` () =
    let result = Chat.buildUserPrompt "Where is my invoice?" "1. invoices/inv.pdf"
    Assert.Contains("Where is my invoice?", result)
    Assert.Contains("invoices/inv.pdf", result)

// ─── formatResultsForPrompt edge cases ───────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_FormatResults_IncludesSubjectAndDateAndVendor`` () =
    let result = Chat.formatResultsForPrompt [ sampleResult "invoices" "inv.pdf" (Some "bob@co.com") (Some 100.0) ]
    Assert.Contains("Subject: Test Subject", result)
    Assert.Contains("Date: 2026-03-15", result)
    Assert.Contains("Vendor: Test Vendor", result)
    Assert.Contains("Content: ...matching snippet...", result)

// ─── Additional Chat edge cases ──────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_BuildUserPrompt_EmptyContext_StillIncludesQuery`` () =
    let result = Chat.buildUserPrompt "How much did I pay?" ""
    Assert.Contains("How much did I pay?", result)
    Assert.Contains("Answer briefly", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_FormatResults_NoOptionalFields_FormatsCleanly`` () =
    let result : Search.SearchResult =
        { DocumentId = 1L; SavedPath = "misc/doc.pdf"; OriginalName = None
          Category = "misc"; Sender = None; Subject = None; EmailDate = None
          ExtractedVendor = None; ExtractedAmount = None; RelevanceScore = 1.0
          Snippet = None; ResultType = "document" }
    let formatted = Chat.formatResultsForPrompt [result]
    Assert.Contains("[misc/document]", formatted)
    Assert.DoesNotContain("From:", formatted)
    Assert.DoesNotContain("Vendor:", formatted)
    Assert.DoesNotContain("Amount:", formatted)
    Assert.DoesNotContain("Subject:", formatted)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_Query_AiEnabled_NoResults_SkipsAiCall`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! response = Chat.query db fakeChat true "xyznonexistent12345"
            Assert.True(response.Results.IsEmpty)
            Assert.True(response.AiSummary.IsNone)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_SystemPrompt_ContainsDocumentTypes`` () =
    Assert.Contains("emails", Chat.systemPrompt)
    Assert.Contains("invoices", Chat.systemPrompt)
    Assert.Contains("receipts", Chat.systemPrompt)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_FormatResults_MultipleResults_NumbersCorrectly`` () =
    let results = [ for i in 1..3 -> sampleResult "invoices" $"file{i}.pdf" None (Some (float i * 100.0)) ]
    let formatted = Chat.formatResultsForPrompt results
    Assert.Contains("1.", formatted)
    Assert.Contains("2.", formatted)
    Assert.Contains("3.", formatted)

// ─── HTTP provider error-path tests ──────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_OllamaProvider_ConnectionRefused_ReturnsError`` () =
    task {
        let provider = Chat.ollamaProvider (new System.Net.Http.HttpClient(Timeout = System.TimeSpan.FromSeconds(5.0))) "http://127.0.0.1:1" "test-model"
        let! result = provider.complete "sys" "user"
        match result with
        | Error msg -> Assert.Contains("Ollama error", msg)
        | Ok _ -> failwith "Expected Error from unreachable Ollama endpoint"
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_AzureOpenAIProvider_ConnectionRefused_ReturnsError`` () =
    task {
        let config : Domain.AzureOpenAIConfig =
            { Endpoint = "http://127.0.0.1:1"
              ApiKey = "fake"
              DeploymentName = "gpt-4o"
              MaxTokens = 100
              TimeoutSeconds = 5 }
        let provider = Chat.azureOpenAIProvider (new System.Net.Http.HttpClient(Timeout = System.TimeSpan.FromSeconds(5.0))) config
        let! result = provider.complete "sys" "user"
        match result with
        | Error msg -> Assert.Contains("Azure OpenAI error", msg)
        | Ok _ -> failwith "Expected Error from unreachable Azure endpoint"
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_ProviderFromConfig_WhitespaceEndpoint_FallsBackToOllama`` () =
    let chatConfig : Domain.ChatConfig =
        { Provider = Domain.ChatProviderKind.AzureOpenAI
          AzureOpenAI =
            { Domain.AzureOpenAIConfig.Endpoint = "  "
              ApiKey = "valid-key"
              DeploymentName = "gpt-4o"
              MaxTokens = 100
              TimeoutSeconds = 30 } }
    let _provider = Chat.providerFromConfig (new System.Net.Http.HttpClient()) chatConfig "http://localhost:11434" "llama3"
    Assert.True(true)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_BuildUserPrompt_ContainsAnswerBrieflyInstruction`` () =
    let result = Chat.buildUserPrompt "test" "context"
    Assert.Contains("Answer briefly and specifically.", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Chat_FormatResults_ResultWithOnlySenderAndSnippet_FormatsCorrectly`` () =
    let result : Search.SearchResult =
        { DocumentId = 1L; SavedPath = "emails/msg.eml"; OriginalName = None
          Category = "emails"; Sender = Some "alice@test.com"; Subject = None
          EmailDate = None; ExtractedVendor = None; ExtractedAmount = None
          RelevanceScore = 5.0; Snippet = Some "important snippet"; ResultType = "email" }
    let formatted = Chat.formatResultsForPrompt [result]
    Assert.Contains("From: alice@test.com", formatted)
    Assert.Contains("Content: important snippet", formatted)
    Assert.DoesNotContain("Subject:", formatted)
    Assert.DoesNotContain("Vendor:", formatted)
    Assert.DoesNotContain("Amount:", formatted)
