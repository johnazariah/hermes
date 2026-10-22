module Hermes.Tests.V5SchedulerConformanceTests

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

let private eligibility (gate: Suspension.Gate)
    (db: Algebra.Database) (id: int64) : Task<bool> =
    task {
        let read () = db.execScalar "SELECT id FROM documents WHERE id=@id"
                          [ "@id", Database.boxVal id ]
        let! value = if id = 1L then gate.Invoke db read else read ()
        return Assert.IsType<int64>(value) = 2L
    }

let private prerequisites (context: Context) (graph: PipelineV5.Dag) : Task<unit> =
    task {
        let run name limit = batch context graph context.Store.Db name limit CancellationToken.None
        let! blocked = run "triage" 10
        Assert.Equal(0, blocked)
        let! extracted = run "extract" 1
        Assert.Equal(1, extracted)
        let! audited = run "audit" 10
        Assert.Equal(2, audited)
        do! ready context.Store.Db graph.Stages["triage"] [ 1L ]
    }

let private pendingGate (context: Context) (before: Snapshot)
    (gate: Suspension.Gate) (pending: Task<int>) : Task<unit> =
    task {
        let! db = gate.Await(pending :> Task)
        Assert.False(gate.EffectStarted)
        do! ledger db [ "audit:1"; "audit:2"; "extract:1" ] 1L
        do! outputs db "triage" [] 0L
        Assert.Empty(context.Calls |> Seq.filter (fun call -> call.Stage = "triage"))
        do! unchanged context before db
        gate.Release()
    }

let private finishReadiness (context: Context) (graph: PipelineV5.Dag)
    (before: Snapshot) : Task<unit> =
    task {
        let run name = batch context graph context.Store.Db name 10 CancellationToken.None
        let! extracted = run "extract"
        let! processed = run "triage"
        let! repeated = run "triage"
        Assert.Equal<int list>([ 1; 1; 0 ], [ extracted; processed; repeated ])
        do! ledger context.Store.Db
                [ "audit:1"; "audit:2"; "extract:1"; "extract:2"; "triage:1"; "triage:2" ] 0L
        do! outputs context.Store.Db "triage" [ 2L ] 0L
        Assert.Equal<int64 list>([ 2L ], context.Calls |> Seq.filter (fun call -> call.Stage = "triage")
                                                       |> Seq.map (fun call -> call.DocumentId) |> Seq.toList)
        do! unchanged context before context.Store.Db
        do! clean context 0L
    }

[<Theory; Trait("Category", "Integration")>]
[<InlineData(false, false); InlineData(false, true)>]
[<InlineData(true, false); InlineData(true, true)>]
let ``V5 scheduler requires every dependency and skip unlocks only the eligible document``
    (isBatch: bool) (yieldEffects: bool) : Task<unit> =
    task {
        use context = create yieldEffects
        let gate = Suspension.Gate("scheduler eligibility database read")
        let join = { define context context.Store.Memory.Fs "triage" [ "extract"; "audit" ] with
                        Gate = Some (eligibility gate)
                        Mode = if isBatch then PipelineV5.Batch(TimeSpan.FromMinutes 1.0) else PipelineV5.Channel }
        let definitions = [ define context context.Store.Memory.Fs "extract" []
                            define context context.Store.Memory.Fs "audit" []; join ]
        let graph = dag definitions
        do! arrange context definitions [ 1L; 2L ]
        let! before = capture context context.Store.Db
        do! prerequisites context graph
        let! skipped = Suspension.exercise [ gate ]
                           (fun () -> batch context graph context.Store.Db "triage" 10 CancellationToken.None)
                           (pendingGate context before gate)
        Assert.Equal(0, skipped)
        do! finishReadiness context graph before
    }

let private failRead (context: Context) (before: Snapshot)
    (gate: Suspension.Gate) (pending: Task<int>) : Task<unit> =
    task {
        let! db = gate.Await(pending :> Task)
        do! ledger db [] 1L
        do! outputs db "extract" [] 0L
        do! unchanged context before db
        gate.Fail(IOException "controlled source read refused")
    }

let private retryRead (context: Context) (before: Snapshot) : Task<unit> =
    task {
        let graph = dag [ define context context.Store.Memory.Fs "extract" [] ]
        let! retried = batch context graph context.Store.Db "extract" 10 CancellationToken.None
        Assert.Equal(1, retried)
        do! ledger context.Store.Db [ "extract:1"; "extract:2" ] 0L
        do! outputs context.Store.Db "extract" [ 1L; 2L ] 0L
        do! State.expectRows context.Store.Db "SELECT retry_count,dismissed FROM dead_letters"
                [ Map.ofList [ "retry_count", 0L :> obj; "dismissed", 1L :> obj ] ]
        Assert.Equal<int64 list>([ 1L; 2L; 1L ], context.Calls
                                |> Seq.map (fun call -> call.DocumentId) |> Seq.toList)
        do! unchanged context before context.Store.Db
        do! clean context 0L
    }

