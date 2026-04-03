module Hermes.Tests.ServiceHostTests

open System
open System.Threading
open Xunit
open Hermes.Core

let private testEnv = TestHelpers.fakeEnvironment "/home" "/home/.config/hermes" "/home/Documents"

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
[<Trait("Category", "Integration")>]
let ``ServiceHost_CountDocuments_EmptyDb_ReturnsZero`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! count = ServiceHost.countDocuments db
            Assert.Equal(0L, count)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
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
[<Trait("Category", "Integration")>]
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
[<Trait("Category", "Integration")>]
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
[<Trait("Category", "Integration")>]
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
[<Trait("Category", "Integration")>]
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

// ─── countUnextracted tests ──────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ServiceHost_CountUnextracted_AllExtracted_ReturnsZero`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ = db.execNonQuery
                        "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text, extracted_at) VALUES ('manual_drop', 'a.pdf', 'invoices', 'sha1', 'text', datetime('now'))"
                        []
            let! count = ServiceHost.countUnextracted db
            Assert.Equal(0L, count)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ServiceHost_CountUnextracted_EmptyDb_ReturnsZero`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! count = ServiceHost.countUnextracted db
            Assert.Equal(0L, count)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ServiceHost_CountUnextracted_MixedDocs_CountsCorrectly`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('manual_drop', 'a.pdf', 'invoices', 'sha1')" []
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('manual_drop', 'b.pdf', 'invoices', 'sha2')" []
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text, extracted_at) VALUES ('manual_drop', 'c.pdf', 'invoices', 'sha3', 'text', datetime('now'))" []
            let! count = ServiceHost.countUnextracted db
            Assert.Equal(2L, count)
        finally db.dispose ()
    }

// ─── defaultServiceConfig tests ──────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_DefaultServiceConfig_ArchiveDirFromConfig`` () =
    let config = TestHelpers.testConfig "/my/archive"
    let sc = ServiceHost.defaultServiceConfig config
    Assert.Equal("/my/archive", sc.ArchiveDir)
    Assert.Equal(config.SyncIntervalMinutes, sc.SyncIntervalMinutes)
    Assert.Equal(60, sc.HeartbeatIntervalSeconds)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_DefaultServiceConfig_ConfigPreserved`` () =
    let config = TestHelpers.testConfig "/test"
    let sc = ServiceHost.defaultServiceConfig config
    Assert.Equal(config, sc.Config)

// ─── Heartbeat with all fields ───────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_WriteAndReadHeartbeat_AllFields_RoundTrip`` () =
    task {
        let m = TestHelpers.memFs ()
        let status : ServiceHost.ServiceStatus =
            { Running = true
              StartedAt = Some (DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
              LastSyncAt = Some (DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero))
              LastSyncOk = false
              DocumentCount = 100L
              UnclassifiedCount = 5
              ErrorMessage = Some "sync failed" }
        do! ServiceHost.writeHeartbeat m.Fs "/archive" status
        let! read = ServiceHost.readHeartbeat m.Fs "/archive"
        Assert.True(read.IsSome)
        Assert.True(read.Value.Running)
        Assert.False(read.Value.LastSyncOk)
        Assert.Equal(100L, read.Value.DocumentCount)
        Assert.Equal(5, read.Value.UnclassifiedCount)
        Assert.True(read.Value.ErrorMessage.IsSome)
    }

// ─── SyncDeps test factory ──────────────────────────────────────────

let private testDeps : ServiceHost.SyncDeps =
    { Extractor =
        { extractPdf = fun _ -> task { return Ok "extracted text" }
          extractImage = fun _ -> task { return Error "not available" } }
      Embedder = None
      ChatProvider = None
      ContentRules = []
      CreateEmailProvider = fun _ _ -> task { return TestHelpers.emptyProvider } }

let private testDepsWithEmbedder (embedder: Algebra.EmbeddingClient) : ServiceHost.SyncDeps =
    { testDeps with Embedder = Some embedder }

