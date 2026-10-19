module Hermes.Tests.FileKeywordHttpTests

open System
open System.Net.Http.Json
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture
open Hermes.Tests.FileKeywordFixtures

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Body-only marker crosses real intake HTTP MCP and existing React query route`` () =
    withServer None (fun server ->
        task {
            let marker = "bodyonlyquartzmarker"
            let! document = ingestBody server $"A synthetic receipt includes {marker} in its body."
            let id = Document.id document
            let path = EmailChannelTestSupport.required "saved_path" document
            do! assertViews server marker id path "email"
            let! react = get server $"/api/documents?q={marker}"
            Assert.Equal(1, react.GetArrayLength())
            Assert.Equal(id, react.[0].GetProperty("id").GetInt64())
            Assert.Contains(marker, textProperty "snippet" react.[0])
            Assert.Equal("email_body", textProperty "sourceType" react.[0])
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Extracted-only marker crosses real V5 publication HTTP MCP and React query route`` () =
    withServer None (fun server ->
        task {
            let marker = "sidecaronlyzirconmarker"
            let! id = seedExtracted server $"Synthetic extracted evidence: {marker}."
            do! assertViews server marker id sourcePath "document"
            let! react = get server $"/api/documents?q={marker}&category=invoices"
            Assert.Equal(1, react.GetArrayLength())
            Assert.Equal(id, react.[0].GetProperty("id").GetInt64())
            Assert.Equal("source.png", textProperty "originalName" react.[0])
            Assert.Contains(marker, textProperty "snippet" react.[0])
            let! source = server.Files.Fs.readAllText sourcePath
            Assert.DoesNotContain(marker, source)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Metadata and content matches collapse to one stable email document`` () =
    withServer None (fun server ->
        task {
            let! document = ingestBody server "Synthetic content shares the metadata keyword."
            let id = Document.id document
            let path = EmailChannelTestSupport.required "saved_path" document
            do! assertViews server "Synthetic" id path "email"
            let! direct = Search.executeEmailSearch server.Db (Search.defaultFilter "Synthetic")
            let hit = direct |> List.exactlyOne
            Assert.Equal(id, hit.DocumentId)
            Assert.Equal(path, hit.SavedPath)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Metadata-only documents remain searchable without reading missing files`` () =
    withServer None (fun server ->
        task {
            let! id = seedSource server sourcePath sourceText
            server.Files.Fs.deleteFile sourcePath
            let! _ =
                server.Db.execNonQuery
                    "UPDATE documents SET subject = 'metadatacoppermarker' WHERE id = @id"
                    [ "@id", Database.boxVal id ]
            do! assertViews server "metadatacoppermarker" id sourcePath "document"
            do! assertAbsent server "invisiblebodymarker"
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Legacy metadata-only message identities are stable and distinct`` () =
    withServer None (fun server ->
        task {
            let! _ =
                server.Db.execNonQuery
                    """INSERT INTO messages(gmail_id, account, subject)
                       VALUES ('first', 'synthetic', 'legacymarker'),
                              ('second', 'synthetic', 'legacymarker');""" []
            let! first = httpSearch server "legacymarker"
            let! second = mcpSearch server "legacymarker"
            let ids (value: System.Text.Json.JsonElement) =
                value.EnumerateArray() |> Seq.map (fun hit -> hit.GetProperty("id").GetInt64()) |> Seq.toList
            Assert.Equal(2, first.GetArrayLength())
            Assert.Equal<int64 list>(ids first, ids second)
            Assert.Equal(2, ids first |> List.distinct |> List.length)
            Assert.All(ids first, fun id -> Assert.True(id < 0L))
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Read-only HTTP and MCP searches preserve source files metadata claims and ledger`` () =
    withServer None (fun server ->
        task {
            let marker = "readonlyopalmarker"
            let! id = seedExtracted server $"Extracted {marker} evidence."
            let! before = metadataSnapshot server
            let! hashes = fileHashes server
            do! assertViews server marker id sourcePath "document"
            do! assertViews server marker id sourcePath "document"
            let! after = metadataSnapshot server
            let! afterHashes = fileHashes server
            Assert.Equal<Map<string, Map<string, obj> list>>(before, after)
            Assert.Equal<Map<string, string>>(hashes, afterHashes)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reclassification preserves file-content hits and source identity`` () =
    withServer None (fun server ->
        task {
            let marker = "classifiedambermarker"
            let! id = seedExtracted server marker
            let! hashes = fileHashes server
            use! response =
                server.Client.PutAsJsonAsync($"/api/documents/{id}/category", box {| category = "tax" |})
            Assert.Equal(200, int response.StatusCode)
            do! assertViews server marker id sourcePath "document"
            let! results = get server $"/api/search?q={marker}&category=tax"
            Assert.Equal(1, results.GetArrayLength())
            let! after = fileHashes server
            Assert.Equal<Map<string, string>>(hashes, after)
        })
