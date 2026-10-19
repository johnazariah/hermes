module Hermes.Tests.PersistedComprehensionHttpTests

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture
open Hermes.Tests.RetrievalHttpFixtures

let private assertCanonical documentId (value: JsonElement) =
    Assert.Equal("available", value.GetProperty("status").GetString())
    Assert.Equal(documentId, value.GetProperty("source").GetProperty("documentId").GetInt64())
    Assert.Equal(0L, value.GetProperty("publicationGeneration").GetInt64())
    let fields = value.GetProperty("comprehension").GetProperty("fields")
    Assert.Equal(1234.56M, fields.GetProperty("gross_pay").GetDecimal())
    Assert.Equal(234.56M, fields.GetProperty("withheld_tax").GetDecimal())
    Assert.Equal(1, fields.GetProperty("earnings").GetArrayLength())
    Assert.True(value.GetProperty("comprehension").GetProperty("unknown_future_field").GetProperty("preserved").GetBoolean())
    Assert.Equal("unavailable", value.GetProperty("enrichment").GetProperty("status").GetString())
    Assert.Equal("no_document_scoped_attribution", value.GetProperty("enrichment").GetProperty("reason").GetString())
    Assert.Equal(64, value.GetProperty("publicationSha256").GetString() |> Option.ofObj |> Option.defaultValue "" |> String.length)

let private both server documentId =
    task {
        let! http = get server $"/api/documents/{documentId}/comprehension"
        let! mcp = mcpCall server "hermes_get_comprehension" (box {| document_id = documentId |})
        assertCanonical documentId http
        assertCanonical documentId (payload mcp)
        Assert.Equal(http.GetRawText(), (payload mcp).GetRawText())
    }

let private verifyReadOnly path server =
    task {
        let! id = seedComprehension server "/archive/persisted/payslip.png" canonical
        server.Files.Put "/archive/persisted/thread.comprehension.json"
            """{"deep_extraction":{"fields":{"sibling_private_marker":999999}}}"""
        let! sqlBefore = databaseHash server
        let! filesBefore = FileKeywordFixtures.fileHashes server
        let! physicalBefore = physicalHashes path
        do! both server id
        do! both server id
        let! sqlAfter = databaseHash server
        let! filesAfter = FileKeywordFixtures.fileHashes server
        let! physicalAfter = physicalHashes path
        Assert.Equal(sqlBefore, sqlAfter)
        Assert.Equal<Map<string, string>>(filesBefore, filesAfter)
        Assert.Equal<Map<string, string>>(physicalBefore, physicalAfter)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Actual HTTP MCP persisted reads preserve canonical fields source files and SQLite hashes`` () =
    task {
        let directory = Path.Combine(Path.GetTempPath(), "hermes-persisted-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(directory) |> ignore
        let path = Path.Combine(directory, "synthetic.sqlite")
        try
            do! withServerUsing (fileDatabase path) None McpAccess.readOnly [] None (verifyReadOnly path)
        finally Directory.Delete(directory, true)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Shared sibling sidecar cannot replace document-specific canonical fields`` () =
    withServer None (fun server ->
        task {
            let! first = seedComprehension server "/archive/shared/first.png" canonical
            let siblingJson = """{"document_type":"payslip","fields":{"gross_pay":999999,"sibling_marker":"do-not-attribute"}}"""
            let! sibling = seedComprehension server "/archive/shared/second.png" siblingJson
            server.Files.Put "/archive/shared/thread.comprehension.json" siblingJson
            do! both server first
            let! value = get server $"/api/documents/{sibling}/comprehension"
            Assert.Equal(999999, value.GetProperty("comprehension").GetProperty("fields").GetProperty("gross_pay").GetInt32())
            Assert.Equal("unavailable", value.GetProperty("enrichment").GetProperty("status").GetString())
        })

[<Theory>]
[<InlineData("reextract")>]
[<InlineData("recomprehend")>]
[<Trait("Category", "Integration")>]
let ``Retained shared JSON and historical publications cannot certify a new generation`` operation =
    withServer None (fun server ->
        task {
            let! id = seedComprehension server "/archive/generation/payslip.png" canonical
            server.Files.Put "/archive/generation/thread.comprehension.json" canonical
            do! applyReflow server id operation
            let! http = response server $"/api/documents/{id}/comprehension" 409
            Assert.Equal("not_available", http.GetProperty("status").GetString())
            let! mcp = mcpCall server "hermes_get_comprehension" (box {| document_id = id |})
            Assert.True(mcp.GetProperty("result").GetProperty("isError").GetBoolean())
            Assert.Equal("not_available", (payload mcp).GetProperty("status").GetString())
        })

[<Theory>]
[<InlineData("[]")>]
[<InlineData("{broken")>]
[<Trait("Category", "Integration")>]
let ``Invalid canonical publications are unavailable rather than repaired`` publication =
    withServer None (fun server ->
        task {
            let! id = seedComprehension server "/archive/invalid/payslip.png" publication
            let! before = databaseHash server
            let! http = response server $"/api/documents/{id}/comprehension" 409
            Assert.Equal("invalid_canonical_publication", http.GetProperty("error").GetString())
            let! mcp = mcpCall server "hermes_get_comprehension" (box {| document_id = id |})
            Assert.Equal("invalid_canonical_publication", (payload mcp).GetProperty("error").GetString())
            let! after = databaseHash server
            Assert.Equal(before, after)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Missing document and invalid IDs have distinct transport outcomes`` () =
    withServer None (fun server ->
        task {
            let! missing = response server "/api/documents/999999/comprehension" 404
            Assert.Equal("missing", missing.GetProperty("status").GetString())
            let! invalid = response server "/api/documents/0/comprehension" 400
            Assert.Equal("invalid_request", invalid.GetProperty("status").GetString())
            let! mcp = rawCall server "hermes_get_comprehension" """{"document_id":[]}"""
            Assert.Equal(-32602, mcp.GetProperty("error").GetProperty("code").GetInt32())
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Source hash failure cannot expose a canonical publication as current`` () =
    withServer None (fun server ->
        task {
            let path = "/archive/hash/payslip.png"
            let! id = seedComprehension server path canonical
            server.Files.Put path "externally changed synthetic source"
            server.Files.Bytes.TryRemove(server.Files.Norm path) |> ignore
            let! http = response server $"/api/documents/{id}/comprehension" 409
            Assert.Equal("not_available", http.GetProperty("status").GetString())
            let! mcp = mcpCall server "hermes_get_comprehension" (box {| document_id = id |})
            Assert.Equal("not_available", (payload mcp).GetProperty("status").GetString())
        })
