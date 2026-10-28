module Hermes.Tests.V5SchedulerConformanceSupport

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core

module Fixture = CoreRegressionSupport
module Suspension = AsyncCapabilityControl
module State = V5AsyncState
module Effects = V5SchedulerConformanceEffects

type Invocation =
    { Stage: string
      DocumentId: int64
      Generation: int64 }

type Context =
    { Store: Fixture.Fixture
      Observation: Fixture.Probe
      Gpu: PipelineV5.GpuScheduler
      Calls: ConcurrentQueue<Invocation> }
    interface IDisposable with
        member this.Dispose() = (this.Store :> IDisposable).Dispose()

type Snapshot =
    { Metadata: Document.T list
      Corrections: Document.T list
      Tags: Document.T list
      Claims: Document.T list
      Files: Map<string, string> }

let create (yieldEffects: bool) : Context =
    let observation = Fixture.probe "Pipeline v5 stopped"
    let store = Fixture.fixture ()
    let interpreted: Fixture.Fixture =
        { Db = Effects.database yieldEffects store.Db
          Memory = { store.Memory with Fs = Effects.files yieldEffects store.Memory.Fs } }
    { Store = interpreted; Observation = observation
      Gpu = PipelineV5.createGpuScheduler observation.Logger
      Calls = ConcurrentQueue<Invocation>() }

let table (name: string) = "v5_scheduler_" + name.Replace('-', '_')

// Deliberately leave settlement to PipelineV5, including stale-output disposal.
let private writeOutput (db: Algebra.Database) (name: string)
    (execution: PipelineV5.StageExecution) (text: string) : Task<PipelineV5.StageOutcome> =
    task {
        let! _ = db.execNonQuery
                    $"INSERT OR REPLACE INTO {table name}
                        (document_id,generation,payload) VALUES (@doc,@generation,@text)"
                    [ "@doc", Database.boxVal execution.DocumentId
                      "@generation", Database.boxVal execution.Generation.Value
                      "@text", Database.boxVal text ]
        return PipelineV5.Completed
    }

let private publish (db: Algebra.Database) (name: string)
    (execution: PipelineV5.StageExecution) (text: string) : Task<PipelineV5.StageOutcome> =
    db.transaction.Run(fun scoped -> writeOutput scoped name execution text)

let define (context: Context) (fs: Algebra.FileSystem)
    (name: string) (dependencies: string list) : PipelineV5.StageDefinition =
    { Name = name; DependsOn = dependencies; OutputTable = table name
      Schema = $"CREATE TABLE IF NOT EXISTS {table name}
                    (document_id INTEGER PRIMARY KEY REFERENCES documents(id),
                     generation INTEGER NOT NULL,payload TEXT NOT NULL)"
      Gate = None; GpuModel = None; Mode = PipelineV5.Channel; Concurrency = 1
      Process = fun db _ execution -> task {
          context.Calls.Enqueue
              { Stage = name; DocumentId = execution.DocumentId
                Generation = execution.Generation.Value }
          let! text = fs.readAllText (Fixture.sourcePath execution.DocumentId)
          return! publish db name execution text
      } }

let standard (context: Context) : PipelineV5.StageDefinition list =
    TestHelpers.standardV5Stages
    |> List.map (fun stage ->
        define context context.Store.Memory.Fs stage.Name stage.DependsOn)

let dag definitions = definitions |> PipelineV5.buildDag |> Result.defaultWith failwith

let private seedOwned (context: Context) () (id: int64) : Task<unit> =
    task {
        do! Fixture.seed context.Store id "received"
        let! bytes = context.Store.Memory.Fs.readAllBytes (Fixture.sourcePath id)
        do! SourceFileAccess.claim context.Store.Db
                (Fixture.savedPath id) (SourceFiles.hash bytes)
    }

let arrange (context: Context) (definitions: PipelineV5.StageDefinition list)
    (ids: int64 list) : Task<unit> =
    task {
        do! PipelineV5.initSchema context.Store.Db definitions
        do! ids |> Prelude.foldTask (seedOwned context) ()
        let! _ = context.Store.Db.execNonQuery
                    """UPDATE documents SET classification_tier='manual';
                       INSERT INTO corrections(document_id,field,corrected_value)
                           SELECT id,'extracted_vendor','archive-vendor' FROM documents;
                       INSERT INTO tags(document_id,tag,source)
                           SELECT id,'owner-pinned','user' FROM documents;""" []
        return ()
    }

