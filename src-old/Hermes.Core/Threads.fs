namespace Hermes.Core

open System.Threading.Tasks

/// Email thread grouping and timeline queries for the UI navigator.
[<RequireQualifiedAccess>]
module Threads =

    // ─── Types ───────────────────────────────────────────────────────

    type ThreadSummary =
        { ThreadId: string; Subject: string; Account: string
          MessageCount: int; AttachmentCount: int
          FirstDate: string; LastDate: string
          Participants: string list }

    type ThreadMessage =
        { GmailId: string; Sender: string; Date: string
          Subject: string; BodyPreview: string
          AttachmentDocIds: int64 list }

    type ThreadDetail =
        { Summary: ThreadSummary; Messages: ThreadMessage list }

    // ─── Queries ─────────────────────────────────────────────────────

    let private mapThreadRow (row: Map<string, obj>) : ThreadSummary option =
        let r = Prelude.RowReader(row)
        r.OptString "thread_id"
        |> Option.map (fun tid ->
            { ThreadId = tid
              Subject = r.String "subject" "(no subject)"
              Account = r.String "account" ""
              MessageCount = r.Int64 "msg_count" 0L |> int
              AttachmentCount = r.Int64 "att_count" 0L |> int
              FirstDate = r.String "first_date" ""
              LastDate = r.String "last_date" ""
              Participants =
                r.OptString "participants"
                |> Option.map (fun p -> p.Split(',') |> Array.map (fun s -> s.Trim()) |> Array.toList)
                |> Option.defaultValue [] })

    /// List threads, most recent first.
    let listThreads (db: Algebra.Database) (offset: int) (limit: int) : Task<ThreadSummary list> =
        task {
            let! rows =
                db.execReader
                    """SELECT m.thread_id, m.account,
                              MAX(m.subject) as subject,
                              COUNT(*) as msg_count,
                              SUM(m.has_attachments) as att_count,
                              MIN(m.date) as first_date,
                              MAX(m.date) as last_date,
                              GROUP_CONCAT(DISTINCT m.sender) as participants
                       FROM messages m
                       WHERE m.thread_id IS NOT NULL AND m.thread_id != ''
                       GROUP BY m.thread_id
                       ORDER BY MAX(m.date) DESC
                       LIMIT @lim OFFSET @off"""
                    [ ("@lim", Database.boxVal (int64 limit))
                      ("@off", Database.boxVal (int64 offset)) ]
            return rows |> List.choose mapThreadRow
        }

    /// List threads for a specific account.
    let listThreadsByAccount (db: Algebra.Database) (account: string) (offset: int) (limit: int) : Task<ThreadSummary list> =
        task {
            let! rows =
                db.execReader
                    """SELECT m.thread_id, m.account,
                              MAX(m.subject) as subject,
                              COUNT(*) as msg_count,
                              SUM(m.has_attachments) as att_count,
                              MIN(m.date) as first_date,
                              MAX(m.date) as last_date,
                              GROUP_CONCAT(DISTINCT m.sender) as participants
                       FROM messages m
                       WHERE m.account = @acct AND m.thread_id IS NOT NULL AND m.thread_id != ''
                       GROUP BY m.thread_id
                       ORDER BY MAX(m.date) DESC
                       LIMIT @lim OFFSET @off"""
                    [ ("@acct", Database.boxVal account)
                      ("@lim", Database.boxVal (int64 limit))
                      ("@off", Database.boxVal (int64 offset)) ]
            return rows |> List.choose mapThreadRow
        }

    /// Get full thread detail with messages and attachment document IDs.
    let getThreadDetail (db: Algebra.Database) (threadId: string) : Task<ThreadDetail option> =
        task {
            let! msgRows =
                db.execReader
                    """SELECT gmail_id, sender, date, subject, body_text
                       FROM messages WHERE thread_id = @tid
                       ORDER BY date ASC"""
                    [ ("@tid", Database.boxVal threadId) ]
            if msgRows.IsEmpty then return None
            else
                let messages =
                    msgRows |> List.choose (fun row ->
                        let r = Prelude.RowReader(row)
                        r.OptString "gmail_id"
                        |> Option.map (fun gid ->
                            let bodyText = r.String "body_text" ""
                            let preview = if bodyText.Length > 200 then bodyText.Substring(0, 200) + "..." else bodyText
                            { GmailId = gid
                              Sender = r.String "sender" ""
                              Date = r.String "date" ""
                              Subject = r.String "subject" ""
                              BodyPreview = preview
                              AttachmentDocIds = [] }))
                // Enrich with attachment doc IDs
                let enrichMessage (msg: ThreadMessage) : Task<ThreadMessage> =
                    task {
                        let! attRows =
                            db.execReader
                                "SELECT id FROM documents WHERE gmail_id = @gid"
                                [ ("@gid", Database.boxVal msg.GmailId) ]
                        let docIds =
                            attRows |> List.choose (fun r ->
                                Prelude.RowReader(r).OptInt64 "id")
                        return { msg with AttachmentDocIds = docIds }
                    }

                let! enriched = messages |> List.map enrichMessage |> List.toArray |> Task.WhenAll
                let first = messages |> List.head
                let last = messages |> List.last
                let summary : ThreadSummary =
                    { ThreadId = threadId
                      Subject = first.Subject
                      Account = ""
                      MessageCount = messages.Length
                      AttachmentCount = enriched |> Array.sumBy (fun m -> m.AttachmentDocIds.Length)
                      FirstDate = first.Date
                      LastDate = last.Date
                      Participants = messages |> List.map (fun m -> m.Sender) |> List.distinct }
                return Some { Summary = summary; Messages = enriched |> Array.toList }
        }
