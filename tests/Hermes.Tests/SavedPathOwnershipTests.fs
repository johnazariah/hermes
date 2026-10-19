module Hermes.Tests.SavedPathOwnershipTests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Xunit
open Hermes.Core

let private insert (db: Algebra.Database) (path: string) (sha: string) =
    db.execScalar
        """INSERT INTO documents (source_type, saved_path, category, sha256)
           VALUES ('manual_drop', @path, 'unsorted', @sha) RETURNING id"""
        [ "@path", Database.boxVal path; "@sha", Database.boxVal sha ]

let private withDatabase verify =
    task {
        let db = TestHelpers.createDb ()
        try
            do! verify db
        finally
            db.dispose ()
    }

let private expectSqlFailure (operation: unit -> Task<'T>) =
    Assert.ThrowsAsync<SqliteException>(
        Func<Task>(fun () -> operation () :> Task))

[<Theory>]
[<InlineData("source.pdf", "SOURCE.PDF")>]
[<InlineData("caf\u00e9.pdf", "cafe\u0301.pdf")>]
[<InlineData("source.pdf", "./source.pdf")>]
[<InlineData("source.pdf", "nested/../source.pdf")>]
[<InlineData("nested/source.pdf", "nested\\source.pdf")>]
[<InlineData("source.pdf", "/archive/source.pdf")>]
[<Trait("Category", "Integration")>]
let ``All insert entry points reject canonical path aliases`` first alias =
    withDatabase (fun db ->
        task {
            let! _ = insert db first "first"
            let! error = expectSqlFailure (fun () -> insert db alias "second")
            Assert.Contains("ownership conflict", error.Message)
            let! count = db.execScalar "SELECT COUNT(*) FROM documents" []
            Assert.Equal(1L, unbox<int64> count)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Full-row persistence cannot take another document path`` () =
    withDatabase (fun db ->
        task {
            let! _ = insert db "first.pdf" "first"
            let! secondValue = insert db "second.pdf" "second"
            let second = unbox<int64> secondValue
            let! rows =
                db.execReader "SELECT * FROM documents WHERE id = @id"
                    [ "@id", Database.boxVal second ]
            let document =
                rows.Head |> Document.encode "saved_path" (Database.boxVal "./first.pdf")
            let! _ = expectSqlFailure (fun () -> Document.persist db document)
            let! path =
                db.execScalar "SELECT saved_path FROM documents WHERE id = @id"
                    [ "@id", Database.boxVal second ]
            Assert.Equal("second.pdf", unbox<string> path)
        })

let private seedExistingDuplicate (db: Algebra.Database) =
    task {
        let! _ = db.execNonQuery "DROP TRIGGER doc_saved_path_insert" []
        let! _ = insert db "source.pdf" "first"
        let! _ = insert db "./SOURCE.PDF" "second"
        let! result = db.initSchema ()
        Assert.True(Result.isOk result)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Existing duplicates remain intact and reject reclassification and deletion`` () =
    withDatabase (fun db ->
        task {
            do! seedExistingDuplicate db
            let files = TestHelpers.memFs ()
            files.Put "/archive/source.pdf" "source"
            let! result = Reclassification.reclassify db files.Fs "/archive" 1L "tax"
            Assert.True(Result.isError result)
            let! _ = expectSqlFailure (fun () -> db.execNonQuery "DELETE FROM documents WHERE id = 1" [])
            let! count = db.execScalar "SELECT COUNT(*) FROM documents WHERE category = 'unsorted'" []
            Assert.Equal(2L, unbox<int64> count)
            Assert.Equal(Some "source", files.Get "/archive/source.pdf")
        })

[<Theory>]
[<InlineData("../outside.pdf")>]
[<InlineData("/outside.pdf")>]
[<InlineData(".")>]
[<Trait("Category", "Integration")>]
let ``Production insert guards reject non-file or escaping paths`` path =
    withDatabase (fun db ->
        task {
            let! _ = expectSqlFailure (fun () -> insert db path "sha")
            let! count = db.execScalar "SELECT COUNT(*) FROM documents" []
            Assert.Equal(0L, unbox<int64> count)
        })

let private attempt db path =
    task {
        try
            let! _ = insert db path "sha"
            return true
        with :? SqliteException ->
            return false
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Independent connections cannot concurrently acquire one canonical path`` () =
    task {
        let db, directory = DatabaseTests.createTempFileDb ()
        try
            let! _ = db.initSchema ()
            let other = Database.fromPath (Path.Combine(directory, "db.sqlite"))
            try
                let first = Task.Run<bool>(Func<Task<bool>>(fun () -> attempt db "source.pdf"))
                let second = Task.Run<bool>(Func<Task<bool>>(fun () -> attempt other "./SOURCE.PDF"))
                let! results = Task.WhenAll(first, second)
                Assert.Equal(1, results |> Array.filter id |> Array.length)
            finally
                other.dispose ()
        finally
            db.dispose ()
            DatabaseTests.cleanupDir directory
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Unregistered SQLite connections fail rather than bypassing ownership guards`` () =
    task {
        let db, directory = DatabaseTests.createTempFileDb ()
        try
            let! _ = db.initSchema ()
            use raw = Database.openConnection (Path.Combine(directory, "db.sqlite"))
            use command = raw.CreateCommand()
            command.CommandText <-
                "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('manual_drop', 'source.pdf', 'tax', 'sha')"
            let error = Assert.Throws<SqliteException>(fun () -> command.ExecuteNonQuery() |> ignore)
            Assert.Contains("hermes_saved_path_key", error.Message)
        finally
            db.dispose ()
            DatabaseTests.cleanupDir directory
    }
