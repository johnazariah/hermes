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

    let private boolProp (desc: string) : JsonObject =
        let p = JsonObject()
        p["type"] <- JsonValue.Create("boolean")
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
          { Name = "hermes_reflow"
            Description =
                "Request a DAG-safe reflow. Defaults to dry_run; use mode='apply' to invalidate and re-queue."
            InputSchema =
                mkSchema
                    [ "document_id", intProp "Document ID (required)"
                      "operation", stringProp "One of: reextract, recomprehend, reembed"
                      "mode", stringProp "One of: dry_run (default), apply" ]
                    [ "document_id"; "operation" ] }
          { Name = "hermes_reflow_status"
            Description = "Get reflow operation status and per-stage outcomes."
            InputSchema =
                mkSchema [ "operation_id", intProp "Reflow operation ID (required)" ] [ "operation_id" ] }
          { Name = "hermes_reextract"
            Description =
                "DAG-safe legacy alias for reextract apply."
            InputSchema =
                mkSchema [ "document_id", intProp "Document ID (required)" ] [ "document_id" ] }
          { Name = "hermes_get_processing_queue"
            Description =
                "Get processing queue overview: unclassified, unextracted, and unembedded document counts."
            InputSchema =
                mkSchema [ "limit", intProp "Sample IDs per stage (default 10)" ] [] }
          { Name = "hermes_deep_extract"
            Description =
                "Run deep field extraction (Pass 2) on a comprehended document. Uses a type-specific prompt to extract detailed structured data (earnings, transactions, expenses). Returns enriched comprehension with deep_fields."
            InputSchema =
                mkSchema
                    [ "document_id", intProp "Document ID (required)"
                      "force", boolProp "Re-extract even if cached (default false)" ]
                    [ "document_id" ] }
          { Name = "hermes_contacts"
            Description =
                "List or search contacts in the address book. Contacts are automatically harvested from document comprehension."
            InputSchema =
                mkSchema
                    [ "query", stringProp "Search by name, email, or ABN"
                      "contact_type", stringProp "Filter: supplier, employer, government, unknown"
                      "tax_relevant", stringProp "Filter: true, false, or omit for all"
                      "limit", intProp "Max results (default 50)" ]
                    [] }
          { Name = "hermes_contact_detail"
            Description =
                "Get contact details with linked documents."
            InputSchema =
                mkSchema
                    [ "contact_id", stringProp "Contact ID (required)" ]
                    [ "contact_id" ] }
          { Name = "hermes_contact_set_tax_relevant"
            Description =
                "Mark a contact as tax-relevant or not. Tax-relevant contacts auto-trigger deep extraction for future documents."
            InputSchema =
                mkSchema
                    [ "contact_id", stringProp "Contact ID (required)"
                      "tax_relevant", stringProp "true, false, or null to clear" ]
                    [ "contact_id" ] }
          { Name = "hermes_contacts_backfill"
            Description =
                "Backfill contacts from already-comprehended documents that haven't been linked yet. Run once after enabling the address book."
            InputSchema =
                mkSchema [] [] } ]

    // ─── tools/list projection ───────────────────────────────────────

    /// One tools/list entry. Schema nodes are shared module-level state and a
    /// JsonNode may have only one parent, so every response carries its own
    /// detached copy.
    let private toolEntry (toolDef: ToolDef) : JsonObject =
        let tool = JsonObject()
        tool["name"] <- JsonValue.Create(toolDef.Name)
        tool["description"] <- JsonValue.Create(toolDef.Description)
        tool["inputSchema"] <- toolDef.InputSchema.DeepClone()
        tool

    /// Fresh tool array for a single response, in declaration order.
    let private toolEntries () : JsonArray =
        let tools = JsonArray()
        toolDefinitions
        |> List.iter (fun toolDef -> tools.Add(toolEntry toolDef))
        tools

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

    // ─── Tool content envelope ───────────────────────────────────────

    /// MCP text content for a tool payload.
    let private textContent (payload: JsonNode) : JsonObject =
        let item = JsonObject()
        item["type"] <- JsonValue.Create("text")
        item["text"] <- JsonValue.Create(payload.ToJsonString(jsonOptions))

        let content = JsonArray()
        content.Add(item)

        let result = JsonObject()
        result["content"] <- content
        result

    /// Successful tool content - no isError marker (MCP default is false).
    let private successContent (payload: JsonNode) : JsonNode =
        textContent payload :> JsonNode

    /// Truthful tool failure - identical content shape plus the isError
    /// marker, so clients cannot read a failure as a successful call.
    let private errorContent (payload: JsonNode) : JsonNode =
        let result = textContent payload
        result["isError"] <- JsonValue.Create(true)
        result :> JsonNode

    // ─── Tool dispatch ───────────────────────────────────────────────

    /// Safely access a JsonNode property, returning option.
    /// Named members exist only on JSON objects: `node.[key]` raises for
    /// arrays and scalars, so kind-checked access keeps every container total.
    let private tryGetNode (node: JsonNode) (key: string) : JsonNode option =
        match node with
        | :? JsonObject as properties ->
            match properties.TryGetPropertyValue(key) with
            | true, value -> Option.ofObj value
            | false, _ -> None
        | _ -> None

    /// How a tool call must be reported over the protocol.
    /// Succeeded and Failed are both tool content; Failed carries isError.
    /// Rejected is a JSON-RPC invalid-params error.
    type private CallOutcome =
        | Succeeded of JsonNode
        | Failed of JsonNode
        | Rejected of string

    /// Lift a tool that always yields a payload.
    let private succeeded (payload: Task<JsonNode>) : Task<CallOutcome> =
        task {
            let! result = payload
            return Succeeded result
        }

    /// Lift a tool that separates schema violations from domain failures.
    let private attempted
        (outcome: Task<Result<JsonNode, McpTools.ToolFailure>>)
        : Task<CallOutcome> =
        task {
            let! result = outcome
            return
                match result with
                | Ok payload -> Succeeded payload
                | Error (McpTools.DomainFailure message) ->
                    Failed(McpTools.errorJson message)
                | Error (McpTools.InvalidArguments message) ->
                    Rejected message
        }

    /// A validated tools/call envelope: an object `params` carrying a string
    /// tool name and an object argument set.
    type private ToolCall =
        { Name: string
          Arguments: JsonNode }

    /// Named members exist only on JSON objects, so any other container kind is
    /// a protocol error rather than a silently empty member set.
    let private requireObject
        (label: string)
        (node: JsonNode)
        : Result<JsonNode, string> =
        match node with
        | :? JsonObject -> Ok node
        | _ -> Error $"{label} must be an object"

    /// Tool name lookup. A missing or non-string name is invalid params.
    let private toolNameOf (parms: JsonNode) : Result<string, string> =
        McpTools.Args.text parms "name"
        |> Result.bind (function
            | Some name -> Ok name
            | None -> Error "Missing tool name")

    /// Clients nest tool arguments under "arguments"; an absent member is an
    /// empty argument set and a non-object member is invalid params, never a
    /// flat or empty one.
    let private toolArgumentsOf
        (parms: JsonNode)
        : Result<JsonNode, string> =
        match tryGetNode parms "arguments" with
        | None -> Ok(JsonObject() :> JsonNode)
        | Some arguments -> requireObject "arguments" arguments

    /// Validates the whole envelope before any tool runs.
    let private toolCallOf
        (parms: JsonNode option)
        : Result<ToolCall, string> =
        match parms with
        | None -> Error "Missing tool name"
        | Some node ->
            requireObject "params" node
            |> Result.bind (fun container ->
                match toolNameOf container, toolArgumentsOf container with
                | Error message, _ -> Error message
                | _, Error message -> Error message
                | Ok name, Ok arguments ->
                    Ok { Name = name; Arguments = arguments })

    let private handleToolCall
        (db: Algebra.Database)
        (reflowDb: Algebra.Database)
        (fs: Algebra.FileSystem)
        (logger: Algebra.Logger)
        (clock: Algebra.Clock)
        (dag: PipelineV5.Dag)
        (archiveDir: string)
        (deepDeps: McpTools.DeepExtractionDeps option)
        (toolName: string)
        (toolArgs: JsonNode)
        : Task<CallOutcome> =
        match toolName with
        | "hermes_search" -> succeeded (McpTools.search db toolArgs)
        | "hermes_get_document" -> succeeded (McpTools.getDocument db toolArgs)
        | "hermes_list_categories" -> succeeded (McpTools.listCategories db toolArgs)
        | "hermes_stats" -> succeeded (McpTools.stats db toolArgs)
        | "hermes_read_file" -> succeeded (McpTools.readFile fs archiveDir toolArgs)
        | "hermes_list_reminders" -> succeeded (McpTools.listReminders db clock toolArgs)
        | "hermes_update_reminder" -> succeeded (McpTools.updateReminder db clock toolArgs)
        | "hermes_list_documents" -> succeeded (McpTools.listDocumentsFeed db toolArgs)
        | "hermes_get_feed_stats" -> succeeded (McpTools.getFeedStats db toolArgs)
        | "hermes_get_document_content" ->
            succeeded (McpTools.getDocumentContent db fs archiveDir toolArgs)
        | "hermes_reclassify" ->
            succeeded (McpTools.reclassifyDocument db fs archiveDir toolArgs)
        | "hermes_reflow" ->
            attempted (McpTools.reflowDocument reflowDb logger dag toolArgs)
        | "hermes_reflow_status" ->
            attempted (McpTools.reflowStatus reflowDb dag toolArgs)
        | "hermes_reextract" ->
            attempted (McpTools.reextractDocument reflowDb logger dag toolArgs)
        | "hermes_get_processing_queue" ->
            succeeded (McpTools.getProcessingQueue db toolArgs)
        | "hermes_deep_extract" ->
            match deepDeps with
            | None ->
                Task.FromResult(
                    Rejected "Deep extraction not configured (no chat provider)")
            | Some deps ->
                succeeded (McpTools.deepExtract db fs archiveDir deps toolArgs)
        | "hermes_contacts" -> succeeded (McpTools.listContacts db toolArgs)
        | "hermes_contact_detail" ->
            succeeded (McpTools.contactDetail db toolArgs)
        | "hermes_contact_set_tax_relevant" ->
            succeeded (McpTools.setTaxRelevant db toolArgs)
        | "hermes_contacts_backfill" ->
            succeeded (McpTools.contactsBackfill db fs archiveDir logger toolArgs)
        | unknown ->
            logger.warn $"Unknown tool: {unknown}"
            Task.FromResult(Rejected $"Unknown tool: {unknown}")

    // ─── Main dispatch ───────────────────────────────────────────────

    /// Process a single JSON-RPC request and return a response.
    let private handleRequestWithReflowDb
        (db: Algebra.Database)
        (reflowDb: Algebra.Database)
        (fs: Algebra.FileSystem)
        (logger: Algebra.Logger)
        (clock: Algebra.Clock)
        (dag: PipelineV5.Dag)
        (archiveDir: string)
        (deepDeps: McpTools.DeepExtractionDeps option)
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
                let result = JsonObject()
                result["tools"] <- toolEntries ()
                return makeResult request.Id (result :> JsonNode)

            | "tools/call" ->
                match toolCallOf request.Params with
                | Error message -> return makeError request.Id -32602 message
                | Ok call ->
                    let! callResult =
                        handleToolCall
                            db reflowDb fs logger clock dag archiveDir deepDeps
                            call.Name call.Arguments

                    match callResult with
                    | Succeeded payload ->
                        return makeResult request.Id (successContent payload)
                    | Failed payload ->
                        return makeResult request.Id (errorContent payload)
                    | Rejected message ->
                        return makeError request.Id -32602 message

            | unknown ->
                logger.debug $"Unknown method: {unknown}"
                return makeError request.Id -32601 $"Method not found: {unknown}"
        }

    /// Backward-compatible request handler for single-connection hosts.
    let handleRequest
        (db: Algebra.Database)
        (fs: Algebra.FileSystem)
        (logger: Algebra.Logger)
        (clock: Algebra.Clock)
        (dag: PipelineV5.Dag)
        (archiveDir: string)
        (deepDeps: McpTools.DeepExtractionDeps option)
        (request: JsonRpcRequest)
        : Task<JsonRpcResponse> =
        handleRequestWithReflowDb
            db db fs logger clock dag archiveDir deepDeps request

    /// Parse and process a message using a dedicated reflow connection.
    let processMessageWithReflowDb
        (db: Algebra.Database)
        (reflowDb: Algebra.Database)
        (fs: Algebra.FileSystem)
        (logger: Algebra.Logger)
        (clock: Algebra.Clock)
        (dag: PipelineV5.Dag)
        (archiveDir: string)
        (deepDeps: McpTools.DeepExtractionDeps option)
        (message: string)
        : Task<string> =
        task {
            match parseRequest message with
            | Error msg ->
                let resp = makeError None -32700 msg
                return serialiseResponse resp
            | Ok request ->
                let! resp =
                    handleRequestWithReflowDb
                        db reflowDb fs logger clock dag archiveDir deepDeps request
                return serialiseResponse resp
        }

    /// Backward-compatible message processor for tests and single-connection hosts.
    let processMessage
        (db: Algebra.Database)
        (fs: Algebra.FileSystem)
        (logger: Algebra.Logger)
        (clock: Algebra.Clock)
        (dag: PipelineV5.Dag)
        (archiveDir: string)
        (deepDeps: McpTools.DeepExtractionDeps option)
        (message: string)
        : Task<string> =
        processMessageWithReflowDb
            db db fs logger clock dag archiveDir deepDeps message
