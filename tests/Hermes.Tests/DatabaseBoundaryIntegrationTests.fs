module Hermes.Tests.DatabaseBoundaryIntegrationTests

open System
open System.Text
open System.Threading.Tasks
open Xunit
open Hermes.Core

let private timeout = TimeSpan.FromSeconds 10.0
let private stagedBytes = Encoding.UTF8.GetBytes "immutable staged bytes"
let private outerAbortMessage = "abort outer transaction"

let private withDb verify =
    task {
        let db = TestHelpers.createDb ()
        try
            do! verify db
        finally
            db.dispose ()
    }

let private insert (scope: Algebra.TransactionScope) =
    task {
        let! _ =
            scope.execNonQuery
                """INSERT INTO documents(source_type, saved_path, category, sha256)
                   VALUES ('manual_drop', '/archive/boundary.pdf', 'unsorted', @sha)"""
                [ "@sha", Database.boxVal (SourceFiles.hash stagedBytes) ]
        return ()
    }

let private reject scope =
    task {
        do! insert scope
        return Error "deliberate rejection"
    }

let private count (db: Algebra.Database) table =
    task {
        let! value = db.execScalar $"SELECT COUNT(*) FROM {table}" []
        return unbox<int64> value
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Scoped Result rollback uses the generic transaction without reentering its gate`` () =
    withDb (fun db ->
        task {
            let work =
                db.transaction.Run(fun scoped ->
                    task {
                        let! result = scoped.inTransaction reject
                        let! remaining = count scoped "documents"
                        Assert.Equal(0L, remaining)
                        return result
                    })
            let! result = work.WaitAsync(timeout)
            Assert.Equal<Result<unit, string>>(Error "deliberate rejection", result)
        })

let private accept scope : Task<Result<unit, string>> =
    task {
        do! insert scope
        return Ok ()
    }

let private abortOuter (db: Algebra.Database) (files: TestHelpers.MemFs) : Task<unit> =
    db.transaction.Run(fun scoped ->
        task {
            do! SourceFiles.writeNew scoped files.Fs "/archive/boundary.pdf"
                    stagedBytes
            let! accepted = scoped.inTransaction accept
            Assert.True(Result.isOk accepted)
            let! documents = count scoped "documents"
            let! claims = count scoped "archive_source_claims"
            Assert.Equal(1L, documents)
            Assert.Equal(1L, claims)
            return invalidOp outerAbortMessage
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Outer rollback includes nested reflow-style writes and ingestion source claims`` () =
    withDb (fun db ->
        task {
            let files = TestHelpers.memFs ()
            let failed = abortOuter db files
            let! error =
                Assert.ThrowsAsync<InvalidOperationException>(
                    Func<Task>(fun () -> failed.WaitAsync(timeout) :> Task))
            Assert.Equal(outerAbortMessage, error.Message)
            let! documents = count db "documents"
            let! claims = count db "archive_source_claims"
            Assert.Equal(0L, documents)
            Assert.Equal(0L, claims)
            Assert.Equal(Some "immutable staged bytes", files.Get "/archive/boundary.pdf")
        })

type private Pending =
    { Root: Task<Result<unit, string>>
      Generic: Task<obj | null>
      Query: Task<obj | null>
      Release: TaskCompletionSource<unit> }

let private signal () =
    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

let private hold (entered: TaskCompletionSource<unit>) (release: TaskCompletionSource<unit>) _ =
    task {
        entered.TrySetResult() |> ignore
        do! release.Task
        return Ok ()
    }

let private pause (db: Algebra.Database) =
    task {
        let entered, release = signal (), signal ()
        let root = db.inTransaction (hold entered release)
        do! entered.Task.WaitAsync(timeout)
        return
            { Root = root
              Generic = db.transaction.Run(fun scoped -> scoped.execScalar "SELECT 7" [])
              Query = db.execScalar "SELECT 8" []
              Release = release }
    }

let private finish (pending: Pending) =
    task {
        pending.Release.TrySetResult() |> ignore
        let! result = pending.Root.WaitAsync(timeout)
        let! generic = pending.Generic.WaitAsync(timeout)
        let! query = pending.Query.WaitAsync(timeout)
        Assert.True(Result.isOk result)
        Assert.Equal(7L, unbox<int64> generic)
        Assert.Equal(8L, unbox<int64> query)
    }

let private verifySerialization db =
    task {
        let! pending = pause db
        try
            do! Task.Delay 100
            Assert.False(pending.Generic.IsCompleted)
            Assert.False(pending.Query.IsCompleted)
            do! finish pending
        finally
            pending.Release.TrySetResult() |> ignore
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Result transactions generic transactions and ordinary commands share one gate`` () =
    withDb verifySerialization
