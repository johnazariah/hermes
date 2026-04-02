module Hermes.Tests.ActivityLogTests

open System
open Xunit
open Hermes.Core

// ─── Helpers ─────────────────────────────────────────────────────────

let private initDb () =
    let db = TestHelpers.createDb ()
    // ActivityLog may need its own table — init schema should create it
    db

// ─── log + getRecent ─────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ActivityLog_LogInfo_And_GetRecent_RoundTrips`` () =
    task {
        let db = initDb ()
        try
            do! ActivityLog.logInfo db "sync" "Synced 5 messages" None
            let! recent = ActivityLog.getRecent db 10
            Assert.True(recent.Length > 0)
            Assert.Equal("sync", recent.[0].Category)
            Assert.Contains("Synced", recent.[0].Message)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ActivityLog_LogWarning_IncludesDetails`` () =
    task {
        let db = initDb ()
        try
            do! ActivityLog.logWarning db "extraction" "PDF corrupt" (Some 42L) "stack trace here"
            let! recent = ActivityLog.getRecent db 10
            Assert.True(recent.Length > 0)
            Assert.Equal("extraction", recent.[0].Category)
            Assert.True(recent.[0].Details.IsSome)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ActivityLog_LogError_SetsErrorLevel`` () =
    task {
        let db = initDb ()
        try
            do! ActivityLog.logError db "gmail" "Auth failed" None "token expired"
            let! recent = ActivityLog.getRecent db 10
            Assert.True(recent.Length > 0)
            Assert.Equal("error", recent.[0].Level.ToLowerInvariant())
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ActivityLog_GetRecent_RespectsLimit`` () =
    task {
        let db = initDb ()
        try
            for i in 1..5 do
                do! ActivityLog.logInfo db "test" $"Message {i}" None
            let! recent = ActivityLog.getRecent db 2
            Assert.Equal(2, recent.Length)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ActivityLog_GetRecent_EmptyLog_ReturnsEmpty`` () =
    task {
        let db = initDb ()
        try
            let! recent = ActivityLog.getRecent db 10
            Assert.Empty(recent)
        finally db.dispose ()
    }

// ─── getForDocument ──────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ActivityLog_GetForDocument_FiltersCorrectly`` () =
    task {
        let db = initDb ()
        try
            do! ActivityLog.logInfo db "extraction" "Extracted doc 1" (Some 1L)
            do! ActivityLog.logInfo db "extraction" "Extracted doc 2" (Some 2L)
            let! doc1Logs = ActivityLog.getForDocument db 1L 10
            Assert.Equal(1, doc1Logs.Length)
        finally db.dispose ()
    }

// ─── getByCategory ───────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ActivityLog_GetByCategory_FiltersCorrectly`` () =
    task {
        let db = initDb ()
        try
            do! ActivityLog.logInfo db "sync" "Sync started" None
            do! ActivityLog.logInfo db "extraction" "Extraction done" None
            let! syncLogs = ActivityLog.getByCategory db "sync" 10
            Assert.Equal(1, syncLogs.Length)
            Assert.Equal("sync", syncLogs.[0].Category)
        finally db.dispose ()
    }
