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
                """INSERT INTO documents (source_type, saved_path, category, sha256, original_name, sender, subject, extracted_text)
                   VALUES ('manual_drop', @path, @cat, @sha, @name, @sender, @subject, @text)"""
                ([ ("@path", Database.boxVal ($"{category}/{name}"))
                   ("@cat", Database.boxVal category)
                   ("@sha", Database.boxVal (Guid.NewGuid().ToString("N")))
                   ("@name", Database.boxVal name)
                   ("@sender", Database.boxVal "test@example.com")
                   ("@subject", Database.boxVal $"Test document {name}")
                   ("@text", Database.boxVal $"Content of {name}") ] : (string * obj) list)

        ()
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
[<Trait("Category", "Unit")>]
let ``McpServer_Dispatch_Initialize_ReturnsCapabilities`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        let logger = TestHelpers.silentLogger

        try
            let json =
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}"""

            let! response = McpServer.processMessage db m.Fs logger "/archive" json
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

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpServer_Dispatch_ToolsList_ReturnsAllTools`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        let logger = TestHelpers.silentLogger

        try
            let json =
                """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":null}"""

            let! response = McpServer.processMessage db m.Fs logger "/archive" json
            let doc = JsonDocument.Parse(response)
            let result = doc.RootElement.GetProperty("result")
            let tools = result.GetProperty("tools")
            Assert.Equal(13, tools.GetArrayLength())

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
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpServer_Dispatch_UnknownMethod_ReturnsError`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        let logger = TestHelpers.silentLogger

        try
            let json =
                """{"jsonrpc":"2.0","id":3,"method":"unknown/method","params":null}"""

            let! response = McpServer.processMessage db m.Fs logger "/archive" json
            let doc = JsonDocument.Parse(response)
            let root = doc.RootElement
            Assert.True(root.TryGetProperty("error") |> fst)
            let error = root.GetProperty("error")
            Assert.Equal(-32601, error.GetProperty("code").GetInt32())
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpServer_Dispatch_ToolsCallUnknownTool_ReturnsError`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        let logger = TestHelpers.silentLogger

        try
            let json =
                """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"nonexistent_tool","arguments":{}}}"""

            let! response = McpServer.processMessage db m.Fs logger "/archive" json
            let doc = JsonDocument.Parse(response)
            let root = doc.RootElement
            Assert.True(root.TryGetProperty("error") |> fst)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
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

            let! response = McpServer.processMessage db m.Fs logger "/archive" json
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
[<Trait("Category", "Unit")>]
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

            let! response = McpServer.processMessage db m.Fs logger "/archive" json
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
[<Trait("Category", "Unit")>]
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

            let! response = McpServer.processMessage db m.Fs logger "/archive" json
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

