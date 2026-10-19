namespace Hermes.Core

open System
open System.Runtime.ExceptionServices
open System.Threading.Tasks
open Microsoft.Data.Sqlite

/// Result-returning transactions over the existing scoped database capability.
[<RequireQualifiedAccess>]
module TransactionExecution =

    let private scope (database: Algebra.Database) : Algebra.TransactionScope =
        { execNonQuery = database.execNonQuery
          execScalar = database.execScalar
          execReader = database.execReader }

    let private execute (database: Algebra.Database) sql =
        task {
            let! _ = database.execNonQuery sql []
            return ()
        }

    let private protect
        (work: unit -> Task<Result<unit, string>>)
        : Task<Result<unit, string>> =
        task {
            try
                return! work ()
            with
            | :? SqliteException as error ->
                return Error $"Transaction failed: {error.Message}"
            | :? InvalidOperationException as error ->
                return Error $"Transaction failed: {error.Message}"
        }

    let private rethrow<'T> (error: exn) : 'T =
        ExceptionDispatchInfo.Capture(error).Throw()
        Unchecked.defaultof<'T>

    let private invoke database name callback =
        task {
            try
                return! callback (scope database)
            with error ->
                do! execute database $"ROLLBACK TO SAVEPOINT {name}"
                do! execute database $"RELEASE SAVEPOINT {name}"
                return rethrow error
        }

    // Every callback failure cleans up its savepoint before compatibility
    // conversion or propagation, including when an outer callback catches it.
    let private apply (database: Algebra.Database) callback =
        task {
            let name = $"hermes_result_{Guid.NewGuid():N}"
            do! execute database $"SAVEPOINT {name}"
            let! result = invoke database name callback
            if Result.isError result then
                do! execute database $"ROLLBACK TO SAVEPOINT {name}"
            do! execute database $"RELEASE SAVEPOINT {name}"
            return result
        }

    let run
        (transaction: Algebra.Transaction)
        (callback: Algebra.TransactionScope -> Task<Result<unit, string>>)
        : Task<Result<unit, string>> =
        protect (fun () ->
            transaction.Run(fun database -> apply database callback))
