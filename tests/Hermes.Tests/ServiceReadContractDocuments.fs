module Hermes.Tests.ServiceReadContractDocuments

open System
open System.Text.Json
open Xunit
open Hermes.Core
open Hermes.Tests.ServiceReadContractSupport

let private assertPipeline understood extracted embedded (detail: JsonElement) =
    let status = detail.GetProperty("pipelineStatus")
    Assert.Equal(understood, status.GetProperty("understood").GetBoolean())
    Assert.Equal(extracted, status.GetProperty("extracted").GetBoolean())
    Assert.Equal(embedded, status.GetProperty("embedded").GetBoolean())

let private assertStageSummary id (document: JsonElement) =
    Assert.Equal(id, document.GetProperty("id").GetInt64())
    Assert.Equal("source.png", text "originalName" document)
    Assert.Equal("invoices", text "category" document)
    Assert.Equal(Some "manual_drop", optionalText "sourceType" document)
    Assert.Equal(Some "/original/source.png", optionalText "sourcePath" document)
    Assert.Equal(Some "Generated before", optionalText "vendor" document)
    Assert.Equal(Some "extracted", optionalText "stage" document)
    Assert.Equal(Some "old-method", optionalText "extractionMethod" document)
    Assert.Equal(10.0, (document.GetProperty("extractedAmount") |> optionValue).GetDouble())
    Assert.Equal(JsonValueKind.Null, document.GetProperty("account").ValueKind)

[<Fact>]
[<Trait("Category", "Integration")>]
let ``HTTP categories count documents and omit an empty compatibility category`` () =
    withServer (fun server ->
        task {
            let! _ = seed server "category-first"
            let! _ = seed server "category-second"
            let! empty = seed server "category-empty"
            do! execute server "UPDATE documents SET category='' WHERE id=@id" [ "@id", Database.boxVal empty ]
            let! before = snapshot server
            let! categories = getJson server "/api/categories"
            Assert.Equal(1, categories.GetArrayLength())
            Assert.Equal("invoices", text "category" categories.[0])
            Assert.Equal(2L, categories.[0].GetProperty("count").GetInt64())
            do! assertUnchanged server before
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``HTTP document pages preserve ordering category filtering and the stage projection`` () =
    withServer (fun server ->
        task {
            let! first = seed server "page-first"
            let! second = seed server "page-second"
            do! execute server "UPDATE documents SET stage='extracted' WHERE id=@id" [ "@id", Database.boxVal first ]
            let! before = snapshot server
            let! all = getJson server "/api/documents"
            let! page = getJson server "/api/documents?category=invoices&offset=1&limit=1"
            let! staged = getJson server "/api/documents?stage=extracted&offset=0&limit=1"
            Assert.Equal<int64 list>([ second; first ], ids all)
            Assert.Equal<int64 list>([ first ], ids page)
            Assert.Equal(1, staged.GetArrayLength())
            assertStageSummary first staged.[0]
            do! assertUnchanged server before
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``HTTP document detail distinguishes extraction from category understanding`` () =
    withServer (fun server ->
        task {
            let! first = seed server "detail-first"
            let! second = seed server "detail-second"
            do! execute server "UPDATE documents SET category='unsorted',extracted_at=NULL WHERE id=@id" [ "@id", Database.boxVal second ]
            let! before = snapshot server
            let! extracted = getJson server $"/api/documents/{first}"
            let! pending = getJson server $"/api/documents/{second}"
            assertPipeline true true false extracted
            assertPipeline false false false pending
            Assert.Equal("/archive/service-contracts/detail-first/source.png", text "filePath" extracted)
            Assert.Equal(JsonValueKind.Null, extracted.GetProperty("extractedText").ValueKind)
            Assert.Equal(JsonValueKind.Null, extracted.GetProperty("comprehension").ValueKind)
            do! assertUnchanged server before
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``HTTP markdown content is the exact current extraction artifact`` () =
    withServer (fun server ->
        task {
            let marker = "Synthetic contract extraction without an external provider."
            let! id = FileKeywordFixtures.seedExtracted server marker
            let! expected = server.Files.Fs.readAllText (FileKeywordFixtures.sourcePath + ".extracted.md")
            let! before = snapshot server
            let! content = getJson server $"/api/documents/{id}/content"
            Assert.Equal(expected, text "markdown" content)
            Assert.Contains(marker, text "markdown" content)
            do! assertUnchanged server before
        })

[<Theory>]
[<InlineData("/api/documents/999999")>]
[<InlineData("/api/documents/999999/content")>]
[<InlineData("/api/documents/999999/file")>]
[<InlineData("/api/reflow/999999")>]
[<Trait("Category", "Integration")>]
let ``HTTP missing read resources are 404 rather than HTML`` (path: string) =
    withServer (fun server ->
        task {
            let! _ = seed server "missing-read-control"
            let! before = snapshot server
            let! reply = read server path 404
            Assert.NotEqual<string>("text/html", reply.MediaType)
            Assert.DoesNotContain("<!doctype", reply.Body.ToLowerInvariant())
            do! assertUnchanged server before
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``HTTP missing physical source preserves its existing synthetic source claim`` () =
    withServer (fun server ->
        task {
            let! id = seed server ("physical-absence-" + Guid.NewGuid().ToString("N"))
            let! before = snapshot server
            let! reply = read server $"/api/documents/{id}/file" 404
            Assert.Equal("File not found on disk", text "error" (decode reply))
            do! assertUnchanged server before
        })
