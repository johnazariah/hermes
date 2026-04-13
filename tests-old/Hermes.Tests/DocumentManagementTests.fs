module Hermes.Tests.DocumentManagementTests

open System
open Xunit
open Hermes.Core

let private insertDoc (db: Algebra.Database) (cat: string) (name: string) =
    task {
        let! _ = db.execNonQuery
                    "INSERT INTO documents (source_type, saved_path, category, sha256, original_name) VALUES ('manual_drop', @p, @c, @s, @n)"
                    ([ ("@p", Database.boxVal $"{cat}/{name}"); ("@c", Database.boxVal cat)
                       ("@s", Database.boxVal (Guid.NewGuid().ToString("N")))
                       ("@n", Database.boxVal name) ])
        let! id = db.execScalar "SELECT last_insert_rowid()" []
        return match id with null -> 0L | v -> v :?> int64
    }

// ─── reclassify ──────────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentManagement_Reclassify_ChangesCategoryInDb`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Put "invoices/test.pdf" "content"
        try
            let! docId = insertDoc db "invoices" "test.pdf"
            let! result = DocumentManagement.reclassify db m.Fs "" docId "receipts"
            Assert.True(Result.isOk result)
            let! catResult = db.execScalar "SELECT category FROM documents WHERE id = @id" ([ ("@id", Database.boxVal docId) ])
            Assert.Equal("receipts", string catResult)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentManagement_Reclassify_NonexistentDoc_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            let! result = DocumentManagement.reclassify db m.Fs "" 999L "receipts"
            Assert.True(Result.isError result)
        finally db.dispose ()
    }

// ─── reextract ───────────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentManagement_Reextract_ClearsExtractedAt`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ = db.execNonQuery
                        "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text, extracted_at) VALUES ('manual_drop', 'a.pdf', 'invoices', 'sha1', 'text', datetime('now'))"
                        []
            let! result = DocumentManagement.reextract db 1L
            Assert.True(Result.isOk result)
            let! eAt = db.execScalar "SELECT extracted_at FROM documents WHERE id = 1" []
            Assert.True(match eAt with null | :? DBNull -> true | _ -> false)
        finally db.dispose ()
    }

// ─── getProcessingQueue ──────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentManagement_GetProcessingQueue_EmptyDb_AllZeros`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! q = DocumentManagement.getProcessingQueue db 5
            Assert.Equal(0, q.Unextracted.Count)
            Assert.Equal(0, q.Unembedded.Count)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``DocumentManagement_GetProcessingQueue_MixedDocs_CorrectCounts`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            // 1 unextracted
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('manual_drop', 'a.pdf', 'invoices', 'sha1')" []
            // 1 extracted but not embedded
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text, extracted_at) VALUES ('manual_drop', 'b.pdf', 'invoices', 'sha2', 'text', datetime('now'))" []
            // 1 fully processed
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text, extracted_at, embedded_at) VALUES ('manual_drop', 'c.pdf', 'invoices', 'sha3', 'text', datetime('now'), datetime('now'))" []
            let! q = DocumentManagement.getProcessingQueue db 5
            Assert.Equal(1, q.Unextracted.Count)
            Assert.Equal(1, q.Unembedded.Count)
        finally db.dispose ()
    }
