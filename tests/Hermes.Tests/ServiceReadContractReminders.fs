module Hermes.Tests.ServiceReadContractReminders

open System
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open Hermes.Core
open Hermes.Tests.ServiceReadContractSupport

let private generatedVendor = "Generated before"
let private vendorField = "extracted_vendor"
let private correctionNote = "Synthetic reminder projection verification"
let private categoryTagCreator = "reclassification"
let private changedFields = [ vendorField; "classification_tier" ]

let private reminderSql =
    """INSERT INTO reminders
           (id,document_id,vendor,amount,due_date,category,status,snoozed_until,created_at)
       VALUES
           (1,@id,'Reminder vendor synthetic',13.25,'2025-03-15T00:00:00+00:00',
            'invoices','active',NULL,'2025-03-01'),
           (2,@id,NULL,20,'2025-03-16T00:00:00+00:00',
            'utilities','snoozed','2025-03-14T00:00:00+00:00','2025-03-01'),
           (3,@id,'Future snooze',30,'2025-03-17T00:00:00+00:00',
            'invoices','snoozed','2025-03-16T00:00:00+00:00','2025-03-01'),
           (4,@id,'Completed reminder',40,'2025-03-18T00:00:00+00:00',
            'invoices','completed',NULL,'2025-03-01'),
           (5,NULL,'Manual synthetic',NULL,'2025-03-10T00:00:00+00:00',
            'invoices','active',NULL,'2025-03-01');"""

let private assertLinked id expectedVendor (item: JsonElement) =
    Assert.Equal(id, (item.GetProperty("documentId") |> optionValue).GetInt64())
    Assert.Equal(expectedVendor, optionalText "vendor" item)
    Assert.Equal(Some "source.png", optionalText "fileName" item)
    Assert.Equal(13.25, (item.GetProperty("amount") |> optionValue).GetDouble())
    Assert.Equal("invoices", text "category" item)
    Assert.Equal("Active", text "status" item)
    let expected = DateTimeOffset(2025, 3, 15, 0, 0, 0, TimeSpan.Zero)
    Assert.Equal(expected, (item.GetProperty("dueDate") |> optionValue).GetDateTimeOffset())

let private assertNullableRows id expectedVendor (reminders: JsonElement) =
    let manual = reminders.[0]
    Assert.Equal(Some "Manual synthetic", optionalText "vendor" manual)
    Assert.Equal(JsonValueKind.Null, manual.GetProperty("documentId").ValueKind)
    Assert.Equal(JsonValueKind.Null, manual.GetProperty("fileName").ValueKind)
    Assert.Equal(JsonValueKind.Null, manual.GetProperty("amount").ValueKind)
    Assert.Equal("invoices", text "category" manual)
    Assert.Equal("Active", text "status" manual)
    let expiredSnooze = reminders.[2]
    Assert.Equal(expectedVendor, optionalText "vendor" expiredSnooze)
    Assert.Equal(id, (expiredSnooze.GetProperty("documentId") |> optionValue).GetInt64())
    Assert.Equal(Some "source.png", optionalText "fileName" expiredSnooze)
    Assert.Equal("Snoozed", text "status" expiredSnooze)
    Assert.Equal("utilities", text "category" expiredSnooze)
    Assert.Equal(20.0, (expiredSnooze.GetProperty("amount") |> optionValue).GetDouble())

let private mcpVendor (item: JsonElement) =
    if item.TryGetProperty("vendor") |> fst then optionalText "vendor" item else None

let private assertMcpRows expectedVendor (response: JsonElement) =
    let reminders = response.GetProperty("reminders")
    Assert.Equal<int64 list>([ 5L; 1L; 2L ], ids reminders)
    Assert.Equal<string option list>(
        [ Some "Manual synthetic"; expectedVendor; expectedVendor ],
        reminders.EnumerateArray() |> Seq.map mcpVendor |> Seq.toList)
    let linked = reminders.[1]
    Assert.Equal(13.25, linked.GetProperty("amount").GetDouble())
    Assert.Equal("2025-03-15", text "dueDate" linked)
    Assert.Equal("source.png", text "fileName" linked)
    Assert.Equal("/archive/service-contracts/reminder-source/source.png", text "documentPath" linked)
    Assert.Equal("active", text "status" linked)
    Assert.Equal("snoozed", text "status" reminders.[2])
    Assert.False(reminders.[0].TryGetProperty("documentPath") |> fst)
    Assert.False(reminders.[0].TryGetProperty("fileName") |> fst)
    Assert.False(reminders.[0].TryGetProperty("amount") |> fst)

