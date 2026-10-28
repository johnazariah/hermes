namespace Hermes.Service

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.StaticFiles
open Microsoft.Extensions.Primitives

/// Validated deployed React files, with no source-directory or SPA fallback.
[<RequireQualifiedAccess>]
module CanonicalUi =

    type private Asset =
        { FullPath: string
          ContentType: string }

    type Bundle = private { Files: Map<string, Asset> }

    let private indexName = "index.html"
    let private manifestName = "asset-manifest.json"
    let private contentTypes = FileExtensionContentTypeProvider()
    let private routes =
        [ "/"; "/" + indexName; "/documents"; "/search"; "/settings"
          "/onboarding"; "/pipeline"; "/chat" ]

    let private property name (root: JsonElement) =
        if root.ValueKind <> JsonValueKind.Object then Error "Manifest must be a JSON object"
        else
            match root.EnumerateObject() |> Seq.filter (fun value -> value.Name = name) |> Seq.toList with
            | [ value ] -> Ok value.Value
            | _ -> Error $"Manifest requires exactly one '{name}' property"

    let private readName (value: JsonElement) =
        let pattern = @"\A(?:assets/(?:[A-Za-z0-9_-]+/)*[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)+|(?:favicon|icons)\.svg)\z"
        if value.ValueKind <> JsonValueKind.String then Error "Manifest assets must be strings"
        else
            value.GetString()
            |> Option.ofObj
            |> Option.filter (fun name -> Regex.IsMatch(name, pattern, RegexOptions.CultureInvariant))
            |> Option.map Ok
            |> Option.defaultValue (Error "Invalid asset path in manifest")

    let private readNames (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Array then Error "Manifest assets must be an array"
        else
            value.EnumerateArray()
            |> Seq.fold (fun state item ->
                state |> Result.bind (fun names ->
                    readName item |> Result.map (fun name -> name :: names))) (Ok [])
            |> Result.map List.rev

    let private uniqueNames (names: string list) =
        let distinct = names |> List.map (fun name -> name.ToLowerInvariant()) |> Set.ofList
        if Set.count distinct = List.length names then Ok names
        else Error "Manifest contains duplicate asset paths"

    let private parseManifest (text: string) =
        try
            use document = JsonDocument.Parse(text)
            let root = document.RootElement
            property "schemaVersion" root
            |> Result.bind (fun version ->
                if version.ValueKind = JsonValueKind.Number && version.GetRawText() = "1" then
                    property "assets" root |> Result.bind readNames
                else Error "Manifest schemaVersion must be 1")
            |> Result.bind uniqueNames
        with :? JsonException as error ->
            Error $"Manifest is not valid JSON: {error.Message}"

    let private contentType name =
        let failure = Error $"Unsupported asset content type: {name}"
        match contentTypes.TryGetContentType(name) with
        | true, value -> Ok value
        | _ -> failure

    let private file root (name: string) =
        let path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar))
        if not (File.Exists(path)) then Error $"Missing required file: {name}"
        else
            use stream = File.OpenRead(path)
            if stream.Length = 0L then Error $"Required file is empty: {name}"
            else
                contentType name
                |> Result.map (fun mime -> { FullPath = path; ContentType = mime })

    let private attributes text =
        let pattern = """(?:\A|\s+)(?<name>[^\s=/>]+)(?:\s*=\s*(?:"(?<value>[^"]*)"|'(?<value>[^']*)'|(?<value>[^\s"'=<>`]+)))?"""
        Regex.Matches(text, pattern, RegexOptions.CultureInvariant)
        |> Seq.map (fun matched ->
            let value =
                System.Net.WebUtility.HtmlDecode(matched.Groups.["value"].Value)
                |> Option.ofObj |> Option.defaultValue ""
            matched.Groups.["name"].Value.ToLowerInvariant(), value)
        |> Seq.toList

    let private values name attributes =
        attributes |> List.choose (fun (key, value) -> if key = name then Some value else None)

    let private moduleScripts html =
        let pattern = """<script(?=\s|>)(?<attributes>(?:[^"'<>]|"[^"]*"|'[^']*')*)>"""
        Regex.Matches(html, pattern, RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)
        |> Seq.map (fun matched -> attributes matched.Groups.["attributes"].Value)
        |> Seq.filter (fun entry ->
            values "type" entry
            |> List.exists (fun value -> value.Trim().Equals("module", StringComparison.OrdinalIgnoreCase)))
        |> Seq.toList

    let private validEntry (names: string list) (entry: (string * string) list) =
        List.length (values "type" entry) = 1
        && match values "src" entry with
           | [ path ] ->
               path.StartsWith("/", StringComparison.Ordinal)
               && path.EndsWith(".js", StringComparison.Ordinal)
               && List.contains (path.Substring(1)) names
           | _ -> false

    let private validateIndex path names =
        let html = File.ReadAllText(path)
        let entries = moduleScripts html
        let hasRoot = Regex.IsMatch(html, """<[^>]+\bid\s*=\s*["']root["']""")
        if html.TrimStart().StartsWith("<!doctype html>", StringComparison.OrdinalIgnoreCase)
           && html.Contains("</html>", StringComparison.OrdinalIgnoreCase)
           && hasRoot && not (List.isEmpty entries)
           && (entries |> List.forall (validEntry names)) then Ok ()
        else Error "Invalid index.html: expected an HTML React root and manifest-listed module script"

    let private assetFiles root names =
        names
        |> List.fold (fun state name ->
            state |> Result.bind (fun files ->
                file root name |> Result.map (fun asset -> ("/" + name, asset) :: files))) (Ok [])

    let private load root =
        file root indexName
        |> Result.bind (fun index ->
            file root manifestName
            |> Result.bind (fun manifest ->
                parseManifest (File.ReadAllText(manifest.FullPath))
                |> Result.bind (fun names ->
                    validateIndex index.FullPath names
                    |> Result.bind (fun () ->
                        assetFiles root names
                        |> Result.map (fun assets ->
                            let pages = routes |> List.map (fun route -> route, index)
                            { Files = (("/" + manifestName, manifest) :: assets) @ pages |> Map.ofList })))))

    let private diagnostic root detail =
        $"Canonical React UI unavailable at '{root}': {detail}. "
        + "Run npm ci --prefix src/Hermes.Web and npm run build --prefix src/Hermes.Web "
        + "before dotnet build/publish, then deploy the complete output wwwroot directory."

    let private fromDirectory baseDirectory =
        let root = Path.Combine(baseDirectory, "wwwroot")
        let loaded =
            try
                if Directory.Exists(root) then load root
                else Error "Missing required wwwroot directory"
            with
            | :? IOException as error -> Error error.Message
            | :? UnauthorizedAccessException as error -> Error error.Message
        loaded |> Result.mapError (diagnostic root)

    /// The caller supplies AppContext.BaseDirectory; relative bases are rejected.
    let preflight (baseDirectory: string | null) =
        baseDirectory
        |> Option.ofObj
        |> Option.filter (fun path ->
            not (String.IsNullOrWhiteSpace(path)) && Path.IsPathFullyQualified(path))
        |> Option.map fromDirectory
        |> Option.defaultValue (
            Error (diagnostic "<invalid base directory>" "An absolute application base directory is required"))

    let private serve asset (context: HttpContext) =
        context.Response.Headers["Cache-Control"] <- StringValues("no-cache")
        if File.Exists(asset.FullPath) then
            Results.File(asset.FullPath, contentType = asset.ContentType).ExecuteAsync(context)
        else Results.NotFound().ExecuteAsync(context)

    let private dispatch (bundle: Bundle) (context: HttpContext) : Task =
        let asset =
            if HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) then
                context.Request.Path.Value
                |> Option.ofObj
                |> Option.bind (fun path -> Map.tryFind path bundle.Files)
            else None
        asset
        |> Option.map (fun value -> serve value context)
        |> Option.defaultWith (fun () -> Results.NotFound().ExecuteAsync(context))

    /// Register only the validated inventory, using the same dispatch in every host.
    /// Exact-path dispatch also rejects routing's case/trailing-separator aliases.
    let attach (app: WebApplication) (bundle: Bundle) : unit =
        bundle.Files
        |> Map.iter (fun path _ ->
            app.MapMethods(
                path, [| HttpMethods.Get; HttpMethods.Head |],
                RequestDelegate(dispatch bundle))
            |> HttpAccessPolicy.classify HttpAccessClassification.AnonymousArchiveRead
            |> ignore)
