module Hermes.Tests.ServiceHostTests

open System
open Xunit
open Hermes.Core

// ─── Heartbeat ───────────────────────────────────────────────────────

let private sampleStatus running : ServiceHost.ServiceStatus =
    { Running = running
      StartedAt = Some (DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero))
      LastSyncAt = Some (DateTimeOffset(2026, 3, 15, 10, 30, 0, TimeSpan.Zero))
      LastSyncOk = true; DocumentCount = 42L; UnclassifiedCount = 3; ErrorMessage = None }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_WriteAndReadHeartbeat_RoundTrips`` () =
    task {
        let m = TestHelpers.memFs ()
        do! ServiceHost.writeHeartbeat m.Fs "/archive" (sampleStatus true)
        let! read = ServiceHost.readHeartbeat m.Fs "/archive"
        Assert.True(read.IsSome)
        Assert.True(read.Value.Running)
        Assert.Equal(42L, read.Value.DocumentCount)
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_WriteHeartbeat_StoppedState`` () =
    task {
        let m = TestHelpers.memFs ()
        do! ServiceHost.writeHeartbeat m.Fs "/archive" (sampleStatus false)
        let! read = ServiceHost.readHeartbeat m.Fs "/archive"
        Assert.False(read.Value.Running)
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_ReadHeartbeat_NoFile_ReturnsNone`` () =
    task {
        let! result = ServiceHost.readHeartbeat (TestHelpers.memFs().Fs) "/nonexistent"
        Assert.True(result.IsNone)
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_ReadHeartbeat_InvalidJson_ReturnsNone`` () =
    task {
        let m = TestHelpers.memFs ()
        m.Put (ServiceHost.statusFilePath "/archive") "not valid json{{"
        let! result = ServiceHost.readHeartbeat m.Fs "/archive"
        Assert.True(result.IsNone)
    }

// ─── Backlog detection ───────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_CountUnclassified_EmptyDir_ReturnsZero`` () =
    Assert.Equal(0, ServiceHost.countUnclassified (TestHelpers.memFs().Fs) "/archive")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_CountUnclassified_WithFiles_CountsCorrectly`` () =
    let m = TestHelpers.memFs ()
    m.Fs.createDirectory "/archive/unclassified"
    m.Put "/archive/unclassified/doc1.pdf" "content"
    m.Put "/archive/unclassified/doc2.pdf" "content"
    m.Put "/archive/unclassified/doc1.pdf.meta.json" "{}"
    Assert.Equal(2, ServiceHost.countUnclassified m.Fs "/archive")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_CountDocuments_EmptyDb_ReturnsZero`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! count = ServiceHost.countDocuments db
            Assert.Equal(0L, count)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_CountDocuments_WithDocs_ReturnsCount`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('manual_drop', 'a.pdf', 'invoices', 'sha1')" []
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('manual_drop', 'b.pdf', 'invoices', 'sha2')" []
            let! count = ServiceHost.countDocuments db
            Assert.Equal(2L, count)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_CountUnextracted_ReturnsCorrect`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('manual_drop', 'a.pdf', 'invoices', 'sha1')" []
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text, extracted_at) VALUES ('manual_drop', 'b.pdf', 'invoices', 'sha2', 'text', datetime('now'))" []
            let! count = ServiceHost.countUnextracted db
            Assert.Equal(1L, count)
        finally db.dispose ()
    }

// ─── Sync trigger ────────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ServiceHost_RequestSync_CreatesFile`` () =
    let dir = IO.Path.Combine(IO.Path.GetTempPath(), $"hermes-test-{Guid.NewGuid():N}")
    IO.Directory.CreateDirectory(dir) |> ignore
    try
        ServiceHost.requestSync dir
        Assert.True(IO.File.Exists(IO.Path.Combine(dir, "hermes-sync-now")))
    finally try IO.Directory.Delete(dir, true) with _ -> ()

// ─── Config defaults ─────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_DefaultServiceConfig_HasSensibleDefaults`` () =
    let config = TestHelpers.testConfig "/archive"
    let sc = ServiceHost.defaultServiceConfig config
    Assert.True(sc.SyncIntervalMinutes > 0)
    Assert.True(sc.HeartbeatIntervalSeconds > 0)
    Assert.Equal("/archive", sc.ArchiveDir)

// ─── StatusFilePath ──────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_StatusFilePath_CombinesCorrectly`` () =
    let path = ServiceHost.statusFilePath "/my/archive"
    Assert.Contains("hermes-status.json", path)

// ─── classifyUnclassified (via runSyncCycle) ─────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_ClassifyUnclassified_ProcessesFiles`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Fs.createDirectory "/archive/unclassified"
        m.Put "/archive/unclassified/invoice-test.pdf" "PDF content"
        let rules : Algebra.RulesEngine =
            { classify = fun _ fname ->
                { Domain.ClassificationResult.Category = "invoices"
                  MatchedRule = Domain.ClassificationRule.DefaultRule }
              reload = fun () -> task { return Ok () } }
        try
            // classifyUnclassified is private, test via runSyncCycle
            // But we can verify the classifier works by checking docs table after
            let! _ = Classifier.processFile m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock rules "/archive" "/archive/unclassified/invoice-test.pdf"
            let! count = ServiceHost.countDocuments db
            Assert.True(count > 0L)
        finally db.dispose ()
    }

// ─── Extraction step ─────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_Extraction_ProcessesUnextractedDocs`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Put "/archive/invoices/test.pdf" "PDF content"
        let extractor : Algebra.TextExtractor =
            { extractPdf = fun _ -> task { return Ok "Extracted text $100 ABN 12345678901" }
              extractImage = fun _ -> task { return Error "no vision" } }
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('manual_drop', 'invoices/test.pdf', 'invoices', 'sha1')" []
            let! (success, _) = Extraction.extractBatch m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock extractor "/archive" None false 10
            Assert.True(success > 0)
        finally db.dispose ()
    }

// ─── Reminders step ──────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_EvaluateReminders_CreatesRemindersForBills`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ = db.execNonQuery
                        "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_amount, extracted_date, extracted_at) VALUES ('manual_drop', 'inv.pdf', 'invoices', 'sha1', 500.0, '2026-04-10', datetime('now'))"
                        []
            let now = DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero)
            let! created = Reminders.evaluateNewDocuments db TestHelpers.silentLogger now
            Assert.True(created > 0)
            let! summary = Reminders.getSummary db now
            Assert.True(summary.TotalActiveAmount > 0m)
        finally db.dispose ()
    }
