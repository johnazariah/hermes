module Hermes.Tests.ServiceAccessHostPortTests

open System
open System.Net
open System.Net.Sockets
open System.Threading.Tasks
open Xunit
open Hermes.Core.OwnerControlPipe
open Hermes.Service
open Hermes.Tests.ServiceAccessHostTestSupport
open Hermes.Tests.ServiceAccessHostFixture

let private stopBeforeFinalRebind (server: Server) : Task<unit> =
    task {
        let! health = startingHealth server
        assertUnavailable health
        server.Probe.Release.SetResult(Error PipeFailure.OwnershipUnavailable)
        let! result = wait server.Run
        Assert.Equal(1, result.ExitCode)
        Assert.Equal(Lifetime.Quiesced, result.Disposition)
        Assert.False(server.Probe.WorkerStarted.Task.IsCompleted)
        Assert.Empty(server.Probe.Effects)
    }

let private assertOwnedCollisionIsVisible (server: Server) : unit =
    let port = Uri(server.Origin).Port
    use contender = new TcpListener(IPAddress.Loopback, port)
    contender.ExclusiveAddressUse <- false
    contender.Start()
    let failure =
        Assert.Throws<SocketException>(Action(fun () -> assertPortReleased server))
    Assert.Equal(SocketError.AddressAlreadyInUse, failure.SocketErrorCode)

let private assertExcludedDuringRebind (server: Server) : unit =
    let port = Uri(server.Origin).Port
    Assert.Throws<InvalidOperationException>(Action(fun () ->
        issuePort [ port ] |> ignore))
    |> ignore
    let competitorPort = issuePort (Seq.append [ port ] (seq { 49152 .. 65535 }))
    use competitor = new TcpListener(IPAddress.Loopback, competitorPort)
    competitor.Start()
    assertPortReleased server
    Assert.NotEqual(port, competitorPort)

[<Fact>]
let ``issued host port cannot be reclaimed between shutdown and final rebind`` () : Task =
    withServer ignore (fun server -> task {
        do! stopBeforeFinalRebind server
        assertOwnedCollisionIsVisible server
        assertExcludedDuringRebind server
    })
