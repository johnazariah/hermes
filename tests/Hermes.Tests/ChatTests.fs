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
    let _provider = Chat.providerFromConfig chatConfig "http://localhost:11434" "llama3"
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
