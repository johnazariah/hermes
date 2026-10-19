module Hermes.Tests.PersistedReadRaceHttpTests

open System.Text
open System.Text.Json
open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture
open Hermes.Tests.RetrievalRaceFixtures
open Hermes.Tests.PersistedReadRaceFixtures

let private assertSource (expected: Expected) (body: JsonElement) =
    let source = body.GetProperty("source")
    Assert.Equal(expected.DocumentId, source.GetProperty("documentId").GetInt64())
    Assert.Equal(expected.SavedPath, text "savedPath" source)
    Assert.Equal(expected.Sha256, text "sha256" source)
    Assert.Equal(expected.Generation, body.GetProperty("generation").GetInt64())

let private assertFields (expected: Payslip) (body: JsonElement) =
    let actual = body.GetProperty("comprehension")
    let fields = actual.GetProperty("fields")
    Assert.Equal(expected.GrossPay, fields.GetProperty("gross_pay").GetDecimal())
    Assert.Equal(expected.WithheldTax, fields.GetProperty("withheld_tax").GetDecimal())
    Assert.Equal((json expected.Json).GetRawText(), actual.GetRawText())
    let digest = expected.Json |> Encoding.UTF8.GetBytes |> SourceFiles.hash
    Assert.Equal(digest, text "publicationSha256" body)

let private assertFinancial selected expected reply =
    assertReply selected 200 false reply
    Assert.Equal("available", text "status" reply.Body)
    assertSource expected reply.Body
    assertFields expected.Payslip reply.Body
    let enrichment = reply.Body.GetProperty("enrichment")
    Assert.Equal("unavailable", text "status" enrichment)
    Assert.Equal("no_document_scoped_attribution", text "reason" enrichment)
    Assert.DoesNotContain("shared_unattributed_deep_marker", reply.Body.GetRawText())

let private verifyBoth server expected =
    task {
        let! http = comprehension Http server expected.DocumentId
        assertFinancial Http expected http
        let! mcp = comprehension Mcp server expected.DocumentId
        assertFinancial Mcp expected mcp
    }

let private assertSuperseded selected reply =
    assertReply selected 409 true reply
    Assert.Equal("not_available", text "status" reply.Body)
    Assert.Equal("comprehension_changed_during_read", text "error" reply.Body)
    Assert.False(reply.Body.TryGetProperty("comprehension") |> fst)
    Assert.False(reply.Body.TryGetProperty("generation") |> fst)

let private verifyBefore server scenario =
    task {
        do! verifyBoth server scenario.Original
        do! verifyBoth server scenario.Sibling
        let! after = checkpoint server
        Assert.Equal<Checkpoint>(scenario.Before, after)
    }

let private verifyAfter server scenario checkpointAfterWriter =
    task {
        do! verifyBoth server scenario.Replacement
        do! verifyBoth server scenario.Sibling
        let! after = checkpoint server
        Assert.Equal<Checkpoint>(checkpointAfterWriter, after)
        Assert.Equal<Map<string, string>>(scenario.Before.FileHashes, after.FileHashes)
    }

let private run pause selected server =
    task {
        let! scenario = prepare server
        do! verifyBefore server scenario
        arm pause.Gate
        let pending = comprehension selected server scenario.Original.DocumentId
        let! completed = interleave pause.Gate pending (transition server scenario)
        assertSuperseded selected completed.Reply
        do! verifyAfter server scenario completed.State
    }

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("Category", "Integration")>]
let ``Paused persisted transport read cannot certify A after current financial publication B replaces its source generation`` useMcp =
    let pause = confirmationPause ()
    withServerUsing (database pause) None McpAccess.readOnly [] None
        (run pause (transport useMcp))
