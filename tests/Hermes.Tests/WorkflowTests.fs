module Hermes.Tests.WorkflowTests

#nowarn "3261"

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open Hermes.Core

/// Helper: create a logger that captures messages.
let private testLogger () =
    let msgs = ResizeArray<string>()
    let logger : Algebra.Logger =
        { info = fun s -> msgs.Add($"INFO: {s}")
          warn = fun s -> msgs.Add($"WARN: {s}")
          error = fun s -> msgs.Add($"ERROR: {s}")
          debug = fun s -> msgs.Add($"DEBUG: {s}") }
    logger, msgs

/// Helper: a minimal in-memory DB that only supports persist.
let private fakeDb () : Algebra.Database =
    { execNonQuery = fun _ _ -> task { return 1 }
      execScalar = fun _ _ -> task { return box 0L }
      execReader = fun _ _ -> task { return [] }
      initSchema = fun () -> task { return Ok () }
      tableExists = fun _ -> task { return true }
      schemaVersion = fun () -> task { return 1 }
      dispose = fun () -> () }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Workflow_runStage_processes_document_and_forwards`` () =
    task {
        let logger, _ = testLogger ()
        let db = fakeDb ()

        let stage : Workflow.StageDefinition =
            { Name = "test"
              OutputKey = "processed_at"
              RequiredKeys = [ "input_data" ]
              Process = fun doc ->
                task {
                    return
                        doc
                        |> Document.encode "processed_at" (box "2026-01-01")
                        |> Document.encode "stage" (box "processed")
                }
              ResourceLock = None
              MaxHoldTime = TimeSpan.Zero }

        let inputCh = Channel.CreateUnbounded<Document.T>()
        let outputCh = Channel.CreateUnbounded<Document.T>()

        let doc : Document.T =
            Map.ofList
                [ "id", box 1L
                  "stage", box "received"
                  "input_data", box "hello" ]

        do! inputCh.Writer.WriteAsync(doc)
        inputCh.Writer.Complete()

        use cts = new CancellationTokenSource(TimeSpan.FromSeconds(5.0))
        do! Workflow.runStage stage db logger inputCh.Reader (Some outputCh.Writer) cts.Token

        let! result = outputCh.Reader.ReadAsync()
        Assert.Equal(Some "2026-01-01", result |> Document.decode<string> "processed_at")
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Workflow_runStage_passthrough_when_already_done`` () =
    task {
        let logger, _ = testLogger ()
        let db = fakeDb ()

        let processCallCount = ref 0

        let stage : Workflow.StageDefinition =
            { Name = "test"
              OutputKey = "processed_at"
              RequiredKeys = [ "input_data" ]
              Process = fun doc ->
                task {
                    Interlocked.Increment(processCallCount) |> ignore
                    return doc
                }
              ResourceLock = None
              MaxHoldTime = TimeSpan.Zero }

        let inputCh = Channel.CreateUnbounded<Document.T>()
        let outputCh = Channel.CreateUnbounded<Document.T>()

        // Document already has the output key
        let doc : Document.T =
            Map.ofList
                [ "id", box 1L
                  "stage", box "processed"
                  "processed_at", box "already done"
                  "input_data", box "hello" ]

        do! inputCh.Writer.WriteAsync(doc)
        inputCh.Writer.Complete()

        use cts = new CancellationTokenSource(TimeSpan.FromSeconds(5.0))
        do! Workflow.runStage stage db logger inputCh.Reader (Some outputCh.Writer) cts.Token

        let! result = outputCh.Reader.ReadAsync()
        Assert.Equal(Some "already done", result |> Document.decode<string> "processed_at")
        Assert.Equal(0, processCallCount.Value)
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Workflow_runStage_passthrough_when_missing_required_keys`` () =
    task {
        let logger, _ = testLogger ()
        let db = fakeDb ()

        let processCallCount = ref 0

        let stage : Workflow.StageDefinition =
            { Name = "test"
              OutputKey = "processed_at"
              RequiredKeys = [ "input_data" ]
              Process = fun doc ->
                task {
                    Interlocked.Increment(processCallCount) |> ignore
                    return doc
                }
              ResourceLock = None
              MaxHoldTime = TimeSpan.Zero }

        let inputCh = Channel.CreateUnbounded<Document.T>()
        let outputCh = Channel.CreateUnbounded<Document.T>()

        // Document is missing "input_data"
        let doc : Document.T =
            Map.ofList [ "id", box 1L; "stage", box "received" ]

        do! inputCh.Writer.WriteAsync(doc)
        inputCh.Writer.Complete()

        use cts = new CancellationTokenSource(TimeSpan.FromSeconds(5.0))
        do! Workflow.runStage stage db logger inputCh.Reader (Some outputCh.Writer) cts.Token

        let! result = outputCh.Reader.ReadAsync()
        Assert.False(Document.hasKey "processed_at" result)
        Assert.Equal(0, processCallCount.Value)
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Workflow_runStage_marks_failed_on_exception`` () =
    task {
        let logger, _ = testLogger ()
        let persistedDocs = ResizeArray<Document.T>()

        let db =
            { fakeDb () with
                execNonQuery = fun _sql ps ->
                    // Capture the stage value from params
                    task { return 1 } }

        let stage : Workflow.StageDefinition =
            { Name = "test"
              OutputKey = "processed_at"
              RequiredKeys = [ "input_data" ]
              Process = fun _ -> failwith "boom"
              ResourceLock = None
              MaxHoldTime = TimeSpan.Zero }

        let inputCh = Channel.CreateUnbounded<Document.T>()
        let outputCh = Channel.CreateUnbounded<Document.T>()

        let doc : Document.T =
            Map.ofList
                [ "id", box 1L
                  "stage", box "received"
                  "input_data", box "hello" ]

        do! inputCh.Writer.WriteAsync(doc)
        inputCh.Writer.Complete()

        use cts = new CancellationTokenSource(TimeSpan.FromSeconds(5.0))
        do! Workflow.runStage stage db logger inputCh.Reader (Some outputCh.Writer) cts.Token

        // Failed docs are NOT forwarded to output
        let mutable discarded = Unchecked.defaultof<Document.T>
        Assert.False(outputCh.Reader.TryRead(&discarded))
    }
