namespace Hermes.Core

open System
open System.IO
open System.Threading.Tasks

/// Query functions for UI status display.
/// Provides structured stats instead of raw SQL from the UI layer.
[<RequireQualifiedAccess>]
module Stats =

    /// Index statistics for the status panel.
    type IndexStats =
        { DocumentCount: int64
          ExtractedCount: int64
          EmbeddedCount: int64
          DatabaseSizeMb: float }

    /// Per-category document count.
    type CategoryCount =
        { Category: string
          Count: int }

    /// Per-account sync statistics.
    type AccountStats =
        { Label: string
          MessageCount: int
          LastSyncAt: string option }

    /// Query index stats from the database.
    let getIndexStats (db: Algebra.Database) (fs: Algebra.FileSystem) (dbPath: string) : Task<IndexStats> =
        task {
            let! docCount = db.execScalar "SELECT COUNT(*) FROM documents" []
            let! extractedCount = db.execScalar "SELECT COUNT(*) FROM documents WHERE extracted_text IS NOT NULL" []
            let! embeddedCount = db.execScalar "SELECT COUNT(*) FROM documents WHERE embedded_at IS NOT NULL" []

            let toInt64 (v: obj | null) =
                match v with
                | null -> 0L
                | :? int64 as i -> i
                | _ -> 0L

            let sizeMb =
                if fs.fileExists dbPath then
                    float (fs.getFileSize dbPath) / (1024.0 * 1024.0)
                else
                    0.0

            return
                { DocumentCount = toInt64 docCount
                  ExtractedCount = toInt64 extractedCount
                  EmbeddedCount = toInt64 embeddedCount
                  DatabaseSizeMb = sizeMb }
        }

    /// Query document counts per category from the archive directory.
    let getCategoryCounts (fs: Algebra.FileSystem) (archiveDir: string) : CategoryCount list =
        if not (fs.directoryExists archiveDir) then
            []
        else
            fs.getDirectories archiveDir
            |> Array.choose (fun dir ->
                let name = Path.GetFileName(dir) |> Option.ofObj |> Option.defaultValue ""
                if name = "unclassified" || name = ".hermes" || name = "" then
                    None
                else
                    let count = fs.getFiles dir "*" |> Array.length
                    if count > 0 then Some { Category = name; Count = count }
                    else None)
            |> Array.sortByDescending (fun c -> c.Count)
            |> Array.toList

    /// Query per-account sync stats from the database.
    let getAccountStats (db: Algebra.Database) : Task<AccountStats list> =
        task {
            let! rows =
                db.execReader
                    """SELECT s.account, s.last_sync_at, COALESCE(m.cnt, 0) AS msg_count
                       FROM sync_state s
                       LEFT JOIN (SELECT account, COUNT(*) AS cnt FROM messages GROUP BY account) m
                       ON s.account = m.account"""
                    []

            return
                rows
                |> List.map (fun row ->
                    let account =
                        match row |> Map.tryFind "account" with
                        | Some (:? string as s) -> s
                        | _ -> "unknown"

                    let lastSync =
                        match row |> Map.tryFind "last_sync_at" with
                        | Some (:? string as s) when not (String.IsNullOrEmpty(s)) -> Some s
                        | _ -> None

                    let msgCount =
                        match row |> Map.tryFind "msg_count" with
                        | Some (:? int64 as i) -> int i
                        | _ -> 0

                    { Label = account
                      MessageCount = msgCount
                      LastSyncAt = lastSync })
        }
