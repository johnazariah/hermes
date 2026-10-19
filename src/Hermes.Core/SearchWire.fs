namespace Hermes.Core

open System.Text.Json
open System.Text.Json.Nodes

[<RequireQualifiedAccess>]
module SearchWire =

    let resultDto (hit: Search.SearchResult) =
        {| id = hit.DocumentId; path = hit.SavedPath
           originalName = hit.OriginalName |> Option.defaultValue ""
           category = hit.Category
           sender = hit.Sender |> Option.toObj; subject = hit.Subject |> Option.toObj
           emailDate = hit.EmailDate |> Option.toObj
           vendor = hit.ExtractedVendor |> Option.toObj
           amount = hit.ExtractedAmount |> Option.toNullable
           score = hit.RelevanceScore; snippet = hit.Snippet |> Option.toObj
           resultType = hit.ResultType |}

    let failureDto failure =
        {| status = SearchContract.failureStatus failure
           error = SearchContract.failureMessage failure |}

    let failureNode mode failure : JsonNode =
        let result = JsonObject()
        result["mode"] <- JsonValue.Create(SearchContract.modeName mode)
        result["status"] <- JsonValue.Create(SearchContract.failureStatus failure)
        result["error"] <- JsonValue.Create(SearchContract.failureMessage failure)
        result

    let successNode (success: SearchContract.Success) : JsonNode =
        let result = JsonObject()
        result["mode"] <- JsonValue.Create(SearchContract.modeName success.Mode)
        result["results"] <- JsonSerializer.SerializeToNode(success.Results |> List.map resultDto)
        result["count"] <- JsonValue.Create(success.Results.Length)
        success.Coverage |> Option.iter (fun coverage ->
            result["coverage"] <- JsonSerializer.SerializeToNode(
                {| indexedDocuments = coverage.IndexedDocuments
                   eligibleDocuments = coverage.EligibleDocuments |}))
        result
