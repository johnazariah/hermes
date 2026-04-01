namespace Hermes.Core

open System
open System.Threading.Tasks

/// Bill detection, reminder lifecycle, and queries.
/// Parameterised over Database, Logger, and Clock algebras.
[<RequireQualifiedAccess>]
module Reminders =

    let private billCategories =
        set [ "invoices"; "utilities"; "insurance"; "subscriptions"; "rates-and-tax" ]

    // ─── Detection ───────────────────────────────────────────────────

    /// Try to parse a date string into DateTimeOffset.
    let private tryParseDate (s: string option) : DateTimeOffset option =
        s |> Option.bind (fun v ->
            match DateTimeOffset.TryParse(v) with
            | true, d -> Some d
            | _ -> None)

    /// Detect if a document is a bill with an actionable due date.
    let detectBill (now: DateTimeOffset) (category: string) (amount: decimal option) (dateStr: string option) (docId: int64) : Domain.Reminder option =
        if not (billCategories.Contains category) then None
        else
        match amount with
        | None | Some 0m -> None
        | Some amt ->
            match tryParseDate dateStr with
            | None -> None
            | Some dueDate ->
                let daysFromNow = (dueDate - now).TotalDays
                if daysFromNow < -30.0 || daysFromNow > 60.0 then None
                else
                    Some
                        { Domain.Reminder.Id = 0L
                          DocumentId = Some docId
                          Vendor = None
                          Amount = Some amt
                          DueDate = Some dueDate
                          Category = category
                          Status = Domain.ReminderStatus.Active
                          SnoozedUntil = None
                          CreatedAt = now
                          CompletedAt = None }

    // ─── Evaluate new documents ──────────────────────────────────────

    /// Scan recently extracted documents and create reminders for bills.
    let evaluateNewDocuments (db: Algebra.Database) (logger: Algebra.Logger) (now: DateTimeOffset) : Task<int> =
        task {
            let! rows =
                db.execReader
                    """SELECT d.id, d.category, d.extracted_amount, d.extracted_date, d.extracted_vendor
                       FROM documents d
                       WHERE d.extracted_at IS NOT NULL
                         AND NOT EXISTS (SELECT 1 FROM reminders r WHERE r.document_id = d.id)"""
                    []

            let mutable created = 0
            for row in rows do
                let getId = row |> Map.tryFind "id" |> Option.bind (fun v -> match v with :? int64 as i -> Some i | _ -> None)
                let getCat = row |> Map.tryFind "category" |> Option.bind (fun v -> match v with :? string as s -> Some s | _ -> None)
                let getAmt = row |> Map.tryFind "extracted_amount" |> Option.bind (fun v -> match v with :? float as f -> Some (decimal f) | :? int64 as i -> Some (decimal i) | _ -> None)
                let getDate = row |> Map.tryFind "extracted_date" |> Option.bind (fun v -> match v with :? string as s -> Some s | _ -> None)
                let getVendor = row |> Map.tryFind "extracted_vendor" |> Option.bind (fun v -> match v with :? string as s -> Some s | _ -> None)

                match getId, getCat with
                | Some docId, Some cat ->
                    match detectBill now cat getAmt getDate docId with
                    | None -> ()
                    | Some reminder ->
                        let! _ =
                            db.execNonQuery
                                """INSERT INTO reminders (document_id, vendor, amount, due_date, category, status, created_at)
                                   VALUES (@doc, @vendor, @amt, @due, @cat, 'active', @now)"""
                                [ ("@doc", Database.boxVal docId)
                                  ("@vendor", getVendor |> Option.map Database.boxVal |> Option.defaultValue (Database.boxVal DBNull.Value))
                                  ("@amt", reminder.Amount |> Option.map (fun a -> Database.boxVal (float a)) |> Option.defaultValue (Database.boxVal DBNull.Value))
                                  ("@due", reminder.DueDate |> Option.map (fun d -> Database.boxVal (d.ToString("o"))) |> Option.defaultValue (Database.boxVal DBNull.Value))
                                  ("@cat", Database.boxVal cat)
                                  ("@now", Database.boxVal (now.ToString("o"))) ]
                        created <- created + 1
                        let vendorName = getVendor |> Option.defaultValue "unknown"
                        logger.info $"Created reminder for doc {docId} ({cat}, {vendorName})"
                | _ -> ()

            return created
        }

    // ─── Queries ─────────────────────────────────────────────────────

    let private mapReminder (row: Map<string, obj>) : Domain.Reminder =
        let getStr key = row |> Map.tryFind key |> Option.bind (fun v -> match v with :? string as s -> Some s | _ -> None)
        let getInt64 key = row |> Map.tryFind key |> Option.bind (fun v -> match v with :? int64 as i -> Some i | _ -> None)
        let getFloat key = row |> Map.tryFind key |> Option.bind (fun v -> match v with :? float as f -> Some f | _ -> None)
        { Id = getInt64 "id" |> Option.defaultValue 0L
          DocumentId = getInt64 "document_id"
          Vendor = getStr "vendor"
          Amount = getFloat "amount" |> Option.map decimal
          DueDate = getStr "due_date" |> Option.bind (fun s -> match DateTimeOffset.TryParse(s) with true, d -> Some d | _ -> None)
          Category = getStr "category" |> Option.defaultValue ""
          Status = getStr "status" |> Option.defaultValue "active" |> Domain.ReminderStatus.fromString
          SnoozedUntil = getStr "snoozed_until" |> Option.bind (fun s -> match DateTimeOffset.TryParse(s) with true, d -> Some d | _ -> None)
          CreatedAt = getStr "created_at" |> Option.bind (fun s -> match DateTimeOffset.TryParse(s) with true, d -> Some d | _ -> None) |> Option.defaultValue DateTimeOffset.MinValue
          CompletedAt = getStr "completed_at" |> Option.bind (fun s -> match DateTimeOffset.TryParse(s) with true, d -> Some d | _ -> None) }

    /// Get active + un-snoozed reminders with document info.
    let getActive (db: Algebra.Database) (now: DateTimeOffset) : Task<(Domain.Reminder * string option * string option) list> =
        task {
            let! rows =
                db.execReader
                    """SELECT r.*, d.saved_path, d.original_name
                       FROM reminders r
                       LEFT JOIN documents d ON r.document_id = d.id
                       WHERE (r.status = 'active'
                              OR (r.status = 'snoozed' AND r.snoozed_until <= @now))
                       ORDER BY r.due_date ASC"""
                    [ ("@now", Database.boxVal (now.ToString("o"))) ]
            return
                rows |> List.map (fun row ->
                    let r = mapReminder row
                    let path = row |> Map.tryFind "saved_path" |> Option.bind (fun v -> match v with :? string as s -> Some s | _ -> None)
                    let name = row |> Map.tryFind "original_name" |> Option.bind (fun v -> match v with :? string as s -> Some s | _ -> None)
                    (r, path, name))
        }

    /// Get recently completed reminders (last 7 days).
    let getRecentlyCompleted (db: Algebra.Database) : Task<Domain.Reminder list> =
        task {
            let! rows =
                db.execReader
                    """SELECT * FROM reminders
                       WHERE status = 'completed' AND completed_at >= datetime('now', '-7 days')
                       ORDER BY completed_at DESC"""
                    []
            return rows |> List.map mapReminder
        }

    /// Get summary for sidebar badge.
    let getSummary (db: Algebra.Database) (now: DateTimeOffset) : Task<Domain.ReminderSummary> =
        task {
            let nowStr = now.ToString("o")
            let! overdueResult =
                db.execScalar
                    "SELECT COUNT(*) FROM reminders WHERE status = 'active' AND due_date < @now"
                    [ ("@now", Database.boxVal nowStr) ]
            let! upcomingResult =
                db.execScalar
                    "SELECT COUNT(*) FROM reminders WHERE status = 'active' AND (due_date >= @now OR due_date IS NULL)"
                    [ ("@now", Database.boxVal nowStr) ]
            let! totalResult =
                db.execScalar
                    "SELECT COALESCE(SUM(amount), 0) FROM reminders WHERE status = 'active'"
                    []
            let summary : Domain.ReminderSummary =
                { OverdueCount = match overdueResult with null -> 0 | v -> v :?> int64 |> int
                  UpcomingCount = match upcomingResult with null -> 0 | v -> v :?> int64 |> int
                  TotalActiveAmount = match totalResult with null -> 0m | v -> v :?> float |> decimal }
            return summary
        }

    // ─── Actions ─────────────────────────────────────────────────────

    let markCompleted (db: Algebra.Database) (id: int64) (now: DateTimeOffset) : Task<unit> =
        task {
            let! _ =
                db.execNonQuery
                    "UPDATE reminders SET status = 'completed', completed_at = @now WHERE id = @id"
                    [ ("@now", Database.boxVal (now.ToString("o"))); ("@id", Database.boxVal id) ]
            ()
        }

    let snooze (db: Algebra.Database) (id: int64) (days: int) (now: DateTimeOffset) : Task<unit> =
        task {
            let until = now.AddDays(float days).ToString("o")
            let! _ =
                db.execNonQuery
                    "UPDATE reminders SET status = 'snoozed', snoozed_until = @until WHERE id = @id"
                    [ ("@until", Database.boxVal until); ("@id", Database.boxVal id) ]
            ()
        }

    let dismiss (db: Algebra.Database) (id: int64) (now: DateTimeOffset) : Task<unit> =
        task {
            let! _ =
                db.execNonQuery
                    "UPDATE reminders SET status = 'dismissed', dismissed_at = @now WHERE id = @id"
                    [ ("@now", Database.boxVal (now.ToString("o"))); ("@id", Database.boxVal id) ]
            ()
        }

    let unsnoozeExpired (db: Algebra.Database) (now: DateTimeOffset) : Task<int> =
        task {
            let! count =
                db.execNonQuery
                    "UPDATE reminders SET status = 'active', snoozed_until = NULL WHERE status = 'snoozed' AND snoozed_until <= @now"
                    [ ("@now", Database.boxVal (now.ToString("o"))) ]
            return count
        }
