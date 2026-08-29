module Hermes.Tests.LegacyReclassificationTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
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

let private loadSavedPath (db: Algebra.Database) documentId =
    task {
        let! value =
            db.execScalar
                "SELECT saved_path FROM documents WHERE id = @id"
                [ "@id", Database.boxVal documentId ]

        return Assert.IsType<string>(value)
    }

let private loadSavedPaths (db: Algebra.Database) =
    task {
        let! rows =
            db.execReader
                "SELECT saved_path FROM documents ORDER BY id"
                []

        return
            rows
            |> List.map (fun row ->
                Prelude.RowReader(row).String "saved_path" "")
    }

let private dispositionFor
    documentId
    (report: LegacyReclassification.RepairReport) =
    report.Outcomes
    |> List.find (fun outcome -> outcome.DocumentId = documentId)
    |> fun outcome -> outcome.Disposition

let private assertConflict expectedOwnerId disposition =
    match disposition with
    | LegacyReclassification.Conflict conflict ->
        Assert.Equal<int64 list>(
            [ expectedOwnerId ],
            conflict.OwnerDocumentIds)
    | other ->
        failwith $"Expected ownership conflict, got {other}"

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Legacy repair reports candidate ownership conflict without mutation`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        archiveFile fs "/archive/owned/source.pdf" "shared bytes"

        try
            let sha = hashText "shared bytes"
            do! insertLegacy db "owned/source.pdf" sha
            do! insertLegacy db "missing/source.pdf" sha

            let! beforeFts =
                db.execScalar
                    "SELECT COUNT(*) FROM documents_fts WHERE rowid IN (1, 2)"
                    []

            let! report =
                LegacyReclassification.repair
                    db fs.Fs "/archive" (bounds 100)

            let disposition = dispositionFor 2L report
            assertConflict 1L disposition

            let! ownerPath = loadSavedPath db 1L
            let! legacyPath = loadSavedPath db 2L
            let! afterFts =
                db.execScalar
                    "SELECT COUNT(*) FROM documents_fts WHERE rowid IN (1, 2)"
                    []
            let! stages =
                db.execReader
                    "SELECT stage, category FROM documents ORDER BY id"
                    []

            Assert.Equal("owned/source.pdf", ownerPath)
            Assert.Equal("missing/source.pdf", legacyPath)
            Assert.Equal(
                Assert.IsType<int64>(beforeFts),
                Assert.IsType<int64>(afterFts))
            Assert.All(
                stages,
                fun row ->
                    let reader = Prelude.RowReader(row)
                    Assert.Equal("received", reader.String "stage" "")
                    Assert.Equal("receipts", reader.String "category" ""))
            Assert.Equal(Some "shared bytes", fs.Get "/archive/owned/source.pdf")
            Assert.False(fs.Fs.fileExists "/archive/missing/source.pdf")
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Duplicate SHA rows with distinct files remain ambiguous`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        archiveFile fs "/archive/a/source.pdf" "duplicate bytes"
        archiveFile fs "/archive/b/source.pdf" "duplicate bytes"

        try
            let sha = hashText "duplicate bytes"
            do! insertLegacy db "a/source.pdf" sha
            do! insertLegacy db "b/source.pdf" sha
            do! insertLegacy db "missing/source.pdf" sha

            let! report =
                LegacyReclassification.repair
                    db fs.Fs "/archive" (bounds 100)

            match dispositionFor 3L report with
            | LegacyReclassification.Skipped(
                LegacyReclassification.AmbiguousShaMatches candidates) ->
                Assert.Equal(2, candidates.Length)
            | disposition ->
                failwith $"Expected ambiguous SHA evidence, got {disposition}"

            let! paths = loadSavedPaths db
            Assert.Equal<string list>(
                [ "a/source.pdf"
                  "b/source.pdf"
                  "missing/source.pdf" ],
                paths)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Successful unowned repair is idempotent`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        archiveFile fs "/archive/found/source.pdf" "source bytes"

        try
            do! insertLegacy
                    db "missing/source.pdf" (hashText "source bytes")

            let! first =
                LegacyReclassification.repair
                    db fs.Fs "/archive" (bounds 100)

            match dispositionFor 1L first with
            | LegacyReclassification.Repaired candidate ->
                Assert.EndsWith(
                    "found/source.pdf",
                    candidate.Replace('\\', '/'))
            | disposition ->
                failwith $"Expected repair, got {disposition}"

            let! second =
                LegacyReclassification.repair
                    db fs.Fs "/archive" (bounds 100)

            Assert.Empty(second.Outcomes)
            let! path = loadSavedPath db 1L
            Assert.EndsWith("found/source.pdf", path.Replace('\\', '/'))
            Assert.Equal(Some "source bytes", fs.Get "/archive/found/source.pdf")
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Same document canonical ownership is explicitly unchanged`` () =
    task {
        let db = TestHelpers.createDb ()

        try
            let sha = hashText "source bytes"
            do! insertLegacy db "owned/../owned/source.pdf" sha

            let request: Algebra.SavedPathRepairRequest =
                { ArchiveDirectory = "/archive"
                  DocumentId = 1L
                  CurrentSavedPath = "owned/../owned/source.pdf"
                  ExpectedSha256 = sha
                  CandidateSavedPath = "owned/source.pdf" }

            let! decision = db.tryRepairSavedPath request

            match decision with
            | Ok(
                Algebra.SavedPathAlreadyOwnedByDocument
                    "owned/../owned/source.pdf") -> ()
            | other ->
                failwith $"Expected same-document ownership, got {other}"

            let! path = loadSavedPath db 1L
            Assert.Equal("owned/../owned/source.pdf", path)
        finally
            db.dispose ()
    }

let private collisionReport ownerPath candidatePath =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        archiveFile fs candidatePath "shared bytes"

        try
            let sha = hashText "shared bytes"
            do! insertLegacy db ownerPath sha
            do! insertLegacy db "missing/source.pdf" sha

            let! report =
                LegacyReclassification.repair
                    db fs.Fs "/archive" (bounds 100)

            assertConflict 1L (dispositionFor 2L report)
            let! missingPath = loadSavedPath db 2L
            Assert.Equal("missing/source.pdf", missingPath)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Unicode composed and decomposed paths collide conservatively`` () =
    collisionReport
        "caf\u0065\u0301/source.pdf"
        "/archive/caf\u00e9/source.pdf"

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Case equivalent paths collide conservatively`` () =
    collisionReport
        "CASE/SOURCE.PDF"
        "/archive/case/source.pdf"

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Slash relative and dot equivalent paths collide`` () =
    collisionReport
        "owned\\folder\\..\\source.pdf"
        "/archive/owned/source.pdf"

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Malformed existing saved path fails the ownership decision`` () =
    task {
        let db = TestHelpers.createDb ()

        try
            do! insertLegacy db "" "unrelated-sha"
            do! insertLegacy db "missing/source.pdf" "target-sha"

            let request: Algebra.SavedPathRepairRequest =
                { ArchiveDirectory = "/archive"
                  DocumentId = 2L
                  CurrentSavedPath = "missing/source.pdf"
                  ExpectedSha256 = "target-sha"
                  CandidateSavedPath = "found/source.pdf" }

            let! decision = db.tryRepairSavedPath request

            match decision with
            | Error message ->
                Assert.Contains("Document 1", message)
                Assert.Contains("invalid saved_path", message)
            | other ->
                failwith $"Expected invalid-owner-path failure, got {other}"

            let! path = loadSavedPath db 2L
            Assert.Equal("missing/source.pdf", path)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Repair transaction rejects a relative candidate path that escapes the archive`` () =
    task {
        let db = TestHelpers.createDb ()

        try
            let sha = hashText "source bytes"
            do! insertLegacy db "missing/source.pdf" sha

            let request: Algebra.SavedPathRepairRequest =
                { ArchiveDirectory = "/archive"
                  DocumentId = 1L
                  CurrentSavedPath = "missing/source.pdf"
                  ExpectedSha256 = sha
                  CandidateSavedPath = "../outside/source.pdf" }

            let! decision = db.tryRepairSavedPath request

            match decision with
            | Error message -> Assert.Contains("escapes the archive directory", message)
            | other -> failwith $"Expected an escaping candidate to be rejected, got {other}"

            let! path = loadSavedPath db 1L
            Assert.Equal("missing/source.pdf", path)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Repair transaction rejects a rooted candidate path outside the archive`` () =
    task {
        let db = TestHelpers.createDb ()

        try
            let sha = hashText "source bytes"
            do! insertLegacy db "missing/source.pdf" sha

            let rootedEscape =
                Path.Combine(Path.GetTempPath(), "hermes-legacy-escape", "outside.pdf")

            let request: Algebra.SavedPathRepairRequest =
                { ArchiveDirectory = "/archive"
                  DocumentId = 1L
                  CurrentSavedPath = "missing/source.pdf"
                  ExpectedSha256 = sha
                  CandidateSavedPath = rootedEscape }

            let! decision = db.tryRepairSavedPath request

            match decision with
            | Error message -> Assert.Contains("escapes the archive directory", message)
            | other -> failwith $"Expected a rooted escape to be rejected, got {other}"

            let! path = loadSavedPath db 1L
            Assert.Equal("missing/source.pdf", path)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Ownership transaction rolls back when update fails`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        archiveFile fs "/archive/found/source.pdf" "source bytes"

        try
            do! insertLegacy
                    db "missing/source.pdf" (hashText "source bytes")

            let! _ =
                db.execNonQuery
                    """CREATE TRIGGER reject_legacy_path_repair
                       BEFORE UPDATE OF saved_path ON documents
                       BEGIN
                           SELECT RAISE(ABORT, 'synthetic path failure');
                       END"""
                    []

            let! report =
                LegacyReclassification.repair
                    db fs.Fs "/archive" (bounds 100)

            match dispositionFor 1L report with
            | LegacyReclassification.Failed(
                LegacyReclassification.DatabaseFailure message) ->
                Assert.Contains("synthetic path failure", message)
            | disposition ->
                failwith $"Expected database failure, got {disposition}"

            let! path = loadSavedPath db 1L
            Assert.Equal("missing/source.pdf", path)
            Assert.Equal(Some "source bytes", fs.Get "/archive/found/source.pdf")
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Concurrent repairs cannot assign one candidate to two documents`` () =
    task {
        let directory =
            Path.Combine(
                Path.GetTempPath(),
                $"hermes-legacy-race-{Guid.NewGuid():N}")

        Directory.CreateDirectory(directory) |> ignore
        let databasePath = Path.Combine(directory, "db.sqlite")
        let firstDb = Database.fromPath databasePath
        let secondDb = Database.fromPath databasePath
        let fs = TestHelpers.memFs ()
        archiveFile fs "/archive/found/source.pdf" "shared bytes"

        try
            let! initialized = firstDb.initSchema ()
            initialized |> Result.defaultWith failwith |> ignore

            let sha = hashText "shared bytes"
            do! insertLegacy firstDb "missing/first.pdf" sha
            do! insertLegacy firstDb "missing/second.pdf" sha

            let repair db =
                LegacyReclassification.repair
                    db fs.Fs "/archive" (bounds 100)

            let repairs: Task<LegacyReclassification.RepairReport> array =
                [| repair firstDb
                   repair secondDb |]

            let! reports = Task.WhenAll repairs

            let dispositions =
                reports
                |> Array.collect (fun report ->
                    report.Outcomes
                    |> List.map (fun outcome -> outcome.Disposition)
                    |> List.toArray)

            let repairedCount =
                dispositions
                |> Array.filter (function
                    | LegacyReclassification.Repaired _ -> true
                    | _ -> false)
                |> Array.length

            let conflictCount =
                dispositions
                |> Array.filter (function
                    | LegacyReclassification.Conflict _ -> true
                    | _ -> false)
                |> Array.length

            let candidateKey =
                Database.canonicalArchivePath
                    "/archive"
                    "found/source.pdf"
                |> Result.defaultWith failwith
                |> fun canonical -> canonical.OwnershipKey

            let! paths = loadSavedPaths firstDb
            let ownerCount =
                paths
                |> List.choose (fun path ->
                    Database.canonicalArchivePath "/archive" path
                    |> Result.toOption)
                |> List.filter (fun path ->
                    path.OwnershipKey = candidateKey)
                |> List.length

            Assert.Equal(1, repairedCount)
            Assert.True(conflictCount >= 1)
            Assert.Equal(1, ownerCount)
            Assert.Equal(Some "shared bytes", fs.Get "/archive/found/source.pdf")
        finally
            firstDb.dispose ()
            secondDb.dispose ()

            try
                Directory.Delete(directory, true)
            with
            | :? IOException -> ()
            | :? UnauthorizedAccessException -> ()
    }

