module Hermes.Tests.PipelineV5BatchRegressionTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.CoreRegressionSupport

let private processStage (db: Algebra.Database) (_: Algebra.Logger)
    (execution: PipelineV5.StageExecution) : Task<PipelineV5.StageOutcome> =
    task {
        let! _ =
            db.execNonQuery
                "INSERT INTO extraction(document_id,method) VALUES (@doc,'batch-regression')"
                [ "@doc", Database.boxVal execution.DocumentId ]
        return PipelineV5.Completed
    }

let private stage () : PipelineV5.StageDefinition =
    { TestHelpers.standardV5Stages.Head with Process = processStage }

let private run (definition: PipelineV5.StageDefinition) (fixture: Fixture)
    (gpu: PipelineV5.GpuScheduler) (maxTime: TimeSpan) (ct: CancellationToken) : Task<int> =
    PipelineV5.processStage definition fixture.Db TestHelpers.silentLogger gpu 10 maxTime ct

let private assertCounts (fixture: Fixture) (completions: int64) (attempts: int64) (outputs: int64) =
    task {
        let! actualCompletions = count fixture.Db "stage_completions"
        let! actualAttempts = count fixture.Db "pipeline_stage_attempts"
        let! actualOutputs = count fixture.Db "extraction"
        Assert.Equal(completions, actualCompletions)
        Assert.Equal(attempts, actualAttempts)
        Assert.Equal(outputs, actualOutputs)
    }

