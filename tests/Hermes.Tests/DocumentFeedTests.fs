module Hermes.Tests.DocumentFeedTests

#nowarn "3261"
#nowarn "3264"

open System.Threading.Tasks
open Xunit
open Hermes.Core

// ─── Helpers ─────────────────────────────────────────────────────────

let private insertDoc (db: Algebra.Database) category name =
    task {
        let! _ =
            db.execNonQuery
                """INSERT INTO documents (source_type, saved_path, category, sha256, original_name)
                   VALUES ('manual_drop', @path, @cat, @sha, @name)"""
                [ ("@path", Database.boxVal $"{category}/{name}")
                  ("@cat", Database.boxVal category)
                  ("@sha", Database.boxVal (System.Guid.NewGuid().ToString("N")))
                  ("@name", Database.boxVal name) ]
        ()
    }

let private insertDocWithText (db: Algebra.Database) category name text =
    task {
        let! _ =
            db.execNonQuery
                """INSERT INTO documents (source_type, saved_path, category, sha256, original_name, extracted_text, extracted_at)
                   VALUES ('manual_drop', @path, @cat, @sha, @name, @text, datetime('now'))"""
                [ ("@path", Database.boxVal $"{category}/{name}")
                  ("@cat", Database.boxVal category)
                  ("@sha", Database.boxVal (System.Guid.NewGuid().ToString("N")))
                  ("@name", Database.boxVal name)
                  ("@text", Database.boxVal text) ]
        ()
    }

// ─── listDocuments tests ─────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentFeed_ListDocuments_SinceId0_ReturnsAll`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! insertDoc db "invoices" "inv1.pdf"
            do! insertDoc db "invoices" "inv2.pdf"
            do! insertDoc db "tax" "tax1.pdf"
            let! docs = DocumentFeed.listDocuments db 0L None 100
            Assert.Equal(3, docs.Length)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentFeed_ListDocuments_SinceId_ReturnsOnlyNewer`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! insertDoc db "invoices" "inv1.pdf"
            do! insertDoc db "invoices" "inv2.pdf"
            do! insertDoc db "tax" "tax1.pdf"
            let! allDocs = DocumentFeed.listDocuments db 0L None 100
            let firstId = allDocs.[0].Id
            let! newer = DocumentFeed.listDocuments db firstId None 100
            Assert.Equal(2, newer.Length)
            Assert.True(newer |> List.forall (fun d -> d.Id > firstId))
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentFeed_ListDocuments_FilterByCategory`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! insertDoc db "invoices" "inv1.pdf"
            do! insertDoc db "invoices" "inv2.pdf"
            do! insertDoc db "tax" "tax1.pdf"
            let! invoices = DocumentFeed.listDocuments db 0L (Some "invoices") 100
            Assert.Equal(2, invoices.Length)
            Assert.True(invoices |> List.forall (fun d -> d.Category = "invoices"))
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentFeed_ListDocuments_Limit_RespectsLimit`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            for i in 1..5 do
                do! insertDoc db "invoices" $"inv{i}.pdf"
            let! docs = DocumentFeed.listDocuments db 0L None 2
            Assert.Equal(2, docs.Length)
        finally
            db.dispose ()
    }

// ─── getFeedStats tests ──────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentFeed_GetFeedStats_ReturnsCorrectCounts`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! insertDoc db "invoices" "inv1.pdf"
            do! insertDoc db "invoices" "inv2.pdf"
            do! insertDoc db "tax" "tax1.pdf"
            let! stats = DocumentFeed.getFeedStats db
            Assert.Equal(3, stats.TotalDocuments)
            Assert.True(stats.MaxDocumentId > 0L)
            Assert.Equal(2, stats.ByCategory.["invoices"])
            Assert.Equal(1, stats.ByCategory.["tax"])
        finally
            db.dispose ()
    }

// ─── getDocumentContent tests ────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentFeed_GetContent_Markdown_ReturnsExtractedText`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            do! insertDocWithText db "invoices" "inv.pdf" "# Invoice\n\nAmount: $500"
            let! docs = DocumentFeed.listDocuments db 0L None 1
            let docId = docs.[0].Id
            let! result = DocumentFeed.getDocumentContent db m.Fs "/archive" docId DocumentFeed.Markdown
            match result with
            | Ok content ->
                Assert.Contains("# Invoice", content)
                Assert.Contains("Amount: $500", content)
            | Error e -> failwith $"Expected Ok, got Error: {e}"
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentFeed_GetContent_Text_StripsFrontmatter`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            do! insertDocWithText db "invoices" "inv.pdf" "---\ntitle: Test\n---\nBody content here"
            let! docs = DocumentFeed.listDocuments db 0L None 1
            let docId = docs.[0].Id
            let! result = DocumentFeed.getDocumentContent db m.Fs "/archive" docId DocumentFeed.Text
            match result with
            | Ok content ->
                Assert.DoesNotContain("---", content)
                Assert.DoesNotContain("title:", content)
                Assert.Contains("Body content here", content)
            | Error e -> failwith $"Expected Ok, got Error: {e}"
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentFeed_GetContent_InvalidId_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            let! result = DocumentFeed.getDocumentContent db m.Fs "/archive" 99999L DocumentFeed.Markdown
            match result with
            | Error e -> Assert.Contains("not found", e)
            | Ok _ -> failwith "Expected Error for invalid ID"
        finally
            db.dispose ()
    }

// ─── getDocumentContent Raw ──────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentFeed_GetContent_Raw_ReturnsFileContent`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            do! insertDocWithText db "invoices" "inv.pdf" "extracted text"
            m.Put "/archive/invoices/inv.pdf" "Raw PDF file content here"
            let! docs = DocumentFeed.listDocuments db 0L None 1
            let docId = docs.[0].Id
            let! result = DocumentFeed.getDocumentContent db m.Fs "/archive" docId DocumentFeed.Raw
            match result with
            | Ok content -> Assert.Equal("Raw PDF file content here", content)
            | Error e -> failwith $"Expected Ok, got Error: {e}"
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentFeed_GetContent_Raw_MissingFile_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            do! insertDocWithText db "invoices" "missing.pdf" "text"
            let! docs = DocumentFeed.listDocuments db 0L None 1
            let docId = docs.[0].Id
            let! result = DocumentFeed.getDocumentContent db m.Fs "/archive" docId DocumentFeed.Raw
            match result with
            | Error e -> Assert.Contains("not found", e)
            | Ok _ -> failwith "Expected Error for missing file"
        finally
            db.dispose ()
    }

