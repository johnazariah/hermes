namespace Hermes.Core

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite

/// Connection serialization and transaction-scoped SQLite execution.
[<RequireQualifiedAccess>]
module DatabaseExecution =

    let private command
        (connection: SqliteConnection)
        (transaction: SqliteTransaction option)
        (sql: string)
        (parameters: (string * obj) list) =
        let result = connection.CreateCommand()
        result.CommandText <- sql
        transaction |> Option.iter (fun value -> result.Transaction <- value)
        parameters
        |> List.iter (fun (name, value) ->
            result.Parameters.AddWithValue(name, value) |> ignore)
        result

    let private nonQuery connection transaction sql parameters : Task<int> =
        task {
            use query = command connection transaction sql parameters
            return! query.ExecuteNonQueryAsync()
        }

    let private scalar connection transaction sql parameters : Task<obj | null> =
        task {
            use query = command connection transaction sql parameters
            return! query.ExecuteScalarAsync()
        }

    let private row (reader: SqliteDataReader) =
        Array.init reader.FieldCount (fun index ->
            reader.GetName(index), reader.GetValue(index))
        |> Map.ofArray

    let rec private readRows
        (reader: SqliteDataReader)
        (collected: Map<string, obj> list) =
        task {
            let! available = reader.ReadAsync()
            if available then
                return! readRows reader (row reader :: collected)
            else
                return List.rev collected
        }

    let private reader connection transaction sql parameters : Task<Map<string, obj> list> =
        task {
            use query = command connection transaction sql parameters
            use! rows = query.ExecuteReaderAsync()
            return! readRows rows []
        }

    let private locked (gate: SemaphoreSlim) work =
        task {
            do! gate.WaitAsync()
            try
                return! work ()
            finally
                gate.Release() |> ignore
        }

    let private exists connection transaction (name: string) =
        task {
            let! count =
                scalar connection transaction
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name"
                    [ "@name", name :> obj ]
            return unbox<int64> count > 0L
        }

    let private version connection transaction =
        task {
            let! present = exists connection transaction "schema_version"
            if not present then return 0
            else
                let! value =
                    scalar connection transaction
                        "SELECT COALESCE(MAX(version), 0) FROM schema_version" []
                return int (unbox<int64> value)
        }

    let rec private scoped connection transaction (database: Algebra.Database) =
        { database with
            execNonQuery = nonQuery connection (Some transaction)
            execScalar = scalar connection (Some transaction)
            execReader = reader connection (Some transaction)
            tableExists = exists connection (Some transaction)
            schemaVersion = fun () -> version connection (Some transaction)
            inTransaction = fun callback ->
                TransactionExecution.run
                    (scoped connection transaction database).transaction callback
            initSchema = fun () ->
                Task.FromResult(Error "Schema initialization is not allowed inside a transaction")
            dispose = fun () -> invalidOp "A transaction-scoped database cannot be disposed"
            transaction =
                { new Algebra.Transaction with
                    member _.Run<'T>(work: Algebra.Database -> Task<'T>) =
                        work (scoped connection transaction database) } }

    let private transact (connection: SqliteConnection) database work =
        task {
            use transaction = connection.BeginTransaction(deferred = false)
            let! result = work (scoped connection transaction database)
            transaction.Commit()
            return result
        }

    let rec private session connection gate initialize : Algebra.Database =
        { execNonQuery = fun sql parameters ->
              locked gate (fun () -> nonQuery connection None sql parameters)
          execScalar = fun sql parameters ->
              locked gate (fun () -> scalar connection None sql parameters)
          execReader = fun sql parameters ->
              locked gate (fun () -> reader connection None sql parameters)
          initSchema = fun () -> locked gate initialize
          tableExists = fun name -> locked gate (fun () -> exists connection None name)
          schemaVersion = fun () -> locked gate (fun () -> version connection None)
          inTransaction = fun callback ->
              TransactionExecution.run
                  (session connection gate initialize).transaction callback
          dispose = fun () -> connection.Dispose(); gate.Dispose()
          transaction =
              { new Algebra.Transaction with
                  member _.Run<'T>(work: Algebra.Database -> Task<'T>) =
                      locked gate (fun () ->
                          transact connection (session connection gate initialize) work) } }

    let create connection initialize =
        session connection (new SemaphoreSlim(1, 1)) initialize
