module Hermes.Tests.ReclassificationTests

open System
open Microsoft.Data.Sqlite
open Xunit
open Hermes.Core

let private insertDocument
    (db: Algebra.Database)
    (savedPath: string)
    (category: string)
    (sha256: string) =
    task {
        let! value =
            db.execScalar
                """INSERT INTO documents
                   (source_type, saved_path, category, sha256, original_name,
                    stage, extracted_at, embedded_at, chunk_count)
                   VALUES
                   ('manual_drop', @path, @category, @sha, 'source.pdf',
                    'embedded', 'extract-time', 'embed-time', 3)
                   RETURNING id"""
                [ "@path", Database.boxVal savedPath
                  "@category", Database.boxVal category
                  "@sha", Database.boxVal sha256 ]
        return value :?> int64
    }

let private expectSuccess result =
    result
    |> Result.defaultWith
        (Reclassification.describeError >> failwith)

let private insertUserTag
    (db: Algebra.Database)
    (documentId: int64)
    (tag: string) =
    task {
        let! _ =
            db.execNonQuery
                """INSERT INTO tags (document_id, tag, source)
                   VALUES (@id, @tag, 'user')"""
                [ "@id", Database.boxVal documentId
                  "@tag", Database.boxVal tag ]
        return ()
    }

let private loadTagOwnership
    (db: Algebra.Database)
    (documentId: int64) =
    task {
        let! rows =
            db.execReader
                """SELECT tag, created_by FROM tags
                   WHERE document_id = @id
                   ORDER BY tag, COALESCE(created_by, '')"""
                [ "@id", Database.boxVal documentId ]
        return
            rows
            |> List.map (fun row ->
                let reader = Prelude.RowReader(row)
                reader.String "tag" "", reader.OptString "created_by")
    }

let private reclassifyAndLoadTags db fs documentId category =
    task {
        let! result =
            DocumentManagement.reclassify
                db fs "/archive" documentId category
        result |> expectSuccess |> ignore
        return! loadTagOwnership db documentId
    }

let private assertTagOwnership
    (expected: (string * string option) list)
    (actual: (string * string option) list) =
    Assert.Equal<(string * string option) list>(expected, actual)

let private competingClassificationSql =
    """UPDATE documents
       SET category = 'tax',
           classification_tier = 'content',
           classification_confidence = 0.97
       WHERE id = @id"""