let batch (context: Context) (graph: PipelineV5.Dag) (db: Algebra.Database)
    (name: string) (limit: int) (ct: CancellationToken) : Task<int> =
    PipelineV5.processStageForDag graph graph.Stages[name] db
        context.Observation.Logger context.Gpu limit Suspension.timeout ct

let loop (context: Context) (graph: PipelineV5.Dag)
    (db: Algebra.Database) (ct: CancellationToken) : Task<unit> =
    PipelineV5.run graph db context.Observation.Logger context.Gpu
        10 Suspension.timeout TimeSpan.Zero ct

let cancelAfter (cancellation: CancellationTokenSource)
    (verify: Task<'T> -> Task<unit>) (pending: Task<'T>) : Task<unit> =
    task {
        try do! verify pending
        finally cancellation.Cancel()
    }

let heldFiles (context: Context) (gate: Suspension.Gate) : Algebra.FileSystem =
    let source = context.Store.Memory.Fs
    { source with
        readAllText = fun path ->
            if path = Fixture.sourcePath 1L then
                gate.Invoke context.Store.Db (fun () -> source.readAllText path)
            else source.readAllText path }

let heldReady (source: Algebra.Database) (gate: Suspension.Gate)
    (stage: PipelineV5.StageDefinition) : Algebra.Database =
    { source with
        execReader = fun sql parameters ->
            if sql = PipelineV5.readyQuery stage 10 && not gate.WasEntered then
                gate.Invoke source (fun () -> source.execReader sql parameters)
            else source.execReader sql parameters }

let capture (context: Context) (db: Algebra.Database) : Task<Snapshot> =
    task {
        let! metadata = db.execReader
                            """SELECT id,source_type,saved_path,sha256,category,starred,
                                      extracted_vendor,classification_tier
                               FROM documents ORDER BY id""" []
        let! corrections = db.execReader "SELECT * FROM corrections ORDER BY id" []
        let! tags = db.execReader "SELECT * FROM tags ORDER BY id" []
        let! claims = db.execReader "SELECT * FROM archive_source_claims ORDER BY ownership_key" []
        return { Metadata = metadata; Corrections = corrections; Tags = tags
                 Claims = claims
                 Files = context.Store.Memory.Files
                         |> Seq.map (fun entry -> entry.Key, entry.Value) |> Map.ofSeq }
    }

let unchanged (context: Context) (before: Snapshot) (db: Algebra.Database) : Task<unit> =
    task {
        let! actual = capture context db
        Assert.Equal<Snapshot>(before, actual)
    }

let ledger (db: Algebra.Database) (expected: string list) (attempts: int64) : Task<unit> =
    task {
        let rows = expected |> List.sort
                   |> List.map (fun value -> Map.ofList [ "value", value :> obj ])
        do! State.expectRows db
                """SELECT stage_name || ':' || document_id AS value
                   FROM stage_completions ORDER BY stage_name,document_id""" rows
        do! State.count db "pipeline_stage_attempts" attempts
    }

let outputs (db: Algebra.Database) (name: string)
    (ids: int64 list) (generation: int64) : Task<unit> =
    ids |> List.map (fun id ->
        Map.ofList [ "document_id", id :> obj; "generation", generation :> obj
                     "payload", Fixture.payload id :> obj ])
    |> State.expectRows db
        $"SELECT document_id,generation,payload FROM {table name} ORDER BY document_id"

let ready (db: Algebra.Database) (stage: PipelineV5.StageDefinition)
    (expected: int64 list) : Task<unit> =
    expected |> List.map (fun id -> Map.ofList [ "id", id :> obj ])
    |> State.expectRows db (PipelineV5.readyQuery stage 10)

let clean (context: Context) (expectedGeneration: int64) : Task<unit> =
    task {
        do! State.count context.Store.Db "pipeline_stage_attempts" 0L
        do! State.count context.Store.Db "dead_letters WHERE dismissed=0" 0L
        do! State.expectRows context.Store.Db
                """SELECT DISTINCT COALESCE(g.generation,0) AS generation
                   FROM documents d LEFT JOIN document_generations g ON g.document_id=d.id"""
                [ Map.ofList [ "generation", expectedGeneration :> obj ] ]
        use cancellation = new CancellationTokenSource(Suspension.timeout)
        use! lease = context.Gpu.Acquire "cleanup-probe" cancellation.Token
        Assert.Equal(Some "cleanup-probe", context.Gpu.CurrentModel())
    }
