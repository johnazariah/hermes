module Hermes.Tests.CoreRegressionSupport

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open Hermes.Core

let timeout = TimeSpan.FromSeconds 10.0
let archiveDir = "/archive"
let stamp = TestHelpers.defaultClock.utcNow().ToString("o")
let payload (id: int64) = $"Original archive content for document {id}"
let savedPath (id: int64) = Path.Combine("local", $"doc-{id}.txt")
let sourcePath (id: int64) = Path.Combine(archiveDir, savedPath id)

type Fixture =
    { Db: Algebra.Database
      Memory: TestHelpers.MemFs }
    interface IDisposable with
        member this.Dispose() = this.Db.dispose ()

type Probe =
    { Logger: Algebra.Logger
      Events: ConcurrentQueue<string>
      Reached: TaskCompletionSource<unit> }

let fixture () : Fixture =
    { Db = TestHelpers.createDb ()
      Memory = TestHelpers.memFs () }

let probe (fragment: string) : Probe =
    let events = ConcurrentQueue<string>()
    let reached = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let record level message =
        let entry = $"{level} {message}"
        events.Enqueue entry
        if entry.Contains(fragment, StringComparison.Ordinal) then
            reached.TrySetResult(()) |> ignore
    { Events = events; Reached = reached
      Logger =
        { info = record "INFO"; warn = record "WARN"
          error = record "ERROR"; debug = record "DEBUG" } }

let saw (observation: Probe) (fragment: string) =
    observation.Events
    |> Seq.exists (fun entry -> entry.Contains(fragment, StringComparison.Ordinal))

let scalarInt64 (value: obj | null) =
    match value with
    | :? int64 as number -> number
    | _ -> failwith $"Expected a SQLite integer, got {value}"

let count (db: Algebra.Database) (table: string) : Task<int64> =
    task {
        let! value = db.execScalar $"SELECT COUNT(*) FROM {table}" []
        return scalarInt64 value
    }

let seed (fixture: Fixture) (id: int64) (stage: string) : Task<unit> =
    task {
        fixture.Memory.Fs.createDirectory(Path.Combine(archiveDir, "local"))
        fixture.Memory.Put (sourcePath id) (payload id)
        let sha = payload id |> Encoding.UTF8.GetBytes |> SourceFiles.hash
        let! _ =
            fixture.Db.execNonQuery
                """INSERT INTO documents
                     (id,stage,source_type,original_name,saved_path,category,sha256,starred,extracted_vendor)
                   VALUES (@id,@stage,'manual_drop',@name,@path,'original',@sha,1,'archive-vendor')"""
                [ "@id", Database.boxVal id; "@stage", Database.boxVal stage
                  "@name", Database.boxVal $"doc-{id}.txt"
                  "@path", Database.boxVal (savedPath id); "@sha", Database.boxVal sha ]
        return ()
    }

let seedMany (fixture: Fixture) (documents: (int64 * string) list) : Task<unit> =
    documents
    |> Prelude.foldTask (fun () (id, stage) -> seed fixture id stage) ()

let documents (fixture: Fixture) : Task<Document.T list> =
    fixture.Db.execReader "SELECT * FROM documents ORDER BY id" []

let row (db: Algebra.Database) (id: int64) : Task<Document.T> =
    task {
        let! rows =
            db.execReader "SELECT * FROM documents WHERE id = @id"
                [ "@id", Database.boxVal id ]
        return rows |> List.exactlyOne
    }

let assertOriginal (fixture: Fixture) (document: Document.T) =
    let id = Document.id document
    let expectedHash = payload id |> Encoding.UTF8.GetBytes |> SourceFiles.hash
    Assert.Equal<string option>(Some "original", Document.decode<string> "category" document)
    Assert.Equal<string option>(Some expectedHash, Document.decode<string> "sha256" document)
    Assert.Equal<string option>(Some (savedPath id), Document.decode<string> "saved_path" document)
    Assert.Equal<int64 option>(Some 1L, Document.decode<int64> "starred" document)
    Assert.Equal<string option>(Some (payload id), fixture.Memory.Get (sourcePath id))

let complete (document: Document.T) : Document.T =
    document
    |> Document.encode "extracted_at" (stamp :> obj)
    |> Document.encode "extracted_vendor" ("generated" :> obj)
    |> Document.encode "stage" ("extracted" :> obj)

