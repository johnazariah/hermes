module Hermes.Tests.ServiceMutationContractCorrectionValidation

// These controls validate the authorized batch defect, not HTTP authentication.
open Xunit
open Hermes.Tests.ServiceReadContractSupport
open Hermes.Tests.ServiceMutationContractCorrectionSupport

let private rejectAnyWriteSql =
    """CREATE TRIGGER service_contract_reject_correction_write
       BEFORE INSERT ON corrections
       BEGIN
           SELECT RAISE(ABORT,'unexpected correction write before complete validation');
       END;"""

[<Theory>]
[<InlineData(
    """{"corrections":[{"field":"extracted_vendor","value":"must-not-persist"},{"field":"sha256","value":"bad"}]}""",
    "Field 'sha256' is not correctable")>]
[<InlineData(
    """{"corrections":[{"field":"extracted_vendor","value":"must-not-persist"},{"field":"extracted_amount","value":42}]}""",
    "value must be a string")>]
[<InlineData(
    """{"corrections":[{"field":"extracted_vendor","value":"must-not-persist"},{"value":"missing field"}]}""",
    "field is required")>]
[<InlineData(
    """{"corrections":[{"field":"extracted_vendor","value":"must-not-persist"}],"note":{}}""",
    "note must be a string")>]
[<InlineData("""{"corrections":{}}""", "corrections must be an array")>]
[<InlineData("[]", "Request body must be an object")>]
[<InlineData("{", "Request body must be valid JSON")>]
[<Trait("Category", "Integration")>]
let ``HTTP validates the entire correction request before attempting its first write``
    (requestBody: string) (expectedError: string) =
    withServer (fun server ->
        task {
            let! id = prepare server
            do! execute server rejectAnyWriteSql []
            let! before = snapshot server
            let! reply = send server id requestBody 400
            let response = decode reply
            Assert.Equal(expectedError, text "error" response)
            Assert.False(response.TryGetProperty("corrected") |> fst)
            do! assertUnchanged server before
        })

[<Theory>]
[<InlineData("saved_path")>]
[<InlineData("source_path")>]
[<InlineData("sha256")>]
[<Trait("Category", "Integration")>]
let ``HTTP rejected identity corrections neither mutate source claims nor append history`` (field: string) =
    withServer (fun server ->
        task {
            let! id = prepare server
            let! before = snapshot server
            let! reply = send server id (body [ field, "must-not-persist" ]) 400
            Assert.Equal($"Field '{field}' is not correctable", text "error" (decode reply))
            do! assertUnchanged server before
        })
