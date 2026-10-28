namespace Hermes.Tests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core

module V5AsyncScenarios =
    module Fixture = V5AsyncFixtures
    module Control = V5AsyncSupport
    module State = V5AsyncState
    module Check = V5AsyncAssertions
    module Suspension = AsyncCapabilityControl

    let successEvents (control: Control.Control) (transactions: int) : unit =
        Control.assertEvents control transactions
            [ Control.Effect.Provider, 1, 1; Control.Effect.Claim, 1, 1
              Control.Effect.Output, 1, 1; Control.Effect.Artifact, 0, 1 ]

    let successful
        (fixture: Fixture.Fixture) (control: Control.Control) (before: Fixture.Before) : Task<int> =
        Suspension.exercise (Control.gates control)
            (fun () -> Fixture.start fixture control fixture.Gpu CancellationToken.None)
            (fun pending ->
                if control.Suspended then Check.drive fixture control before pending
                else Task.FromResult ())

    let publication (kind: Control.Kind) (suspended: bool) : Task<unit> =
        task {
            use fixture = Fixture.create ()
            do! Fixture.arrange fixture kind
            let control = Control.create kind suspended (Ok (Fixture.expected kind).RawJson)
            let! before = Fixture.before fixture control
            let! completed = successful fixture control before
            Assert.Equal(1, completed)
            do! Check.published fixture control before
            do! State.count fixture.Store.Db "dead_letters" 0L
            successEvents control (if kind = Control.Kind.Deep then 5 else 6)
            Assert.Equal(suspended, control.Provider.WasEntered)
        }

    let private failureState
        (fixture: Fixture.Fixture) (control: Control.Control) (before: Fixture.Before)
        (error: string) (canonical: int64) : Task<unit> =
        task {
            do! State.stable fixture fixture.Store.Db control.Kind
            do! State.effective fixture.Store.Db control.Kind false
            do! State.ledger fixture.Store.Db (Control.prerequisites control.Kind) 0L
            do! State.count fixture.Store.Db (Control.table control.Kind) 0L
            do! State.signals fixture.Store.Db control.Kind false
            do! State.publicationCount fixture.Store.Db control.Kind canonical
            do! State.revision fixture.Store.Db before.Revision
            do! State.errorEvidence fixture.Store.Db (Control.name control.Kind) error 0L
        }

    let providerFailure
        (fixture: Fixture.Fixture) (control: Control.Control) (before: Fixture.Before) : Task<unit> =
        task {
            let! completed =
                Suspension.exercise (Control.gates control)
                    (fun () -> Fixture.start fixture control fixture.Gpu CancellationToken.None)
                    (Check.release fixture control before Control.Effect.Provider)
            Assert.Equal(0, completed)
            do! failureState fixture control before
                    "Deep comprehension failed for doc 1: async provider failure" 0L
            Assert.Equal(before.Artifact, fixture.Store.Memory.Get Fixture.artifactPath)
            Control.assertEvents control 2 [ Control.Effect.Provider, 1, 1 ]
        }

    let private rejectOutput
        (fixture: Fixture.Fixture) (control: Control.Control)
        (before: Fixture.Before) (pending: Task<int>) : Task<unit> =
        task {
            do! Check.release fixture control before Control.Effect.Provider pending
            do! Check.release fixture control before Control.Effect.Claim pending
            let! scoped = control.Output.Await(pending :> Task)
            do! Check.checkpoint fixture control before Control.Effect.Output scoped
            control.Output.Fail(InvalidOperationException "async derived-output failure")
        }

    let outputFailure
        (fixture: Fixture.Fixture) (control: Control.Control) (before: Fixture.Before) : Task<unit> =
        task {
            let! completed =
                Suspension.exercise (Control.gates control)
                    (fun () -> Fixture.start fixture control fixture.Gpu CancellationToken.None)
                    (rejectOutput fixture control before)
            Assert.Equal(0, completed)
            do! failureState fixture control before
                    "Generation-fenced publication failed: Transaction failed: async derived-output failure" 1L
            do! Check.canonical fixture control
            Control.assertEvents control 5
                [ Control.Effect.Provider, 1, 1; Control.Effect.Claim, 1, 1
                  Control.Effect.Output, 1, 0; Control.Effect.Artifact, 0, 1 ]
        }

    let retry (fixture: Fixture.Fixture) (response: string) (error: string) : Task<unit> =
        task {
            let control = Control.create Control.Kind.Deep true (Ok response)
            let! before = Fixture.before fixture control
            let! completed = successful fixture control before
            Assert.Equal(1, completed)
            do! Check.published fixture control before
            do! State.errorEvidence fixture.Store.Db (Control.name control.Kind) error 1L
            successEvents control 5
        }

    let pendingReflow (fixture: Fixture.Fixture) (graph: PipelineV5.Dag) : Task<unit> =
        task {
            let! rows = fixture.Store.Db.execReader "SELECT id FROM reflow_operations" []
            let operation = Assert.Single<Document.T>(rows)
            let operationId =
                Document.decode<int64> "id" operation
                |> Option.defaultWith (fun () -> failwith "Reflow operation ID was missing")
            let! result = Reflow.getStatus graph fixture.Store.Db operationId
            let status: Reflow.OperationStatus = result |> Result.defaultWith failwith
            Assert.Equal(Reflow.LifecycleRunning, status.Lifecycle)
            Assert.Equal(None, status.CompletedAt)
            Assert.Equal(None, status.Error)
            Assert.Equal<Map<string, Reflow.StageOutcome>>(
                Map.ofList [ "extract", Reflow.Current; "embed", Reflow.Current
                             "triage", Reflow.Pending; "deep-comprehend", Reflow.Pending ],
                status.Stages |> List.map (fun stage -> stage.StageName, stage.Outcome) |> Map.ofList)
        }

    let acceptReflow
        (fixture: Fixture.Fixture) (control: Control.Control)
        (before: Fixture.Before) (pending: Task<int>) : Task<unit> =
        task {
            let! database = control.Provider.Await(pending :> Task)
            do! Check.checkpoint fixture control before Control.Effect.Provider database
            let graph = Fixture.definitions fixture control |> Fixture.dag
            let! result =
                Reflow.request fixture.Store.Db fixture.Observation.Logger graph 1L
                    Reflow.Recomprehend Reflow.Apply
            let accepted: Reflow.RequestResult = result |> Result.defaultWith failwith
            Assert.False(accepted.Duplicate)
            do! pendingReflow fixture graph
            do! State.stable fixture fixture.Store.Db control.Kind
            do! State.ledger fixture.Store.Db [ "extract" ] 1L
            Assert.Equal(before.Artifact, fixture.Store.Memory.Get Fixture.artifactPath)
            control.Provider.Release()
        }

    let announcedGpu
        (gpu: PipelineV5.GpuScheduler)
        (entered: TaskCompletionSource<unit>) : PipelineV5.GpuScheduler =
        { gpu with
            Acquire = fun model ct ->
                let pending = gpu.Acquire model ct
                Assert.True(entered.TrySetResult(()))
                pending }

    let queued (fixture: Fixture.Fixture) (control: Control.Control) : Task<unit> =
        let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let observed = announcedGpu fixture.Gpu entered
        Control.cancelQueued
            (fun ct -> Fixture.start fixture control observed ct)
            entered.Task
            (fun () -> State.ledger fixture.Store.Db (Control.prerequisites control.Kind) 1L)

    let competing
        (fixture: Fixture.Fixture) (control: Control.Control)
        (before: Fixture.Before) (pending: Task<int>) : Task<unit> =
        task {
            let! database = control.Provider.Await(pending :> Task)
            do! Check.checkpoint fixture control before Control.Effect.Provider database
            let! lease = database.execReader "SELECT * FROM pipeline_stage_attempts" []
            let otherGpu = PipelineV5.createGpuScheduler TestHelpers.silentLogger
            let! completed = Fixture.start fixture control otherGpu CancellationToken.None
            Assert.Equal(0, completed)
            do! State.expectRows database "SELECT * FROM pipeline_stage_attempts" lease
            do! queued fixture control
            do! Check.checkpoint fixture control before Control.Effect.Provider database
            control.Provider.Release()
            do! Check.release fixture control before Control.Effect.Claim pending
            do! Check.release fixture control before Control.Effect.Output pending
        }

    let exactPermit (fixture: Fixture.Fixture) : Task<unit> =
        task {
            use cancellation = new CancellationTokenSource(Suspension.timeout)
            use! held = fixture.Gpu.Acquire "following-model" cancellation.Token
            let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let observed = announcedGpu fixture.Gpu entered
            let acquire (ct: CancellationToken) : Task<int> =
                task {
                    use! lease = observed.Acquire "must-still-wait" ct
                    return 0
                }
            do! Control.cancelQueued acquire entered.Task (fun () -> Task.FromResult ())
            Assert.Equal(Some "following-model", fixture.Gpu.CurrentModel())
        }

