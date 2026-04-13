namespace Hermes.Core

open System
open System.Threading.Tasks

/// Activity log: append-only event log for pipeline actions, classification, extraction, errors.
[<RequireQualifiedAccess>]
module ActivityLog =

    // ─── Types ───────────────────────────────────────────────────────

    type EventLevel = Info | Warning | Error

    type LogEntry =
        { Id: int64; Timestamp: string; Level: string
          Category: string; Message: string
          DocumentId: int64 option; Details: string option }

    // ─── Schema ──────────────────────────────────────────────────────

    let createTableSql =
        """CREATE TABLE IF NOT EXISTS activity_log (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp   TEXT NOT NULL DEFAULT (datetime('now')),
            level       TEXT NOT NULL DEFAULT 'info',
            category    TEXT NOT NULL,
            message     TEXT NOT NULL,
            document_id INTEGER,
            details     TEXT,
            FOREIGN KEY (document_id) REFERENCES documents(id)
        )"""

    let createIndexSql =
        "CREATE INDEX IF NOT EXISTS idx_activity_log_ts ON activity_log(timestamp DESC)"

    // ─── Write ───────────────────────────────────────────────────────

    let private levelToString = function
        | Info -> "info"
        | Warning -> "warning"
        | Error -> "error"

    /// Log an activity event.
    let log (db: Algebra.Database) (level: EventLevel) (category: string) (message: string)
            (documentId: int64 option) (details: string option) : Task<unit> =
        task {
            let! _ =
                db.execNonQuery
                    """INSERT INTO activity_log (level, category, message, document_id, details)
                       VALUES (@lvl, @cat, @msg, @docId, @details)"""
                    [ ("@lvl", Database.boxVal (levelToString level))
                      ("@cat", Database.boxVal category)
                      ("@msg", Database.boxVal message)
                      ("@docId", documentId |> Option.map Database.boxVal |> Option.defaultValue (Database.boxVal DBNull.Value))
                      ("@details", details |> Option.map Database.boxVal |> Option.defaultValue (Database.boxVal DBNull.Value)) ]
            ()
        }

    let logInfo db cat msg docId = log db Info cat msg docId None
    let logWarning db cat msg docId details = log db Warning cat msg docId (Some details)
    let logError db cat msg docId details = log db Error cat msg docId (Some details)

    // ─── Read ────────────────────────────────────────────────────────

    let private mapEntry (row: Map<string, obj>) : LogEntry option =
        let r = Prelude.RowReader(row)
        r.OptInt64 "id"
        |> Option.map (fun id ->
            { Id = id
              Timestamp = r.String "timestamp" ""
              Level = r.String "level" "info"
              Category = r.String "category" ""
              Message = r.String "message" ""
              DocumentId = r.OptInt64 "document_id"
              Details = r.OptString "details" })

    /// Get recent activity log entries.
    let getRecent (db: Algebra.Database) (limit: int) : Task<LogEntry list> =
        task {
            let! rows =
                db.execReader
                    "SELECT * FROM activity_log ORDER BY id DESC LIMIT @lim"
                    [ ("@lim", Database.boxVal (int64 limit)) ]
            return rows |> List.choose mapEntry
        }

    /// Get activity log entries for a specific document.
    let getForDocument (db: Algebra.Database) (documentId: int64) (limit: int) : Task<LogEntry list> =
        task {
            let! rows =
                db.execReader
                    "SELECT * FROM activity_log WHERE document_id = @docId ORDER BY id DESC LIMIT @lim"
                    [ ("@docId", Database.boxVal documentId)
                      ("@lim", Database.boxVal (int64 limit)) ]
            return rows |> List.choose mapEntry
        }

    /// Get activity log entries by category.
    let getByCategory (db: Algebra.Database) (category: string) (limit: int) : Task<LogEntry list> =
        task {
            let! rows =
                db.execReader
                    "SELECT * FROM activity_log WHERE category = @cat ORDER BY id DESC LIMIT @lim"
                    [ ("@cat", Database.boxVal category)
                      ("@lim", Database.boxVal (int64 limit)) ]
            return rows |> List.choose mapEntry
        }
