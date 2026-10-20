module Hermes.Tests.ServiceAccessHostTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Xunit
open Hermes.Core.LocalAccess
open Hermes.Core.OwnerControlPipe
open Hermes.Service
open Hermes.Tests.ServiceAccessHostTestSupport
open Hermes.Tests.ServiceAccessHostFixture
open Hermes.Tests.ServiceAccessHostSessionSupport

let private assertStartingOwner (server: Server) : Task<unit> =
    task {
        let! captured = wait server.Captured
        let! outcome =
            ownerRequest captured.Context Hermes.Core.LocalAccessWire.Operation.Status
        let response = ok outcome
        Assert.Equal(captured.Context.Target.ConfigKey, response.Instance.ConfigKey)
        match response.Outcome |> ok with
        | Hermes.Core.LocalAccessWire.Reply.Status status ->
            Assert.Equal(Hermes.Core.LocalAccessWire.Phase.Starting, status.Phase)
        | _ -> failwith "Expected starting owner status."
    }

let private assertActualIdentity (server: Server) : Task<unit> =
    task {
        let! captured = wait server.Captured
        use current = Process.GetCurrentProcess()
        Assert.Equal(current.Id, captured.Context.Identity.ProcessId)
        Assert.Equal(
            DateTimeOffset(current.StartTime.ToUniversalTime()),
            captured.Context.Identity.ProcessStartTimeUtc)
        let! reply = get server accessPath
        Assert.Equal(200, reply.Status)
        let identity = json reply
        Assert.Equal(current.Id, identity.GetProperty("processId").GetInt32())
        Assert.Equal(server.Origin, field "origin" identity)
        Assert.Equal(
            captured.Context.Identity.ProcessStartTimeUtc,
            identity.GetProperty("processStartTimeUtc").GetDateTimeOffset())
    }

let private assertClosedAuthority
    (publicAccess: PublicAccess)
    (credential: string)
    : Task<unit> =
    task {
        let context: SessionContext =
            { Credential =
                credential |> SessionCredential.tryParse |> ok
                |> CredentialInput.Present
              BrowserOrigin = None }
        let! outcome =
            publicAccess.Admit (Set.singleton Capability.Mutate)
                context CancellationToken.None
        outcome
        |> Result.map (fun _ -> failwith "Stopped authority admitted a credential.")
        |> Result.defaultWith (fun error ->
            Assert.Equal(AccessError.AccessUnavailable, error))
    }

[<Fact>]
let ``bound HTTP remains closed until private control readiness`` () : Task =
    withServer ignore (fun server -> task {
        do! wait server.Probe.Listening.Task
        let! health = get server healthPath
        assertUnavailable health
        let! archive = get server archivePath
        assertUnavailable archive
        let! model = post server modelPath None "{}"
        assertUnavailable model
        Assert.Empty(server.Probe.Effects)
        Assert.False(server.Probe.WorkerStarted.Task.IsCompleted)
        do! assertStartingOwner server
        do! becomeReady server
        let! ready = get server healthPath
        Assert.Equal(200, ready.Status)
        do! assertActualIdentity server
        let! accepted = get server archivePath
        Assert.Equal(204, accepted.Status)
        Assert.Equal<Effect>([| Effect.Archive |], server.Probe.Effects.ToArray())
    })

[<Fact>]
let ``unclassified application endpoint fails before HTTP or owner binding`` () : Task =
    let unclassified (app: WebApplication) : unit =
        app.MapGet("/unclassified", Func<IResult>(fun () -> Results.Ok()))
        |> ignore
    withServer unclassified (fun server -> task {
        let! result = wait server.Run
        Assert.Equal(1, result.ExitCode)
        Assert.Equal(Lifetime.Quiesced, result.Disposition)
        Assert.False(server.Probe.ControlStarted.Task.IsCompleted)
        Assert.False(server.Probe.WorkerStarted.Task.IsCompleted)
        Assert.Empty(server.Probe.Effects)
        assertPortReleased server
    })

[<Fact>]
let ``control startup failure tears down the already bound HTTP listener`` () : Task =
    withServer ignore (fun server -> task {
        do! wait server.Probe.Listening.Task
        let! health = get server healthPath
        assertUnavailable health
        server.Probe.Release.SetResult(Error PipeFailure.OwnershipUnavailable)
        let! result = wait server.Run
        Assert.Equal(1, result.ExitCode)
        Assert.Equal(Lifetime.Quiesced, result.Disposition)
        Assert.False(server.Probe.WorkerStarted.Task.IsCompleted)
        Assert.Empty(server.Probe.Effects)
        let! captured = wait server.Captured
        Assert.True(Option.isNone (captured.Context.Public()))
        assertPortReleased server
    })

[<Fact>]
let ``pending pairing grants no mutation and private approval enables real HTTP work`` () : Task =
    withServer ignore (fun server -> task {
        do! becomeReady server
        let! pairing = createPairing server
        let! pending = exchange server pairing
        Assert.Equal(202, pending.Status)
        let! denied = post server mutationPath None """{"value":"pending"}"""
        Assert.Equal(401, denied.Status)
        Assert.False(File.Exists(Path.Combine(server.Probe.Root, "accepted.txt")))
        Assert.Empty(server.Probe.Effects)
        do! approve server pairing
        let! issued = exchange server pairing
        Assert.Equal(201, issued.Status)
        let credential = json issued |> field "accessToken"
        let! accepted = post server mutationPath (Some credential) """{"value":"approved"}"""
        Assert.Equal(204, accepted.Status)
        Assert.Equal(
            """{"value":"approved"}""",
            File.ReadAllText(Path.Combine(server.Probe.Root, "accepted.txt")))
    })

let private verifyTerminalFailure (server: Server) : Task<unit> =
    task {
        let! captured = wait server.Captured
        let publicAccess =
            captured.Context.Public()
            |> Option.defaultWith (fun () -> failwith "Expected ready authority.")
        let! credential = issue server
        let! fatal = wait server.Probe.Fatal.Task
        fatal PipeFailure.ListenerFailed
        do! wait server.Probe.WorkerDraining.Task
        Assert.False(server.Run.IsCompleted)
        Assert.True(server.Probe.QueuesClosed.Task.IsCompleted)
        Assert.True(Option.isNone (captured.Context.Public()))
        do! assertClosedAuthority publicAccess credential
        server.Probe.JoinRelease.SetResult(())
        let! result = wait server.Run
        Assert.Equal(1, result.ExitCode)
        Assert.True(server.Probe.WorkerStopped.Task.IsCompleted)
        assertPortReleased server
    }

[<Fact>]
let ``terminal control failure revokes authority and joins workers before returning`` () : Task =
    withServer ignore (fun server -> task {
        do! becomeReady server
        do! verifyTerminalFailure server
    })

[<Fact>]
let ``unknown paths and wrong methods remain truthful terminal errors`` () : Task =
    withServer ignore (fun server -> task {
        do! becomeReady server
        let! missing = get server "/api/host/not-registered"
        Assert.Equal(404, missing.Status)
        let! method = post server archivePath None "{}"
        Assert.Equal(405, method.Status)
        Assert.Empty(server.Probe.Effects)
    })
