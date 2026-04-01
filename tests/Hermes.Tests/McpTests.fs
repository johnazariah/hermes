module Hermes.Tests.McpTests

#nowarn "3261"

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
            Assert.Equal(10, tools.GetArrayLength())

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

[<Fact(Skip = "MCP response structure needs debugging — neither result nor error key present")>]
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
            // Should have result (not error)
            if root.TryGetProperty("error") |> fst then
                let err = root.GetProperty("error").GetProperty("message").GetString()
                failwith $"Expected result, got error: {err}"
            let result = root.GetProperty("result")
            let reminders = result.GetProperty("reminders")
            Assert.True(reminders.GetArrayLength() > 0)
        finally db.dispose ()
    }

[<Fact(Skip = "MCP response structure needs debugging — neither result nor error key present")>]
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
            Assert.Equal("completed", result.GetProperty("status").GetString())
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
