namespace Hermes.Service

open System
open Hermes.Core

[<RequireQualifiedAccess>]
module ServicePolicy =

    type Settings =
        { Port: int
          ExtraOrigins: string list
          Mcp: McpAccess.Policy }

    let private port = function
        | None | Some "" -> Ok 21741
        | Some value ->
            match Int32.TryParse value with
            | true, parsed when parsed > 0 && parsed <= 65535 -> Ok parsed
            | _ -> Error "HERMES_PORT must be between 1 and 65535"

    /// The service and early owner commands select the same environment port.
    let selectPort (value: string | null) : Result<int, string> =
        value |> Option.ofObj |> port

    let private enabled = function
        | None | Some "" | Some "false" -> Ok false
        | Some "true" -> Ok true
        | _ -> Error "HERMES_MCP_MUTATIONS must be true or false"

    /// Environment access is injected; no config files or token values are logged.
    let load (get: string -> string | null) =
        let setting name = get name |> Option.ofObj
        selectPort (get "HERMES_PORT")
        |> Result.bind (fun selectedPort ->
            enabled (setting "HERMES_MCP_MUTATIONS")
            |> Result.bind (fun mutations ->
                McpAccess.configure mutations (setting "HERMES_MCP_TOKEN")
                |> Result.bind (fun policy ->
                    setting "HERMES_ALLOWED_ORIGINS" |> Option.defaultValue ""
                    |> LocalBoundary.configuredOrigins
                    |> Result.map (fun origins ->
                        { Port = selectedPort; ExtraOrigins = origins; Mcp = policy }))))
