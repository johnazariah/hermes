module Hermes.Tests.DocumentFeedTests

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
[<Trait("Category", "Unit")>]
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
[<Trait("Category", "Unit")>]
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
[<Trait("Category", "Unit")>]
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
[<Trait("Category", "Unit")>]
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
[<Trait("Category", "Unit")>]
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
[<Trait("Category", "Unit")>]
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
[<Trait("Category", "Unit")>]
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
[<Trait("Category", "Unit")>]
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
