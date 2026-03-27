module Hermes.Cli.Program

open System
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

[<RequireSubcommand>]
type CliArgs =
    | Version
    | [<CliPrefix(CliPrefix.None)>] Init of ParseResults<InitArgs>
    | [<CliPrefix(CliPrefix.None)>] Search of ParseResults<SearchArgs>
    | [<CliPrefix(CliPrefix.None)>] Sync of ParseResults<SyncArgs>
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Version -> "print version and exit"
            | Init _ -> "initialise config, rules, and database"
            | Search _ -> "search documents (not yet implemented)"
            | Sync _ -> "sync email accounts (not yet implemented)"

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

    // Init config files
    let configResult = Config.init fs |> Async.AwaitTask |> Async.RunSynchronously

    match configResult with
    | Error e ->
        logger.error $"Config init failed: {e}"
        1
    | Ok created ->
        for path in created do
            logger.info $"Created: {path}"

        // Load config to get archive dir
        let configPath = System.IO.Path.Combine(Config.configDir (), "config.yaml")
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

let private notImplemented (name: string) =
    printfn $"hermes {name}: not yet implemented"
    0

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<CliArgs>(programName = "hermes")

    // Handle --version before Argu parsing (which requires subcommand)
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
