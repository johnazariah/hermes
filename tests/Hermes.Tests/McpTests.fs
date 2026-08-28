module Hermes.Tests.McpTests

#nowarn "3261"
#nowarn "3264"

open System
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open Hermes.Core

// ─── Test helpers ────────────────────────────────────────────────────

let insertTestDocument (db: Algebra.Database) (category: string) (name: string) : Task<unit> =
    task {
        let! _ =
            db.execNonQuery
                """INSERT INTO documents (source_type, saved_path, category, sha256, original_name, sender, subject)
                   VALUES ('manual_drop', @path, @cat, @sha, @name, @sender, @subject)"""
                ([ ("@path", Database.boxVal ($"{category}/{name}"))
                   ("@cat", Database.boxVal category)
                   ("@sha", Database.boxVal (Guid.NewGuid().ToString("N")))
                   ("@name", Database.boxVal name)
                   ("@sender", Database.boxVal "test@example.com")
                   ("@subject", Database.boxVal $"Test document {name}") ] : (string * obj) list)

        ()
    }

let private markExtractCurrent
    (db: Algebra.Database)
    (documentId: int64)
    : Task<unit> =
    task {
        do! PipelineV5.initSchema db []
        let! _ =
            db.execNonQuery
                """INSERT OR IGNORE INTO stage_completions
                     (document_id, stage_name)
                   VALUES (@doc, 'extract')"""
                [ ("@doc", Database.boxVal documentId) ]
        return ()
    }

// ─── JSON-RPC request/response format ────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpServer_ParseRequest_ValidRequest_ReturnsOk`` () =
    let json =
        """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":null}"""

    match McpServer.parseRequest json with
    | Ok req ->
        Assert.Equal("tools/list", req.Method)
        Assert.True(req.Id.IsSome)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpServer_ParseRequest_MissingMethod_ReturnsError`` () =
    let json = """{"jsonrpc":"2.0","id":1}"""

    match McpServer.parseRequest json with
    | Error msg -> Assert.Contains("method", msg.ToLower())
    | Ok _ -> failwith "Expected Error for missing method"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpServer_ParseRequest_InvalidJson_ReturnsError`` () =
    match McpServer.parseRequest "not json" with
    | Error msg -> Assert.Contains("Invalid JSON", msg)
    | Ok _ -> failwith "Expected Error for invalid JSON"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpServer_SerialiseResponse_ContainsJsonRpcVersion`` () =
    let resp: McpServer.JsonRpcResponse =
        { Id = Some(JsonValue.Create(1) :> JsonNode)
          Result = Some(JsonObject() :> JsonNode)
          Error = None }

    let json = McpServer.serialiseResponse resp
    Assert.Contains("\"jsonrpc\":\"2.0\"", json)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpServer_SerialiseResponse_ErrorResponse_ContainsErrorField`` () =
    let err = JsonObject()
    err["code"] <- JsonValue.Create(-32601)
    err["message"] <- JsonValue.Create("Method not found")

    let resp: McpServer.JsonRpcResponse =
        { Id = Some(JsonValue.Create(1) :> JsonNode)
          Result = None
          Error = Some err }

    let json = McpServer.serialiseResponse resp
    Assert.Contains("error", json)
    Assert.Contains("-32601", json)

