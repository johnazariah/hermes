module Hermes.Tests.ServiceMutationContractCorrectionSupport

open System.Net.Http
open System.Text.Json
open Xunit
open Hermes.Core
open Hermes.Tests.ServiceReadContractSupport

let note = "synthetic multi-correction verification"

let prepare server =
    task {
        let! id = seed server "correction-contract"
        do! HttpRegressionFixture.seedCompletions server id
        let! previous =
            DocumentManagement.correctField server.Db id "extracted_vendor"
                "Previously verified" (Some "existing verified history")
        Assert.Equal<Result<unit, string>>(Ok (), previous)
        do! execute server "INSERT INTO tags(document_id,tag) VALUES (@id,'keep-user')" [ "@id", Database.boxVal id ]
        return id
    }

let history server id =
    rows server "SELECT * FROM corrections WHERE document_id=@id ORDER BY id" [ "@id", Database.boxVal id ]

let send server id (body: string) expected =
    writeText server HttpMethod.Post $"/api/documents/{id}/correct" "application/json" body expected

let body (corrections: (string * string) list) =
    let fields = corrections |> List.map (fun (field, value) -> {| field = field; value = value |})
    JsonSerializer.Serialize({| corrections = fields; note = note |})

let assertHistoryPrefix (before: Map<string, obj> list) (after: Map<string, obj> list) =
    Assert.True(List.length after >= List.length before)
    Assert.Equal<Map<string, obj> list>(before, after |> List.take (List.length before))

let assertAppended expected before after =
    assertHistoryPrefix before after
    let appended = after |> List.skip (List.length before)
    let values =
        appended |> List.map (fun row ->
            let reader = Prelude.RowReader row
            reader.String "field" "", reader.String "corrected_value" "")
    Assert.Equal<(string * string) list>(expected, values)
    appended |> List.iter (fun row -> Assert.Equal(note, (Prelude.RowReader row).String "note" ""))

let assertDocument server id vendor amount date category =
    task {
        let! document = HttpRegressionFixture.readDocument server id
        let reader = Prelude.RowReader document
        Assert.Equal(vendor, reader.String "extracted_vendor" "")
        Assert.Equal(Some amount, reader.OptFloat "extracted_amount")
        Assert.Equal(date, reader.String "extracted_date" "")
        Assert.Equal(category, reader.String "category" "")
        Assert.Equal("manual", reader.String "classification_tier" "")
        let! response = getJson server $"/api/documents/{id}"
        let summary = response.GetProperty("summary")
        Assert.Equal(Some vendor, optionalText "vendor" summary)
        Assert.Equal(amount, (summary.GetProperty("extractedAmount") |> optionValue).GetDouble())
        Assert.Equal(Some date, optionalText "extractedDate" summary)
        Assert.Equal(category, text "category" summary)
    }

let assertTags server =
    task {
        let! tags = rows server "SELECT tag,source,created_by FROM tags ORDER BY tag" []
        let actual =
            tags |> List.map (fun row ->
                let reader = Prelude.RowReader row
                reader.String "tag" "", reader.OptString "created_by")
            |> Map.ofList
        Assert.Equal<Map<string, string option>>(
            Map.ofList [ "keep-user", None; "receipts", Some "reclassification" ], actual)
        tags |> List.iter (fun row -> Assert.Equal("user", (Prelude.RowReader row).String "source" ""))
    }
