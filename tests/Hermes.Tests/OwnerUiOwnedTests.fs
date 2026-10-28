namespace Hermes.Tests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Xunit
open Hermes.Core
open Hermes.Core.LocalAccessWire
open Hermes.Core.OwnerControlPipe
open Hermes.Service

module OwnerUiOwnedTests =
    open OwnerUiFixtures
    module Input = Hermes.Service.OwnerUiInput

    type private DisposeApp =
        { App: WebApplication }
        interface IAsyncDisposable with
            member host.DisposeAsync() = host.App.DisposeAsync()

    type private StopApp =
        { App: WebApplication }
        interface IAsyncDisposable with
            member host.DisposeAsync() =
                ValueTask(task {
                    use dispose: DisposeApp = { App = host.App }
                    use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)
                    do! host.App.StopAsync(deadline.Token)
                })

    let private respondHttp (holder: TaskCompletionSource<Rig>)
        (render: Identity -> byte array) (context: HttpContext) : Task =
        task {
            let! rig = holder.Task.WaitAsync(context.RequestAborted)
            Assert.False(context.Request.Headers.ContainsKey("Authorization"))
            Assert.False(context.Request.Headers.ContainsKey("Origin"))
            Assert.False(context.Request.Headers.ContainsKey("Cookie"))
            Assert.Equal("GET", context.Request.Method)
            rig.State.Trace.Enqueue("http")
            context.Response.ContentType <- "application/json"
            let bytes: byte array = render rig.Identity
            do! context.Response.Body.WriteAsync(ReadOnlyMemory<byte>(bytes), context.RequestAborted).AsTask()
        }

    let private app (holder: TaskCompletionSource<Rig>)
        (render: Identity -> byte array) : WebApplication =
        let options = WebApplicationOptions(ContentRootPath = Path.GetTempPath(), EnvironmentName = "OwnerUiSynthetic")
        let builder = WebApplication.CreateEmptyBuilder(options)
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0") |> ignore
        builder.Logging.ClearProviders() |> ignore
        builder.Services.AddRouting() |> ignore
        let application = builder.Build()
        application.MapGet("/api/access", Func<HttpContext, Task>(respondHttp holder render)) |> ignore
        application

    let private target (application: WebApplication) : OwnerControlTarget.Target =
        let address = application.Urls |> Seq.exactlyOne |> Uri
        let directory = Path.Combine(Path.GetTempPath(), "hermes-owned-cli-" + Guid.NewGuid().ToString("N"))
        OwnerControlTarget.tryCreate directory address.Port |> ok

    let private withPipe (rig: Rig)
        (action: Rig -> CancellationToken -> Task<unit>)
        (cancellation: CancellationToken) : Task<unit> =
        task {
            let dispatch request _ = Task.FromResult(respond rig.Identity rig.Pairing rig.State request)
            let factory = Factory.createCancellable rig.Target dispatch ignore
            use running = factory.Start cancellation
            let! ready = running.Ready
            Assert.Equal<Result<unit, PipeFailure>>(Ok (), ready)
            do! action rig cancellation
            let! stopped = running.Stop()
            Assert.Equal<Result<unit, PipeFailure>>(Ok (), stopped)
        }

    let private withServer (render: Identity -> byte array)
        (action: Rig -> CancellationToken -> Task<unit>) : Task<unit> =
        task {
            use lifetime = new CancellationTokenSource(TimeSpan.FromSeconds 15.0)
            let holder = TaskCompletionSource<Rig>(TaskCreationOptions.RunContinuationsAsynchronously)
            let application = app holder render
            use host: StopApp = { App = application }
            do! application.StartAsync(lifetime.Token)
            let rig = target application |> makeAt
            holder.SetResult(rig)
            do! withPipe rig action lifetime.Token
        }

    let private liveEffects (rig: Rig) : Input.Effects =
        { OwnerUiCommand.systemEffects () with
            Write = rig.State.Output.Enqueue
            ReadLine = read rig.State
            Browser = fun _ _ -> failwith "A real browser must never run in this fixture."
            UtcNow = fun () -> epoch
            Elapsed = fun () -> TimeSpan.Zero }

    let private command (rig: Rig) (cancellation: CancellationToken) : Task<int> =
        ServiceEntry.runEarlyWith (settings rig) (fun () -> liveEffects rig) rig.State.Output.Enqueue
            (fun _ -> failwith "Ordinary service startup is forbidden in this fixture.")
            pairArguments cancellation

    let private positive (rig: Rig) (cancellation: CancellationToken) : Task<unit> =
        task {
            do! command rig cancellation |> exit 0
            Assert.Equal(4, rig.State.Requests.Count)
            Assert.Equal<ApprovalPayload>(payload rig.Pairing, Assert.Single(rig.State.Approvals))
            Assert.Equal(1, rig.State.Trace |> Seq.filter ((=) "http") |> Seq.length)
            let requests = rig.State.Requests.ToArray()
            Assert.Equal(None, requests[0].ExpectedInstanceId)
            Assert.Equal<Operation>(Operation.Status, requests[0].Operation)
            requests |> Array.skip 1 |> Array.iter (fun request ->
                Assert.Equal(Some rig.Identity.InstanceId, request.ExpectedInstanceId))
            Assert.DoesNotContain(rig.Pairing.Challenge, String.concat "\n" rig.State.Output)
        }

    [<Fact>]
    let ``actual same-owner pipe plus isolated HTTP approves only after explicit input`` () =
        withServer publicStatus positive

    let private rejected (rig: Rig) (cancellation: CancellationToken) : Task<unit> =
        task {
            do! command rig cancellation |> exit 1
            Assert.Equal(1, rig.State.Requests.Count)
            Assert.Empty(rig.State.Approvals)
            Assert.DoesNotContain("read", rig.State.Trace)
        }

    [<Fact>]
    let ``actual HTTP PID spoof cannot authorize resolve or approval through an owned pipe`` () =
        let spoof (identity: Identity) = publicStatus { identity with ProcessId = identity.ProcessId + 1 }
        withServer spoof rejected
