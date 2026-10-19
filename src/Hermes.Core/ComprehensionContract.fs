namespace Hermes.Core

open System.Text.Json
open System.Text.Json.Nodes

[<RequireQualifiedAccess>]
module ComprehensionContract =

    type Failure =
        | InvalidRequest of string
        | Missing
        | NotAvailable of string

    type SourceIdentity =
        { DocumentId: int64
          SavedPath: string
          Sha256: string
          SourceType: string
          SourcePath: string option
          Account: string option
          ProviderId: string option
          ThreadId: string option }

    type EffectiveMetadata =
        { Category: string
          Date: string option
          Amount: float option
          Vendor: string option }

    type Snapshot =
        { Source: SourceIdentity
          Generation: int64
          PublicationGeneration: int64
          PublicationSha256: string
          Canonical: JsonElement
          Effective: EffectiveMetadata }

    let failureStatus = function
        | InvalidRequest _ -> "invalid_request"
        | Missing -> "missing"
        | NotAvailable _ -> "not_available"

    let failureMessage = function
        | InvalidRequest message | NotAvailable message -> message
        | Missing -> "document_not_found"

    let httpStatus = function
        | InvalidRequest _ -> 400
        | Missing -> 404
        | NotAvailable _ -> 409

    let failureNode failure : JsonNode =
        let result = JsonObject()
        result["status"] <- JsonValue.Create(failureStatus failure)
        result["error"] <- JsonValue.Create(failureMessage failure)
        result

    let private sourceDto (source: SourceIdentity) =
        {| documentId = source.DocumentId; savedPath = source.SavedPath
           sha256 = source.Sha256; sourceType = source.SourceType
           sourcePath = source.SourcePath |> Option.toObj
           account = source.Account |> Option.toObj
           providerId = source.ProviderId |> Option.toObj
           threadId = source.ThreadId |> Option.toObj |}

    let successNode (snapshot: Snapshot) : JsonNode =
        let result = JsonObject()
        result["status"] <- JsonValue.Create("available")
        result["source"] <- JsonSerializer.SerializeToNode(sourceDto snapshot.Source)
        result["generation"] <- JsonValue.Create(snapshot.Generation)
        result["publicationGeneration"] <- JsonValue.Create(snapshot.PublicationGeneration)
        result["publicationSha256"] <- JsonValue.Create(snapshot.PublicationSha256)
        result["comprehension"] <- JsonSerializer.SerializeToNode(snapshot.Canonical)
        result["effectiveMetadata"] <- JsonSerializer.SerializeToNode(
            {| category = snapshot.Effective.Category
               date = snapshot.Effective.Date |> Option.toObj
               amount = snapshot.Effective.Amount |> Option.toNullable
               vendor = snapshot.Effective.Vendor |> Option.toObj |})
        result["enrichment"] <- JsonSerializer.SerializeToNode(
            {| status = "unavailable"; reason = "no_document_scoped_attribution" |})
        result
