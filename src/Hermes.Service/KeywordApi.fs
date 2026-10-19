namespace Hermes.Service

open System
open System.Text.Json
open Microsoft.AspNetCore.Http
open Hermes.Core

/// Read-only keyword transport adapters; no providers or archive reads.
[<RequireQualifiedAccess>]
module internal KeywordApi =

    let private json value =
        Results.Json(value, JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))

    let private optional (value: string) =
        if String.IsNullOrWhiteSpace value then None else Some value

    let private limit (value: string) =
        value
        |> Int32.TryParse
        |> function
            | true, number -> Math.Clamp(number, 1, 100)
            | _ -> 50

    let private filter (context: HttpContext) =
        { Search.defaultFilter (context.Request.Query["q"].ToString()) with
            Category = context.Request.Query["category"].ToString() |> optional
            Sender = context.Request.Query["sender"].ToString() |> optional
            DateFrom = context.Request.Query["dateFrom"].ToString() |> optional
            DateTo = context.Request.Query["dateTo"].ToString() |> optional
            Account = context.Request.Query["account"].ToString() |> optional
            SourceType = context.Request.Query["sourceType"].ToString() |> optional
            Limit = context.Request.Query["limit"].ToString() |> limit }

    let private resultDto (hit: Search.SearchResult) =
        {| id = hit.DocumentId
           path = hit.SavedPath
           originalName = hit.OriginalName |> Option.defaultValue ""
           category = hit.Category
           sender = hit.Sender |> Option.toObj
           subject = hit.Subject |> Option.toObj
           emailDate = hit.EmailDate |> Option.toObj
           vendor = hit.ExtractedVendor |> Option.toObj
           amount = hit.ExtractedAmount |> Option.toNullable
           score = hit.RelevanceScore
           snippet = hit.Snippet |> Option.toObj
           resultType = hit.ResultType |}

    let search db context =
        task {
            let! hits = Search.executeUnified db (filter context)
            return hits |> List.map resultDto |> json
        }

    let private documentDto (hit: Search.SearchResult) (detail: DocumentBrowser.DocumentDetail) =
        let summary = detail.Summary
        {| id = summary.Id
           originalName = summary.OriginalName
           category = summary.Category
           extractedDate = summary.ExtractedDate |> Option.toObj
           extractedAmount = summary.ExtractedAmount |> Option.toNullable
           sender = summary.Sender |> Option.toObj
           vendor = summary.Vendor |> Option.toObj
           sourceType = summary.SourceType |> Option.toObj
           account = summary.Account |> Option.toObj
           sourcePath = summary.SourcePath |> Option.toObj
           classificationTier = summary.ClassificationTier |> Option.toObj
           classificationConfidence = summary.ClassificationConfidence |> Option.toNullable
           snippet = hit.Snippet |> Option.toObj
           score = hit.RelevanceScore; savedPath = hit.SavedPath; resultType = hit.ResultType |}

    let private appendDocument db collected (hit: Search.SearchResult) =
        task {
            let! detail = DocumentBrowser.getDocumentDetail db hit.DocumentId
            return
                detail
                |> Option.map (fun value -> documentDto hit value :: collected)
                |> Option.defaultValue collected
        }

    type private Page =
        { Offset: int
          Count: int }

    let private pageNumber name minimum (raw: string) =
        match Int32.TryParse raw with
        | true, value when value >= minimum -> Ok value
        | _ -> Error $"{name} must be an integer greater than or equal to {minimum}"

    let private page offset count =
        pageNumber "offset" 0 offset
        |> Result.bind (fun skip ->
            pageNumber "limit" 1 count
            |> Result.map (fun take ->
                let selected: Page = { Offset = skip; Count = take }
                selected))

    let private rejectPage error : System.Threading.Tasks.Task<IResult> =
        System.Threading.Tasks.Task.FromResult(Results.BadRequest({| error = error |}))

    let private renderDocuments db hits =
        task {
            let! results = hits |> Prelude.foldTask (appendDocument db) []
            return results |> List.rev |> json
        }

    let private documentsPage db category query (selected: Page) =
        task {
            let criteria =
                { Search.defaultFilter query with
                    Category = optional category; Limit = selected.Count }
            let! result = Search.executePage db criteria selected.Offset
            return!
                result
                |> Result.map (renderDocuments db)
                |> Result.defaultWith rejectPage
        }

    /// React's existing documents?q= route retains its document-summary shape.
    let documents db category query offset requestedLimit =
        page offset requestedLimit
        |> Result.map (documentsPage db category query)
        |> Result.defaultWith rejectPage
