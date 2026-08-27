module Hermes.Tests.ReflowTests

#nowarn "3261"

open System
open System.IO
open System.Threading.Tasks
open Xunit
open FsCheck.Xunit
open Hermes.Core

let private logger = TestHelpers.silentLogger
let private allStageNames = [ "extract"; "triage"; "deep-comprehend"; "embed" ]
let private stageOutputTables = [ "extraction"; "triage"; "comprehension"; "embedding" ]
let private allKinds = [| Reflow.Reextract; Reflow.Recomprehend; Reflow.Reembed |]

let private kindOf (seed: int) : Reflow.OperationKind =
    allKinds.[int (abs (int64 seed) % int64 allKinds.Length)]

let private freshDb () : Task<Algebra.Database> =
    task {
        let db = TestHelpers.createDb ()
        do! TestHelpers.initV5 db
        return db
    }

type private FileDatabases =
    { Writer: Algebra.Database
      Observer: Algebra.Database
      Directory: string }

let private freshFileDatabases () : Task<FileDatabases> =
    task {
        let directory =
            Path.Combine(Path.GetTempPath(), $"hermes-reflow-{Guid.NewGuid():N}")
        Directory.CreateDirectory(directory) |> ignore
        let writer = Database.fromPath (Path.Combine(directory, "reflow.sqlite"))
        try
            match! writer.initSchema () with
            | Error error -> return failwith error
            | Ok () ->
                do! TestHelpers.initV5 writer
                let observer =
                    Database.fromPath (Path.Combine(directory, "reflow.sqlite"))
                return { Writer = writer; Observer = observer; Directory = directory }
        with ex ->
            writer.dispose ()
            if Directory.Exists(directory) then Directory.Delete(directory, true)
            return raise ex
    }

let private cleanupFileDatabases (databases: FileDatabases) : unit =
    databases.Observer.dispose ()
    databases.Writer.dispose ()
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
    if Directory.Exists(databases.Directory) then
        Directory.Delete(databases.Directory, true)

let private toInt64 (o: obj) : int64 =
    match o with
    | null -> 0L
    | :? int64 as i -> i
    | :? int as i -> int64 i
    | _ -> 0L

let private scalarInt64 (db: Algebra.Database) (sql: string) (ps: (string * obj) list) : Task<int64> =
    task {
        let! result = db.execScalar sql ps
        return toInt64 result
    }

let private scalarStr (db: Algebra.Database) sql ps =
    task {
        let! result = db.execScalar sql ps
        return match result with null -> "" | v -> string v
    }

let private countRows (db: Algebra.Database) (table: string) (docId: int64) : Task<int64> =
    scalarInt64 db $"SELECT count(*) FROM {table} WHERE document_id = @doc" [ ("@doc", Database.boxVal docId) ]

let private documentGeneration
    (db: Algebra.Database)
    (docId: int64)
    : Task<int64> =
    scalarInt64 db
        """SELECT COALESCE(
             (SELECT generation FROM document_generations
              WHERE document_id = @doc), 0)"""
        [ ("@doc", Database.boxVal docId) ]

let private readDocument (db: Algebra.Database) (docId: int64) : Task<Map<string, obj>> =
    task {
        let! rows = db.execReader "SELECT * FROM documents WHERE id = @id" [ ("@id", Database.boxVal docId) ]
        return rows |> List.exactlyOne
    }

let private stageCompletionNames (db: Algebra.Database) (docId: int64) : Task<Set<string>> =
    task {
        let! rows =
            db.execReader
                "SELECT stage_name FROM stage_completions WHERE document_id = @doc"
                [ ("@doc", Database.boxVal docId) ]
        return rows |> List.map (fun row -> (Prelude.RowReader row).String "stage_name" "") |> Set.ofList
    }

let private completionTime db docId stageName =
    scalarStr db
        """SELECT completed_at FROM stage_completions
           WHERE document_id = @doc AND stage_name = @stage"""
        [ ("@doc", Database.boxVal docId)
          ("@stage", Database.boxVal stageName) ]

let private documentInsertSql =
    """INSERT INTO documents
         (stage, source_type, account, sender, subject, email_date, original_name,
          saved_path, source_path, size_bytes, category, sha256, extracted_date, extracted_amount,
          extracted_vendor, extracted_abn, ocr_confidence, extraction_method,
          extraction_confidence, classification_tier, classification_confidence,
          extracted_at, embedded_at, chunk_count)
       VALUES
         ('embedded', 'email_attachment', 'test', 'billing@telstra.com', 'Invoice',
          '2025-03-01', 'invoice.pdf', '/archive/invoice.pdf', '/source/invoice.pdf', 4096,
          'invoices', 'deadbeef', '2025-03-01', 89.5, 'Telstra', '12345678901', 0.95,
          'pdf-text', 0.9, 'high', 0.88, '2025-03-02T00:00:00Z',
          '2025-03-02T00:00:00Z', 1)"""

let private insertStageCompletions (db: Algebra.Database) (docId: int64) : Task<unit> =
    let insertOne () stageName =
        task {
            let! _ =
                db.execNonQuery
                    "INSERT INTO stage_completions (document_id, stage_name) VALUES (@doc, @stage)"
                    [ ("@doc", Database.boxVal docId); ("@stage", Database.boxVal stageName) ]
            ()
        }
    allStageNames |> Prelude.foldTask insertOne ()

let private insertStageArtifacts (db: Algebra.Database) (docId: int64) : Task<unit> =
    let insertSql table =
        match table with
        | "extraction" -> "INSERT INTO extraction (document_id) VALUES (@doc)"
        | "triage" ->
            "INSERT INTO triage (document_id,document_type,category,confidence) VALUES (@doc,'invoice','invoices',0.9)"
        | "comprehension" ->
            "INSERT INTO comprehension (document_id,document_type,category,confidence) VALUES (@doc,'invoice','invoices',0.9)"
        | "embedding" -> "INSERT INTO embedding (document_id,chunk_count) VALUES (@doc,1)"
        | other -> failwith $"Unknown stage output table: {other}"
    let insertOne () table =
        task {
            let! _ =
                db.execNonQuery (insertSql table) [ ("@doc", Database.boxVal docId) ]
            ()
        }
    stageOutputTables |> Prelude.foldTask insertOne ()

let private insertChunkTagAndCorrection (db: Algebra.Database) (docId: int64) : Task<unit> =
    task {
        let! _ =
            db.execNonQuery
                """INSERT INTO document_chunks (document_id, chunk_index, chunk_text, embedded_at)
                   VALUES (@doc, 0, 'Invoice total', datetime('now'))"""
                [ ("@doc", Database.boxVal docId) ]
        let! _ =
            db.execNonQuery
                "INSERT INTO tags (document_id, tag, source) VALUES (@doc, 'urgent', 'user')"
                [ ("@doc", Database.boxVal docId) ]
        let! _ =
            db.execNonQuery
                """INSERT INTO corrections (document_id, field, original_value, corrected_value)
                   VALUES (@doc, 'category', 'unclassified', 'invoices')"""
                [ ("@doc", Database.boxVal docId) ]
        ()
    }

let private insertFullyCompletedDocument (db: Algebra.Database) : Task<int64> =
    task {
        let! _ = db.execNonQuery documentInsertSql []
        let! idObj = db.execScalar "SELECT last_insert_rowid()" []
        let docId = toInt64 idObj
        do! insertStageCompletions db docId
        do! insertStageArtifacts db docId
        do! insertChunkTagAndCorrection db docId
        return docId
    }

[<Property>]
let ``Reflow_Plan_ClosureExactlyMatchesExpectedAndSignatureIsDeterministic``
    (docIdSeed: int)
    (kindSeed: int)
    : bool =
    let dagA = TestHelpers.standardV5Dag ()
    let dagB = TestHelpers.standardV5Dag ()
    let docId = abs (int64 docIdSeed) + 1L
    let kind = kindOf kindSeed

    match Reflow.plan dagA docId kind with
    | Error _ -> false
    | Ok p ->
        let expected = Reflow.OperationKind.expectedClosure kind
        let allStages = dagA.Order |> Set.ofList
        let invalidated = p.InvalidatedStages |> Set.ofList
        let current = p.CurrentStages |> Set.ofList
        invalidated = expected
        && current = Set.difference allStages expected
        && Set.intersect invalidated current = Set.empty
        && (p.InvalidatedStages @ p.CurrentStages |> List.sort) = (dagA.Order |> List.sort)
        && p.DocumentId = docId
        && p.Kind = kind
        && p.DagSignature = Reflow.dagSignature dagB

let private extraDescendantStage : PipelineV5.StageDefinition =
    { Name = "summarize"
      DependsOn = [ "extract" ]
      OutputTable = "summary"
      Schema = "CREATE TABLE IF NOT EXISTS summary (document_id INTEGER PRIMARY KEY REFERENCES documents(id))"
      Process = fun _ _ _ -> task { return PipelineV5.Completed }
      Gate = None
      GpuModel = None
      Mode = PipelineV5.Channel
      Concurrency = 1 }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Reflow_Plan_UndeclaredDescendantOfRoot_FailsClosed`` () =
    match PipelineV5.buildDag (TestHelpers.standardV5Stages @ [ extraDescendantStage ]) with
    | Error e -> failwith e
    | Ok dag ->
        match Reflow.plan dag 1L Reflow.Reextract with
        | Error msg -> Assert.Contains("Closure mismatch", msg)
        | Ok _ -> failwith "Expected fail-closed plan"

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_Request_DryRun_MutatesNothing`` () =
    task {
        let! db = freshDb ()
        try
            let! docId = insertFullyCompletedDocument db
            let dag = TestHelpers.standardV5Dag ()
            let! before = readDocument db docId
            let! result = Reflow.request db logger dag docId Reflow.Reextract Reflow.DryRun
            match result with
            | Error e -> failwith e
            | Ok r ->
                Assert.True(r.Status.IsNone)
                Assert.False(r.Duplicate)
                Assert.Equal(4, r.Plan.InvalidatedStages.Length)
            let! after = readDocument db docId
            Assert.Equal<Map<string, obj>>(before, after)
            let! opCount = scalarInt64 db "SELECT count(*) FROM reflow_operations" []
            Assert.Equal(0L, opCount)
            let! completions = stageCompletionNames db docId
            Assert.Equal<Set<string>>(Set.ofList allStageNames, completions)
            let! chunks = countRows db "document_chunks" docId
            Assert.Equal(1L, chunks)
        finally db.dispose ()
    }

let private markManualClassification (db: Algebra.Database) (docId: int64) : Task<unit> =
    task {
        let! _ =
            db.execNonQuery
                """UPDATE documents SET classification_tier = 'manual', classification_confidence = 0.99,
                     category = 'manual-review' WHERE id = @doc"""
                [ ("@doc", Database.boxVal docId) ]
        ()
    }

let private throwingTransactionDb (baseDb: Algebra.Database) (faultSql: string) : Algebra.Database =
    let faultyScope (scope: Algebra.TransactionScope) : Algebra.TransactionScope =
        { scope with
            execNonQuery =
                fun sql ps ->
                    if sql.Contains(faultSql) then task { return failwith "Injected transaction fault" }
                    else scope.execNonQuery sql ps }
    { baseDb with
        inTransaction = fun callback -> baseDb.inTransaction (fun scope -> callback (faultyScope scope)) }

