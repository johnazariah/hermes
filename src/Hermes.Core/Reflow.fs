namespace Hermes.Core

open System
open System.Text.RegularExpressions

/// Typed, DAG-validated planning and execution for pipeline reflow.
[<RequireQualifiedAccess>]
module Reflow =

    type OperationKind =
        | Reextract
        | Recomprehend
        | Reembed

    module OperationKind =
        let toString (kind: OperationKind) : string =
            match kind with
            | Reextract -> "reextract"
            | Recomprehend -> "recomprehend"
            | Reembed -> "reembed"

        let parse (s: string) : Result<OperationKind, string> =
            match s.Trim().ToLowerInvariant() with
            | "reextract" -> Ok Reextract
            | "recomprehend" -> Ok Recomprehend
            | "reembed" -> Ok Reembed
            | other -> Error $"Unknown OperationKind: '{other}'"

        let rootStage (kind: OperationKind) : string =
            match kind with
            | Reextract -> "extract"
            | Recomprehend -> "triage"
            | Reembed -> "embed"

        let expectedClosure (kind: OperationKind) : Set<string> =
            match kind with
            | Reextract -> set [ "extract"; "triage"; "deep-comprehend"; "embed" ]
            | Recomprehend -> set [ "triage"; "deep-comprehend" ]
            | Reembed -> set [ "embed" ]

    type RequestMode =
        | DryRun
        | Apply

    module RequestMode =
        let toString (mode: RequestMode) : string =
            match mode with
            | DryRun -> "dry_run"
            | Apply -> "apply"

        let parse (s: string) : Result<RequestMode, string> =
            match s.Trim().ToLowerInvariant() with
            | "dry-run" | "dryrun" | "dry_run" -> Ok DryRun
            | "apply" -> Ok Apply
            | other -> Error $"Unknown RequestMode: '{other}'"

    type StageOutcome =
        | Current
        | Pending
        | Reran
        | Failed
        | Skipped

    module StageOutcome =
        let toString (outcome: StageOutcome) : string =
            match outcome with
            | Current -> "current"
            | Pending -> "pending"
            | Reran -> "reran"
            | Failed -> "failed"
            | Skipped -> "skipped"

        let parse (s: string) : Result<StageOutcome, string> =
            match s.Trim().ToLowerInvariant() with
            | "current" -> Ok Current
            | "pending" -> Ok Pending
            | "reran" -> Ok Reran
            | "failed" -> Ok Failed
            | "skipped" -> Ok Skipped
            | other -> Error $"Unknown StageOutcome: '{other}'"

    type Lifecycle =
        | LifecyclePending
        | LifecycleRunning
        | LifecycleCompleted
        | LifecycleFailed

    module Lifecycle =
        let toString (lifecycle: Lifecycle) : string =
            match lifecycle with
            | LifecyclePending -> "pending"
            | LifecycleRunning -> "running"
            | LifecycleCompleted -> "completed"
            | LifecycleFailed -> "failed"

        let parse (s: string) : Result<Lifecycle, string> =
            match s.Trim().ToLowerInvariant() with
            | "pending" -> Ok LifecyclePending
            | "running" -> Ok LifecycleRunning
            | "completed" -> Ok LifecycleCompleted
            | "failed" -> Ok LifecycleFailed
            | other -> Error $"Unknown Lifecycle: '{other}'"

    type Plan =
        { DocumentId: int64
          Kind: OperationKind
          InvalidatedStages: string list
          CurrentStages: string list
          DagSignature: string }

    type StageStatus =
        { StageName: string
          Outcome: StageOutcome
          Error: string option }

    type OperationStatus =
        { OperationId: int64
          DocumentId: int64
          Kind: OperationKind
          Mode: RequestMode
          Lifecycle: Lifecycle
          DagSignature: string
          CreatedAt: string
          CompletedAt: string option
          Error: string option
          Stages: StageStatus list }

    let private outputTableRegex = Regex(@"^[A-Za-z0-9_]+$", RegexOptions.Compiled)

    let private descendantsClosure (dag: PipelineV5.Dag) (root: string) : Set<string> =
        let dependents =
            dag.Stages
            |> Map.toList
            |> List.collect (fun (_, stage) -> stage.DependsOn |> List.map (fun dep -> dep, stage.Name))
            |> List.groupBy fst
            |> List.map (fun (dep, edges) -> dep, edges |> List.map snd)
            |> Map.ofList

        let rec walk (frontier: string list) (visited: Set<string>) : Set<string> =
            match frontier with
            | [] -> visited
            | node :: rest when visited.Contains node -> walk rest visited
            | node :: rest ->
                let children = dependents |> Map.tryFind node |> Option.defaultValue []
                walk (children @ rest) (Set.add node visited)

        walk [ root ] Set.empty

    let private validateStage (dag: PipelineV5.Dag) (name: string) : Result<unit, string> =
        match dag.Stages |> Map.tryFind name with
        | None -> Error $"Affected stage '{name}' not found in DAG"
        | Some stage ->
            if String.IsNullOrWhiteSpace stage.OutputTable then
                Error $"Stage '{name}' has a blank output table"
            elif not (outputTableRegex.IsMatch stage.OutputTable) then
                Error $"Stage '{name}' output table '{stage.OutputTable}' has invalid characters"
            else
                Ok ()

    let private validateAffected (dag: PipelineV5.Dag) (names: string list) : Result<unit, string> =
        names
        |> List.fold (fun acc name -> acc |> Result.bind (fun () -> validateStage dag name)) (Ok ())

    let dagSignature = PipelineV5.dagSignature

    let plan (dag: PipelineV5.Dag) (documentId: int64) (kind: OperationKind) : Result<Plan, string> =
        let root = OperationKind.rootStage kind

        if not (dag.Stages.ContainsKey root) then
            Error $"Root stage '{root}' for {OperationKind.toString kind} not found in DAG"
        else
            let actual = descendantsClosure dag root
            let expected = OperationKind.expectedClosure kind

            if actual <> expected then
                Error
                    $"Closure mismatch for {OperationKind.toString kind}: \
                      expected {expected |> Set.toList |> List.sort}, \
                      got {actual |> Set.toList |> List.sort}"
            else
                validateAffected dag (Set.toList actual)
                |> Result.map (fun () ->
                    let invalidated = dag.Order |> List.filter actual.Contains
                    let current = dag.Order |> List.filter (actual.Contains >> not)

                    { DocumentId = documentId
                      Kind = kind
                      InvalidatedStages = invalidated
                      CurrentStages = current
                      DagSignature = dagSignature dag })

    open System.Threading.Tasks

    type RequestResult =
        { Plan: Plan
          Status: OperationStatus option
          Duplicate: bool }

    let private toStageStatus (dag: PipelineV5.Dag) (row: Map<string, obj>) : Result<StageStatus, string> =
        let r = Prelude.RowReader(row)
        let name = r.String "stage_name" ""
        if not (dag.Stages.ContainsKey name) then
            Error $"Stage '{name}' is not part of the current DAG"
        else
            StageOutcome.parse (r.String "outcome" "")
            |> Result.map (fun outcome ->
                { StageName = name
                  Outcome = outcome
                  Error = r.OptString "stage_error" })

    let private sequenceResults (xs: Result<'a, string> list) : Result<'a list, string> =
        List.foldBack
            (fun x acc -> acc |> Result.bind (fun rest -> x |> Result.map (fun v -> v :: rest)))
            xs (Ok [])

    let private validateStageCompleteness (dag: PipelineV5.Dag) (stageRows: Map<string, obj> list) : Result<unit, string> =
        let names = stageRows |> List.map (fun row -> (Prelude.RowReader row).String "stage_name" "")
        let counts = names |> List.countBy id |> Map.ofList
        let missing = dag.Order |> List.filter (fun name -> not (counts |> Map.containsKey name))
        let duplicated = dag.Order |> List.filter (fun name -> (counts |> Map.tryFind name |> Option.defaultValue 0) > 1)
        match missing, duplicated with
        | [], [] -> Ok ()
        | _ -> Error $"Stage rows incomplete: missing {missing}, duplicated {duplicated}"

    let private buildStatus (dag: PipelineV5.Dag) (opRow: Map<string, obj>) (stageRows: Map<string, obj> list) : Result<OperationStatus, string> =
        let r = Prelude.RowReader(opRow)
        let sig_ = r.String "dag_signature" ""
        let opId = r.Int64 "id" 0L
        if sig_ <> dagSignature dag then
            Error $"Operation {opId} DAG signature is stale for current pipeline"
        else
            validateStageCompleteness dag stageRows
            |> Result.bind (fun () ->
                OperationKind.parse (r.String "operation_kind" "")
                |> Result.bind (fun kind ->
                    RequestMode.parse (r.String "requested_mode" "")
                    |> Result.bind (fun mode ->
                        Lifecycle.parse (r.String "lifecycle" "")
                        |> Result.bind (fun lifecycle ->
                            stageRows
                            |> List.map (toStageStatus dag)
                            |> sequenceResults
                            |> Result.map (fun stages ->
                                { OperationId = opId
                                  DocumentId = r.Int64 "document_id" 0L
                                  Kind = kind
                                  Mode = mode
                                  Lifecycle = lifecycle
                                  DagSignature = sig_
                                  CreatedAt = r.String "created_at" ""
                                  CompletedAt = r.OptString "completed_at"
                                  Error = r.OptString "operation_error"
                                  Stages = stages })))))

    let private statusQuery =
        """SELECT ro.id, ro.document_id, ro.operation_kind, ro.requested_mode,
                  ro.lifecycle, ro.dag_signature, ro.created_at, ro.completed_at,
                  ro.error AS operation_error,
                  ros.stage_name, ros.outcome, ros.error AS stage_error
           FROM reflow_operations ro
           LEFT JOIN reflow_operation_stages ros ON ros.operation_id = ro.id
           WHERE ro.id = @id
           ORDER BY ros.stage_name"""

    let private statusStageRows (rows: Map<string, obj> list) =
        rows
        |> List.filter (fun row ->
            (Prelude.RowReader row).OptString "stage_name" |> Option.isSome)

    let getStatus (dag: PipelineV5.Dag) (db: Algebra.Database) (operationId: int64) : Task<Result<OperationStatus, string>> =
        task {
            let! rows =
                db.execReader statusQuery [ ("@id", Database.boxVal operationId) ]
            match rows with
            | [] -> return Error $"Operation {operationId} not found"
            | opRow :: _ ->
                return buildStatus dag opRow (statusStageRows rows)
        }

    let getLatestForDocument
        (dag: PipelineV5.Dag)
        (db: Algebra.Database)
        (documentId: int64)
        (kind: OperationKind)
        : Task<Result<OperationStatus option, string>> =
        task {
            let! rows =
                db.execReader
                    """SELECT id FROM reflow_operations
                       WHERE document_id = @doc AND operation_kind = @kind
                       ORDER BY id DESC LIMIT 1"""
                    [ ("@doc", Database.boxVal documentId)
                      ("@kind", Database.boxVal (OperationKind.toString kind)) ]
            match rows |> List.tryHead |> Option.bind (fun row -> (Prelude.RowReader row).OptInt64 "id") with
            | None -> return Ok None
            | Some opId ->
                let! status = getStatus dag db opId
                return status |> Result.map Some
        }

    type private DocumentResources =
        { ComprehensionFolder: PublicationFence.ArtifactFolder option }

    let private documentResources
        (db: Algebra.Database)
        (documentId: int64)
        : Task<DocumentResources option> =
        task {
            let! rows =
                db.execReader
                    "SELECT saved_path, folder_path FROM documents WHERE id = @id"
                    [ ("@id", Database.boxVal documentId) ]
            return
                rows
                |> List.tryHead
                |> Option.map (fun row ->
                    let reader = Prelude.RowReader(row)
                    { ComprehensionFolder =
                        PublicationFence.ArtifactFolder.tryFromMetadata
                            PublicationFence.UnknownArchiveRoot
                            (reader.String "saved_path" "")
                            (reader.OptString "folder_path") })
        }

    let private findOperationIdWith
        (execReader:
            string ->
            (string * obj) list ->
            Task<Map<string, obj> list>)
        (documentId: int64)
        (kind: OperationKind)
        (mode: RequestMode)
        (currentDagSignature: string)
        (lifecycles: string list)
        : Task<int64 option> =
        task {
            let placeholders = lifecycles |> List.mapi (fun i _ -> $"@lc{i}") |> String.concat ","
            let ps =
                [ ("@doc", Database.boxVal documentId)
                  ("@kind", Database.boxVal (OperationKind.toString kind))
                  ("@mode", Database.boxVal (RequestMode.toString mode))
                  ("@signature", Database.boxVal currentDagSignature) ]
                @ (lifecycles |> List.mapi (fun i lc -> ($"@lc{i}", Database.boxVal lc)))
            let! rows =
                execReader
                    $"""SELECT id FROM reflow_operations
                        WHERE document_id = @doc
                          AND operation_kind = @kind
                          AND requested_mode = @mode
                           AND dag_signature = @signature
                          AND lifecycle IN ({placeholders})
                        ORDER BY id DESC LIMIT 1"""
                    ps
            return rows |> List.tryHead |> Option.bind (fun row -> (Prelude.RowReader row).OptInt64 "id")
        }

    let private findActiveOperationId
        (db: Algebra.Database)
        (plan: Plan)
        (mode: RequestMode)
        : Task<int64 option> =
        findOperationIdWith
            db.execReader plan.DocumentId plan.Kind mode plan.DagSignature
            [ "pending"; "running" ]

    let private findLatestFailedOperationId
        (scope: Algebra.TransactionScope)
        (plan: Plan)
        (mode: RequestMode)
        : Task<int64 option> =
        findOperationIdWith
            scope.execReader plan.DocumentId plan.Kind mode plan.DagSignature
            [ "failed" ]

    let private insertOperation
        (scope: Algebra.TransactionScope)
        (plan: Plan)
        (mode: RequestMode)
        : Task<int64> =
        task {
            let! idObj =
                scope.execScalar
                    """INSERT INTO reflow_operations (document_id, operation_kind, requested_mode, lifecycle, dag_signature)
                       VALUES (@doc, @kind, @mode, 'pending', @sig)
                       RETURNING id"""
                    [ ("@doc", Database.boxVal plan.DocumentId)
                      ("@kind", Database.boxVal (OperationKind.toString plan.Kind))
                      ("@mode", Database.boxVal (RequestMode.toString mode))
                      ("@sig", Database.boxVal plan.DagSignature) ]
            return
                match idObj with
                | :? int64 as id -> id
                | _ -> invalidOp "SQLite INSERT RETURNING did not return an operation ID"
        }

    let private reuseOperation
        (scope: Algebra.TransactionScope)
        (opId: int64)
        (plan: Plan)
        (mode: RequestMode)
        : Task<unit> =
        task {
            let! affected =
                scope.execNonQuery
                    """UPDATE reflow_operations
                       SET requested_mode = @mode, lifecycle = 'pending', dag_signature = @sig,
                           completed_at = NULL, error = NULL
                       WHERE id = @id AND requested_mode = @mode AND lifecycle = 'failed'"""
                    [ ("@mode", Database.boxVal (RequestMode.toString mode))
                      ("@sig", Database.boxVal plan.DagSignature)
                      ("@id", Database.boxVal opId) ]
            if affected <> 1 then
                invalidOp $"Failed operation {opId} was no longer reusable"
        }

    let private obtainOperation
        (scope: Algebra.TransactionScope)
        (plan: Plan)
        (mode: RequestMode)
        : Task<int64> =
        task {
            let! failedId =
                findLatestFailedOperationId scope plan mode
            match failedId with
            | Some opId ->
                do! reuseOperation scope opId plan mode
                return opId
            | None -> return! insertOperation scope plan mode
        }

    let private stageRowSql =
        """INSERT INTO reflow_operation_stages (operation_id, stage_name, outcome, started_at, completed_at, error)
           VALUES (@op, @stage, @outcome, NULL, NULL, NULL)
           ON CONFLICT(operation_id, stage_name) DO UPDATE SET
             outcome = excluded.outcome, started_at = NULL, completed_at = NULL, error = NULL"""

    let private upsertStageRows
        (scope: Algebra.TransactionScope)
        (dag: PipelineV5.Dag)
        (plan: Plan)
        (opId: int64)
        : Task<unit> =
        let outcomeFor name = if plan.InvalidatedStages |> List.contains name then Pending else Current
        let upsertOne () name =
            task {
                let! _ =
                    scope.execNonQuery
                        stageRowSql
                        [ ("@op", Database.boxVal opId)
                          ("@stage", Database.boxVal name)
                          ("@outcome", Database.boxVal (StageOutcome.toString (outcomeFor name))) ]
                ()
            }
        dag.Order |> Prelude.foldTask upsertOne ()

    let private invalidateStage
        (dag: PipelineV5.Dag)
        (plan: Plan)
        (scope: Algebra.TransactionScope)
        ()
        (stageName: string)
        : Task<unit> =
        let stage = dag.Stages.[stageName]
        StageState.invalidate
            scope plan.DocumentId stage.Name stage.OutputTable

    let private invalidate
        (dag: PipelineV5.Dag)
        (plan: Plan)
        (scope: Algebra.TransactionScope)
        : Task<unit> =
        task {
            do!
                plan.InvalidatedStages
                |> Prelude.foldTask
                    (invalidateStage dag plan scope)
                    ()
            do!
                StageState.updateDocumentProjection
                    scope plan.DocumentId
        }

    let private markRunning
        (scope: Algebra.TransactionScope)
        (opId: int64)
        : Task<unit> =
        task {
            let! affected =
                scope.execNonQuery
                    """UPDATE reflow_operations
                       SET lifecycle = 'running', completed_at = NULL, error = NULL
                       WHERE id = @id AND lifecycle = 'pending'"""
                    [ ("@id", Database.boxVal opId) ]
            if affected <> 1 then
                invalidOp $"Operation {opId} could not transition to running"
        }

    let private recordRunningActivity
        (scope: Algebra.TransactionScope)
        (opId: int64)
        (plan: Plan)
        : Task<unit> =
        task {
            let! _ =
                scope.execNonQuery
                    """INSERT INTO activity_log (level, category, message, document_id)
                       VALUES ('info', 'reflow', @message, @doc)"""
                    [ ("@message",
                       Database.boxVal
                           $"Reflow {OperationKind.toString plan.Kind} running (op {opId})")
                      ("@doc", Database.boxVal plan.DocumentId) ]
            return ()
        }

    let private duplicateResult (dag: PipelineV5.Dag) (db: Algebra.Database) (plan: Plan) (opId: int64) : Task<Result<RequestResult, string>> =
        task {
            let! status = getStatus dag db opId
            return status |> Result.map (fun s -> { Plan = plan; Status = Some s; Duplicate = true })
        }

    type private AcceptOutcome =
        | Accepted of int64
        | Coalesced of int64

    let private findActiveOperationIdIn
        (scope: Algebra.TransactionScope)
        (plan: Plan)
        (mode: RequestMode)
        : Task<int64 option> =
        findOperationIdWith
            scope.execReader plan.DocumentId plan.Kind mode
            plan.DagSignature [ "pending"; "running" ]

    let private acceptNewRequest
        (dag: PipelineV5.Dag)
        (plan: Plan)
        (mode: RequestMode)
        (captured: TaskCompletionSource<AcceptOutcome>)
        (scope: Algebra.TransactionScope)
        : Task<Result<unit, string>> =
        task {
            let! opId = obtainOperation scope plan mode
            do! upsertStageRows scope dag plan opId
            do! markRunning scope opId
            let! _ = Generation.bump scope plan.DocumentId
            do! invalidate dag plan scope
            do! recordRunningActivity scope opId plan
            captured.TrySetResult(Accepted opId) |> ignore
            return Ok ()
        }

    let private setupApply
        (dag: PipelineV5.Dag)
        (plan: Plan)
        (mode: RequestMode)
        (captured: TaskCompletionSource<AcceptOutcome>)
        (scope: Algebra.TransactionScope)
        : Task<Result<unit, string>> =
        task {
            do!
                PipelineV5.retireStaleOperations
                    scope plan.DocumentId plan.DagSignature
            let! active = findActiveOperationIdIn scope plan mode
            match active with
            | Some opId ->
                captured.TrySetResult(Coalesced opId) |> ignore
                return Ok ()
            | None ->
                return!
                    acceptNewRequest
                        dag plan mode captured scope
        }

    let private committedResult
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (dag: PipelineV5.Dag)
        (plan: Plan)
        (opId: int64)
        : Task<Result<RequestResult, string>> =
        task {
            logger.info
                $"Reflow op {opId} ({OperationKind.toString plan.Kind}) invalidated for doc {plan.DocumentId}"
            let! status = getStatus dag db opId
            return
                status
                |> Result.map (fun value ->
                    { Plan = plan
                      Status = Some value
                      Duplicate = false })
        }

    let private settledResult
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (dag: PipelineV5.Dag)
        (plan: Plan)
        (outcome: AcceptOutcome)
        : Task<Result<RequestResult, string>> =
        match outcome with
        | Coalesced opId -> duplicateResult dag db plan opId
        | Accepted opId -> committedResult db logger dag plan opId

    let private failedApplyResult
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (dag: PipelineV5.Dag)
        (plan: Plan)
        (mode: RequestMode)
        (error: string)
        : Task<Result<RequestResult, string>> =
        task {
            let! raced = findActiveOperationId db plan mode
            match raced with
            | Some opId -> return! duplicateResult dag db plan opId
            | None ->
                logger.error
                    $"Reflow {OperationKind.toString plan.Kind} request rejected for doc {plan.DocumentId}: {error}"
                return Error error
        }

    let private applyAtomic
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (dag: PipelineV5.Dag)
        (plan: Plan)
        (mode: RequestMode)
        (folder: PublicationFence.ArtifactFolder)
        : Task<Result<RequestResult, string>> =
        task {
            let captured =
                TaskCompletionSource<AcceptOutcome>(
                    TaskCreationOptions.RunContinuationsAsynchronously)
            // The generation bump and its invalidation take the same fence as
            // every shared-sidecar publisher, so acceptance can never land
            // between a publisher's generation check and its file replacement.
            let! result =
                Generation.fencedArtifact plan.DocumentId folder (fun () ->
                    db.inTransaction (setupApply dag plan mode captured))
            match result with
            | Ok () ->
                let! outcome = captured.Task
                return! settledResult db logger dag plan outcome
            | Error error ->
                return! failedApplyResult db logger dag plan mode error
        }

    let private applyRequest
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (dag: PipelineV5.Dag)
        (plan: Plan)
        (mode: RequestMode)
        (folder: PublicationFence.ArtifactFolder)
        : Task<Result<RequestResult, string>> =
        applyAtomic db logger dag plan mode folder

    let request
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (dag: PipelineV5.Dag)
        (documentId: int64)
        (kind: OperationKind)
        (mode: RequestMode)
        : Task<Result<RequestResult, string>> =
        task {
            match plan dag documentId kind with
            | Error e -> return Error e
            | Ok p ->
                let! resources = documentResources db documentId
                match resources with
                | None ->
                    return Error $"Document {documentId} not found"
                | Some _ when mode = DryRun ->
                    return Ok { Plan = p; Status = None; Duplicate = false }
                | Some { ComprehensionFolder = None } ->
                    return
                        Error
                            $"Document {documentId} has no usable folder for thread.comprehension.json"
                | Some { ComprehensionFolder = Some folder } ->
                    return! applyRequest db logger dag p mode folder
        }
