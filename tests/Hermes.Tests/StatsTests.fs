module Hermes.Tests.StatsTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open Hermes.Core

// ─── getIndexStats ───────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Stats_GetIndexStats_EmptyDb_ReturnsZeros`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! stats = Stats.getIndexStats db (TestHelpers.memFs().Fs) ""
            Assert.Equal(0L, stats.DocumentCount)
            Assert.Equal(0L, stats.ExtractedCount)
            Assert.Equal(0L, stats.EmbeddedCount)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Stats_GetIndexStats_WithDocuments_ReturnsCorrectCounts`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ = db.execNonQuery
                        "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text, extracted_at) VALUES ('manual_drop', 'a.pdf', 'invoices', 'aaa', 'text', datetime('now'))"
                        []
            let! _ = db.execNonQuery
                        "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('manual_drop', 'b.pdf', 'invoices', 'bbb')"
                        []
            let! stats = Stats.getIndexStats db (TestHelpers.memFs().Fs) ""
            Assert.Equal(2L, stats.DocumentCount)
            Assert.Equal(1L, stats.ExtractedCount)
            Assert.Equal(0L, stats.EmbeddedCount)
        finally db.dispose ()
    }

// ─── getCategoryCounts ───────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Stats_GetCategoryCounts_NonexistentDir_ReturnsEmpty`` () =
    let counts = Stats.getCategoryCounts (TestHelpers.memFs().Fs) "/nonexistent/dir"
    Assert.Empty(counts)

// ─── getAccountStats ─────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Stats_GetAccountStats_EmptyDb_ReturnsEmpty`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! stats = Stats.getAccountStats db
            Assert.Empty(stats)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Stats_GetAccountStats_WithSyncState_ReturnsAccounts`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ = db.execNonQuery
                        "INSERT INTO sync_state (account, last_sync_at, message_count) VALUES ('test@gmail.com', '2026-03-15T10:00:00Z', 42)"
                        []
            let! stats = Stats.getAccountStats db
            Assert.Equal(1, stats.Length)
            Assert.Equal("test@gmail.com", stats.[0].Label)
            Assert.True(stats.[0].LastSyncAt.IsSome)
        finally db.dispose ()
    }

// ─── Additional stats tests ──────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Stats_GetIndexStats_WithExtractedAndEmbedded_CountsCorrectly`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('manual_drop', 'a.pdf', 'invoices', 'sha1')" []
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text, extracted_at) VALUES ('manual_drop', 'b.pdf', 'invoices', 'sha2', 'text', datetime('now'))" []
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text, extracted_at, embedded_at) VALUES ('manual_drop', 'c.pdf', 'invoices', 'sha3', 'text', datetime('now'), datetime('now'))" []
            let! stats = Stats.getIndexStats db (TestHelpers.memFs().Fs) ":memory:"
            Assert.Equal(3L, stats.DocumentCount)
            Assert.Equal(2L, stats.ExtractedCount)
            Assert.Equal(1L, stats.EmbeddedCount)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Stats_GetIndexStats_NonExistentDbPath_SizeIsZero`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! stats = Stats.getIndexStats db (TestHelpers.memFs().Fs) "/nonexistent/path/db.sqlite"
            Assert.Equal(0.0, stats.DatabaseSizeMb)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Stats_GetAccountStats_MultipleAccounts_ReturnsAll`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ = db.execNonQuery "INSERT INTO sync_state (account, last_sync_at, message_count) VALUES ('acct1@gmail.com', '2026-01-01T00:00:00Z', 10)" []
            let! _ = db.execNonQuery "INSERT INTO sync_state (account, last_sync_at, message_count) VALUES ('acct2@gmail.com', '2026-02-01T00:00:00Z', 20)" []
            let! stats = Stats.getAccountStats db
            Assert.Equal(2, stats.Length)
        finally db.dispose ()
    }

