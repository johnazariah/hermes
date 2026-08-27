namespace Hermes.Core

open System
open System.Security.Cryptography
open System.Text
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

    /// Identity and generation captured atomically with a stage lease.
    type StageExecution =
        { DocumentId: int64
          Generation: Generation.Token }

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
          Process: Algebra.Database -> Algebra.Logger -> StageExecution -> Task<StageOutcome>

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

    /// Canonical identity for persisted work created against a DAG.
    let dagSignature (dag: Dag) : string =
        dag.Order
        |> List.map (fun name ->
            let stage = dag.Stages.[name]
            let dependencies = stage.DependsOn |> List.sort |> String.concat ","
            $"{stage.Name}|{dependencies}|{stage.OutputTable}")
        |> String.concat ";"
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString

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

    // ── Atomic stage lifecycle ───────────────────────────────────────

    type private StageAttemptIdentity =
        { DocumentId: int64
          StageName: string
          Token: string }

    type private StageAttempt =
        { Identity: StageAttemptIdentity
          Generation: Generation.Token
          OperationIds: int64 list }

    type private AttemptResolution =
        | Applied of countedAsProcessed: bool
        | Superseded

    let private createAttemptIdentity docId stageName =
        { DocumentId = docId
          StageName = stageName
          Token = Guid.NewGuid().ToString("N") }

    let deriveDocumentStage = StageState.deriveDocumentStage

    let private staleDagError =
        "Operation DAG signature is stale for the current pipeline"

    let private staleStageSql =
        """UPDATE reflow_operation_stages
           SET outcome = 'failed',
               completed_at = COALESCE(completed_at, datetime('now')),
               error = @error
           WHERE outcome IN ('pending', 'failed')
             AND operation_id IN (
                 SELECT id FROM reflow_operations
                 WHERE document_id = @doc
                   AND lifecycle IN ('pending', 'running', 'failed')
                   AND COALESCE(dag_signature, '') <> @signature)"""

    let private staleOperationSql =
        """UPDATE reflow_operations
           SET lifecycle = 'failed',
               completed_at = COALESCE(completed_at, datetime('now')),
               error = @error
           WHERE document_id = @doc
             AND lifecycle IN ('pending', 'running', 'failed')
             AND COALESCE(dag_signature, '') <> @signature"""

    let internal retireStaleOperations
        (scope: Algebra.TransactionScope)
        (docId: int64)
        (currentSignature: string)
        : Task<unit> =
        task {
            let parameters =
                [ ("@doc", Database.boxVal docId)
                  ("@signature", Database.boxVal currentSignature)
                  ("@error", Database.boxVal staleDagError) ]
            let! _ = scope.execNonQuery staleStageSql parameters
            let! _ = scope.execNonQuery staleOperationSql parameters
            return ()
        }

    let private claimReflowStageSql =
        """UPDATE reflow_operation_stages
           SET outcome = 'pending',
               started_at = COALESCE(started_at, datetime('now')),
               completed_at = NULL,
               error = NULL
           WHERE stage_name = @stage
             AND outcome IN ('pending', 'failed')
             AND operation_id IN (
                 SELECT id FROM reflow_operations
                 WHERE document_id = @doc
                   AND lifecycle IN ('running', 'failed')
                   AND dag_signature = @signature)
           RETURNING operation_id AS id"""

    let private claimReflowStage
        (scope: Algebra.TransactionScope)
        (docId: int64)
        (stageName: string)
        (currentSignature: string)
        : Task<int64 list> =
        task {
            let! rows =
                scope.execReader
                    claimReflowStageSql
                    [ ("@doc", Database.boxVal docId)
                      ("@stage", Database.boxVal stageName)
                      ("@signature", Database.boxVal currentSignature) ]
            return
                rows
                |> List.choose (fun row -> (Prelude.RowReader row).OptInt64 "id")
                |> List.distinct
        }

    let private markClaimedOperationRunningSql =
        """UPDATE reflow_operations
           SET lifecycle = 'running', completed_at = NULL, error = NULL
           WHERE id = @id
             AND dag_signature = @signature
             AND lifecycle IN ('running', 'failed')"""

    let private markClaimedOperationsRunning
        (scope: Algebra.TransactionScope)
        (currentSignature: string)
        (operationIds: int64 list)
        : Task<unit> =
        let markOne () operationId =
            task {
                let! affected =
                    scope.execNonQuery
                        markClaimedOperationRunningSql
                        [ ("@id", Database.boxVal operationId)
                          ("@signature", Database.boxVal currentSignature) ]
                if affected <> 1 then
                    invalidOp $"Reflow operation {operationId} could not be claimed"
            }
        operationIds |> Prelude.foldTask markOne ()

    let private attemptDependencySql (stage: StageDefinition) =
        stage.DependsOn
        |> List.mapi (fun index _ ->
            $"""AND EXISTS (
                    SELECT 1 FROM stage_completions
                    WHERE document_id = @doc
                      AND stage_name = @dependency{index})""")
        |> String.concat "\n"

    let private attemptLeaseSql stage =
        $"""INSERT INTO pipeline_stage_attempts
                 (document_id, stage_name, attempt_token)
           SELECT @doc, @stage, @token
           WHERE NOT EXISTS (
               SELECT 1 FROM stage_completions
               WHERE document_id = @doc AND stage_name = @stage)
           {attemptDependencySql stage}
           ON CONFLICT(document_id) DO NOTHING"""

    let private acquireAttemptLease
        (scope: Algebra.TransactionScope)
        (stage: StageDefinition)
        (identity: StageAttemptIdentity)
        : Task<bool> =
        task {
            let dependencyParameters =
                stage.DependsOn
                |> List.mapi (fun index name ->
                    ($"@dependency{index}", Database.boxVal name))
            let! affected =
                scope.execNonQuery
                    (attemptLeaseSql stage)
                    ([ ("@doc", Database.boxVal identity.DocumentId)
                       ("@stage", Database.boxVal identity.StageName)
                       ("@token", Database.boxVal identity.Token) ]
                     @ dependencyParameters)
            return affected = 1
        }

    let private claimAttemptOperations
        (scope: Algebra.TransactionScope)
        (identity: StageAttemptIdentity)
        (currentSignature: string option)
        : Task<int64 list> =
        match currentSignature with
        | None -> Task.FromResult<int64 list>([])
        | Some signature ->
            task {
                do! retireStaleOperations scope identity.DocumentId signature
                let! operationIds =
                    claimReflowStage
                        scope identity.DocumentId identity.StageName signature
                do! markClaimedOperationsRunning scope signature operationIds
                return operationIds
            }

    let private startStageAttemptTransaction
        currentSignature
        (stage: StageDefinition)
        (identity: StageAttemptIdentity)
        (captured: TaskCompletionSource<StageAttempt option>)
        (scope: Algebra.TransactionScope)
        : Task<Result<unit, string>> =
        task {
            let! acquired = acquireAttemptLease scope stage identity
            if acquired then
                let! generation =
                    Generation.currentIn scope identity.DocumentId
                let! operationIds =
                    claimAttemptOperations scope identity currentSignature
                captured.TrySetResult(
                    Some
                        { Identity = identity
                          Generation = generation
                          OperationIds = operationIds })
                |> ignore
            else
                captured.TrySetResult(None) |> ignore
            return Ok ()
        }

    let private startStageAttempt
        currentSignature
        (stage: StageDefinition)
        (db: Algebra.Database)
        (docId: int64)
        : Task<StageAttempt option> =
        task {
            let identity = createAttemptIdentity docId stage.Name
            let captured =
                TaskCompletionSource<StageAttempt option>(
                    TaskCreationOptions.RunContinuationsAsynchronously)
            let! result =
                db.inTransaction
                    (startStageAttemptTransaction
                        currentSignature stage identity captured)
            match result with
            | Ok () -> return! captured.Task
            | Error error ->
                return invalidOp
                    $"Stage '{stage.Name}' attempt claim failed: {error}"
        }

    let private reflowOutcome = function
        | Completed -> "reran"
        | Skipped -> "skipped"
        | Failed _ -> invalidArg "outcome" "A failed outcome cannot be finalised as success"

    let private markCapturedStageOutcome
        (scope: Algebra.TransactionScope)
        (currentSignature: string)
        (operationIds: int64 list)
        (stageName: string)
        (outcome: StageOutcome)
        : Task<unit> =
        let markOne () operationId =
            task {
                let! affected =
                    scope.execNonQuery
                        """UPDATE reflow_operation_stages
                           SET outcome = @outcome, completed_at = datetime('now'), error = NULL
                           WHERE operation_id = @op
                             AND stage_name = @stage
                             AND outcome = 'pending'
                             AND EXISTS (
                                SELECT 1 FROM reflow_operations
                                 WHERE id = @op
                                   AND lifecycle = 'running'
                                   AND dag_signature = @signature)"""
                        [ ("@outcome", Database.boxVal (reflowOutcome outcome))
                          ("@op", Database.boxVal operationId)
                          ("@stage", Database.boxVal stageName)
                          ("@signature", Database.boxVal currentSignature) ]
                if affected <> 1 then
                    invalidOp $"Captured operation {operationId} stage '{stageName}' changed before finalisation"
            }
        operationIds |> Prelude.foldTask markOne ()

    let private completeCapturedOperation
        (scope: Algebra.TransactionScope)
        (currentSignature: string)
        (operationId: int64)
        : Task<unit> =
        task {
            let! _ =
                scope.execNonQuery
                    """UPDATE reflow_operations
                       SET lifecycle = 'completed', completed_at = datetime('now'), error = NULL
                       WHERE id = @id
                         AND lifecycle = 'running'
                         AND dag_signature = @signature
                         AND NOT EXISTS (
                             SELECT 1 FROM reflow_operation_stages
                             WHERE operation_id = @id
                               AND outcome IN ('pending', 'failed'))"""
                    [ ("@id", Database.boxVal operationId)
                      ("@signature", Database.boxVal currentSignature) ]
            return ()
        }

    let private completeCapturedOperations
        (scope: Algebra.TransactionScope)
        (currentSignature: string)
        (operationIds: int64 list)
        : Task<unit> =
        operationIds
        |> Prelude.foldTask
            (fun () operationId ->
                completeCapturedOperation scope currentSignature operationId)
            ()

    let private insertCompletionWhenUnblocked
        (scope: Algebra.TransactionScope)
        (docId: int64)
        (stageName: string)
        : Task<unit> =
        task {
            let! _ =
                scope.execNonQuery
                    """INSERT OR IGNORE INTO stage_completions (document_id, stage_name)
                       SELECT @doc, @stage
                       WHERE NOT EXISTS (
                           SELECT 1
                           FROM reflow_operation_stages ros
                           JOIN reflow_operations ro ON ro.id = ros.operation_id
                           WHERE ro.document_id = @doc
                             AND ro.lifecycle IN ('pending', 'running')
                             AND ros.stage_name = @stage
                             AND ros.outcome IN ('pending', 'failed'))"""
                    [ ("@doc", Database.boxVal docId)
                      ("@stage", Database.boxVal stageName) ]
            return ()
        }

    let private dismissDeadLetters
        (scope: Algebra.TransactionScope)
        (docId: int64)
        (stageName: string)
        : Task<unit> =
        task {
            let! _ =
                scope.execNonQuery
                    """UPDATE dead_letters
                       SET dismissed = 1
                       WHERE doc_id = @doc AND stage = @stage AND dismissed = 0"""
                    [ ("@doc", Database.boxVal docId)
                      ("@stage", Database.boxVal stageName) ]
            return ()
        }

    let private upsertActiveDeadLetter
        (scope: Algebra.TransactionScope)
        (docId: int64)
        (stageName: string)
        (error: string)
        : Task<unit> =
        task {
            let! _ =
                scope.execNonQuery
                    """INSERT INTO dead_letters
                         (doc_id, stage, error, retryable, failed_at, original_name)
                       SELECT @doc, @stage, @error, 1, datetime('now'), original_name
                       FROM documents WHERE id = @doc
                       ON CONFLICT(doc_id, stage) WHERE dismissed = 0 DO UPDATE SET
                         error = excluded.error,
                         retryable = 1,
                         failed_at = excluded.failed_at,
                         retry_count = dead_letters.retry_count + 1,
                         original_name = COALESCE(dead_letters.original_name, excluded.original_name)"""
                    [ ("@doc", Database.boxVal docId)
                      ("@stage", Database.boxVal stageName)
                      ("@error", Database.boxVal error) ]
            return ()
        }

    let private failCapturedOperation
        (scope: Algebra.TransactionScope)
        (currentSignature: string)
        (stageName: string)
        (error: string)
        (operationId: int64)
        : Task<unit> =
        task {
            let parameters =
                [ ("@operation", Database.boxVal operationId)
                  ("@stage", Database.boxVal stageName)
                  ("@signature", Database.boxVal currentSignature)
                  ("@error", Database.boxVal error) ]
            let! stageCount =
                scope.execNonQuery
                    """UPDATE reflow_operation_stages
                       SET outcome = 'failed', completed_at = datetime('now'), error = @error
                       WHERE operation_id = @operation
                         AND stage_name = @stage
                         AND outcome = 'pending'
                         AND EXISTS (
                             SELECT 1 FROM reflow_operations
                             WHERE id = @operation
                               AND lifecycle = 'running'
                               AND dag_signature = @signature)"""
                    parameters
            if stageCount <> 1 then
                invalidOp $"Captured operation {operationId} could not be failed"
            let! operationCount =
                scope.execNonQuery
                    """UPDATE reflow_operations
                       SET lifecycle = 'failed', completed_at = datetime('now'), error = @error
                       WHERE id = @operation
                         AND lifecycle = 'running'
                         AND dag_signature = @signature"""
                    parameters
            if operationCount <> 1 then
                invalidOp $"Captured operation {operationId} lifecycle could not be failed"
        }

    let private deleteAttemptWith
        (execNonQuery: string -> (string * obj) list -> Task<int>)
        (identity: StageAttemptIdentity)
        : Task<int> =
        execNonQuery
            """DELETE FROM pipeline_stage_attempts
               WHERE document_id = @doc
                 AND stage_name = @stage
                 AND attempt_token = @token"""
            [ ("@doc", Database.boxVal identity.DocumentId)
              ("@stage", Database.boxVal identity.StageName)
              ("@token", Database.boxVal identity.Token) ]

    let private releaseAttemptInTransaction
        (scope: Algebra.TransactionScope)
        (attempt: StageAttemptIdentity option)
        : Task<unit> =
        match attempt with
        | None -> Task.FromResult(())
        | Some identity ->
            task {
                let! affected = deleteAttemptWith scope.execNonQuery identity
                if affected <> 1 then
                    invalidOp
                        $"Stage '{identity.StageName}' attempt lease changed before finalisation"
            }

    let private discardStaleAttemptTransaction
        (stage: StageDefinition)
        (identity: StageAttemptIdentity)
        (scope: Algebra.TransactionScope)
        : Task<Result<unit, string>> =
        task {
            do!
                StageState.invalidate
                    scope identity.DocumentId stage.Name stage.OutputTable
            do!
                StageState.updateDocumentProjection
                    scope identity.DocumentId
            do! releaseAttemptInTransaction scope (Some identity)
            return Ok ()
        }

    let private cleanupAttempt
        (db: Algebra.Database)
        (identity: StageAttemptIdentity)
        : Task<unit> =
        task {
            let! _ = deleteAttemptWith db.execNonQuery identity
            return ()
        }

    let private releaseAttemptWhenSettled
        (db: Algebra.Database)
        (identity: StageAttemptIdentity)
        (work: Task<'a>)
        : Task<'a> =
        task {
            let pending: Task array = [| work :> Task |]
            let! _ = Task.WhenAny(pending)
            do! cleanupAttempt db identity
            return! work
        }

    let private finishSuccessTransaction
        currentSignature docId stageName outcome operationIds attempt
        (scope: Algebra.TransactionScope)
        : Task<Result<unit, string>> =
        task {
            do! markCapturedStageOutcome scope currentSignature operationIds stageName outcome
            do! completeCapturedOperations scope currentSignature operationIds
            do! insertCompletionWhenUnblocked scope docId stageName
            do! dismissDeadLetters scope docId stageName
            do! StageState.updateDocumentProjection scope docId
            do! releaseAttemptInTransaction scope attempt
            return Ok ()
        }

    let private finishFailureTransaction
        currentSignature docId stageName error operationIds attempt
        (scope: Algebra.TransactionScope)
        : Task<Result<unit, string>> =
        task {
            do! upsertActiveDeadLetter scope docId stageName error
            do!
                operationIds
                |> Prelude.foldTask
                    (fun () operationId ->
                        failCapturedOperation
                            scope currentSignature stageName error operationId)
                    ()
            do! releaseAttemptInTransaction scope attempt
            return Ok ()
        }

    let private captureResolution
        (captured: TaskCompletionSource<AttemptResolution>)
        (resolution: AttemptResolution)
        (work: Task<Result<unit, string>>)
        : Task<Result<unit, string>> =
        task {
            let! result = work
            if Result.isOk result then
                captured.TrySetResult(resolution) |> ignore
            return result
        }

    let private settleSuccessTransaction
        (currentSignature: string)
        (stage: StageDefinition)
        (attempt: StageAttempt)
        (outcome: StageOutcome)
        (captured: TaskCompletionSource<AttemptResolution>)
        (scope: Algebra.TransactionScope)
        : Task<Result<unit, string>> =
        task {
            let! current = Generation.isCurrentIn scope attempt.Generation
            if current then
                return!
                    finishSuccessTransaction
                        currentSignature attempt.Identity.DocumentId stage.Name
                        outcome attempt.OperationIds (Some attempt.Identity) scope
                    |> captureResolution captured (Applied (outcome = Completed))
            else
                return!
                    discardStaleAttemptTransaction stage attempt.Identity scope
                    |> captureResolution captured Superseded
        }

    let private settleFailureTransaction
        (currentSignature: string)
        (stage: StageDefinition)
        (attempt: StageAttempt)
        (error: string)
        (captured: TaskCompletionSource<AttemptResolution>)
        (scope: Algebra.TransactionScope)
        : Task<Result<unit, string>> =
        task {
            let! current = Generation.isCurrentIn scope attempt.Generation
            if current then
                return!
                    finishFailureTransaction
                        currentSignature attempt.Identity.DocumentId stage.Name
                        error attempt.OperationIds (Some attempt.Identity) scope
                    |> captureResolution captured (Applied false)
            else
                return!
                    discardStaleAttemptTransaction stage attempt.Identity scope
                    |> captureResolution captured Superseded
        }

    let private operationWasCompleted
        (db: Algebra.Database)
        (operationId: int64)
        : Task<bool> =
        task {
            let! value =
                db.execScalar
                    "SELECT id FROM reflow_operations WHERE id = @id AND lifecycle = 'completed'"
                    [ ("@id", Database.boxVal operationId) ]
            return match value with null -> false | _ -> true
        }

    let private logCompletedOperations
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (docId: int64)
        (operationIds: int64 list)
        : Task<unit> =
        let logOne () operationId =
            task {
                let! completed = operationWasCompleted db operationId
                if completed then
                    do!
                        ActivityLog.logInfo db "reflow"
                            $"Reflow operation {operationId} completed" (Some docId)
                    logger.info $"Reflow operation {operationId} completed"
            }
        operationIds |> Prelude.foldTask logOne ()

    let private recordFailure
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (docId: int64)
        (stageName: string)
        (error: string)
        : Task<unit> =
        task {
            do!
                ActivityLog.logError db "pipeline"
                    $"Stage '{stageName}' failed for doc {docId}" (Some docId) error
            logger.warn $"Stage '{stageName}' failed for doc {docId}: {error}"
        }

    let private requireCommit context = function
        | Ok () -> ()
        | Error error -> invalidOp $"{context}: {error}"

    let private settleAttempt
        (db: Algebra.Database)
        (context: string)
        (transaction:
            TaskCompletionSource<AttemptResolution> ->
                Algebra.TransactionScope ->
                Task<Result<unit, string>>)
        : Task<AttemptResolution> =
        task {
            let captured =
                TaskCompletionSource<AttemptResolution>(
                    TaskCreationOptions.RunContinuationsAsynchronously)
            let! result = db.inTransaction (transaction captured)
            requireCommit context result
            return! captured.Task
        }

    let private logSuperseded
        (logger: Algebra.Logger)
        (stage: StageDefinition)
        documentId =
        logger.warn
            $"Stage '{stage.Name}' output for doc {documentId} was superseded by reflow and discarded"

    /// Backward-compatible direct completion helper.
    let markCompleted
        (db: Algebra.Database)
        (docId: int64)
        (stageName: string)
        : Task<unit> =
        task {
            let! result =
                db.inTransaction
                    (finishSuccessTransaction
                        "" docId stageName Completed [] None)
            requireCommit $"Stage '{stageName}' completion failed" result
        }

    /// Backward-compatible direct failure helper.
    let markFailed
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (docId: int64)
        (stageName: string)
        (error: string)
        : Task<unit> =
        task {
            let! result =
                db.inTransaction
                    (finishFailureTransaction
                        "" docId stageName error [] None)
            requireCommit $"Stage '{stageName}' failure finalisation failed" result
            do! recordFailure db logger docId stageName error
        }

    let private finalizeSuccess
        (currentSignature: string)
        (stage: StageDefinition)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (attempt: StageAttempt)
        (outcome: StageOutcome)
        : Task<bool> =
        task {
            let identity = attempt.Identity
            let! resolution =
                settleAttempt
                    db
                    $"Stage '{stage.Name}' success finalisation failed"
                    (settleSuccessTransaction
                        currentSignature stage attempt outcome)
            match resolution with
            | Superseded ->
                logSuperseded logger stage identity.DocumentId
                return false
            | Applied counted ->
                do!
                    logCompletedOperations
                        db logger identity.DocumentId attempt.OperationIds
                return counted
        }

    let private finalizeFailure
        (currentSignature: string)
        (stage: StageDefinition)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (attempt: StageAttempt)
        (error: string)
        : Task<bool> =
        task {
            let identity = attempt.Identity
            let! resolution =
                settleAttempt
                    db
                    $"Stage '{stage.Name}' failure finalisation failed"
                    (settleFailureTransaction
                        currentSignature stage attempt error)
            match resolution with
            | Superseded ->
                logSuperseded logger stage identity.DocumentId
            | Applied _ ->
                do!
                    recordFailure
                        db logger identity.DocumentId stage.Name error
            return false
        }

    let private finalizeOutcome
        currentSignature stage db logger attempt outcome
        : Task<bool> =
        match outcome with
        | Completed
        | Skipped ->
            finalizeSuccess
                currentSignature stage db logger attempt outcome
        | Failed error ->
            finalizeFailure
                currentSignature stage db logger attempt error

    let private evaluateStage
        (stage: StageDefinition)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (attempt: StageAttempt)
        : Task<StageOutcome> =
        task {
            try
                let execution =
                    { DocumentId = attempt.Identity.DocumentId
                      Generation = attempt.Generation }
                let! shouldRun =
                    stage.Gate
                    |> Option.map (fun gate -> gate db execution.DocumentId)
                    |> Option.defaultValue (Task.FromResult true)
                if shouldRun then
                    return! stage.Process db logger execution
                else return Skipped
            with ex ->
                return Failed ex.Message
        }

    let private executeStageAttempt
        currentSignature stage db logger (attempt: StageAttempt)
        : Task<bool> =
        task {
            let! outcome =
                evaluateStage stage db logger attempt
            return!
                finalizeOutcome
                    (currentSignature |> Option.defaultValue "")
                    stage db logger attempt outcome
        }

    /// Evidence for an infrastructure fault. Never silent, never a dead letter:
    /// the stage itself did not fail, the finalisation did.
    let private recordAttemptFault
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (stageName: string)
        (docId: int64)
        (error: exn)
        : Task<unit> =
        task {
            logger.error
                $"Stage '{stageName}' finalisation faulted for doc {docId}: {error.Message}"
            try
                do!
                    ActivityLog.logError
                        db "pipeline"
                        $"Stage '{stageName}' finalisation faulted for doc {docId}; lease released, no completion or dead letter written"
                        (Some docId) (error.ToString())
            with logFailure ->
                logger.error
                    $"Stage '{stageName}' fault evidence could not be recorded for doc {docId}: {logFailure.Message}"
        }

    let private runAttempt
        (currentSignature: string option)
        (stage: StageDefinition)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (docId: int64)
        : Task<bool> =
        task {
            let! attempt =
                startStageAttempt currentSignature stage db docId
            match attempt with
            | None -> return false
            | Some active ->
                return!
                    executeStageAttempt
                        currentSignature stage db logger active
                    |> releaseAttemptWhenSettled db active.Identity
        }

    /// A faulted finalisation is an infrastructure failure, not a stage
    /// failure: the lease is already released, nothing false is written, the
    /// evidence is logged, and the document stays ready for the next cycle.
    /// Cancellation is never swallowed.
    let private processDocument
        (currentSignature: string option)
        (stage: StageDefinition)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (docId: int64)
        : Task<bool> =
        task {
            try
                return! runAttempt currentSignature stage db logger docId
            with error ->
                if error :? OperationCanceledException then
                    return raise error
                else
                    do! recordAttemptFault db logger stage.Name docId error
                    return false
        }

    // ── Phase executor ───────────────────────────────────────────────

    let private readyDocumentIds
        (stage: StageDefinition)
        (db: Algebra.Database)
        (maxDocs: int)
        : Task<int64 list> =
        task {
            let! rows = db.execReader (readyQuery stage maxDocs) []
            return
                rows
                |> List.choose (fun row -> (Prelude.RowReader row).OptInt64 "id")
        }

    let private acquireGpu
        (gpu: GpuScheduler)
        (stage: StageDefinition)
        (ct: CancellationToken)
        : Task<IDisposable option> =
        match stage.GpuModel with
        | Some model ->
            task {
                let! handle = gpu.Acquire model ct
                return Some handle
            }
        | None -> Task.FromResult<IDisposable option>(None)

    let private withGpu gpu stage ct (work: unit -> Task<'a>) : Task<'a> =
        task {
            let! handle = acquireGpu gpu stage ct
            try
                return! work ()
            finally
                handle |> Option.iter (fun value -> value.Dispose())
        }

    let private processReadyDocument
        (currentSignature: string option)
        (stage: StageDefinition)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (started: DateTime)
        (maxTime: TimeSpan)
        (ct: CancellationToken)
        (count: int)
        (docId: int64)
        : Task<int> =
        task {
            let expired = DateTime.UtcNow - started > maxTime
            if ct.IsCancellationRequested || expired then return count
            else
                let! completed =
                    processDocument currentSignature stage db logger docId
                return if completed then count + 1 else count
        }

    let private processReadyDocuments
        currentSignature stage db logger maxTime ct docIds
        : Task<int> =
        let started = DateTime.UtcNow
        docIds
        |> Prelude.foldTask
            (processReadyDocument
                currentSignature stage db logger started maxTime ct)
            0

    let private executeStageBatch
        (currentSignature: string option)
        (stage: StageDefinition)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (gpu: GpuScheduler)
        (maxTime: TimeSpan)
        (ct: CancellationToken)
        (docIds: int64 list)
        : Task<int> =
        task {
            logger.info $"Stage '{stage.Name}': {docIds.Length} docs ready"
            let! processed =
                withGpu gpu stage ct (fun () ->
                    processReadyDocuments
                        currentSignature stage db logger maxTime ct docIds)
            logger.info $"Stage '{stage.Name}': processed {processed} docs"
            return processed
        }

    let private processStageCore
        currentSignature stage db logger gpu maxDocs maxTime ct
        : Task<int> =
        task {
            let! docIds = readyDocumentIds stage db maxDocs
            if docIds.IsEmpty then return 0
            else
                return!
                    executeStageBatch
                        currentSignature stage db logger gpu maxTime ct docIds
        }

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
        processStageCore None stage db logger gpu maxDocs maxTime ct

    /// Process with the current DAG identity, including reflow work.
    let processStageForDag
        (dag: Dag)
        (stage: StageDefinition)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (gpu: GpuScheduler)
        (maxDocs: int)
        (maxTime: TimeSpan)
        (ct: CancellationToken)
        : Task<int> =
        processStageCore
            (Some (dagSignature dag))
            stage db logger gpu maxDocs maxTime ct

    // ── Main pipeline loop ───────────────────────────────────────────

    type private RunContext =
        { DagSignature: string
          Db: Algebra.Database
          Logger: Algebra.Logger
          Gpu: GpuScheduler
          MaxDocs: int
          MaxTime: TimeSpan
          CancellationToken: CancellationToken }

    let private processRunStage context total stage : Task<int> =
        task {
            if context.CancellationToken.IsCancellationRequested then
                return total
            else
                let! count =
                    processStageCore
                        (Some context.DagSignature)
                        stage context.Db context.Logger context.Gpu
                        context.MaxDocs context.MaxTime context.CancellationToken
                return total + count
        }

    let private processRunPhase context total (_, stages) : Task<int> =
        stages |> Prelude.foldTask (processRunStage context) total

    let private runCycle context (dag: Dag) : Task<int> =
        dag.Phases |> Prelude.foldTask (processRunPhase context) 0

    let private delayFor
        (interval: TimeSpan)
        (ct: CancellationToken)
        : Task<unit> =
        task {
            if interval > TimeSpan.Zero then
                try
                    do! Task.Delay(interval, ct)
                with :? OperationCanceledException ->
                    ()
        }

    let private delayIfIdle
        (interval: TimeSpan)
        (ct: CancellationToken)
        (processed: int)
        : Task<unit> =
        if processed = 0 then delayFor interval ct
        else Task.FromResult(())

    // ── Run-cycle fault isolation ────────────────────────────────────

    type private CycleOutcome =
        | CycleProcessed of documents: int
        | CycleCancelled
        | CycleFaulted

    type private CycleState = { ConsecutiveFaults: int }

    let private noFaults : CycleState = { ConsecutiveFaults = 0 }

    let private maxCycleBackoff = TimeSpan.FromMinutes 5.0

    /// Doubles the idle interval per consecutive fault and caps it, so a broken
    /// dependency can neither spin nor stall the loop forever.
    let private cycleBackoff (idleInterval: TimeSpan) (faults: int) : TimeSpan =
        let steps = faults |> max 1 |> min 6
        let ticks = idleInterval.Ticks * pown 2L (steps - 1)
        TimeSpan.FromTicks(min ticks maxCycleBackoff.Ticks)

    let private isCancellation (context: RunContext) (error: exn) : bool =
        context.CancellationToken.IsCancellationRequested
        && (error :? OperationCanceledException)

    let private recordCycleFault (context: RunContext) (error: exn) : Task<unit> =
        task {
            context.Logger.error $"Pipeline cycle failed: {error.Message}"
            try
                do!
                    ActivityLog.logError
                        context.Db "pipeline"
                        "Pipeline run cycle failed; backing off before the next cycle"
                        None (error.ToString())
            with logFailure ->
                context.Logger.error
                    $"Pipeline cycle fault could not be recorded: {logFailure.Message}"
        }

    let private runCycleSafely (context: RunContext) (dag: Dag) : Task<CycleOutcome> =
        task {
            try
                let! processed = runCycle context dag
                return CycleProcessed processed
            with error ->
                if isCancellation context error then
                    return CycleCancelled
                else
                    do! recordCycleFault context error
                    return CycleFaulted
        }

    let private nextCycleState (state: CycleState) (outcome: CycleOutcome) : CycleState =
        match outcome with
        | CycleFaulted -> { ConsecutiveFaults = state.ConsecutiveFaults + 1 }
        | CycleProcessed _
        | CycleCancelled -> noFaults

    let private pauseAfterCycle
        (context: RunContext)
        (idleInterval: TimeSpan)
        (outcome: CycleOutcome)
        (state: CycleState)
        : Task<unit> =
        match outcome with
        | CycleFaulted ->
            delayFor
                (cycleBackoff idleInterval state.ConsecutiveFaults)
                context.CancellationToken
        | CycleCancelled -> Task.FromResult(())
        | CycleProcessed processed ->
            delayIfIdle idleInterval context.CancellationToken processed

    let private runCycles
        (context: RunContext)
        (dag: Dag)
        (idleInterval: TimeSpan)
        : Task<unit> =
        task {
            let mutable state = noFaults
            while not context.CancellationToken.IsCancellationRequested do
                let! outcome = runCycleSafely context dag
                state <- nextCycleState state outcome
                do! pauseAfterCycle context idleInterval outcome state
        }

    let private runContext
        (dag: Dag)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (gpu: GpuScheduler)
        (maxDocs: int)
        (maxTime: TimeSpan)
        (ct: CancellationToken)
        : RunContext =
        { DagSignature = dagSignature dag
          Db = db
          Logger = logger
          Gpu = gpu
          MaxDocs = maxDocs
          MaxTime = maxTime
          CancellationToken = ct }

    /// Run the pipeline: cycle through phases, processing each stage.
    /// Each phase loads one GPU model and processes all ready work for that model.
    /// A faulted cycle is logged with ActivityLog evidence and retried after a
    /// bounded backoff; only cancellation ends the loop.
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
            let context =
                runContext dag db logger gpu maxDocsPerPhase maxTimePerPhase ct
            do! runCycles context dag idleInterval
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
        """
        CREATE TABLE IF NOT EXISTS pipeline_stage_attempts (
            document_id     INTEGER PRIMARY KEY REFERENCES documents(id) ON DELETE CASCADE,
            stage_name      TEXT NOT NULL CHECK (length(trim(stage_name)) > 0),
            attempt_token   TEXT NOT NULL UNIQUE CHECK (length(attempt_token) = 32),
            started_at      TEXT NOT NULL DEFAULT (datetime('now'))
        );
        """
        "CREATE INDEX IF NOT EXISTS idx_pipeline_stage_attempts_stage ON pipeline_stage_attempts(stage_name);"
    |]

    let private staleAttemptIdentities
        (db: Algebra.Database)
        : Task<StageAttemptIdentity list> =
        task {
            let! rows =
                db.execReader
                    """SELECT document_id, stage_name, attempt_token
                       FROM pipeline_stage_attempts"""
                    []
            return
                rows
                |> List.map (fun row ->
                    let reader = Prelude.RowReader(row)
                    { DocumentId = reader.Int64 "document_id" 0L
                      StageName = reader.String "stage_name" ""
                      Token = reader.String "attempt_token" "" })
        }

    let private recoverOne
        (db: Algebra.Database)
        (stages: Map<string, StageDefinition>)
        (count: int)
        (identity: StageAttemptIdentity)
        : Task<int> =
        task {
            let stage =
                stages
                |> Map.tryFind identity.StageName
                |> Option.defaultWith (fun () ->
                    invalidOp
                        $"Stale attempt refers to unknown stage '{identity.StageName}'")
            let! result =
                db.inTransaction
                    (discardStaleAttemptTransaction stage identity)
            requireCommit
                $"Stale attempt recovery for stage '{identity.StageName}' failed"
                result
            return count + 1
        }

    /// Cleans crashed-attempt output and releases its lease. This must complete
    /// before workers or API traffic are started.
    let recoverStaleAttempts
        (db: Algebra.Database)
        (stages: StageDefinition list)
        : Task<int> =
        task {
            let byName =
                stages
                |> List.map (fun stage -> stage.Name, stage)
                |> Map.ofList
            let! identities = staleAttemptIdentities db
            return!
                identities
                |> Prelude.foldTask (recoverOne db byName) 0
        }

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