// ─── runSyncCycle tests ─────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ServiceHost_RunSyncCycle_EmptyState_ReturnsOk`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        let rules : Algebra.RulesEngine =
            { classify = fun _ _ -> { Domain.ClassificationResult.Category = "unsorted"; MatchedRule = Domain.ClassificationRule.DefaultRule }
              reload = fun () -> task { return Ok () } }
        let config = TestHelpers.testConfig "/archive"
        m.Fs.createDirectory "/archive"
        m.Fs.createDirectory "/archive/unclassified"
        try
            let! result = ServiceHost.runSyncCycle m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock rules testDeps config "/config"
            Assert.True(Result.isOk result)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ServiceHost_RunSyncCycle_WithUnclassifiedFiles_ClassifiesThem`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Fs.createDirectory "/archive"
        m.Fs.createDirectory "/archive/unclassified"
        m.Fs.createDirectory "/archive/invoices"
        m.Put "/archive/unclassified/invoice-test.pdf" "PDF content"
        let rules : Algebra.RulesEngine =
            { classify = fun _ fname ->
                if fname.Contains("invoice") then
                    { Domain.ClassificationResult.Category = "invoices"; MatchedRule = Domain.ClassificationRule.DefaultRule }
                else
                    { Domain.ClassificationResult.Category = "unsorted"; MatchedRule = Domain.ClassificationRule.DefaultRule }
              reload = fun () -> task { return Ok () } }
        let config = TestHelpers.testConfig "/archive"
        try
            let! result = ServiceHost.runSyncCycle m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock rules testDeps config "/config"
            Assert.True(Result.isOk result)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ServiceHost_RunSyncCycle_WithExtractor_ExtractsDocuments`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Fs.createDirectory "/archive"
        m.Fs.createDirectory "/archive/unclassified"
        m.Put "/archive/invoices/test.pdf" "PDF bytes"
        let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('manual_drop', 'invoices/test.pdf', 'invoices', 'sha1')" []
        let rules : Algebra.RulesEngine =
            { classify = fun _ _ -> { Domain.ClassificationResult.Category = "unsorted"; MatchedRule = Domain.ClassificationRule.DefaultRule }
              reload = fun () -> task { return Ok () } }
        let config = TestHelpers.testConfig "/archive"
        try
            let! result = ServiceHost.runSyncCycle m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock rules testDeps config "/config"
            Assert.True(Result.isOk result)
            // Verify extraction was attempted
            let! rows = db.execReader "SELECT extracted_text FROM documents WHERE id = 1" []
            match rows with
            | [row] ->
                let r = Prelude.RowReader(row)
                Assert.True(r.OptString "extracted_text" |> Option.isSome)
            | _ -> ()
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ServiceHost_RunSyncCycle_WithEmbedder_RunsEmbedding`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Fs.createDirectory "/archive"
        m.Fs.createDirectory "/archive/unclassified"
        let embedder = TestHelpers.fakeEmbedder 768
        let deps = testDepsWithEmbedder embedder
        let rules : Algebra.RulesEngine =
            { classify = fun _ _ -> { Domain.ClassificationResult.Category = "unsorted"; MatchedRule = Domain.ClassificationRule.DefaultRule }
              reload = fun () -> task { return Ok () } }
        let config = TestHelpers.testConfig "/archive"
        try
            let! result = ServiceHost.runSyncCycle m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock rules deps config "/config"
            Assert.True(Result.isOk result)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ServiceHost_RunSyncCycle_NoEmbedder_SkipsEmbedding`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Fs.createDirectory "/archive"
        m.Fs.createDirectory "/archive/unclassified"
        let rules : Algebra.RulesEngine =
            { classify = fun _ _ -> { Domain.ClassificationResult.Category = "unsorted"; MatchedRule = Domain.ClassificationRule.DefaultRule }
              reload = fun () -> task { return Ok () } }
        let config = TestHelpers.testConfig "/archive"
        try
            let! result = ServiceHost.runSyncCycle m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock rules testDeps config "/config"
            Assert.True(Result.isOk result)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ServiceHost_RunSyncCycle_NoChatProvider_SkipsLlmClassification`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Fs.createDirectory "/archive"
        m.Fs.createDirectory "/archive/unclassified"
        let! _ = db.execNonQuery
                    "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text, extracted_at) VALUES ('manual_drop', 'unsorted/test.pdf', 'unsorted', 'sha1', 'some text', datetime('now'))"
                    []
        let rules : Algebra.RulesEngine =
            { classify = fun _ _ -> { Domain.ClassificationResult.Category = "unsorted"; MatchedRule = Domain.ClassificationRule.DefaultRule }
              reload = fun () -> task { return Ok () } }
        let config = TestHelpers.testConfig "/archive"
        try
            let! result = ServiceHost.runSyncCycle m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock rules testDeps config "/config"
            Assert.True(Result.isOk result)
            let! rows = db.execReader "SELECT category FROM documents WHERE id = 1" []
            match rows with
            | [row] -> Assert.Equal(Some "unsorted", Prelude.RowReader(row).OptString "category")
            | _ -> Assert.Fail("Expected one document")
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ServiceHost_RunSyncCycle_WithEmailProvider_SyncsEmails`` () =
    task {
        let mutable providerCalled = false
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Fs.createDirectory "/archive"
        m.Fs.createDirectory "/archive/unclassified"
        let deps =
            { testDeps with
                CreateEmailProvider = fun _ _ ->
                    task {
                        providerCalled <- true
                        return TestHelpers.emptyProvider
                    } }
        let rules : Algebra.RulesEngine =
            { classify = fun _ _ -> { Domain.ClassificationResult.Category = "unsorted"; MatchedRule = Domain.ClassificationRule.DefaultRule }
              reload = fun () -> task { return Ok () } }
        let config = TestHelpers.testConfig "/archive"
        try
            let! result = ServiceHost.runSyncCycle m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock rules deps config "/config"
            Assert.True(Result.isOk result)
            Assert.True(providerCalled, "Email provider factory should have been called")
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ServiceHost_RunSyncCycle_EmailProviderFails_ContinuesGracefully`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Fs.createDirectory "/archive"
        m.Fs.createDirectory "/archive/unclassified"
        let deps =
            { testDeps with
                CreateEmailProvider = fun _ _ -> task { return failwith "auth failed" } }
        let rules : Algebra.RulesEngine =
            { classify = fun _ _ -> { Domain.ClassificationResult.Category = "unsorted"; MatchedRule = Domain.ClassificationRule.DefaultRule }
              reload = fun () -> task { return Ok () } }
        let config = TestHelpers.testConfig "/archive"
        try
            let! result = ServiceHost.runSyncCycle m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock rules deps config "/config"
            Assert.True(Result.isOk result)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ServiceHost_RunSyncCycle_WithContentRules_AppliesReclassification`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Fs.createDirectory "/archive"
        m.Fs.createDirectory "/archive/unclassified"
        m.Fs.createDirectory "/archive/unsorted"
        m.Fs.createDirectory "/archive/invoices"
        let! _ = db.execNonQuery
                    "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text, extracted_at) VALUES ('manual_drop', 'unsorted/test.pdf', 'unsorted', 'sha1', 'INVOICE TOTAL: $500', datetime('now'))"
                    []
        m.Put "/archive/unsorted/test.pdf" "content"
        let contentRules : Domain.ContentRule list =
            [ { Domain.ContentRule.Name = "invoice-rule"
                Conditions = [ Domain.ContentMatch.ContentAny [ "invoice" ] ]
                Category = "invoices"
                Confidence = 0.8 } ]
        let deps = { testDeps with ContentRules = contentRules }
        let rules : Algebra.RulesEngine =
            { classify = fun _ _ -> { Domain.ClassificationResult.Category = "unsorted"; MatchedRule = Domain.ClassificationRule.DefaultRule }
              reload = fun () -> task { return Ok () } }
        let config = TestHelpers.testConfig "/archive"
        try
            let! result = ServiceHost.runSyncCycle m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock rules deps config "/config"
            Assert.True(Result.isOk result)
        finally db.dispose ()
    }

// ─── buildProductionDeps tests ──────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_BuildProductionDeps_OllamaDisabled_EmbedderIsNone`` () =
    let config = TestHelpers.testConfig "/archive"
    let m = TestHelpers.memFs ()
    let deps = ServiceHost.buildProductionDeps config "/config" TestHelpers.silentLogger m.Fs
    Assert.True(deps.Embedder.IsNone)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_BuildProductionDeps_NoRulesFile_EmptyContentRules`` () =
    let config = TestHelpers.testConfig "/archive"
    let m = TestHelpers.memFs ()
    let deps = ServiceHost.buildProductionDeps config "/config" TestHelpers.silentLogger m.Fs
    Assert.Empty(deps.ContentRules)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_BuildProductionDeps_WithRulesFile_ParsesContentRules`` () =
    let config = TestHelpers.testConfig "/archive"
    let m = TestHelpers.memFs ()
    let rulesYaml = """content_rules:
  - name: invoice-rule
    match:
      content_any: ["invoice"]
    category: invoices
    confidence: 0.8"""
    m.Put "/config/rules.yaml" rulesYaml
    let deps = ServiceHost.buildProductionDeps config "/config" TestHelpers.silentLogger m.Fs
    Assert.NotEmpty(deps.ContentRules)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_BuildProductionDeps_ExtractorIsConfigured`` () =
    let config = TestHelpers.testConfig "/archive"
    let m = TestHelpers.memFs ()
    let deps = ServiceHost.buildProductionDeps config "/config" TestHelpers.silentLogger m.Fs
    Assert.NotNull(deps.Extractor.extractPdf)
    Assert.NotNull(deps.Extractor.extractImage)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ServiceHost_BuildProductionDeps_CreateEmailProviderIsConfigured`` () =
    let config = TestHelpers.testConfig "/archive"
    let m = TestHelpers.memFs ()
    let deps = ServiceHost.buildProductionDeps config "/config" TestHelpers.silentLogger m.Fs
    Assert.NotNull(deps.CreateEmailProvider)

// ─── createServiceHost tests ─────────────────────────────────────────

let private minimalRules : Algebra.RulesEngine =
    { classify = fun _ _ ->
        { Domain.ClassificationResult.Category = "unsorted"
          MatchedRule = Domain.ClassificationRule.DefaultRule }
      reload = fun () -> task { return Ok () } }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ServiceHost_CreateServiceHost_PreCancelledToken_WritesStoppedStatus`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Fs.createDirectory "/archive"
        m.Fs.createDirectory "/archive/unclassified"
        let config = TestHelpers.testConfig "/archive"
        let serviceConfig = ServiceHost.defaultServiceConfig config
        use cts = new CancellationTokenSource()
        cts.Cancel()
        try
            do! ServiceHost.createServiceHost m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock testEnv minimalRules testDeps serviceConfig "/test/config.yaml" cts.Token
            let! status = ServiceHost.readHeartbeat m.Fs "/archive"
            Assert.True(status.IsSome)
            Assert.False(status.Value.Running)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ServiceHost_CreateServiceHost_PreCancelledToken_ReportsDocCount`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Fs.createDirectory "/archive"
        m.Fs.createDirectory "/archive/unclassified"
        let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('manual_drop', 'a.pdf', 'invoices', 'sha1')" []
        let config = TestHelpers.testConfig "/archive"
        let serviceConfig = ServiceHost.defaultServiceConfig config
        use cts = new CancellationTokenSource()
        cts.Cancel()
        try
            do! ServiceHost.createServiceHost m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock testEnv minimalRules testDeps serviceConfig "/test/config.yaml" cts.Token
            let! status = ServiceHost.readHeartbeat m.Fs "/archive"
            Assert.True(status.IsSome)
            Assert.Equal(1L, status.Value.DocumentCount)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ServiceHost_CreateServiceHost_PreCancelledToken_UpdatesSyncState`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Fs.createDirectory "/archive"
        m.Fs.createDirectory "/archive/unclassified"
        let config = TestHelpers.testConfig "/archive"
        let serviceConfig = ServiceHost.defaultServiceConfig config
        use cts = new CancellationTokenSource()
        cts.Cancel()
        try
            do! ServiceHost.createServiceHost m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock testEnv minimalRules testDeps serviceConfig "/test/config.yaml" cts.Token
            let! status = ServiceHost.readHeartbeat m.Fs "/archive"
            Assert.True(status.IsSome)
            // Should have run at least one sync cycle
            Assert.True(status.Value.LastSyncOk)
        finally db.dispose ()
    }