// ─── Tool dispatch routing ───────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_Dispatch_Initialize_ReturnsCapabilities`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        let logger = TestHelpers.silentLogger

        try
            let json =
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}"""

            let! response = McpServer.processMessage db m.Fs logger TestHelpers.defaultClock (TestHelpers.standardV5Dag ()) "/archive" None json
            let doc = JsonDocument.Parse(response)
            let root = doc.RootElement

            Assert.True(root.TryGetProperty("result") |> fst)
            let result = root.GetProperty("result")
            Assert.Equal("2024-11-05", result.GetProperty("protocolVersion").GetString())
            Assert.True(result.TryGetProperty("capabilities") |> fst)
            Assert.True(result.TryGetProperty("serverInfo") |> fst)
        finally
            db.dispose ()
    }

let private reflowLastRowId (db: Algebra.Database) =
    task {
        let! value = db.execScalar "SELECT last_insert_rowid()" []
        return match value with :? int64 as i -> i | _ -> 0L
    }

let private reflowInnerContent (response: string) : JsonElement =
    let outer = JsonDocument.Parse(response).RootElement
    let text =
        outer.GetProperty("result").GetProperty("content").[0].GetProperty("text").GetString()
        |> Option.ofObj
        |> Option.defaultValue "{}"
    (JsonDocument.Parse(text).RootElement).Clone()

/// True when tools/call reported a truthful tool failure (MCP isError marker).
let private toolCallIsError (response: string) : bool =
    match (JsonDocument.Parse(response).RootElement).TryGetProperty("result") with
    | true, result ->
        match result.TryGetProperty("isError") with
        | true, marker -> marker.GetBoolean()
        | _ -> false
    | _ -> false

/// The JSON-RPC error object of a protocol-level rejection.
let private jsonRpcError (response: string) : JsonElement =
    ((JsonDocument.Parse(response).RootElement).GetProperty("error")).Clone()

let private elementText (element: JsonElement) (name: string) : string =
    element.GetProperty(name).GetString() |> Option.ofObj |> Option.defaultValue ""

/// Issue a tools/call request with a raw JSON arguments object.
let private callTool
    (db: Algebra.Database)
    (m: TestHelpers.MemFs)
    (tool: string)
    (argsJson: string)
    =
    let json =
        $"""{{"jsonrpc":"2.0","id":90,"method":"tools/call","params":{{"name":"{tool}","arguments":{argsJson}}}}}"""
    McpServer.processMessage
        db m.Fs TestHelpers.silentLogger TestHelpers.defaultClock
        (TestHelpers.standardV5Dag ()) "/archive" None json

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_Reflow_DryRun_DefaultsSafelyAndDoesNotWrite`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            do! TestHelpers.initV5 db
            do! insertTestDocument db "invoices" "dryrun.pdf"
            let! docId = reflowLastRowId db
            let json =
                sprintf """{"jsonrpc":"2.0","id":30,"method":"tools/call","params":{"name":"hermes_reflow","arguments":{"document_id":%d,"operation":"reembed"}}}""" docId
            let! response =
                McpServer.processMessage db m.Fs TestHelpers.silentLogger TestHelpers.defaultClock
                    (TestHelpers.standardV5Dag ()) "/archive" None json
            let inner = reflowInnerContent response
            Assert.False(toolCallIsError response)
            let stages = inner.GetProperty("plan").GetProperty("invalidated_stages")
            Assert.Equal(1, stages.GetArrayLength())
            Assert.Equal("embed", stages.[0].GetString())
            let! count = db.execScalar "SELECT count(*) FROM reflow_operations" []
            Assert.Equal(0L, count :?> int64)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_Reflow_ApplyThenStatus_ReportsPendingStages`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            do! TestHelpers.initV5 db
            do! insertTestDocument db "invoices" "apply.pdf"
            let! docId = reflowLastRowId db
            let dag = TestHelpers.standardV5Dag ()
            let applyJson =
                sprintf """{"jsonrpc":"2.0","id":31,"method":"tools/call","params":{"name":"hermes_reflow","arguments":{"document_id":%d,"operation":"reextract","mode":"apply"}}}""" docId
            let! response =
                McpServer.processMessage db m.Fs TestHelpers.silentLogger TestHelpers.defaultClock dag "/archive" None applyJson
            let applied = reflowInnerContent response
            let opId = applied.GetProperty("status").GetProperty("operation_id").GetInt64()
            let statusJson =
                sprintf """{"jsonrpc":"2.0","id":32,"method":"tools/call","params":{"name":"hermes_reflow_status","arguments":{"operation_id":%d}}}""" opId
            let! statusResponse =
                McpServer.processMessage db m.Fs TestHelpers.silentLogger TestHelpers.defaultClock dag "/archive" None statusJson
            let status = reflowInnerContent statusResponse
            Assert.Equal("running", status.GetProperty("lifecycle").GetString())
            let pending =
                status.GetProperty("stages").EnumerateArray()
                |> Seq.filter (fun stage -> stage.GetProperty("outcome").GetString() = "pending")
                |> Seq.length
            Assert.Equal(4, pending)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_Reflow_MissingDocument_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            do! TestHelpers.initV5 db
            let json =
                """{"jsonrpc":"2.0","id":33,"method":"tools/call","params":{"name":"hermes_reflow","arguments":{"document_id":999999,"operation":"reembed","mode":"apply"}}}"""
            let! response =
                McpServer.processMessage db m.Fs TestHelpers.silentLogger TestHelpers.defaultClock
                    (TestHelpers.standardV5Dag ()) "/archive" None json
            let inner = reflowInnerContent response
            Assert.Contains("not found", inner.GetProperty("error").GetString())
            Assert.True(toolCallIsError response)
            let! count = db.execScalar "SELECT count(*) FROM reflow_operations" []
            Assert.Equal(0L, count :?> int64)
        finally db.dispose ()
    }

/// Assert one malformed tools/call payload is rejected as JSON-RPC invalid params.
let private assertInvalidParams
    (db: Algebra.Database)
    (m: TestHelpers.MemFs)
    (tool: string, arguments: string, field: string)
    : Task<unit> =
    task {
        let! response = callTool db m tool arguments
        let error = jsonRpcError response
        Assert.Equal(-32602, error.GetProperty("code").GetInt32())
        Assert.Contains(field, elementText error "message")
        Assert.False(toolCallIsError response)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_Reflow_MalformedArgumentTypes_ReturnInvalidParams`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            do! TestHelpers.initV5 db

            let malformed =
                [ "hermes_reflow", """{"document_id":"1","operation":"reembed"}""", "document_id"
                  "hermes_reflow", """{"document_id":1,"operation":7}""", "operation"
                  "hermes_reflow", """{"document_id":1,"operation":"reembed","mode":true}""", "mode"
                  "hermes_reflow_status", """{"operation_id":[]}""", "operation_id"
                  "hermes_reextract", """{"document_id":{}}""", "document_id" ]

            do!
                malformed
                |> Prelude.foldTask (fun () -> assertInvalidParams db m) ()
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_ToolsCall_NonStringToolName_ReturnsInvalidParams`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            let json =
                """{"jsonrpc":"2.0","id":91,"method":"tools/call","params":{"name":7,"arguments":{}}}"""
            let! response =
                McpServer.processMessage
                    db m.Fs TestHelpers.silentLogger TestHelpers.defaultClock
                    (TestHelpers.standardV5Dag ()) "/archive" None json
            let error = jsonRpcError response
            Assert.Equal(-32602, error.GetProperty("code").GetInt32())
            Assert.Contains("name", elementText error "message")
        finally db.dispose ()
    }

/// Parse a JSON fixture into a node, failing the test if it is not JSON.
let private parseNode (json: string) : JsonNode =
    match JsonNode.Parse(json) with
    | null -> failwith $"Test fixture is not a JSON node: {json}"
    | node -> node

/// Assert one malformed tools/call container is a protocol error rather than
/// an exception escaping the handler.
let private assertContainerRejected
    (db: Algebra.Database)
    (m: TestHelpers.MemFs)
    (parameters: string, expected: string)
    : Task<unit> =
    task {
        let json =
            $"""{{"jsonrpc":"2.0","id":92,"method":"tools/call","params":{parameters}}}"""
        let! outcome =
            task {
                try
                    let! response =
                        McpServer.processMessage
                            db m.Fs TestHelpers.silentLogger
                            TestHelpers.defaultClock
                            (TestHelpers.standardV5Dag ())
                            "/archive" None json
                    return Ok response
                with error ->
                    return Error error.Message
            }
        match outcome with
        | Error message ->
            failwith $"tools/call raised for params {parameters}: {message}"
        | Ok response ->
            let error = jsonRpcError response
            Assert.Equal(-32602, error.GetProperty("code").GetInt32())
            Assert.Equal(expected, elementText error "message")
            Assert.False(toolCallIsError response)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_ToolsCall_NonObjectContainers_ReturnInvalidParams`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            do! TestHelpers.initV5 db

            let malformed =
                [ """["hermes_search"]""", "params must be an object"
                  "\"hermes_search\"", "params must be an object"
                  """{"name":"hermes_search","arguments":[]}""",
                    "arguments must be an object"
                  """{"name":"hermes_search","arguments":7}""",
                    "arguments must be an object" ]

            do!
                malformed
                |> Prelude.foldTask
                    (fun () -> assertContainerRejected db m) ()
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_ToolsCall_AbsentArguments_StillDispatches`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        try
            let! _ = db.initSchema ()
            let! _ = insertTestDocument db "invoices" "absent-args.pdf"
            let json =
                """{"jsonrpc":"2.0","id":93,"method":"tools/call","params":{"name":"hermes_stats"}}"""
            let! response =
                McpServer.processMessage
                    db m.Fs TestHelpers.silentLogger
                    TestHelpers.defaultClock
                    (TestHelpers.standardV5Dag ())
                    "/archive" None json
            let root = JsonDocument.Parse(response).RootElement
            Assert.False(root.TryGetProperty("error") |> fst)
            Assert.True(
                root.GetProperty("result").TryGetProperty("content") |> fst)
            Assert.False(toolCallIsError response)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpTools_Args_NonObjectContainer_IsAbsentNotAnException`` () =
    let assertTotal (container: JsonNode) =
        match McpTools.Args.text container "operation" with
        | Ok None -> ()
        | other -> failwith $"Expected no string value, got {other}"

        match McpTools.Args.integer container "document_id" with
        | Ok None -> ()
        | other -> failwith $"Expected no integer value, got {other}"

        match McpTools.Args.flag container "force" with
        | Ok None -> ()
        | other -> failwith $"Expected no boolean value, got {other}"

        match McpTools.Args.requiredInteger container "document_id" with
        | Error message -> Assert.Equal("document_id is required", message)
        | Ok value ->
            failwith $"Expected a required-argument error, got {value}"

    [ "[1,2,3]"; "7"; "\"text\""; "true" ]
    |> List.map parseNode
    |> List.iter assertTotal

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_ReflowDocument_MissingDocument_IsDomainFailure`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! TestHelpers.initV5 db
            let args = JsonObject()
            args["document_id"] <- JsonValue.Create(999999L)
            args["operation"] <- JsonValue.Create("reembed")
            args["mode"] <- JsonValue.Create("apply")
            let! result =
                McpTools.reflowDocument
                    db TestHelpers.silentLogger (TestHelpers.standardV5Dag ())
                    (args :> JsonNode)
            match result with
            | Error (McpTools.DomainFailure message) ->
                Assert.Contains("not found", message)
            | other -> failwith $"Expected a domain failure, got {other}"
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_ReflowStatus_UnknownOperation_IsDomainFailure`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! TestHelpers.initV5 db
            let args = JsonObject()
            args["operation_id"] <- JsonValue.Create(4242L)
            let! result =
                McpTools.reflowStatus
                    db (TestHelpers.standardV5Dag ()) (args :> JsonNode)
            match result with
            | Error (McpTools.DomainFailure message) ->
                Assert.Contains("not found", message)
            | other -> failwith $"Expected a domain failure, got {other}"
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_ReflowDocument_WrongTypedArguments_AreInvalidArguments`` () =
    task {
        let db = TestHelpers.createDb ()
        let dag = TestHelpers.standardV5Dag ()
        try
            let wrongId = JsonObject()
            wrongId["document_id"] <- JsonValue.Create("1")
            wrongId["operation"] <- JsonValue.Create("reembed")
            let! byId =
                McpTools.reflowDocument
                    db TestHelpers.silentLogger dag (wrongId :> JsonNode)

            let wrongMode = JsonObject()
            wrongMode["document_id"] <- JsonValue.Create(1L)
            wrongMode["operation"] <- JsonValue.Create("reembed")
            wrongMode["mode"] <- JsonValue.Create(1L)
            let! byMode =
                McpTools.reflowDocument
                    db TestHelpers.silentLogger dag (wrongMode :> JsonNode)

            match byId, byMode with
            | Error (McpTools.InvalidArguments idMessage),
              Error (McpTools.InvalidArguments modeMessage) ->
                Assert.Equal("document_id must be an integer", idMessage)
                Assert.Equal("mode must be a string", modeMessage)
            | other -> failwith $"Expected validation failures, got {other}"
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpTools_Args_WrongTypes_ReturnErrorsInsteadOfThrowing`` () =
    let args = JsonObject()
    args["document_id"] <- JsonValue.Create("1")
    args["operation"] <- JsonValue.Create(7L)
    args["force"] <- JsonValue.Create("yes")
    let node = args :> JsonNode

    match McpTools.Args.integer node "document_id" with
    | Error message -> Assert.Equal("document_id must be an integer", message)
    | Ok value -> failwith $"Expected a validation error, got {value}"

    match McpTools.Args.text node "operation" with
    | Error message -> Assert.Equal("operation must be a string", message)
    | Ok value -> failwith $"Expected a validation error, got {value}"

    match McpTools.Args.flag node "force" with
    | Error message -> Assert.Equal("force must be a boolean", message)
    | Ok value -> failwith $"Expected a validation error, got {value}"

    match McpTools.Args.text node "absent" with
    | Ok None -> ()
    | other -> failwith $"Expected no value for an absent argument, got {other}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpTools_Args_WellTypedValues_StillParse`` () =
    let args = JsonObject()
    args["document_id"] <- JsonValue.Create(7L)
    args["operation"] <- JsonValue.Create("reembed")
    args["force"] <- JsonValue.Create(true)
    let node = args :> JsonNode

    match McpTools.Args.requiredInteger node "document_id" with
    | Ok value -> Assert.Equal(7L, value)
    | Error message -> failwith $"Expected an integer, got {message}"

    match McpTools.Args.text node "operation" with
    | Ok (Some value) -> Assert.Equal("reembed", value)
    | other -> failwith $"Expected a string, got {other}"

    match McpTools.Args.flag node "force" with
    | Ok (Some value) -> Assert.True(value)
    | other -> failwith $"Expected a boolean, got {other}"

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_Dispatch_ToolsList_ReturnsAllTools`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        let logger = TestHelpers.silentLogger

        try
            let json =
                """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":null}"""

            let! response = McpServer.processMessage db m.Fs logger TestHelpers.defaultClock (TestHelpers.standardV5Dag ()) "/archive" None json
            let doc = JsonDocument.Parse(response)
            let result = doc.RootElement.GetProperty("result")
            let tools = result.GetProperty("tools")
            Assert.Equal(20, tools.GetArrayLength())

            let toolNames =
                [ for i in 0 .. tools.GetArrayLength() - 1 ->
                      tools.[i].GetProperty("name").GetString() |> Option.ofObj |> Option.defaultValue "" ]

            Assert.Contains("hermes_search", toolNames :> seq<string>)
            Assert.Contains("hermes_get_document", toolNames :> seq<string>)
            Assert.Contains("hermes_list_categories", toolNames :> seq<string>)
            Assert.Contains("hermes_stats", toolNames :> seq<string>)
            Assert.Contains("hermes_read_file", toolNames :> seq<string>)
            Assert.Contains("hermes_list_documents", toolNames :> seq<string>)
            Assert.Contains("hermes_get_feed_stats", toolNames :> seq<string>)
            Assert.Contains("hermes_get_document_content", toolNames :> seq<string>)
            Assert.Contains("hermes_reflow", toolNames :> seq<string>)
            Assert.Contains("hermes_reflow_status", toolNames :> seq<string>)
        finally
            db.dispose ()
    }