// ─── parseFormat tests ───────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DocumentFeed_ParseFormat_Text_ReturnsSome`` () =
    Assert.Equal(Some DocumentFeed.Text, DocumentFeed.parseFormat "text")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DocumentFeed_ParseFormat_Markdown_ReturnsSome`` () =
    Assert.Equal(Some DocumentFeed.Markdown, DocumentFeed.parseFormat "markdown")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DocumentFeed_ParseFormat_Raw_ReturnsSome`` () =
    Assert.Equal(Some DocumentFeed.Raw, DocumentFeed.parseFormat "raw")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DocumentFeed_ParseFormat_CaseInsensitive`` () =
    Assert.Equal(Some DocumentFeed.Text, DocumentFeed.parseFormat "TEXT")
    Assert.Equal(Some DocumentFeed.Markdown, DocumentFeed.parseFormat "MARKDOWN")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DocumentFeed_ParseFormat_Unknown_ReturnsNone`` () =
    Assert.True((DocumentFeed.parseFormat "pdf").IsNone)

// ─── feedDocToJson tests ─────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DocumentFeed_FeedDocToJson_IncludesAllFields`` () =
    let doc : DocumentFeed.FeedDocument =
        { Id = 42L; OriginalName = "test.pdf"; Category = "invoices"
          FilePath = "invoices/test.pdf"; Sender = Some "alice@co.com"
          Subject = Some "Invoice"; Account = Some "test-account"
          ExtractedDate = Some "2025-03-15"; ExtractedAmount = Some 500.0
          ExtractedVendor = Some "ACME"; IngestedAt = "2025-03-15T00:00:00Z"
          ExtractedAt = Some "2025-03-15T01:00:00Z" }
    let json = DocumentFeed.feedDocToJson doc
    Assert.Equal(42L, json["id"].GetValue<int64>())
    Assert.Equal("test.pdf", json["original_name"].GetValue<string>())
    Assert.Equal("invoices", json["category"].GetValue<string>())
    Assert.Equal("alice@co.com", json["sender"].GetValue<string>())

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DocumentFeed_FeedDocToJson_OmitsNoneFields`` () =
    let doc : DocumentFeed.FeedDocument =
        { Id = 1L; OriginalName = "test.pdf"; Category = "invoices"
          FilePath = "invoices/test.pdf"; Sender = None; Subject = None
          Account = None; ExtractedDate = None; ExtractedAmount = None
          ExtractedVendor = None; IngestedAt = ""; ExtractedAt = None }
    let json = DocumentFeed.feedDocToJson doc
    Assert.False(json.ContainsKey("sender"))
    Assert.False(json.ContainsKey("subject"))

// ─── feedStatsToJson tests ───────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``DocumentFeed_FeedStatsToJson_IncludesFields`` () =
    let stats : DocumentFeed.FeedStats =
        { TotalDocuments = 10; MaxDocumentId = 42L
          ByCategory = Map.ofList [ ("invoices", 5); ("tax", 3) ] }
    let json = DocumentFeed.feedStatsToJson stats
    Assert.Equal(10, json["total_documents"].GetValue<int>())
    Assert.Equal(42L, json["max_document_id"].GetValue<int64>())
    let cats = json["by_category"] :?> System.Text.Json.Nodes.JsonObject
    Assert.Equal(5, cats["invoices"].GetValue<int>())

// ─── getDocumentContent Markdown no text ─────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentFeed_GetContent_Markdown_NoExtractedText_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            do! insertDoc db "invoices" "no-text.pdf"
            let! docs = DocumentFeed.listDocuments db 0L None 1
            let docId = docs.[0].Id
            let! result = DocumentFeed.getDocumentContent db m.Fs "/archive" docId DocumentFeed.Markdown
            match result with
            | Error e -> Assert.Contains("No extracted text", e)
            | Ok _ -> failwith "Expected Error when no extracted text"
        finally
            db.dispose ()
    }

// ─── listDocuments empty DB ──────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentFeed_ListDocuments_EmptyDb_ReturnsEmpty`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! docs = DocumentFeed.listDocuments db 0L None 10
            Assert.Empty(docs)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentFeed_GetFeedStats_EmptyDb_ReturnsZeros`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! stats = DocumentFeed.getFeedStats db
            Assert.Equal(0, stats.TotalDocuments)
            Assert.Equal(0L, stats.MaxDocumentId)
            Assert.Empty(stats.ByCategory)
        finally
            db.dispose ()
    }
