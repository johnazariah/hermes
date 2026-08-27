module Hermes.Tests.ReclassificationApiTests

open Xunit
open Hermes.Core

let private insertDocument
    (db: Algebra.Database)
    (path: string) =
    task {
        let! _ =
            db.execNonQuery
                """INSERT INTO documents
                   (source_type, saved_path, category, sha256)
                   VALUES ('manual_drop', @path, 'unsorted', @path)"""
                [ "@path", Database.boxVal path ]
        ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``REST single returns explicit identity-preserving outcome`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        fs.Put "/archive/stable/source.pdf" "source"

        try
            do! insertDocument db "stable/source.pdf"
            let! response =
                Hermes.Core.ReclassificationApi.single
                    db fs.Fs "/archive" 1L "receipts"

            Assert.Equal("reclassified", response.status)
            Assert.Equal(Some "stable/source.pdf", response.savedPath)
            Assert.Equal(Some "stable/source.pdf", response.sha256)
            Assert.Equal(None, response.error)
            Assert.True(fs.Fs.fileExists "/archive/stable/source.pdf")
            Assert.False(fs.Fs.fileExists "/archive/receipts/source.pdf")
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``REST single reports an idempotent reclassification as unchanged`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        fs.Put "/archive/stable/source.pdf" "source"

        try
            do! insertDocument db "stable/source.pdf"
            let! first =
                Hermes.Core.ReclassificationApi.single
                    db fs.Fs "/archive" 1L "receipts"
            let! second =
                Hermes.Core.ReclassificationApi.single
                    db fs.Fs "/archive" 1L "receipts"
            Assert.Equal("reclassified", first.status)
            Assert.Equal("unchanged", second.status)
            Assert.False(second.changed)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``REST single maps whitespace category validation explicitly`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()

        try
            let! response =
                Hermes.Core.ReclassificationApi.single
                    db fs.Fs "/archive" 1L " "
            Assert.Equal("failed", response.status)
            Assert.False(response.changed)
            Assert.Equal(None, response.category)
            Assert.Equal(Some "Category must not be empty", response.error)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``REST batch retains per-document partial failures`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        fs.Put "/archive/present/source.pdf" "source"

        try
            do! insertDocument db "present/source.pdf"
            do! insertDocument db "missing/source.pdf"
            let! response =
                Hermes.Core.ReclassificationApi.batch
                    db fs.Fs "/archive" [ 1L; 2L; 999L ] "receipts"

            Assert.Equal("reclassify", response.action)
            Assert.Equal(1, response.succeeded)
            Assert.Equal(2, response.failed)
            Assert.Equal(3, response.outcomes.Length)
            Assert.Contains(
                response.outcomes,
                fun outcome ->
                    outcome.documentId = 2L
                    && outcome.status = "failed"
                    && outcome.error.IsSome)
            Assert.Contains(
                response.outcomes,
                fun outcome ->
                    outcome.documentId = 999L
                    && outcome.status = "failed"
                    && outcome.error.IsSome)

            let! failedCategory =
                db.execScalar
                    "SELECT category FROM documents WHERE id = 2"
                    []
            Assert.Equal("unsorted", Assert.IsType<string>(failedCategory))
        finally
            db.dispose ()
    }
