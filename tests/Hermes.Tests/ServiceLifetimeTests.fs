module Hermes.Tests.ServiceLifetimeTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Service

type private CapturedLog =
    { Infos: ResizeArray<string>
      Warns: ResizeArray<string>
      Errors: ResizeArray<string> }

let private capturingLogger () : Algebra.Logger * CapturedLog =
    let captured =
        { Infos = ResizeArray<string>()
          Warns = ResizeArray<string>()
          Errors = ResizeArray<string>() }
    let add (sink: ResizeArray<string>) (message: string) =
        lock captured (fun () -> sink.Add message)
    let logger: Algebra.Logger =
        { info = add captured.Infos
          warn = add captured.Warns
          error = add captured.Errors
          debug = ignore }
    logger, captured

// ─── Startup must not run on a broken schema ─────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Lifetime_RequireSchema_Error_FailsStartupWithTheActualError`` () =
    let failure =
        Assert.Throws<Exception>(
            Action(fun () -> Lifetime.requireSchema (Error "disk is full")))
    Assert.Contains("disk is full", failure.Message)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Lifetime_RequireSchema_Ok_Continues`` () =
    Lifetime.requireSchema (Ok ())

// ─── Background outcomes are surfaced truthfully ─────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Lifetime_ObserveBackground_FaultedWork_IsReportedAsError`` () =
    let logger, captured = capturingLogger ()
    use cts = new CancellationTokenSource()
    let work: Task = Task.FromException(InvalidOperationException "boom")
    let handle = Lifetime.observeBackground logger "faulty" cts.Token work
    Assert.Equal(
        Lifetime.Quiesced,
        Lifetime.shutdown logger (TimeSpan.FromSeconds 5.0) [ handle ])
    Assert.Contains(captured.Errors, fun (m: string) -> m.Contains "faulty")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Lifetime_ObserveBackground_CancellationDuringShutdown_IsNotAFailure`` () =
    let logger, captured = capturingLogger ()
    use cts = new CancellationTokenSource()
    cts.Cancel()
    let work: Task = Task.FromCanceled(cts.Token)
    let handle = Lifetime.observeBackground logger "worker" cts.Token work
    Lifetime.shutdown logger (TimeSpan.FromSeconds 5.0) [ handle ] |> ignore
    Assert.Empty(captured.Errors)
    Assert.Contains(captured.Infos, fun (m: string) -> m.Contains "stopped")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Lifetime_ObserveBackground_ExitBeforeShutdown_IsWarned`` () =
    let logger, captured = capturingLogger ()
    use cts = new CancellationTokenSource()
    let handle =
        Lifetime.observeBackground
            logger "worker" cts.Token Task.CompletedTask
    Lifetime.shutdown logger (TimeSpan.FromSeconds 5.0) [ handle ] |> ignore
    Assert.Contains(
        captured.Warns, fun (m: string) -> m.Contains "exited before shutdown")

// ─── Connections are never released beneath a live worker ────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Lifetime_Shutdown_WaitsForWorkerBeforeReportingQuiesced`` () =
    let logger, _ = capturingLogger ()
    use cts = new CancellationTokenSource()
    let gate =
        TaskCompletionSource<unit>(
            TaskCreationOptions.RunContinuationsAsynchronously)
    let stillUsingDatabase = ref true
    let work =
        task {
            do! gate.Task
            do! Task.Delay(20)
            stillUsingDatabase.Value <- false
        }
        :> Task
    let handle = Lifetime.observeBackground logger "worker" cts.Token work

    cts.Cancel()
    gate.TrySetResult() |> ignore
    let disposition =
        Lifetime.shutdown logger (TimeSpan.FromSeconds 5.0) [ handle ]

    Assert.Equal(Lifetime.Quiesced, disposition)
    Assert.False(
        stillUsingDatabase.Value,
        "Quiesced was reported while the worker was still using the database")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Lifetime_Shutdown_UnresponsiveWorker_AbandonsInsteadOfDisposing`` () =
    let logger, captured = capturingLogger ()
    use cts = new CancellationTokenSource()
    let blocked =
        TaskCompletionSource<unit>(
            TaskCreationOptions.RunContinuationsAsynchronously)
    let handle =
        Lifetime.observeBackground
            logger "stuck" cts.Token (blocked.Task :> Task)

    cts.Cancel()
    let disposition =
        Lifetime.shutdown logger (TimeSpan.FromMilliseconds 100.0) [ handle ]

    // The contract Program relies on: this value must never be Quiesced while
    // the worker is live, because Quiesced is what authorises disposal.
    Assert.Equal(Lifetime.Abandoned 1, disposition)
    Assert.NotEqual(Lifetime.Quiesced, disposition)
    Assert.False(handle.IsCompleted)
    Assert.Contains(
        captured.Errors, fun (m: string) -> m.Contains "still running")

    blocked.TrySetResult() |> ignore

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Lifetime_Shutdown_PartialCompletion_IsNotQuiesced`` () =
    let logger, _ = capturingLogger ()
    use cts = new CancellationTokenSource()
    let blocked =
        TaskCompletionSource<unit>(
            TaskCreationOptions.RunContinuationsAsynchronously)
    let finished =
        Lifetime.observeBackground
            logger "done" cts.Token Task.CompletedTask
    let stuck =
        Lifetime.observeBackground
            logger "stuck" cts.Token (blocked.Task :> Task)

    cts.Cancel()
    let disposition =
        Lifetime.shutdown
            logger (TimeSpan.FromMilliseconds 100.0) [ finished; stuck ]

    Assert.Equal(Lifetime.Abandoned 1, disposition)
    blocked.TrySetResult() |> ignore

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Lifetime_Shutdown_NoBackgroundWork_IsQuiescedSilently`` () =
    let logger, captured = capturingLogger ()
    Assert.Equal(
        Lifetime.Quiesced,
        Lifetime.shutdown logger (TimeSpan.FromSeconds 1.0) [])
    Assert.Empty(captured.Errors)
    Assert.Empty(captured.Warns)