module V5AsyncTests =
    module Fixture = V5AsyncFixtures
    module Control = V5AsyncSupport
    module State = V5AsyncState
    module Check = V5AsyncAssertions
    module Scenario = V5AsyncScenarios
    module Suspension = AsyncCapabilityControl

    [<Theory; InlineData(false); InlineData(true)>]
    [<Trait("Category", "Integration")>]
    let ``V5 final triage publishes identical canonical review state with pending effects``
        (suspended: bool) : Task<unit> =
        Scenario.publication Control.Kind.Triage suspended

    [<Theory; InlineData(false); InlineData(true)>]
    [<Trait("Category", "Integration")>]
    let ``V5 deep publication preserves manual corrections and exposes canonical provenance``
        (suspended: bool) : Task<unit> =
        Scenario.publication Control.Kind.Deep suspended

    [<Fact; Trait("Category", "Integration")>]
    let ``V5 a suspended provider failure retains its root error and retries unchanged source`` () : Task<unit> =
        task {
            use fixture = Fixture.create ()
            do! Fixture.arrange fixture Control.Kind.Deep
            let control = Control.create Control.Kind.Deep true (Error "async provider failure")
            let! before = Fixture.before fixture control
            do! Scenario.providerFailure fixture control before
            do! Scenario.retry fixture Fixture.deepJson
                    "Deep comprehension failed for doc 1: async provider failure"
        }

    [<Fact; Trait("Category", "Integration")>]
    let ``V5 a suspended derived write rolls back signals and retry retains the first canonical response`` () : Task<unit> =
        task {
            use fixture = Fixture.create ()
            do! Fixture.arrange fixture Control.Kind.Deep
            let control = Control.create Control.Kind.Deep true (Ok Fixture.deepJson)
            let! before = Fixture.before fixture control
            do! Scenario.outputFailure fixture control before
            do! Scenario.retry fixture Fixture.conflictingJson
                    "Generation-fenced publication failed: Transaction failed: async derived-output failure"
        }

    [<Fact; Trait("Category", "Integration")>]
    let ``V5 reflow accepted during pending deep comprehension rejects all stale publication`` () : Task<unit> =
        task {
            use fixture = Fixture.create ()
            do! Fixture.arrange fixture Control.Kind.Deep
            let control = Control.create Control.Kind.Deep true (Ok Fixture.deepJson)
            let! before = Fixture.before fixture control
            let! completed =
                Suspension.exercise (Control.gates control)
                    (fun () -> Fixture.start fixture control fixture.Gpu CancellationToken.None)
                    (Scenario.acceptReflow fixture control before)
            Assert.Equal(0, completed)
            do! State.ledger fixture.Store.Db [ "extract" ] 0L
            do! State.publicationCount fixture.Store.Db control.Kind 0L
            do! State.count fixture.Store.Db "comprehension" 0L
            do! State.count fixture.Store.Db "dead_letters" 0L
            do! State.signals fixture.Store.Db control.Kind false
        }

    [<Fact; Trait("Category", "Integration")>]
    let ``V5 pending real processing owns one document lease and queued GPU cancellation cannot steal its permit`` () : Task<unit> =
        task {
            use fixture = Fixture.create ()
            do! Fixture.arrange fixture Control.Kind.Deep
            let control = Control.create Control.Kind.Deep true (Ok Fixture.deepJson)
            let! before = Fixture.before fixture control
            let! completed =
                Suspension.exercise (Control.gates control)
                    (fun () -> Fixture.start fixture control fixture.Gpu CancellationToken.None)
                    (Scenario.competing fixture control before)
            Assert.Equal(1, completed)
            do! Check.published fixture control before
            Scenario.successEvents control 6
            do! Scenario.exactPermit fixture
        }

