module Hermes.Tests.AsyncCapabilityControl

open System
open System.Threading.Tasks
open Xunit
open Hermes.Core

let timeout = TimeSpan.FromSeconds(20.0)

/// Instrumentation only: a single invocation announces entry before its effect.
/// Repeated invocation is an error, rather than silently reusing a released gate.
type Gate(name: string) =
    let entered =
        TaskCompletionSource<Algebra.Database>(
            TaskCreationOptions.RunContinuationsAsynchronously)
    let resumed =
        TaskCompletionSource<Result<unit, exn>>(
            TaskCreationOptions.RunContinuationsAsynchronously)
    let effected =
        TaskCompletionSource<unit>(
            TaskCreationOptions.RunContinuationsAsynchronously)

    member _.WasEntered = entered.Task.IsCompletedSuccessfully
    member _.EffectStarted = effected.Task.IsCompletedSuccessfully

    member _.Await(pending: Task) : Task<Algebra.Database> =
        task {
            let! database = entered.Task.WaitAsync(timeout)
            Assert.False(pending.IsCompleted, $"{name}: top-level work completed")
            Assert.False(resumed.Task.IsCompleted, $"{name}: gate was already released")
            return database
        }

    member _.Invoke<'T>
        (database: Algebra.Database)
        (effect: unit -> Task<'T>) : Task<'T> =
        task {
            if not (entered.TrySetResult(database)) then
                invalidOp $"{name}: capability invoked more than once"
            let! permission = resumed.Task.WaitAsync(timeout)
            permission |> Result.defaultWith raise
            effected.SetResult(())
            return! effect ()
        }

    member _.Release() =
        Assert.True(resumed.TrySetResult(Ok ()), $"{name}: duplicate release")

    member _.Fail(error: exn) =
        Assert.True(resumed.TrySetResult(Error error), $"{name}: duplicate release")

    member _.Stop() =
        entered.TrySetCanceled() |> ignore
        resumed.TrySetCanceled() |> ignore

let private settle (work: unit -> Task<'T>) : Task<Result<'T, exn>> =
    task {
        try
            let! result = work ()
            return Ok result
        with error ->
            return Error error
    }

let private inspect
    (gates: Gate list)
    (verify: Task<'T> -> Task<unit>)
    (pending: Task<'T>) =
    task {
        try
            return! settle (fun () -> verify pending)
        finally
            gates |> List.iter (fun gate -> gate.Stop())
    }

/// Cleanup releases/cancels every controlled wait and joins the original task,
/// including when a checkpoint assertion fails. Neither failure is made success.
let exercise
    (gates: Gate list)
    (start: unit -> Task<'T>)
    (verify: Task<'T> -> Task<unit>) : Task<'T> =
    task {
        let pending = start ()
        let! inspection = inspect gates verify pending
        let! completion = settle (fun () -> pending)
        return
            inspection
            |> Result.bind (fun () -> completion)
            |> Result.defaultWith raise
    }

let release
    (gate: Gate)
    (pending: Task<'T>)
    (verify: Algebra.Database -> Task<unit>) =
    task {
        let! database = gate.Await(pending :> Task)
        do! verify database
        gate.Release()
    }

let drive gates pending verify =
    gates
    |> Prelude.foldTask (fun () gate -> release gate pending verify) ()