let assertExtracted (document: Document.T) =
    Assert.Equal("extracted", Document.stage document)
    Assert.Equal<string option>(Some "generated", Document.decode<string> "extracted_vendor" document)

let workflowStage (processor: Document.T -> Task<Document.T>) : Workflow.StageDefinition =
    { Name = "regression"; OutputKey = "extracted_at"; RequiredKeys = [ "saved_path" ]
      Process = processor; ResourceLock = None; MaxHoldTime = TimeSpan.Zero }

let private capture (work: unit -> Task<'a>) : Task<Result<'a, exn>> =
    task {
        try
            let! value = work ()
            return Ok value
        with error ->
            return Error error
    }

/// Cleanup runs before joining, including when verification fails.
/// Verification failures are rethrown, never converted into successful tests.
let settle cleanup (workers: Task list) verify : Task<unit> =
    task {
        let! outcome = capture verify
        cleanup ()
        do! Task.WhenAll(workers |> List.toArray).WaitAsync(timeout)
        return outcome |> Result.defaultWith raise
    }

let finishNaturally (cleanup: unit -> unit) (workers: Task list) (verify: unit -> Task<unit>) : Task<unit> =
    task {
        let! outcome = capture verify
        if Result.isError outcome then cleanup ()
        let completion = Task.WhenAll(workers |> List.toArray)
        try
            do! completion.WaitAsync(timeout)
        with :? TimeoutException as expired ->
            cleanup ()
            do! completion.WaitAsync(timeout)
            return raise expired
        return outcome |> Result.defaultWith raise
    }

let runWorkflow (stage: Workflow.StageDefinition) (db: Algebra.Database)
    (observation: Probe) (documents: Document.T list) (forward: bool) =
    task {
        use cancellation = new CancellationTokenSource()
        let input = Channel.CreateUnbounded<Document.T>()
        let output = Channel.CreateUnbounded<Document.T>()
        documents |> List.iter (fun document -> Assert.True(input.Writer.TryWrite document))
        input.Writer.Complete()
        let destination = if forward then Some output.Writer else None
        let worker =
            Task.Run(Func<Task>(fun () ->
                Workflow.runStage stage db observation.Logger input.Reader destination cancellation.Token :> Task))
        do! finishNaturally cancellation.Cancel [ worker ] (fun () -> Task.FromResult ())
        Assert.False(cancellation.IsCancellationRequested)
        Assert.True(observation.Reached.Task.IsCompletedSuccessfully, "Expected workflow interaction was not observed")
        return output.Reader
    }

let config () : Domain.HermesConfig =
    { TestHelpers.testConfig archiveDir with Accounts = [] }

let private legacyDeps : Pipeline.Deps =
    { Extractor = Interpreters.nullTextExtractor
      Embedder = None; ChatProvider = None; TriageProvider = None
      ContentRules = []; ComprehensionPrompt = None; TriagePrompt = None
      CreateEmailProvider = fun _ _ ->
          Task.FromException<Algebra.EmailProvider>(
              InvalidOperationException("Unexpected email-provider creation")) }

let private unusedRules : Algebra.RulesEngine =
    { classify = fun _ _ -> invalidOp "Legacy pipeline must not call this rules capability"
      reload = fun () -> Task.FromResult(Ok ()) }

let runLegacy (fixture: Fixture) (fs: Algebra.FileSystem) (observation: Probe)
    (configuration: Domain.HermesConfig) : Task<unit> =
    task {
        use cancellation = new CancellationTokenSource(timeout)
        let worker =
            Pipeline.start fs fixture.Db observation.Logger TestHelpers.defaultClock
                unusedRules legacyDeps configuration "/fake-config" cancellation.Token
        do! settle cancellation.Cancel [ worker :> Task ]
                (fun () -> observation.Reached.Task.WaitAsync(cancellation.Token))
        Assert.True(saw observation "INFO Pipeline stopped")
    }

/// The second scan directory is reached after the unclassified scan.
/// Cancellation does not interrupt the current legacy file-enumeration batch.
let observeLocalScan (fixture: Fixture) (observation: Probe) : Algebra.FileSystem =
    { fixture.Memory.Fs with
        directoryExists = fun path ->
            if fixture.Memory.Norm path = fixture.Memory.Norm (Path.Combine(archiveDir, "local")) then
                observation.Logger.debug "regression local scan reached"
            fixture.Memory.Fs.directoryExists path }
