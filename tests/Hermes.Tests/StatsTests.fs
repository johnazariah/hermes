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

// ─── Additional stats tests ──────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Stats_GetIndexStats_WithExtractedAndEmbedded_CountsCorrectly`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('manual_drop', 'a.pdf', 'invoices', 'sha1')" []
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text, extracted_at) VALUES ('manual_drop', 'b.pdf', 'invoices', 'sha2', 'text', datetime('now'))" []
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text, extracted_at, embedded_at) VALUES ('manual_drop', 'c.pdf', 'invoices', 'sha3', 'text', datetime('now'), datetime('now'))" []
            let! stats = Stats.getIndexStats db ":memory:"
            Assert.Equal(3L, stats.DocumentCount)
            Assert.Equal(2L, stats.ExtractedCount)
            Assert.Equal(1L, stats.EmbeddedCount)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Stats_GetIndexStats_NonExistentDbPath_SizeIsZero`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! stats = Stats.getIndexStats db "/nonexistent/path/db.sqlite"
            Assert.Equal(0.0, stats.DatabaseSizeMb)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
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