let private assertGpuAvailable (gpu: PipelineV5.GpuScheduler) =
    task {
        use cancellation = new CancellationTokenSource(timeout)
        use! lease = gpu.Acquire "following-model" cancellation.Token
        Assert.Equal<string option>(Some "following-model", gpu.CurrentModel ())
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``V5 an already expired batch leaves ready documents unclaimed and releases the GPU`` () =
    task {
        use fixture = fixture ()
        use cancellation = new CancellationTokenSource(timeout)
        do! seed fixture 1L "received"
        let definition = { stage () with GpuModel = Some "fake-7b" }
        do! PipelineV5.initSchema fixture.Db [ definition ]
        let gpu = PipelineV5.createGpuScheduler TestHelpers.silentLogger
        let! processed = run definition fixture gpu (TimeSpan.FromTicks(-1L)) cancellation.Token
        Assert.Equal(0, processed)
        do! assertCounts fixture 0L 0L 0L
        let! unchanged = row fixture.Db 1L
        Assert.Equal("received", Document.stage unchanged)
        assertOriginal fixture unchanged
        do! assertGpuAvailable gpu
    }

let private cancelAfterWrite (cancellation: CancellationTokenSource) (db: Algebra.Database)
    (logger: Algebra.Logger) (execution: PipelineV5.StageExecution) : Task<PipelineV5.StageOutcome> =
    task {
        let! outcome = processStage db logger execution
        cancellation.Cancel()
        return outcome
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``V5 cancellation between ready documents commits the active document and leaves its sibling ready`` () =
    task {
        use fixture = fixture ()
        use cancellation = new CancellationTokenSource(timeout)
        do! seedMany fixture [ 1L, "received"; 2L, "received" ]
        let definition =
            { stage () with Process = cancelAfterWrite cancellation; GpuModel = Some "fake-7b" }
        do! PipelineV5.initSchema fixture.Db [ definition ]
        let gpu = PipelineV5.createGpuScheduler TestHelpers.silentLogger
        let! processed = run definition fixture gpu timeout cancellation.Token
        Assert.Equal(1, processed)
        do! assertCounts fixture 1L 0L 1L
        let! first = row fixture.Db 1L
        let! second = row fixture.Db 2L
        Assert.Equal("extracted", Document.stage first)
        Assert.Equal("received", Document.stage second)
        [ first; second ] |> List.iter (assertOriginal fixture)
        do! assertGpuAvailable gpu
    }

let private gateFailure = "gate could not read its prerequisite"

let private assertDeadLetter (fixture: Fixture) (dismissed: int64) : Task<unit> =
    task {
        let! rows = fixture.Db.execReader "SELECT * FROM dead_letters" []
        let deadLetter = Assert.Single<Map<string, obj>> rows
        Assert.Equal<string option>(Some gateFailure, Document.decode<string> "error" deadLetter)
        Assert.Equal<int64 option>(Some 1L, Document.decode<int64> "retry_count" deadLetter)
        Assert.Equal<int64 option>(Some dismissed, Document.decode<int64> "dismissed" deadLetter)
        Assert.Equal<string option>(Some "doc-1.txt", Document.decode<string> "original_name" deadLetter)
    }

let private exerciseGateFailures (fixture: Fixture) (definition: PipelineV5.StageDefinition)
    (gpu: PipelineV5.GpuScheduler) (ct: CancellationToken) : Task<unit> =
    task {
        let! first = run definition fixture gpu timeout ct
        let! retry = run definition fixture gpu timeout ct
        Assert.Equal(0, first)
        Assert.Equal(0, retry)
        do! assertCounts fixture 0L 0L 0L
        do! assertDeadLetter fixture 0L
    }

let private recoverWithClosedGate (fixture: Fixture) (definition: PipelineV5.StageDefinition)
    (gpu: PipelineV5.GpuScheduler) (ct: CancellationToken) : Task<unit> =
    task {
        let skipped : PipelineV5.StageDefinition =
            { definition with Gate = Some (fun _ _ -> Task.FromResult false) }
        let! recovered = run skipped fixture gpu timeout ct
        let! repeated = run skipped fixture gpu timeout ct
        Assert.Equal(0, recovered)
        Assert.Equal(0, repeated)
        do! assertCounts fixture 1L 0L 0L
        do! assertDeadLetter fixture 1L
        let! document = row fixture.Db 1L
        Assert.Equal("extracted", Document.stage document)
        assertOriginal fixture document
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``V5 gate faults retry one active dead letter and a later skip completes without invoking processing`` () =
    task {
        use fixture = fixture ()
        use cancellation = new CancellationTokenSource(timeout)
        do! seed fixture 1L "received"
        let definition =
            { stage () with
                Gate = Some (fun _ _ -> Task.FromException<bool>(IOException gateFailure)) }
        do! PipelineV5.initSchema fixture.Db [ definition ]
        let gpu = PipelineV5.createGpuScheduler TestHelpers.silentLogger
        do! exerciseGateFailures fixture definition gpu cancellation.Token
        do! recoverWithClosedGate fixture definition gpu cancellation.Token
    }

let private holdAfterWrite
    (entered: TaskCompletionSource<unit>) (release: TaskCompletionSource<unit>) (ct: CancellationToken)
    (db: Algebra.Database) (logger: Algebra.Logger) (execution: PipelineV5.StageExecution) =
    task {
        let! outcome = processStage db logger execution
        entered.TrySetResult(()) |> ignore
        do! release.Task.WaitAsync(ct)
        return outcome
    }

let private verifyCompetingBatch (fixture: Fixture) (definition: PipelineV5.StageDefinition)
    (gpu: PipelineV5.GpuScheduler) (entered: TaskCompletionSource<unit>) (ct: CancellationToken) () =
    task {
        do! entered.Task.WaitAsync(ct)
        let! competing = run definition fixture gpu timeout ct
        Assert.Equal(0, competing)
        do! assertCounts fixture 0L 1L 1L
        let! document = row fixture.Db 1L
        Assert.Equal("received", Document.stage document)
        assertOriginal fixture document
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``V5 a competing batch cannot process a document whose first attempt still owns its lease`` () =
    task {
        use fixture = fixture ()
        use cancellation = new CancellationTokenSource(timeout)
        let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        do! seed fixture 1L "received"
        let definition = { stage () with Process = holdAfterWrite entered release cancellation.Token }
        do! PipelineV5.initSchema fixture.Db [ definition ]
        let gpu = PipelineV5.createGpuScheduler TestHelpers.silentLogger
        let first = run definition fixture gpu timeout cancellation.Token
        do! settle (fun () -> release.TrySetResult(()) |> ignore) [ first :> Task ]
                (verifyCompetingBatch fixture definition gpu entered cancellation.Token)
        let! processed = first
        Assert.Equal(1, processed)
        do! assertCounts fixture 1L 0L 1L
        let! failures = count fixture.Db "dead_letters"
        Assert.Equal(0L, failures)
    }
