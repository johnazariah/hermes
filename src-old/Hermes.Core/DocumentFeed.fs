namespace Hermes.Core

open System
open System.Text.Json.Nodes
open System.Threading.Tasks

/// Document feed: cursor-based pagination for consumers + content retrieval.
[<RequireQualifiedAccess>]
module DocumentFeed =

    // ─── Types ───────────────────────────────────────────────────────

    type FeedDocument =
        { Id: int64; OriginalName: string; Category: string; FilePath: string
          Sender: string option; Subject: string option; Account: string option
          ExtractedDate: string option; ExtractedAmount: float option
          ExtractedVendor: string option
          IngestedAt: string; ExtractedAt: string option }

    type FeedStats =
        { TotalDocuments: int; MaxDocumentId: int64
          ByCategory: Map<string, int> }

    type ContentFormat = Text | Markdown | Raw

    // ─── Feed queries ────────────────────────────────────────────────

    let private mapRow (row: Map<string, obj>) : FeedDocument option =
        let r = Prelude.RowReader(row)
        r.OptInt64 "id"
        |> Option.map (fun id ->
            { Id = id
              OriginalName = r.String "original_name" ""
              Category = r.String "category" "unsorted"
              FilePath = r.String "saved_path" ""
              Sender = r.OptString "sender"
              Subject = r.OptString "subject"
              Account = r.OptString "account"
              ExtractedDate = r.OptString "extracted_date"
              ExtractedAmount = r.OptFloat "extracted_amount"
              ExtractedVendor = r.OptString "extracted_vendor"
              IngestedAt = r.String "ingested_at" ""
              ExtractedAt = r.OptString "extracted_at" })

    /// List documents with cursor-based pagination.
    let listDocuments
        (db: Algebra.Database) (sinceId: int64)
        (category: string option) (limit: int)
        : Task<FeedDocument list> =
        task {
            let catClause, catParams =
                match category with
                | Some c -> " AND category = @cat", [ ("@cat", Database.boxVal c) ]
                | None -> "", []
            let sql =
                $"""SELECT id, original_name, category, saved_path, sender, subject,
                           account, extracted_date, extracted_amount, extracted_vendor,
                           ingested_at, extracted_at
                    FROM documents WHERE id > @since{catClause}
                    ORDER BY id ASC LIMIT @lim"""
            let parms =
                ("@since", Database.boxVal sinceId)
                :: ("@lim", Database.boxVal (int64 limit))
                :: catParams
            let! rows = db.execReader sql parms
            return rows |> List.choose mapRow
        }

    /// Get feed statistics.
    let getFeedStats (db: Algebra.Database) : Task<FeedStats> =
        task {
            let! totalObj = db.execScalar "SELECT COUNT(*) FROM documents" []
            let! maxIdObj = db.execScalar "SELECT COALESCE(MAX(id), 0) FROM documents" []
            let! catRows =
                db.execReader
                    "SELECT category, COUNT(*) as cnt FROM documents GROUP BY category ORDER BY cnt DESC"
                    []
            let total =
                match totalObj with
                | :? int64 as i -> int i
                | _ -> 0
            let maxId =
                match maxIdObj with
                | :? int64 as i -> i
                | _ -> 0L
            let byCat =
                catRows
                |> List.choose (fun row ->
                    let r = Prelude.RowReader(row)
                    r.OptString "category"
                    |> Option.map (fun cat -> (cat, r.Int64 "cnt" 0L |> int)))
                |> Map.ofList
            return { TotalDocuments = total; MaxDocumentId = maxId; ByCategory = byCat }
        }

    // ─── Content retrieval ───────────────────────────────────────────

    let parseFormat (s: string) : ContentFormat option =
        match s.ToLowerInvariant() with
        | "text" -> Some Text
        | "markdown" -> Some Markdown
        | "raw" -> Some Raw
        | _ -> None

    let private stripFrontmatter (text: string) : string =
        if text.StartsWith("---") then
            match text.IndexOf("---", 3) with
            | -1 -> text
            | idx -> text.Substring(idx + 3).TrimStart()
        else text

    /// Get document content in the specified format.
    let getDocumentContent
        (db: Algebra.Database) (fs: Algebra.FileSystem)
        (archiveDir: string) (documentId: int64) (format: ContentFormat)
        : Task<Result<string, string>> =
        task {
            let! rows =
                db.execReader
                    "SELECT saved_path, extracted_text, extracted_markdown FROM documents WHERE id = @id"
                    [ ("@id", Database.boxVal documentId) ]
            match rows with
            | [] -> return Error $"Document {documentId} not found"
            | row :: _ ->
                let r = Prelude.RowReader(row)
                let savedPath = r.String "saved_path" ""
                let extractedText = r.OptString "extracted_text"
                let extractedMarkdown = r.OptString "extracted_markdown"
                match format with
                | Markdown ->
                    return
                        extractedMarkdown
                        |> Option.orElse extractedText
                        |> Option.map Ok
                        |> Option.defaultValue (Error "No extracted content available")
                | Text ->
                    return Ok (extractedText |> Option.defaultValue "" |> stripFrontmatter)
                | Raw ->
                    let fullPath =
                        if IO.Path.IsPathRooted(savedPath) then savedPath
                        else IO.Path.Combine(archiveDir, savedPath)
                    if fs.fileExists fullPath then
                        let! content = fs.readAllText fullPath
                        return Ok content
                    else
                        return Error $"File not found: {savedPath}"
        }

    // ─── JSON serialization ──────────────────────────────────────────

    let feedDocToJson (doc: FeedDocument) : JsonObject =
        let obj = JsonObject()
        obj["id"] <- JsonValue.Create(doc.Id)
        obj["original_name"] <- JsonValue.Create(doc.OriginalName)
        obj["category"] <- JsonValue.Create(doc.Category)
        obj["file_path"] <- JsonValue.Create(doc.FilePath)
        doc.Sender |> Option.iter (fun v -> obj["sender"] <- JsonValue.Create(v))
        doc.Subject |> Option.iter (fun v -> obj["subject"] <- JsonValue.Create(v))
        doc.Account |> Option.iter (fun v -> obj["account"] <- JsonValue.Create(v))
        doc.ExtractedDate |> Option.iter (fun v -> obj["extracted_date"] <- JsonValue.Create(v))
        doc.ExtractedAmount |> Option.iter (fun v -> obj["extracted_amount"] <- JsonValue.Create(v))
        doc.ExtractedVendor |> Option.iter (fun v -> obj["extracted_vendor"] <- JsonValue.Create(v))
        obj["ingested_at"] <- JsonValue.Create(doc.IngestedAt)
        doc.ExtractedAt |> Option.iter (fun v -> obj["extracted_at"] <- JsonValue.Create(v))
        obj

    let feedStatsToJson (stats: FeedStats) : JsonObject =
        let obj = JsonObject()
        obj["total_documents"] <- JsonValue.Create(stats.TotalDocuments)
        obj["max_document_id"] <- JsonValue.Create(stats.MaxDocumentId)
        let catObj = JsonObject()
        stats.ByCategory |> Map.iter (fun k v -> catObj[k] <- JsonValue.Create(v))
        obj["by_category"] <- catObj
        obj
