namespace Hermes.Service

open System.Text.Json
open Hermes.Core

/// JSON contains identities and outcomes, never source bodies or extracted text.
[<RequireQualifiedAccess>]
module ReindexOutput =

    [<RequireQualifiedAccess>]
    type Status =
        | WouldIndex
        | WouldClearContentIndex
        | Indexed
        | MetadataOnly
        | Failed
        | NotAttempted

    module Status =
        let toString = function
            | Status.WouldIndex -> "would_index"
            | Status.WouldClearContentIndex -> "would_clear_content_index"
            | Status.Indexed -> "indexed"
            | Status.MetadataOnly -> "metadata_only"
            | Status.Failed -> "failed"
            | Status.NotAttempted -> "not_attempted"

    type Outcome =
        { DocumentId: int64
          Status: Status
          Error: string option }

    type Execution =
        { Mode: ReindexCommand.Mode option
          ArchiveDirectory: string option
          SchemaVersion: int option
          Outcomes: Outcome list
          Error: string option
          ExitCode: int }

    let usage error =
        { Mode = None; ArchiveDirectory = None; SchemaVersion = None
          Outcomes = []; Error = Some error; ExitCode = 2 }

    let setupFailure (request: ReindexCommand.Request) archive error =
        { Mode = Some request.Mode
          ArchiveDirectory = archive
          SchemaVersion = None
          Outcomes =
            request.DocumentIds
            |> List.map (fun id ->
                { DocumentId = id; Status = Status.NotAttempted; Error = Some error })
          Error = Some error
          ExitCode = 1 }

    let complete (request: ReindexCommand.Request) archive version outcomes =
        { Mode = Some request.Mode
          ArchiveDirectory = Some archive
          SchemaVersion = Some version
          Outcomes = outcomes
          Error = None
          ExitCode =
            if outcomes |> List.exists (fun outcome -> outcome.Status = Status.Failed) then 1
            else 0 }

    let operationalError execution message =
        { execution with Error = Some message; ExitCode = 1 }

    let private outcomeDto outcome =
        {| documentId = outcome.DocumentId
           status = Status.toString outcome.Status
           error = outcome.Error |> Option.toObj |}

    let serialize execution =
        {| command = ReindexCommand.Name
           mode = execution.Mode |> Option.map ReindexCommand.Mode.toString |> Option.toObj
           archiveDirectory = execution.ArchiveDirectory |> Option.toObj
           schemaVersion = execution.SchemaVersion |> Option.toNullable
           indexSchemaVersion = Database.CurrentSchemaVersion
           outcomes = execution.Outcomes |> List.map outcomeDto
           error = execution.Error |> Option.toObj
           exitCode = execution.ExitCode |}
        |> JsonSerializer.Serialize
