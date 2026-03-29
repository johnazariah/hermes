module Hermes.Tests.SearchTests

open System
open System.Threading.Tasks
open Xunit
open Hermes.Core

// ─── Helpers ─────────────────────────────────────────────────────────

let insertTestDocument
    (db: Algebra.Database)
    (sender: string)
    (subject: string)
    (category: string)
    (originalName: string)
    (extractedText: string)
    (extractedVendor: string)
    : Task<unit>
    =
    task {
        let! _ =
            db.execNonQuery
                """INSERT INTO documents
                   (source_type, saved_path, category, sha256, sender, subject,
                    original_name, extracted_text, extracted_vendor)
                   VALUES
                   (@st, @sp, @cat, @sha, @sender, @subject, @name, @text, @vendor)"""
                ([ ("@st", Database.boxVal "manual_drop")
                   ("@sp", Database.boxVal (category + "/" + originalName))
                   ("@cat", Database.boxVal category)
                   ("@sha", Database.boxVal (Guid.NewGuid().ToString("N")))
                   ("@sender", Database.boxVal sender)
                   ("@subject", Database.boxVal subject)
                   ("@name", Database.boxVal originalName)
                   ("@text", Database.boxVal extractedText)
                   ("@vendor", Database.boxVal extractedVendor) ] : (string * obj) list)

        return ()
    }