/// Tool names in the exact order tools/list must report them.
let private expectedToolNames: string list =
    McpServer.toolDefinitions |> List.map (fun toolDef -> toolDef.Name)

/// A tools/list response must be error-free and carry every tool, in order,
/// each with its own input schema.
let private assertFullToolList (response: string) : unit =
    let root = JsonDocument.Parse(response).RootElement
    Assert.False(root.TryGetProperty("error") |> fst)

    let entries =
        let tools = root.GetProperty("result").GetProperty("tools")
        [ for index in 0 .. tools.GetArrayLength() - 1 -> tools.[index] ]

    let names =
        entries |> List.map (fun entry -> elementText entry "name")

    Assert.Equal<string list>(expectedToolNames, names)

    Assert.True(
        entries
        |> List.forall (fun entry ->
            elementText (entry.GetProperty("inputSchema")) "type" = "object"))

/// Schema nodes are shared module-level state, so a second tools/list must
/// still render the full list rather than fail on an already-parented node.
[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_Dispatch_ToolsList_TwiceInOneProcess_ReturnsFullListBothTimes`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        let logger = TestHelpers.silentLogger

        let listTools () =
            McpServer.processMessage
                db m.Fs logger TestHelpers.defaultClock
                (TestHelpers.standardV5Dag ()) "/archive" None
                """{"jsonrpc":"2.0","id":21,"method":"tools/list","params":null}"""

        try
            let! first = listTools ()
            let! second = listTools ()

            assertFullToolList first
            assertFullToolList second
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_Dispatch_UnknownMethod_ReturnsError`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        let logger = TestHelpers.silentLogger

        try
            let json =
                """{"jsonrpc":"2.0","id":3,"method":"unknown/method","params":null}"""

            let! response = McpServer.processMessage db m.Fs logger TestHelpers.defaultClock (TestHelpers.standardV5Dag ()) "/archive" None json
            let doc = JsonDocument.Parse(response)
            let root = doc.RootElement
            Assert.True(root.TryGetProperty("error") |> fst)
            let error = root.GetProperty("error")
            Assert.Equal(-32601, error.GetProperty("code").GetInt32())
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_Dispatch_ToolsCallUnknownTool_ReturnsError`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        let logger = TestHelpers.silentLogger

        try
            let json =
                """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"nonexistent_tool","arguments":{}}}"""

            let! response = McpServer.processMessage db m.Fs logger TestHelpers.defaultClock (TestHelpers.standardV5Dag ()) "/archive" None json
            let doc = JsonDocument.Parse(response)
            let root = doc.RootElement
            Assert.True(root.TryGetProperty("error") |> fst)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_Dispatch_ToolsCallSearch_ReturnsContent`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        let logger = TestHelpers.silentLogger

        try
            let! _ = db.initSchema ()

            let! _ =
                insertTestDocument db "invoices" "invoice-2024.pdf"

            let json =
                """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"hermes_search","arguments":{"query":"invoice"}}}"""

            let! response = McpServer.processMessage db m.Fs logger TestHelpers.defaultClock (TestHelpers.standardV5Dag ()) "/archive" None json
            let doc = JsonDocument.Parse(response)
            let root = doc.RootElement

            // Should have a result with content array
            Assert.True(root.TryGetProperty("result") |> fst)
            let result = root.GetProperty("result")
            Assert.True(result.TryGetProperty("content") |> fst)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_Dispatch_ToolsCallStats_ReturnsStats`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        let logger = TestHelpers.silentLogger

        try
            let! _ = db.initSchema ()
            let! _ = insertTestDocument db "invoices" "test.pdf"

            let json =
                """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"hermes_stats","arguments":{}}}"""

            let! response = McpServer.processMessage db m.Fs logger TestHelpers.defaultClock (TestHelpers.standardV5Dag ()) "/archive" None json
            let doc = JsonDocument.Parse(response)
            let result = doc.RootElement.GetProperty("result")
            let content = result.GetProperty("content")
            Assert.True(content.GetArrayLength() > 0)

            let textContent = content.[0].GetProperty("text").GetString() |> Option.ofObj |> Option.defaultValue ""
            let stats = JsonDocument.Parse(textContent)
            Assert.True(stats.RootElement.GetProperty("totalDocuments").GetInt64() >= 1L)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_Dispatch_ToolsCallListCategories_ReturnsCategories`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        let logger = TestHelpers.silentLogger

        try
            let! _ = db.initSchema ()
            let! _ = insertTestDocument db "invoices" "inv1.pdf"
            let! _ = insertTestDocument db "invoices" "inv2.pdf"
            let! _ = insertTestDocument db "receipts" "receipt1.pdf"

            let json =
                """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"hermes_list_categories","arguments":{}}}"""

            let! response = McpServer.processMessage db m.Fs logger TestHelpers.defaultClock (TestHelpers.standardV5Dag ()) "/archive" None json
            let doc = JsonDocument.Parse(response)
            let result = doc.RootElement.GetProperty("result")
            let content = result.GetProperty("content")
            let textContent = content.[0].GetProperty("text").GetString() |> Option.ofObj |> Option.defaultValue ""
            let categories = JsonDocument.Parse(textContent)
            Assert.True(categories.RootElement.GetProperty("categories").GetArrayLength() >= 2)
        finally
            db.dispose ()
    }

