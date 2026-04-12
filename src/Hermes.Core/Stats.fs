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
          ClassifiedCount: int64
          EmbeddedCount: int64
          AwaitingExtract: int64
          AwaitingClassify: int64
          AwaitingEmbed: int64
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
            let! classifiedCount = db.execScalar "SELECT COUNT(*) FROM documents WHERE extracted_text IS NOT NULL AND category NOT IN ('unsorted', 'unclassified')" []
            let! embeddedCount = db.execScalar "SELECT COUNT(*) FROM documents WHERE embedded_at IS NOT NULL" []
            let! awaitExtract = db.execScalar "SELECT COUNT(*) FROM stage_extract" []
            let! awaitClassify = db.execScalar "SELECT COUNT(*) FROM stage_classify" []
            let! awaitEmbed = db.execScalar "SELECT COUNT(*) FROM stage_embed" []

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
                  ClassifiedCount = toInt64 classifiedCount
                  EmbeddedCount = toInt64 embeddedCount
                  AwaitingExtract = toInt64 awaitExtract
                  AwaitingClassify = toInt64 awaitClassify
                  AwaitingEmbed = toInt64 awaitEmbed
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

    /// Pipeline stage counts for the funnel UI.
    type PipelineCounts =
        { IntakeCount: int
          ExtractingCount: int
          ClassifyingCount: int }

    /// Query pipeline stage counts from the database and filesystem.
    let getPipelineCounts (db: Algebra.Database) (fs: Algebra.FileSystem) (archiveDir: string) : Task<PipelineCounts> =
        task {
            let unclassifiedDir = Path.Combine(archiveDir, "unclassified")
            let intakeCount =
                if fs.directoryExists unclassifiedDir then
                    fs.getFiles unclassifiedDir "*" |> Array.length
                else 0

            let! extractingObj =
                db.execScalar "SELECT COUNT(*) FROM documents WHERE extracted_at IS NULL" []
            let extractingCount =
                match extractingObj with
                | :? int64 as i -> int i
                | _ -> 0

            let! classifyingObj =
                db.execScalar
                    """SELECT COUNT(*) FROM documents
                       WHERE (category = 'unsorted' OR category = 'unclassified')
                         AND extracted_at IS NOT NULL"""
                    []
            let classifyingCount =
                match classifyingObj with
                | :? int64 as i -> int i
                | _ -> 0

            return
                { IntakeCount = intakeCount
                  ExtractingCount = extractingCount
                  ClassifyingCount = classifyingCount }
        }

    // ─── Extraction queue ────────────────────────────────────────────

    /// A document awaiting text extraction.
    type ExtractionQueueItem =
        { Id: int64
          OriginalName: string
          SizeBytes: int64 }

    /// Get documents waiting for extraction, ordered by insertion.
    let getExtractionQueue (db: Algebra.Database) (limit: int) : Task<ExtractionQueueItem list> =
        task {
            let! rows =
                db.execReader
                    "SELECT id, original_name, size_bytes FROM documents WHERE extracted_at IS NULL ORDER BY id ASC LIMIT @limit"
                    [ ("@limit", Database.boxVal (int64 limit)) ]

            return
                rows
                |> List.choose (fun row ->
                    let r = Prelude.RowReader(row)
                    r.OptInt64 "id"
                    |> Option.map (fun id ->
                        { Id = id
                          OriginalName = r.String "original_name" "unknown"
                          SizeBytes = r.Int64 "size_bytes" 0L }))
        }

    // ─── Recent classifications ──────────────────────────────────────

    /// A recently classified document with its confidence metadata.
    type RecentClassification =
        { Id: int64
          OriginalName: string
          Category: string
          ClassificationTier: string option
          ClassificationConfidence: float option }

    /// Get recently classified documents, newest first.
    let getRecentClassifications (db: Algebra.Database) (limit: int) : Task<RecentClassification list> =
        task {
            let! rows =
                db.execReader
                    """SELECT id, original_name, category, classification_tier, classification_confidence
                       FROM documents WHERE classification_tier IS NOT NULL
                       ORDER BY extracted_at DESC LIMIT @limit"""
                    [ ("@limit", Database.boxVal (int64 limit)) ]

            return
                rows
                |> List.choose (fun row ->
                    let r = Prelude.RowReader(row)
                    r.OptInt64 "id"
                    |> Option.map (fun id ->
                        { Id = id
                          OriginalName = r.String "original_name" "unknown"
                          Category = r.String "category" ""
                          ClassificationTier = r.OptString "classification_tier"
                          ClassificationConfidence = r.OptFloat "classification_confidence" }))
        }

    // ─── Tier breakdown ──────────────────────────────────────────────

    /// Classification tier counts for the funnel display.
    type TierBreakdown =
        { ContentCount: int
          LlmCount: int
          ManualCount: int }

    /// Helper: run a scalar COUNT and convert to int.
    let private countByTier (db: Algebra.Database) (tier: string) : Task<int> =
        task {
            let! result =
                db.execScalar
                    "SELECT COUNT(*) FROM documents WHERE classification_tier = @tier"
                    [ ("@tier", Database.boxVal tier) ]
            return
                match result with
                | :? int64 as i -> int i
                | _ -> 0
        }

    /// Get document counts per classification tier.
    let getTierBreakdown (db: Algebra.Database) : Task<TierBreakdown> =
        task {
            let! contentCount = countByTier db "content-rules"
            let! llmCount = countByTier db "llm"
            let! manualCount = countByTier db "manual"

            return
                { ContentCount = contentCount
                  LlmCount = llmCount
                  ManualCount = manualCount }
        }
