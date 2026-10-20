module Hermes.Tests.WorkflowTerminalRegressionTests

open System
open System.IO
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.CoreRegressionSupport

type private FaultedInput =
    { Worker: Task
      Reader: ChannelReader<Document.T> }

let private capture (worker: Task) : Task<Result<unit, exn>> =
    task {
        try
            do! worker
            return Ok ()
        with error -> return Error error
    }

let private assertCause (expected: IOException) (observed: Task<Result<unit, exn>>) () : Task<unit> =
    task {
        let! outcome = observed.WaitAsync(timeout)
        Assert.True(Result.isError outcome, "Faulted input must not report a successful worker")
        outcome |> Result.defaultWith (fun actual -> Assert.Same(expected, actual))
    }

let private startFaultedInput (db: Algebra.Database) (observation: Probe)
    (failure: IOException) (buffered: bool) (ct: CancellationToken) : FaultedInput =
    let input = Channel.CreateUnbounded<Document.T>()
    let document: Document.T = Map.ofList [ "extracted_at", "already" :> obj ]
    if buffered then Assert.True(input.Writer.TryWrite document)
    input.Writer.Complete(failure)
    let stage =
        workflowStage (fun _ ->
            Task.FromException<Document.T>(InvalidOperationException("Unexpected processing")))
    let worker =
        Task.Run(Func<Task>(fun () ->
            Workflow.runStage stage db observation.Logger input.Reader None ct :> Task))
    { Worker = worker; Reader = input.Reader }

let private assertTerminalLog (observation: Probe) (message: string) =
    Assert.Equal<string list>(
        [ $"ERROR Stage 'regression' loop error: {message}" ],
        observation.Events
        |> Seq.filter (fun entry -> entry.StartsWith("ERROR ", StringComparison.Ordinal))
        |> Seq.toList)
    Assert.True(saw observation "consumer stopped")

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("Category", "Integration")>]
let ``Faulted input drains its buffer then terminates once with the original cause`` buffered =
    task {
        use fixture = fixture ()
        use cancellation = new CancellationTokenSource()
        let failure = IOException("deliberate input failure")
        let observation = probe "loop error:"
        let input = startFaultedInput fixture.Db observation failure buffered cancellation.Token
        let observed = capture input.Worker
        do! finishNaturally cancellation.Cancel [ observed :> Task ] (assertCause failure observed)
        Assert.False(cancellation.IsCancellationRequested)
        Assert.True(input.Worker.IsFaulted)
        Assert.Equal(0, input.Reader.Count)
        assertTerminalLog observation failure.Message
    }