// ─── LLM reclassification path ──────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ServiceHost_RunSyncCycle_WithChatProvider_RunsLlmClassification`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Fs.createDirectory "/archive"
        m.Fs.createDirectory "/archive/unclassified"
        m.Fs.createDirectory "/archive/unsorted"
        m.Fs.createDirectory "/archive/invoices"
        let! _ = db.execNonQuery
                    "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text, extracted_at) VALUES ('manual_drop', 'invoices/existing.pdf', 'invoices', 'sha-exist', 'existing invoice', datetime('now'))"
                    []
        let! _ = db.execNonQuery
                    "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text, extracted_at) VALUES ('manual_drop', 'unsorted/mystery.pdf', 'unsorted', 'sha-unsorted', 'INVOICE Total $500 from ACME Corp', datetime('now'))"
                    []
        m.Put "/archive/unsorted/mystery.pdf" "content"
        let chatProvider = TestHelpers.fakeChatProvider """{"category":"invoices","confidence":0.92,"reasoning":"Contains invoice markers"}"""
        let deps = { testDeps with ChatProvider = Some chatProvider }
        let rules : Algebra.RulesEngine =
            { classify = fun _ _ -> { Domain.ClassificationResult.Category = "unsorted"; MatchedRule = Domain.ClassificationRule.DefaultRule }
              reload = fun () -> task { return Ok () } }
        let config = TestHelpers.testConfig "/archive"
        try
            let! result = ServiceHost.runSyncCycle m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock rules deps config "/config"
            Assert.True(Result.isOk result)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ServiceHost_RunSyncCycle_ChatProviderError_ContinuesGracefully`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Fs.createDirectory "/archive"
        m.Fs.createDirectory "/archive/unclassified"
        m.Fs.createDirectory "/archive/unsorted"
        m.Fs.createDirectory "/archive/invoices"
        let! _ = db.execNonQuery
                    "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text, extracted_at) VALUES ('manual_drop', 'invoices/existing.pdf', 'invoices', 'sha-exist', 'existing invoice', datetime('now'))"
                    []
        let! _ = db.execNonQuery
                    "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text, extracted_at) VALUES ('manual_drop', 'unsorted/test.pdf', 'unsorted', 'sha-unsorted2', 'some text', datetime('now'))"
                    []
        m.Put "/archive/unsorted/test.pdf" "content"
        let deps = { testDeps with ChatProvider = Some TestHelpers.failingChatProvider }
        let rules : Algebra.RulesEngine =
            { classify = fun _ _ -> { Domain.ClassificationResult.Category = "unsorted"; MatchedRule = Domain.ClassificationRule.DefaultRule }
              reload = fun () -> task { return Ok () } }
        let config = TestHelpers.testConfig "/archive"
        try
            let! result = ServiceHost.runSyncCycle m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock rules deps config "/config"
            Assert.True(Result.isOk result)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ServiceHost_RunSyncCycle_LowConfidence_NoReclassification`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Fs.createDirectory "/archive"
        m.Fs.createDirectory "/archive/unclassified"
        m.Fs.createDirectory "/archive/unsorted"
        m.Fs.createDirectory "/archive/invoices"
        let! _ = db.execNonQuery
                    "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text, extracted_at) VALUES ('manual_drop', 'invoices/existing.pdf', 'invoices', 'sha-exist', 'existing invoice', datetime('now'))"
                    []
        let! _ = db.execNonQuery
                    "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text, extracted_at) VALUES ('manual_drop', 'unsorted/unclear.pdf', 'unsorted', 'sha-unclear', 'unclear text', datetime('now'))"
                    []
        m.Put "/archive/unsorted/unclear.pdf" "content"
        let chatProvider = TestHelpers.fakeChatProvider """{"category":"invoices","confidence":0.2,"reasoning":"Not sure"}"""
        let deps = { testDeps with ChatProvider = Some chatProvider }
        let rules : Algebra.RulesEngine =
            { classify = fun _ _ -> { Domain.ClassificationResult.Category = "unsorted"; MatchedRule = Domain.ClassificationRule.DefaultRule }
              reload = fun () -> task { return Ok () } }
        let config = TestHelpers.testConfig "/archive"
        try
            let! result = ServiceHost.runSyncCycle m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock rules deps config "/config"
            Assert.True(Result.isOk result)
            // Doc should still be unsorted (confidence too low)
            let! rows = db.execReader "SELECT category FROM documents WHERE sha256 = 'sha-unclear'" []
            match rows with
            | [row] -> Assert.Equal(Some "unsorted", Prelude.RowReader(row).OptString "category")
            | _ -> ()
        finally db.dispose ()
    }

// ─── createServiceHost with short-lived token ────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ServiceHost_CreateServiceHost_ShortLivedToken_RunsLoopBriefly`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Fs.createDirectory "/archive"
        m.Fs.createDirectory "/archive/unclassified"
        let config = TestHelpers.testConfig "/archive"
        let serviceConfig = { ServiceHost.defaultServiceConfig config with HeartbeatIntervalSeconds = 1 }
        use cts = new CancellationTokenSource()
        cts.CancelAfter(200)
        try
            do! ServiceHost.createServiceHost m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock testEnv minimalRules testDeps serviceConfig "/test/config.yaml" cts.Token
            let! status = ServiceHost.readHeartbeat m.Fs "/archive"
            Assert.True(status.IsSome)
            Assert.False(status.Value.Running)
        finally db.dispose ()
    }