// ─── getCategoryCounts with real dirs ────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Stats_GetCategoryCounts_WithRealDir_ReturnsCounts`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), $"hermes-test-stats-{Guid.NewGuid():N}")
    try
        Directory.CreateDirectory(tempDir) |> ignore
        let invoicesDir = Path.Combine(tempDir, "invoices")
        Directory.CreateDirectory(invoicesDir) |> ignore
        File.WriteAllText(Path.Combine(invoicesDir, "test.pdf"), "content")
        File.WriteAllText(Path.Combine(invoicesDir, "test2.pdf"), "content2")
        let taxDir = Path.Combine(tempDir, "tax")
        Directory.CreateDirectory(taxDir) |> ignore
        File.WriteAllText(Path.Combine(taxDir, "return.pdf"), "content")
        let unDir = Path.Combine(tempDir, "unclassified")
        Directory.CreateDirectory(unDir) |> ignore
        File.WriteAllText(Path.Combine(unDir, "doc.pdf"), "content")
        let hermesDir = Path.Combine(tempDir, ".hermes")
        Directory.CreateDirectory(hermesDir) |> ignore
        File.WriteAllText(Path.Combine(hermesDir, "config"), "data")

        let counts = Stats.getCategoryCounts Interpreters.realFileSystem tempDir
        Assert.True(counts.Length >= 2, $"Expected >= 2 categories, got {counts.Length}")
        Assert.True(counts |> List.exists (fun c -> c.Category = "invoices" && c.Count = 2))
        Assert.True(counts |> List.exists (fun c -> c.Category = "tax" && c.Count = 1))
        Assert.False(counts |> List.exists (fun c -> c.Category = "unclassified"))
        Assert.False(counts |> List.exists (fun c -> c.Category = ".hermes"))
        Assert.Equal("invoices", counts.[0].Category)
    finally
        try Directory.Delete(tempDir, true) with _ -> ()

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Stats_GetCategoryCounts_EmptySubdirs_ExcludesEmpty`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), $"hermes-test-stats2-{Guid.NewGuid():N}")
    try
        Directory.CreateDirectory(tempDir) |> ignore
        Directory.CreateDirectory(Path.Combine(tempDir, "empty-category")) |> ignore
        let withFiles = Path.Combine(tempDir, "has-files")
        Directory.CreateDirectory(withFiles) |> ignore
        File.WriteAllText(Path.Combine(withFiles, "a.pdf"), "content")

        let counts = Stats.getCategoryCounts Interpreters.realFileSystem tempDir
        Assert.Equal(1, counts.Length)
        Assert.Equal("has-files", counts.[0].Category)
    finally
        try Directory.Delete(tempDir, true) with _ -> ()

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Stats_GetAccountStats_NullSyncAt_ReturnsNone`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ = db.execNonQuery "INSERT INTO sync_state (account, message_count) VALUES ('nodate@gmail.com', 5)" []
            let! stats = Stats.getAccountStats db
            Assert.Equal(1, stats.Length)
            Assert.Equal("nodate@gmail.com", stats.[0].Label)
            Assert.True(stats.[0].LastSyncAt.IsNone || stats.[0].LastSyncAt = Some "")
        finally db.dispose ()
    }

// ─── getPipelineCounts ───────────────────────────────────────────────

let private pipelineStubDb : Algebra.Database =
    { execScalar = fun _ _ -> Task.FromResult(box 0L)
      execNonQuery = fun _ _ -> Task.FromResult(0)
      execReader = fun _ _ -> Task.FromResult([] : Map<string, obj> list)
      initSchema = fun () -> Task.FromResult(Ok ())
      tableExists = fun _ -> Task.FromResult(false)
      schemaVersion = fun () -> Task.FromResult(0)
      dispose = ignore }