[<Fact>]
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
[<Trait("Category", "Unit")>]
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
[<Trait("Category", "Unit")>]
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
[<Trait("Category", "Unit")>]
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
[<Trait("Category", "Unit")>]
let ``McpServer_ProcessMessage_CompleteRoundTrip_ValidJsonRpc`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let m = TestHelpers.memFs ()
        let logger = TestHelpers.silentLogger

        try
            let! _ = db.initSchema ()

            let json =
                """{"jsonrpc":"2.0","id":42,"method":"initialize","params":{}}"""

            let! response = McpServer.processMessage db m.Fs logger "/archive" json
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
            let! response = McpServer.processMessage db m.Fs TestHelpers.silentLogger "/archive" json
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
            let! response = McpServer.processMessage db m.Fs TestHelpers.silentLogger "/archive" (req.ToJsonString())
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
[<Trait("Category", "Unit")>]
let ``McpServer_GetDocumentContent_Markdown_ReturnsStructuredContent`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            let mdContent = "---\ntitle: Invoice\n---\n\n## Summary\n\n| Item | Amount |\n| --- | --- |\n| Service | $500 |"
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (source_type, saved_path, category, sha256, original_name, extracted_text)
                       VALUES ('manual_drop', 'invoices/test.pdf', 'invoices', 'abc', 'test.pdf', @text)"""
                    [ ("@text", Database.boxVal mdContent) ]
            let! idObj = db.execScalar "SELECT MAX(id) FROM documents" []
            let docId = match idObj with :? int64 as i -> i | _ -> 1L
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
            let! response = McpServer.processMessage db m.Fs TestHelpers.silentLogger "/archive" (req.ToJsonString())
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
[<Trait("Category", "Unit")>]
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
[<Trait("Category", "Unit")>]
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
[<Trait("Category", "Unit")>]
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
[<Trait("Category", "Unit")>]
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
[<Trait("Category", "Unit")>]
let ``McpTools_ReextractDocument_ValidId_ReturnsSuccess`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text, extracted_at) VALUES ('manual_drop', 'a.pdf', 'invoices', 'sha1', 'text', datetime('now'))" []
            let args = JsonObject()
            args["document_id"] <- JsonValue.Create(1L)
            let! result = McpTools.reextractDocument db (args :> JsonNode)
            let obj = result :?> JsonObject
            Assert.Equal("queued_for_reextraction", obj["status"].GetValue<string>())
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpTools_ReextractDocument_MissingId_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let args = JsonObject() :> JsonNode
            let! result = McpTools.reextractDocument db args
            let obj = result :?> JsonObject
            Assert.True(obj.ContainsKey("error"))
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
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
[<Trait("Category", "Unit")>]
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
[<Trait("Category", "Unit")>]
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
[<Trait("Category", "Unit")>]
let ``McpTools_GetDocumentContent_ValidId_ReturnsContent`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256, extracted_text) VALUES ('manual_drop', 'inv/a.pdf', 'invoices', 'sha1', 'Hello world')" []
            let args = JsonObject()
            args["document_id"] <- JsonValue.Create(1L)
            args["format"] <- JsonValue.Create("markdown")
            let! result = McpTools.getDocumentContent db (TestHelpers.memFs().Fs) "/archive" (args :> JsonNode)
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
[<Trait("Category", "Unit")>]
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
[<Trait("Category", "Unit")>]
let ``McpTools_UpdateReminder_Snooze_ChangesStatus`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! rid = insertReminder db "invoices" 100.0 "2026-04-05"
            let args = JsonObject()
            args["reminder_id"] <- JsonValue.Create(rid)
            args["action"] <- JsonValue.Create("snooze")
            args["snooze_days"] <- JsonValue.Create(5)
            let! result = McpTools.updateReminder db (args :> JsonNode)
            let doc = System.Text.Json.JsonDocument.Parse(result.ToJsonString())
            Assert.Equal("snoozed", doc.RootElement.GetProperty("status").GetString())
            Assert.Equal(5, doc.RootElement.GetProperty("snoozedDays").GetInt32())
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpTools_UpdateReminder_Dismiss_ChangesStatus`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! rid = insertReminder db "invoices" 100.0 "2026-04-05"
            let args = JsonObject()
            args["reminder_id"] <- JsonValue.Create(rid)
            args["action"] <- JsonValue.Create("dismiss")
            let! result = McpTools.updateReminder db (args :> JsonNode)
            let doc = System.Text.Json.JsonDocument.Parse(result.ToJsonString())
            Assert.Equal("dismissed", doc.RootElement.GetProperty("status").GetString())
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpTools_UpdateReminder_UnknownAction_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! rid = insertReminder db "invoices" 100.0 "2026-04-05"
            let args = JsonObject()
            args["reminder_id"] <- JsonValue.Create(rid)
            args["action"] <- JsonValue.Create("delete")
            let! result = McpTools.updateReminder db (args :> JsonNode)
            let doc = System.Text.Json.JsonDocument.Parse(result.ToJsonString())
            Assert.True(doc.RootElement.TryGetProperty("error") |> fst)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpTools_UpdateReminder_MissingFields_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let args = JsonObject()
            let! result = McpTools.updateReminder db (args :> JsonNode)
            let doc = System.Text.Json.JsonDocument.Parse(result.ToJsonString())
            Assert.True(doc.RootElement.TryGetProperty("error") |> fst)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``McpTools_UpdateReminder_Paid_IsAlias_ForComplete`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! rid = insertReminder db "invoices" 100.0 "2026-04-05"
            let args = JsonObject()
            args["reminder_id"] <- JsonValue.Create(rid)
            args["action"] <- JsonValue.Create("paid")
            let! result = McpTools.updateReminder db (args :> JsonNode)
            let doc = System.Text.Json.JsonDocument.Parse(result.ToJsonString())
            Assert.Equal("completed", doc.RootElement.GetProperty("status").GetString())
        finally db.dispose ()
    }

// ─── McpTools.reclassifyDocument ─────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
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
[<Trait("Category", "Unit")>]
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
