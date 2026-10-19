module Hermes.Tests.ReclassificationCompatibilityTests

open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open Hermes.Core

type private Fixture =
    { Db: Algebra.Database
      Files: TestHelpers.MemFs
      DocumentId: int64 }

let private withDocument (verify: Fixture -> Task<unit>) =
    task {
        let db = TestHelpers.createDb ()
        let files = TestHelpers.memFs ()
        files.Put "/archive/stable/source.pdf" "immutable source"
        try
            let! id =
                db.execScalar
                    "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('manual_drop', 'stable/source.pdf', 'unsorted', 'stored-sha') RETURNING id"
                    []
            do! verify { Db = db; Files = files; DocumentId = unbox<int64> id }
        finally
            db.dispose ()
    }

let private callTool (fixture: Fixture) message =
    McpServer.processMessage
        fixture.Db fixture.Files.Fs TestHelpers.silentLogger
        TestHelpers.defaultClock (TestHelpers.standardV5Dag ()) "/archive" None message

let private nodeString (node: JsonNode) (name: string) : string =
    node[name]
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failwith $"Missing JSON field: {name}")
    |> fun value -> value.GetValue<string>()

let private elementString (element: JsonElement) (name: string) : string =
    element.GetProperty(name).GetString()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failwith $"Null JSON field: {name}")

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Main single signature remains usable and preserves not-found error`` () =
    withDocument (fun fixture ->
        task {
            let operation: Task<Result<unit, string>> =
                DocumentManagement.reclassify
                    fixture.Db fixture.Files.Fs "/archive" fixture.DocumentId "tax"
            let! result = operation
            let! missing =
                DocumentManagement.reclassify fixture.Db fixture.Files.Fs "/archive" 999L "tax"
            Assert.Equal<Result<unit, string>>(Ok (), result)
            Assert.Equal<Result<unit, string>>(Error "Document 999 not found", missing)
            Assert.Equal(Some "immutable source", fixture.Files.Get "/archive/stable/source.pdf")
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``MCP retains new_category alongside detailed identity fields`` () =
    withDocument (fun fixture ->
        task {
            let args = JsonObject()
            args["document_id"] <- JsonValue.Create(fixture.DocumentId)
            args["new_category"] <- JsonValue.Create("tax")
            let! result =
                McpTools.reclassifyDocument fixture.Db fixture.Files.Fs "/archive" args
            Assert.Equal("reclassified", nodeString result "status")
            Assert.Equal("tax", nodeString result "new_category")
            Assert.Equal("stable/source.pdf", nodeString result "saved_path")
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Removed repair tool returns a JSON-RPC error`` () =
    withDocument (fun fixture ->
        task {
            let! response =
                callTool fixture
                    """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"hermes_legacy_reclassify_page","arguments":{}}}"""
            use document = JsonDocument.Parse(response)
            let error = document.RootElement.GetProperty("error")
            Assert.Equal(-32602, error.GetProperty("code").GetInt32())
            Assert.Contains("Unknown tool", error.GetProperty("message").GetString())
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Legacy move batch retains action and successful count with explicit failures`` () =
    withDocument (fun fixture ->
        task {
            let! response =
                ReclassificationApi.legacyBatch
                    fixture.Db fixture.Files.Fs "/archive" [ fixture.DocumentId; 999L ] "tax"
            Assert.Equal("move", response.action)
            Assert.Equal(1, response.count)
            Assert.Equal(1, response.succeeded)
            Assert.Equal(1, response.failed)
            Assert.Equal(2, response.outcomes.Length)
            Assert.All(response.outcomes, fun item -> Assert.False(item.moved))
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``MCP reclassification failure sets the tool error flag`` () =
    withDocument (fun fixture ->
        task {
            let! response =
                callTool fixture
                    """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"hermes_reclassify","arguments":{"document_id":999,"new_category":"tax"}}}"""
            use document = JsonDocument.Parse(response)
            let result = document.RootElement.GetProperty("result")
            Assert.True(result.GetProperty("isError").GetBoolean())
            let item = (result.GetProperty("content")).[0]
            let text = elementString item "text"
            use content = JsonDocument.Parse(text)
            Assert.Equal("Document 999 not found", content.RootElement.GetProperty("error").GetString())
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Single compatibility response reports classification without claiming a move`` () =
    withDocument (fun fixture ->
        task {
            let! response =
                ReclassificationApi.single
                    fixture.Db fixture.Files.Fs "/archive" fixture.DocumentId "tax"
            Assert.False(response.moved)
            Assert.True(response.reclassified)
            Assert.True(response.changed)
            Assert.Equal(Some "tax", response.category)
            Assert.Equal(None, response.error)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Main Classifier tuple signature remains callable`` () =
    withDocument (fun fixture ->
        task {
            let operation: Task<int * int> =
                Classifier.reclassifyUnsortedBatch
                    fixture.Db fixture.Files.Fs [] "/archive" 10
            let! changed, remaining = operation
            Assert.Equal(0, changed)
            Assert.Equal(1, remaining)
        })

let private failingCandidate (fixture: Fixture) =
    task {
        let! _ =
            fixture.Db.execNonQuery
                "UPDATE documents SET extracted_at = 'test-time' WHERE id = @id"
                [ "@id", Database.boxVal fixture.DocumentId ]
        fixture.Files.Put "/archive/stable/source.pdf.extracted.md" "invoice"
        fixture.Files.Fs.deleteFile "/archive/stable/source.pdf"
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Legacy Classifier wrapper exposes partial failure instead of returning success`` () =
    withDocument (fun fixture ->
        task {
            do! failingCandidate fixture
            let rules: Domain.ContentRule list =
                [ { Name = "invoice"; Conditions = [ Domain.ContentAny [ "invoice" ] ]
                    Category = "tax"; Confidence = 0.9 } ]
            let! error =
                Assert.ThrowsAnyAsync<System.Exception>(
                    System.Func<Task>(fun () ->
                        Classifier.reclassifyUnsortedBatch
                            fixture.Db fixture.Files.Fs rules "/archive" 10 :> Task))
            match error with
            | Classifier.ContentReclassificationFailed batch ->
                Assert.Equal(1, batch.FailedCount)
                Assert.Equal(1, batch.Items.Length)
            | other -> failwith $"Expected structured batch failure, got {other}"
        })