let private customDb (extracting: int) (classifying: int) : Algebra.Database =
    { pipelineStubDb with
        execScalar = fun sql _ ->
            if sql.Contains("extracted_at IS NULL") then
                Task.FromResult(box (int64 extracting))
            elif sql.Contains("unsorted") then
                Task.FromResult(box (int64 classifying))
            else
                Task.FromResult(box 0L) }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Stats_getPipelineCounts_EmptyDbAndFs_AllZeroes`` () =
    task {
        let fs = TestHelpers.memFs ()
        let! counts = Stats.getPipelineCounts pipelineStubDb fs.Fs "archive"
        Assert.Equal(0, counts.IntakeCount)
        Assert.Equal(0, counts.ExtractingCount)
        Assert.Equal(0, counts.ClassifyingCount)
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Stats_getPipelineCounts_FilesInIntake_CountsIntake`` () =
    task {
        let fs = TestHelpers.memFs ()
        fs.Dirs.["archive/unclassified"] <- true
        fs.Put "archive/unclassified/a.pdf" "content"
        fs.Put "archive/unclassified/b.pdf" "content"
        fs.Put "archive/unclassified/c.pdf" "content"
        let! counts = Stats.getPipelineCounts pipelineStubDb fs.Fs "archive"
        Assert.Equal(3, counts.IntakeCount)
        Assert.Equal(0, counts.ExtractingCount)
        Assert.Equal(0, counts.ClassifyingCount)
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Stats_getPipelineCounts_DocumentsNeedingExtraction_CountsExtracting`` () =
    task {
        let fs = TestHelpers.memFs ()
        let db = customDb 5 0
        let! counts = Stats.getPipelineCounts db fs.Fs "archive"
        Assert.Equal(0, counts.IntakeCount)
        Assert.Equal(5, counts.ExtractingCount)
        Assert.Equal(0, counts.ClassifyingCount)
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Stats_getPipelineCounts_UnsortedExtractedDocs_CountsClassifying`` () =
    task {
        let fs = TestHelpers.memFs ()
        let db = customDb 0 2
        let! counts = Stats.getPipelineCounts db fs.Fs "archive"
        Assert.Equal(0, counts.IntakeCount)
        Assert.Equal(0, counts.ExtractingCount)
        Assert.Equal(2, counts.ClassifyingCount)
    }

// ─── getExtractionQueue ──────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Stats_GetExtractionQueue_EmptyDb_ReturnsEmpty`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! queue = Stats.getExtractionQueue db 5
            Assert.Empty(queue)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Stats_GetExtractionQueue_ReturnsUnextractedDocs`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ = db.execNonQuery
                        "INSERT INTO documents (source_type, saved_path, category, sha256, original_name, size_bytes) VALUES ('manual_drop', 'a.pdf', 'unsorted', 'sha1', 'invoice.pdf', 102400)"
                        []
            let! _ = db.execNonQuery
                        "INSERT INTO documents (source_type, saved_path, category, sha256, original_name, size_bytes, extracted_at) VALUES ('manual_drop', 'b.pdf', 'invoices', 'sha2', 'extracted.pdf', 2048, datetime('now'))"
                        []
            let! _ = db.execNonQuery
                        "INSERT INTO documents (source_type, saved_path, category, sha256, original_name, size_bytes) VALUES ('manual_drop', 'c.pdf', 'unsorted', 'sha3', 'scan.pdf', 5242880)"
                        []
            let! queue = Stats.getExtractionQueue db 5
            Assert.Equal(2, queue.Length)
            Assert.Equal("invoice.pdf", queue.[0].OriginalName)
            Assert.Equal(102400L, queue.[0].SizeBytes)
            Assert.Equal("scan.pdf", queue.[1].OriginalName)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Stats_GetExtractionQueue_RespectsLimit`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            for i in 1..10 do
                let! _ = db.execNonQuery
                            $"INSERT INTO documents (source_type, saved_path, category, sha256, original_name) VALUES ('manual_drop', 'doc{i}.pdf', 'unsorted', 'sha{i}', 'doc{i}.pdf')"
                            []
                ()
            let! queue = Stats.getExtractionQueue db 3
            Assert.Equal(3, queue.Length)
        finally db.dispose ()
    }

// ─── getRecentClassifications ────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Stats_GetRecentClassifications_EmptyDb_ReturnsEmpty`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! results = Stats.getRecentClassifications db 10
            Assert.Empty(results)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Stats_GetRecentClassifications_ReturnsClassifiedDocs`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ = db.execNonQuery
                        "INSERT INTO documents (source_type, saved_path, category, sha256, original_name, classification_tier, classification_confidence, extracted_at) VALUES ('manual_drop', 'a.pdf', 'invoices', 'sha1', 'invoice.pdf', 'content-rules', 0.87, datetime('now'))"
                        []
            let! _ = db.execNonQuery
                        "INSERT INTO documents (source_type, saved_path, category, sha256, original_name) VALUES ('manual_drop', 'b.pdf', 'unsorted', 'sha2', 'unclassified.pdf')"
                        []
            let! _ = db.execNonQuery
                        "INSERT INTO documents (source_type, saved_path, category, sha256, original_name, classification_tier, classification_confidence, extracted_at) VALUES ('manual_drop', 'c.pdf', 'legal', 'sha3', 'contract.pdf', 'llm', 0.35, datetime('now', '-1 minute'))"
                        []
            let! results = Stats.getRecentClassifications db 10
            Assert.Equal(2, results.Length)
            Assert.Equal("invoice.pdf", results.[0].OriginalName)
            Assert.Equal("invoices", results.[0].Category)
            Assert.Equal(Some "content-rules", results.[0].ClassificationTier)
            Assert.True(results.[0].ClassificationConfidence.IsSome)
            Assert.Equal("contract.pdf", results.[1].OriginalName)
            Assert.Equal(Some "llm", results.[1].ClassificationTier)
        finally db.dispose ()
    }

// ─── getTierBreakdown ────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Stats_GetTierBreakdown_EmptyDb_AllZeroes`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! tb = Stats.getTierBreakdown db
            Assert.Equal(0, tb.ContentCount)
            Assert.Equal(0, tb.LlmCount)
            Assert.Equal(0, tb.ManualCount)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Stats_GetTierBreakdown_CountsByTier`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256, classification_tier) VALUES ('manual_drop', 'a.pdf', 'invoices', 'sha1', 'content-rules')" []
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256, classification_tier) VALUES ('manual_drop', 'b.pdf', 'invoices', 'sha2', 'content-rules')" []
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256, classification_tier) VALUES ('manual_drop', 'c.pdf', 'legal', 'sha3', 'llm')" []
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256, classification_tier) VALUES ('manual_drop', 'd.pdf', 'tax', 'sha4', 'manual')" []
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256, classification_tier) VALUES ('manual_drop', 'e.pdf', 'tax', 'sha5', 'manual')" []
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256, classification_tier) VALUES ('manual_drop', 'f.pdf', 'unsorted', 'sha6', 'manual')" []
            let! tb = Stats.getTierBreakdown db
            Assert.Equal(2, tb.ContentCount)
            Assert.Equal(1, tb.LlmCount)
            Assert.Equal(3, tb.ManualCount)
        finally db.dispose ()
    }
