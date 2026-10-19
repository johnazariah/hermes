module Hermes.Tests.ThreadBodyIngestionTests

open System
open System.Threading.Tasks
open Xunit
open Hermes.Core

let private withFiles verify =
    task {
        let db = TestHelpers.createDb ()
        let files = TestHelpers.memFs ()
        try
            do! verify db files
        finally
            db.dispose ()
    }

let private record (db: Algebra.Database) (source: ThreadBodyIngestion.Source) =
    task {
        let! id =
            db.execScalar
                """INSERT INTO documents
                   (source_type, account, thread_id, email_date, saved_path, category, sha256)
                   VALUES ('email_body', 'account', 'thread-001', '2024-06-15', @path, 'unsorted', @sha)
                   RETURNING id"""
                [ "@path", Database.boxVal source.SavedPath
                  "@sha", Database.boxVal source.Sha256 ]
        return unbox<int64> id
    }

let private seedPrevious (db: Algebra.Database) (path: string) =
    db.execNonQuery
        """INSERT INTO documents
           (source_type, account, thread_id, email_date, saved_path, category, sha256)
           VALUES ('email_body', 'account', 'thread-001', '2020-01-01', @path, 'unsorted', 'old')"""
        [ "@path", Database.boxVal path ]

let private save db (files: TestHelpers.MemFs) =
    ThreadBodyIngestion.save db files.Fs "/archive" "account"
        EmailSyncTests.sampleMessage (TestHelpers.defaultClock.utcNow ())
        (Some "new body") record

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Appending a body preserves the previous document and source`` () =
    withFiles (fun db files ->
        task {
            let! _ = seedPrevious db "old.md"
            files.Put "/archive/old.md" "old source"
            let! saved = save db files
            Assert.True(saved.IsSome)
            Assert.Equal(Some "old source", files.Get "/archive/old.md")
            let! count = db.execScalar "SELECT COUNT(*) FROM documents" []
            Assert.Equal(2L, unbox<int64> count)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Appending a body never invokes document deletion`` () =
    withFiles (fun db files ->
        task {
            let! _ = seedPrevious db "old.md"
            files.Put "/archive/old.md" "old source"
            let! _ =
                db.execNonQuery
                    "CREATE TRIGGER reject_body_delete BEFORE DELETE ON documents BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END" []
            let! saved = save db files
            Assert.True(saved.IsSome)
            let! count = db.execScalar "SELECT COUNT(*) FROM documents WHERE saved_path = 'old.md'" []
            Assert.Equal(1L, unbox<int64> count)
            Assert.Equal(Some "old source", files.Get "/archive/old.md")
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Appending an independent source does not repair ambiguous historical ownership`` () =
    withFiles (fun db files ->
        task {
            let! _ = seedPrevious db "old.md"
            let! _ = db.execNonQuery "DROP TRIGGER doc_saved_path_insert" []
            let! _ = seedPrevious db "./OLD.MD"
            let! _ = db.initSchema ()
            files.Put "/archive/old.md" "old source"
            let! saved = save db files
            Assert.True(saved.IsSome)
            Assert.Equal(Some "old source", files.Get "/archive/old.md")
            let! count = db.execScalar "SELECT COUNT(*) FROM documents" []
            Assert.Equal(3L, unbox<int64> count)
        })
