module Hermes.Cli.Program

open System
open System.IO
open Argu
open Hermes.Core

type InitArgs =
    | [<Hidden>] Placeholder
    interface IArgParserTemplate with
        member _.Usage = ""

type SearchArgs =
    | [<MainCommand>] Query of string
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Query _ -> "search query"

type SyncArgs =
    | [<Hidden>] Placeholder
    interface IArgParserTemplate with
        member _.Usage = ""

type ReconcileArgs =
    | [<AltCommandLine("-n")>] Dry_Run
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Dry_Run -> "show what would be done without making changes"

type SuggestRulesArgs =
    | [<Hidden>] Placeholder
    interface IArgParserTemplate with
        member _.Usage = ""

[<RequireSubcommand>]
type CliArgs =
    | Version
    | [<CliPrefix(CliPrefix.None)>] Init of ParseResults<InitArgs>
    | [<CliPrefix(CliPrefix.None)>] Search of ParseResults<SearchArgs>
    | [<CliPrefix(CliPrefix.None)>] Sync of ParseResults<SyncArgs>
    | [<CliPrefix(CliPrefix.None)>] Reconcile of ParseResults<ReconcileArgs>
    | [<CliPrefix(CliPrefix.None)>] Suggest_Rules of ParseResults<SuggestRulesArgs>
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Version -> "print version and exit"
            | Init _ -> "initialise config, rules, and database"
            | Search _ -> "search documents (not yet implemented)"
            | Sync _ -> "sync email accounts (not yet implemented)"
            | Reconcile _ -> "walk archive, find moved/deleted/new files"
            | Suggest_Rules _ -> "analyse unsorted patterns and suggest rules"

let private version () =
    let asm = System.Reflection.Assembly.GetEntryAssembly()
    match asm with
    | null -> printfn "hermes 0.0.0"; 0
    | a ->
        let ver = a.GetName().Version
        printfn $"hermes {ver}"
        0

let private initCmd () =
    let fs = Interpreters.realFileSystem
    let logger = Logging.configureDefault ()

    logger.info "Initialising Hermes..."

    let configResult = Config.init fs |> Async.AwaitTask |> Async.RunSynchronously

    match configResult with
    | Error e ->
        logger.error $"Config init failed: {e}"
        1
    | Ok created ->
        for path in created do
            logger.info $"Created: {path}"

        let configPath = Path.Combine(Config.configDir (), "config.yaml")
        let loadResult = Config.load fs configPath |> Async.AwaitTask |> Async.RunSynchronously

        match loadResult with
        | Error e ->
            logger.error $"Failed to load config: {e}"
            1
        | Ok config ->
            let archiveResult =
                Database.initArchive fs config.ArchiveDir
                |> Async.AwaitTask
                |> Async.RunSynchronously

            match archiveResult with
            | Error e ->
                logger.error $"Archive init failed: {e}"
                1
            | Ok db ->
                db.dispose ()
                logger.info $"Archive initialised at: {config.ArchiveDir}"
                logger.info "Hermes is ready."
                0

let private loadConfigAndDb () =
    let fs = Interpreters.realFileSystem
    let logger = Logging.configureDefault ()
    let configPath = Path.Combine(Config.configDir (), "config.yaml")
    let loadResult = Config.load fs configPath |> Async.AwaitTask |> Async.RunSynchronously

    match loadResult with
    | Error e ->
        logger.error $"Failed to load config: {e}"
        None
    | Ok config ->
        let dbPath = Path.Combine(config.ArchiveDir, "db.sqlite")

        if not (File.Exists(dbPath)) then
            logger.error "Database not found. Run 'hermes init' first."
            None
        else
            let db = Database.fromPath dbPath
            Some(fs, logger, config, db)

let private reconcileCmd (args: ParseResults<ReconcileArgs>) =
    let dryRun = args.Contains Dry_Run

    match loadConfigAndDb () with
    | None -> 1
    | Some(fs, logger, config, db) ->
        try
            let actions =
                Classifier.reconcile fs db logger config.ArchiveDir dryRun
                |> Async.AwaitTask
                |> Async.RunSynchronously

            if actions.IsEmpty then
                logger.info "Archive is in sync - no discrepancies found."
            else
                for action in actions do
                    match action with
                    | Classifier.NewOnDisk(path, cat) ->
                        let prefix = if dryRun then "[DRY-RUN] " else ""
                        printfn $"{prefix}New file on disk (not in DB): {path} (category: {cat})"
                    | Classifier.MissingFromDisk(path, docId) ->
                        let prefix = if dryRun then "[DRY-RUN] " else ""
                        printfn $"{prefix}Missing from disk: {path} (doc ID: {docId})"
                    | Classifier.MovedOnDisk(path, docId) ->
                        let prefix = if dryRun then "[DRY-RUN] " else ""
                        printfn $"{prefix}Moved on disk: {path} (doc ID: {docId})"

                printfn $"Total discrepancies: {actions.Length}"

            0
        finally
            db.dispose ()

let private suggestRulesCmd () =
    match loadConfigAndDb () with
    | None -> 1
    | Some(fs, logger, config, db) ->
        try
            let suggestions =
                Classifier.suggestRules fs db logger config.ArchiveDir
                |> Async.AwaitTask
                |> Async.RunSynchronously

            if suggestions.IsEmpty then
                printfn "No rule suggestions found."
            else
                printfn "Suggested rules (add to rules.yaml):"
                printfn ""

                for s in suggestions do
                    printfn $"  - name: {s.SuggestedName}"
                    printfn $"    match:"
                    printfn $"      {s.MatchType}: \"{s.Pattern}\""
                    printfn $"    category: {s.Category}"
                    printfn $"    # based on: {s.ExampleFile}"
                    printfn ""

            0
        finally
            db.dispose ()

let private notImplemented (name: string) =
    printfn $"hermes {name}: not yet implemented"
    0

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<CliArgs>(programName = "hermes")

    if argv |> Array.exists (fun a -> a = "--version" || a = "-v") then
        version ()
    else

    try
        let results = parser.ParseCommandLine(inputs = argv, raiseOnUsage = true)

        if results.Contains Version then
            version ()
        elif results.Contains Init then
            initCmd ()
        elif results.Contains Search then
            notImplemented "search"
        elif results.Contains Sync then
            notImplemented "sync"
        elif results.Contains Reconcile then
            reconcileCmd (results.GetResult Reconcile)
        elif results.Contains Suggest_Rules then
            suggestRulesCmd ()
        else
            printfn "%s" (parser.PrintUsage())
            0
    with
    | :? ArguParseException as ex ->
        printfn "%s" ex.Message
        if ex.ErrorCode = ErrorCode.HelpText then 0 else 1
    | ex ->
        eprintfn $"Error: {ex.Message}"
        1