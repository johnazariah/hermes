namespace Hermes.Service

open System
open System.Net
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives

/// Local listener/Host/origin checks run before endpoints and static assets.
[<RequireQualifiedAccess>]
module LocalBoundary =

    let private localHost (host: string) =
        match host.Trim([| '['; ']' |]).ToLowerInvariant() with
        | "localhost" | "127.0.0.1" | "::1" -> true
        | _ -> false

    let canonicalOrigin (value: string) =
        let parsed =
            match Uri.TryCreate(value, UriKind.Absolute) with
            | true, candidate -> Option.ofObj candidate
            | _ -> None
        match parsed with
        | Some uri when
            (uri.Scheme = "http" || uri.Scheme = "https")
            && localHost uri.Host && uri.UserInfo = ""
            && uri.AbsolutePath = "/" && uri.Query = "" && uri.Fragment = "" ->
            Ok(uri.GetLeftPart(UriPartial.Authority))
        | _ -> Error "Allowed origins must be absolute loopback HTTP(S) origins without paths or credentials"

    let private appendOrigin state value =
        state |> Result.bind (fun origins ->
            canonicalOrigin value |> Result.map (fun origin -> Set.add origin origins))

    let configuredOrigins (setting: string) =
        setting.Split(';', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
        |> Array.fold appendOrigin (Ok Set.empty)
        |> Result.map Set.toList

    let private serviceOrigins port =
        [ 21741; 21742; port ] |> List.distinct
        |> List.collect (fun value ->
            [ $"http://localhost:{value}"; $"http://127.0.0.1:{value}"; $"http://[::1]:{value}" ])
        |> Set.ofList

    let private localConnection (context: HttpContext) =
        match context.Connection.RemoteIpAddress with
        | null -> false
        | address ->
            let ip = if address.IsIPv4MappedToIPv6 then address.MapToIPv4() else address
            IPAddress.IsLoopback ip

    let private validHost (context: HttpContext) =
        let defaultPort = if context.Request.IsHttps then 443 else 80
        localHost context.Request.Host.Host
        && context.Request.Host.Port.GetValueOrDefault(defaultPort) = context.Connection.LocalPort

    let private origin extraOrigins (context: HttpContext) =
        let values = context.Request.Headers["Origin"]
        if values.Count = 0 then Ok None
        elif values.Count <> 1 then Error ()
        else
            let allowed = serviceOrigins context.Connection.LocalPort |> Set.union (Set.ofList extraOrigins)
            let supplied = values.ToString()
            if Set.contains supplied allowed then Ok(Some supplied) else Error ()

    let private header (context: HttpContext) name (value: string) =
        context.Response.Headers[name] <- StringValues(value)

    let private cors (context: HttpContext) supplied =
        supplied |> Option.iter (fun value ->
            header context "Access-Control-Allow-Origin" value
            header context "Vary" "Origin"
            header context "Access-Control-Expose-Headers" "X-Hermes-Search-Mode, X-Hermes-Vector-Coverage")

    let private methods = Set.ofList [ "GET"; "POST"; "PUT"; "PATCH"; "DELETE"; "OPTIONS"; "HEAD" ]
    let private headers =
        Set.ofList [ "content-type"; "authorization"; "accept"; "mcp-protocol-version"
                     "mcp-session-id"; "last-event-id" ]

    let private validPreflight supplied (context: HttpContext) =
        let requestedHeaders =
            context.Request.Headers["Access-Control-Request-Headers"].ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
        Option.isSome supplied
        && Set.contains (context.Request.Headers["Access-Control-Request-Method"].ToString()) methods
        && (requestedHeaders |> Array.forall (fun value -> Set.contains (value.ToLowerInvariant()) headers))

    let private preflight supplied (context: HttpContext) =
        if validPreflight supplied context then
            cors context supplied
            header context "Access-Control-Allow-Methods" (methods |> String.concat ", ")
            header context "Access-Control-Allow-Headers" (headers |> String.concat ", ")
            context.Response.StatusCode <- StatusCodes.Status204NoContent
        else context.Response.StatusCode <- StatusCodes.Status403Forbidden

    let private dispatch extraOrigins (context: HttpContext) (next: RequestDelegate) : Task =
        task {
            if not (localConnection context && validHost context) then
                context.Response.StatusCode <- StatusCodes.Status403Forbidden
            else
                match origin extraOrigins context with
                | Error () -> context.Response.StatusCode <- StatusCodes.Status403Forbidden
                | Ok supplied ->
                    if context.Request.Method = "OPTIONS"
                       && context.Request.Headers.ContainsKey("Access-Control-Request-Method") then
                        preflight supplied context
                    else
                        cors context supplied
                        do! next.Invoke(context)
        }

    let attach (app: WebApplication) extraOrigins =
        app.Use(Func<HttpContext, RequestDelegate, Task>(dispatch extraOrigins)) |> ignore