let private cursorBounds maxDocuments maxFiles =
    LegacyReclassification.createBounds maxDocuments maxFiles
    |> Result.defaultWith failwith

let private validCursor () =
    let bounds = cursorBounds 20 100

    LegacyReclassification.createRunCursor "/archive" bounds 7L
    |> Result.defaultWith failwith

let private assertInvalidCursor bounds cursor expected =
    match
        LegacyReclassification.validateRunCursor
            "/archive"
            bounds
            cursor
    with
    | Error error -> Assert.Contains(expected, error)
    | Ok() -> failwith "Expected cursor validation failure"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Candidate evidence is deterministic distinct and capped at two`` () =
    let add path candidates =
        LegacyReclassification.addCandidate
            "/archive"
            "ABCDEF"
            path
            candidates
        |> Result.defaultWith failwith

    let forward =
        []
        |> add "z/source.pdf"
        |> add "a/source.pdf"
        |> add "m/source.pdf"
        |> add "a/./source.pdf"

    let reverse =
        []
        |> add "a/./source.pdf"
        |> add "m/source.pdf"
        |> add "a/source.pdf"
        |> add "z/source.pdf"

    let forwardPaths =
        LegacyReclassification.candidatePaths "abcdef" forward

    let reversePaths =
        LegacyReclassification.candidatePaths "ABCDEF" reverse

    Assert.Equal(2, forwardPaths.Length)
    Assert.Equal<string list>(forwardPaths, reversePaths)
    Assert.Equal<string list>(
        forwardPaths
        |> List.distinct
        |> List.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right)),
        forwardPaths)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Cursor validation rejects malformed identity epoch and continuations`` () =
    let bounds = cursorBounds 20 100
    let cursor = validCursor ()

    assertInvalidCursor
        bounds
        { cursor with
            RunId = LegacyReclassification.RepairRunId "" }
        "run ID"

    assertInvalidCursor
        bounds
        { cursor with
            RunId = LegacyReclassification.RepairRunId "not-a-guid" }
        "run ID"

    assertInvalidCursor
        bounds
        { cursor with
            Epoch = LegacyReclassification.SnapshotEpoch -1L }
        "epoch"

    assertInvalidCursor
        bounds
        { cursor with
            Documents = LegacyReclassification.AfterDocument -1L }
        "Document cursor"

    assertInvalidCursor
        bounds
        { cursor with
            Archive = LegacyReclassification.AfterArchiveFile "" }
        "sort key"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Cursor validation rejects impossible phase and oversized evidence`` () =
    let bounds = cursorBounds 20 100
    let cursor = validCursor ()

    assertInvalidCursor
        bounds
        { cursor with
            Phase = LegacyReclassification.DocumentScan
            Archive = LegacyReclassification.ArchiveNotStarted }
        "requires a completed archive cursor"

    assertInvalidCursor
        bounds
        { cursor with
            Phase = LegacyReclassification.ArchiveScan
            Archive = LegacyReclassification.ArchiveCompleted }
        "cannot have a completed archive cursor"

    let oversized: LegacyReclassification.TargetCandidates =
        { Sha256 = "sha"
          Paths =
            [ { OwnershipKey = "A"; SavedPath = "a" }
              { OwnershipKey = "B"; SavedPath = "b" }
              { OwnershipKey = "C"; SavedPath = "c" } ] }

    assertInvalidCursor
        bounds
        { cursor with Candidates = [ oversized ] }
        "at most two"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Cursor validation rejects archive root and bounds mismatch`` () =
    let bounds = cursorBounds 20 100
    let cursor = validCursor ()

    match
        LegacyReclassification.validateRunCursor
            "/different-archive"
            bounds
            cursor
    with
    | Error error -> Assert.Contains("archive root", error)
    | Ok() -> failwith "Expected archive-root mismatch"

    assertInvalidCursor
        (cursorBounds 19 100)
        cursor
        "maxDocuments"

    assertInvalidCursor
        (cursorBounds 20 99)
        cursor
        "maxFiles"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Cursor validation rejects a candidate whose ownership key does not match its saved path`` () =
    let bounds = cursorBounds 20 100
    let cursor = validCursor ()

    let forged: LegacyReclassification.TargetCandidates =
        { Sha256 = "abc123"
          Paths =
            [ { OwnershipKey = "not-the-real-key"
                SavedPath = "receipts/source.pdf" } ] }

    assertInvalidCursor
        bounds
        { cursor with Candidates = [ forged ] }
        "does not match its saved_path"

