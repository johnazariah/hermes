namespace Hermes.Core

open System
open System.Threading
open System.Threading.Tasks

/// Pipeline v5: Declarative DAG-based pipeline framework.
/// Stages declare dependencies; framework discovers the DAG, manages GPU scheduling,
/// and handles idempotency via stage_completions table.
[<RequireQualifiedAccess>]
module PipelineV5 =

    // ── Stage outcome ────────────────────────────────────────────────

    /// Result of processing a single document through a stage.
    type StageOutcome =
        | Completed   // successfully processed
        | Skipped     // gate returned false — mark done, skip processing
        | Failed of error: string

    // ── Execution mode ───────────────────────────────────────────────

    type ExecutionMode =
        | Channel                           // in-memory channel, low-latency
        | Batch of pollInterval: TimeSpan   // DB-polled at interval

    // ── Stage definition ─────────────────────────────────────────────

    /// A stage declares what it needs, what it produces, and how to process.
    /// The framework handles wiring, idempotency, GPU scheduling, and failure.
    type StageDefinition =
        { /// Unique name (used in stage_completions, logging, DAG edges).
          Name: string

          /// Names of stages that must complete before this stage can run.
          DependsOn: string list

          /// The table this stage writes its output to.
          OutputTable: string

          /// DDL to create the output table. Run at startup (IF NOT EXISTS).
          Schema: string

          /// The processing function. Reads from input tables, writes to OutputTable.
          /// Returns StageOutcome.
          Process: Algebra.Database -> Algebra.Logger -> int64 -> Task<StageOutcome>

          /// Optional gate: should this stage run for this document?
          /// If None → always run. If Some f and f returns false → Skipped.
          Gate: (Algebra.Database -> int64 -> Task<bool>) option

          /// GPU model required (None = CPU-only stage).
          GpuModel: string option

          /// How this stage receives work.
          Mode: ExecutionMode

          /// Max concurrent processors (typically 1 for GPU, 8 for CPU).
          Concurrency: int }

    // ── DAG ──────────────────────────────────────────────────────────

    /// Validated DAG of stages.
    type Dag =
        { Stages: Map<string, StageDefinition>
          /// Topological order (dependencies before dependents).
          Order: string list
          /// Stages grouped by GPU model for phase scheduling.
          Phases: (string option * StageDefinition list) list }

    /// Validate stage definitions and build a DAG.
    /// Checks: no duplicate names, no unknown dependencies, no cycles.
    let buildDag (stages: StageDefinition list) : Result<Dag, string> =
        let names = stages |> List.map (fun s -> s.Name) |> Set.ofList
        let byName = stages |> List.map (fun s -> s.Name, s) |> Map.ofList

        // Check duplicates
        if names.Count <> stages.Length then
            let dupes = stages |> List.groupBy (fun s -> s.Name) |> List.filter (fun (_, g) -> g.Length > 1) |> List.map fst
            Error $"Duplicate stage names: {dupes}"
        // Check unknown dependencies
        else
            let unknown =
                stages
                |> List.collect (fun s -> s.DependsOn |> List.filter (fun d -> not (names.Contains d)))
            if unknown <> [] then
                Error $"Unknown dependencies: {unknown}"
            else
                // Topological sort (Kahn's algorithm)
                let mutable inDegree = stages |> List.map (fun s -> s.Name, s.DependsOn.Length) |> Map.ofList
                let mutable adjacency =
                    stages |> List.collect (fun s -> s.DependsOn |> List.map (fun d -> d, s.Name))
                    |> List.groupBy fst |> List.map (fun (k, vs) -> k, vs |> List.map snd) |> Map.ofList
                let mutable queue = stages |> List.filter (fun s -> s.DependsOn.IsEmpty) |> List.map (fun s -> s.Name)
                let mutable order = []
                let mutable visited = 0

                while queue <> [] do
                    let node = queue.Head
                    queue <- queue.Tail
                    order <- order @ [node]
                    visited <- visited + 1
                    let neighbors = adjacency |> Map.tryFind node |> Option.defaultValue []
                    for n in neighbors do
                        let newDeg = (inDegree.[n]) - 1
                        inDegree <- inDegree |> Map.add n newDeg
                        if newDeg = 0 then
                            queue <- queue @ [n]

                if visited <> stages.Length then
                    Error "Cycle detected in stage dependencies"
                else
                    // Group by GPU model for phase scheduling
                    let phases =
                        stages
                        |> List.groupBy (fun s -> s.GpuModel)
                        |> List.sortBy (fun (model, _) ->
                            match model with
                            | None -> 0          // CPU first
                            | Some m when m.Contains "7b" -> 1
                            | Some m when m.Contains "embed" || m.Contains "nomic" -> 2
                            | _ -> 3)            // largest model last

                    Ok { Stages = byName; Order = order; Phases = phases }

    // ── GPU scheduler ────────────────────────────────────────────────

    /// Cooperative GPU scheduler. Stages acquire by model name.
    /// Only one model can be active at a time.
    type GpuScheduler =
        { /// Acquire GPU for a model. Blocks until available. Returns release handle.
          Acquire: string -> CancellationToken -> Task<IDisposable>
          /// Currently loaded model (for dashboard).
          CurrentModel: unit -> string option }

    /// Create a GPU scheduler backed by a semaphore.
    let createGpuScheduler (logger: Algebra.Logger) : GpuScheduler =
        let sem = new SemaphoreSlim(1, 1)
        let mutable currentModel: string option = None

        let release () =
            sem.Release() |> ignore
            logger.debug "GPU released"

        { Acquire = fun model ct -> task {
              do! sem.WaitAsync(ct)
              if currentModel <> Some model then
                  let prev = currentModel |> Option.defaultValue "none"
                  logger.info $"GPU: loading model '{model}' (was '{prev}')"
                  currentModel <- Some model
              return { new IDisposable with member _.Dispose() = release () }
          }
          CurrentModel = fun () -> currentModel }

    // ── Ready query generation ───────────────────────────────────────

    /// Generate SQL to find documents ready for a given stage.
    /// Ready = all dependencies completed AND this stage not completed.
    let readyQuery (stage: StageDefinition) (limit: int) : string =
        let depJoins =
            stage.DependsOn
            |> List.mapi (fun i dep ->
                $"JOIN stage_completions sc{i} ON sc{i}.document_id = d.id AND sc{i}.stage_name = '{dep}'")
            |> String.concat "\n"

        $"""SELECT d.id FROM documents d
{depJoins}
WHERE NOT EXISTS (
    SELECT 1 FROM stage_completions sc_self
    WHERE sc_self.document_id = d.id AND sc_self.stage_name = '{stage.Name}'
)
ORDER BY d.id ASC
LIMIT {limit}"""

    // ── Stage completion ─────────────────────────────────────────────

    /// Mark a stage as completed for a document.
    let markCompleted (db: Algebra.Database) (docId: int64) (stageName: string) : Task<unit> =
        task {
            let! _ =
                db.execNonQuery
                    """INSERT OR IGNORE INTO stage_completions (document_id, stage_name)
                       VALUES (@doc, @stage)"""
                    [ ("@doc", Database.boxVal docId)
                      ("@stage", Database.boxVal stageName) ]
            return ()
        }

    /// Mark a stage as failed for a document (logs to dead_letters).
    let markFailed (db: Algebra.Database) (logger: Algebra.Logger) (docId: int64) (stageName: string) (error: string) : Task<unit> =
        task {
            let! _ =
                db.execNonQuery
                    """INSERT INTO dead_letters (doc_id, stage, error, retryable, failed_at, original_name)
                       SELECT @doc, @stage, @error, 1, datetime('now'), original_name
                       FROM documents WHERE id = @doc"""
                    [ ("@doc", Database.boxVal docId)
                      ("@stage", Database.boxVal stageName)
                      ("@error", Database.boxVal error) ]
            logger.warn $"Stage '{stageName}' failed for doc {docId}: {error}"
            return ()
        }

    // ── Phase executor ───────────────────────────────────────────────

    /// Process a batch of documents through a stage.
    /// Returns count of documents processed.
    let processStage
        (stage: StageDefinition)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (gpu: GpuScheduler)
        (maxDocs: int)
        (maxTime: TimeSpan)
        (ct: CancellationToken)
        : Task<int> =
        task {
            let sql = readyQuery stage maxDocs
            let! rows = db.execReader sql []
            let docIds =
                rows |> List.choose (fun row ->
                    let r = Prelude.RowReader(row)
                    r.OptInt64 "id")

            if docIds.IsEmpty then return 0
            else

            logger.info $"Stage '{stage.Name}': {docIds.Length} docs ready"

            // Acquire GPU if needed
            let! gpuHandle =
                match stage.GpuModel with
                | Some model -> task {
                    let! handle = gpu.Acquire model ct
                    return Some handle }
                | None -> Task.FromResult None

            try
                let phaseStart = DateTime.UtcNow
                let mutable processed = 0

                for docId in docIds do
                    if ct.IsCancellationRequested then () else
                    if (DateTime.UtcNow - phaseStart) > maxTime then () else

                    // Gate check
                    let! shouldRun =
                        match stage.Gate with
                        | None -> Task.FromResult true
                        | Some gate -> gate db docId

                    if shouldRun then
                        try
                            let! outcome = stage.Process db logger docId
                            match outcome with
                            | Completed ->
                                do! markCompleted db docId stage.Name
                                processed <- processed + 1
                            | Skipped ->
                                do! markCompleted db docId stage.Name
                            | Failed error ->
                                do! markFailed db logger docId stage.Name error
                        with ex ->
                            do! markFailed db logger docId stage.Name ex.Message
                    else
                        // Gate said no — mark as completed (skipped)
                        do! markCompleted db docId stage.Name

                logger.info $"Stage '{stage.Name}': processed {processed} docs"
                return processed
            finally
                match gpuHandle with
                | Some h -> h.Dispose()
                | None -> ()
        }

    // ── Main pipeline loop ───────────────────────────────────────────

    /// Run the pipeline: cycle through phases, processing each stage.
    /// Each phase loads one GPU model and processes all ready work for that model.
    let run
        (dag: Dag)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (gpu: GpuScheduler)
        (maxDocsPerPhase: int)
        (maxTimePerPhase: TimeSpan)
        (idleInterval: TimeSpan)
        (ct: CancellationToken)
        : Task<unit> =
        task {
            logger.info $"Pipeline v5 starting: {dag.Order.Length} stages, {dag.Phases.Length} phases"

            while not ct.IsCancellationRequested do
                let mutable totalProcessed = 0

                // Run each phase
                for (model, stages) in dag.Phases do
                    if ct.IsCancellationRequested then () else

                    for stage in stages do
                        if ct.IsCancellationRequested then () else

                        let! count = processStage stage db logger gpu maxDocsPerPhase maxTimePerPhase ct
                        totalProcessed <- totalProcessed + count

                // If no work was done in any phase, wait before next cycle
                if totalProcessed = 0 then
                    try do! Task.Delay(idleInterval, ct)
                    with :? OperationCanceledException -> ()

            logger.info "Pipeline v5 stopped"
        }

    // ── DAG visualization ──────────────────────────────────────────

    /// Generate a Mermaid diagram of the pipeline DAG.
    let toMermaid (dag: Dag) (db: Algebra.Database) : Task<string> =
        task {
            // Get doc counts per stage
            let! rows =
                db.execReader
                    "SELECT stage_name, count(*) as cnt FROM stage_completions GROUP BY stage_name"
                    []
            let counts =
                rows |> List.map (fun row ->
                    let r = Prelude.RowReader(row)
                    r.String "stage_name" "", r.Int64 "cnt" 0L)
                |> Map.ofList

            let! totalObj = db.execScalar "SELECT count(*) FROM documents" []
            let total = match totalObj with :? int64 as i -> i | _ -> 0L

            let lines = System.Text.StringBuilder()
            lines.AppendLine("graph LR") |> ignore

            for name in dag.Order do
                let stage = dag.Stages.[name]
                let safeName = name.Replace("-", "_")
                let completed = counts |> Map.tryFind name |> Option.defaultValue 0L
                let gpu = stage.GpuModel |> Option.map (fun m -> $"\\n🔧 {m}") |> Option.defaultValue ""
                let mode = match stage.Mode with Batch _ -> "\\n📦 batch" | Channel -> ""
                let gate = if stage.Gate.IsSome then "\\n🚪 gated" else ""
                let count = $"\\n✅ {completed}/{total}"
                lines.AppendLine($"    {safeName}[\"{name}{gpu}{mode}{gate}{count}\"]") |> ignore

            for name in dag.Order do
                let stage = dag.Stages.[name]
                let safeName = name.Replace("-", "_")
                for dep in stage.DependsOn do
                    let safeDep = dep.Replace("-", "_")
                    lines.AppendLine($"    {safeDep} --> {safeName}") |> ignore

            // Style GPU phases
            for name in dag.Order do
                let stage = dag.Stages.[name]
                let safeName = name.Replace("-", "_")
                match stage.GpuModel with
                | None -> lines.AppendLine($"    style {safeName} fill:#334155,stroke:#64748b") |> ignore
                | Some m when m.Contains "7b" -> lines.AppendLine($"    style {safeName} fill:#1e3a5f,stroke:#3b82f6") |> ignore
                | Some m when m.Contains "32b" -> lines.AppendLine($"    style {safeName} fill:#5b2138,stroke:#ef4444") |> ignore
                | Some m when m.Contains "embed" || m.Contains "nomic" -> lines.AppendLine($"    style {safeName} fill:#1a3d2e,stroke:#22c55e") |> ignore
                | _ -> ()

            return lines.ToString()
        }

    // ── Schema ───────────────────────────────────────────────────────

    /// Core tables required by the framework.
    let coreSchema = [|
        """
        CREATE TABLE IF NOT EXISTS stage_completions (
            document_id     INTEGER NOT NULL REFERENCES documents(id),
            stage_name      TEXT NOT NULL,
            completed_at    TEXT NOT NULL DEFAULT (datetime('now')),
            PRIMARY KEY (document_id, stage_name)
        );
        """
        "CREATE INDEX IF NOT EXISTS idx_stage_completions_stage ON stage_completions(stage_name);"
    |]

    /// Initialize schema: core tables + all stage output tables.
    let initSchema (db: Algebra.Database) (stages: StageDefinition list) : Task<unit> =
        task {
            for sql in coreSchema do
                let! _ = db.execNonQuery sql []
                ()
            for stage in stages do
                if not (System.String.IsNullOrWhiteSpace stage.Schema) then
                    let! _ = db.execNonQuery stage.Schema []
                    ()
        }
