module Hermes.Tests.StatsTests

open System
open Xunit
open Hermes.Core

// ─── getIndexStats ───────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Stats_GetIndexStats_EmptyDb_ReturnsZeros`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! stats = Stats.getIndexStats db ""
            Assert.Equal(0L, stats.DocumentCount)
            Assert.Equal(0L, stats.ExtractedCount)
            Assert.Equal(0L, stats.EmbeddedCount)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
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
            let! stats = Stats.getIndexStats db ""
            Assert.Equal(2L, stats.DocumentCount)
            Assert.Equal(1L, stats.ExtractedCount)
            Assert.Equal(0L, stats.EmbeddedCount)
        finally db.dispose ()
    }

// ─── getCategoryCounts ───────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Stats_GetCategoryCounts_NonexistentDir_ReturnsEmpty`` () =
    let counts = Stats.getCategoryCounts "/nonexistent/dir"
    Assert.Empty(counts)

// ─── getAccountStats ─────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Stats_GetAccountStats_EmptyDb_ReturnsEmpty`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! stats = Stats.getAccountStats db
            Assert.Empty(stats)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
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
