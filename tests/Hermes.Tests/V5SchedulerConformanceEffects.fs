module Hermes.Tests.V5SchedulerConformanceEffects

open System.Threading.Tasks
open Hermes.Core

// Delay invocation, not just observation of an already-started operation.
// Await the owned inner task without translating results or cancellation.
let private invoke (yieldEffects: bool) (work: unit -> Task<'T>) : Task<'T> =
    if yieldEffects then
        task {
            do! Task.Yield()
            return! work ()
        }
    else work ()

let private scope (yieldEffects: bool)
    (source: Algebra.TransactionScope) : Algebra.TransactionScope =
    { execNonQuery = fun sql parameters ->
          invoke yieldEffects (fun () -> source.execNonQuery sql parameters)
      execScalar = fun sql parameters ->
          invoke yieldEffects (fun () -> source.execScalar sql parameters)
      execReader = fun sql parameters ->
          invoke yieldEffects (fun () -> source.execReader sql parameters) }

// Each callback receives its ORIGINAL interpreter-owned scope, decorated.
// The original transaction implementation still owns commit and rollback.
let rec database (yieldEffects: bool) (source: Algebra.Database) : Algebra.Database =
    { source with
        execNonQuery = fun sql parameters ->
            invoke yieldEffects (fun () -> source.execNonQuery sql parameters)
        execScalar = fun sql parameters ->
            invoke yieldEffects (fun () -> source.execScalar sql parameters)
        execReader = fun sql parameters ->
            invoke yieldEffects (fun () -> source.execReader sql parameters)
        initSchema = fun () -> invoke yieldEffects source.initSchema
        tableExists = fun name -> invoke yieldEffects (fun () -> source.tableExists name)
        schemaVersion = fun () -> invoke yieldEffects source.schemaVersion
        transaction = transaction yieldEffects source.transaction
        inTransaction = fun callback ->
            invoke yieldEffects (fun () ->
                source.inTransaction (fun scoped ->
                    invoke yieldEffects (fun () -> callback (scope yieldEffects scoped)))) }

and private transaction (yieldEffects: bool)
    (source: Algebra.Transaction) : Algebra.Transaction =
    { new Algebra.Transaction with
        member _.Run<'T>(work: Algebra.Database -> Task<'T>) : Task<'T> =
            invoke yieldEffects (fun () ->
                source.Run(fun scoped ->
                    invoke yieldEffects (fun () -> work (database yieldEffects scoped)))) }

let files (yieldEffects: bool) (source: Algebra.FileSystem) : Algebra.FileSystem =
    { source with
        readAllText = fun path ->
            invoke yieldEffects (fun () -> source.readAllText path)
        readAllBytes = fun path ->
            invoke yieldEffects (fun () -> source.readAllBytes path)
        writeAllText = fun path text ->
            invoke yieldEffects (fun () -> source.writeAllText path text)
        writeAllBytes = fun path bytes ->
            invoke yieldEffects (fun () -> source.writeAllBytes path bytes) }
