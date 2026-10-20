module Hermes.Tests.ServiceReadContractPagination

open System
open System.Text.Json
open Xunit
open Hermes.Core
open Hermes.Tests.ServiceReadContractSupport

let private prepare server =
    task {
        let! id = seed server "pagination"
        do! execute server
                """INSERT INTO activity_log(category,message,document_id)
                   VALUES ('synthetic','pagination control',@id);"""
                [ "@id", Database.boxVal id ]
        let! correction =
            DocumentManagement.correctField server.Db id "extracted_vendor"
                "Pagination verified vendor" (Some "synthetic history")
        Assert.Equal<Result<unit, string>>(Ok (), correction)
        return id
    }

[<Theory>]
[<InlineData("/api/documents?offset=abc", "offset")>]
[<InlineData("/api/documents?offset=-1", "offset")>]
[<InlineData("/api/documents?offset=2147483648", "offset")>]
[<InlineData("/api/documents?offset=9223372036854775807", "offset")>]
[<InlineData("/api/documents?offset=0&offset=1", "offset")>]
[<InlineData("/api/documents?limit=", "limit")>]
[<InlineData("/api/documents?limit=1&limit=2", "limit")>]
[<InlineData("/api/documents?stage=embedded&offset=1.5", "offset")>]
[<InlineData("/api/documents?stage=embedded&limit=NaN", "limit")>]
[<InlineData("/api/documents?stage=embedded&limit=-1", "limit")>]
[<InlineData("/api/documents?stage=embedded&limit=2147483648", "limit")>]
[<InlineData("/api/activity?limit=abc", "limit")>]
[<InlineData("/api/activity?limit=-1", "limit")>]
[<InlineData("/api/activity?limit=2147483648", "limit")>]
[<InlineData("/api/activity?limit=1&limit=2", "limit")>]
[<InlineData("/api/corrections?limit=abc", "limit")>]
[<InlineData("/api/corrections?limit=-1", "limit")>]
[<InlineData("/api/corrections?limit=2147483648", "limit")>]
[<InlineData("/api/corrections?limit=1&limit=2", "limit")>]
[<Trait("Category", "Integration")>]
let ``HTTP invalid pagination is a JSON bad request without database or filesystem changes`` (path: string) (field: string) =
    withServer (fun server ->
        task {
            let! _ = prepare server
            let! before = snapshot server
            let! reply = read server path 400
            let error = decode reply
            Assert.Contains(field, text "error" error)
            Assert.False(String.IsNullOrWhiteSpace(text "error" error))
            do! assertUnchanged server before
        })

let private assertBoundaryPayload (path: string) id expectedCount (response: JsonElement) =
    Assert.Equal(expectedCount, response.GetArrayLength())
    if expectedCount > 0 then
        if path.StartsWith("/api/documents", StringComparison.Ordinal) then
            Assert.Equal(id, response.[0].GetProperty("id").GetInt64())
            Assert.Equal("source.png", text "originalName" response.[0])
        elif path.StartsWith("/api/activity", StringComparison.Ordinal) then
            Assert.Equal("pagination control", text "message" response.[0])
            Assert.Equal(id, (response.[0].GetProperty("documentId") |> optionValue).GetInt64())
        else
            Assert.Equal(id, response.[0].GetProperty("documentId").GetInt64())
            Assert.Equal("extracted_vendor", text "field" response.[0])
            Assert.Equal("Pagination verified vendor", text "correctedValue" response.[0])

[<Theory>]
[<InlineData("/api/documents?offset=0&limit=0", 0)>]
[<InlineData("/api/documents?offset=2147483647&limit=1", 0)>]
[<InlineData("/api/documents?limit=2147483647", 1)>]
[<InlineData("/api/documents?stage=embedded&limit=2147483647", 1)>]
[<InlineData("/api/activity?limit=0", 0)>]
[<InlineData("/api/activity?limit=2147483647", 1)>]
[<InlineData("/api/corrections?limit=0", 0)>]
[<InlineData("/api/corrections?limit=2147483647", 1)>]
[<Trait("Category", "Integration")>]
let ``HTTP representable pagination boundaries preserve empty and populated response contracts`` (path: string) expectedCount =
    withServer (fun server ->
        task {
            let! id = prepare server
            let! before = snapshot server
            let! response = getJson server path
            assertBoundaryPayload path id expectedCount response
            do! assertUnchanged server before
        })