module V5AsyncEmbeddingScenarios =
    module Fixture = V5AsyncFixtures
    module Encoder = V5AsyncSupport.Embedding
    module Check = V5AsyncEmbeddingState
    module State = V5AsyncState
    module Suspension = AsyncCapabilityControl

    let failure: string =
        "Embedding failed for doc 1: 1 of 1 chunks failed: chunk 0: async encoder failure"

    let prepare (fixture: Fixture.Fixture) : Task<unit> =
        task {
            do! Fixture.arrange fixture V5AsyncSupport.Kind.Triage
            do! Embeddings.initSchema fixture.Store.Db
        }

    let pipeline
        (fixture: Fixture.Fixture) (before: Check.Snapshot)
        (generation: int64) (response: Result<float32 array, string>) : Task<int> =
        let control = Encoder.create Fixture.sourceText response
        task {
            let! completed =
                Suspension.exercise (Encoder.gates control)
                    (fun () -> Fixture.Embedding.start fixture control)
                    (fun pending -> Encoder.drive control pending
                                        (Check.checkpoint fixture before generation 1L false))
            Encoder.assertUsed control
            return completed
        }

    let complete
        (fixture: Fixture.Fixture) (before: Check.Snapshot)
        (generation: int64) (vector: float32 array) : Task<unit> =
        task {
            let! completed = pipeline fixture before generation (Ok vector)
            Assert.Equal<int>(1, completed)
            do! Check.published fixture before generation vector
            do! Check.search fixture generation vector true
        }

    let partialOutput (fixture: Fixture.Fixture) (vector: float32 array) : Task<Check.Snapshot> =
        task {
            let! before = Check.capture fixture fixture.Store.Db
            let control = Encoder.create Fixture.sourceText (Ok vector)
            let! outcome =
                Suspension.exercise (Encoder.gates control)
                    (fun () -> Fixture.Embedding.publishOnly fixture control)
                    (fun pending -> Encoder.drive control pending
                                        (Check.checkpoint fixture before 0L 0L false))
            Assert.Equal<PipelineV5.StageOutcome>(PipelineV5.Completed, outcome)
            Encoder.assertUsed control
            do! State.ledger fixture.Store.Db [ "extract" ] 0L
            do! Check.search fixture 0L vector false
            return! Check.capture fixture fixture.Store.Db
        }

    let failedReplacement (fixture: Fixture.Fixture) (before: Check.Snapshot) : Task<unit> =
        task {
            Assert.Single<Document.T>(before.Chunks) |> ignore
            Assert.Single<Document.T>(before.Output) |> ignore
            let! completed = pipeline fixture before 0L (Error "async encoder failure")
            Assert.Equal<int>(0, completed)
            do! Check.unchanged fixture before 0L fixture.Store.Db
            do! State.ledger fixture.Store.Db [ "extract" ] 0L
            do! State.errorEvidence fixture.Store.Db "embed" failure 0L
            do! Check.search fixture 0L [| 1.0f; 0.0f; 0.0f |] false
        }

    let assertOperation
        (lifecycle: Reflow.Lifecycle) (outcome: Reflow.StageOutcome)
        (status: Reflow.OperationStatus) : unit =
        let expected: Map<string, Reflow.StageOutcome> =
            Map.ofList [ "extract", Reflow.Current; "triage", Reflow.Current
                         "deep-comprehend", Reflow.Current; "embed", outcome ]
        Assert.Equal<Reflow.Lifecycle>(lifecycle, status.Lifecycle)
        Assert.Equal<string option>(None, status.Error)
        Assert.Equal<bool>((lifecycle = Reflow.LifecycleCompleted), Option.isSome status.CompletedAt)
        Assert.Equal<Map<string, Reflow.StageOutcome>>(
            expected, status.Stages |> List.map (fun stage -> stage.StageName, stage.Outcome) |> Map.ofList)
        status.Stages |> List.iter (fun stage -> Assert.Equal<string option>(None, stage.Error))

    let private accept (fixture: Fixture.Fixture) (control: Encoder.Control) : Task<unit> =
        task {
            let graph = Fixture.Embedding.definitions fixture control |> Fixture.dag
            let! result =
                Reflow.request fixture.Store.Db fixture.Observation.Logger graph
                    1L Reflow.Reembed Reflow.Apply
            let accepted: Reflow.RequestResult = result |> Result.defaultWith failwith
            Assert.False(accepted.Duplicate)
            let status: Reflow.OperationStatus =
                accepted.Status |> Option.defaultWith (fun () -> failwith "Missing accepted reembed status")
            Assert.Equal<string>(PipelineV5.dagSignature graph, status.DagSignature)
            assertOperation Reflow.LifecycleRunning Reflow.Pending status
        }

    let staleCheckpoint
        (fixture: Fixture.Fixture) (control: Encoder.Control)
        (before: Check.Snapshot) (pending: Task<int>) : Task<unit> =
        task {
            do! Suspension.release control.Available pending
                    (Check.checkpoint fixture before 0L 1L false)
            let! db = control.Encode.Await(pending :> Task)
            do! Check.checkpoint fixture before 0L 1L false db
            do! accept fixture control
            do! Check.checkpoint fixture before 1L 1L false db
            control.Encode.Release()
        }

    let operation
        (fixture: Fixture.Fixture) (control: Encoder.Control)
        (lifecycle: Reflow.Lifecycle) (outcome: Reflow.StageOutcome) : Task<unit> =
        task {
            let! rows = fixture.Store.Db.execReader "SELECT id FROM reflow_operations" []
            let row = Assert.Single<Document.T>(rows)
            let id = Document.decode<int64> "id" row
                     |> Option.defaultWith (fun () -> failwith "Missing reembed operation ID")
            let graph = Fixture.Embedding.definitions fixture control |> Fixture.dag
            let! result = Reflow.getStatus graph fixture.Store.Db id
            let status: Reflow.OperationStatus = result |> Result.defaultWith failwith
            assertOperation lifecycle outcome status
        }

    let staleSettled (fixture: Fixture.Fixture) (before: Check.Snapshot) : Task<unit> =
        task {
            do! Check.unchanged fixture before 1L fixture.Store.Db
            do! State.ledger fixture.Store.Db [ "extract" ] 0L
            do! State.count fixture.Store.Db "document_chunks" 0L
            do! State.count fixture.Store.Db "embedding" 0L
            do! State.count fixture.Store.Db "dead_letters" 0L
            do! State.count fixture.Store.Db "activity_log WHERE level='error'" 0L
            do! Check.search fixture 1L [| 1.0f; 0.0f; 0.0f |] false
        }

