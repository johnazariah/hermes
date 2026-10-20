module Hermes.Tests.ServiceMutationContractMissing

// Authorized bug-coupled logical handler contracts; no HTTP authentication assertion.
open System.Net.Http
open Xunit
open Hermes.Core
open Hermes.Tests.ServiceReadContractSupport

let private prepare server =
    task {
        let! id = seed server "missing-mutation-control"
        do! execute server
                """INSERT INTO tags(document_id,tag) VALUES (@id,'keep-control');
                   INSERT INTO suggestions(id,document_id,proposed_category,current_category,confidence)
                   VALUES (1,@id,'receipts','invoices',0.8);"""
                [ "@id", Database.boxVal id ]
        return id
    }

let private request server (method: string) path body expected =
    if method = "GET" then read server path expected
    else writeText server (new HttpMethod(method)) path "application/json" body expected

[<Theory>]
[<InlineData("POST", "/api/documents/999999/star", "null", "Document 999999 not found")>]
[<InlineData("GET", "/api/documents/999999/tags", "", "Document 999999 not found")>]
[<InlineData("POST", "/api/documents/999999/tags", """{"tags":["must-not-insert"]}""", "Document 999999 not found")>]
[<InlineData("DELETE", "/api/documents/999999/tags/keep-control", "", "Document 999999 not found")>]
[<InlineData("POST", "/api/documents/999999/correct", """{"corrections":[{"field":"extracted_vendor","value":"must-not-persist"}]}""", "Document 999999 not found")>]
[<InlineData("POST", "/api/suggestions/999999/approve", "null", "Suggestion 999999 not found")>]
[<InlineData("POST", "/api/suggestions/999999/reject", "null", "Suggestion 999999 not found")>]
[<Trait("Category", "Integration")>]
let ``HTTP missing mutation resources are explicit JSON 404 with no archive side effects``
    (method: string) (path: string) (body: string) (expectedError: string) =
    withServer (fun server ->
        task {
            let! _ = prepare server
            let! before = snapshot server
            let! reply = request server method path body 404
            let response = decode reply
            Assert.Equal(expectedError, text "error" response)
            Assert.False(response.TryGetProperty("starred") |> fst)
            Assert.False(response.TryGetProperty("added") |> fst)
            Assert.False(response.TryGetProperty("approved") |> fst)
            Assert.False(response.TryGetProperty("rejected") |> fst)
            do! assertUnchanged server before
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``HTTP an existing document with no tags is an empty collection not a missing resource`` () =
    withServer (fun server ->
        task {
            let! id = seed server "existing-empty-tags"
            let! before = snapshot server
            let! tags = getJson server $"/api/documents/{id}/tags"
            Assert.Equal(0, tags.GetArrayLength())
            do! assertUnchanged server before
        })

let private toggle server id expected =
    task {
        let! response = postJson server $"/api/documents/{id}/star" null
        Assert.Equal(expected, response.GetProperty("starred").GetBoolean())
        let! document = HttpRegressionFixture.readDocument server id
        Assert.Equal((if expected then 1L else 0L), (Prelude.RowReader document).Int64 "starred" -1L)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``HTTP existing star resources retain persisted toggle semantics`` () =
    withServer (fun server ->
        task {
            let! id = prepare server
            let! before = preserve [] [ "starred" ] server
            do! toggle server id true
            do! toggle server id false
            do! assertPreserved [] [ "starred" ] server before
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``HTTP an existing suggestion can resolve without being mistaken for a missing ID`` () =
    withServer (fun server ->
        task {
            let! _ = prepare server
            let! before = preserve [] [] server
            let! response = postJson server "/api/suggestions/1/reject" null
            Assert.True(response.GetProperty("rejected").GetBoolean())
            Assert.Equal(1L, response.GetProperty("id").GetInt64())
            let! resolved = rows server "SELECT status,resolved_at FROM suggestions WHERE id=1" []
            let reader = resolved |> List.exactlyOne |> Prelude.RowReader
            Assert.Equal("rejected", reader.String "status" "")
            Assert.True(reader.OptString "resolved_at" |> Option.exists (System.String.IsNullOrWhiteSpace >> not))
            do! assertPreserved [] [] server before
        })
