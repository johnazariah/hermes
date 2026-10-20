module Hermes.Tests.V5SchedulerConformanceLifecycleTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.V5SchedulerConformanceSupport

module Suspension = Hermes.Tests.AsyncCapabilityControl
module State = Hermes.Tests.V5AsyncState
module Fixture = Hermes.Tests.CoreRegressionSupport

let private stopAfter (cancellation: CancellationTokenSource)
    (stage: PipelineV5.StageDefinition) : PipelineV5.StageDefinition =
    { stage with
        Process = fun db logger execution ->
            task {
                let! outcome = stage.Process db logger execution
                cancellation.Cancel()
                return outcome
            } }

let private phases (context: Context) (gate: Suspension.Gate)
    (cancellation: CancellationTokenSource) : PipelineV5.StageDefinition list =
    let stage name model = { define context context.Store.Memory.Fs name [] with GpuModel = model }
    [ stage "large" (Some "scheduler-32b") |> stopAfter cancellation
      stage "vector" (Some "scheduler-embed")
      stage "small" (Some "scheduler-7b")
      define context (heldFiles context gate) "cpu" [] ]

let private recoverCycle (context: Context) (before: Snapshot)
    (readyGate: Suspension.Gate) (fileGate: Suspension.Gate) (pending: Task<unit>) : Task<unit> =
    task {
        let! db = readyGate.Await(pending :> Task)
        do! ledger db [] 0L
        do! unchanged context before db
        readyGate.Fail(IOException "controlled readiness failure")
        let! recovered = fileGate.Await(pending :> Task)
        do! ledger recovered [] 1L
        do! unchanged context before recovered
        do! State.count recovered "activity_log WHERE level='error' AND category='pipeline'" 1L
        Assert.True(Fixture.saw context.Observation "Pipeline cycle failed: controlled readiness failure")
        fileGate.Release()
        do! pending.WaitAsync(Suspension.timeout)
    }

let private finishCycle (context: Context) (before: Snapshot) : Task<unit> =
    task {
        do! ledger context.Store.Db [ "cpu:1"; "small:1"; "vector:1"; "large:1" ] 0L
        for name in [ "cpu"; "small"; "vector"; "large" ] do
            do! outputs context.Store.Db name [ 1L ] 0L
        Assert.True(Fixture.saw context.Observation "INFO Pipeline v5 stopped")
        Assert.Equal(Some "scheduler-32b", context.Gpu.CurrentModel())
        do! unchanged context before context.Store.Db
        do! clean context 0L
    }

[<Theory; InlineData(false); InlineData(true); Trait("Category", "Integration")>]
let ``V5 scheduler recovers a yielded readiness fault preserves model priorities and joins shutdown``
    (yieldEffects: bool) : Task<unit> =
    task {
        use context = create yieldEffects
        use cancellation = new CancellationTokenSource(Suspension.timeout)
        let readyGate = Suspension.Gate("cycle readiness query")
        let fileGate = Suspension.Gate("recovered CPU source read")
        let definitions = phases context fileGate cancellation
        let graph = dag definitions
        do! arrange context definitions [ 1L ]
        let! before = capture context context.Store.Db
        let db = heldReady context.Store.Db readyGate graph.Stages["cpu"]
        do! Suspension.exercise [ readyGate; fileGate ] (fun () -> loop context graph db cancellation.Token)
                (cancelAfter cancellation (recoverCycle context before readyGate fileGate))
        Assert.Equal<string list>([ "cpu"; "small"; "vector"; "large" ],
            context.Calls |> Seq.map (fun call -> call.Stage) |> Seq.toList)
        do! finishCycle context before
    }

let private inspectContender (context: Context) (lease: Document.T list)
    (gate: Suspension.Gate) (pending: Task<unit>) : Task<unit> =
    task {
        let! db = gate.Await(pending :> Task)
        do! State.expectRows db "SELECT * FROM pipeline_stage_attempts" lease
        do! ledger db [] 1L
        Assert.Empty(context.Calls |> Seq.filter (fun call -> call.DocumentId = 2L))
    }

let private contend (context: Context) (graph: PipelineV5.Dag)
    (fileGate: Suspension.Gate) (ownerCancellation: CancellationTokenSource)
    (pending: Task<int>) : Task<unit> =
    task {
        let! db = fileGate.Await(pending :> Task)
        let! lease = db.execReader "SELECT * FROM pipeline_stage_attempts" []
        use cancellation = new CancellationTokenSource(Suspension.timeout)
        let gate = Suspension.Gate("contender readiness after live-lease recovery")
        let contenderDb = heldReady db gate graph.Stages["extract"]
        do! Suspension.exercise [ gate ] (fun () -> loop context graph contenderDb cancellation.Token)
                (cancelAfter cancellation (fun worker -> task {
                    do! inspectContender context lease gate worker
                    cancellation.Cancel()
                    gate.Release()
                }))
        Assert.False(pending.IsCompleted)
        ownerCancellation.Cancel()
        fileGate.Release()
    }

[<Theory; InlineData(false); InlineData(true); Trait("Category", "Integration")>]
let ``V5 scheduler recovery protects the live token and cancellation finishes admitted work only``
    (yieldEffects: bool) : Task<unit> =
    task {
        use context = create yieldEffects
        use cancellation = new CancellationTokenSource(Suspension.timeout)
        let gate = Suspension.Gate("admitted source read")
        let definitions = [ define context (heldFiles context gate) "extract" [] ]
        let graph = dag definitions
        do! arrange context definitions [ 1L; 2L ]
        let! before = capture context context.Store.Db
        let! count = Suspension.exercise [ gate ]
                         (fun () -> batch context graph context.Store.Db "extract" 10 cancellation.Token)
                         (cancelAfter cancellation (contend context graph gate cancellation))
        Assert.Equal(1, count)
        do! ledger context.Store.Db [ "extract:1" ] 0L
        do! outputs context.Store.Db "extract" [ 1L ] 0L
        do! ready context.Store.Db graph.Stages["extract"] [ 2L ]
        Assert.Single(context.Calls) |> fun call -> Assert.Equal(1L, call.DocumentId)
        do! unchanged context before context.Store.Db
        do! clean context 0L
    }
