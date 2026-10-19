module Hermes.Tests.SourceFilesTests

open System
open System.IO
open System.Text
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Xunit
open Hermes.Core

let private content text = Encoding.UTF8.GetBytes(text: string)

let private withFiles verify =
    task {
        let db = TestHelpers.createDb ()
        let files = TestHelpers.memFs ()
        try
            do! verify db files
        finally
            db.dispose ()
    }

let private own (db: Algebra.Database) (path: string) (bytes: byte array) =
    db.execScalar
        """INSERT INTO documents (source_type, saved_path, category, sha256)
           VALUES ('manual_drop', @path, 'unsorted', @sha) RETURNING id"""
        [ "@path", Database.boxVal path
          "@sha", Database.boxVal (SourceFiles.hash bytes) ]

let private expectOwnershipFailure (operation: unit -> Task<unit>) =
    Assert.ThrowsAsync<InvalidOperationException>(
        Func<Task>(fun () -> operation () :> Task))

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Staged source claims prevent another writer from replacing bytes`` () =
    withFiles (fun db files ->
        task {
            do! SourceFiles.writeNew db files.Fs "/archive/source.pdf" (content "first")
            let! _ =
                expectOwnershipFailure (fun () ->
                    SourceFiles.writeNew db files.Fs "/archive/SOURCE.PDF" (content "second"))
            Assert.Equal(Some "first", files.Get "/archive/source.pdf")
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Staged source retries are byte-identical and cannot register another hash`` () =
    withFiles (fun db files ->
        task {
            let bytes = content "first"
            do! SourceFiles.writeNew db files.Fs "/archive/source.pdf" bytes
            do! SourceFiles.writeNew db files.Fs "/archive/source.pdf" bytes
            let! _ =
                Assert.ThrowsAsync<SqliteException>(
                    Func<Task>(fun () -> own db "source.pdf" (content "second") :> Task))
            let! _ = own db "source.pdf" bytes
            Assert.Equal(Some "first", files.Get "/archive/source.pdf")
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Generated writes cannot overwrite a document or staged source`` () =
    withFiles (fun db files ->
        task {
            let path = "/archive/source.pdf.extracted.md"
            do! SourceFiles.writeNew db files.Fs path (content "source")
            let! _ =
                expectOwnershipFailure (fun () ->
                    SourceFiles.writeMetadata db files.Fs path (content "generated"))
            Assert.Equal(Some "source", files.Get path)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Owned source deletion and movement both fail without mutation`` () =
    withFiles (fun db files ->
        task {
            files.Put "/archive/source.pdf" "source"
            let! _ = own db "source.pdf" (content "source")
            let! _ = expectOwnershipFailure (fun () -> SourceFiles.delete db files.Fs "/archive/source.pdf")
            let! _ =
                expectOwnershipFailure (fun () ->
                    SourceFiles.move db files.Fs "/archive/source.pdf" "/archive/other.pdf")
            Assert.Equal(Some "source", files.Get "/archive/source.pdf")
            Assert.Equal(None, files.Get "/archive/other.pdf")
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Missing-source redownload requires the exact exclusive identity`` () =
    withFiles (fun db files ->
        task {
            let bytes = content "source"
            let! id = own db "source.pdf" bytes
            let sha = SourceFiles.hash bytes
            do! SourceFiles.restore db files.Fs (unbox<int64> id) "/archive/source.pdf" sha bytes
            let! _ =
                expectOwnershipFailure (fun () ->
                    SourceFiles.restore db files.Fs 999L "/archive/source.pdf" sha bytes)
            Assert.Equal(Some "source", files.Get "/archive/source.pdf")
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Classifier duplicate intake cannot delete its own archived source`` () =
    withFiles (fun db files ->
        task {
            files.Put "/archive/source.pdf" "source"
            let! _ = own db "source.pdf" (content "source")
            let rules: Algebra.RulesEngine =
                { classify = fun _ _ -> { Category = "tax"; MatchedRule = Domain.DefaultRule }
                  reload = fun () -> Task.FromResult(Ok ()) }
            let! result =
                Classifier.processFile files.Fs db TestHelpers.silentLogger
                    TestHelpers.defaultClock rules "/archive" "/archive/source.pdf"
            Assert.True(Result.isError result)
            Assert.Equal(Some "source", files.Get "/archive/source.pdf")
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Source writes reject a child symlink escaping the archive`` () =
    task {
        let db, directory = DatabaseTests.createTempFileDb ()
        let outside = Path.Combine(Path.GetTempPath(), "hermes-outside-" + Guid.NewGuid().ToString("N"))
        try
            Directory.CreateDirectory(outside) |> ignore
            Directory.CreateSymbolicLink(Path.Combine(directory, "linked"), outside) |> ignore
            let! _ = db.initSchema ()
            let path = Path.Combine(directory, "linked", "source.pdf")
            let! _ =
                Assert.ThrowsAsync<SqliteException>(
                    Func<Task>(fun () ->
                        SourceFiles.writeNew db Interpreters.realFileSystem path (content "source") :> Task))
            Assert.False(File.Exists(Path.Combine(outside, "source.pdf")))
        finally
            db.dispose ()
            DatabaseTests.cleanupDir directory
            DatabaseTests.cleanupDir outside
    }
