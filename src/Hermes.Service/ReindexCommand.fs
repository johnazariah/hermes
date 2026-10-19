namespace Hermes.Service

open System
open System.Globalization
open Hermes.Core

/// Explicit bounded local command; no positional IDs, ranges, or archive scans.
[<RequireQualifiedAccess>]
module ReindexCommand =

    let [<Literal>] Name = "reindex-files"
    let [<Literal>] ApplyFlag = "--apply"
    let [<Literal>] DryRunFlag = "--dry-run"
    let [<Literal>] DocumentFlag = "--document-id"
    let [<Literal>] ConfigFlag = "--config"

    [<RequireQualifiedAccess>]
    type Mode = DryRun | Apply

    module Mode =
        let toString = function
            | Mode.DryRun -> "dry_run"
            | Mode.Apply -> "apply"

    type Request =
        { Mode: Mode
          DocumentIds: int64 list
          ConfigPath: string option }

    type Invocation =
        | Service
        | Reindex of Result<Request, string>

    type private Parsed =
        { Mode: Mode option
          ReverseIds: int64 list
          ConfigPath: string option }

    let validate (request: Request) =
        if List.isEmpty request.DocumentIds || request.DocumentIds.Length > FileContentReindex.MaxDocuments then
            Error $"Select between 1 and {FileContentReindex.MaxDocuments} explicit document IDs"
        elif request.DocumentIds |> List.exists (fun value -> value <= 0L) then
            Error "Document IDs must be positive"
        elif List.distinct request.DocumentIds <> request.DocumentIds then
            Error "Document IDs must be distinct"
        elif request.ConfigPath |> Option.exists String.IsNullOrWhiteSpace then
            Error "Configuration path must not be empty"
        else Ok request

    let private documentId (value: string) =
        match Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, id when id > 0L -> Ok id
        | _ -> Error $"Invalid positive document ID: '{value}'"

    let private setMode mode state =
        state.Mode
        |> Option.map (fun _ -> Error "Specify --apply or --dry-run at most once")
        |> Option.defaultWith (fun () -> Ok { state with Mode = Some mode })

    let private addId state id =
        if state.ReverseIds.Length >= FileContentReindex.MaxDocuments then
            Error $"At most {FileContentReindex.MaxDocuments} document IDs are allowed"
        else Ok { state with ReverseIds = id :: state.ReverseIds }

    let private setConfig state path =
        if String.IsNullOrWhiteSpace path || path.StartsWith("--", StringComparison.Ordinal) then
            Error "--config requires a file path"
        else
            state.ConfigPath
            |> Option.map (fun _ -> Error "Specify --config at most once")
            |> Option.defaultWith (fun () -> Ok { state with ConfigPath = Some path })

    let private finish state =
        { Mode = state.Mode |> Option.defaultValue Mode.DryRun
          DocumentIds = List.rev state.ReverseIds
          ConfigPath = state.ConfigPath }
        |> validate

    let rec private walk state = function
        | [] -> finish state
        | ApplyFlag :: rest ->
            setMode Mode.Apply state |> Result.bind (fun next -> walk next rest)
        | DryRunFlag :: rest ->
            setMode Mode.DryRun state |> Result.bind (fun next -> walk next rest)
        | DocumentFlag :: value :: rest ->
            documentId value |> Result.bind (addId state) |> Result.bind (fun next -> walk next rest)
        | ConfigFlag :: path :: rest ->
            setConfig state path |> Result.bind (fun next -> walk next rest)
        | argument :: _ -> Error $"Unknown or incomplete reindex argument: '{argument}'"

    let parse arguments =
        walk { Mode = None; ReverseIds = []; ConfigPath = None } arguments

    let route (arguments: string array) =
        let tokens = Array.toList arguments
        let reserved = [ Name; ApplyFlag; DryRunFlag; DocumentFlag; ConfigFlag ]
        match tokens with
        | Name :: rest -> Reindex(parse rest)
        | head :: _ when head.StartsWith("reindex", StringComparison.OrdinalIgnoreCase) ->
            Reindex(Error $"Use '{Name}' as the first argument")
        | _ when tokens |> List.exists (fun value -> List.contains value reserved) ->
            Reindex(Error $"Reindex options require '{Name}' as the first argument")
        | _ -> Service
