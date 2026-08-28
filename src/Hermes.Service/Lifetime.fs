namespace Hermes.Service

#nowarn "3261"

open System
open System.Threading
open System.Threading.Tasks
open Hermes.Core

/// Startup and shutdown lifetime for the service host.
[<RequireQualifiedAccess>]
module Lifetime =

    /// Exit code used when background work outlived its shutdown budget and the
    /// database connections were deliberately not disposed.
    [<Literal>]
    let AbandonedExitCode = 75

    /// Whether it is safe to release the database connections.
    type Disposition =
        /// Every owned background task has settled. Disposal is safe.
        | Quiesced
        /// Owned background work is still running. The connections must be left
        /// open: disposing them would tear the gate and connection out from
        /// underneath a live worker.
        | Abandoned of stillRunning: int

    /// Schema initialisation must succeed before any background work or API
    /// traffic starts. A broken schema is not something the service can run on,
    /// so the actual error ends startup instead of being discarded.
    let requireSchema (result: Result<unit, string>) : unit =
        match result with
        | Ok () -> ()
        | Error error ->
            failwith
                $"Hermes cannot start: schema initialisation failed: {error}"

    let private isCancellation (error: exn) =
        match error with
        | :? OperationCanceledException -> true
        | :? AggregateException as aggregate ->
            aggregate.InnerExceptions.Count > 0
            && aggregate.InnerExceptions
               |> Seq.forall (fun inner ->
                   inner :? OperationCanceledException)
        | _ -> false

    /// Classifies a settled background task. Cancellation during shutdown is
    /// expected and is never reported as a failure; anything else that stops is
    /// surfaced the same way regardless of which task it was.
    let describeOutcome
        (logger: Algebra.Logger)
        (name: string)
        (cancellationRequested: bool)
        (finished: Task)
        : unit =
        if finished.IsFaulted then
            let fault: exn =
                match finished.Exception with
                | null -> InvalidOperationException "unknown fault"
                | aggregate -> aggregate
            if cancellationRequested && isCancellation fault then
                logger.info $"Background task '{name}' stopped"
            else
                logger.error
                    $"Background task '{name}' faulted and stopped: {fault}"
        elif finished.IsCanceled || cancellationRequested then
            logger.info $"Background task '{name}' stopped"
        else
            logger.warn $"Background task '{name}' exited before shutdown"

    /// Background work is fire-and-forget for the request path, but its terminal
    /// state must still be observed AND its handle retained, so shutdown can
    /// prove it has stopped before the database is released. The returned task
    /// completes only after the work has settled, and never faults: the outcome
    /// is reported, not rethrown.
    let observeBackground
        (logger: Algebra.Logger)
        (name: string)
        (token: CancellationToken)
        (work: Task)
        : Task =
        work.ContinueWith(
            Action<Task>(fun finished ->
                describeOutcome
                    logger name token.IsCancellationRequested finished),
            TaskContinuationOptions.ExecuteSynchronously)

    /// Waits for cancelled background work to settle and reports whether the
    /// database may be released.
    ///
    /// `Quiesced` is returned only when every owned task is genuinely completed,
    /// so a caller can never be told disposal is safe while a worker is still
    /// using the connection. If the budget expires the connections are left to
    /// the process teardown instead, which is strictly safer than disposing the
    /// gate and connection beneath a live worker.
    ///
    /// Both owned tasks are cancellation-aware and the only untokened wait is a
    /// bounded 5s producer start-up delay, so the budget is a backstop against a
    /// wedged external call rather than the expected path. This runs on the
    /// console host's main thread, which has no SynchronizationContext, so the
    /// wait cannot deadlock; and it is the last thing the process does.
    let shutdown
        (logger: Algebra.Logger)
        (budget: TimeSpan)
        (tasks: Task list)
        : Disposition =
        match tasks with
        | [] -> Quiesced
        | pending ->
            try
                Task.WhenAll(pending).Wait(budget) |> ignore
            with error ->
                // Observation tasks do not fault, but never let an unexpected
                // one masquerade as a completed shutdown.
                logger.error
                    $"Background task shutdown reported a fault: {error.Message}"

            match pending |> List.filter (fun task -> not task.IsCompleted) with
            | [] ->
                logger.info "Background tasks stopped; releasing database"
                Quiesced
            | stillRunning ->
                logger.error
                    $"{stillRunning.Length} background task(s) still running after {budget.TotalSeconds:F0}s; leaving database connections open rather than disposing beneath active workers"
                Abandoned stillRunning.Length
