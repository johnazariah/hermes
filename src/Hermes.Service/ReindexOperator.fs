namespace Hermes.Service

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Hermes.Core

/// Local operator capabilities deliberately exclude providers, watchers and HTTP.
[<RequireQualifiedAccess>]
module ReindexOperator =

    type Context =
        { Files: Algebra.FileSystem
          Environment: Algebra.Environment
          Logger: Algebra.Logger
          OpenArchive: ReindexCommand.Mode -> string -> Algebra.Database }

    let private existingArchive (files: Algebra.FileSystem) archive =
        if not (files.directoryExists archive) then
            Error $"Configured archive directory does not exist: {archive}"
        else
            Database.canonicalArchivePath archive "db.sqlite"
            |> Result.bind (fun path ->
                SavedPathSchema.validateFile archive path.FullPath
                |> Result.bind (fun () ->
                    if files.fileExists path.FullPath then Ok archive
                    else Error $"Archive database does not exist: {path.FullPath}"))

    let private configuredArchive (config: Domain.HermesConfig) =
        if String.IsNullOrWhiteSpace config.ArchiveDir then
            Error "Configured archive directory must not be empty"
        else Ok(Path.GetFullPath config.ArchiveDir)

    let private loadArchive (context: Context) (request: ReindexCommand.Request) =
        task {
            let path =
                request.ConfigPath
                |> Option.defaultWith (fun () ->
                    Path.Combine(Config.configDir context.Environment, "config.yaml"))
            let! result = Config.loadForOperator context.Files context.Environment path
            return result |> Result.bind configuredArchive |> Result.bind (existingArchive context.Files)
        }

    let private prepare (db: Algebra.Database) mode =
        task {
            let! version = db.schemaVersion ()
            if version < 15 || version > Database.CurrentSchemaVersion then
                invalidOp $"Unsupported archive schema {version}; expected 15 through {Database.CurrentSchemaVersion}"
            match mode with
            | ReindexCommand.Mode.DryRun -> return version
            | ReindexCommand.Mode.Apply ->
                let! initialized = db.initSchema ()
                initialized |> Result.defaultWith invalidOp
                let! current = db.schemaVersion ()
                if current <> Database.CurrentSchemaVersion then
                    invalidOp "Index schema initialization did not publish the expected schema version"
                return current
        }

    let private inspect (context: Context) (db: Algebra.Database) archive documentId =
        task {
            let! result =
                FileContentReindex.inspect db context.Files context.Logger archive [ documentId ]
            return result |> Result.map (fun report ->
                if List.contains documentId report.Indexable then ReindexOutput.Status.WouldIndex
                else ReindexOutput.Status.WouldClearContentIndex)
        }

    let private apply (context: Context) (db: Algebra.Database) archive documentId =
        task {
            let! result =
                FileContentReindex.rebuild db context.Files context.Logger archive [ documentId ]
            return result |> Result.map (fun report ->
                if List.contains documentId report.Indexed then ReindexOutput.Status.Indexed
                else ReindexOutput.Status.MetadataOnly)
        }

    let private one (context: Context) (db: Algebra.Database) archive mode documentId =
        let makeOutcome status error =
            let outcome: ReindexOutput.Outcome =
                { DocumentId = documentId; Status = status; Error = error }
            outcome
        task {
            let operation =
                match mode with
                | ReindexCommand.Mode.DryRun -> inspect
                | ReindexCommand.Mode.Apply -> apply
            let! result = operation context db archive documentId
            return
                result
                |> Result.map (fun status -> makeOutcome status None)
                |> Result.defaultWith (fun error ->
                    makeOutcome ReindexOutput.Status.Failed (Some error))
        }

    let private collect context db archive mode collected documentId =
        task {
            let! outcome = one context db archive mode documentId
            return outcome :: collected
        }

    let private runOpened
        (context: Context) (request: ReindexCommand.Request) archive (db: Algebra.Database) =
        task {
            let! version = prepare db request.Mode
            let! reversed =
                request.DocumentIds
                |> Prelude.foldTask (collect context db archive request.Mode) []
            return ReindexOutput.complete request archive version (List.rev reversed)
        }

    let private protect
        (request: ReindexCommand.Request) (archive: string option)
        (work: unit -> Task<ReindexOutput.Execution>) =
        task {
            try return! work ()
            with
            | :? IOException as error -> return ReindexOutput.setupFailure request archive error.Message
            | :? UnauthorizedAccessException as error -> return ReindexOutput.setupFailure request archive error.Message
            | :? InvalidOperationException as error -> return ReindexOutput.setupFailure request archive error.Message
            | :? ArgumentException as error -> return ReindexOutput.setupFailure request archive error.Message
            | :? NotSupportedException as error -> return ReindexOutput.setupFailure request archive error.Message
            | :? SqliteException as error -> return ReindexOutput.setupFailure request archive error.Message
        }

    let private finishDatabase (db: Algebra.Database) (execution: ReindexOutput.Execution) =
        let failed (error: exn) =
            ReindexOutput.operationalError execution $"Database disposal failed: {error.Message}"
        try db.dispose (); execution
        with
        | :? IOException as error -> failed error
        | :? InvalidOperationException as error -> failed error
        | :? SqliteException as error -> failed error

    let private runArchive (context: Context) (request: ReindexCommand.Request) (archive: string) =
        task {
            let db: Algebra.Database = context.OpenArchive request.Mode archive
            try
                let! execution = protect request (Some archive) (fun () -> runOpened context request archive db)
                return finishDatabase db execution
            with error ->
                db.dispose ()
                return raise error
        }

    let private runConfigured (context: Context) (request: ReindexCommand.Request) =
        task {
            let! archive = loadArchive context request
            return!
                archive
                |> Result.map (fun root ->
                    protect request (Some root) (fun () -> runArchive context request root))
                |> Result.defaultWith (fun error ->
                    Task.FromResult(ReindexOutput.setupFailure request None error))
        }

    let execute (context: Context) (request: ReindexCommand.Request) =
        request
        |> ReindexCommand.validate
        |> Result.map (fun selected ->
            protect selected None (fun () -> runConfigured context selected))
        |> Result.defaultWith (ReindexOutput.usage >> Task.FromResult)
