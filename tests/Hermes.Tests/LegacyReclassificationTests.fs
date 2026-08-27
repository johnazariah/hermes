module Hermes.Tests.LegacyReclassificationTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Xunit
open Hermes.Core

let private bounds maxFiles =
    LegacyReclassification.createBounds 20 maxFiles
    |> Result.defaultWith failwith

let private hashText (value: string) =
    let bytes = Encoding.UTF8.GetBytes(value)
    let hash: byte array = SHA256.HashData(bytes)
    Convert.ToHexString(hash).ToLowerInvariant()

let private insertLegacy
    (db: Algebra.Database)
    (savedPath: string)
    (sha256: string) =
    task {
        let! _ =
            db.execNonQuery
                """INSERT INTO documents
                   (source_type, saved_path, category, sha256)
                   VALUES ('manual_drop', @path, 'receipts', @sha)"""
                [ "@path", Database.boxVal savedPath
                  "@sha", Database.boxVal sha256 ]
        ()
    }

let private archiveFile
    (fs: TestHelpers.MemFs)
    (path: string)
    (content: string) =
    fs.Fs.createDirectory "/archive"
    match Path.GetDirectoryName(path) with
    | null -> failwith $"Expected parent directory for archive path: {path}"
    | directory -> fs.Fs.createDirectory directory
    fs.Put path content

