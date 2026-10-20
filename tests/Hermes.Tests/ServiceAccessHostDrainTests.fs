module Hermes.Tests.ServiceAccessHostDrainTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Service
open Hermes.Tests.ServiceAccessHostTestSupport
open Hermes.Tests.ServiceAccessHostDrainSupport

let private withDirectory (verify: string -> Task<unit>) : Task<unit> =
    task {
        let root = Path.Combine(Path.GetTempPath(), "hermes-drain-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try do! verify root
        finally Directory.Delete(root, true)
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
        let owner = new Hermes.Service.Program.DatabaseScope(Path.Combine(root, "db.sqlite"))
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
        use owner = new Hermes.Service.Program.DatabaseScope(Path.Combine(root, "db.sqlite"))
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
