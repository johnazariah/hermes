module Hermes.Tests.DocumentBrowserTests

open System
open Xunit
open Hermes.Core

let private insertDoc (db: Algebra.Database) (cat: string) (name: string) =
    task {
        let! _ = db.execNonQuery
                    "INSERT INTO documents (source_type, saved_path, category, sha256, original_name, sender) VALUES ('manual_drop', @p, @c, @s, @n, @sender)"
                    ([ ("@p", Database.boxVal $"{cat}/{name}"); ("@c", Database.boxVal cat)
                       ("@s", Database.boxVal (Guid.NewGuid().ToString("N")))
                       ("@n", Database.boxVal name); ("@sender", Database.boxVal "test@co.com") ])
        ()
    }

let private insertExtractedDoc (db: Algebra.Database) (cat: string) (name: string) =
    task {
        let! _ = db.execNonQuery
                    "INSERT INTO documents (source_type, saved_path, category, sha256, original_name, extracted_text, extracted_at) VALUES ('manual_drop', @p, @c, @s, @n, 'text', datetime('now'))"
                    ([ ("@p", Database.boxVal $"{cat}/{name}"); ("@c", Database.boxVal cat)
                       ("@s", Database.boxVal (Guid.NewGuid().ToString("N")))
                       ("@n", Database.boxVal name) ])
        ()
    }

// ─── listCategories ──────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentBrowser_ListCategories_EmptyDb_ReturnsEmpty`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! cats = DocumentBrowser.listCategories db
            Assert.Empty(cats)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentBrowser_ListCategories_MultipleCats_ReturnsAll`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! insertDoc db "invoices" "a.pdf"
            do! insertDoc db "invoices" "b.pdf"
            do! insertDoc db "receipts" "c.pdf"
            let! cats = DocumentBrowser.listCategories db
            Assert.Equal(2, cats.Length)
            // Sorted by count descending
            let inv = cats |> List.find (fun (c, _) -> c = "invoices")
            Assert.Equal(2, snd inv)
        finally db.dispose ()
    }

// ─── listDocuments ───────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentBrowser_ListDocuments_FiltersByCategory`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! insertDoc db "invoices" "a.pdf"
            do! insertDoc db "receipts" "b.pdf"
            let! docs = DocumentBrowser.listDocuments db "invoices" 0 10
            Assert.Equal(1, docs.Length)
            Assert.Equal("invoices", docs.[0].Category)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentBrowser_ListDocuments_RespectsLimit`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            for i in 1..5 do do! insertDoc db "invoices" $"doc{i}.pdf"
            let! docs = DocumentBrowser.listDocuments db "invoices" 0 2
            Assert.Equal(2, docs.Length)
        finally db.dispose ()
    }

// ─── getDocumentDetail ───────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentBrowser_GetDocumentDetail_ExistingDoc_ReturnsSome`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! insertExtractedDoc db "invoices" "test.pdf"
            let! detail = DocumentBrowser.getDocumentDetail db 1L
            Assert.True(detail.IsSome)
            Assert.Equal("invoices", detail.Value.Summary.Category)
            Assert.True(detail.Value.PipelineStatus.Extracted)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentBrowser_GetDocumentDetail_Missing_ReturnsNone`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! detail = DocumentBrowser.getDocumentDetail db 999L
            Assert.True(detail.IsNone)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentBrowser_GetDocumentDetail_PipelineStatus_Unextracted`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! insertDoc db "invoices" "raw.pdf"
            let! detail = DocumentBrowser.getDocumentDetail db 1L
            Assert.True(detail.IsSome)
            Assert.False(detail.Value.PipelineStatus.Extracted)
            Assert.False(detail.Value.PipelineStatus.Embedded)
        finally db.dispose ()
    }