// ─── Path sandboxing ─────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpTools_IsPathSafe_RelativePath_ReturnsOk`` () =
    let result = McpTools.isPathSafe "/archive" "invoices/test.pdf"
    Assert.True(Result.isOk result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpTools_IsPathSafe_PathTraversal_ReturnsError`` () =
    let result = McpTools.isPathSafe "/archive" "../etc/passwd"
    Assert.True(Result.isError result)

    match result with
    | Error msg -> Assert.Contains("traversal", msg.ToLower())
    | _ -> ()

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpTools_IsPathSafe_DotDotInMiddle_ReturnsError`` () =
    let result = McpTools.isPathSafe "/archive" "invoices/../../etc/passwd"
    Assert.True(Result.isError result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpTools_IsPathSafe_AbsolutePath_ReturnsError`` () =
    let result = McpTools.isPathSafe "/archive" "/etc/passwd"
    Assert.True(Result.isError result)

    match result with
    | Error msg -> Assert.Contains("Absolute", msg)
    | _ -> ()

[<Fact(Skip = "Windows-only: path resolution differs on macOS/Linux")>]
[<Trait("Category", "Unit")>]
let ``McpTools_IsPathSafe_WindowsAbsolutePath_ReturnsError`` () =
    let result = McpTools.isPathSafe "C:\\archive" "C:\\Windows\\System32\\config"
    Assert.True(Result.isError result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpTools_IsPathSafe_EmptyPath_ReturnsError`` () =
    let result = McpTools.isPathSafe "/archive" ""
    Assert.True(Result.isError result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpTools_IsPathSafe_WhitespacePath_ReturnsError`` () =
    let result = McpTools.isPathSafe "/archive" "   "
    Assert.True(Result.isError result)

// ─── Tool result formatting ──────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_Search_EmptyQuery_ReturnsError`` () =
    task {
        let db = TestHelpers.createRawDb ()

        try
            let! _ = db.initSchema ()
            let args = JsonObject()
            args["query"] <- JsonValue.Create("")
            let! result = McpTools.search db (args :> JsonNode)
            let doc = JsonDocument.Parse(result.ToJsonString())
            Assert.True(doc.RootElement.TryGetProperty("error") |> fst)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_GetDocument_MissingIdAndPath_ReturnsNotFound`` () =
    task {
        let db = TestHelpers.createRawDb ()

        try
            let! _ = db.initSchema ()
            let args = JsonObject()
            let! result = McpTools.getDocument db (args :> JsonNode)
            let doc = JsonDocument.Parse(result.ToJsonString())
            Assert.True(doc.RootElement.TryGetProperty("error") |> fst)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_GetDocument_ValidId_ReturnsDocument`` () =
    task {
        let db = TestHelpers.createRawDb ()

        try
            let! _ = db.initSchema ()
            let! _ = insertTestDocument db "invoices" "inv-test.pdf"

            let args = JsonObject()
            args["id"] <- JsonValue.Create(1L)
            let! result = McpTools.getDocument db (args :> JsonNode)
            let doc = JsonDocument.Parse(result.ToJsonString())
            Assert.Equal("invoices", doc.RootElement.GetProperty("category").GetString())
            Assert.Equal("inv-test.pdf", doc.RootElement.GetProperty("originalName").GetString())
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpTools_ReadFile_PathTraversal_ReturnsError`` () =
    task {
        let m = TestHelpers.memFs ()
        let args = JsonObject()
        args["path"] <- JsonValue.Create("../secret.txt")
        let! result = McpTools.readFile m.Fs "/archive" (args :> JsonNode)
        let doc = JsonDocument.Parse(result.ToJsonString())
        Assert.True(doc.RootElement.TryGetProperty("error") |> fst)
        Assert.Contains("traversal", (doc.RootElement.GetProperty("error").GetString() |> Option.ofObj |> Option.defaultValue "").ToLower())
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpTools_ReadFile_MissingPath_ReturnsError`` () =
    task {
        let m = TestHelpers.memFs ()
        let args = JsonObject()
        let! result = McpTools.readFile m.Fs "/archive" (args :> JsonNode)
        let doc = JsonDocument.Parse(result.ToJsonString())
        Assert.True(doc.RootElement.TryGetProperty("error") |> fst)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_ProcessMessage_CompleteRoundTrip_ValidJsonRpc`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        let logger = TestHelpers.silentLogger

        try
            let! _ = db.initSchema ()

            let json =
                """{"jsonrpc":"2.0","id":42,"method":"initialize","params":{}}"""

            let! response = McpServer.processMessage db m.Fs logger TestHelpers.defaultClock (TestHelpers.standardV5Dag ()) "/archive" None json
            let doc = JsonDocument.Parse(response)
            let root = doc.RootElement

            // Verify JSON-RPC 2.0 structure
            Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString())
            Assert.Equal(42, root.GetProperty("id").GetInt32())
            Assert.True(root.TryGetProperty("result") |> fst)
        finally
            db.dispose ()
    }

// ─── MCP Reminder tools ──────────────────────────────────────────────

let private insertReminder (db: Algebra.Database) (cat: string) (amount: float) (dueDate: string) =
    task {
        let! _ =
            db.execNonQuery
                """INSERT INTO reminders (category, amount, due_date, status, created_at)
                   VALUES (@cat, @amt, @due, 'active', datetime('now'))"""
                ([ ("@cat", Database.boxVal cat)
                   ("@amt", Database.boxVal amount)
                   ("@due", Database.boxVal dueDate) ])
        let! id = db.execScalar "SELECT last_insert_rowid()" []
        return match id with null -> 0L | v -> v :?> int64
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``MCP_ListReminders_ReturnsActiveReminders`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        try
            let! _ = db.initSchema ()
            let! _ = insertReminder db "invoices" 500.0 "2026-04-10"
            let json = """{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"hermes_list_reminders","arguments":{}}}"""
            let! response = McpServer.processMessage db m.Fs TestHelpers.silentLogger TestHelpers.defaultClock (TestHelpers.standardV5Dag ()) "/archive" None json
            let doc = JsonDocument.Parse(response)
            let root = doc.RootElement
            if root.TryGetProperty("error") |> fst then
                let err = root.GetProperty("error").GetProperty("message").GetString()
                failwith $"Expected result, got error: {err}"
            let result = root.GetProperty("result")
            let content = result.GetProperty("content")
            let textContent = content.[0].GetProperty("text").GetString() |> Option.ofObj |> Option.defaultValue ""
            let inner = JsonDocument.Parse(textContent).RootElement
            let reminders = inner.GetProperty("reminders")
            Assert.True(reminders.GetArrayLength() > 0)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``MCP_UpdateReminder_MarkComplete_ChangesStatus`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        try
            let! _ = db.initSchema ()
            let! rid = insertReminder db "invoices" 100.0 "2026-04-05"
            let req = JsonObject()
            req["jsonrpc"] <- JsonValue.Create("2.0")
            req["id"] <- JsonValue.Create(11)
            req["method"] <- JsonValue.Create("tools/call")
            let ps = JsonObject()
            ps["name"] <- JsonValue.Create("hermes_update_reminder")
            let args = JsonObject()
            args["reminder_id"] <- JsonValue.Create(rid)
            args["action"] <- JsonValue.Create("complete")
            ps["arguments"] <- args
            req["params"] <- ps
            let! response = McpServer.processMessage db m.Fs TestHelpers.silentLogger TestHelpers.defaultClock (TestHelpers.standardV5Dag ()) "/archive" None (req.ToJsonString())
            let doc = JsonDocument.Parse(response)
            let root = doc.RootElement
            if root.TryGetProperty("error") |> fst then
                let err = root.GetProperty("error").GetProperty("message").GetString()
                failwith $"Expected result, got error: {err}"
            let result = root.GetProperty("result")
            let content = result.GetProperty("content")
            let textContent = content.[0].GetProperty("text").GetString() |> Option.ofObj |> Option.defaultValue ""
            let inner = JsonDocument.Parse(textContent).RootElement
            Assert.Equal("completed", inner.GetProperty("status").GetString())
        finally db.dispose ()
    }

// ─── hermes_get_document_content MCP integration (P8) ────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_GetDocumentContent_Markdown_ReturnsStructuredContent`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            let mdContent = "---\ntitle: Invoice\n---\n\n## Summary\n\n| Item | Amount |\n| --- | --- |\n| Service | $500 |"
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (source_type, saved_path, category, sha256, original_name)
                       VALUES ('manual_drop', 'invoices/test.pdf', 'invoices', 'abc', 'test.pdf')"""
                    []
            m.Put "/archive/invoices/test.pdf.extracted.md" mdContent
            let! idObj = db.execScalar "SELECT MAX(id) FROM documents" []
            let docId = match idObj with :? int64 as i -> i | _ -> 1L
            do! markExtractCurrent db docId
            let req = JsonObject()
            req["jsonrpc"] <- JsonValue.Create("2.0")
            req["id"] <- JsonValue.Create(1)
            req["method"] <- JsonValue.Create("tools/call")
            let ps = JsonObject()
            ps["name"] <- JsonValue.Create("hermes_get_document_content")
            let args = JsonObject()
            args["document_id"] <- JsonValue.Create(docId)
            args["format"] <- JsonValue.Create("markdown")
            ps["arguments"] <- args
            req["params"] <- ps
            let! response = McpServer.processMessage db m.Fs TestHelpers.silentLogger TestHelpers.defaultClock (TestHelpers.standardV5Dag ()) "/archive" None (req.ToJsonString())
            let doc = JsonDocument.Parse(response)
            let root = doc.RootElement
            if root.TryGetProperty("error") |> fst then
                let err = root.GetProperty("error").GetProperty("message").GetString()
                failwith $"Expected result, got error: {err}"
            let result = root.GetProperty("result")
            let content = result.GetProperty("content")
            Assert.True(content.GetArrayLength() > 0)
            let textContent = content.[0].GetProperty("text").GetString()
            Assert.Contains("## Summary", textContent)
            Assert.Contains("| Item | Amount |", textContent)
        finally db.dispose ()
    }

// ─── McpTools direct function tests ──────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_ListDocumentsFeed_ReturnsDocuments`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! insertTestDocument db "invoices" "test.pdf"
            let args = JsonObject() :> JsonNode
            let! result = McpTools.listDocumentsFeed db args
            let arr = result :?> JsonArray
            Assert.True(arr.Count > 0)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_ListDocumentsFeed_EmptyDb_ReturnsEmptyArray`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let args = JsonObject() :> JsonNode
            let! result = McpTools.listDocumentsFeed db args
            let arr = result :?> JsonArray
            Assert.Equal(0, arr.Count)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_GetFeedStats_ReturnsStats`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! insertTestDocument db "invoices" "a.pdf"
            do! insertTestDocument db "tax" "b.pdf"
            let args = JsonObject() :> JsonNode
            let! result = McpTools.getFeedStats db args
            let obj = result :?> JsonObject
            Assert.Equal(2, obj["total_documents"].GetValue<int>())
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_GetProcessingQueue_ReturnsQueueInfo`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('manual_drop', 'a.pdf', 'unsorted', 'sha1')" []
            let args = JsonObject() :> JsonNode
            let! result = McpTools.getProcessingQueue db args
            let obj = result :?> JsonObject
            Assert.True(obj.ContainsKey("unclassified"))
            Assert.True(obj.ContainsKey("unextracted"))
            Assert.True(obj.ContainsKey("unembedded"))
            let unclassified = obj["unclassified"] :?> JsonObject
            Assert.True(unclassified["count"].GetValue<int>() >= 1)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_ReextractDocument_ValidId_ReturnsSuccess`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! TestHelpers.initV5 db
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_at) VALUES ('manual_drop', 'a.pdf', 'invoices', 'sha1', datetime('now'))" []
            let args = JsonObject()
            args["document_id"] <- JsonValue.Create(1L)
            let! result = McpTools.reextractDocument db TestHelpers.silentLogger (TestHelpers.standardV5Dag ()) (args :> JsonNode)
            match result with
            | Ok payload ->
                let obj = payload :?> JsonObject
                Assert.Equal("queued_for_reextraction", obj["status"].GetValue<string>())
            | Error failure -> failwith $"Expected success, got {failure}"
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_ReextractDocument_MissingId_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let args = JsonObject() :> JsonNode
            let! result = McpTools.reextractDocument db TestHelpers.silentLogger (TestHelpers.standardV5Dag ()) args
            match result with
            | Error (McpTools.InvalidArguments message) ->
                Assert.Contains("document_id is required", message)
            | other -> failwith $"Expected a validation failure, got {other}"
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_ReclassifyDocument_MissingId_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let args = JsonObject()
            args["new_category"] <- JsonValue.Create("tax")
            let! result = McpTools.reclassifyDocument db (TestHelpers.memFs().Fs) "/archive" (args :> JsonNode)
            let obj = result :?> JsonObject
            Assert.True(obj.ContainsKey("error"))
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_ReclassifyDocument_MissingCategory_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let args = JsonObject()
            args["document_id"] <- JsonValue.Create(1L)
            let! result = McpTools.reclassifyDocument db (TestHelpers.memFs().Fs) "/archive" (args :> JsonNode)
            let obj = result :?> JsonObject
            Assert.True(obj.ContainsKey("error"))
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_GetDocumentContent_MissingId_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let args = JsonObject() :> JsonNode
            let! result = McpTools.getDocumentContent db (TestHelpers.memFs().Fs) "/archive" args
            let obj = result :?> JsonObject
            Assert.True(obj.ContainsKey("error"))
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_GetDocumentContent_ValidId_ReturnsContent`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_at) VALUES ('manual_drop', 'inv/a.pdf', 'invoices', 'sha1', datetime('now'))" []
            m.Put "/archive/inv/a.pdf.extracted.md" "Hello world"
            do! markExtractCurrent db 1L
            let args = JsonObject()
            args["document_id"] <- JsonValue.Create(1L)
            args["format"] <- JsonValue.Create("markdown")
            let! result = McpTools.getDocumentContent db m.Fs "/archive" (args :> JsonNode)
            let obj = result :?> JsonObject
            Assert.Equal("Hello world", obj["content"].GetValue<string>())
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpTools_ReadFile_ValidPath_ReturnsContent`` () =
    task {
        let m = TestHelpers.memFs ()
        let archiveDir = IO.Path.GetFullPath("testarchive_read")
        let filePath = IO.Path.Combine(archiveDir, "invoices", "test.txt")
        m.Put (m.Norm filePath) "file contents here"
        let args = JsonObject()
        args["path"] <- JsonValue.Create("invoices/test.txt")
        let! result = McpTools.readFile m.Fs archiveDir (args :> JsonNode)
        let doc = JsonDocument.Parse(result.ToJsonString())
        let root = doc.RootElement
        match root.TryGetProperty("content") with
        | true, contentProp -> Assert.Contains("file contents here", contentProp.GetString())
        | _ ->
            let errMsg = match root.TryGetProperty("error") with true, e -> e.GetString() | _ -> "unknown"
            failwith $"Expected content, got error: {errMsg}"
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_ListDocumentsFeed_WithCategory_FiltersCorrectly`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! insertTestDocument db "invoices" "a.pdf"
            do! insertTestDocument db "tax" "b.pdf"
            let args = JsonObject()
            args["category"] <- JsonValue.Create("invoices")
            let! result = McpTools.listDocumentsFeed db (args :> JsonNode)
            let arr = result :?> JsonArray
            Assert.Equal(1, arr.Count)
        finally db.dispose ()
    }

// ─── McpTools.updateReminder additional branches ─────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_UpdateReminder_Snooze_ChangesStatus`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! rid = insertReminder db "invoices" 100.0 "2026-04-05"
            let args = JsonObject()
            args["reminder_id"] <- JsonValue.Create(rid)
            args["action"] <- JsonValue.Create("snooze")
            args["snooze_days"] <- JsonValue.Create(5)
            let! result = McpTools.updateReminder db TestHelpers.defaultClock (args :> JsonNode)
            let doc = System.Text.Json.JsonDocument.Parse(result.ToJsonString())
            Assert.Equal("snoozed", doc.RootElement.GetProperty("status").GetString())
            Assert.Equal(5, doc.RootElement.GetProperty("snoozedDays").GetInt32())
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_UpdateReminder_Dismiss_ChangesStatus`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! rid = insertReminder db "invoices" 100.0 "2026-04-05"
            let args = JsonObject()
            args["reminder_id"] <- JsonValue.Create(rid)
            args["action"] <- JsonValue.Create("dismiss")
            let! result = McpTools.updateReminder db TestHelpers.defaultClock (args :> JsonNode)
            let doc = System.Text.Json.JsonDocument.Parse(result.ToJsonString())
            Assert.Equal("dismissed", doc.RootElement.GetProperty("status").GetString())
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_UpdateReminder_UnknownAction_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! rid = insertReminder db "invoices" 100.0 "2026-04-05"
            let args = JsonObject()
            args["reminder_id"] <- JsonValue.Create(rid)
            args["action"] <- JsonValue.Create("delete")
            let! result = McpTools.updateReminder db TestHelpers.defaultClock (args :> JsonNode)
            let doc = System.Text.Json.JsonDocument.Parse(result.ToJsonString())
            Assert.True(doc.RootElement.TryGetProperty("error") |> fst)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_UpdateReminder_MissingFields_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let args = JsonObject()
            let! result = McpTools.updateReminder db TestHelpers.defaultClock (args :> JsonNode)
            let doc = System.Text.Json.JsonDocument.Parse(result.ToJsonString())
            Assert.True(doc.RootElement.TryGetProperty("error") |> fst)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_UpdateReminder_Paid_IsAlias_ForComplete`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! rid = insertReminder db "invoices" 100.0 "2026-04-05"
            let args = JsonObject()
            args["reminder_id"] <- JsonValue.Create(rid)
            args["action"] <- JsonValue.Create("paid")
            let! result = McpTools.updateReminder db TestHelpers.defaultClock (args :> JsonNode)
            let doc = System.Text.Json.JsonDocument.Parse(result.ToJsonString())
            Assert.Equal("completed", doc.RootElement.GetProperty("status").GetString())
        finally db.dispose ()
    }

// ─── McpTools.reclassifyDocument ─────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_ReclassifyDocument_ValidDoc_Reclassifies`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Fs.createDirectory "/archive/unsorted"
        m.Fs.createDirectory "/archive/invoices"
        let! _ = db.execNonQuery
                    "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('manual_drop', 'unsorted/test.pdf', 'unsorted', 'sha1')"
                    []
        m.Put "/archive/unsorted/test.pdf" "content"
        try
            let args = JsonObject()
            args["document_id"] <- JsonValue.Create(1L)
            args["new_category"] <- JsonValue.Create("invoices")
            let! result = McpTools.reclassifyDocument db m.Fs "/archive" (args :> JsonNode)
            let doc = System.Text.Json.JsonDocument.Parse(result.ToJsonString())
            Assert.Equal("reclassified", doc.RootElement.GetProperty("status").GetString())
        finally db.dispose ()
    }

// ─── McpTools.readFile ───────────────────────────────────────────────

// ─── McpTools.readFile (covered by existing path safety tests) ──────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpTools_ReadFile_MissingPathArg_ReturnsError`` () =
    task {
        let m = TestHelpers.memFs ()
        let args = JsonObject()
        let! result = McpTools.readFile m.Fs "/archive" (args :> JsonNode)
        let json = result.ToJsonString()
        Assert.Contains("error", json)
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpTools_ReadFile_NonexistentFile_ReturnsError`` () =
    task {
        let m = TestHelpers.memFs ()
        let args = JsonObject()
        args["path"] <- JsonValue.Create("nonexistent/file.txt")
        let! result = McpTools.readFile m.Fs "/archive" (args :> JsonNode)
        let json = result.ToJsonString()
        Assert.Contains("error", json)
    }

// ─── McpTools.getProcessingQueue extra ───────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_GetProcessingQueue_WithDocs_ReturnsJsonObject`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! insertTestDocument db "invoices" "test.pdf"
            let args = JsonObject()
            let! result = McpTools.getProcessingQueue db (args :> JsonNode)
            let json = result.ToJsonString()
            // Should return some queue info as JSON
            Assert.True(json.Length > 2, "Expected non-empty JSON response")
        finally db.dispose ()
    }

// ─── handleToolCall dispatch branch coverage ────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_Dispatch_ToolsCallGetDocument_ReturnsResult`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        let logger = TestHelpers.silentLogger
        try
            let! _ = db.initSchema ()
            let! _ = insertTestDocument db "invoices" "test-doc.pdf"
            let json =
                """{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"hermes_get_document","arguments":{"id":1}}}"""
            let! response = McpServer.processMessage db m.Fs logger TestHelpers.defaultClock (TestHelpers.standardV5Dag ()) "/archive" None json
            let doc = JsonDocument.Parse(response)
            Assert.True(doc.RootElement.TryGetProperty("result") |> fst)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_Dispatch_ToolsCallListDocuments_ReturnsResult`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        let logger = TestHelpers.silentLogger
        try
            let! _ = db.initSchema ()
            let! _ = insertTestDocument db "invoices" "list-test.pdf"
            let json =
                """{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"hermes_list_documents","arguments":{"limit":10}}}"""
            let! response = McpServer.processMessage db m.Fs logger TestHelpers.defaultClock (TestHelpers.standardV5Dag ()) "/archive" None json
            let doc = JsonDocument.Parse(response)
            Assert.True(doc.RootElement.TryGetProperty("result") |> fst)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_Dispatch_ToolsCallGetProcessingQueue_ReturnsResult`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        let logger = TestHelpers.silentLogger
        try
            let! _ = db.initSchema ()
            let json =
                """{"jsonrpc":"2.0","id":12,"method":"tools/call","params":{"name":"hermes_get_processing_queue","arguments":{}}}"""
            let! response = McpServer.processMessage db m.Fs logger TestHelpers.defaultClock (TestHelpers.standardV5Dag ()) "/archive" None json
            let doc = JsonDocument.Parse(response)
            Assert.True(doc.RootElement.TryGetProperty("result") |> fst)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_Dispatch_ToolsCallReadFile_ReturnsResult`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        m.Put "/archive/invoices/test.pdf" "file content here"
        let logger = TestHelpers.silentLogger
        try
            let! _ = db.initSchema ()
            let json =
                """{"jsonrpc":"2.0","id":13,"method":"tools/call","params":{"name":"hermes_read_file","arguments":{"path":"invoices/test.pdf"}}}"""
            let! response = McpServer.processMessage db m.Fs logger TestHelpers.defaultClock (TestHelpers.standardV5Dag ()) "/archive" None json
            let doc = JsonDocument.Parse(response)
            Assert.True(doc.RootElement.TryGetProperty("result") |> fst)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_Dispatch_ToolsCallListReminders_ReturnsResult`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        let logger = TestHelpers.silentLogger
        try
            let! _ = db.initSchema ()
            let json =
                """{"jsonrpc":"2.0","id":14,"method":"tools/call","params":{"name":"hermes_list_reminders","arguments":{}}}"""
            let! response = McpServer.processMessage db m.Fs logger TestHelpers.defaultClock (TestHelpers.standardV5Dag ()) "/archive" None json
            let doc = JsonDocument.Parse(response)
            Assert.True(doc.RootElement.TryGetProperty("result") |> fst)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_Dispatch_ToolsCallGetFeedStats_ReturnsResult`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        let logger = TestHelpers.silentLogger
        try
            let! _ = db.initSchema ()
            let json =
                """{"jsonrpc":"2.0","id":15,"method":"tools/call","params":{"name":"hermes_get_feed_stats","arguments":{}}}"""
            let! response = McpServer.processMessage db m.Fs logger TestHelpers.defaultClock (TestHelpers.standardV5Dag ()) "/archive" None json
            let doc = JsonDocument.Parse(response)
            Assert.True(doc.RootElement.TryGetProperty("result") |> fst)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_Dispatch_ToolsCallGetDocumentContent_ReturnsResult`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        m.Put "/archive/invoices/content-test.pdf.extracted.md" "pdf content"
        let logger = TestHelpers.silentLogger
        try
            let! _ = db.initSchema ()
            let! _ = insertTestDocument db "invoices" "content-test.pdf"
            do! markExtractCurrent db 1L
            let json =
                """{"jsonrpc":"2.0","id":16,"method":"tools/call","params":{"name":"hermes_get_document_content","arguments":{"id":1}}}"""
            let! response = McpServer.processMessage db m.Fs logger TestHelpers.defaultClock (TestHelpers.standardV5Dag ()) "/archive" None json
            let doc = JsonDocument.Parse(response)
            Assert.True(doc.RootElement.TryGetProperty("result") |> fst)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_Dispatch_ToolsCallReclassify_ReturnsResult`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        m.Fs.createDirectory "/archive/unsorted"
        m.Fs.createDirectory "/archive/invoices"
        m.Put "/archive/unsorted/recl-test.pdf" "content"
        let logger = TestHelpers.silentLogger
        try
            let! _ = db.initSchema ()
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (source_type, saved_path, category, sha256, original_name)
                       VALUES ('manual_drop', 'unsorted/recl-test.pdf', 'unsorted', 'sha-recl', 'recl-test.pdf')"""
                    []
            let json =
                """{"jsonrpc":"2.0","id":17,"method":"tools/call","params":{"name":"hermes_reclassify","arguments":{"id":1,"category":"invoices"}}}"""
            let! response = McpServer.processMessage db m.Fs logger TestHelpers.defaultClock (TestHelpers.standardV5Dag ()) "/archive" None json
            let doc = JsonDocument.Parse(response)
            Assert.True(doc.RootElement.TryGetProperty("result") |> fst)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_Dispatch_ToolsCallReextract_MissingDocumentId_ReturnsInvalidParams`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        let logger = TestHelpers.silentLogger
        try
            let! _ = db.initSchema ()
            let! _ = insertTestDocument db "invoices" "reext-test.pdf"
            let json =
                """{"jsonrpc":"2.0","id":18,"method":"tools/call","params":{"name":"hermes_reextract","arguments":{"id":1}}}"""
            let! response = McpServer.processMessage db m.Fs logger TestHelpers.defaultClock (TestHelpers.standardV5Dag ()) "/archive" None json
            let error = jsonRpcError response
            Assert.Equal(-32602, error.GetProperty("code").GetInt32())
            Assert.Contains("document_id", elementText error "message")
        finally db.dispose ()
    }

// ─── hermes_deep_extract dispatch (deepDeps = None) ──────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_Dispatch_DeepExtract_NoDeps_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        let logger = TestHelpers.silentLogger
        try
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (original_name, saved_path, source_type, category, classification_tier, sha256)
                       VALUES (@n, @p, @s, @c, 'llm', @sha)"""
                    [ ("@n", Database.boxVal "test.pdf"); ("@p", Database.boxVal "unclassified/test.pdf")
                      ("@s", Database.boxVal "email"); ("@c", Database.boxVal "payslips")
                      ("@sha", Database.boxVal "deadbeef01234567") ]
            let json =
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"hermes_deep_extract","arguments":{"document_id":1}}}"""
            let! response = McpServer.processMessage db m.Fs logger TestHelpers.defaultClock (TestHelpers.standardV5Dag ()) "/archive" None json
            let doc = JsonDocument.Parse(response)
            let root = doc.RootElement
            Assert.True(root.TryGetProperty("error") |> fst)
        finally
            db.dispose ()
    }