let private withCompetingClassification
    (db: Algebra.Database)
    (documentId: int64)
    : Algebra.Database =
    { db with
        execNonQuery =
            fun (sql: string) (parameters: (string * obj) list) ->
                task {
                    let! _ =
                        db.execNonQuery competingClassificationSql
                            [ "@id", Database.boxVal documentId ]
                    return! db.execNonQuery sql parameters
                }
        tryRepairSavedPath =
            fun _ ->
                System.Threading.Tasks.Task.FromException<
                    Result<Algebra.SavedPathRepairDecision, string>>(
                        InvalidOperationException(
                            "Unexpected saved-path repair in classification fake")) }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Content reclassification reports provenance changes only`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        fs.Put "/archive/invoices/source.pdf" "source"

        try
            let! id =
                insertDocument
                    db "invoices/source.pdf" "invoices" "stored-sha"
            let classify confidence =
                Reclassification.reclassifyFromContent
                    db fs.Fs "/archive" id "invoices" confidence
            let! initial = classify 0.8
            let! unchanged = classify 0.8
            let! revised = classify 0.9
            Assert.True((expectSuccess initial).Changed)
            Assert.False((expectSuccess unchanged).Changed)
            Assert.True((expectSuccess revised).Changed)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reclassification rejects concurrent category and provenance changes`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        fs.Put "/archive/invoices/source.pdf" "source"

        try
            let! id =
                insertDocument
                    db "invoices/source.pdf" "invoices" "stored-sha"
            let interleavedDb = withCompetingClassification db id
            let! result =
                DocumentManagement.reclassify
                    interleavedDb fs.Fs "/archive" id "receipts"

            match result with
            | Error (Reclassification.ConcurrentChange actualId) ->
                Assert.Equal(id, actualId)
            | other ->
                failwith $"Expected concurrent-change failure, got {other}"

            let! rows =
                db.execReader
                    """SELECT category, classification_tier,
                              classification_confidence
                       FROM documents WHERE id = @id"""
                    [ "@id", Database.boxVal id ]
            let reader = Prelude.RowReader(rows.Head)
            Assert.Equal("tax", reader.String "category" "")
            Assert.Equal(
                "content",
                reader.String "classification_tier" "")
            Assert.Equal(
                0.97,
                reader.Float "classification_confidence" 0.0)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reclassification preserves source identity and does not create category directory`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        fs.Put "/archive/original/source.pdf" "immutable bytes"

        try
            let! id =
                insertDocument
                    db "original/source.pdf" "original" "stored-sha"
            let before = fs.Get "/archive/original/source.pdf"
            let! result =
                DocumentManagement.reclassify
                    db fs.Fs "/archive" id "receipts"
            let outcome = expectSuccess result

            Assert.Equal("original/source.pdf", outcome.SavedPath)
            Assert.Equal("stored-sha", outcome.Sha256)
            Assert.Equal(before, fs.Get "/archive/original/source.pdf")
            Assert.False(fs.Fs.fileExists "/archive/receipts/source.pdf")
            Assert.False(fs.Fs.directoryExists "/archive/receipts")

            let! identity =
                db.execReader
                    "SELECT saved_path, sha256 FROM documents WHERE id = @id"
                    [ "@id", Database.boxVal id ]
            let reader = Prelude.RowReader(identity.Head)
            Assert.Equal(
                "original/source.pdf",
                reader.String "saved_path" "")
            Assert.Equal("stored-sha", reader.String "sha256" "")
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reclassification missing source fails before metadata mutation`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()

        try
            let! id =
                insertDocument
                    db "missing/source.pdf" "invoices" "stored-sha"
            let! result =
                DocumentManagement.reclassify
                    db fs.Fs "/archive" id "receipts"

            match result with
            | Error (Reclassification.SourceMissing path) ->
                Assert.EndsWith("missing/source.pdf", path.Replace('\\', '/'))
            | other -> failwith $"Expected source-missing failure, got {other}"

            let! category =
                db.execScalar
                    "SELECT category FROM documents WHERE id = @id"
                    [ "@id", Database.boxVal id ]
            let! tags =
                db.execScalar
                    "SELECT COUNT(*) FROM tags WHERE document_id = @id"
                    [ "@id", Database.boxVal id ]
            Assert.Equal("invoices", Assert.IsType<string>(category))
            Assert.Equal(0L, tags :?> int64)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reclassification is idempotent and updates provenance tag and FTS`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        fs.Put "/archive/invoices/source.pdf" "source"

        try
            let! id =
                insertDocument
                    db "invoices/source.pdf" "invoices" "stored-sha"
            let! first =
                DocumentManagement.reclassify
                    db fs.Fs "/archive" id "receipts"
            let! second =
                DocumentManagement.reclassify
                    db fs.Fs "/archive" id "receipts"
            Assert.True((expectSuccess first).Changed)
            Assert.False((expectSuccess second).Changed)

            let! metadata =
                db.execReader
                    """SELECT category, classification_tier,
                              classification_confidence
                       FROM documents WHERE id = @id"""
                    [ "@id", Database.boxVal id ]
            let reader = Prelude.RowReader(metadata.Head)
            Assert.Equal("receipts", reader.String "category" "")
            Assert.Equal("manual", reader.String "classification_tier" "")
            Assert.True(reader.OptFloat("classification_confidence").IsNone)

            let! tagCount =
                db.execScalar
                    """SELECT COUNT(*) FROM tags
                       WHERE document_id = @id
                         AND tag = 'receipts'
                         AND source = 'user'"""
                    [ "@id", Database.boxVal id ]
            let! ftsCount =
                db.execScalar
                    """SELECT COUNT(*) FROM documents_fts
                       WHERE rowid = @id
                         AND documents_fts MATCH 'receipts'"""
                    [ "@id", Database.boxVal id ]
            Assert.Equal(1L, tagCount :?> int64)
            Assert.Equal(1L, ftsCount :?> int64)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reclassification replaces generated category tag and preserves user tags`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        fs.Put "/archive/invoices/source.pdf" "source"

        try
            let! id =
                insertDocument
                    db "invoices/source.pdf" "invoices" "stored-sha"
            let! _ =
                db.execNonQuery
                    """INSERT INTO tags (document_id, tag, source)
                       VALUES (@id, 'important', 'user')"""
                    [ "@id", Database.boxVal id ]
            let! receipts =
                DocumentManagement.reclassify
                    db fs.Fs "/archive" id "receipts"
            let! tax =
                DocumentManagement.reclassify
                    db fs.Fs "/archive" id "tax"
            receipts |> expectSuccess |> ignore
            tax |> expectSuccess |> ignore

            let! generated =
                db.execReader
                    """SELECT tag FROM tags
                       WHERE document_id = @id
                         AND created_by = 'reclassification'"""
                    [ "@id", Database.boxVal id ]
            let! userTagCount =
                db.execScalar
                    """SELECT COUNT(*) FROM tags
                       WHERE document_id = @id
                         AND tag = 'important'
                         AND source = 'user'
                         AND created_by IS NULL"""
                    [ "@id", Database.boxVal id ]

            Assert.Equal(1, generated.Length)
            let generatedTag = Prelude.RowReader(generated.Head)
            Assert.Equal("tax", generatedTag.String "tag" "")
            Assert.Equal(1L, Assert.IsType<int64>(userTagCount))
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reclassification preserves a user tag matching the generated category`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        fs.Put "/archive/invoices/source.pdf" "source"
        try
            let! id =
                insertDocument
                    db "invoices/source.pdf" "invoices" "stored-sha"
            do! insertUserTag db id "receipts"
            let! matching = reclassifyAndLoadTags db fs.Fs id "receipts"
            assertTagOwnership
                [ "receipts", None
                  "receipts", Some "reclassification" ]
                matching
            let! replaced = reclassifyAndLoadTags db fs.Fs id "tax"
            assertTagOwnership
                [ "receipts", None
                  "tax", Some "reclassification" ]
                replaced
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reclassification rolls back category tag and FTS when trigger write fails`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        fs.Put "/archive/invoices/source.pdf" "source"

        try
            let! id =
                insertDocument
                    db "invoices/source.pdf" "invoices" "stored-sha"
            let! _ =
                db.execNonQuery
                    """CREATE TRIGGER fail_reclassification_tag
                       BEFORE INSERT ON tags
                       BEGIN
                           SELECT RAISE(ABORT, 'synthetic tag failure');
                       END"""
                    []
            let! result =
                DocumentManagement.reclassify
                    db fs.Fs "/archive" id "receipts"

            match result with
            | Error (Reclassification.DatabaseFailure _) -> ()
            | other -> failwith $"Expected database failure, got {other}"

            let! category =
                db.execScalar
                    "SELECT category FROM documents WHERE id = @id"
                    [ "@id", Database.boxVal id ]
            let! tagCount =
                db.execScalar
                    "SELECT COUNT(*) FROM tags WHERE document_id = @id"
                    [ "@id", Database.boxVal id ]
            let! ftsCount =
                db.execScalar
                    """SELECT COUNT(*) FROM documents_fts
                       WHERE rowid = @id
                         AND documents_fts MATCH 'receipts'"""
                    [ "@id", Database.boxVal id ]
            Assert.Equal("invoices", Assert.IsType<string>(category))
            Assert.Equal(0L, tagCount :?> int64)
            Assert.Equal(0L, ftsCount :?> int64)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reclassification preserves every V5 completion and output`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        fs.Put "/archive/original/source.pdf" "source"

        try
            let! id =
                insertDocument
                    db "original/source.pdf" "original" "stored-sha"
            for sql in PipelineV5.coreSchema do
                let! _ = db.execNonQuery sql []
                ()
            for tableName in
                [ "extraction"; "triage"; "comprehension"; "embedding" ] do
                let! _ =
                    db.execNonQuery
                        $"CREATE TABLE {tableName} (document_id INTEGER PRIMARY KEY, marker TEXT)"
                        []
                let! _ =
                    db.execNonQuery
                        $"INSERT INTO {tableName} (document_id, marker) VALUES (@id, @marker)"
                        [ "@id", Database.boxVal id
                          "@marker", Database.boxVal tableName ]
                ()
            for stage in
                [ "extract"; "triage"; "deep-comprehend"; "embed" ] do
                do! PipelineV5.markCompleted db id stage

            let! result =
                DocumentManagement.reclassify
                    db fs.Fs "/archive" id "receipts"
            Assert.True(Result.isOk result)

            let! completions =
                db.execScalar
                    "SELECT COUNT(*) FROM stage_completions WHERE document_id = @id"
                    [ "@id", Database.boxVal id ]
            Assert.Equal(4L, completions :?> int64)

            for tableName in
                [ "extraction"; "triage"; "comprehension"; "embedding" ] do
                let! marker =
                    db.execScalar
                        $"SELECT marker FROM {tableName} WHERE document_id = @id"
                        [ "@id", Database.boxVal id ]
                Assert.Equal(tableName, Assert.IsType<string>(marker))
        finally
            db.dispose ()
    }