module V5AsyncEmbeddingTests =
    module Fixture = V5AsyncFixtures
    module Encoder = V5AsyncSupport.Embedding
    module Check = V5AsyncEmbeddingState
    module State = V5AsyncState
    module Scenario = V5AsyncEmbeddingScenarios
    module Suspension = AsyncCapabilityControl

    [<Fact; Trait("Category", "Integration")>]
    let ``V5 pending embedding publishes one current vector and production semantic search reads it`` () : Task<unit> =
        task {
            use fixture = Fixture.create ()
            do! Scenario.prepare fixture
            let! before = Check.capture fixture fixture.Store.Db
            Assert.Empty(before.Chunks)
            Assert.Empty(before.Output)
            do! Scenario.complete fixture before 0L [| 1.0f; 0.0f; 0.0f |]
            do! State.count fixture.Store.Db "dead_letters" 0L
        }

    [<Fact; Trait("Category", "Integration")>]
    let ``V5 pending encoder Error preserves published output and corrections before a successful retry`` () : Task<unit> =
        task {
            use fixture = Fixture.create ()
            do! Scenario.prepare fixture
            let! before = Scenario.partialOutput fixture [| 1.0f; 0.0f; 0.0f |]
            do! Scenario.failedReplacement fixture before
            do! Scenario.complete fixture before 0L [| 0.0f; 1.0f; 0.0f |]
            do! State.errorEvidence fixture.Store.Db "embed" Scenario.failure 1L
        }

    [<Fact; Trait("Category", "Integration")>]
    let ``V5 reembed accepted during pending encoding rejects the old vector and completes the new generation`` () : Task<unit> =
        task {
            use fixture = Fixture.create ()
            do! Scenario.prepare fixture
            let! before = Check.capture fixture fixture.Store.Db
            let control = Encoder.create Fixture.sourceText (Ok [| 1.0f; 0.0f; 0.0f |])
            let! completed =
                Suspension.exercise (Encoder.gates control)
                    (fun () -> Fixture.Embedding.start fixture control)
                    (Scenario.staleCheckpoint fixture control before)
            Assert.Equal<int>(0, completed)
            Encoder.assertUsed control
            do! Scenario.staleSettled fixture before
            do! Scenario.operation fixture control Reflow.LifecycleRunning Reflow.Pending
            do! Scenario.complete fixture before 1L [| 0.0f; 1.0f; 0.0f |]
            do! Scenario.operation fixture control Reflow.LifecycleCompleted Reflow.Reran
        }