[<Theory>]
[<InlineData("../escape.pdf")>]
[<InlineData("owned/../../escape.pdf")>]
[<Trait("Category", "Unit")>]
let ``Cursor validation rejects a candidate saved path that escapes the archive``
    (escapingPath: string) =
    let bounds = cursorBounds 20 100
    let cursor = validCursor ()

    let escaping: LegacyReclassification.TargetCandidates =
        { Sha256 = "abc123"
          Paths =
            [ { OwnershipKey = "irrelevant-any-key"
                SavedPath = escapingPath } ] }

    assertInvalidCursor
        bounds
        { cursor with Candidates = [ escaping ] }
        "escapes the archive directory"

let private runPageOrFail db fs bounds mode cursor =
    task {
        let! result =
            LegacyReclassification.runPage
                db fs "/archive" bounds mode cursor

        return result |> Result.defaultWith failwith
    }

let private nextCursor (page: LegacyReclassification.RunPageResult) =
    page.Cursor
    |> Option.defaultWith (fun () -> failwith "Expected continuation cursor")

let private insertDocumentRange
    (db: Algebra.Database)
    firstId
    lastId =
    task {
        let! _ =
            db.execNonQuery
                """WITH RECURSIVE ids(value) AS (
                       SELECT @first
                       UNION ALL
                       SELECT value + 1 FROM ids WHERE value < @last
                   )
                   INSERT INTO documents
                       (id, source_type, saved_path, category, sha256)
                   SELECT
                       value,
                       'manual_drop',
                       printf('missing/%06d.pdf', value),
                       'receipts',
                       printf('sha-%d', value)
                   FROM ids"""
                [ "@first", Database.boxVal firstId
                  "@last", Database.boxVal lastId ]

        return ()
    }

