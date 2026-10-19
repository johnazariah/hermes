module Hermes.Tests.TransactionSavepointTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core

let private timeout = TimeSpan.FromSeconds 10.0

let private insertSql =
    "INSERT INTO transaction_probe(name) VALUES (@name)"

let private failAfterWrite fail (scope: Algebra.TransactionScope) =
    task {
        let! _ = scope.execNonQuery insertSql [ "@name", Database.boxVal "inner" ]
        do! fail ()
        return Ok ()
    }

let private catchInner fail verify (scoped: Algebra.Database) =
    task {
        let! _ = scoped.execNonQuery insertSql [ "@name", Database.boxVal "outer" ]
        let! observed =
            Assert.ThrowsAnyAsync<Exception>(
                Func<Task>(fun () -> scoped.inTransaction (failAfterWrite fail) :> Task))
        verify observed
    }

let private readNames (db: Algebra.Database) =
    task {
        let! rows = db.execReader "SELECT name FROM transaction_probe ORDER BY name" []
        return rows |> List.map (fun row -> (Prelude.RowReader row).String "name" "")
    }

let private acceptedRead (scope: Algebra.TransactionScope) =
    task {
        let! value = scope.execScalar "SELECT 7" []
        Assert.Equal(7L, unbox<int64> value)
        return Ok ()
    }

let private verifyLaterTransactions (db: Algebra.Database) =
    task {
        let! accepted = (db.inTransaction acceptedRead).WaitAsync(timeout)
        Assert.Equal<Result<unit, string>>(Ok (), accepted)
        let! value =
            (db.transaction.Run(fun scoped -> scoped.execScalar "SELECT 8" [])).WaitAsync(timeout)
        Assert.Equal(8L, unbox<int64> value)
        let! names = readNames db
        Assert.Equal<string list>([ "outer" ], names)
    }

let private verifyCleanup fail verify =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ = db.execNonQuery "CREATE TABLE transaction_probe(name TEXT PRIMARY KEY)" []
            do! (db.transaction.Run(catchInner fail verify)).WaitAsync(timeout)
            let! names = readNames db
            Assert.Equal<string list>([ "outer" ], names)
            do! verifyLaterTransactions db
        finally db.dispose ()
    }

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("Category", "Integration")>]
let ``Caught unexpected inner failure cannot commit its writes with the outer sentinel`` ioFailure =
    let error: exn =
        if ioFailure then IOException "deliberate inner IO failure"
        else Failure "deliberate ordinary callback failure"
    verifyCleanup
        (fun () -> Task.FromException<unit>(error))
        (fun observed -> Assert.Same(error, observed))

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Caught inner cancellation rolls back and releases its savepoint before outer commit`` () =
    task {
        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()
        do! verifyCleanup
                (fun () -> Task.FromCanceled<unit>(cancellation.Token))
                (fun observed ->
                    let canceled = Assert.IsAssignableFrom<OperationCanceledException>(observed)
                    Assert.Equal(cancellation.Token, canceled.CancellationToken))
    }