[<Theory; InlineData(false); InlineData(true); Trait("Category", "Integration")>]
let ``V5 scheduler records a pending input failure processes its sibling and retries without duplicate output``
    (yieldEffects: bool) : Task<unit> =
    task {
        use context = create yieldEffects
        let gate = Suspension.Gate("scheduler source read")
        let definitions = [ define context (heldFiles context gate) "extract" [] ]
        let graph = dag definitions
        do! arrange context definitions [ 1L; 2L ]
        let! before = capture context context.Store.Db
        let! count = Suspension.exercise [ gate ]
                         (fun () -> batch context graph context.Store.Db "extract" 10 CancellationToken.None)
                         (failRead context before gate)
        Assert.Equal(1, count)
        do! ledger context.Store.Db [ "extract:2" ] 0L
        do! outputs context.Store.Db "extract" [ 2L ] 0L
        do! State.errorEvidence context.Store.Db "extract" "controlled source read refused" 0L
        do! retryRead context before
    }

let private supersede (context: Context) (graph: PipelineV5.Dag) (before: Snapshot)
    (gate: Suspension.Gate) (pending: Task<int>) : Task<unit> =
    task {
        let! db = gate.Await(pending :> Task)
        do! ledger db [ "extract:1"; "embed:1" ] 1L
        do! unchanged context before db
        let! result = Reflow.request db context.Observation.Logger graph 1L Reflow.Recomprehend Reflow.Apply
        let accepted = result |> Result.defaultWith failwith
        let status = accepted.Status |> Option.defaultWith (fun () -> failwith "Missing accepted operation")
        Assert.False(accepted.Duplicate)
        Assert.Equal(Reflow.LifecycleRunning, status.Lifecycle)
        let! generation = Generation.current db 1L
        Assert.Equal(1L, generation.Value)
        do! unchanged context before db
        gate.Fail(IOException "superseded read failure")
    }

let private finishReflow (context: Context) (graph: PipelineV5.Dag) : Task<unit> =
    task {
        for name in [ "triage"; "deep-comprehend" ] do
            let! count = batch context graph context.Store.Db name 10 CancellationToken.None
            Assert.Equal(1, count)
            do! outputs context.Store.Db name [ 1L ] 1L
        let! result = Reflow.getLatestForDocument graph context.Store.Db 1L Reflow.Recomprehend
        let status = result |> Result.defaultWith failwith
                     |> Option.defaultWith (fun () -> failwith "Missing reflow status")
        Assert.Equal(Reflow.LifecycleCompleted, status.Lifecycle)
        Assert.True(status.CompletedAt.IsSome)
        Assert.Equal(None, status.Error)
        Assert.Equal<Map<string, Reflow.StageOutcome>>(
            Map.ofList [ "extract", Reflow.Current; "embed", Reflow.Current
                         "triage", Reflow.Reran; "deep-comprehend", Reflow.Reran ],
            status.Stages |> List.map (fun stage -> stage.StageName, stage.Outcome) |> Map.ofList)
    }

let private prerequisitesForReflow (context: Context) (graph: PipelineV5.Dag) : Task<unit> =
    task {
        for name in [ "extract"; "embed" ] do
            let! count = batch context graph context.Store.Db name 10 CancellationToken.None
            Assert.Equal(1, count)
    }

let private settleSuperseded (context: Context) (graph: PipelineV5.Dag) (before: Snapshot) : Task<unit> =
    task {
        do! ledger context.Store.Db [ "extract:1"; "embed:1" ] 0L
        do! outputs context.Store.Db "triage" [] 1L
        do! State.count context.Store.Db "activity_log WHERE level='error'" 0L
        Assert.True(Fixture.saw context.Observation "was superseded by reflow and discarded")
        do! finishReflow context graph
        do! ledger context.Store.Db [ "extract:1"; "embed:1"; "triage:1"; "deep-comprehend:1" ] 0L
        Assert.Equal<int64 list>([ 0L; 0L; 0L; 1L; 1L ], context.Calls
                                |> Seq.map (fun call -> call.Generation) |> Seq.toList)
        do! unchanged context before context.Store.Db
        do! clean context 1L
    }

[<Theory; InlineData(false); InlineData(true); Trait("Category", "Integration")>]
let ``V5 scheduler discards a superseded failure without failing the newly accepted operation``
    (yieldEffects: bool) : Task<unit> =
    task {
        use context = create yieldEffects
        let gate = Suspension.Gate("superseded scheduler source read")
        let normal = standard context
        let definitions = normal |> List.map (fun stage ->
            if stage.Name = "triage" then define context (heldFiles context gate) stage.Name stage.DependsOn
            else stage)
        let graph = dag definitions
        do! arrange context definitions [ 1L ]
        do! prerequisitesForReflow context graph
        let! before = capture context context.Store.Db
        let! count = Suspension.exercise [ gate ]
                         (fun () -> batch context graph context.Store.Db "triage" 10 CancellationToken.None)
                         (supersede context graph before gate)
        Assert.Equal(0, count)
        do! settleSuperseded context (dag normal) before
    }
