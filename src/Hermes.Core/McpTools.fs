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
    /// Named properties exist only on JSON objects: `node.[key]` raises for
    /// arrays and scalars, so kind-checked access keeps every container total.
    let private tryGetNode (node: JsonNode) (key: string) : JsonNode option =
        match node with
        | :? JsonObject as properties ->
            match properties.TryGetPropertyValue(key) with
            | true, value -> Option.ofObj value
            | false, _ -> None
        | _ -> None

    /// Total accessors for tool arguments. An absent argument is `Ok None`;
    /// an argument of the wrong JSON type is a deterministic `Error`. These
    /// never raise, so malformed client input cannot escape as an unhandled
    /// exception (HTTP 500 on the /mcp endpoint).
    module Args =

        /// Detached JsonElement view of a node - kind inspection is total.
        let private element (node: JsonNode) : JsonElement =
            use parsed = JsonDocument.Parse(node.ToJsonString())
            parsed.RootElement.Clone()

        let private asText (value: JsonElement) : string option =
            match value.ValueKind with
            | JsonValueKind.String -> value.GetString() |> Option.ofObj
            | _ -> None

        let private asInteger (value: JsonElement) : int64 option =
            match value.ValueKind with
            | JsonValueKind.Number ->
                match value.TryGetInt64() with
                | true, parsed -> Some parsed
                | _ -> None
            | _ -> None

        let private asFlag (value: JsonElement) : bool option =
            match value.ValueKind with
            | JsonValueKind.True -> Some true
            | JsonValueKind.False -> Some false
            | _ -> None

        let private read
            (expected: string)
            (convert: JsonElement -> 'T option)
            (node: JsonNode)
            (key: string)
            : Result<'T option, string> =
            match tryGetNode node key with
            | None -> Ok None
            | Some value ->
                match convert (element value) with
                | Some converted -> Ok(Some converted)
                | None -> Error $"{key} must be {expected}"

        /// Optional string argument.
        let text (node: JsonNode) (key: string) : Result<string option, string> =
            read "a string" asText node key

        /// Optional integer argument.
        let integer (node: JsonNode) (key: string) : Result<int64 option, string> =
            read "an integer" asInteger node key

        /// Optional boolean flag argument.
        let flag (node: JsonNode) (key: string) : Result<bool option, string> =
            read "a boolean" asFlag node key

        /// Integer argument the tool schema declares as required.
        let requiredInteger (node: JsonNode) (key: string) : Result<int64, string> =
            integer node key
            |> Result.bind (function
                | Some value -> Ok value
                | None -> Error $"{key} is required")

    /// Why a tool call produced no payload.
    /// `InvalidArguments` violates the declared input schema - a protocol
    /// error. `DomainFailure` is a truthful tool error: the request was
    /// well-formed but the operation could not be performed.
    type ToolFailure =
        | InvalidArguments of string
        | DomainFailure of string

    /// Get a string property, ignoring values of the wrong type.
    let private tryGetString (node: JsonNode) (key: string) : string option =
        match Args.text node key with
        | Ok value -> value
        | Error _ -> None

    /// Get an int property, defaulting when absent or wrongly typed.
    let private tryGetInt (node: JsonNode) (key: string) (defaultValue: int) : int =
        match Args.integer node key with
        | Ok (Some value) -> int value
        | _ -> defaultValue

    /// Get an int64 property, ignoring values of the wrong type.
    let private tryGetInt64 (node: JsonNode) (key: string) : int64 option =
        match Args.integer node key with
        | Ok value -> value
        | Error _ -> None

    // ─── Path sandboxing ─────────────────────────────────────────────

    /// Validate that a relative path stays within the archive directory.
    /// Rejects "..", absolute paths, and null/empty strings.
    let isPathSafe (archiveDir: string) (requestedPath: string) : Result<string, string> =
        let isAbsolute (p: string) =
            Path.IsPathRooted(p)
            || (p.Length >= 2 && Char.IsLetter(p.[0]) && p.[1] = ':')  // Windows drive letter
            || p.StartsWith(@"\\")                                      // UNC path

        if String.IsNullOrWhiteSpace(requestedPath) then
            Error "Path must not be empty"
        elif requestedPath.Contains("..") then
            Error "Path traversal (..) is not allowed"
        elif isAbsolute requestedPath then
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
                scalarInt64 db "SELECT COUNT(*) FROM documents WHERE extracted_at IS NOT NULL"

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

    /// Failure payload shared by every tool: { "error": message }.
    let errorJson (message: string) : JsonNode =
        let err = JsonObject()
        err["error"] <- JsonValue.Create(message)
        err :> JsonNode

    let private stageStatusToJson (stage: Reflow.StageStatus) : JsonObject =
        let obj = JsonObject()
        obj["stage_name"] <- JsonValue.Create(stage.StageName)
        obj["outcome"] <- JsonValue.Create(Reflow.StageOutcome.toString stage.Outcome)
        stage.Error |> Option.iter (fun e -> obj["error"] <- JsonValue.Create(e))
        obj

    let private planToJson (plan: Reflow.Plan) : JsonObject =
        let obj = JsonObject()
        obj["document_id"] <- JsonValue.Create(plan.DocumentId)
        obj["kind"] <- JsonValue.Create(Reflow.OperationKind.toString plan.Kind)
        let invalidated = JsonArray()
        plan.InvalidatedStages |> List.iter (fun s -> invalidated.Add(JsonValue.Create(s)))
        obj["invalidated_stages"] <- invalidated
        let current = JsonArray()
        plan.CurrentStages |> List.iter (fun s -> current.Add(JsonValue.Create(s)))
        obj["current_stages"] <- current
        obj["dag_signature"] <- JsonValue.Create(plan.DagSignature)
        obj

    let private operationStatusToJson (status: Reflow.OperationStatus) : JsonObject =
        let obj = JsonObject()
        obj["operation_id"] <- JsonValue.Create(status.OperationId)
        obj["document_id"] <- JsonValue.Create(status.DocumentId)
        obj["kind"] <- JsonValue.Create(Reflow.OperationKind.toString status.Kind)
        obj["mode"] <- JsonValue.Create(Reflow.RequestMode.toString status.Mode)
        obj["lifecycle"] <- JsonValue.Create(Reflow.Lifecycle.toString status.Lifecycle)
        obj["dag_signature"] <- JsonValue.Create(status.DagSignature)
        obj["created_at"] <- JsonValue.Create(status.CreatedAt)
        status.CompletedAt |> Option.iter (fun v -> obj["completed_at"] <- JsonValue.Create(v))
        status.Error |> Option.iter (fun v -> obj["error"] <- JsonValue.Create(v))
        let stages = JsonArray()
        status.Stages |> List.iter (fun s -> stages.Add(stageStatusToJson s))
        obj["stages"] <- stages
        obj

    let private requestResultToJson (result: Reflow.RequestResult) : JsonObject =
        let obj = JsonObject()
        obj["plan"] <- planToJson result.Plan
        result.Status |> Option.iter (fun s -> obj["status"] <- operationStatusToJson s)
        obj["duplicate"] <- JsonValue.Create(result.Duplicate)
        obj

    type private ReflowRequestArgs =
        { DocumentId: int64
          Kind: Reflow.OperationKind
          Mode: Reflow.RequestMode }

    let private parseReflowKind (args: JsonNode) : Result<Reflow.OperationKind, string> =
        Args.text args "operation"
        |> Result.bind (function
            | Some operation -> Reflow.OperationKind.parse operation
            | None -> Error "operation is required (reextract|recomprehend|reembed)")

    let private parseReflowMode (args: JsonNode) : Result<Reflow.RequestMode, string> =
        Args.text args "mode"
        |> Result.map (Option.defaultValue "dry_run")
        |> Result.bind Reflow.RequestMode.parse

    let private parseReflowArgs (args: JsonNode) : Result<ReflowRequestArgs, string> =
        Args.requiredInteger args "document_id"
        |> Result.bind (fun documentId ->
            parseReflowKind args
            |> Result.bind (fun kind ->
                parseReflowMode args
                |> Result.map (fun mode ->
                    { DocumentId = documentId
                      Kind = kind
                      Mode = mode })))

    /// Request a reflow. Schema violations are InvalidArguments; a reflow the
    /// pipeline cannot perform (unknown document, stale DAG) is a DomainFailure.
    let reflowDocument
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (dag: PipelineV5.Dag)
        (args: JsonNode)
        : Task<Result<JsonNode, ToolFailure>> =
        task {
            match parseReflowArgs args with
            | Error e -> return Error(InvalidArguments e)
            | Ok parsed ->
                let! result = Reflow.request db logger dag parsed.DocumentId parsed.Kind parsed.Mode
                return
                    match result with
                    | Ok requestResult -> Ok(requestResultToJson requestResult :> JsonNode)
                    | Error e -> Error(DomainFailure e)
        }

    let reflowStatus
        (db: Algebra.Database)
        (dag: PipelineV5.Dag)
        (args: JsonNode)
        : Task<Result<JsonNode, ToolFailure>> =
        task {
            match Args.requiredInteger args "operation_id" with
            | Error e -> return Error(InvalidArguments e)
            | Ok opId ->
                let! result = Reflow.getStatus dag db opId
                return
                    match result with
                    | Ok status -> Ok(operationStatusToJson status :> JsonNode)
                    | Error e -> Error(DomainFailure e)
        }

    let private legacyReextractJson (docId: int64) (result: Reflow.RequestResult) : JsonNode =
        let obj = JsonObject()
        obj["status"] <- JsonValue.Create("queued_for_reextraction")
        obj["document_id"] <- JsonValue.Create(docId)
        result.Status |> Option.iter (fun s -> obj["operation_id"] <- JsonValue.Create(s.OperationId))
        obj["duplicate"] <- JsonValue.Create(result.Duplicate)
        obj :> JsonNode

    let reextractDocument
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (dag: PipelineV5.Dag)
        (args: JsonNode)
        : Task<Result<JsonNode, ToolFailure>> =
        task {
            match Args.requiredInteger args "document_id" with
            | Error e -> return Error(InvalidArguments e)
            | Ok docId ->
                let! result = Reflow.request db logger dag docId Reflow.Reextract Reflow.Apply
                return
                    match result with
                    | Ok requestResult -> Ok(legacyReextractJson docId requestResult)
                    | Error e -> Error(DomainFailure e)
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

    /// Deep extraction dependencies (optional, only needed for hermes_deep_extract).
    type DeepExtractionDeps =
        { Chat: Algebra.ChatProvider
          Registry: Map<string, PromptLoader.ParsedPrompt>
          Provider: string
          Model: string }

    type private DeepArtifactPaths =
        { Source: string
          Folder: string
          Fence: PublicationFence.ArtifactFolder }

    type private DeepExtractRequest =
        { DocumentId: int64
          Force: bool }

    type private DeepTarget =
        { Generation: Generation.Token
          Artifacts: DeepArtifactPaths }

    type private DeepArtifactSnapshot =
        { ExtractedText: string
          Comprehension: string
          /// Folder revision observed with these bytes, under the folder fence.
          Revision: ArtifactRevision.Token
          Current: bool }

    let private sourceArtifactPath docId archiveDir savedPath =
        if String.IsNullOrWhiteSpace savedPath then
            Error $"Document {docId} has no usable saved_path"
        elif Path.IsPathRooted savedPath then
            Ok savedPath
        elif String.IsNullOrWhiteSpace archiveDir then
            Error $"Document {docId} has a relative saved_path but no archive root"
        else
            try Ok(Path.Combine(archiveDir, savedPath))
            with error -> Error $"Document {docId} has an invalid saved_path: {error.Message}"

    let private deepArtifactPaths docId archiveDir savedPath folderPath =
        match
            PublicationFence.ArtifactFolder.tryFromMetadata
                archiveDir savedPath folderPath
        with
        | None ->
            Error $"Document {docId} has no usable folder for thread.comprehension.json"
        | Some fence ->
            PublicationFence.ArtifactFolder.resolve archiveDir fence
            |> Result.mapError (fun error -> $"Document {docId}: {error}")
            |> Result.bind (fun folder ->
                sourceArtifactPath docId archiveDir savedPath
                |> Result.map (fun source ->
                     { Source = source; Folder = folder; Fence = fence }))

    let private deepArtifactsCurrentSql =
        """SELECT CASE WHEN
             EXISTS (
                 SELECT 1 FROM stage_completions
                 WHERE document_id = @doc AND stage_name = 'extract')
             AND EXISTS (
                 SELECT 1 FROM stage_completions
                 WHERE document_id = @doc AND stage_name = 'deep-comprehend')
             AND EXISTS (
                 SELECT 1 FROM comprehension
                 WHERE document_id = @doc)
           THEN 1 ELSE 0 END"""

    let private deepArtifactsPresentWith
        (execScalar:
            string -> (string * obj) list -> Task<obj | null>)
        documentId =
        task {
            let! value =
                execScalar
                    deepArtifactsCurrentSql
                    [ ("@doc", Database.boxVal documentId) ]
            return
                match value with
                | :? int64 as number -> number = 1L
                | :? int as number -> number = 1
                | _ -> false
        }

    let private deepArtifactsCurrent
        (db: Algebra.Database)
        (generation: Generation.Token) =
        task {
            let! before = Generation.isCurrent db generation
            if not before then
                return false
            else
                let! outputs =
                    deepArtifactsPresentWith
                        db.execScalar generation.DocumentId
                let! after = Generation.isCurrent db generation
                return outputs && after
        }

    let private deepArtifactsCurrentIn
        (generation: Generation.Token)
        (scope: Algebra.TransactionScope) =
        task {
            let! current =
                Generation.isCurrentIn scope generation
            if not current then return false
            else
                return!
                    deepArtifactsPresentWith
                        scope.execScalar generation.DocumentId
        }

    let private deepExtractRequest
        (args: JsonNode)
        : Result<DeepExtractRequest, string> =
        Args.requiredInteger args "document_id"
        |> Result.bind (fun documentId ->
            Args.flag args "force"
            |> Result.map (fun force ->
                { DocumentId = documentId
                  Force = force |> Option.defaultValue false }))

    let private deepTargetSql =
        """SELECT saved_path, folder_path,
                  COALESCE(
                    (SELECT generation
                     FROM document_generations
                     WHERE document_id = d.id), 0)
                      AS generation,
                  (SELECT COUNT(*) FROM stage_completions sc
                   WHERE sc.document_id = d.id
                     AND sc.stage_name IN ('extract', 'deep-comprehend'))
                      AS current_input_count,
                  (SELECT COUNT(*) FROM comprehension c
                   WHERE c.document_id = d.id)
                      AS current_output_count
           FROM documents d WHERE id = @id"""

    let private deepTargetFromRow
        (archiveDir: string)
        (documentId: int64)
        (row: Map<string, obj>)
        : Result<DeepTarget, string> =
        let reader = Prelude.RowReader(row)
        if
            reader.Int64 "current_input_count" 0L <> 2L
            || reader.Int64 "current_output_count" 0L <> 1L
        then
            Error
                $"Document {documentId} extract and deep-comprehend artifacts are not current"
        else
            let generation: Generation.Token =
                { DocumentId = documentId
                  Value = reader.Int64 "generation" 0L }
            deepArtifactPaths
                documentId archiveDir
                (reader.String "saved_path" "")
                (reader.OptString "folder_path")
            |> Result.map (fun artifacts ->
                { Generation = generation
                  Artifacts = artifacts })

    let private loadDeepTarget
        (db: Algebra.Database)
        (archiveDir: string)
        (documentId: int64)
        : Task<Result<DeepTarget, string>> =
        task {
            let! rows =
                db.execReader
                    deepTargetSql
                    [ ("@id", Database.boxVal documentId) ]
            return
                match rows |> List.tryHead with
                | None -> Error $"Document {documentId} not found"
                | Some row ->
                    deepTargetFromRow
                        archiveDir documentId row
        }

    let private readDeepArtifactFiles
        (fs: Algebra.FileSystem)
        (paths: DeepArtifactPaths) =
        task {
            let! text =
                ArchiveWriter.readExtraction fs paths.Source
            let! comprehension =
                ArchiveWriter.readComprehension fs paths.Folder
            return text, comprehension
        }

    /// Captured under the folder fence with the bytes it describes, before the
    /// slow model call, so a sibling that republishes the shared artifact can
    /// be detected before this call writes anything.
    let private readDeepSnapshot
        db fs
        (target: DeepTarget)
        ()
        : Task<DeepArtifactSnapshot> =
        task {
            let! text, comprehension =
                readDeepArtifactFiles fs target.Artifacts
            let! revision =
                ArtifactRevision.current db target.Artifacts.Fence
            let! current = deepArtifactsCurrent db target.Generation
            return
                { ExtractedText = text |> Option.defaultValue ""
                  Comprehension = comprehension |> Option.defaultValue ""
                  Revision = revision
                  Current = current }
        }

    let private readDeepArtifacts
        db fs
        (target: DeepTarget)
        : Task<DeepArtifactSnapshot option> =
        task {
            let! publication =
                Generation.readArtifactStable
                    db target.Generation target.Artifacts.Fence
                    (readDeepSnapshot db fs target)
            return
                match publication with
                | Generation.Published snapshot
                    when snapshot.Current -> Some snapshot
                | _ -> None
        }

    let private prepareDeepMerge
        documentId fs
        (paths: DeepArtifactPaths)
        deep =
        task {
            let! latest =
                ArchiveWriter.readComprehension fs paths.Folder
            return
                match latest with
                | Some json when not (String.IsNullOrWhiteSpace json) ->
                    DeepExtraction.mergeIntoComprehension json deep
                | _ ->
                    Error
                        $"Document {documentId} has no comprehension (run Pass 1 first)"
        }

    let private publishDeepResult
        db fs
        (target: DeepTarget)
        (revision: ArtifactRevision.Token)
        deep =
        Generation.republishArtifact
            db target.Generation target.Artifacts.Fence revision
            (deepArtifactsCurrentIn target.Generation)
            (fun () ->
                prepareDeepMerge
                    target.Generation.DocumentId
                    fs target.Artifacts deep)
            (fun merged ->
                ArchiveWriter.writeComprehension
                    fs target.Artifacts.Folder merged)

    let private deepResultJson
        (status: string)
        (documentId: int64)
        (comprehension: string)
        : JsonNode =
        let result = JsonObject()
        result["status"] <- JsonValue.Create(status)
        result["document_id"] <- JsonValue.Create(documentId)
        result["comprehension"] <- JsonNode.Parse(comprehension)
        result :> JsonNode

    let private validateDeepDocumentType documentId comprehension =
        if String.IsNullOrWhiteSpace comprehension then
            Error
                $"Document {documentId} has no comprehension (run Pass 1 first)"
        else
            match DeepExtraction.getDocumentType comprehension with
            | None ->
                Error "Cannot determine document_type from comprehension"
            | Some documentType ->
                match DeepExtraction.promptFileForType documentType with
                | Some _ -> Ok documentType
                | None ->
                    Error
                        $"No deep extraction prompt for document type: {documentType}"

    let private publishDeepDelta
        db fs documentId
        (target: DeepTarget)
        (revision: ArtifactRevision.Token)
        deep =
        task {
            match! publishDeepResult db fs target revision deep with
            | Error error -> return errorJson error
            | Ok Generation.Superseded ->
                return
                    errorJson
                        $"Document {documentId} was reflowed, or its shared folder was republished, while deep extraction ran; retry after comprehension is current"
            | Ok (Generation.Published merged) ->
                return
                    deepResultJson
                        "extracted" documentId merged
        }

    let private runDeepExtraction
        db fs
        (deps: DeepExtractionDeps)
        (request: DeepExtractRequest)
        (target: DeepTarget)
        (snapshot: DeepArtifactSnapshot)
        documentType =
        task {
            let sourceHash =
                DeepExtraction.computeHash snapshot.ExtractedText
            if
                not request.Force
                && DeepExtraction.hasValidDeepExtraction
                    snapshot.Comprehension sourceHash
            then
                return
                    deepResultJson
                        "cached" request.DocumentId
                        snapshot.Comprehension
            else
                match!
                    DeepExtraction.extract
                        deps.Chat deps.Registry deps.Provider deps.Model
                        documentType snapshot.ExtractedText ""
                with
                | Error error -> return errorJson error
                | Ok deep ->
                    return!
                        publishDeepDelta
                            db fs request.DocumentId target
                            snapshot.Revision deep
        }

    let private processDeepTarget
        db fs deps request
        (target: DeepTarget) =
        task {
            match! readDeepArtifacts db fs target with
            | None ->
                return
                    errorJson
                        $"Document {request.DocumentId} extract and deep-comprehend artifacts are not current"
            | Some snapshot ->
                match
                    validateDeepDocumentType
                        request.DocumentId snapshot.Comprehension
                with
                | Error error -> return errorJson error
                | Ok documentType ->
                    return!
                        runDeepExtraction
                            db fs deps request target
                            snapshot documentType
        }

    let deepExtract
        (db: Algebra.Database)
        (fs: Algebra.FileSystem)
        (archiveDir: string)
        (deps: DeepExtractionDeps)
        (args: JsonNode)
        : Task<JsonNode> =
        task {
            match deepExtractRequest args with
            | Error error -> return errorJson error
            | Ok request ->
                match!
                    loadDeepTarget
                        db archiveDir request.DocumentId
                with
                | Error error -> return errorJson error
                | Ok target ->
                    return!
                        processDeepTarget
                            db fs deps request target
        }

    // ─── Contact tools ──────────────────────────────────────────────

    let private mapContactRow (row: Map<string, obj>) : JsonObject =
        let r = Prelude.RowReader(row)
        let obj = JsonObject()
        obj["id"] <- JsonValue.Create(r.String "id" "")
        obj["name"] <- JsonValue.Create(r.String "name" "")
        obj["contact_type"] <- JsonValue.Create(r.String "contact_type" "unknown")
        obj["doc_count"] <- JsonValue.Create(r.Int64 "doc_count" 0L)

        r.OptString "email" |> Option.iter (fun v -> obj["email"] <- JsonValue.Create(v))
        r.OptString "abn" |> Option.iter (fun v -> obj["abn"] <- JsonValue.Create(v))
        r.OptString "phone" |> Option.iter (fun v -> obj["phone"] <- JsonValue.Create(v))
        r.OptString "address" |> Option.iter (fun v -> obj["address"] <- JsonValue.Create(v))
        r.OptString "source_sender" |> Option.iter (fun v -> obj["source_sender"] <- JsonValue.Create(v))
        r.OptInt64 "tax_relevant" |> Option.iter (fun v ->
            let boolVal : bool = (v = 1L)
            obj["tax_relevant"] <- JsonValue.Create(boolVal))
        obj["first_seen_at"] <- JsonValue.Create(r.String "first_seen_at" "")
        obj["last_seen_at"] <- JsonValue.Create(r.String "last_seen_at" "")
        obj

    /// List/search contacts with optional filters.
    let listContacts (db: Algebra.Database) (args: JsonNode) : Task<JsonNode> =
        task {
            let query = tryGetString args "query"
            let contactType = tryGetString args "contact_type"
            let taxRelevant = tryGetString args "tax_relevant"
            let limit = tryGetInt args "limit" 50

            let mutable sql = "SELECT c.*, (SELECT COUNT(*) FROM document_contacts dc WHERE dc.contact_id = c.id) AS doc_count FROM contacts c WHERE 1=1"
            let mutable parms : (string * obj) list = []

            match query with
            | Some q ->
                sql <- sql + " AND (c.name LIKE @q OR c.canonical_name LIKE @q OR c.email LIKE @q OR c.abn LIKE @q)"
                parms <- ("@q", Database.boxVal $"%%{q}%%") :: parms
            | None -> ()

            match contactType with
            | Some t ->
                sql <- sql + " AND c.contact_type = @type"
                parms <- ("@type", Database.boxVal t) :: parms
            | None -> ()

            match taxRelevant with
            | Some "true" -> sql <- sql + " AND c.tax_relevant = 1"
            | Some "false" -> sql <- sql + " AND c.tax_relevant = 0"
            | _ -> ()

            sql <- sql + " ORDER BY c.last_seen_at DESC LIMIT @limit"
            parms <- ("@limit", Database.boxVal (int64 limit)) :: parms

            let! rows = db.execReader sql parms

            let result = JsonObject()
            let arr = JsonArray()
            for row in rows do arr.Add(mapContactRow row)
            result["contacts"] <- arr
            result["count"] <- JsonValue.Create(rows.Length)
            return result :> JsonNode
        }

    /// Get contact detail with linked documents.
    let contactDetail (db: Algebra.Database) (args: JsonNode) : Task<JsonNode> =
        task {
            let contactId = tryGetString args "contact_id" |> Option.defaultValue ""

            if String.IsNullOrWhiteSpace(contactId) then
                let err = JsonObject()
                err["error"] <- JsonValue.Create("contact_id parameter is required")
                return err :> JsonNode
            else

            let! contacts =
                db.execReader
                    "SELECT c.*, (SELECT COUNT(*) FROM document_contacts dc WHERE dc.contact_id = c.id) AS doc_count FROM contacts c WHERE c.id = @id"
                    [ ("@id", Database.boxVal contactId) ]

            match contacts with
            | [] ->
                let err = JsonObject()
                err["error"] <- JsonValue.Create($"Contact not found: {contactId}")
                return err :> JsonNode
            | contactRow :: _ ->
                let! docRows =
                    db.execReader
                        """SELECT d.id, d.original_name, d.category, dc.role,
                                  d.sender, d.email_date
                           FROM document_contacts dc
                           JOIN documents d ON d.id = dc.document_id
                           WHERE dc.contact_id = @id
                           ORDER BY d.email_date DESC
                           LIMIT 50"""
                        [ ("@id", Database.boxVal contactId) ]

                let contact = mapContactRow contactRow
                let arr = JsonArray()
                for dRow in docRows do
                    let dr = Prelude.RowReader(dRow)
                    let docObj = JsonObject()
                    docObj["id"] <- JsonValue.Create(dr.Int64 "id" 0L)
                    dr.OptString "original_name" |> Option.iter (fun v -> docObj["original_name"] <- JsonValue.Create(v))
                    dr.OptString "category" |> Option.iter (fun v -> docObj["category"] <- JsonValue.Create(v))
                    dr.OptString "role" |> Option.iter (fun v -> docObj["role"] <- JsonValue.Create(v))
                    dr.OptString "sender" |> Option.iter (fun v -> docObj["sender"] <- JsonValue.Create(v))
                    dr.OptString "email_date" |> Option.iter (fun v -> docObj["email_date"] <- JsonValue.Create(v))
                    arr.Add(docObj)

                contact["documents"] <- arr
                return contact :> JsonNode
        }

    /// Set tax_relevant flag on a contact.
    let setTaxRelevant (db: Algebra.Database) (args: JsonNode) : Task<JsonNode> =
        task {
            let contactId = tryGetString args "contact_id" |> Option.defaultValue ""
            let taxRelevant = tryGetString args "tax_relevant"

            if String.IsNullOrWhiteSpace(contactId) then
                let err = JsonObject()
                err["error"] <- JsonValue.Create("contact_id parameter is required")
                return err :> JsonNode
            else

            let taxVal : obj =
                match taxRelevant with
                | Some "true" -> Database.boxVal 1L
                | Some "false" -> Database.boxVal 0L
                | _ -> box DBNull.Value

            let! rows =
                db.execNonQuery
                    "UPDATE contacts SET tax_relevant = @tax WHERE id = @id"
                    [ ("@id", Database.boxVal contactId)
                      ("@tax", taxVal) ]

            let result = JsonObject()
            if rows > 0 then
                result["status"] <- JsonValue.Create("updated")
                result["contact_id"] <- JsonValue.Create(contactId)
                result["tax_relevant"] <- JsonValue.Create(taxRelevant |> Option.defaultValue "null")
            else
                result["error"] <- JsonValue.Create($"Contact not found: {contactId}")
            return result :> JsonNode
        }

    type private BackfillTarget =
        { Generation: Generation.Token
          Folder: string
          Fence: PublicationFence.ArtifactFolder
          Sender: string option }

    type private BackfillTally =
        { Linked: int
          Skipped: int
          Superseded: int }

    let private backfillTarget
        (archiveDir: string)
        (row: Map<string, obj>)
        : BackfillTarget option =
        let reader = Prelude.RowReader(row)
        let documentId = reader.Int64 "id" 0L
        let savedPath = reader.String "saved_path" ""
        match
            PublicationFence.ArtifactFolder.tryFromMetadata
                archiveDir savedPath
                (reader.OptString "folder_path")
        with
        | None -> None
        | Some fence ->
            match PublicationFence.ArtifactFolder.resolve archiveDir fence with
            | Error _ -> None
            | Ok folder ->
                Some
                    { Generation =
                        { DocumentId = documentId
                          Value = reader.Int64 "generation" 0L }
                      Folder = folder
                      Fence = fence
                      Sender = reader.OptString "sender" }

    let private backfillOne
        (db: Algebra.Database)
        (fs: Algebra.FileSystem)
        (logger: Algebra.Logger)
        (archiveDir: string)
        (tally: BackfillTally)
        (row: Map<string, obj>)
        : Task<BackfillTally> =
        task {
            match backfillTarget archiveDir row with
            | None ->
                return { tally with Skipped = tally.Skipped + 1 }
            | Some target ->
                let! comprehension =
                    Generation.readArtifactStable
                        db target.Generation target.Fence (fun () ->
                            ArchiveWriter.readComprehension
                                fs target.Folder)
                match comprehension with
                | Generation.Superseded ->
                    return
                        { tally with
                            Superseded = tally.Superseded + 1 }
                | Generation.Published None ->
                    return { tally with Skipped = tally.Skipped + 1 }
                | Generation.Published (Some json)
                    when String.IsNullOrWhiteSpace json ->
                    return { tally with Skipped = tally.Skipped + 1 }
                | Generation.Published (Some json) ->
                    let! publication =
                        ContactExtraction.harvestAndLinkAt
                            db logger target.Generation
                            json target.Sender
                    return
                        match publication with
                        | Generation.Published () ->
                            { tally with Linked = tally.Linked + 1 }
                        | Generation.Superseded ->
                            { tally with
                                Superseded = tally.Superseded + 1 }
        }

    /// Backfill contacts from already-comprehended documents.
    let contactsBackfill
        (db: Algebra.Database)
        (fs: Algebra.FileSystem)
        (archiveDir: string)
        (logger: Algebra.Logger)
        (_args: JsonNode)
        : Task<JsonNode> =
        task {
            let! unlinked =
                db.execReader
                    """SELECT d.id, d.saved_path, d.folder_path, d.sender,
                              COALESCE(
                                (SELECT generation
                                 FROM document_generations
                                 WHERE document_id = d.id), 0)
                                  AS generation
                       FROM documents d
                       WHERE EXISTS (
                           SELECT 1 FROM stage_completions sc
                           WHERE sc.document_id = d.id
                             AND sc.stage_name = 'deep-comprehend')
                         AND EXISTS (
                           SELECT 1 FROM comprehension c
                           WHERE c.document_id = d.id)
                         AND NOT EXISTS (
                           SELECT 1 FROM document_contacts dc
                           WHERE dc.document_id = d.id)
                       LIMIT 500"""
                    []

            let! tally =
                unlinked
                |> Prelude.foldTask
                    (backfillOne db fs logger archiveDir)
                    { Linked = 0
                      Skipped = 0
                      Superseded = 0 }

            let result = JsonObject()
            result["status"] <- JsonValue.Create("backfill_complete")
            result["processed"] <- JsonValue.Create(tally.Linked)
            result["skipped"] <- JsonValue.Create(tally.Skipped)
            result["superseded"] <- JsonValue.Create(tally.Superseded)
            result["remaining"] <-
                JsonValue.Create(
                    unlinked.Length
                    - tally.Linked
                    - tally.Skipped
                    - tally.Superseded)
            return result :> JsonNode
        }
