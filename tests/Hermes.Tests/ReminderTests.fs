module Hermes.Tests.ReminderTests

open System
open Xunit
open Hermes.Core

let private now = DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero)

let private insertTestDoc (db: Algebra.Database) (cat: string) (amount: float option) (date: string option) =
    task {
        let amtVal = amount |> Option.map Database.boxVal |> Option.defaultValue (Database.boxVal DBNull.Value)
        let dateVal = date |> Option.map Database.boxVal |> Option.defaultValue (Database.boxVal DBNull.Value)
        let! _ =
            db.execNonQuery
                """INSERT INTO documents (source_type, saved_path, category, sha256, extracted_amount, extracted_date, extracted_at, ingested_at)
                   VALUES ('manual_drop', @path, @cat, @sha, @amt, @date, datetime('now'), datetime('now'))"""
                [ ("@path", Database.boxVal $"{cat}/test-{Guid.NewGuid():N}.pdf")
                  ("@cat", Database.boxVal cat)
                  ("@sha", Database.boxVal (Guid.NewGuid().ToString("N")))
                  ("@amt", amtVal)
                  ("@date", dateVal) ]
        let! idResult = db.execScalar "SELECT last_insert_rowid()" []
        return match idResult with null -> 0L | v -> v :?> int64
    }

// ─── Detection ───────────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Reminders_DetectBill_InvoiceWithDueDate_CreatesReminder`` () =
    let result = Reminders.detectBill now "invoices" (Some 385m) (Some "2026-04-10") 1L
    Assert.True(result.IsSome)
    Assert.Equal(385m, result.Value.Amount.Value)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Reminders_DetectBill_WrongCategory_ReturnsNone`` () =
    Assert.True((Reminders.detectBill now "medical" (Some 100m) (Some "2026-04-10") 1L).IsNone)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Reminders_DetectBill_OldDate_ReturnsNone`` () =
    Assert.True((Reminders.detectBill now "invoices" (Some 100m) (Some "2025-01-01") 1L).IsNone)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Reminders_DetectBill_NoAmount_ReturnsNone`` () =
    Assert.True((Reminders.detectBill now "invoices" None (Some "2026-04-10") 1L).IsNone)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Reminders_DetectBill_FutureDate_Within60Days_CreatesReminder`` () =
    let result = Reminders.detectBill now "utilities" (Some 200m) (Some "2026-05-15") 1L
    Assert.True(result.IsSome)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Reminders_DetectBill_FutureDate_Beyond60Days_ReturnsNone`` () =
    Assert.True((Reminders.detectBill now "invoices" (Some 100m) (Some "2026-07-01") 1L).IsNone)

// ─── Evaluate new documents ──────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reminders_EvaluateNew_InsertsReminders`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _docId = insertTestDoc db "invoices" (Some 500.0) (Some "2026-04-05")
            let! created = Reminders.evaluateNewDocuments db TestHelpers.silentLogger now
            Assert.True(created > 0)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reminders_EvaluateNew_DeduplicatesExisting`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _docId = insertTestDoc db "invoices" (Some 500.0) (Some "2026-04-05")
            let! first = Reminders.evaluateNewDocuments db TestHelpers.silentLogger now
            let! second = Reminders.evaluateNewDocuments db TestHelpers.silentLogger now
            Assert.True(first > 0)
            Assert.Equal(0, second)
        finally db.dispose ()
    }

// ─── Actions ─────────────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reminders_MarkCompleted_ChangesStatus`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! docId = insertTestDoc db "invoices" (Some 100.0) (Some "2026-04-05")
            let! _ = Reminders.evaluateNewDocuments db TestHelpers.silentLogger now
            let! active = Reminders.getActive db now
            Assert.True(active.Length > 0)
            let reminderId = active.[0] |> fun (r, _, _) -> r.Id
            do! Reminders.markCompleted db reminderId now
            let! activeAfter = Reminders.getActive db now
            Assert.Equal(0, activeAfter.Length)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reminders_Snooze_HidesUntilExpiry`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _docId = insertTestDoc db "invoices" (Some 100.0) (Some "2026-04-05")
            let! _ = Reminders.evaluateNewDocuments db TestHelpers.silentLogger now
            let! active = Reminders.getActive db now
            let rid = active.[0] |> fun (r, _, _) -> r.Id
            do! Reminders.snooze db rid 7 now
            // Not visible now
            let! afterSnooze = Reminders.getActive db now
            Assert.Equal(0, afterSnooze.Length)
            // Visible after 7 days
            let future = now.AddDays(8.0)
            let! afterExpiry = Reminders.getActive db future
            Assert.True(afterExpiry.Length > 0)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reminders_Dismiss_PermanentlyRemoves`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _docId = insertTestDoc db "invoices" (Some 100.0) (Some "2026-04-05")
            let! _ = Reminders.evaluateNewDocuments db TestHelpers.silentLogger now
            let! active = Reminders.getActive db now
            let rid = active.[0] |> fun (r, _, _) -> r.Id
            do! Reminders.dismiss db rid now
            let! afterDismiss = Reminders.getActive db now
            Assert.Equal(0, afterDismiss.Length)
            let! completed = Reminders.getRecentlyCompleted db
            Assert.Equal(0, completed.Length) // dismissed, not completed
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reminders_GetSummary_CorrectCounts`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            // One overdue, one upcoming
            let! _d1 = insertTestDoc db "invoices" (Some 100.0) (Some "2026-03-20") // overdue
            let! _d2 = insertTestDoc db "utilities" (Some 200.0) (Some "2026-04-15") // upcoming
            let! _ = Reminders.evaluateNewDocuments db TestHelpers.silentLogger now
            let! summary = Reminders.getSummary db now
            Assert.Equal(1, summary.OverdueCount)
            Assert.Equal(1, summary.UpcomingCount)
            Assert.Equal(300m, summary.TotalActiveAmount)
        finally db.dispose ()
    }

// ─── unsnoozeExpired ─────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reminders_UnsnoozeExpired_WithExpiredSnooze_UnsnoozesReminder`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _docId = insertTestDoc db "invoices" (Some 100.0) (Some "2026-04-05")
            let! _ = Reminders.evaluateNewDocuments db TestHelpers.silentLogger now
            let! active = Reminders.getActive db now
            Assert.True(active.Length > 0)
            let rid = active.[0] |> fun (r, _, _) -> r.Id
            do! Reminders.snooze db rid 3 now
            let! afterSnooze = Reminders.getActive db now
            Assert.Equal(0, afterSnooze.Length)
            let afterExpiry = now.AddDays(4.0)
            let! unsnoozed = Reminders.unsnoozeExpired db afterExpiry
            Assert.True(unsnoozed > 0)
            let! afterUnsnooze = Reminders.getActive db afterExpiry
            Assert.True(afterUnsnooze.Length > 0)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reminders_UnsnoozeExpired_NoSnoozedReminders_ReturnsZero`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! count = Reminders.unsnoozeExpired db now
            Assert.Equal(0, count)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reminders_UnsnoozeExpired_BeforeExpiry_ReturnsZero`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _docId = insertTestDoc db "invoices" (Some 100.0) (Some "2026-04-05")
            let! _ = Reminders.evaluateNewDocuments db TestHelpers.silentLogger now
            let! active = Reminders.getActive db now
            let rid = active.[0] |> fun (r, _, _) -> r.Id
            do! Reminders.snooze db rid 7 now
            let beforeExpiry = now.AddDays(3.0)
            let! count = Reminders.unsnoozeExpired db beforeExpiry
            Assert.Equal(0, count)
        finally db.dispose ()
    }