[<Theory>]
[<InlineData(0, 20, "maxDocuments must be between 1 and 1000")>]
[<InlineData(1001, 20, "maxDocuments must be between 1 and 1000")>]
[<InlineData(20, 0, "maxFiles must be between 1 and 10000")>]
[<InlineData(20, 10001, "maxFiles must be between 1 and 10000")>]
[<Trait("Category", "Unit")>]
let ``Legacy scan bounds reject unsafe limits``
    (maxDocuments: int, maxFiles: int, expected: string) =
    match LegacyReclassification.createBounds maxDocuments maxFiles with
    | Error actual -> Assert.Equal(expected, actual)
    | Ok _ -> failwith "Expected invalid scan bounds"

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Legacy detector dry-run and repair use unique SHA evidence only`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        archiveFile fs "/archive/legacy/source.pdf" "source bytes"

        try
            let sha = hashText "source bytes"
            do! insertLegacy db "missing/source.pdf" sha
            let! scan =
                LegacyReclassification.detect
                    db fs.Fs "/archive" (bounds 100)

            match scan.Findings.Head.Evidence with
            | LegacyReclassification.UniqueShaMatch candidate ->
                Assert.EndsWith(
                    "legacy/source.pdf",
                    candidate.Replace('\\', '/'))
            | evidence ->
                failwith $"Expected unique evidence, got {evidence}"

            let! dryRunPath =
                db.execScalar "SELECT saved_path FROM documents WHERE id = 1" []
            Assert.Equal("missing/source.pdf", Assert.IsType<string>(dryRunPath))

            let! report =
                LegacyReclassification.repair
                    db fs.Fs "/archive" (bounds 100)
            match report.Outcomes.Head.Disposition with
            | LegacyReclassification.Repaired candidate ->
                Assert.EndsWith(
                    "legacy/source.pdf",
                    candidate.Replace('\\', '/'))
            | disposition ->
                failwith $"Expected repaired outcome, got {disposition}"

            Assert.True(fs.Fs.fileExists "/archive/legacy/source.pdf")
            Assert.False(fs.Fs.fileExists "/archive/missing/source.pdf")
            let! category =
                db.execScalar "SELECT category FROM documents WHERE id = 1" []
            Assert.Equal("receipts", Assert.IsType<string>(category))
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Legacy repair leaves ambiguous SHA evidence unchanged`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        archiveFile fs "/archive/a/one.pdf" "same bytes"
        archiveFile fs "/archive/b/two.pdf" "same bytes"

        try
            do! insertLegacy
                    db "missing/source.pdf" (hashText "same bytes")
            let! report =
                LegacyReclassification.repair
                    db fs.Fs "/archive" (bounds 100)

            match report.Outcomes.Head.Disposition with
            | LegacyReclassification.Skipped(
                LegacyReclassification.AmbiguousShaMatches candidates) ->
                Assert.Equal(2, candidates.Length)
            | disposition ->
                failwith $"Expected ambiguous skip, got {disposition}"

            let! path =
                db.execScalar "SELECT saved_path FROM documents WHERE id = 1" []
            Assert.Equal("missing/source.pdf", Assert.IsType<string>(path))
            Assert.True(fs.Fs.fileExists "/archive/a/one.pdf")
            Assert.True(fs.Fs.fileExists "/archive/b/two.pdf")
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Legacy repair leaves missing SHA evidence unchanged and visible`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        fs.Fs.createDirectory "/archive"

        try
            do! insertLegacy db "missing/source.pdf" "absent-sha"
            let! report =
                LegacyReclassification.repair
                    db fs.Fs "/archive" (bounds 100)

            match report.Outcomes.Head.Disposition with
            | LegacyReclassification.Skipped(
                LegacyReclassification.MissingShaMatch) -> ()
            | disposition ->
                failwith $"Expected missing skip, got {disposition}"

            let! path =
                db.execScalar "SELECT saved_path FROM documents WHERE id = 1" []
            Assert.Equal("missing/source.pdf", Assert.IsType<string>(path))
        finally
            db.dispose ()
    }

[<Theory>]
[<InlineData("source.pdf.extracted.md")>]
[<InlineData("source.pdf.md")>]
[<InlineData(".hermes.json")>]
[<InlineData("thread.comprehension.json")>]
[<InlineData("db.sqlite")>]
[<InlineData("db.sqlite-wal")>]
[<InlineData("db.sqlite-shm")>]
[<InlineData("db.sqlite-journal")>]
[<InlineData("source.pdf.meta.json")>]
[<Trait("Category", "Integration")>]
let ``Legacy repair excludes generated artifacts from SHA candidates``
    (artifactName: string) =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        archiveFile
            fs
            $"/archive/generated/{artifactName}"
            "matching bytes"

        try
            do! insertLegacy
                    db
                    "missing/source.pdf"
                    (hashText "matching bytes")
            let! report =
                LegacyReclassification.repair
                    db fs.Fs "/archive" (bounds 100)

            match report.Outcomes.Head.Disposition with
            | LegacyReclassification.Skipped(
                LegacyReclassification.MissingShaMatch) -> ()
            | disposition ->
                failwith $"Expected missing skip, got {disposition}"

            let! path =
                db.execScalar "SELECT saved_path FROM documents WHERE id = 1" []
            Assert.Equal("missing/source.pdf", Assert.IsType<string>(path))
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Legacy repair leaves current-path SHA mismatch unchanged and visible`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        archiveFile fs "/archive/current/source.pdf" "different bytes"

        try
            do! insertLegacy
                    db
                    "current/source.pdf"
                    (hashText "expected bytes")
            let! report =
                LegacyReclassification.repair
                    db fs.Fs "/archive" (bounds 100)

            match report.Outcomes.Head.Disposition with
            | LegacyReclassification.Skipped(
                LegacyReclassification.ShaMismatch actual) ->
                Assert.Equal(hashText "different bytes", actual)
            | disposition ->
                failwith $"Expected mismatch skip, got {disposition}"

            let! path =
                db.execScalar "SELECT saved_path FROM documents WHERE id = 1" []
            Assert.Equal("current/source.pdf", Assert.IsType<string>(path))
            Assert.Equal(
                Some "different bytes",
                fs.Get "/archive/current/source.pdf")
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Truncated legacy scan never claims unique identity`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        archiveFile fs "/archive/a/source.pdf" "target"
        archiveFile fs "/archive/a/other.pdf" "other"

        try
            do! insertLegacy
                    db "missing/source.pdf" (hashText "target")
            let! scan =
                LegacyReclassification.detect
                    db fs.Fs "/archive" (bounds 1)

            Assert.True(scan.FilesTruncated)
            match scan.Findings.Head.Evidence with
            | LegacyReclassification.UniqueShaMatch _ ->
                failwith "Truncated scan must not prove uniqueness"
            | LegacyReclassification.InconclusiveScan _ -> ()
            | evidence ->
                failwith $"Expected inconclusive evidence, got {evidence}"
        finally
            db.dispose ()
    }