let private expectInvalidOperation (work: unit -> Task<'a>) : Task<string> =
    task {
        try
            let! _ = work ()
            return failwith "Expected InvalidOperationException"
        with :? InvalidOperationException as error ->
            return error.Message
    }

let private isOperationInsert (sql: string) : bool =
    sql.TrimStart().StartsWith("INSERT INTO reflow_operations ", System.StringComparison.Ordinal)

let private interleavingInsertDb
    (baseDb: Algebra.Database)
    (competingDocId: int64)
    (dagSignature: string)
    : Algebra.Database =
    let insertCompeting (scope: Algebra.TransactionScope) =
        task {
            let! _ =
                scope.execNonQuery
                    """INSERT INTO reflow_operations
                         (document_id, operation_kind, requested_mode, lifecycle, dag_signature)
                       VALUES (@doc, 'reembed', 'apply', 'completed', @sig)"""
                    [ ("@doc", Database.boxVal competingDocId)
                      ("@sig", Database.boxVal dagSignature) ]
            return ()
        }
    { baseDb with
        inTransaction =
            fun callback ->
                baseDb.inTransaction (fun scope ->
                    let decoratedScope =
                        { scope with
                            execScalar =
                                fun sql ps ->
                                    task {
                                        let! result = scope.execScalar sql ps
                                        if isOperationInsert sql then do! insertCompeting scope
                                        return result
                                    } }
                    callback decoratedScope) }

let private pauseBeforeCommit
    (baseDb: Algebra.Database)
    (entered: TaskCompletionSource<unit>)
    (release: TaskCompletionSource<unit>)
    : Algebra.Database =
    { baseDb with
        inTransaction =
            fun callback ->
                baseDb.inTransaction (fun scope ->
                    task {
                        let! result = callback scope
                        entered.TrySetResult() |> ignore
                        do! release.Task
                        return result
                    }) }

let private pauseAfterOperationRead
    (baseDb: Algebra.Database)
    (entered: TaskCompletionSource<unit>)
    (release: TaskCompletionSource<unit>)
    : Algebra.Database =
    { baseDb with
        execReader =
            fun sql parameters ->
                task {
                    let! rows = baseDb.execReader sql parameters
                    if sql.Contains("FROM reflow_operations", StringComparison.Ordinal) then
                        entered.TrySetResult() |> ignore
                        do! release.Task
                    return rows
                } }

type private PreCommitObservation =
    { CompletedWithoutCommit: bool
      OperationCount: int64 }

let private observeBeforeCommit
    (readTask: Task<int64>)
    (release: TaskCompletionSource<unit>)
    : Task<PreCommitObservation> =
    task {
        let timeout = Task.Delay(TimeSpan.FromSeconds 2.0)
        let! completed = Task.WhenAny(readTask :> Task, timeout)
        let readCompleted = Object.ReferenceEquals(completed, readTask)
        let count = if readCompleted then readTask.Result else -1L
        release.TrySetResult() |> ignore
        return { CompletedWithoutCommit = readCompleted; OperationCount = count }
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_Request_Apply_Recomprehend_PreservesExtractionAndEmbedding`` () =
    task {
        let! db = freshDb ()
        try
            let! docId = insertFullyCompletedDocument db
            let! result =
                Reflow.request db logger (TestHelpers.standardV5Dag ()) docId Reflow.Recomprehend Reflow.Apply
            match result with Error e -> failwith e | Ok r -> Assert.False(r.Duplicate)
            let! completions = stageCompletionNames db docId
            Assert.Equal<Set<string>>(Set.ofList [ "extract"; "embed" ], completions)
            let! extractionCount = countRows db "extraction" docId
            let! embeddingCount = countRows db "embedding" docId
            let! triageCount = countRows db "triage" docId
            let! comprehensionCount = countRows db "comprehension" docId
            Assert.Equal(1L, extractionCount)
            Assert.Equal(1L, embeddingCount)
            Assert.Equal(0L, triageCount)
            Assert.Equal(0L, comprehensionCount)
            let! chunks = countRows db "document_chunks" docId
            Assert.Equal(1L, chunks)
            let! doc = readDocument db docId
            let r = Prelude.RowReader(doc)
            Assert.Equal("2025-03-02T00:00:00Z", r.String "extracted_at" "")
            Assert.Equal("2025-03-02T00:00:00Z", r.String "embedded_at" "")
            Assert.Equal(89.5, (r.OptFloat "extracted_amount").Value, 3)
            Assert.Equal("extracted", r.String "stage" "")
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_Request_Apply_Reembed_InvalidatesEmbeddingOnly`` () =
    task {
        let! db = freshDb ()
        try
            let! docId = insertFullyCompletedDocument db
            let! result =
                Reflow.request db logger (TestHelpers.standardV5Dag ()) docId Reflow.Reembed Reflow.Apply
            match result with Error e -> failwith e | Ok r -> Assert.False(r.Duplicate)
            let! completions = stageCompletionNames db docId
            Assert.Equal<Set<string>>(Set.ofList [ "extract"; "triage"; "deep-comprehend" ], completions)
            let! extractionCount = countRows db "extraction" docId
            let! triageCount = countRows db "triage" docId
            let! comprehensionCount = countRows db "comprehension" docId
            let! embeddingCount = countRows db "embedding" docId
            Assert.Equal(1L, extractionCount)
            Assert.Equal(1L, triageCount)
            Assert.Equal(1L, comprehensionCount)
            Assert.Equal(0L, embeddingCount)
            let! chunks = countRows db "document_chunks" docId
            Assert.Equal(0L, chunks)
            let! doc = readDocument db docId
            let r = Prelude.RowReader(doc)
            Assert.Equal("2025-03-02T00:00:00Z", r.String "extracted_at" "")
            Assert.True((r.OptString "embedded_at").IsNone)
            Assert.Equal(89.5, (r.OptFloat "extracted_amount").Value, 3)
            Assert.Equal("understood", r.String "stage" "")
        finally db.dispose ()
    }

[<Theory>]
[<InlineData("reextract")>]
[<InlineData("recomprehend")>]
[<Trait("Category", "Integration")>]
let ``Reflow_Request_Apply_ManualClassification_IsPreserved`` (kindName: string) =
    task {
        match Reflow.OperationKind.parse kindName with
        | Error e -> failwith e
        | Ok kind ->
            let! db = freshDb ()
            try
                let! docId = insertFullyCompletedDocument db
                do! markManualClassification db docId
                let! result = Reflow.request db logger (TestHelpers.standardV5Dag ()) docId kind Reflow.Apply
                match result with Error e -> failwith e | Ok r -> Assert.False(r.Duplicate)
                let! doc = readDocument db docId
                let r = Prelude.RowReader(doc)
                Assert.Equal("manual", r.String "classification_tier" "")
                Assert.Equal("manual-review", r.String "category" "")
                Assert.Equal(0.99, (r.OptFloat "classification_confidence").Value, 3)
            finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_Request_Apply_DuplicateActive_ReturnsSameOperation`` () =
    task {
        let! db = freshDb ()
        try
            let! docId = insertFullyCompletedDocument db
            let dag = TestHelpers.standardV5Dag ()
            let! first = Reflow.request db logger dag docId Reflow.Reextract Reflow.Apply
            let opId =
                match first with Error e -> failwith e | Ok r -> r.Status.Value.OperationId
            let! second = Reflow.request db logger dag docId Reflow.Reextract Reflow.Apply
            match second with
            | Error e -> failwith e
            | Ok r ->
                Assert.True(r.Duplicate)
                Assert.Equal(opId, r.Status.Value.OperationId)
            let! opCount = scalarInt64 db "SELECT count(*) FROM reflow_operations" []
            Assert.Equal(1L, opCount)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_Request_InterleavedInsertReturning_KeepsOperationAndStagesTogether`` () =
    task {
        let! db = freshDb ()
        try
            let! requestedDocId = insertFullyCompletedDocument db
            let! competingDocId = insertFullyCompletedDocument db
            let dag = TestHelpers.standardV5Dag ()
            let interleaved = interleavingInsertDb db competingDocId (Reflow.dagSignature dag)
            let! result =
                Reflow.request interleaved logger dag requestedDocId Reflow.Reextract Reflow.Apply
            let status =
                match result with
                | Error error -> failwith error
                | Ok response -> response.Status.Value
            let! requestedOperationId =
                scalarInt64 db
                    "SELECT id FROM reflow_operations WHERE document_id = @doc AND operation_kind = 'reextract'"
                    [ ("@doc", Database.boxVal requestedDocId) ]
            let! requestedStages =
                scalarInt64 db
                    "SELECT count(*) FROM reflow_operation_stages WHERE operation_id = @op"
                    [ ("@op", Database.boxVal requestedOperationId) ]
            let! competingStages =
                scalarInt64 db
                    """SELECT count(*) FROM reflow_operation_stages ros
                       JOIN reflow_operations ro ON ro.id = ros.operation_id
                       WHERE ro.document_id = @doc"""
                    [ ("@doc", Database.boxVal competingDocId) ]
            Assert.Equal(requestedDocId, status.DocumentId)
            Assert.Equal(requestedOperationId, status.OperationId)
            Assert.Equal(4L, requestedStages)
            Assert.Equal(0L, competingStages)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_Request_IsVisibleOnlyAfterInvalidationTransactionCommits`` () =
    task {
        let! databases = freshFileDatabases ()
        let entered =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let release =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        try
            let! docId = insertFullyCompletedDocument databases.Writer
            let pausedWriter = pauseBeforeCommit databases.Writer entered release
            let requestTask =
                Reflow.request pausedWriter logger (TestHelpers.standardV5Dag ())
                    docId Reflow.Reextract Reflow.Apply
            do! entered.Task
            let readTask =
                scalarInt64 databases.Observer
                    "SELECT count(*) FROM reflow_operations WHERE document_id = @doc"
                    [ ("@doc", Database.boxVal docId) ]
            let! observation = observeBeforeCommit readTask release
            let! result = requestTask
            match result with Error error -> failwith error | Ok _ -> ()
            let! committedCount =
                scalarInt64 databases.Observer
                    "SELECT count(*) FROM reflow_operations WHERE document_id = @doc"
                    [ ("@doc", Database.boxVal docId) ]
            Assert.True(observation.CompletedWithoutCommit, "Observer read blocked until commit")
            Assert.Equal(0L, observation.OperationCount)
            Assert.Equal(1L, committedCount)
        finally
            release.TrySetResult() |> ignore
            cleanupFileDatabases databases
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_Request_Apply_DoesNotReuseDryRunLedger`` () =
    task {
        let! db = freshDb ()
        try
            let! docId = insertFullyCompletedDocument db
            let dag = TestHelpers.standardV5Dag ()
            let! dryRunIdObj =
                db.execScalar
                    """INSERT INTO reflow_operations
                         (document_id, operation_kind, requested_mode, lifecycle, dag_signature)
                       VALUES (@doc, 'reextract', 'dry_run', 'failed', @sig)
                       RETURNING id"""
                    [ ("@doc", Database.boxVal docId)
                      ("@sig", Database.boxVal (Reflow.dagSignature dag)) ]
            let dryRunId = toInt64 dryRunIdObj
            let! result = Reflow.request db logger dag docId Reflow.Reextract Reflow.Apply
            let status =
                match result with
                | Error error -> failwith error
                | Ok response -> response.Status.Value
            let! dryRunLifecycle =
                scalarStr db
                    "SELECT lifecycle FROM reflow_operations WHERE id = @id"
                    [ ("@id", Database.boxVal dryRunId) ]
            let! dryRunStages =
                scalarInt64 db
                    "SELECT count(*) FROM reflow_operation_stages WHERE operation_id = @id"
                    [ ("@id", Database.boxVal dryRunId) ]
            Assert.NotEqual(dryRunId, status.OperationId)
            Assert.Equal(Reflow.Apply, status.Mode)
            Assert.Equal("failed", dryRunLifecycle)
            Assert.Equal(0L, dryRunStages)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_GetStatus_InconsistentLedger_FailsClosed`` () =
    task {
        let! db = freshDb ()
        try
            let! docId = insertFullyCompletedDocument db
            let dag = TestHelpers.standardV5Dag ()
            let! result = Reflow.request db logger dag docId Reflow.Reextract Reflow.Apply
            let opId =
                match result with Error e -> failwith e | Ok r -> r.Status.Value.OperationId
            let! _ =
                db.execNonQuery
                    "DELETE FROM reflow_operation_stages WHERE operation_id = @op AND stage_name = 'embed'"
                    [ ("@op", Database.boxVal opId) ]
            let! status = Reflow.getStatus dag db opId
            match status with Error msg -> Assert.Contains("missing", msg) | Ok _ -> failwith "Expected Error"
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_GetStatus_ChangedDagSignature_FailsClosed`` () =
    task {
        let! db = freshDb ()
        try
            let! docId = insertFullyCompletedDocument db
            let dag = TestHelpers.standardV5Dag ()
            let! result = Reflow.request db logger dag docId Reflow.Reextract Reflow.Apply
            let opId =
                match result with Error e -> failwith e | Ok r -> r.Status.Value.OperationId
            match PipelineV5.buildDag (TestHelpers.standardV5Stages @ [ extraDescendantStage ]) with
            | Error e -> failwith e
            | Ok changedDag ->
                let! status = Reflow.getStatus changedDag db opId
                match status with Error msg -> Assert.Contains("signature", msg) | Ok _ -> failwith "Expected Error"
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_Request_TransactionFault_RollsBackAllInvalidation`` () =
    task {
        let! db = freshDb ()
        try
            let! docId = insertFullyCompletedDocument db
            let faultyDb = throwingTransactionDb db "DELETE FROM triage"
            let! result =
                Reflow.request faultyDb logger (TestHelpers.standardV5Dag ()) docId Reflow.Reextract Reflow.Apply
            match result with Error msg -> Assert.Contains("Injected", msg) | Ok _ -> failwith "Expected Error"
            let! completions = stageCompletionNames db docId
            Assert.Equal<Set<string>>(Set.ofList allStageNames, completions)
            for table in stageOutputTables do
                let! count = countRows db table docId
                Assert.Equal(1L, count)
            let! chunks = countRows db "document_chunks" docId
            Assert.Equal(1L, chunks)
            let! doc = readDocument db docId
            let r = Prelude.RowReader(doc)
            Assert.Equal("embedded", r.String "stage" "")
            Assert.Equal("invoices", r.String "category" "")
            Assert.True((r.OptFloat "extracted_amount").IsSome)
            let! operationCount =
                scalarInt64 db "SELECT count(*) FROM reflow_operations WHERE document_id = @doc"
                    [ ("@doc", Database.boxVal docId) ]
            let! generation = documentGeneration db docId
            Assert.Equal(0L, operationCount)
            Assert.Equal(0L, generation)
        finally db.dispose ()
    }

let private testArchiveDir = "/archive"

let private baseDeps (db: Algebra.Database) (fs: Algebra.FileSystem) : Stages.Deps =
    { Fs = fs; Db = db; Logger = logger; Clock = TestHelpers.defaultClock
      Extractor = Interpreters.nullTextExtractor; Embedder = None
      ChatProvider = None; TriageProvider = None; ContentRules = []
      ComprehensionPrompt = None; TriagePrompt = None; Preferences = ""
      ArchiveDir = testArchiveDir }

let private insertBareDocument (db: Algebra.Database) (savedPath: string) : Task<int64> =
    task {
        let! _ =
            db.execNonQuery
                """INSERT INTO documents (source_type, saved_path, category, sha256, sender, subject)
                   VALUES ('watched_folder', @path, 'unsorted', 'deadbeef', 'billing@test.com', 'Test')"""
                [ ("@path", Database.boxVal savedPath) ]
        let! idObj = db.execScalar "SELECT last_insert_rowid()" []
        return toInt64 idObj
    }

let private insertBareExtractionRow (db: Algebra.Database) (docId: int64) : Task<unit> =
    task {
        let! _ = db.execNonQuery "INSERT INTO extraction (document_id) VALUES (@doc)" [ ("@doc", Database.boxVal docId) ]
        ()
    }

let private assertFailed label outcome =
    match outcome with
    | PipelineV5.Failed _ -> ()
    | other -> failwith $"{label}: expected Failed, got {other}"

let private setupTriageDocument (db: Algebra.Database) (mem: TestHelpers.MemFs) =
    task {
        let savedPath = "/triage/invoice.pdf"
        let! docId = insertBareDocument db savedPath
        do! insertBareExtractionRow db docId
        mem.Put (savedPath + ".extracted.md") "Invoice total: $500 due 2025-04-01"
        return docId
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_Pipeline_PartialFailureThenRetry_IsTruthfulAndIdempotent`` () =
    task {
        let! db = freshDb ()
        try
            let! docId = insertFullyCompletedDocument db
            let mutable embedCalls = 0
            let controlled _ _ _ =
                task {
                    embedCalls <- embedCalls + 1
                    if embedCalls = 1 then return PipelineV5.Failed "transient failure"
                    else return PipelineV5.Completed
                }
            let stages =
                TestHelpers.standardV5Stages
                |> List.map (fun s -> if s.Name = "embed" then { s with Process = controlled } else s)
            let dag =
                match PipelineV5.buildDag stages with Ok d -> d | Error e -> failwith e
            let embedStage = dag.Stages.["embed"]
            let gpu = PipelineV5.createGpuScheduler logger
            let! requested = Reflow.request db logger dag docId Reflow.Reembed Reflow.Apply
            let opId =
                match requested with
                | Error e -> failwith e
                | Ok response -> response.Status.Value.OperationId
            let! first =
                PipelineV5.processStageForDag dag embedStage db logger gpu 10 (System.TimeSpan.FromMinutes 1.0)
                    System.Threading.CancellationToken.None
            Assert.Equal(0, first)
            let! lifecycle1 = scalarStr db "SELECT lifecycle FROM reflow_operations WHERE document_id=@doc" [ ("@doc", box docId) ]
            let! stage1 =
                scalarStr db
                    "SELECT outcome FROM reflow_operation_stages WHERE operation_id=@op AND stage_name='embed'"
                    [ ("@op", box opId) ]
            let! started1 =
                scalarStr db
                    "SELECT started_at FROM reflow_operation_stages WHERE operation_id=@op AND stage_name='embed'"
                    [ ("@op", box opId) ]
            Assert.Equal("failed", lifecycle1)
            Assert.Equal("failed", stage1)
            Assert.NotEmpty(started1)
            let! openLetters =
                scalarInt64 db "SELECT count(*) FROM dead_letters WHERE doc_id=@doc AND stage='embed' AND dismissed=0"
                    [ ("@doc", box docId) ]
            Assert.Equal(1L, openLetters)
            let! second =
                PipelineV5.processStageForDag dag embedStage db logger gpu 10 (System.TimeSpan.FromMinutes 1.0)
                    System.Threading.CancellationToken.None
            Assert.Equal(1, second)
            let! lifecycle2 = scalarStr db "SELECT lifecycle FROM reflow_operations WHERE document_id=@doc" [ ("@doc", box docId) ]
            let! stage2 =
                scalarStr db
                    "SELECT outcome FROM reflow_operation_stages WHERE operation_id=@op AND stage_name='embed'"
                    [ ("@op", box opId) ]
            let! started2 =
                scalarStr db
                    "SELECT started_at FROM reflow_operation_stages WHERE operation_id=@op AND stage_name='embed'"
                    [ ("@op", box opId) ]
            Assert.Equal("completed", lifecycle2)
            Assert.Equal("reran", stage2)
            Assert.Equal(started1, started2)
            let! letters = scalarInt64 db "SELECT count(*) FROM dead_letters WHERE doc_id=@doc AND stage='embed'" [ ("@doc", box docId) ]
            let! dismissed = scalarInt64 db "SELECT count(*) FROM dead_letters WHERE doc_id=@doc AND stage='embed' AND dismissed=1" [ ("@doc", box docId) ]
            Assert.Equal(1L, letters)
            Assert.Equal(1L, dismissed)
        finally db.dispose ()
    }

let private requireOperationId (result: Result<Reflow.RequestResult, string>) : int64 =
    match result with
    | Error error -> failwith error
    | Ok response -> response.Status.Value.OperationId

let private requireStatus
    (dag: PipelineV5.Dag)
    (db: Algebra.Database)
    (operationId: int64)
    : Task<Reflow.OperationStatus> =
    task {
        let! result = Reflow.getStatus dag db operationId
        return match result with Error error -> failwith error | Ok status -> status
    }

let private requirePublished label = function
    | Generation.Published () -> ()
    | Generation.Superseded -> failwith $"{label} was superseded"

type private ArtifactFenceScenario =
    { Db: Algebra.Database
      Mem: TestHelpers.MemFs
      Dag: PipelineV5.Dag
      DocumentA: int64
      Folder: PublicationFence.ArtifactFolder
      OldGeneration: Generation.Token
      CurrentGeneration: Generation.Token
      ReflowEntered: TaskCompletionSource<unit>
      ReflowRelease: TaskCompletionSource<unit>
      Sidecar: string }

let private createArtifactFenceScenario db mem =
    task {
        let! documentA = insertFullyCompletedDocument db
        let! documentB = insertFullyCompletedDocument db
        let! oldGeneration = Generation.current db documentA
        let! currentGeneration = Generation.current db documentB
        let folder =
            PublicationFence.ArtifactFolder.tryFromMetadata
                "/archive/invoice.pdf" None
            |> Option.defaultWith (fun () -> failwith "Expected folder")
        return
            { Db = db; Mem = mem; Dag = TestHelpers.standardV5Dag ()
              DocumentA = documentA; Folder = folder
              OldGeneration = oldGeneration; CurrentGeneration = currentGeneration
              ReflowEntered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
              ReflowRelease = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
              Sidecar = "/archive/thread.comprehension.json" }
    }

type private PausedPublication =
    { Entered: Task
      Release: unit -> unit
      Completion: Task<Generation.Publication<unit>> }

let private pausedPublication scenario token content =
    let entered =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let release =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let effect () =
        task {
            entered.TrySetResult() |> ignore
            do! release.Task
            scenario.Mem.Put scenario.Sidecar content
        }
    { Entered = entered.Task
      Release = fun () -> release.TrySetResult() |> ignore
      Completion =
        Generation.publishEffect
            scenario.Db token scenario.Folder effect }

let private holdReflowBehindNewerPublication scenario =
    task {
        let newer =
            pausedPublication scenario scenario.CurrentGeneration "newer-b"
        try
            do! newer.Entered
            let pausedDb =
                pauseBeforeCommit
                    scenario.Db scenario.ReflowEntered scenario.ReflowRelease
            let reflowing =
                Reflow.request pausedDb logger scenario.Dag scenario.DocumentA
                    Reflow.Recomprehend Reflow.Apply
            do! Task.Yield()
            Assert.False(
                scenario.ReflowEntered.Task.IsCompleted,
                "Reflow crossed the shared folder publication")
            newer.Release()
            let! publication = newer.Completion
            requirePublished "Document B publication" publication
            do! scenario.ReflowEntered.Task
            return reflowing
        finally
            newer.Release()
    }

let private proveOldPublicationIsSuperseded scenario reflowing =
    task {
        let oldWrite =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let stale =
            Generation.publishEffect
                scenario.Db scenario.OldGeneration scenario.Folder (fun () ->
                    oldWrite.TrySetResult() |> ignore
                    task { scenario.Mem.Put scenario.Sidecar "old-a" })
        do! Task.Yield()
        Assert.False(oldWrite.Task.IsCompleted)
        scenario.ReflowRelease.TrySetResult() |> ignore
        let! accepted = reflowing
        requireOperationId accepted |> ignore
        match! stale with
        | Generation.Published () -> failwith "Old document A work was published"
        | Generation.Superseded -> ()
        Assert.False(oldWrite.Task.IsCompleted)
        Assert.Equal(Some "newer-b", scenario.Mem.Get scenario.Sidecar)
        Assert.True(scenario.Mem.Fs.fileExists scenario.Sidecar)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_SharedFolderFence_OrdersNewerDocumentBeforeReflowAndRejectsOldWork`` () =
    task {
        let! db = freshDb ()
        let mem = TestHelpers.memFs ()
        let! scenario = createArtifactFenceScenario db mem
        try
            let! reflowing = holdReflowBehindNewerPublication scenario
            do! proveOldPublicationIsSuperseded scenario reflowing
        finally
            scenario.ReflowRelease.TrySetResult() |> ignore
            db.dispose ()
    }

let private processNamedStage
    (dag: PipelineV5.Dag)
    (db: Algebra.Database)
    (gpu: PipelineV5.GpuScheduler)
    (name: string)
    : Task<int> =
    PipelineV5.processStageForDag
        dag
        dag.Stages.[name]
        db
        logger
        gpu
        10
        (System.TimeSpan.FromMinutes 1.0)
        System.Threading.CancellationToken.None

let private publishStageRow
    (db: Algebra.Database)
    (token: Generation.Token)
    (sql: string)
    : Task<Generation.Publication<unit>> =
    Generation.publish db token (fun scope ->
        task {
            let! _ =
                scope.execNonQuery
                    sql
                    [ ("@doc", Database.boxVal token.DocumentId) ]
            return ()
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_StageOwnedOutputs_AfterGenerationBump_NeverBecomeObservable`` () =
    task {
        let! db = freshDb ()
        try
            let! docId = insertFullyCompletedDocument db
            // A stage adapter captured this generation with its lease.
            let! captured = Generation.current db docId
            let! reflow =
                Reflow.request db logger (TestHelpers.standardV5Dag ()) docId
                    Reflow.Reextract Reflow.Apply
            match reflow with Error error -> failwith error | Ok _ -> ()
            let! bumped = Generation.current db docId
            Assert.NotEqual(captured.Value, bumped.Value)

            // Every stage adapter now publishes its output through the same
            // generation-checked transaction, so none of these can land.
            let statements =
                [ "extraction",
                  "INSERT OR REPLACE INTO extraction (document_id) VALUES (@doc)"
                  "triage",
                  "INSERT OR REPLACE INTO triage (document_id,document_type,category,confidence) VALUES (@doc,'invoice','invoices',0.9)"
                  "comprehension",
                  "INSERT OR REPLACE INTO comprehension (document_id,document_type,category,confidence) VALUES (@doc,'invoice','invoices',0.9)"
                  "embedding",
                  "INSERT OR REPLACE INTO embedding (document_id,chunk_count) VALUES (@doc,3)"
                  "document_chunks",
                  "INSERT INTO document_chunks (document_id, chunk_index, chunk_text, embedded_at) VALUES (@doc, 9, 'stale', datetime('now'))" ]

            for (table, sql) in statements do
                let! publication = publishStageRow db captured sql
                Assert.Equal(Generation.Superseded, publication)
                let! rows = countRows db table docId
                Assert.Equal(0L, rows)
        finally db.dispose ()
    }

let private processNamedStageUnused
    (dag: PipelineV5.Dag)
    (db: Algebra.Database)
    (gpu: PipelineV5.GpuScheduler)
    (name: string)
    : Task<int> =
    PipelineV5.processStageForDag
        dag
        dag.Stages.[name]
        db
        logger
        gpu
        10
        (System.TimeSpan.FromMinutes 1.0)
        System.Threading.CancellationToken.None

let private insertConcurrentReembedLedger
    (db: Algebra.Database)
    (dag: PipelineV5.Dag)
    (documentId: int64)
    : Task<int64> =
    task {
        let! value =
            db.execScalar
                """INSERT INTO reflow_operations
                     (document_id, operation_kind, requested_mode, lifecycle, dag_signature)
                   VALUES (@doc, 'reembed', 'apply', 'running', @signature)
                   RETURNING id"""
                [ ("@doc", Database.boxVal documentId)
                  ("@signature", Database.boxVal (Reflow.dagSignature dag)) ]
        let operationId = toInt64 value
        let! _ =
            db.execNonQuery
                """INSERT INTO reflow_operation_stages
                     (operation_id, stage_name, outcome)
                   VALUES (@operation, 'extract', 'current'),
                          (@operation, 'triage', 'current'),
                          (@operation, 'deep-comprehend', 'current'),
                          (@operation, 'embed', 'pending')"""
                [ ("@operation", Database.boxVal operationId) ]
        return operationId
    }

let private insertStaleFailedEmbedLedger
    (db: Algebra.Database)
    (documentId: int64)
    : Task<unit> =
    task {
        let! operationId =
            db.execScalar
                """INSERT INTO reflow_operations
                     (document_id, operation_kind, requested_mode, lifecycle, dag_signature)
                   VALUES (@doc, 'reembed', 'apply', 'failed', 'stale-ledger')
                   RETURNING id"""
                [ ("@doc", Database.boxVal documentId) ]
        let! _ =
            db.execNonQuery
                """INSERT INTO reflow_operation_stages
                     (operation_id, stage_name, outcome, completed_at, error)
                   VALUES (@op, 'embed', 'failed', datetime('now'), 'stale failure')"""
                [ ("@op", operationId) ]
        return ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_Pipeline_ConcurrentKinds_KeepAttemptOutcomesIndependent`` () =
    task {
        let! db = freshDb ()
        try
            let! docId = insertFullyCompletedDocument db
            let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let controlledEmbed _ _ _ =
                task {
                    entered.TrySetResult() |> ignore
                    do! release.Task
                    return PipelineV5.Completed
                }
            let stages =
                TestHelpers.standardV5Stages
                |> List.map (fun stage ->
                    if stage.Name = "embed" then { stage with Process = controlledEmbed }
                    else stage)
            let dag =
                match PipelineV5.buildDag stages with
                | Error error -> failwith error
                | Ok value -> value
            let gpu = PipelineV5.createGpuScheduler logger
            let! reextract = Reflow.request db logger dag docId Reflow.Reextract Reflow.Apply
            let reextractId = requireOperationId reextract
            let! extractCount = processNamedStage dag db gpu "extract"
            Assert.Equal(1, extractCount)
            let firstEmbed = processNamedStage dag db gpu "embed"
            do! entered.Task
            let! startedBefore =
                scalarStr db
                    """SELECT started_at FROM reflow_operation_stages
                       WHERE operation_id = @op AND stage_name = 'embed'"""
                    [ ("@op", Database.boxVal reextractId) ]
            let! reembedId =
                insertConcurrentReembedLedger db dag docId
            release.TrySetResult() |> ignore
            let! firstEmbedCount = firstEmbed
            Assert.Equal(1, firstEmbedCount)
            let! reextractAfterFirst = requireStatus dag db reextractId
            let! reembedAfterFirst = requireStatus dag db reembedId
            let! completionCount =
                scalarInt64 db
                    "SELECT count(*) FROM stage_completions WHERE document_id=@doc AND stage_name='embed'"
                    [ ("@doc", Database.boxVal docId) ]
            Assert.Equal(Reflow.LifecycleRunning, reextractAfterFirst.Lifecycle)
            Assert.Equal(Reflow.Reran, reextractAfterFirst.Stages |> List.find (fun s -> s.StageName = "embed") |> fun s -> s.Outcome)
            Assert.Equal(Reflow.LifecycleRunning, reembedAfterFirst.Lifecycle)
            Assert.Equal(Reflow.Pending, reembedAfterFirst.Stages |> List.find (fun s -> s.StageName = "embed") |> fun s -> s.Outcome)
            Assert.Equal(0L, completionCount)
            let! secondEmbedCount = processNamedStage dag db gpu "embed"
            Assert.Equal(1, secondEmbedCount)
            let! reembedCompleted = requireStatus dag db reembedId
            Assert.Equal(Reflow.LifecycleCompleted, reembedCompleted.Lifecycle)
            let! _ = processNamedStage dag db gpu "triage"
            let! _ = processNamedStage dag db gpu "deep-comprehend"
            let! reextractCompleted = requireStatus dag db reextractId
            let! reembedStillCompleted = requireStatus dag db reembedId
            let! startedAfter =
                scalarStr db
                    """SELECT started_at FROM reflow_operation_stages
                       WHERE operation_id = @op AND stage_name = 'embed'"""
                    [ ("@op", Database.boxVal reextractId) ]
            Assert.Equal(Reflow.LifecycleCompleted, reextractCompleted.Lifecycle)
            Assert.Equal(Reflow.LifecycleCompleted, reembedStillCompleted.Lifecycle)
            Assert.Equal(startedBefore, startedAfter)
        finally db.dispose ()
    }

type private ControlledAttempt =
    { Dag: PipelineV5.Dag
      Gpu: PipelineV5.GpuScheduler
      Entered: Task<unit>
      Release: unit -> unit
      Processing: Task<int> }

let private startControlledEmbedAttempt
    (db: Algebra.Database)
    : ControlledAttempt =
    let entered =
        TaskCompletionSource<unit>(
            TaskCreationOptions.RunContinuationsAsynchronously)
    let release =
        TaskCompletionSource<unit>(
            TaskCreationOptions.RunContinuationsAsynchronously)
    let controlled
        (database: Algebra.Database)
        _
        (execution: PipelineV5.StageExecution) =
        task {
            entered.TrySetResult() |> ignore
            do! release.Task
            let documentId = execution.DocumentId
            let! _ =
                database.execNonQuery
                    """INSERT OR REPLACE INTO embedding
                         (document_id, chunk_count)
                       VALUES (@doc, 3)"""
                    [ ("@doc", Database.boxVal documentId) ]
            let! _ =
                database.execNonQuery
                    """INSERT OR REPLACE INTO document_chunks
                         (document_id, chunk_index, chunk_text, embedded_at)
                       VALUES
                         (@doc, 0, 'controlled chunk', datetime('now'))"""
                    [ ("@doc", Database.boxVal documentId) ]
            return PipelineV5.Completed
        }
    let stages =
        TestHelpers.standardV5Stages
        |> List.map (fun stage ->
            if stage.Name = "embed" then { stage with Process = controlled }
            else stage)
    let dag =
        match PipelineV5.buildDag stages with
        | Ok value -> value
        | Error error -> failwith error
    let gpu = PipelineV5.createGpuScheduler logger
    { Dag = dag
      Gpu = gpu
      Entered = entered.Task
      Release = fun () -> release.TrySetResult() |> ignore
      Processing = processNamedStage dag db gpu "embed" }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_Request_DuringActiveProcessor_DiscardsStaleOutputAndRetryConverges`` () =
    task {
        let! db = freshDb ()
        try
            let! documentId = insertFullyCompletedDocument db
            let! _ =
                db.execNonQuery
                    """DELETE FROM stage_completions
                       WHERE document_id = @doc
                         AND stage_name = 'embed'"""
                    [ ("@doc", Database.boxVal documentId) ]
            let attempt = startControlledEmbedAttempt db
            try
                do! attempt.Entered
                let! accepted =
                    Reflow.request
                        db logger attempt.Dag documentId
                        Reflow.Reembed Reflow.Apply
                let operationId = requireOperationId accepted
                attempt.Release ()
                let! processed = attempt.Processing
                let! staleOutput =
                    countRows db "embedding" documentId
                let! staleChunks =
                    countRows db "document_chunks" documentId
                let! letters =
                    scalarInt64 db
                        """SELECT count(*) FROM dead_letters
                           WHERE doc_id = @doc AND dismissed = 0"""
                        [ ("@doc", Database.boxVal documentId) ]
                Assert.Equal(0, processed)
                Assert.Equal(0L, staleOutput)
                Assert.Equal(0L, staleChunks)
                Assert.Equal(0L, letters)

                let! retried =
                    processNamedStage
                        attempt.Dag db attempt.Gpu "embed"
                let! status =
                    requireStatus attempt.Dag db operationId
                let! output = countRows db "embedding" documentId
                let! chunks =
                    countRows db "document_chunks" documentId
                Assert.Equal(1, retried)
                Assert.Equal(1L, output)
                Assert.Equal(1L, chunks)
                Assert.Equal(
                    Reflow.LifecycleCompleted,
                    status.Lifecycle)
            finally
                attempt.Release ()
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_Request_DuplicateDuringActiveAttempt_CoalescesWithoutGenerationBump`` () =
    task {
        let! db = freshDb ()
        try
            let! documentId = insertFullyCompletedDocument db
            let! _ =
                db.execNonQuery
                    """DELETE FROM stage_completions
                       WHERE document_id = @doc
                         AND stage_name = 'embed'"""
                    [ ("@doc", Database.boxVal documentId) ]
            let attempt = startControlledEmbedAttempt db
            try
                do! attempt.Entered
                let! first =
                    Reflow.request
                        db logger attempt.Dag documentId
                        Reflow.Reembed Reflow.Apply
                let operationId = requireOperationId first
                let! firstGeneration =
                    documentGeneration db documentId
                let! duplicate =
                    Reflow.request
                        db logger attempt.Dag documentId
                        Reflow.Reembed Reflow.Apply
                let! secondGeneration =
                    documentGeneration db documentId
                match duplicate with
                | Error error -> failwith error
                | Ok response ->
                    Assert.True(response.Duplicate)
                    Assert.Equal(
                        operationId,
                        response.Status.Value.OperationId)
                Assert.Equal(firstGeneration, secondGeneration)
                Assert.Equal(1L, secondGeneration)
            finally
                attempt.Release ()
            let! processed = attempt.Processing
            Assert.Equal(0, processed)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_Request_SimultaneousDifferentKinds_HaveIndependentAttribution`` () =
    task {
        let! db = freshDb ()
        try
            let! documentId = insertFullyCompletedDocument db
            let dag = TestHelpers.standardV5Dag ()
            let start =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously)
            let request kind =
                task {
                    do! start.Task
                    return!
                        Reflow.request
                            db logger dag documentId kind Reflow.Apply
                }
            let reextractTask = request Reflow.Reextract
            let reembedTask = request Reflow.Reembed
            start.TrySetResult(()) |> ignore
            let! reextractResult = reextractTask
            let! reembedResult = reembedTask
            let reextractId = requireOperationId reextractResult
            let reembedId = requireOperationId reembedResult
            let! generation = documentGeneration db documentId
            Assert.NotEqual(reextractId, reembedId)
            Assert.Equal(2L, generation)

            let gpu = PipelineV5.createGpuScheduler logger
            let! _ = processNamedStage dag db gpu "extract"
            let! _ = processNamedStage dag db gpu "triage"
            let! _ =
                processNamedStage dag db gpu "deep-comprehend"
            let! _ = processNamedStage dag db gpu "embed"
            let! reextract = requireStatus dag db reextractId
            let! reembed = requireStatus dag db reembedId
            let outcome
                (status: Reflow.OperationStatus)
                name =
                status.Stages
                |> List.find (fun stage -> stage.StageName = name)
                |> fun stage -> stage.Outcome
            Assert.Equal(
                Reflow.LifecycleCompleted,
                reextract.Lifecycle)
            Assert.Equal(
                Reflow.LifecycleCompleted,
                reembed.Lifecycle)
            Assert.Equal(Reflow.Reran, outcome reextract "extract")
            Assert.Equal(Reflow.Reran, outcome reextract "embed")
            Assert.Equal(Reflow.Current, outcome reembed "extract")
            Assert.Equal(Reflow.Reran, outcome reembed "embed")
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``PipelineV5_RecoverStaleAttempt_CleansOutputBeforeRetry`` () =
    task {
        let! db = freshDb ()
        try
            let! documentId = insertFullyCompletedDocument db
            let parameters =
                [ ("@doc", Database.boxVal documentId) ]
            let! _ =
                db.execNonQuery
                    """DELETE FROM stage_completions
                       WHERE document_id = @doc
                         AND stage_name = 'embed'"""
                    parameters
            let! _ =
                db.execNonQuery
                    """INSERT INTO pipeline_stage_attempts
                         (document_id, stage_name, attempt_token)
                       VALUES (@doc, 'embed', @token)"""
                    (parameters
                     @ [ ("@token",
                          Database.boxVal
                              (Guid.NewGuid().ToString("N"))) ])
            let publish
                (database: Algebra.Database)
                _
                (execution: PipelineV5.StageExecution) =
                task {
                    let! _ =
                        database.execNonQuery
                            """INSERT OR REPLACE INTO embedding
                                 (document_id, chunk_count)
                               VALUES (@doc, 1)"""
                            [ ("@doc",
                               Database.boxVal execution.DocumentId) ]
                    return PipelineV5.Completed
                }
            let stages =
                TestHelpers.standardV5Stages
                |> List.map (fun stage ->
                    if stage.Name = "embed" then
                        { stage with Process = publish }
                    else stage)
            let dag =
                match PipelineV5.buildDag stages with
                | Ok value -> value
                | Error error -> failwith error
            do! PipelineV5.initSchema db stages
            let! preserved =
                countRows db "pipeline_stage_attempts" documentId
            let! recovered =
                PipelineV5.recoverStaleAttempts db stages
            let! leases =
                countRows db "pipeline_stage_attempts" documentId
            let! staleOutput = countRows db "embedding" documentId
            let! staleChunks =
                countRows db "document_chunks" documentId
            Assert.Equal(1L, preserved)
            Assert.Equal(1, recovered)
            Assert.Equal(0L, leases)
            Assert.Equal(0L, staleOutput)
            Assert.Equal(0L, staleChunks)

            let gpu = PipelineV5.createGpuScheduler logger
            let! processed =
                processNamedStage dag db gpu "embed"
            let! output = countRows db "embedding" documentId
            Assert.Equal(1, processed)
            Assert.Equal(1L, output)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_Pipeline_StaleFailedLedger_DoesNotSuppressCompletion`` () =
    task {
        let! db = freshDb ()
        try
            let! docId = insertFullyCompletedDocument db
            let! _ =
                db.execNonQuery
                    "DELETE FROM stage_completions WHERE document_id = @doc AND stage_name = 'embed'"
                    [ ("@doc", Database.boxVal docId) ]
            let processDocument
                database
                _
                (execution: PipelineV5.StageExecution) =
                task {
                    do!
                        insertStaleFailedEmbedLedger
                            database execution.DocumentId
                    return PipelineV5.Completed
                }
            let stages =
                TestHelpers.standardV5Stages
                |> List.map (fun stage ->
                    if stage.Name = "embed" then
                        { stage with Process = processDocument }
                    else stage)
            let dag =
                match PipelineV5.buildDag stages with
                | Ok value -> value
                | Error error -> failwith error
            let stage = dag.Stages.["embed"]
            let gpu = PipelineV5.createGpuScheduler logger
            let! processed =
                PipelineV5.processStageForDag dag stage db logger gpu 1 (TimeSpan.FromMinutes 1.0)
                    Threading.CancellationToken.None
            let! completionCount =
                scalarInt64 db
                    """SELECT count(*) FROM stage_completions
                       WHERE document_id = @doc AND stage_name = 'embed'"""
                    [ ("@doc", Database.boxVal docId) ]
            Assert.Equal(1, processed)
            Assert.Equal(1L, completionCount)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``StagesV5_Extract_MissingSource_DoesNotComplete`` () =
    task {
        let! db = freshDb ()
        try
            let mem = TestHelpers.memFs ()
            let! docId = insertBareDocument db "/missing/source.pdf"
            let! outcome = StagesV5.extract (baseDeps db mem.Fs) db logger docId
            assertFailed "extract missing source" outcome
            let! count = countRows db "extraction" docId
            Assert.Equal(0L, count)
            let! doc = readDocument db docId
            Assert.True((Prelude.RowReader doc).OptString("extracted_at").IsNone)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``StagesV5_Triage_MissingProvider_DoesNotComplete`` () =
    task {
        let! db = freshDb ()
        try
            let mem = TestHelpers.memFs ()
            let! docId = setupTriageDocument db mem
            let! outcome = StagesV5.triage (baseDeps db mem.Fs) db logger docId
            assertFailed "triage missing provider" outcome
            let! count = countRows db "triage" docId
            Assert.Equal(0L, count)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``StagesV5_Triage_MalformedResponse_DoesNotComplete`` () =
    task {
        let! db = freshDb ()
        try
            let mem = TestHelpers.memFs ()
            let! docId = setupTriageDocument db mem
            let deps = { baseDeps db mem.Fs with ChatProvider = Some(TestHelpers.fakeChatProvider "not json") }
            let! outcome = StagesV5.triage deps db logger docId
            assertFailed "triage parse" outcome
            let! count = countRows db "triage" docId
            Assert.Equal(0L, count)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``StagesV5_Extract_ArtifactWriteFailure_DoesNotComplete`` () =
    task {
        let! db = freshDb ()
        try
            let mem = TestHelpers.memFs ()
            let savedPath = "/plain/source.txt"
            mem.Bytes.[mem.Norm savedPath] <- System.Text.Encoding.UTF8.GetBytes("plain text")
            let throwingFs = { mem.Fs with writeAllText = fun _ _ -> task { return failwith "write fault" } }
            let! docId = insertBareDocument db savedPath
            let! outcome = StagesV5.extract (baseDeps db throwingFs) db logger docId
            assertFailed "write failure" outcome
            let! count = countRows db "extraction" docId
            Assert.Equal(0L, count)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``StagesV5_Embed_MissingProvider_DoesNotComplete`` () =
    task {
        let! db = freshDb ()
        try
            let mem = TestHelpers.memFs ()
            let! docId = insertBareDocument db "/embed/source.txt"
            do! insertBareExtractionRow db docId
            let! outcome = StagesV5.embed (baseDeps db mem.Fs) db logger docId
            assertFailed "embed missing provider" outcome
            let! count = countRows db "embedding" docId
            Assert.Equal(0L, count)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_Request_MissingDocument_ReturnsError`` () =
    task {
        let! db = freshDb ()
        try
            let! result =
                Reflow.request db logger (TestHelpers.standardV5Dag ()) 999999L Reflow.Reextract Reflow.Apply
            match result with
            | Error msg -> Assert.Contains("not found", msg)
            | Ok _ -> failwith "Expected Error"
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_Request_ApplyWithoutUsableArtifactFolder_FailsWithoutMutation`` () =
    task {
        let! db = freshDb ()
        try
            let! docId = insertBareDocument db ""
            let! result =
                Reflow.request db logger (TestHelpers.standardV5Dag ())
                    docId Reflow.Recomprehend Reflow.Apply
            match result with
            | Ok _ -> failwith "Expected missing-folder rejection"
            | Error error -> Assert.Contains("no usable folder", error)
            let! operations = scalarInt64 db "SELECT count(*) FROM reflow_operations" []
            let! generation = documentGeneration db docId
            Assert.Equal(0L, operations)
            Assert.Equal(0L, generation)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_Request_Apply_Reextract_InvalidatesArtefactsAndPreservesMetadata`` () =
    task {
        let! db = freshDb ()
        try
            let! docId = insertFullyCompletedDocument db
            let! result =
                Reflow.request db logger (TestHelpers.standardV5Dag ()) docId Reflow.Reextract Reflow.Apply
            match result with
            | Error e -> failwith e
            | Ok r ->
                Assert.True(r.Status.IsSome)
                Assert.False(r.Duplicate)
                Assert.Equal(Reflow.LifecycleRunning, r.Status.Value.Lifecycle)
                Assert.True(r.Status.Value.Stages |> List.forall (fun s -> s.Outcome = Reflow.Pending))
            for table in stageOutputTables do
                let! count = countRows db table docId
                Assert.Equal(0L, count)
            let! completions = stageCompletionNames db docId
            Assert.True(Set.isEmpty completions)
            let! chunks = countRows db "document_chunks" docId
            Assert.Equal(0L, chunks)
            let! doc = readDocument db docId
            let r = Prelude.RowReader(doc)
            Assert.Equal("received", r.String "stage" "")
            Assert.Equal("unclassified", r.String "category" "")
            Assert.True((r.OptString "classification_tier").IsNone)
            Assert.True((r.OptFloat "extracted_amount").IsNone)
            Assert.True((r.OptString "embedded_at").IsNone)
            Assert.Equal("/source/invoice.pdf", r.String "source_path" "")
            Assert.Equal("deadbeef", r.String "sha256" "")
            Assert.Equal(4096L, r.Int64 "size_bytes" 0L)
            let! tagCount = countRows db "tags" docId
            Assert.Equal(1L, tagCount)
            let! correctionCount = countRows db "corrections" docId
            Assert.Equal(1L, correctionCount)
        finally db.dispose ()
    }

let private completeStatusLedger
    operationId
    (scope: Algebra.TransactionScope)
    : Task<Result<unit, string>> =
    task {
        let! _ =
            scope.execNonQuery
                """UPDATE reflow_operation_stages
                   SET outcome = 'reran', completed_at = datetime('now'), error = NULL
                   WHERE operation_id = @operation"""
                [ ("@operation", Database.boxVal operationId) ]
        let! _ =
            scope.execNonQuery
                """UPDATE reflow_operations
                   SET lifecycle = 'completed', completed_at = datetime('now'), error = NULL
                   WHERE id = @operation"""
                [ ("@operation", Database.boxVal operationId) ]
        return Ok ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_FinalisationSuccessFault_RollsBackAllLifecycleWrites`` () =
    task {
        let! db = freshDb ()
        try
            let! docId = insertFullyCompletedDocument db
            let dag = TestHelpers.standardV5Dag ()
            let! requested =
                Reflow.request db logger dag docId Reflow.Reembed Reflow.Apply
            let operationId = requireOperationId requested
            let! _ =
                db.execNonQuery
                    """INSERT INTO dead_letters
                         (doc_id, stage, error, retryable, failed_at)
                       VALUES (@doc, 'embed', 'old failure', 1, datetime('now'))"""
                    [ ("@doc", Database.boxVal docId) ]
            let stageProcessor
                (database: Algebra.Database)
                _
                (execution: PipelineV5.StageExecution) =
                task {
                    let id = execution.DocumentId
                    let! _ =
                        database.execNonQuery
                            """INSERT OR REPLACE INTO embedding
                                 (document_id, chunk_count)
                               VALUES (@doc, 7)"""
                            [ ("@doc", Database.boxVal id) ]
                    return PipelineV5.Completed
                }
            let stage = { dag.Stages.["embed"] with Process = stageProcessor }
            let faultyDb =
                throwingTransactionDb db "UPDATE documents SET stage = @stage"
            let gpu = PipelineV5.createGpuScheduler logger
            let! faulted =
                PipelineV5.processStageForDag
                    dag stage faultyDb logger gpu 1
                    (TimeSpan.FromMinutes 1.0)
                    Threading.CancellationToken.None
            let! status = requireStatus dag db operationId
            let embed =
                status.Stages |> List.find (fun value -> value.StageName = "embed")
            let! completionCount =
                scalarInt64 db
                    """SELECT count(*) FROM stage_completions
                       WHERE document_id = @doc AND stage_name = 'embed'"""
                    [ ("@doc", Database.boxVal docId) ]
            let! activeLetters =
                scalarInt64 db
                    """SELECT count(*) FROM dead_letters
                       WHERE doc_id = @doc AND stage = 'embed' AND dismissed = 0"""
                    [ ("@doc", Database.boxVal docId) ]
            Assert.Equal(0, faulted)
            Assert.Equal(Reflow.LifecycleRunning, status.Lifecycle)
            Assert.Equal(Reflow.Pending, embed.Outcome)
            Assert.Equal(0L, completionCount)
            Assert.Equal(1L, activeLetters)
            let! embeddingCount = countRows db "embedding" docId
            let! attempts = countRows db "pipeline_stage_attempts" docId
            Assert.Equal(1L, embeddingCount)
            Assert.Equal(0L, attempts)
            let! retried =
                PipelineV5.processStageForDag
                    dag stage db logger gpu 1
                    (TimeSpan.FromMinutes 1.0)
                    Threading.CancellationToken.None
            let! finalStatus = requireStatus dag db operationId
            let finalEmbed =
                finalStatus.Stages
                |> List.find (fun value -> value.StageName = "embed")
            let! finalCompletionCount =
                scalarInt64 db
                    """SELECT count(*) FROM stage_completions
                       WHERE document_id = @doc AND stage_name = 'embed'"""
                    [ ("@doc", Database.boxVal docId) ]
            let! finalActiveLetters =
                scalarInt64 db
                    """SELECT count(*) FROM dead_letters
                       WHERE doc_id = @doc AND stage = 'embed' AND dismissed = 0"""
                    [ ("@doc", Database.boxVal docId) ]
            let! finalLetterCount =
                scalarInt64 db
                    """SELECT count(*) FROM dead_letters
                       WHERE doc_id = @doc AND stage = 'embed'"""
                    [ ("@doc", Database.boxVal docId) ]
            let! finalAttempts = countRows db "pipeline_stage_attempts" docId
            Assert.Equal(1, retried)
            Assert.Equal(Reflow.LifecycleCompleted, finalStatus.Lifecycle)
            Assert.Equal(Reflow.Reran, finalEmbed.Outcome)
            Assert.Equal(1L, finalCompletionCount)
            Assert.Equal(0L, finalActiveLetters)
            Assert.Equal(1L, finalLetterCount)
            Assert.Equal(0L, finalAttempts)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_FinalisationFailureFault_RollsBackDeadLetterAndCapturedRows`` () =
    task {
        let! db = freshDb ()
        try
            let! docId = insertFullyCompletedDocument db
            let dag = TestHelpers.standardV5Dag ()
            let! requested =
                Reflow.request db logger dag docId Reflow.Reembed Reflow.Apply
            let operationId = requireOperationId requested
            let stage =
                { dag.Stages.["embed"] with
                    Process =
                        fun _ _ _ ->
                            Task.FromResult(PipelineV5.Failed "processor failure") }
            let faultyDb =
                throwingTransactionDb db
                    "SET outcome = 'failed', completed_at = datetime('now')"
            let gpu = PipelineV5.createGpuScheduler logger
            let! faulted =
                PipelineV5.processStageForDag
                    dag stage faultyDb logger gpu 1
                    (TimeSpan.FromMinutes 1.0)
                    Threading.CancellationToken.None
            let! status = requireStatus dag db operationId
            let embed =
                status.Stages |> List.find (fun value -> value.StageName = "embed")
            let! activeLetters =
                scalarInt64 db
                    """SELECT count(*) FROM dead_letters
                       WHERE doc_id = @doc AND stage = 'embed' AND dismissed = 0"""
                    [ ("@doc", Database.boxVal docId) ]
            Assert.Equal(0, faulted)
            Assert.Equal(Reflow.LifecycleRunning, status.Lifecycle)
            Assert.Equal(Reflow.Pending, embed.Outcome)
            Assert.Equal(0L, activeLetters)
            let! attempts = countRows db "pipeline_stage_attempts" docId
            Assert.Equal(0L, attempts)
            let! retried =
                PipelineV5.processStageForDag
                    dag stage db logger gpu 1
                    (TimeSpan.FromMinutes 1.0)
                    Threading.CancellationToken.None
            let! finalStatus = requireStatus dag db operationId
            let finalEmbed =
                finalStatus.Stages
                |> List.find (fun value -> value.StageName = "embed")
            let! finalActiveLetters =
                scalarInt64 db
                    """SELECT count(*) FROM dead_letters
                       WHERE doc_id = @doc AND stage = 'embed' AND dismissed = 0"""
                    [ ("@doc", Database.boxVal docId) ]
            let! finalCompletionCount =
                scalarInt64 db
                    """SELECT count(*) FROM stage_completions
                       WHERE document_id = @doc AND stage_name = 'embed'"""
                    [ ("@doc", Database.boxVal docId) ]
            let! finalAttempts = countRows db "pipeline_stage_attempts" docId
            Assert.Equal(0, retried)
            Assert.Equal(Reflow.LifecycleFailed, finalStatus.Lifecycle)
            Assert.Equal(Reflow.Failed, finalEmbed.Outcome)
            Assert.Equal(1L, finalActiveLetters)
            Assert.Equal(0L, finalCompletionCount)
            Assert.Equal(0L, finalAttempts)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_Pipeline_StaleRunningDagIsFailedWithoutBeingClaimed`` () =
    task {
        let! db = freshDb ()
        try
            let! docId = insertFullyCompletedDocument db
            let dag = TestHelpers.standardV5Dag ()
            let! _ =
                db.execNonQuery
                    """DELETE FROM stage_completions
                       WHERE document_id = @doc AND stage_name = 'embed'"""
                    [ ("@doc", Database.boxVal docId) ]
            let! staleId =
                db.execScalar
                    """INSERT INTO reflow_operations
                         (document_id, operation_kind, requested_mode, lifecycle, dag_signature)
                       VALUES (@doc, 'reembed', 'apply', 'running', 'stale')
                       RETURNING id"""
                    [ ("@doc", Database.boxVal docId) ]
            let! _ =
                db.execNonQuery
                    """INSERT INTO reflow_operation_stages
                         (operation_id, stage_name, outcome)
                       VALUES (@operation, 'embed', 'pending')"""
                    [ ("@operation", staleId) ]
            let gpu = PipelineV5.createGpuScheduler logger
            let! processed = processNamedStage dag db gpu "embed"
            let! lifecycle =
                scalarStr db
                    "SELECT lifecycle FROM reflow_operations WHERE id = @id"
                    [ ("@id", staleId) ]
            let! operationError =
                scalarStr db
                    "SELECT error FROM reflow_operations WHERE id = @id"
                    [ ("@id", staleId) ]
            let! outcome =
                scalarStr db
                    """SELECT outcome FROM reflow_operation_stages
                       WHERE operation_id = @id AND stage_name = 'embed'"""
                    [ ("@id", staleId) ]
            let! completions = stageCompletionNames db docId
            Assert.Equal(1, processed)
            Assert.Equal("failed", lifecycle)
            Assert.Equal("failed", outcome)
            Assert.Contains("stale", operationError)
            Assert.Contains("embed", completions)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_GetStatus_TwoConnectionCommitRaceReturnsAtomicSnapshot`` () =
    task {
        let! databases = freshFileDatabases ()
        let entered =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let release =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        try
            let! docId = insertFullyCompletedDocument databases.Writer
            let dag = TestHelpers.standardV5Dag ()
            let! requested =
                Reflow.request
                    databases.Writer logger dag docId Reflow.Reextract Reflow.Apply
            let operationId = requireOperationId requested
            let observer =
                pauseAfterOperationRead databases.Observer entered release
            let statusTask = Reflow.getStatus dag observer operationId
            do! entered.Task
            let! committed =
                databases.Writer.inTransaction
                    (completeStatusLedger operationId)
            match committed with
            | Error error -> failwith error
            | Ok () -> ()
            release.TrySetResult() |> ignore
            let! result = statusTask
            let status =
                match result with Error error -> failwith error | Ok value -> value
            let pending =
                status.Stages |> List.forall (fun stage -> stage.Outcome = Reflow.Pending)
            let reran =
                status.Stages |> List.forall (fun stage -> stage.Outcome = Reflow.Reran)
            let coherent =
                (status.Lifecycle = Reflow.LifecycleRunning && pending)
                || (status.Lifecycle = Reflow.LifecycleCompleted && reran)
            Assert.True(coherent, "Operation and stage rows came from different snapshots")
        finally
            release.TrySetResult() |> ignore
            cleanupFileDatabases databases
    }

let private insertComprehensionOwnedData
    (db: Algebra.Database)
    (docId: int64)
    : Task<unit> =
    task {
        let doc = [ ("@doc", Database.boxVal docId) ]
        let! _ =
            db.execNonQuery
                "INSERT INTO tags(document_id,tag,source) VALUES (@doc,'generated','comprehension')"
                doc
        let! _ =
            db.execNonQuery
                """INSERT INTO contacts(id,name,canonical_name)
                   VALUES ('shared-contact','Shared Contact','shared contact')"""
                []
        let! _ =
            db.execNonQuery
                """INSERT INTO document_contacts(document_id,contact_id,role)
                   VALUES (@doc,'shared-contact','issuer')"""
                doc
        let! _ =
            db.execNonQuery
                """INSERT INTO suggestions
                     (document_id,proposed_category,confidence,status,resolved_at)
                   VALUES (@doc,'tax',0.4,'pending',NULL),
                          (@doc,'tax',0.4,'approved',datetime('now')),
                          (@doc,'tax',0.4,'rejected',datetime('now'))"""
                doc
        return ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_InvalidateComprehension_RemovesOnlyOwnedCurrentData`` () =
    task {
        let! db = freshDb ()
        try
            let! docId = insertFullyCompletedDocument db
            do! markManualClassification db docId
            do! insertComprehensionOwnedData db docId
            let! result =
                Reflow.request
                    db logger (TestHelpers.standardV5Dag ())
                    docId Reflow.Recomprehend Reflow.Apply
            match result with Error error -> failwith error | Ok _ -> ()
            let count sql =
                scalarInt64 db sql [ ("@doc", Database.boxVal docId) ]
            let! comprehensionTags =
                count "SELECT count(*) FROM tags WHERE document_id=@doc AND source='comprehension'"
            let! userTags =
                count "SELECT count(*) FROM tags WHERE document_id=@doc AND source='user'"
            let! links =
                count "SELECT count(*) FROM document_contacts WHERE document_id=@doc"
            let! pending =
                count "SELECT count(*) FROM suggestions WHERE document_id=@doc AND status='pending'"
            let! resolved =
                count "SELECT count(*) FROM suggestions WHERE document_id=@doc AND status IN ('approved','rejected')"
            let! corrections =
                count "SELECT count(*) FROM corrections WHERE document_id=@doc"
            let! contacts = scalarInt64 db "SELECT count(*) FROM contacts" []
            let! document = readDocument db docId
            let reader = Prelude.RowReader(document)
            Assert.Equal(0L, comprehensionTags)
            Assert.Equal(1L, userTags)
            Assert.Equal(0L, links)
            Assert.Equal(0L, pending)
            Assert.Equal(2L, resolved)
            Assert.Equal(1L, corrections)
            Assert.Equal(1L, contacts)
            Assert.Equal("manual", reader.String "classification_tier" "")
            Assert.Equal("manual-review", reader.String "category" "")
        finally
            db.dispose ()
    }

let private processorDag db fs =
    let response =
        """{"document_type":"invoice","confidence":0.95,"summary":"invoice","tags":["finance"],"sender_name":"Telstra","fields":{"amount":89.5}}"""
    let provider = TestHelpers.fakeChatProvider response
    let deps =
        { baseDeps db fs with
            ChatProvider = Some provider
            TriageProvider = Some provider }
    match PipelineV5.buildDag (StagesV5.standardStages deps) with
    | Ok dag -> dag
    | Error error -> failwith error

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_Pipeline_RecomprehendRestoresEmbeddedProjectionAndStableArtifacts`` () =
    task {
        let! db = freshDb ()
        let mem = TestHelpers.memFs ()
        try
            let! docId = insertFullyCompletedDocument db
            do! markManualClassification db docId
            mem.Put "/archive/invoice.pdf.extracted.md" "Invoice total $89.50"
            mem.Put "/archive/thread.comprehension.json" """{"old":true}"""
            let dag = processorDag db mem.Fs
            let! extractBefore = completionTime db docId "extract"
            let! embedBefore = completionTime db docId "embed"
            let! requested =
                Reflow.request
                    db logger dag docId Reflow.Recomprehend Reflow.Apply
            let operationId = requireOperationId requested
            let gpu = PipelineV5.createGpuScheduler logger
            let! triaged = processNamedStage dag db gpu "triage"
            let! interimDocument = readDocument db docId
            let interimReader = Prelude.RowReader(interimDocument)
            Assert.Equal("triaged", interimReader.String "stage" "")
            let! comprehended = processNamedStage dag db gpu "deep-comprehend"
            let! status = requireStatus dag db operationId
            let! document = readDocument db docId
            let reader = Prelude.RowReader(document)
            let! extractAfter = completionTime db docId "extract"
            let! embedAfter = completionTime db docId "embed"
            let! completions = stageCompletionNames db docId
            let! chunks = countRows db "document_chunks" docId
            Assert.Equal(1, triaged)
            Assert.Equal(1, comprehended)
            Assert.Equal(Reflow.LifecycleCompleted, status.Lifecycle)
            Assert.Equal("embedded", reader.String "stage" "")
            Assert.Equal("manual", reader.String "classification_tier" "")
            Assert.Equal("manual-review", reader.String "category" "")
            Assert.Equal(extractBefore, extractAfter)
            Assert.Equal(embedBefore, embedAfter)
            Assert.Equal<Set<string>>(Set.ofList allStageNames, completions)
            Assert.Equal(1L, chunks)
            Assert.True(mem.Fs.fileExists "/archive/thread.comprehension.json")
        finally
            db.dispose ()
    }

// ── Two-connection transaction fencing ───────────────────────────────

let private signalBeforeTransactionAttempt
    (baseDb: Algebra.Database)
    (entered: TaskCompletionSource<unit>)
    : Algebra.Database =
    { baseDb with
        inTransaction =
            fun callback ->
                task {
                    entered.TrySetResult() |> ignore
                    return! baseDb.inTransaction callback
                } }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_TwoConnections_ReadThenWriteSettle_SurvivesConcurrentReflowCommit`` () =
    task {
        let! databases = freshFileDatabases ()
        let entered =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let release =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let transactionAttempted =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        try
            let! docId = insertFullyCompletedDocument databases.Writer
            let! token = Generation.current databases.Writer docId
            // Settle transaction: generation read, barrier, then the write.
            let settleBody (scope: Algebra.TransactionScope) =
                task {
                    entered.TrySetResult() |> ignore
                    do! release.Task
                    let! _ =
                        scope.execNonQuery
                            """INSERT OR REPLACE INTO embedding
                                 (document_id, chunk_count)
                               VALUES (@doc, 7)"""
                            [ ("@doc", Database.boxVal docId) ]
                    return ()
                }
            let settling = Generation.publish databases.Writer token settleBody
            do! entered.Task
            // Run the synchronous BEGIN IMMEDIATE wait away from this
            // continuation so the lock owner can be released deterministically.
            let reflowDb =
                signalBeforeTransactionAttempt
                    databases.Observer transactionAttempted
            let reflowing =
                task {
                    do! Task.Yield()
                    return!
                        Reflow.request
                            reflowDb logger (TestHelpers.standardV5Dag ())
                            docId Reflow.Recomprehend Reflow.Apply
                }
            do! transactionAttempted.Task
            let blocked = not reflowing.IsCompleted
            release.TrySetResult() |> ignore
            let! settled = settling
            let! reflowed = reflowing
            let operationId = requireOperationId reflowed
            let! status =
                requireStatus
                    (TestHelpers.standardV5Dag ())
                    databases.Observer operationId
            let! generation = documentGeneration databases.Observer docId
            let! chunkCount =
                scalarInt64 databases.Observer
                    "SELECT chunk_count FROM embedding WHERE document_id = @doc"
                    [ ("@doc", Database.boxVal docId) ]
            let outcome stageName =
                status.Stages
                |> List.find (fun stage -> stage.StageName = stageName)
                |> fun stage -> stage.Outcome
            Assert.True(
                blocked,
                "The second BEGIN IMMEDIATE must wait while the first writer owns the lock")
            match settled with
            | Generation.Published () -> ()
            | Generation.Superseded ->
                failwith "Settle transaction did not commit (busy snapshot?)"
            Assert.Equal(1L, generation)
            Assert.Equal(7L, chunkCount)
            Assert.Equal(Reflow.LifecycleRunning, status.Lifecycle)
            Assert.Equal(Reflow.Pending, outcome "triage")
            Assert.Equal(Reflow.Pending, outcome "deep-comprehend")
            Assert.Equal(Reflow.Current, outcome "embed")
        finally
            release.TrySetResult() |> ignore
            cleanupFileDatabases databases
    }

// ── Pipeline fault isolation ─────────────────────────────────────────

let private oneShotTransactionFaultDb
    (baseDb: Algebra.Database)
    (marker: string)
    (fired: TaskCompletionSource<unit>)
    : Algebra.Database =
    let faultyScope (scope: Algebra.TransactionScope) : Algebra.TransactionScope =
        { scope with
            execNonQuery =
                fun sql parameters ->
                    let shouldFault =
                        sql.Contains(marker, StringComparison.Ordinal)
                        && fired.TrySetResult()
                    if shouldFault then
                        task { return failwith "Injected finalisation fault" }
                    else scope.execNonQuery sql parameters }
    { baseDb with
        inTransaction =
            fun callback ->
                baseDb.inTransaction (fun scope -> callback (faultyScope scope)) }

let private markStageCurrent (db: Algebra.Database) documentId stageName =
    task {
        let! _ =
            db.execNonQuery
                """INSERT INTO stage_completions (document_id, stage_name)
                   VALUES (@doc, @stage)"""
                [ ("@doc", Database.boxVal documentId)
                  ("@stage", Database.boxVal stageName) ]
        return ()
    }

let private prepareDeepComprehensionInput (db: Algebra.Database) documentId =
    task {
        let! _ =
            db.execNonQuery
                """INSERT INTO triage
                     (document_id, document_type, category, confidence)
                   VALUES (@doc, 'invoice', 'invoices', 0.4)"""
                [ ("@doc", Database.boxVal documentId) ]
        do! markStageCurrent db documentId "triage"
    }

type private RealComprehensionHarness =
    { Db: Algebra.Database
      Mem: TestHelpers.MemFs
      Dag: PipelineV5.Dag
      Stage: PipelineV5.StageDefinition
      Gpu: PipelineV5.GpuScheduler
      StageName: string
      DocumentType: string
      OutputTable: string }

let private realComprehensionHarness db (mem: TestHelpers.MemFs) stageName documentType outputTable =
    let response =
        $"""{{"document_type":"{documentType}","confidence":0.4,"summary":"retry evidence","tags":[]}}"""
    let provider = TestHelpers.fakeChatProvider response
    let deps =
        { baseDeps db mem.Fs with
            ChatProvider = Some provider
            TriageProvider = Some provider }
    let dag =
        match PipelineV5.buildDag (StagesV5.standardStages deps) with
        | Ok value -> value
        | Error error -> failwith error
    { Db = db; Mem = mem; Dag = dag; Stage = dag.Stages.[stageName]
      Gpu = PipelineV5.createGpuScheduler logger; StageName = stageName
      DocumentType = documentType; OutputTable = outputTable }

let private setupRealComprehensionDocument harness suffix =
    task {
        let savedPath = $"/retry/{harness.StageName}-{suffix}.pdf"
        let! documentId = insertBareDocument harness.Db savedPath
        do! insertBareExtractionRow harness.Db documentId
        harness.Mem.Put (savedPath + ".extracted.md") "Invoice retry evidence"
        do! markStageCurrent harness.Db documentId "extract"
        if harness.StageName = "deep-comprehend" then
            do! prepareDeepComprehensionInput harness.Db documentId
        return documentId
    }

let private processRealComprehension harness database =
    PipelineV5.processStageForDag
        harness.Dag harness.Stage database logger harness.Gpu 1
        (TimeSpan.FromMinutes 1.0) Threading.CancellationToken.None

type private RealEffectCounts =
    { Learned: int64
      Evidence: int64
      Pending: int64
      Output: int64
      Completion: int64 }

let private learnedPatternCount harness =
    scalarInt64 harness.Db
        """SELECT count FROM learned_patterns
           WHERE sender_domain = 'test.com' AND document_type = @type"""
        [ ("@type", Database.boxVal harness.DocumentType) ]

let private learnedEvidenceCount harness =
    scalarInt64 harness.Db
        """SELECT count(*) FROM learned_pattern_evidence
           WHERE stage_name = @stage AND document_type = @type"""
        [ ("@stage", Database.boxVal harness.StageName)
          ("@type", Database.boxVal harness.DocumentType) ]

let private pendingSuggestionCount harness documentId =
    scalarInt64 harness.Db
        """SELECT count(*) FROM suggestions
           WHERE document_id = @doc AND status = 'pending'"""
        [ ("@doc", Database.boxVal documentId) ]

let private stageCompletionCount harness documentId =
    scalarInt64 harness.Db
        """SELECT count(*) FROM stage_completions
           WHERE document_id = @doc AND stage_name = @stage"""
        [ ("@doc", Database.boxVal documentId)
          ("@stage", Database.boxVal harness.StageName) ]

let private readRealEffectCounts harness documentId =
    task {
        let! learned = learnedPatternCount harness
        let! evidence = learnedEvidenceCount harness
        let! pending = pendingSuggestionCount harness documentId
        let! output =
            countRows harness.Db harness.OutputTable documentId
        let! completion = stageCompletionCount harness documentId
        return
            { Learned = learned; Evidence = evidence; Pending = pending
              Output = output; Completion = completion }
    }

let private assertRealEffects learned evidence pending output completion actual =
    Assert.Equal(learned, actual.Learned)
    Assert.Equal(evidence, actual.Evidence)
    Assert.Equal(pending, actual.Pending)
    Assert.Equal(output, actual.Output)
    Assert.Equal(completion, actual.Completion)

let private faultRealComprehensionAttempt harness documentId =
    task {
        let fired =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let faulty =
            oneShotTransactionFaultDb
                harness.Db "INSERT OR IGNORE INTO stage_completions" fired
        let! processed = processRealComprehension harness faulty
        let! effects = readRealEffectCounts harness documentId
        Assert.True(fired.Task.IsCompleted)
        Assert.Equal(0, processed)
        assertRealEffects 1L 1L 1L 1L 0L effects
    }

let private retryRealComprehensionAttempt harness documentId =
    task {
        let! processed = processRealComprehension harness harness.Db
        let! effects = readRealEffectCounts harness documentId
        Assert.Equal(1, processed)
        assertRealEffects 1L 1L 1L 1L 1L effects
    }

let private proveDifferentDocumentAddsEvidence harness =
    task {
        let! documentId = setupRealComprehensionDocument harness "second"
        let! processed = processRealComprehension harness harness.Db
        let! effects = readRealEffectCounts harness documentId
        Assert.Equal(1, processed)
        assertRealEffects 2L 2L 1L 1L 1L effects
    }

let private verifyRealComprehensionRetry harness =
    task {
        let! documentId = setupRealComprehensionDocument harness "first"
        do! faultRealComprehensionAttempt harness documentId
        do! retryRealComprehensionAttempt harness documentId
        do! proveDifferentDocumentAddsEvidence harness
    }

[<Theory>]
[<InlineData("triage", "letter", "triage")>]
[<InlineData("deep-comprehend", "invoice", "comprehension")>]
[<Trait("Category", "Integration")>]
let ``PipelineV5_RealComprehension_FinalisationFault_RetriesEffectsExactlyOnce``
    (stageName: string, documentType: string, outputTable: string) =
    task {
        let! db = freshDb ()
        try
            let mem = TestHelpers.memFs ()
            let harness =
                realComprehensionHarness
                    db mem stageName documentType outputTable
            do! verifyRealComprehensionRetry harness
        finally
            db.dispose ()
    }

let private oneShotReaderFaultDb
    (baseDb: Algebra.Database)
    (marker: string)
    (fired: TaskCompletionSource<unit>)
    : Algebra.Database =
    { baseDb with
        execReader =
            fun sql parameters ->
                let shouldFault =
                    sql.Contains(marker, StringComparison.Ordinal)
                    && fired.TrySetResult()
                if shouldFault then task { return failwith "Injected cycle fault" }
                else baseDb.execReader sql parameters }

let private probeStageSchema =
    """CREATE TABLE IF NOT EXISTS probe (
         document_id INTEGER PRIMARY KEY REFERENCES documents(id))"""

let private probeStage (observe: int64 -> unit) : PipelineV5.StageDefinition =
    { Name = "probe"
      DependsOn = []
      OutputTable = "probe"
      Schema = probeStageSchema
      Process =
        fun _ _ execution ->
            task {
                observe execution.DocumentId
                return PipelineV5.Completed
            }
      Gate = None
      GpuModel = None
      Mode = PipelineV5.Channel
      Concurrency = 1 }

let private probeDag (stage: PipelineV5.StageDefinition) : PipelineV5.Dag =
    match PipelineV5.buildDag [ stage ] with
    | Ok dag -> dag
    | Error error -> failwith error

[<Fact>]
[<Trait("Category", "Integration")>]
let ``PipelineV5_Run_FinalisationFault_KeepsCycleAliveAndConvergesWithoutDeadLetter`` () =
    task {
        let! db = freshDb ()
        use cancellation = new Threading.CancellationTokenSource()
        try
            let! firstId = insertBareDocument db "/probe/first.pdf"
            let! secondId = insertBareDocument db "/probe/second.pdf"
            let attempts = Collections.Concurrent.ConcurrentDictionary<int64, int>()
            let secondProcessed =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let firstRetried =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let observe documentId =
                let seen =
                    attempts.AddOrUpdate(
                        documentId, 1, (fun _ (previous: int) -> previous + 1))
                if documentId = secondId then
                    secondProcessed.TrySetResult() |> ignore
                elif documentId = firstId && seen > 1 then
                    firstRetried.TrySetResult() |> ignore
            let stage = probeStage observe
            do! PipelineV5.initSchema db [ stage ]
            let fired =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let faultyDb =
                oneShotTransactionFaultDb
                    db "INSERT OR IGNORE INTO stage_completions" fired
            let gpu = PipelineV5.createGpuScheduler logger
            let running =
                PipelineV5.run
                    (probeDag stage) faultyDb logger gpu 10
                    (TimeSpan.FromMinutes 1.0)
                    (TimeSpan.FromMilliseconds 10.0)
                    cancellation.Token
            do! fired.Task
            do! secondProcessed.Task
            do! firstRetried.Task
            cancellation.Cancel()
            do! running
            let! completions =
                scalarInt64 db
                    "SELECT count(*) FROM stage_completions WHERE stage_name = 'probe'"
                    []
            let! letters = scalarInt64 db "SELECT count(*) FROM dead_letters" []
            let! leases = scalarInt64 db "SELECT count(*) FROM pipeline_stage_attempts" []
            let! evidence =
                scalarInt64 db
                    """SELECT count(*) FROM activity_log
                       WHERE level = 'error' AND category = 'pipeline'
                         AND document_id = @doc"""
                    [ ("@doc", Database.boxVal firstId) ]
            Assert.Equal(2L, completions)
            Assert.Equal(0L, letters)
            Assert.Equal(0L, leases)
            Assert.Equal(1L, evidence)
            Assert.Equal(2, attempts.[firstId])
            Assert.Equal(1, attempts.[secondId])
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``PipelineV5_Run_CycleFault_LogsEvidenceBacksOffAndKeepsRunning`` () =
    task {
        let! db = freshDb ()
        use cancellation = new Threading.CancellationTokenSource()
        try
            let! docId = insertBareDocument db "/probe/cycle.pdf"
            let processed =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let stage = probeStage (fun _ -> processed.TrySetResult() |> ignore)
            do! PipelineV5.initSchema db [ stage ]
            let fired =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let faultyDb = oneShotReaderFaultDb db "FROM documents d" fired
            let gpu = PipelineV5.createGpuScheduler logger
            let running =
                PipelineV5.run
                    (probeDag stage) faultyDb logger gpu 10
                    (TimeSpan.FromMinutes 1.0)
                    (TimeSpan.FromMilliseconds 10.0)
                    cancellation.Token
            do! fired.Task
            do! processed.Task
            cancellation.Cancel()
            do! running
            let! completions =
                scalarInt64 db
                    """SELECT count(*) FROM stage_completions
                       WHERE document_id = @doc AND stage_name = 'probe'"""
                    [ ("@doc", Database.boxVal docId) ]
            let! evidence =
                scalarInt64 db
                    """SELECT count(*) FROM activity_log
                       WHERE level = 'error' AND category = 'pipeline'
                         AND document_id IS NULL"""
                    []
            let! letters = scalarInt64 db "SELECT count(*) FROM dead_letters" []
            Assert.Equal(1L, completions)
            Assert.Equal(1L, evidence)
            Assert.Equal(0L, letters)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reflow_Pipeline_FinalisationFault_ReleasesLeaseWithoutDeadLetterAndConverges`` () =
    task {
        let! db = freshDb ()
        try
            let! docId = insertFullyCompletedDocument db
            let dag = TestHelpers.standardV5Dag ()
            let! requested =
                Reflow.request db logger dag docId Reflow.Reembed Reflow.Apply
            let operationId = requireOperationId requested
            let fired =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let faultyDb =
                oneShotTransactionFaultDb
                    db "INSERT OR IGNORE INTO stage_completions" fired
            let gpu = PipelineV5.createGpuScheduler logger
            let embed = dag.Stages.["embed"]
            let! faulted =
                PipelineV5.processStageForDag
                    dag embed faultyDb logger gpu 10
                    (TimeSpan.FromMinutes 1.0)
                    Threading.CancellationToken.None
            let! duringStatus = requireStatus dag db operationId
            let! duringLeases =
                scalarInt64 db "SELECT count(*) FROM pipeline_stage_attempts" []
            let! duringLetters =
                scalarInt64 db
                    "SELECT count(*) FROM dead_letters WHERE doc_id = @doc"
                    [ ("@doc", Database.boxVal docId) ]
            let! duringCompletions = stageCompletionNames db docId
            let embedOutcome (status: Reflow.OperationStatus) =
                status.Stages
                |> List.find (fun value -> value.StageName = "embed")
                |> fun value -> value.Outcome
            // Truthful recovery: lease released, nothing false written, work
            // still owed and still claimable on the next cycle.
            Assert.Equal(0, faulted)
            Assert.Equal(0L, duringLeases)
            Assert.Equal(0L, duringLetters)
            Assert.Equal(Reflow.LifecycleRunning, duringStatus.Lifecycle)
            Assert.Equal(Reflow.Pending, embedOutcome duringStatus)
            Assert.False(duringCompletions.Contains "embed")
            let! retried =
                PipelineV5.processStageForDag
                    dag embed db logger gpu 10
                    (TimeSpan.FromMinutes 1.0)
                    Threading.CancellationToken.None
            let! finalStatus = requireStatus dag db operationId
            let! finalLetters =
                scalarInt64 db
                    "SELECT count(*) FROM dead_letters WHERE doc_id = @doc"
                    [ ("@doc", Database.boxVal docId) ]
            let! finalCompletions = stageCompletionNames db docId
            Assert.Equal(1, retried)
            Assert.Equal(Reflow.LifecycleCompleted, finalStatus.Lifecycle)
            Assert.Equal(Reflow.Reran, embedOutcome finalStatus)
            Assert.Equal(0L, finalLetters)
            Assert.True(finalCompletions.Contains "embed")
        finally
            db.dispose ()
    }
