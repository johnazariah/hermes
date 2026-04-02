namespace Hermes.Core

open System
open System.Text.RegularExpressions
open YamlDotNet.Serialization
open YamlDotNet.Serialization.NamingConventions

/// Rules engine for document classification.
/// Loads rules from YAML, applies cascade: domain → filename → subject → default.
[<RequireQualifiedAccess>]
module Rules =

    // ─── YAML DTO types ──────────────────────────────────────────────

    [<CLIMutable>]
    type MatchDto =
        { [<YamlMember(Alias = "sender_domain")>]
          SenderDomain: string
          [<YamlMember(Alias = "filename")>]
          Filename: string
          [<YamlMember(Alias = "subject")>]
          Subject: string }

    [<CLIMutable>]
    type RuleDto =
        { [<YamlMember(Alias = "name")>]
          Name: string
          [<YamlMember(Alias = "match")>]
          Match: MatchDto
          [<YamlMember(Alias = "category")>]
          Category: string }

    [<CLIMutable>]
    type RulesFileDto =
        { [<YamlMember(Alias = "rules")>]
          Rules: RuleDto array
          [<YamlMember(Alias = "default_category")>]
          DefaultCategory: string
          [<YamlMember(Alias = "content_rules")>]
          ContentRules: ContentRuleDto array }

    and [<CLIMutable>] ContentRuleDto =
        { [<YamlMember(Alias = "name")>]
          Name: string
          [<YamlMember(Alias = "match")>]
          Match: ContentMatchDto
          [<YamlMember(Alias = "category")>]
          Category: string
          [<YamlMember(Alias = "confidence")>]
          Confidence: float }

    and [<CLIMutable>] ContentMatchDto =
        { [<YamlMember(Alias = "content_any")>]
          ContentAny: string array
          [<YamlMember(Alias = "content_all")>]
          ContentAll: string array
          [<YamlMember(Alias = "has_table")>]
          HasTable: bool
          [<YamlMember(Alias = "table_headers_any")>]
          TableHeadersAny: string array
          [<YamlMember(Alias = "table_headers_all")>]
          TableHeadersAll: string array
          [<YamlMember(Alias = "has_amount")>]
          HasAmount: bool
          [<YamlMember(Alias = "has_date")>]
          HasDate: bool }

    // ─── Internal compiled rule representation ───────────────────────

    type CompiledRule =
        { Name: string
          Category: string
          Kind: CompiledRuleKind }

    and CompiledRuleKind =
        | DomainMatch of domain: string
        | FilenameMatch of pattern: Regex
        | SubjectMatch of pattern: Regex

    // ─── Helpers ─────────────────────────────────────────────────────

    let private orDefault (fallback: string) (value: string | null) =
        match value with
        | null -> fallback
        | v -> v

    let private isNullOrEmpty (s: string | null) =
        match s with
        | null -> true
        | v -> String.IsNullOrWhiteSpace(v)

    let private tryCompileRegex (pattern: string) =
        try
            Some(Regex(pattern, RegexOptions.IgnoreCase ||| RegexOptions.Compiled))
        with _ ->
            None

    let private extractDomain (email: string) =
        match email.IndexOf('@') with
        | -1 -> email.ToLowerInvariant()
        | idx -> email.Substring(idx + 1).ToLowerInvariant()

    // ─── Compilation ─────────────────────────────────────────────────

    let private compileRule (dto: RuleDto) : CompiledRule list =
        let name = dto.Name |> orDefault "unnamed"
        let category = dto.Category |> orDefault "unsorted"

        if isNull (box dto.Match) then
            []
        else
            let matchDto = dto.Match
            let mutable rules = []

            if not (isNullOrEmpty matchDto.SenderDomain) then
                rules <-
                    { Name = name
                      Category = category
                      Kind = DomainMatch(matchDto.SenderDomain.ToLowerInvariant()) }
                    :: rules

            if not (isNullOrEmpty matchDto.Filename) then
                match tryCompileRegex matchDto.Filename with
                | Some regex ->
                    rules <-
                        { Name = name
                          Category = category
                          Kind = FilenameMatch regex }
                        :: rules
                | None -> ()

            if not (isNullOrEmpty matchDto.Subject) then
                match tryCompileRegex matchDto.Subject with
                | Some regex ->
                    rules <-
                        { Name = name
                          Category = category
                          Kind = SubjectMatch regex }
                        :: rules
                | None -> ()

            rules |> List.rev

    // ─── YAML parsing ────────────────────────────────────────────────

    let private deserializer =
        DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()

    let parseRulesYaml (yaml: string) : Result<CompiledRule list * string, string> =
        try
            if String.IsNullOrWhiteSpace(yaml) then
                Ok([], "unsorted")
            else
                let dto = deserializer.Deserialize<RulesFileDto>(yaml)

                if isNull (box dto) then
                    Ok([], "unsorted")
                else
                    let defaultCat = dto.DefaultCategory |> orDefault "unsorted"

                    let rules =
                        if isNull (box dto.Rules) || dto.Rules.Length = 0 then
                            []
                        else
                            dto.Rules |> Array.toList |> List.collect compileRule

                    Ok(rules, defaultCat)
        with ex ->
            Error $"Failed to parse rules: {ex.Message}"

    /// Compile a content rule DTO into a Domain.ContentRule.
    let private compileContentMatch (dto: ContentMatchDto) : Domain.ContentMatch list =
        [ if not (isNull (box dto.ContentAny)) && dto.ContentAny.Length > 0 then
              yield Domain.ContentAny (dto.ContentAny |> Array.toList)
          if not (isNull (box dto.ContentAll)) && dto.ContentAll.Length > 0 then
              yield Domain.ContentAll (dto.ContentAll |> Array.toList)
          if dto.HasTable then yield Domain.HasTable
          if not (isNull (box dto.TableHeadersAny)) && dto.TableHeadersAny.Length > 0 then
              yield Domain.TableHeadersAny (dto.TableHeadersAny |> Array.toList)
          if not (isNull (box dto.TableHeadersAll)) && dto.TableHeadersAll.Length > 0 then
              yield Domain.TableHeadersAll (dto.TableHeadersAll |> Array.toList)
          if dto.HasAmount then yield Domain.HasAmount
          if dto.HasDate then yield Domain.HasDate ]

    /// Parse content rules from the same rules.yaml file.
    let parseContentRules (yaml: string) : Domain.ContentRule list =
        try
            if String.IsNullOrWhiteSpace(yaml) then []
            else
                let dto = deserializer.Deserialize<RulesFileDto>(yaml)
                if isNull (box dto) || isNull (box dto.ContentRules) || dto.ContentRules.Length = 0 then []
                else
                    dto.ContentRules
                    |> Array.toList
                    |> List.map (fun cr ->
                        { Domain.ContentRule.Name = cr.Name |> orDefault "unnamed"
                          Conditions = compileContentMatch cr.Match
                          Category = cr.Category |> orDefault "unsorted"
                          Confidence = if cr.Confidence > 0.0 then cr.Confidence else 0.5 })
        with _ -> []

    // ─── Classification cascade ──────────────────────────────────────

    /// Separate compiled rules by kind for cascade ordering.
    let private partitionRules (rules: CompiledRule list) =
        let domains = rules |> List.filter (fun r -> match r.Kind with DomainMatch _ -> true | _ -> false)
        let filenames = rules |> List.filter (fun r -> match r.Kind with FilenameMatch _ -> true | _ -> false)
        let subjects = rules |> List.filter (fun r -> match r.Kind with SubjectMatch _ -> true | _ -> false)
        domains, filenames, subjects

    let classifyWithRules
        (rules: CompiledRule list)
        (defaultCategory: string)
        (sidecar: Domain.SidecarMetadata option)
        (filename: string)
        : Domain.ClassificationResult =

        let domains, filenames, subjects = partitionRules rules

        // Phase 1: Domain rules — match sender domain from sidecar
        let senderDomain =
            sidecar
            |> Option.bind (fun s -> s.Sender)
            |> Option.map extractDomain

        let domainMatch =
            match senderDomain with
            | Some sd ->
                domains
                |> List.tryFind (fun r ->
                    match r.Kind with
                    | DomainMatch d -> sd.Contains(d)
                    | _ -> false)
            | None -> None

        match domainMatch with
        | Some r ->
            let domain = match r.Kind with DomainMatch d -> d | _ -> ""

            { Category = r.Category
              MatchedRule = Domain.DomainRule(r.Name, domain) }
        | None ->

        // Phase 2: Filename rules
        let fnMatch =
            filenames
            |> List.tryFind (fun r ->
                match r.Kind with
                | FilenameMatch regex -> regex.IsMatch(filename)
                | _ -> false)

        match fnMatch with
        | Some r ->
            let pat = match r.Kind with FilenameMatch rx -> rx.ToString() | _ -> ""

            { Category = r.Category
              MatchedRule = Domain.FilenameRule(r.Name, pat) }
        | None ->

        // Phase 3: Subject rules — match subject from sidecar
        let subject =
            sidecar
            |> Option.bind (fun s -> s.Subject)
            |> Option.defaultValue ""

        let subjMatch =
            if String.IsNullOrWhiteSpace(subject) then
                None
            else
                subjects
                |> List.tryFind (fun r ->
                    match r.Kind with
                    | SubjectMatch regex -> regex.IsMatch(subject)
                    | _ -> false)

        match subjMatch with
        | Some r ->
            let pat = match r.Kind with SubjectMatch rx -> rx.ToString() | _ -> ""

            { Category = r.Category
              MatchedRule = Domain.SubjectRule(r.Name, pat) }
        | None ->

        // Phase 4: Default
        { Category = defaultCategory
          MatchedRule = Domain.DefaultRule }

    // ─── RulesEngine algebra creation ────────────────────────────────

    /// Create a RulesEngine algebra from a FileSystem algebra and rules file path.
    let fromFile (fs: Algebra.FileSystem) (logger: Algebra.Logger) (rulesPath: string) : Algebra.RulesEngine =
        let mutable currentRules: CompiledRule list = []
        let mutable defaultCategory = "unsorted"

        let loadRules () =
            task {
                if not (fs.fileExists rulesPath) then
                    logger.warn $"Rules file not found: {rulesPath}, using defaults"
                    currentRules <- []
                    defaultCategory <- "unsorted"
                    return Ok()
                else
                    try
                        let! yaml = fs.readAllText rulesPath

                        match parseRulesYaml yaml with
                        | Ok(rules, defCat) ->
                            currentRules <- rules
                            defaultCategory <- defCat
                            logger.info $"Loaded {rules.Length} classification rules from {rulesPath}"
                            return Ok()
                        | Error e ->
                            logger.error $"Failed to parse rules: {e}"
                            return Error e
                    with ex ->
                        let msg = $"Failed to load rules: {ex.Message}"
                        logger.error msg
                        return Error msg
            }

        // Initial load (synchronous bootstrap)
        loadRules () |> Async.AwaitTask |> Async.RunSynchronously |> ignore

        { classify =
            fun sidecar filename ->
                classifyWithRules currentRules defaultCategory sidecar filename
          reload = loadRules }