let private findingIds (page: LegacyReclassification.RunPageResult) =
    page.Findings |> List.map (fun finding -> finding.DocumentId)

[<Fact>]
[<Trait("Category", "Integration")>]
let ``First bounded archive page advances without findings or outcomes`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        let bounds = cursorBounds 20 1
        archiveFile fs "/archive/a/source.pdf" "target"
        archiveFile fs "/archive/b/other.pdf" "other"

        try
            do! insertLegacy
                    db "missing/source.pdf" (hashText "target")

            let! page =
                runPageOrFail
                    db fs.Fs bounds
                    LegacyReclassification.DryRun
                    None

            Assert.Equal(
                LegacyReclassification.InProgress,
                page.Stability)
            Assert.Equal(1, page.Progress.FilesHashed)
            Assert.False(page.Progress.ArchiveComplete)
            Assert.Empty(page.Findings)
            Assert.Empty(page.Outcomes)

            match (nextCursor page).Archive with
            | LegacyReclassification.AfterArchiveFile key ->
                Assert.False(String.IsNullOrWhiteSpace key)
            | state ->
                failwith $"Expected advanced archive cursor, got {state}"
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Archive completion emits evidence only on the final page`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        let bounds = cursorBounds 20 1
        archiveFile fs "/archive/a/noise.pdf" "noise"
        archiveFile fs "/archive/b/source.pdf" "target"

        try
            do! insertLegacy
                    db "missing/source.pdf" (hashText "target")

            let! first =
                runPageOrFail
                    db fs.Fs bounds
                    LegacyReclassification.DryRun
                    None

            Assert.Empty(first.Findings)
            Assert.False(first.Progress.ArchiveComplete)

            let! finalPage =
                runPageOrFail
                    db fs.Fs bounds
                    LegacyReclassification.DryRun
                    (Some(nextCursor first))

            Assert.True(finalPage.Progress.ArchiveComplete)
            Assert.Equal(
                LegacyReclassification.StablePassCompleted,
                finalPage.Stability)
            Assert.Single(finalPage.Findings) |> ignore
            Assert.Empty(finalPage.Outcomes)

            match finalPage.Findings.Head.Evidence with
            | LegacyReclassification.UniqueShaMatch path ->
                Assert.EndsWith(
                    "b/source.pdf",
                    path.Replace('\\', '/'))
            | evidence ->
                failwith $"Expected unique SHA evidence, got {evidence}"
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Archive continuation progresses beyond ten thousand files`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        let bounds = cursorBounds 20 10000
        let reads = ResizeArray<string>()

        fs.Fs.createDirectory "/archive"
        fs.Fs.createDirectory "/archive/noise"

        seq { 1 .. 10000 }
        |> Seq.iter (fun index ->
            fs.Put
                $"/archive/noise/{index:D5}.bin"
                "noise")

        fs.Put "/archive/zz-target.bin" "target"

        let countingFs =
            { fs.Fs with
                readAllBytes =
                    fun path ->
                        reads.Add(fs.Norm path)
                        fs.Fs.readAllBytes path }

        try
            do! insertLegacy
                    db "missing/target.bin" (hashText "target")

            let! first =
                runPageOrFail
                    db countingFs bounds
                    LegacyReclassification.DryRun
                    None

            Assert.Equal(10000, first.Progress.FilesHashed)
            Assert.False(first.Progress.ArchiveComplete)
            Assert.Empty(first.Findings)
            Assert.Empty(first.Outcomes)

            let firstCursor = nextCursor first

            match firstCursor.Archive with
            | LegacyReclassification.AfterArchiveFile key ->
                Assert.False(String.IsNullOrWhiteSpace key)
            | state ->
                failwith $"Expected archive continuation, got {state}"

            let! finalPage =
                runPageOrFail
                    db countingFs bounds
                    LegacyReclassification.DryRun
                    (Some firstCursor)

            Assert.Equal(1, finalPage.Progress.FilesHashed)
            Assert.True(finalPage.Progress.ArchiveComplete)
            Assert.Equal(
                LegacyReclassification.StablePassCompleted,
                finalPage.Stability)
            Assert.Single(finalPage.Findings) |> ignore
            Assert.Empty(finalPage.Outcomes)
            Assert.Equal(10001, reads.Count)
            Assert.Equal(10001, reads |> Seq.distinct |> Seq.length)

            match finalPage.Findings.Head.Evidence with
            | LegacyReclassification.UniqueShaMatch path ->
                Assert.EndsWith(
                    "zz-target.bin",
                    path.Replace('\\', '/'))
            | evidence ->
                failwith $"Expected final unique evidence, got {evidence}"
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Document continuation evaluates more than one thousand IDs once`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        let bounds = cursorBounds 1000 100
        fs.Fs.createDirectory "/archive"

        try
            do! insertDocumentRange db 1L 1005L

            let! first =
                runPageOrFail
                    db fs.Fs bounds
                    LegacyReclassification.DryRun
                    None

            Assert.Equal(1000, first.Progress.DocumentsScanned)
            Assert.Equal(1000, first.Findings.Length)
            Assert.Equal(
                LegacyReclassification.InProgress,
                first.Stability)

            let firstIds = findingIds first
            let cursor = nextCursor first

            match cursor.Documents with
            | LegacyReclassification.AfterDocument 1000L -> ()
            | continuation ->
                failwith $"Expected cursor after 1000, got {continuation}"

            let! finalPage =
                runPageOrFail
                    db fs.Fs bounds
                    LegacyReclassification.DryRun
                    (Some cursor)

            Assert.Equal(5, finalPage.Progress.DocumentsScanned)
            Assert.Equal(
                LegacyReclassification.StablePassCompleted,
                finalPage.Stability)

            let allIds = firstIds @ findingIds finalPage
            Assert.Equal(1005, allIds.Length)
            Assert.Equal(1005, allIds |> List.distinct |> List.length)
            Assert.Equal<int64 list>([ 1L .. 1005L ], allIds)
            Assert.Empty(first.Outcomes)
            Assert.Empty(finalPage.Outcomes)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Behind cursor insertion forces restart and is eventually covered`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        let bounds = cursorBounds 1000 100
        fs.Fs.createDirectory "/archive"

        try
            do! insertDocumentRange db 1001L 2001L

            let! first =
                runPageOrFail
                    db fs.Fs bounds
                    LegacyReclassification.DryRun
                    None

            let staleCursor = nextCursor first

            match staleCursor.Documents with
            | LegacyReclassification.AfterDocument 2000L -> ()
            | continuation ->
                failwith $"Expected cursor after 2000, got {continuation}"

            let! _ =
                db.execNonQuery
                    """INSERT INTO documents
                       (id, source_type, saved_path, category, sha256)
                       VALUES
                       (500, 'manual_drop', 'missing/000500.pdf',
                        'receipts', 'sha-500')"""
                    []

            let! changed =
                runPageOrFail
                    db fs.Fs bounds
                    LegacyReclassification.DryRun
                    (Some staleCursor)

            let restart =
                match changed.Stability with
                | LegacyReclassification.SnapshotChanged cursor -> cursor
                | state ->
                    failwith $"Expected snapshot restart, got {state}"

            Assert.Empty(changed.Findings)
            Assert.Empty(changed.Outcomes)
            Assert.Equal(
                LegacyReclassification.BeforeFirstDocument,
                restart.Documents)

            let! restartedFirst =
                runPageOrFail
                    db fs.Fs bounds
                    LegacyReclassification.DryRun
                    (Some restart)

            let! restartedFinal =
                runPageOrFail
                    db fs.Fs bounds
                    LegacyReclassification.DryRun
                    (Some(nextCursor restartedFirst))

            let stableIds =
                findingIds restartedFirst
                @ findingIds restartedFinal

            Assert.Contains(500L, stableIds)
            Assert.Equal(1002, stableIds.Length)
            Assert.Equal(1002, stableIds |> List.distinct |> List.length)
            Assert.Equal(
                LegacyReclassification.StablePassCompleted,
                restartedFinal.Stability)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Retrying unchanged archive cursor is deterministic and idempotent`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        let bounds = cursorBounds 20 1
        archiveFile fs "/archive/a.bin" "a"
        archiveFile fs "/archive/b.bin" "b"
        archiveFile fs "/archive/c.bin" "target"

        try
            do! insertLegacy
                    db "missing/source.bin" (hashText "target")

            let! first =
                runPageOrFail
                    db fs.Fs bounds
                    LegacyReclassification.DryRun
                    None

            let cursor = nextCursor first

            let! retryOne =
                runPageOrFail
                    db fs.Fs bounds
                    LegacyReclassification.DryRun
                    (Some cursor)

            let! retryTwo =
                runPageOrFail
                    db fs.Fs bounds
                    LegacyReclassification.DryRun
                    (Some cursor)

            Assert.Equal(retryOne.Cursor, retryTwo.Cursor)
            Assert.Equal(retryOne.Progress, retryTwo.Progress)
            Assert.Equal(retryOne.Stability, retryTwo.Stability)
            Assert.Equal<LegacyReclassification.Finding list>(
                retryOne.Findings,
                retryTwo.Findings)
            Assert.Empty(retryOne.Outcomes)
            Assert.Empty(retryTwo.Outcomes)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Apply repairs each proven path once and accounts for own epoch`` () =
    task {
        let db = TestHelpers.createDb ()
        let fs = TestHelpers.memFs ()
        let bounds = cursorBounds 20 1
        archiveFile fs "/archive/found/first.pdf" "first"
        archiveFile fs "/archive/found/second.pdf" "second"

        try
            do! insertLegacy
                    db "missing/first.pdf" (hashText "first")
            do! insertLegacy
                    db "missing/second.pdf" (hashText "second")

            let! first =
                runPageOrFail
                    db fs.Fs bounds
                    LegacyReclassification.Apply
                    None

            Assert.False(first.Progress.ArchiveComplete)
            Assert.Empty(first.Findings)
            Assert.Empty(first.Outcomes)

            let! finalPage =
                runPageOrFail
                    db fs.Fs bounds
                    LegacyReclassification.Apply
                    (Some(nextCursor first))

            let repaired =
                finalPage.Outcomes
                |> List.choose (fun outcome ->
                    match outcome.Disposition with
                    | LegacyReclassification.Repaired path ->
                        Some(outcome.DocumentId, path)
                    | _ -> None)

            Assert.Equal(2, repaired.Length)
            Assert.Equal(2, repaired |> List.map fst |> List.distinct |> List.length)
            Assert.Equal(
                LegacyReclassification.StablePassCompleted,
                finalPage.Stability)

            let! paths = loadSavedPaths db
            Assert.All(
                paths,
                fun path ->
                    Assert.StartsWith("found", path.Replace('\\', '/')))

            let! retry =
                runPageOrFail
                    db fs.Fs bounds
                    LegacyReclassification.Apply
                    (Some(nextCursor first))

            match retry.Stability with
            | LegacyReclassification.SnapshotChanged _ -> ()
            | state ->
                failwith $"Expected stale-cursor restart, got {state}"

            Assert.Empty(retry.Outcomes)
        finally
            db.dispose ()
    }
