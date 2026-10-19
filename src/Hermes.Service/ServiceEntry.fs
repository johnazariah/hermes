namespace Hermes.Service

open System
open System.IO
open Microsoft.Data.Sqlite
open Hermes.Core

/// The CLI branch runs before any ordinary service startup side effect.
[<RequireQualifiedAccess>]
module ServiceEntry =

    let private openArchive mode archive =
        let access =
            match mode with
            | ReindexCommand.Mode.DryRun -> SqliteOpenMode.ReadOnly
            | ReindexCommand.Mode.Apply -> SqliteOpenMode.ReadWrite
        let settings =
            SqliteConnectionStringBuilder(
                DataSource = Path.Combine(archive, "db.sqlite"),
                Mode = access, Pooling = false)
        let connection = new SqliteConnection(settings.ConnectionString)
        try
            connection.Open()
            match mode with
            | ReindexCommand.Mode.DryRun -> Database.fromReadOnlyConnectionForArchive archive connection
            | ReindexCommand.Mode.Apply -> Database.fromConnectionForArchive archive connection
        with error ->
            connection.Dispose()
            raise error

    let localContext files environment : ReindexOperator.Context =
        let diagnostic (message: string) = Console.Error.WriteLine(message)
        { Files = files; Environment = environment
          Logger = { info = ignore; debug = ignore; warn = diagnostic; error = diagnostic }
          OpenArchive = openArchive }

    let runWith context writeJson startService arguments =
        match ReindexCommand.route arguments with
        | ReindexCommand.Service -> startService arguments
        | ReindexCommand.Reindex parsed ->
            let execution =
                parsed
                |> Result.map (fun request ->
                    (ReindexOperator.execute context request).GetAwaiter().GetResult())
                |> Result.defaultWith ReindexOutput.usage
            execution |> ReindexOutput.serialize |> writeJson
            execution.ExitCode

    let run arguments startService =
        runWith
            (localContext Interpreters.realFileSystem Interpreters.systemEnvironment)
            (fun message -> Console.Out.WriteLine(message))
            startService arguments
