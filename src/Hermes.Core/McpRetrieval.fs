namespace Hermes.Core

open System
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks

[<RequireQualifiedAccess>]
module internal McpRetrieval =

    let private mode (arguments: JsonNode) =
        use document = JsonDocument.Parse(arguments.ToJsonString())
        match document.RootElement.TryGetProperty("mode") with
        | false, _ -> SearchContract.parseMode None
        | true, value when value.ValueKind = JsonValueKind.String ->
            SearchContract.parseMode (value.GetString() |> Option.ofObj)
        | _ -> Error "mode must be a string"

    let private limit (arguments: JsonNode) =
        use document = JsonDocument.Parse(arguments.ToJsonString())
        match document.RootElement.TryGetProperty("limit") with
        | false, _ -> Ok 20
        | true, value when value.ValueKind = JsonValueKind.Number ->
            match value.TryGetInt32() with
            | true, count when count > 0 -> Ok count
            | _ -> Error "limit must be a positive 32-bit integer"
        | _ -> Error "limit must be a positive 32-bit integer"

    let private setters:
        (string * (string option -> Search.SearchFilter -> Search.SearchFilter)) list =
        [ "category", (fun value filter -> { filter with Category = value })
          "sender", (fun value filter -> { filter with Sender = value })
          "dateFrom", (fun value filter -> { filter with DateFrom = value })
          "dateTo", (fun value filter -> { filter with DateTo = value })
          "account", (fun value filter -> { filter with Account = value })
          "sourceType", (fun value filter -> { filter with SourceType = value }) ]

    let private applyFilter arguments state (name, assign) =
        state |> Result.bind (fun filter ->
            McpTools.Args.text arguments name |> Result.map (fun value -> assign value filter))

    let private filter arguments query =
        limit arguments
        |> Result.bind (fun count ->
            setters |> List.fold (applyFilter arguments)
                (Ok { Search.defaultFilter query with Limit = count }))

    let private request arguments =
        McpTools.Args.text arguments "query"
        |> Result.bind (fun query ->
            query |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.map Ok |> Option.defaultValue (Error "query is required"))
        |> Result.bind (fun query ->
            mode arguments |> Result.bind (fun selected ->
                filter arguments query |> Result.map (fun criteria ->
                    let value: SearchContract.Request = { Mode = selected; Filter = criteria }
                    value)))
        |> Result.bind (SearchContract.validate >> Result.mapError SearchContract.failureMessage)

    let private searchRequested db encoder (request: SearchContract.Request) =
        task {
            let! result = SearchQuery.execute db encoder request
            return result |> Result.map SearchWire.successNode
                   |> Result.defaultWith (SearchWire.failureNode request.Mode)
        }

    let private wrapSearch db encoder requested =
        task {
            let! result = searchRequested db encoder requested
            return Ok result
        }

    let search db encoder arguments : Task<Result<JsonNode, McpTools.ToolFailure>> =
        request arguments
        |> Result.mapError McpTools.InvalidArguments
        |> Result.map (wrapSearch db encoder)
        |> Result.defaultWith (Error >> Task.FromResult)

    let private readRequested db fs archiveDir documentId =
        task {
            let! result = PersistedComprehension.read db fs archiveDir documentId
            return Ok(
                result |> Result.map ComprehensionContract.successNode
                |> Result.defaultWith ComprehensionContract.failureNode)
        }

    let comprehension db fs archiveDir arguments : Task<Result<JsonNode, McpTools.ToolFailure>> =
        McpTools.Args.requiredInteger arguments "document_id"
        |> Result.bind (fun id ->
            if id > 0L then Ok id else Error "document_id must be positive")
        |> Result.mapError McpTools.InvalidArguments
        |> Result.map (readRequested db fs archiveDir)
        |> Result.defaultWith (Error >> Task.FromResult)
