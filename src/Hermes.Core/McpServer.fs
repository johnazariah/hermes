namespace Hermes.Core

#nowarn "3261"

open System
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks

/// MCP (Model Context Protocol) server over JSON-RPC 2.0.
/// Handles initialize, tools/list, tools/call.
/// Parameterised over algebras for testability.
[<RequireQualifiedAccess>]
module McpServer =

    // ─── JSON-RPC types ──────────────────────────────────────────────

    type JsonRpcRequest =
        { Id: JsonNode option
          Method: string
          Params: JsonNode option }

    type JsonRpcResponse =
        { Id: JsonNode option
          Result: JsonNode option
          Error: JsonObject option }

    // ─── Tool definitions ────────────────────────────────────────────

    type ToolDef =
        { Name: string
          Description: string
          InputSchema: JsonObject }

    let private mkSchema (properties: (string * JsonObject) list) (required: string list) : JsonObject =
        let schema = JsonObject()
        schema["type"] <- JsonValue.Create("object")

        let props = JsonObject()

        for (name, propSchema) in properties do
            props[name] <- propSchema

        schema["properties"] <- props

        if not required.IsEmpty then
            let arr = JsonArray()

            for r in required do
                arr.Add(JsonValue.Create(r))

            schema["required"] <- arr

        schema

    let private stringProp (desc: string) : JsonObject =
        let p = JsonObject()
        p["type"] <- JsonValue.Create("string")
        p["description"] <- JsonValue.Create(desc)
        p

    let private intProp (desc: string) : JsonObject =
        let p = JsonObject()
        p["type"] <- JsonValue.Create("integer")
        p["description"] <- JsonValue.Create(desc)
        p

    let toolDefinitions: ToolDef list =
        [ { Name = "hermes_search"
            Description =
                "Full-text search over indexed documents. Returns matching documents with relevance scores."
            InputSchema =
                mkSchema
                    [ "query", stringProp "Search query string"
                      "category", stringProp "Filter by category (optional)"
                      "limit", intProp "Maximum results to return (default 20)" ]
                    [ "query" ] }
          { Name = "hermes_get_document"
            Description =
                "Get full metadata and extracted text for a document by ID or path."
            InputSchema =
                mkSchema
                    [ "id", intProp "Document ID"
                      "path", stringProp "Document saved path" ]
                    [] }
          { Name = "hermes_list_categories"
            Description = "List all document categories with counts."
            InputSchema = mkSchema [] [] }
          { Name = "hermes_stats"
            Description =
                "Get summary statistics: total documents, emails, categories, extraction and embedding coverage."
            InputSchema = mkSchema [] [] }
          { Name = "hermes_read_file"
            Description =
                "Read a text file from the archive. Path is relative to the archive directory."
            InputSchema =
                mkSchema [ "path", stringProp "Relative path within the archive" ] [ "path" ] }
          { Name = "hermes_list_reminders"
            Description =
                "List active bill reminders and action items with amounts and due dates."
            InputSchema =
                mkSchema
                    [ "status", stringProp "Filter: 'active', 'overdue', 'upcoming', 'completed', 'all' (default: active)"
                      "limit", intProp "Max results (default 20)" ]
                    [] }
          { Name = "hermes_update_reminder"
            Description =
                "Mark a reminder as paid, snoozed, or dismissed."
            InputSchema =
                mkSchema
                    [ "reminder_id", intProp "Reminder ID"
                      "action", stringProp "One of: 'complete', 'snooze', 'dismiss'"
                      "snooze_days", intProp "Days to snooze (default 7, only for snooze action)" ]
                    [ "reminder_id"; "action" ] }
          { Name = "hermes_list_documents"
            Description =
                "List documents with cursor-based pagination. Returns documents with id > since_id."
            InputSchema =
                mkSchema
                    [ "since_id", intProp "Cursor position — returns docs with id > this value (default 0)"
                      "category", stringProp "Filter by category (optional)"
                      "limit", intProp "Maximum results (default 100)" ]
                    [] }
          { Name = "hermes_get_feed_stats"
            Description = "Get document feed statistics: total count, max ID, category breakdown."
            InputSchema = mkSchema [] [] }
          { Name = "hermes_get_document_content"
            Description =
                "Get document content in text, markdown, or raw format."
            InputSchema =
                mkSchema
                    [ "document_id", intProp "Document ID (required)"
                      "format", stringProp "Content format: 'text', 'markdown', or 'raw' (default 'markdown')" ]
                    [ "document_id" ] }
          { Name = "hermes_reclassify"
            Description =
                "Move a document to a different category. Moves file on disk and updates DB."
            InputSchema =
                mkSchema
                    [ "document_id", intProp "Document ID (required)"
                      "new_category", stringProp "Target category (required)" ]
                    [ "document_id"; "new_category" ] }
          { Name = "hermes_reextract"
            Description =
                "Clear extraction fields and re-queue document for extraction on next sync cycle."
            InputSchema =
                mkSchema [ "document_id", intProp "Document ID (required)" ] [ "document_id" ] }
          { Name = "hermes_get_processing_queue"
            Description =
                "Get processing queue overview: unclassified, unextracted, and unembedded document counts."
            InputSchema =
                mkSchema [ "limit", intProp "Sample IDs per stage (default 10)" ] [] } ]

    // ─── Request parsing ─────────────────────────────────────────────

    let parseRequest (json: string) : Result<JsonRpcRequest, string> =
        try
            let doc = JsonDocument.Parse(json)
            let root = doc.RootElement

            let id =
                match root.TryGetProperty("id") with
                | true, idElem ->
                    let parsed: JsonNode | null = JsonNode.Parse(idElem.GetRawText())

                    match parsed with
                    | null -> None
                    | v -> Some v
                | false, _ -> None

            let methodName =
                match root.TryGetProperty("method") with
                | true, m -> m.GetString()
                | false, _ -> null

            match methodName with
            | null -> Error "Missing 'method' field"
            | m ->
                let parms =
                    match root.TryGetProperty("params") with
                    | true, p ->
                        let raw = p.GetRawText()

                        if raw = "null" then
                            None
                        else
                            let parsed: JsonNode | null = JsonNode.Parse(raw)

                            match parsed with
                            | null -> None
                            | v -> Some v
                    | false, _ -> None

                Ok
                    { Id = id
                      Method = m
                      Params = parms }
        with ex ->
            Error $"Invalid JSON: {ex.Message}"

    // ─── Response serialisation ──────────────────────────────────────

    let private jsonOptions =
        JsonSerializerOptions(WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    let serialiseResponse (resp: JsonRpcResponse) : string =
        let obj = JsonObject()
        obj["jsonrpc"] <- JsonValue.Create("2.0")

        match resp.Id with
        | Some id -> obj["id"] <- id
        | None -> ()

        match resp.Error with
        | Some err -> obj["error"] <- err
        | None ->
            match resp.Result with
            | Some r -> obj["result"] <- r
            | None -> ()

        obj.ToJsonString(jsonOptions)

    let private makeError (id: JsonNode option) (code: int) (message: string) : JsonRpcResponse =
        let err = JsonObject()
        err["code"] <- JsonValue.Create(code)
        err["message"] <- JsonValue.Create(message)

        { Id = id
          Result = None
          Error = Some err }

    let private makeResult (id: JsonNode option) (result: JsonNode) : JsonRpcResponse =
        { Id = id
          Result = Some result
          Error = None }

    // ─── Tool dispatch ───────────────────────────────────────────────

    /// Safely access a JsonNode property, returning option.
    let private tryGetNode (node: JsonNode) (key: string) : JsonNode option =
        let result: JsonNode | null = node.[key]

        match result with
        | null -> None
        | v -> Some v

    let private handleToolCall
        (db: Algebra.Database)
        (fs: Algebra.FileSystem)
        (logger: Algebra.Logger)
        (archiveDir: string)
        (toolName: string)
        (args: JsonNode option)
        : Task<Result<JsonNode, string>> =
        task {
            let toolArgs =
                match args with
                | Some a ->
                    tryGetNode a "arguments" |> Option.defaultValue a
                | None -> JsonObject() :> JsonNode

            match toolName with
            | "hermes_search" ->
                let! result = McpTools.search db toolArgs
                return Ok result
            | "hermes_get_document" ->
                let! result = McpTools.getDocument db toolArgs
                return Ok result
            | "hermes_list_categories" ->
                let! result = McpTools.listCategories db toolArgs
                return Ok result
            | "hermes_stats" ->
                let! result = McpTools.stats db toolArgs
                return Ok result
            | "hermes_read_file" ->
                let! result = McpTools.readFile fs archiveDir toolArgs
                return Ok result
            | "hermes_list_reminders" ->
                let! result = McpTools.listReminders db toolArgs
                return Ok result
            | "hermes_update_reminder" ->
                let! result = McpTools.updateReminder db toolArgs
                return Ok result
            | "hermes_list_documents" ->
                let! result = McpTools.listDocumentsFeed db toolArgs
                return Ok result
            | "hermes_get_feed_stats" ->
                let! result = McpTools.getFeedStats db toolArgs
                return Ok result
            | "hermes_get_document_content" ->
                let! result = McpTools.getDocumentContent db fs archiveDir toolArgs
                return Ok result
            | "hermes_reclassify" ->
                let! result = McpTools.reclassifyDocument db fs archiveDir toolArgs
                return Ok result
            | "hermes_reextract" ->
                let! result = McpTools.reextractDocument db toolArgs
                return Ok result
            | "hermes_get_processing_queue" ->
                let! result = McpTools.getProcessingQueue db toolArgs
                return Ok result
            | unknown ->
                logger.warn $"Unknown tool: {unknown}"
                return Error $"Unknown tool: {unknown}"
        }

    // ─── Main dispatch ───────────────────────────────────────────────

    /// Process a single JSON-RPC request and return a response.
    let handleRequest
        (db: Algebra.Database)
        (fs: Algebra.FileSystem)
        (logger: Algebra.Logger)
        (archiveDir: string)
        (request: JsonRpcRequest)
        : Task<JsonRpcResponse> =
        task {
            match request.Method with
            | "initialize" ->
                let result = JsonObject()
                result["protocolVersion"] <- JsonValue.Create("2024-11-05")

                let caps = JsonObject()
                let toolsCap = JsonObject()
                caps["tools"] <- toolsCap
                result["capabilities"] <- caps

                let info = JsonObject()
                info["name"] <- JsonValue.Create("hermes")
                info["version"] <- JsonValue.Create("0.1.0")
                result["serverInfo"] <- info

                return makeResult request.Id (result :> JsonNode)

            | "notifications/initialized" ->
                // Client acknowledgement — no response needed for notifications
                return makeResult request.Id (JsonObject() :> JsonNode)

            | "tools/list" ->
                let tools = JsonArray()

                for toolDef in toolDefinitions do
                    let tool = JsonObject()
                    tool["name"] <- JsonValue.Create(toolDef.Name)
                    tool["description"] <- JsonValue.Create(toolDef.Description)
                    tool["inputSchema"] <- toolDef.InputSchema
                    tools.Add(tool)

                let result = JsonObject()
                result["tools"] <- tools
                return makeResult request.Id (result :> JsonNode)

            | "tools/call" ->
                let toolName =
                    match request.Params with
                    | Some p ->
                        tryGetNode p "name"
                        |> Option.map (fun n -> n.GetValue<string>())
                    | None -> None

                match toolName with
                | None -> return makeError request.Id -32602 "Missing tool name"
                | Some name ->
                    let toolArgs =
                        match request.Params with
                        | Some p -> tryGetNode p "arguments"
                        | None -> None

                    let! callResult = handleToolCall db fs logger archiveDir name toolArgs

                    match callResult with
                    | Ok resultNode ->
                        let content = JsonArray()
                        let contentItem = JsonObject()
                        contentItem["type"] <- JsonValue.Create("text")

                        contentItem["text"] <-
                            JsonValue.Create(resultNode.ToJsonString(jsonOptions))

                        content.Add(contentItem)

                        let result = JsonObject()
                        result["content"] <- content
                        return makeResult request.Id (result :> JsonNode)
                    | Error msg -> return makeError request.Id -32602 msg

            | unknown ->
                logger.debug $"Unknown method: {unknown}"
                return makeError request.Id -32601 $"Method not found: {unknown}"
        }

    /// Parse a JSON string, process the request, and return a JSON response string.
    let processMessage
        (db: Algebra.Database)
        (fs: Algebra.FileSystem)
        (logger: Algebra.Logger)
        (archiveDir: string)
        (message: string)
        : Task<string> =
        task {
            match parseRequest message with
            | Error msg ->
                let resp = makeError None -32700 msg
                return serialiseResponse resp
            | Ok request ->
                let! resp = handleRequest db fs logger archiveDir request
                return serialiseResponse resp
        }
