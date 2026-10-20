module Hermes.Tests.ServiceMutationContractCorrectionAtomicity

open Xunit
open Hermes.Core
open Hermes.Tests.ServiceReadContractSupport
open Hermes.Tests.ServiceMutationContractCorrectionSupport

let private changedFields =
    [ "extracted_vendor"; "extracted_amount"; "extracted_date"; "category"; "classification_tier" ]

let private corrections =
    [ "extracted_vendor", "Reviewed once"
      "extracted_vendor", "Reviewed final"
      "extracted_amount", "42.5"
      "extracted_date", "2025-06-30"
      "category", "receipts" ]

let private assertOriginalStringValues appended =
    let readers = appended |> List.map Prelude.RowReader
    Assert.Equal("Previously verified", readers.[0].String "original_value" "")
    Assert.Equal("Reviewed once", readers.[1].String "original_value" "")
    Assert.Equal("2020-01-01", readers.[3].String "original_value" "")
    Assert.Equal("invoices", readers.[4].String "original_value" "")

let private verifySuccessfulBatch server id beforeHistory =
    task {
        do! assertDocument server id "Reviewed final" 42.5 "2025-06-30" "receipts"
        let! afterHistory = history server id
        assertAppended corrections beforeHistory afterHistory
        afterHistory |> List.skip beforeHistory.Length |> assertOriginalStringValues
        do! assertTags server
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``HTTP a legitimate multi-correction batch preserves prior history source identity and stage evidence`` () =
    withServer (fun server ->
        task {
            let! id = prepare server
            let! beforeHistory = history server id
            let! before = preserve [ "corrections"; "tags" ] changedFields server
            let! reply = send server id (body corrections) 200
            let response = decode reply
            Assert.True(response.GetProperty("corrected").GetBoolean())
            Assert.False(response.TryGetProperty("error") |> fst)
            do! verifySuccessfulBatch server id beforeHistory
            do! assertPreserved [ "corrections"; "tags" ] changedFields server before
        })

let private failLaterWriteSql =
    """CREATE TRIGGER service_contract_fail_later_correction
       BEFORE UPDATE OF extracted_amount ON documents
       WHEN NEW.extracted_amount=99
       BEGIN
           SELECT RAISE(ABORT,'synthetic later correction write failure');
       END;"""

let private retryCorrections =
    [ "extracted_vendor", "Temporary verified vendor"
      "extracted_amount", "99" ]

let private assertRecovery server id beforeHistory =
    task {
        let! before = preserve [ "corrections"; "tags" ] [ "extracted_vendor"; "extracted_amount"; "classification_tier" ] server
        let! reply = send server id (body retryCorrections) 200
        Assert.True((decode reply).GetProperty("corrected").GetBoolean())
        do! assertDocument server id "Temporary verified vendor" 99.0 "2020-01-01" "invoices"
        let! afterHistory = history server id
        assertAppended retryCorrections beforeHistory afterHistory
        do! assertPreserved [ "corrections"; "tags" ] [ "extracted_vendor"; "extracted_amount"; "classification_tier" ] server before
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``HTTP a later SQLite correction failure rolls back earlier writes and permits a clean retry`` () =
    withServer (fun server ->
        task {
            let! id = prepare server
            let! beforeHistory = history server id
            do! execute server failLaterWriteSql []
            let! before = snapshot server
            let! reply = send server id (body retryCorrections) 500
            let response = decode reply
            Assert.Equal("correction_batch_failed", text "error" response)
            Assert.Equal("No corrections were applied", text "message" response)
            Assert.False(response.TryGetProperty("corrected") |> fst)
            do! assertUnchanged server before
            do! execute server "DROP TRIGGER service_contract_fail_later_correction" []
            do! assertRecovery server id beforeHistory
        })
