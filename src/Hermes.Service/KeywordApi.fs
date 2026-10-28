namespace Hermes.Service

open System
open System.Text.Json
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open Hermes.Core
open Hermes.Core.LocalAccess

/// Read-only keyword transport adapters; no providers or archive reads.
[<RequireQualifiedAccess>]
module internal KeywordApi =

    let private json value =
        Results.Json(value, JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase))

    let private optional (value: string) =
        if String.IsNullOrWhiteSpace value then None else Some value

    let private requested
        (context: HttpContext) : Result<SearchContract.Request, HttpAccessFailure> =
        HttpAccessPolicy.preparsed context
        |> Result.bind (fun prepared ->
            match prepared.Request, prepared.Policy with
            | InteractiveRequest.Search request, ModelWorkPolicy.ArchiveOnly
                when request.Mode = SearchContract.Keyword -> Ok request
            | InteractiveRequest.Search request, ModelWorkPolicy.InvokeModel ModelOperation.Search
                when request.Mode <> SearchContract.Keyword -> Ok request
            | _ -> Error(HttpAccessFailure.Domain AccessError.AccessUnavailable))

    let private reject failure =
        Results.Json(SearchWire.failureDto failure, statusCode = SearchContract.httpStatus failure)

    let private searchHeaders (context: HttpContext) (request: SearchContract.Request) coverage =
        context.Response.Headers["X-Hermes-Search-Mode"] <-
            StringValues(SearchContract.modeName request.Mode)
        let value =
            coverage |> Option.map (fun (value: SearchContract.Coverage) ->
                $"{value.IndexedDocuments}/{value.EligibleDocuments}")
            |> Option.defaultValue "not_applicable"
        context.Response.Headers["X-Hermes-Vector-Coverage"] <- StringValues(value)

    let private execute db encoder context (request: SearchContract.Request) =
        task {
            searchHeaders context request None
            let! result = SearchQuery.execute db encoder request
            return result |> Result.map (fun success ->
                searchHeaders context request success.Coverage
                success.Results |> List.map SearchWire.resultDto |> json)
                |> Result.defaultWith reject
        }

    let private rejectPreparation (_: HttpAccessFailure) : System.Threading.Tasks.Task<IResult> =
        Results.Json(
            {| error = "access_unavailable"; message = "Prepared search request is unavailable." |},
            statusCode = StatusCodes.Status503ServiceUnavailable)
        |> System.Threading.Tasks.Task.FromResult

    let searchWithEncoder
        (db: Algebra.Database) (encoder: Algebra.EmbeddingClient option)
        (context: HttpContext) : System.Threading.Tasks.Task<IResult> =
        requested context |> Result.map (execute db encoder context)
        |> Result.defaultWith rejectPreparation

    let search db context = searchWithEncoder db None context

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