let private assertReadSurfaces server id expectedVendor =
    task {
        let! before = snapshot server
        let! detail = getJson server $"/api/documents/{id}"
        Assert.Equal(expectedVendor, optionalText "vendor" detail)
        Assert.Equal(expectedVendor, optionalText "vendor" (detail.GetProperty("summary")))
        let! reminders = getJson server "/api/reminders"
        Assert.Equal<int64 list>([ 5L; 1L; 2L ], ids reminders)
        assertLinked id expectedVendor reminders.[1]
        assertNullableRows id expectedVendor reminders
        let! response = HttpRegressionFixture.mcpCall server "hermes_list_reminders" (JsonObject() :> obj)
        assertMcpRows expectedVendor (HttpRegressionFixture.payload response)
        do! assertUnchanged server before
    }

let private history server id =
    rows server "SELECT * FROM corrections WHERE document_id=@id ORDER BY id" [ "@id", Database.boxVal id ]

let private reminderRows server =
    rows server "SELECT * FROM reminders ORDER BY id" []

let private assertHistory id (original: string) (expected: string)
    (before: Map<string, obj> list) (after: Map<string, obj> list) =
    Assert.Equal(before.Length + 1, after.Length)
    Assert.Equal<Map<string, obj> list>(before, after |> List.take before.Length)
    let latest = Prelude.RowReader(List.last after)
    Assert.Equal(id, latest.Int64 "document_id" 0L)
    Assert.Equal(vendorField, latest.String "field" "")
    Assert.Equal(original, latest.String "original_value" "")
    Assert.Equal(expected, latest.String "corrected_value" "")
    Assert.Equal(correctionNote, latest.String "note" "")

let private isCategoryTag id (row: Map<string, obj>) =
    let reader = Prelude.RowReader row
    reader.Int64 "document_id" 0L = id
    && reader.String "tag" "" = "invoices"
    && reader.OptString "created_by" = Some categoryTagCreator

let private expectedCorrectionTags id (before: Map<string, obj> list) (after: Map<string, obj> list) =
    if before |> List.exists (isCategoryTag id) then before
    else
        Assert.Equal(before.Length + 1, after.Length)
        let added = List.last after
        let reader = Prelude.RowReader added
        let maxId = before |> List.map (fun row -> (Prelude.RowReader row).Int64 "id" 0L) |> List.fold max 0L
        Assert.True(reader.Int64 "id" 0L > maxId)
        Assert.True(reader.OptDateTimeOffset "created_at" |> Option.isSome)
        let expected =
            [ "document_id", Database.boxVal id
              "tag", Database.boxVal "invoices"
              "source", Database.boxVal "user"
              "confidence", Database.boxVal DBNull.Value
              "created_by", Database.boxVal categoryTagCreator ] |> Map.ofList
        Assert.Equal<Map<string, obj>>(expected, added |> Map.remove "id" |> Map.remove "created_at")
        before @ [added]

let private assertCorrectionPreserved server id (before: PreservedState) =
    task {
        let! after = preserve [ "corrections" ] changedFields server
        let tags = expectedCorrectionTags id before.Metadata.["tags"] after.Metadata.["tags"]
        let expected = { before with Metadata = before.Metadata |> Map.add "tags" tags }
        Assert.Equal<PreservedState>(expected, after)
    }

let private correctVendor server id (original: string) (expected: string) =
    task {
        let! preserved = preserve [ "corrections" ] changedFields server
        let! remindersBefore = reminderRows server
        let! historyBefore = history server id
        let request = {| corrections = [ {| field = vendorField; value = expected |} ]; note = correctionNote |}
        let! result = postJson server $"/api/documents/{id}/correct" (request :> obj)
        Assert.True(result.GetProperty("corrected").GetBoolean())
        let! historyAfter = history server id
        assertHistory id original expected historyBefore historyAfter
        let! remindersAfter = reminderRows server
        Assert.Equal<Map<string, obj> list>(remindersBefore, remindersAfter)
        do! assertCorrectionPreserved server id preserved
    }

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("Category", "Integration")>]
let ``Reminder reads prefer the linked document vendor including null and preserve manual rows`` (unknownVendor: bool) =
    withServer (fun server ->
        task {
            let! id = seed server "reminder-source"
            do! execute server reminderSql [ "@id", Database.boxVal id ]
            if unknownVendor then
                do! execute server "UPDATE documents SET extracted_vendor=NULL WHERE id=@id" [ "@id", Database.boxVal id ]
            let expected = if unknownVendor then None else Some generatedVendor
            do! assertReadSurfaces server id expected
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reminder reads follow two corrections without rewriting reminders or prior history`` () =
    withServer (fun server ->
        task {
            let! id = seed server "reminder-source"
            do! HttpRegressionFixture.seedCompletions server id
            do! execute server reminderSql [ "@id", Database.boxVal id ]
            do!
                execute server
                    """INSERT INTO tags(document_id,tag,source,confidence,created_at)
                       VALUES (@id,'invoices','user',0.75,'2025-03-01')"""
                    [ "@id", Database.boxVal id ]
            let first = "Verified vendor once"
            let latest = "Verified vendor latest"
            do! correctVendor server id generatedVendor first
            do! assertReadSurfaces server id (Some first)
            do! correctVendor server id first latest
            do! assertReadSurfaces server id (Some latest)
        })