// ─── Query sanitisation tests ────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Search_SanitiseQuery_RemovesFtsOperators`` () =
    let result = Search.sanitiseQuery "hello + world"
    Assert.DoesNotContain("+", result)
    Assert.Contains("hello", result)
    Assert.Contains("world", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Search_SanitiseQuery_QuotesTokens`` () =
    let result = Search.sanitiseQuery "invoice plumbing"
    Assert.Equal("\"invoice\" \"plumbing\"", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Search_SanitiseQuery_EmptyInput_ReturnsEmpty`` () =
    let result = Search.sanitiseQuery ""
    Assert.Equal("", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Search_SanitiseQuery_WhitespaceOnly_ReturnsEmpty`` () =
    let result = Search.sanitiseQuery "   "
    Assert.Equal("", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Search_SanitiseQuery_SpecialChars_Stripped`` () =
    let result = Search.sanitiseQuery "test* (foo) \"bar\" ^baz"
    Assert.DoesNotContain("*", result)
    Assert.DoesNotContain("(", result)
    Assert.Contains("test", result)
    Assert.Contains("foo", result)
    Assert.Contains("bar", result)
    Assert.Contains("baz", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Search_SanitiseQuery_SingleToken_Quoted`` () =
    let result = Search.sanitiseQuery "invoice"
    Assert.Equal("\"invoice\"", result)

// ─── Filter / query building tests ───────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Search_BuildQuery_BasicQuery_IncludesMatchAndBm25`` () =
    let filter = Search.defaultFilter "test"
    let sql, _params = Search.buildQuery filter
    Assert.Contains("MATCH", sql)
    Assert.Contains("bm25", sql)
    Assert.Contains("snippet", sql)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Search_BuildQuery_WithCategory_AddsFilter`` () =
    let filter = { Search.defaultFilter "test" with Category = Some "invoices" }
    let sql, pars = Search.buildQuery filter
    Assert.Contains("d.category = @category", sql)
    Assert.True(pars |> List.exists (fun (n, _) -> n = "@category"))

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Search_BuildQuery_WithSender_AddsLikeFilter`` () =
    let filter = { Search.defaultFilter "test" with Sender = Some "bob" }
    let sql, pars = Search.buildQuery filter
    Assert.Contains("d.sender LIKE @sender", sql)
    Assert.True(pars |> List.exists (fun (n, _) -> n = "@sender"))

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Search_BuildQuery_WithDateRange_AddsFilters`` () =
    let filter =
        { Search.defaultFilter "test" with
            DateFrom = Some "2024-01-01"
            DateTo = Some "2024-12-31" }
    let sql, pars = Search.buildQuery filter
    Assert.Contains("d.email_date >= @dateFrom", sql)
    Assert.Contains("d.email_date <= @dateTo", sql)
    Assert.True(pars |> List.exists (fun (n, _) -> n = "@dateFrom"))
    Assert.True(pars |> List.exists (fun (n, _) -> n = "@dateTo"))

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Search_BuildQuery_WithAccount_AddsFilter`` () =
    let filter = { Search.defaultFilter "test" with Account = Some "work@example.com" }
    let sql, pars = Search.buildQuery filter
    Assert.Contains("d.account = @account", sql)
    Assert.True(pars |> List.exists (fun (n, _) -> n = "@account"))

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Search_BuildQuery_EmptyQuery_ReturnsNoResults`` () =
    let filter = Search.defaultFilter ""
    let sql, _ = Search.buildQuery filter
    Assert.Contains("WHERE 0", sql)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Search_BuildQuery_LimitIsParameterised`` () =
    let filter = { Search.defaultFilter "test" with Limit = 5 }
    let sql, pars = Search.buildQuery filter
    Assert.Contains("LIMIT @limit", sql)
    Assert.True(pars |> List.exists (fun (n, _) -> n = "@limit"))

// ─── Result mapping tests ────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Search_MapRow_MapsAllFields`` () =
    let row : Map<string, obj> =
        [ "id", Database.boxVal 42L
          "saved_path", Database.boxVal "invoices/test.pdf"
          "original_name", Database.boxVal "test.pdf"
          "category", Database.boxVal "invoices"
          "sender", Database.boxVal "bob@example.com"
          "subject", Database.boxVal "Invoice #42"
          "email_date", Database.boxVal "2024-01-15"
          "extracted_vendor", Database.boxVal "Acme Corp"
          "extracted_amount", Database.boxVal 99.95
          "rank", Database.boxVal -1.5
          "snippet", Database.boxVal "...invoice text..." ]
        |> Map.ofList

    let result = Search.mapRow row
    Assert.Equal(42L, result.DocumentId)
    Assert.Equal("invoices/test.pdf", result.SavedPath)
    Assert.Equal(Some "test.pdf", result.OriginalName)
    Assert.Equal("invoices", result.Category)
    Assert.Equal(Some "bob@example.com", result.Sender)
    Assert.Equal(Some "Invoice #42", result.Subject)
    Assert.Equal(Some "2024-01-15", result.EmailDate)
    Assert.Equal(Some "Acme Corp", result.ExtractedVendor)
    Assert.Equal(Some 99.95, result.ExtractedAmount)
    Assert.Equal(Some "...invoice text...", result.Snippet)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Search_MapRow_HandlesMissingFields`` () =
    let row : Map<string, obj> =
        [ "id", Database.boxVal 1L
          "saved_path", Database.boxVal "test.pdf"
          "category", Database.boxVal "unsorted"
          "rank", Database.boxVal 0.0 ]
        |> Map.ofList

    let result = Search.mapRow row
    Assert.Equal(1L, result.DocumentId)
    Assert.Equal("test.pdf", result.SavedPath)
    Assert.Equal("unsorted", result.Category)
    Assert.Equal(None, result.OriginalName)
    Assert.Equal(None, result.Sender)
    Assert.Equal(None, result.Subject)
    Assert.Equal(None, result.EmailDate)
    Assert.Equal(None, result.ExtractedVendor)
    Assert.Equal(None, result.ExtractedAmount)
    Assert.Equal(None, result.Snippet)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Search_MapRow_HandlesDbNullValues`` () =
    let row : Map<string, obj> =
        [ "id", Database.boxVal 1L
          "saved_path", Database.boxVal "test.pdf"
          "category", Database.boxVal "unsorted"
          "sender", Database.boxVal DBNull.Value
          "subject", Database.boxVal DBNull.Value
          "rank", Database.boxVal 0.0 ]
        |> Map.ofList

    let result = Search.mapRow row
    Assert.Equal(None, result.Sender)
    Assert.Equal(None, result.Subject)

// ─── Integration: search with real SQLite ────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Search_Execute_FindsMatchingDocument`` () =
    task {
        let db = TestHelpers.createRawDb ()

        try
            let! _ = db.initSchema ()
            do! insertTestDocument db "bob@example.com" "Invoice #42" "invoices" "inv42.pdf" "Total amount due five hundred dollars" "Acme Corp"

            let filter = Search.defaultFilter "invoice"
            let! results = Search.execute db filter

            Assert.NotEmpty(results)
            Assert.Equal("invoices", results.[0].Category)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Search_Execute_FilterByCategory_ExcludesOthers`` () =
    task {
        let db = TestHelpers.createRawDb ()

        try
            let! _ = db.initSchema ()
            do! insertTestDocument db "bob@example.com" "Invoice #42" "invoices" "inv42.pdf" "Payment invoice document" "Acme"
            do! insertTestDocument db "sue@example.com" "Receipt" "receipts" "receipt.pdf" "Payment receipt with invoice mention" "Shop"

            let filter = { Search.defaultFilter "invoice" with Category = Some "invoices" }
            let! results = Search.execute db filter

            Assert.NotEmpty(results)
            Assert.All(results, fun r -> Assert.Equal("invoices", r.Category))
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Search_Execute_NoMatch_ReturnsEmpty`` () =
    task {
        let db = TestHelpers.createRawDb ()

        try
            let! _ = db.initSchema ()
            do! insertTestDocument db "bob@example.com" "Invoice" "invoices" "test.pdf" "Some text content" "Vendor"

            let filter = Search.defaultFilter "xyznonexistent"
            let! results = Search.execute db filter

            Assert.Empty(results)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Search_Execute_ReturnsSnippet`` () =
    task {
        let db = TestHelpers.createRawDb ()

        try
            let! _ = db.initSchema ()
            do! insertTestDocument db "bob@example.com" "Invoice" "invoices" "test.pdf" "This is a long document with important invoice details" "Acme"

            let filter = Search.defaultFilter "invoice"
            let! results = Search.execute db filter

            Assert.NotEmpty(results)
            // Snippet should contain highlight markers
            let snippet = results.[0].Snippet
            Assert.True(snippet.IsSome, "Expected snippet to be present")
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Search_Execute_EmptyQuery_ReturnsEmpty`` () =
    task {
        let db = TestHelpers.createRawDb ()

        try
            let! _ = db.initSchema ()
            do! insertTestDocument db "bob@example.com" "Invoice" "invoices" "test.pdf" "Some text" "Vendor"

            let filter = Search.defaultFilter ""
            let! results = Search.execute db filter

            Assert.Empty(results)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Search_Execute_MultipleResults_RankedByRelevance`` () =
    task {
        let db = TestHelpers.createRawDb ()

        try
            let! _ = db.initSchema ()
            do! insertTestDocument db "a@test.com" "Water Bill" "utilities" "water.pdf" "Water usage charges" "Water Co"
            do! insertTestDocument db "b@test.com" "Electric Bill" "utilities" "elec.pdf" "Electricity bill charges" "Power Co"
            do! insertTestDocument db "c@test.com" "Gas Bill" "utilities" "gas.pdf" "Gas bill utility charges" "Gas Co"

            let filter = { Search.defaultFilter "bill" with Limit = 10 }
            let! results = Search.execute db filter

            Assert.True(results.Length >= 2, $"Expected at least 2 results, got {results.Length}")
        finally
            db.dispose ()
    }
