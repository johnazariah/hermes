namespace Hermes.Core

#nowarn "3261"

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks

/// Individual MCP tool implementations.
/// Each tool is parameterised over algebras (Database, Logger, FileSystem)
/// and returns a JsonNode result.
[<RequireQualifiedAccess>]
module McpTools =

    // ─── Nullable JsonNode helpers ───────────────────────────────────

    /// Safely access a property of a JsonNode, returning option.
    let private tryGetNode (node: JsonNode) (key: string) : JsonNode option =
        let result: JsonNode | null = node.[key]

        match result with
        | null -> None
        | v -> Some v

    /// Get a string property from a JsonNode, returning option.
    let private tryGetString (node: JsonNode) (key: string) : string option =
        tryGetNode node key |> Option.map (fun n -> n.GetValue<string>())

    /// Get an int property from a JsonNode, with default.
    let private tryGetInt (node: JsonNode) (key: string) (defaultValue: int) : int =
        tryGetNode node key
        |> Option.map (fun n -> n.GetValue<int>())
        |> Option.defaultValue defaultValue

    /// Get an int64 property from a JsonNode, returning option.
    let private tryGetInt64 (node: JsonNode) (key: string) : int64 option =
        tryGetNode node key |> Option.map (fun n -> n.GetValue<int64>())

    // ─── Path sandboxing ─────────────────────────────────────────────

    /// Validate that a relative path stays within the archive directory.
    /// Rejects "..", absolute paths, and null/empty strings.
    let isPathSafe (archiveDir: string) (requestedPath: string) : Result<string, string> =
        if String.IsNullOrWhiteSpace(requestedPath) then
            Error "Path must not be empty"
        elif requestedPath.Contains("..") then
            Error "Path traversal (..) is not allowed"
        elif Path.IsPathRooted(requestedPath) then
            Error "Absolute paths are not allowed"
        else
            let full = Path.GetFullPath(Path.Combine(archiveDir, requestedPath))
            let normalised = Path.GetFullPath(archiveDir + string Path.DirectorySeparatorChar)

            if full.StartsWith(normalised, StringComparison.OrdinalIgnoreCase) then
                Ok full
            else
                Error "Path resolves outside the archive directory"

    // ─── Helper: row value extraction ────────────────────────────────

    // Row reading uses Prelude.RowReader — no local boilerplate needed

    // ─── hermes_search ───────────────────────────────────────────────

    /// Full-text search over the document index.
    let search (db: Algebra.Database) (args: JsonNode) : Task<JsonNode> =
        task {
            let query =
                tryGetString args "query" |> Option.defaultValue ""

            if String.IsNullOrWhiteSpace(query) then
                let result = JsonObject()
                result["results"] <- JsonArray()
                result["error"] <- JsonValue.Create("query parameter is required")
                return result :> JsonNode
            else
                let category = tryGetString args "category"
                let limit = tryGetInt args "limit" 20

                let filter: Search.SearchFilter =
                    { Query = query
                      Category = category
                      Sender = None
                      DateFrom = None
                      DateTo = None
                      Account = None
                      SourceType = None
                      Limit = limit }

                let! results = Search.executeUnified db filter

                let arr = JsonArray()

                for r in results do
                    let item = JsonObject()
                    item["id"] <- JsonValue.Create(r.DocumentId)
                    item["path"] <- JsonValue.Create(r.SavedPath)
                    item["category"] <- JsonValue.Create(r.Category)
                    item["score"] <- JsonValue.Create(r.RelevanceScore)
                    item["resultType"] <- JsonValue.Create(r.ResultType)

                    r.OriginalName
                    |> Option.iter (fun v -> item["originalName"] <- JsonValue.Create(v))

                    r.Sender |> Option.iter (fun v -> item["sender"] <- JsonValue.Create(v))
                    r.Subject |> Option.iter (fun v -> item["subject"] <- JsonValue.Create(v))
                    r.EmailDate |> Option.iter (fun v -> item["emailDate"] <- JsonValue.Create(v))

                    r.ExtractedVendor
                    |> Option.iter (fun v -> item["vendor"] <- JsonValue.Create(v))

                    r.ExtractedAmount
                    |> Option.iter (fun v -> item["amount"] <- JsonValue.Create(v))

                    r.Snippet |> Option.iter (fun v -> item["snippet"] <- JsonValue.Create(v))
                    arr.Add(item)

                let result = JsonObject()
                result["results"] <- arr
                result["count"] <- JsonValue.Create(results.Length)
                return result :> JsonNode
        }

    // ─── hermes_get_document ─────────────────────────────────────────

    /// Map a DB row to a JsonObject for document responses. Pure — no async.
    let private mapDocumentRow (row: Map<string, obj>) : JsonObject =
        let r = Prelude.RowReader(row)
        let doc = JsonObject()
        doc["id"] <- JsonValue.Create(r.Int64 "id" 0L)
        doc["sourceType"] <- JsonValue.Create(r.String "source_type" "")
        doc["savedPath"] <- JsonValue.Create(r.String "saved_path" "")
        doc["category"] <- JsonValue.Create(r.String "category" "")
        doc["sha256"] <- JsonValue.Create(r.String "sha256" "")

        let addOpt (jsonKey: string) (dbKey: string) =
            r.OptString dbKey
            |> Option.iter (fun v -> doc[jsonKey] <- JsonValue.Create(v))

        addOpt "gmailId" "gmail_id"
        addOpt "account" "account"
        addOpt "sender" "sender"
        addOpt "subject" "subject"
        addOpt "emailDate" "email_date"
        addOpt "originalName" "original_name"
        addOpt "mimeType" "mime_type"
        addOpt "extractedText" "extracted_text"
        addOpt "extractedDate" "extracted_date"
        addOpt "extractedVendor" "extracted_vendor"
        addOpt "extractionMethod" "extraction_method"
        addOpt "extractedAt" "extracted_at"
        addOpt "embeddedAt" "embedded_at"
        addOpt "ingestedAt" "ingested_at"

        r.OptFloat "size_bytes"
        |> Option.iter (fun v -> doc["sizeBytes"] <- JsonValue.Create(int64 v))

        r.OptFloat "extracted_amount"
        |> Option.iter (fun v -> doc["extractedAmount"] <- JsonValue.Create(v))

        doc

    /// Get full metadata + extracted text for a document by ID or path.
    let getDocument (db: Algebra.Database) (args: JsonNode) : Task<JsonNode> =
        task {
            let idOpt = tryGetInt64 args "id"
            let pathOpt = tryGetString args "path"

            let sql, parameters =
                match idOpt with
                | Some id ->
                    "SELECT * FROM documents WHERE id = @id LIMIT 1",
                    [ ("@id", Database.boxVal id) ]
                | None ->
                    match pathOpt with
                    | Some p ->
                        "SELECT * FROM documents WHERE saved_path = @path LIMIT 1",
                        [ ("@path", Database.boxVal p) ]
                    | None ->
                        "SELECT 0 WHERE 0", []

            let! rows = db.execReader sql parameters

            match rows with
            | [] ->
                let result = JsonObject()
                result["error"] <- JsonValue.Create("Document not found")
                return result :> JsonNode
            | row :: _ ->
                return mapDocumentRow row :> JsonNode
        }

    // ─── hermes_list_categories ──────────────────────────────────────

    /// List all categories with document counts.
    let listCategories (db: Algebra.Database) (_args: JsonNode) : Task<JsonNode> =
        task {
            let! rows =
                db.execReader
                    "SELECT category, COUNT(*) AS doc_count FROM documents GROUP BY category ORDER BY doc_count DESC"
                    []

            let arr = JsonArray()

            for row in rows do
                let rr = Prelude.RowReader(row)
                let item = JsonObject()
                item["category"] <- JsonValue.Create(rr.String "category" "")
                item["count"] <- JsonValue.Create(rr.Int64 "doc_count" 0L)
                arr.Add(item)

            let result = JsonObject()
            result["categories"] <- arr
            return result :> JsonNode
        }

    // ─── hermes_stats ────────────────────────────────────────────────

    let private scalarInt64 (db: Algebra.Database) (sql: string) : Task<int64> =
        task {
            let! result = db.execScalar sql []

            match result with
            | null -> return 0L
            | v ->
                match v with
                | :? int64 as i -> return i
                | _ -> return 0L
        }

    /// Get summary statistics about the archive.
    let stats (db: Algebra.Database) (_args: JsonNode) : Task<JsonNode> =
        task {
            let! totalDocs = scalarInt64 db "SELECT COUNT(*) FROM documents"
            let! totalEmails = scalarInt64 db "SELECT COUNT(*) FROM messages"

            let! categoryCount =
                scalarInt64 db "SELECT COUNT(DISTINCT category) FROM documents"

            let! extractedCount =
                scalarInt64 db "SELECT COUNT(*) FROM documents WHERE extracted_text IS NOT NULL"

            let! embeddedCount =
                scalarInt64 db "SELECT COUNT(*) FROM documents WHERE embedded_at IS NOT NULL"

            let result = JsonObject()
            result["totalDocuments"] <- JsonValue.Create(totalDocs)
            result["totalEmails"] <- JsonValue.Create(totalEmails)
            result["categories"] <- JsonValue.Create(categoryCount)
            result["extractedDocuments"] <- JsonValue.Create(extractedCount)
            result["embeddedDocuments"] <- JsonValue.Create(embeddedCount)

            if totalDocs > 0L then
                result["extractionCoverage"] <-
                    JsonValue.Create(Math.Round(float extractedCount / float totalDocs * 100.0, 1))

                result["embeddingCoverage"] <-
                    JsonValue.Create(Math.Round(float embeddedCount / float totalDocs * 100.0, 1))
            else
                result["extractionCoverage"] <- JsonValue.Create(0.0)
                result["embeddingCoverage"] <- JsonValue.Create(0.0)

            return result :> JsonNode
        }

    // ─── hermes_read_file ────────────────────────────────────────────

    /// Read a text file from the archive, with path sandboxing.
    let readFile
        (fs: Algebra.FileSystem)
        (archiveDir: string)
        (args: JsonNode)
        : Task<JsonNode> =
        task {
            let pathArg = tryGetString args "path"

            match pathArg with
            | None ->
                let result = JsonObject()
                result["error"] <- JsonValue.Create("path parameter is required")
                return result :> JsonNode
            | Some requestedPath ->
                match isPathSafe archiveDir requestedPath with
                | Error msg ->
                    let result = JsonObject()
                    result["error"] <- JsonValue.Create(msg)
                    return result :> JsonNode
                | Ok fullPath ->
                    if not (fs.fileExists fullPath) then
                        let result = JsonObject()
                        result["error"] <- JsonValue.Create("File not found")
                        return result :> JsonNode
                    else
                        try
                            let! content = fs.readAllText fullPath

                            let result = JsonObject()
                            result["path"] <- JsonValue.Create(requestedPath)
                            result["content"] <- JsonValue.Create(content)
                            result["size"] <- JsonValue.Create(fs.getFileSize fullPath)
                            return result :> JsonNode
                        with ex ->
                            let result = JsonObject()
                            result["error"] <- JsonValue.Create($"Failed to read file: {ex.Message}")
                            return result :> JsonNode
        }

    // ─── hermes_list_reminders ───────────────────────────────────────

    let listReminders (db: Algebra.Database) (clock: Algebra.Clock) (args: JsonNode) : Task<JsonNode> =
        task {
            let now = clock.utcNow ()
            let! active = Reminders.getActive db now
            let! completed = Reminders.getRecentlyCompleted db
            let! summary = Reminders.getSummary db now

            let result = JsonObject()
            let items = JsonArray()

            for (r, path, name) in active do
                let item = JsonObject()
                item["id"] <- JsonValue.Create(r.Id)
                item["status"] <- JsonValue.Create(Domain.ReminderStatus.toString r.Status)
                item["category"] <- JsonValue.Create(r.Category)
                r.Vendor |> Option.iter (fun v -> item["vendor"] <- JsonValue.Create(v))
                r.Amount |> Option.iter (fun a -> item["amount"] <- JsonValue.Create(float a))
                r.DueDate |> Option.iter (fun d -> item["dueDate"] <- JsonValue.Create(d.ToString("yyyy-MM-dd")))
                path |> Option.iter (fun p -> item["documentPath"] <- JsonValue.Create(p))
                name |> Option.iter (fun n -> item["fileName"] <- JsonValue.Create(n))
                let isOverdue = r.DueDate |> Option.map (fun d -> d < now) |> Option.defaultValue false
                item["isOverdue"] <- JsonValue.Create(isOverdue)
                items.Add(item)

            result["reminders"] <- items
            result["overdueCount"] <- JsonValue.Create(summary.OverdueCount)
            result["upcomingCount"] <- JsonValue.Create(summary.UpcomingCount)
            result["totalActiveAmount"] <- JsonValue.Create(float summary.TotalActiveAmount)
            return result :> JsonNode
        }

    // ─── hermes_update_reminder ──────────────────────────────────────

    let updateReminder (db: Algebra.Database) (clock: Algebra.Clock) (args: JsonNode) : Task<JsonNode> =
        task {
            let idOpt = tryGetInt64 args "reminder_id"
            let actionOpt = tryGetString args "action"
            let now = clock.utcNow ()

            match idOpt, actionOpt with
            | None, _ | _, None ->
                let r = JsonObject()
                r["error"] <- JsonValue.Create("reminder_id and action are required")
                return r :> JsonNode
            | Some rid, Some action ->
                match action.ToLowerInvariant() with
                | "complete" | "paid" ->
                    do! Reminders.markCompleted db rid now
                    let r = JsonObject()
                    r["status"] <- JsonValue.Create("completed")
                    r["reminderId"] <- JsonValue.Create(rid)
                    return r :> JsonNode
                | "snooze" ->
                    let days = tryGetInt args "snooze_days" 7
                    do! Reminders.snooze db rid days now
                    let r = JsonObject()
                    r["status"] <- JsonValue.Create("snoozed")
                    r["reminderId"] <- JsonValue.Create(rid)
                    r["snoozedDays"] <- JsonValue.Create(days)
                    return r :> JsonNode
                | "dismiss" ->
                    do! Reminders.dismiss db rid now
                    let r = JsonObject()
                    r["status"] <- JsonValue.Create("dismissed")
                    r["reminderId"] <- JsonValue.Create(rid)
                    return r :> JsonNode
                | other ->
                    let r = JsonObject()
                    r["error"] <- JsonValue.Create($"Unknown action: {other}. Use 'complete', 'snooze', or 'dismiss'.")
                    return r :> JsonNode
        }

    // ─── Feed tools ──────────────────────────────────────────────────

    let listDocumentsFeed (db: Algebra.Database) (args: JsonNode) : Task<JsonNode> =
        task {
            let sinceId = tryGetInt64 args "since_id" |> Option.defaultValue 0L
            let category = tryGetString args "category"
            let limit = tryGetInt args "limit" 100
            let! docs = DocumentFeed.listDocuments db sinceId category limit
            let arr = JsonArray()
            for doc in docs do
                arr.Add(DocumentFeed.feedDocToJson doc)
            return arr :> JsonNode
        }

    let getFeedStats (db: Algebra.Database) (args: JsonNode) : Task<JsonNode> =
        task {
            let! stats = DocumentFeed.getFeedStats db
            return DocumentFeed.feedStatsToJson stats :> JsonNode
        }

    let getDocumentContent
        (db: Algebra.Database) (fs: Algebra.FileSystem) (archiveDir: string)
        (args: JsonNode) : Task<JsonNode> =
        task {
            match tryGetInt64 args "document_id" with
            | None ->
                let err = JsonObject()
                err["error"] <- JsonValue.Create("document_id is required")
                return err :> JsonNode
            | Some docId ->
                let formatStr = tryGetString args "format" |> Option.defaultValue "markdown"
                let format =
                    DocumentFeed.parseFormat formatStr
                    |> Option.defaultValue DocumentFeed.Markdown
                let! result = DocumentFeed.getDocumentContent db fs archiveDir docId format
                match result with
                | Ok content ->
                    let obj = JsonObject()
                    obj["document_id"] <- JsonValue.Create(docId)
                    obj["format"] <- JsonValue.Create(formatStr)
                    obj["content"] <- JsonValue.Create(content)
                    return obj :> JsonNode
                | Error e ->
                    let err = JsonObject()
                    err["error"] <- JsonValue.Create(e)
                    return err :> JsonNode
        }

    // ─── Document management tools ───────────────────────────────────

    let reclassifyDocument
        (db: Algebra.Database) (fs: Algebra.FileSystem) (archiveDir: string)
        (args: JsonNode) : Task<JsonNode> =
        task {
            match tryGetInt64 args "document_id", tryGetString args "new_category" with
            | None, _ ->
                let err = JsonObject()
                err["error"] <- JsonValue.Create("document_id is required")
                return err :> JsonNode
            | _, None ->
                let err = JsonObject()
                err["error"] <- JsonValue.Create("new_category is required")
                return err :> JsonNode
            | Some docId, Some category ->
                let! result = DocumentManagement.reclassify db fs archiveDir docId category
                match result with
                | Ok () ->
                    let obj = JsonObject()
                    obj["status"] <- JsonValue.Create("reclassified")
                    obj["document_id"] <- JsonValue.Create(docId)
                    obj["new_category"] <- JsonValue.Create(category)
                    return obj :> JsonNode
                | Error e ->
                    let err = JsonObject()
                    err["error"] <- JsonValue.Create(e)
                    return err :> JsonNode
        }

    let reextractDocument (db: Algebra.Database) (args: JsonNode) : Task<JsonNode> =
        task {
            match tryGetInt64 args "document_id" with
            | None ->
                let err = JsonObject()
                err["error"] <- JsonValue.Create("document_id is required")
                return err :> JsonNode
            | Some docId ->
                let! result = DocumentManagement.reextract db docId
                match result with
                | Ok () ->
                    let obj = JsonObject()
                    obj["status"] <- JsonValue.Create("queued_for_reextraction")
                    obj["document_id"] <- JsonValue.Create(docId)
                    return obj :> JsonNode
                | Error e ->
                    let err = JsonObject()
                    err["error"] <- JsonValue.Create(e)
                    return err :> JsonNode
        }

    let getProcessingQueue (db: Algebra.Database) (args: JsonNode) : Task<JsonNode> =
        task {
            let limit = tryGetInt args "limit" 10
            let! queue = DocumentManagement.getProcessingQueue db limit
            let obj = JsonObject()
            let stageToJson (stage: DocumentManagement.QueueStage) =
                let s = JsonObject()
                s["count"] <- JsonValue.Create(stage.Count)
                let ids = JsonArray()
                for id in stage.SampleIds do ids.Add(JsonValue.Create(id))
                s["sample_ids"] <- ids
                s
            obj["unclassified"] <- stageToJson queue.Unclassified
            obj["unextracted"] <- stageToJson queue.Unextracted
            obj["unembedded"] <- stageToJson queue.Unembedded
            return obj :> JsonNode
        }
