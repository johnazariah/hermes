module Hermes.Tests.ServiceAccessHostDrainTests

open System
open System.IO
open System.Reflection
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Xunit
open Hermes.Core
open Hermes.Service
open Hermes.Tests.ServiceAccessHostTestSupport
open Hermes.Tests.ServiceAccessHostDrainSupport

module Session = Hermes.Tests.ServiceAccessHostSessionSupport

let private databasePath root = Path.Combine(root, "db.sqlite")

let private clearFixturePool root =
    let path = databasePath root
    // Match Database.openConnection's exact connection string, without opening another connection.
    use pool = new SqliteConnection($"Data Source={path}")
    SqliteConnection.ClearPool(pool)

let private withDirectory (verify: string -> Task<unit>) : Task<unit> =
    task {
        let root = Path.Combine(Path.GetTempPath(), "hermes-drain-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try do! verify root
        finally
            // Owner disposal and admitted-request cleanup have already unwound.
            clearFixturePool root
            Directory.Delete(root, true)
    }

let private abandoned (owner: Hermes.Service.Program.DatabaseScope)
    (path: string) (pending: Pending) (server: Server) : Task<unit> =
    task {
        let! request = startRequest server pending path
        let! captured = wait server.Captured
        use gate = beginStop captured
        do! assertClosed server pending gate
        do! stopHttp server pending gate
        let! outcome = wait server.Run
        Assert.NotEqual(Lifetime.Quiesced, outcome.Disposition)
        Assert.Equal(75, owner.Complete(outcome))
        (owner :> IDisposable).Dispose()
        do! assertPending server pending
        do! complete server pending request
    }

let private verifyAbandoned (root: string) (path: string) : Task<unit> =
    task {
        let owner = new Hermes.Service.Program.DatabaseScope(databasePath root)
        try
            do! withPending (TimeSpan.FromMilliseconds 250.0) owner
                    (abandoned owner path)
        finally
            // The harness releases deliberately abandoned resources only after drain.
            try owner.Reflow.dispose ()
            finally owner.Database.dispose ()
    }

[<Theory>]
[<InlineData("/api/host/pending")>]
[<InlineData("/mcp")>]
let ``Kestrel stop cannot quiesce an admitted noncancelable database user`` (path: string) =
    withDirectory (fun root -> verifyAbandoned root path)

let private verifyCredentialSelection
    (path: string) (requiresSession: bool) : Task<unit> =
    let calls = System.Collections.Concurrent.ConcurrentQueue<unit>()
    let credential = "synthetic-fixture-session"
    let issueCredential () =
        calls.Enqueue(())
        Task.FromResult(credential)
    task {
        let! selected =
            Session.credentialForPath pendingPath issueCredential path
        let expected = if requiresSession then Some credential else None
        Assert.Equal<string option>(expected, selected)
        Assert.Equal((if requiresSession then 1 else 0), calls.Count)
    }

[<Theory>]
[<InlineData("/api/host/pending", true)>]
[<InlineData("/mcp", false)>]
let ``drain setup issues HTTP authority only for the protected API route``
    (path: string) (requiresSession: bool) =
    verifyCredentialSelection path requiresSession

let private quiesce (owner: Hermes.Service.Program.DatabaseScope)
    (pending: Pending) (server: Server) : Task<unit> =
    task {
        let! request = startRequest server pending pendingPath
        let! captured = wait server.Captured
        use gate = beginStop captured
        do! assertClosed server pending gate
        do! stopHttp server pending gate
        Assert.False(server.Run.IsCompleted)
        do! complete server pending request
        let! outcome = wait server.Run
        Assert.Equal(Lifetime.Quiesced, outcome.Disposition)
        Assert.Equal(0, owner.Complete(outcome))
    }

let private normalOwner (root: string) : Task<Algebra.Database> =
    task {
        use owner = new Hermes.Service.Program.DatabaseScope(databasePath root)
        do! withPending (TimeSpan.FromSeconds 20.0) owner (quiesce owner)
        return owner.Database
    }

let private verifyNormal (root: string) : Task<unit> =
    task {
        let! released = normalOwner root
        let query () : Task = released.execScalar "SELECT 52" []
        let! _ = Assert.ThrowsAsync<ObjectDisposedException>(Func<Task>(query))
        ()
    }

[<Fact>]
let ``normal request drain permits quiescence and real database disposal`` () =
    withDirectory verifyNormal

let private collateralPoolFailure =
    "Fixture cleanup invalidated another fixture's active SQLite pool lease."

let private requirePoolMember value =
    value |> Option.ofObj
    |> Option.defaultWith (fun () ->
        invalidOp $"SQLite pool probe unavailable: {typeof<SqliteConnection>.Assembly.FullName}")

// SQLite 9.0.4 test-only probe: DoNotPool's flag, independent of active lease ownership.
let private poolRetentionFlag (connection: SqliteConnection) =
    let flags = BindingFlags.Instance ||| BindingFlags.NonPublic
    let field = typeof<SqliteConnection>.GetField("_innerConnection", flags) |> requirePoolMember
    let inner = field.GetValue(connection) |> requirePoolMember
    let flag = inner.GetType().GetField("_canBePooled", flags) |> requirePoolMember
    if flag.FieldType <> typeof<bool> || flag.IsStatic || not flag.IsPrivate then
        invalidOp $"SQLite pool probe requires a private instance Boolean: {typeof<SqliteConnection>.Assembly.FullName}"
    fun () -> flag.GetValue(inner) |> unbox<bool>

let private scalar (connection: SqliteConnection) sql =
    use command = connection.CreateCommand()
    command.CommandText <- sql
    command.ExecuteScalar()

let private assertPersisted root =
    use connection = new SqliteConnection($"Data Source={databasePath root};Pooling=False")
    connection.Open()
    let value = scalar connection "SELECT value FROM fixture_pool_proof" |> unbox<int64>
    Assert.Equal<int64>(52L, value)
    Assert.True(File.Exists(databasePath root))

let private verifyPoolClear clear (canRetain: unit -> bool) root : Task<unit> =
    use connection = Database.openConnection (databasePath root)
    let canPool = poolRetentionFlag connection
    let handle =
        connection.Handle |> Option.ofObj
        |> Option.defaultWith (fun () ->
            invalidOp "An open fixture SQLite connection must expose its native handle.")
    Assert.NotNull(handle)
    Assert.False(handle.IsClosed)
    scalar connection "CREATE TABLE fixture_pool_proof AS SELECT 52 AS value" |> ignore
    Assert.True(canPool())
    Assert.True(canRetain(), collateralPoolFailure)
    clear root
    Assert.False(canPool())
    Assert.True(canRetain(), collateralPoolFailure)
    connection.Close()
    Assert.True(handle.IsClosed)
    assertPersisted root
    Task.FromResult(())

let private verifyPoolBoundary clear otherRoot : Task<unit> =
    task {
        use retained = Database.openConnection (databasePath otherRoot)
        let canRetain = poolRetentionFlag retained
        Assert.True(canRetain(), collateralPoolFailure)
        do! withDirectory (verifyPoolClear (fun root -> clear root otherRoot) canRetain)
        Assert.True(canRetain(), collateralPoolFailure)
        Assert.Equal<int64>(53L, scalar retained "SELECT 53" |> unbox<int64>)
    }

let private clearBothFixturePools root otherRoot =
    clearFixturePool root
    clearFixturePool otherRoot

[<Fact>]
let ``fixture cleanup releases its own SQLite pool without clearing another fixture pool`` () =
    task {
        do! withDirectory (verifyPoolBoundary (fun root _ -> clearFixturePool root))
        // Deliberate collateral eviction, confined to these two synthetic fixtures.
        let! rejected =
            Assert.ThrowsAsync<Xunit.Sdk.TrueException>(
                Func<Task>(fun () ->
                    withDirectory (verifyPoolBoundary clearBothFixturePools) :> Task))
        Assert.Contains(collateralPoolFailure, rejected.Message)
    }
