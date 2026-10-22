namespace Hermes.Tests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core.LocalAccessWire
open Hermes.Core.OwnerControlPipe

module OwnerUiOperatorTests =
    open OwnerUiFixtures
    module Input = Hermes.Service.OwnerUiInput

    [<Fact>]
    let ``preview pairing discovers then matches HTTP then confirms and approves the frozen pin`` () =
        task {
            let rig = make ()
            do! run rig pairArguments |> exit 0
            Assert.Equal<string>(
                [| "create"; "client"; "discover"; "http"; "resolve"; "read"; "resolve"; "approve" |],
                rig.State.Trace.ToArray())
            Assert.Equal<ApprovalPayload>(payload rig.Pairing, Assert.Single(rig.State.Approvals))
            Assert.Contains("Browser origin: http://localhost:5173", rig.State.Output)
            Assert.Contains("API audience: " + rig.Target.ApiOrigin, rig.State.Output)
            Assert.Contains("Permissions: config.read, model.invoke, mutate", rig.State.Output)
            Assert.DoesNotContain(rig.Pairing.Challenge, String.concat "\n" rig.State.Output)
            let identifiers = rig.State.Requests |> Seq.map (fun request -> request.RequestId) |> Seq.toList
            Assert.Equal(4, identifiers |> List.distinct |> List.length)
        }

    [<Fact>]
    let ``open-ui matches identity before the injected browser and reads only code then confirmation`` () =
        task {
            let rig = make ()
            rig.State.Lines.Clear()
            rig.State.Lines.Enqueue(Some userCode)
            rig.State.Lines.Enqueue(Some "YES")
            do! run rig [| "open-ui" |] |> exit 0
            Assert.Equal<string>(
                [| "create"; "client"; "discover"; "http"; "browser"; "read"
                   "resolve"; "read"; "resolve"; "approve" |], rig.State.Trace.ToArray())
        }

    let private withLine (rig: Rig) (line: string option) : Input.Effects =
        { rig.Effects with
            ReadLine = fun _ ->
                rig.State.Trace.Enqueue("read")
                Task.FromResult line }

    [<Theory>]
    [<InlineData("no")>]
    [<InlineData("")>]
    [<InlineData(" yes")>]
    [<InlineData("yes ")>]
    let ``anything other than exact explicit consent grants nothing`` (text: string) =
        task {
            let rig = make ()
            do! runUsing rig (settings rig) (withLine rig (Some text)) pairArguments CancellationToken.None |> exit 1
            Assert.Empty(rig.State.Approvals)
            Assert.DoesNotContain("ordinary", rig.State.Trace)
        }

    [<Fact>]
    let ``foreground EOF never approves`` () =
        task {
            let rig = make ()
            do! runUsing rig (settings rig) (withLine rig None) pairArguments CancellationToken.None |> exit 1
            Assert.Empty(rig.State.Approvals)
        }

    [<Fact>]
    let ``unready owner status prevents HTTP browser input and approval`` () =
        task {
            let rig = make ()
            let effects =
                { rig.Effects with
                    Client = fun target ->
                        let client = rig.Effects.Client target
                        { client with
                            DiscoverStatus = fun request _ ->
                                let response = respond rig.Identity rig.Pairing rig.State request
                                Task.FromResult(Ok { response with Outcome = Ok(Reply.Status { Phase = Phase.Starting; Capabilities = [ "archive.read" ] }) }) } }
            do! runUsing rig (settings rig) effects pairArguments CancellationToken.None |> exit 1
            Assert.DoesNotContain("http", rig.State.Trace)
            Assert.DoesNotContain("read", rig.State.Trace)
            Assert.Empty(rig.State.Approvals)
        }

    [<Fact>]
    let ``transport rejection cannot use HTTP as alternative authority`` () =
        task {
            let rig = make ()
            let effects =
                { rig.Effects with
                    Client = fun target ->
                        { rig.Effects.Client target with
                            DiscoverStatus = fun _ _ ->
                                Task.FromResult(Error PipeFailure.PrincipalRejected) } }
            do! runUsing rig (settings rig) effects [| "open-ui" |] CancellationToken.None |> exit 1
            Assert.DoesNotContain("http", rig.State.Trace)
            Assert.DoesNotContain("browser", rig.State.Trace)
            Assert.Empty(rig.State.Approvals)
        }

    let private interrupted (rig: Rig) (changed: ConcurrentQueue<unit>) : Input.Effects =
        { rig.Effects with
            ReadLine = fun _ -> changed.Enqueue(()); Task.FromResult(Some "yes")
            Client = fun target ->
                let original = rig.Effects.Client target
                { original with
                    Send = fun request peer cancellation ->
                        if changed.IsEmpty then original.Send request peer cancellation
                        else Task.FromResult(Error(PipeFailure.InvalidWire CodecFailure.IdentityMismatch)) } }

    [<Fact>]
    let ``restart during human confirmation cannot repin or approve the new instance`` () =
        task {
            let rig = make ()
            let changed = ConcurrentQueue<unit>()
            do! runUsing rig (settings rig) (interrupted rig changed) pairArguments CancellationToken.None |> exit 1
            Assert.Empty(rig.State.Approvals)
            Assert.Equal(1, rig.State.Trace |> Seq.filter ((=) "discover") |> Seq.length)
            Assert.Equal(1, rig.State.Trace |> Seq.filter ((=) "http") |> Seq.length)
        }

    [<Fact>]
    let ``elapsed pairing expiry while waiting for consent grants nothing`` () =
        task {
            let rig = make ()
            let changed = ConcurrentQueue<unit>()
            let effects =
                { withLine rig (Some "yes") with
                    ReadLine = fun _ -> changed.Enqueue(()); Task.FromResult(Some "yes")
                    Elapsed = fun () -> if changed.IsEmpty then TimeSpan.Zero else TimeSpan.FromSeconds 181.0 }
            do! runUsing rig (settings rig) effects pairArguments CancellationToken.None |> exit 1
            Assert.Empty(rig.State.Approvals)
        }

    [<Fact>]
    let ``explicit cancellation has no pipe HTTP browser or approval effects`` () =
        task {
            let rig = make ()
            use lifetime = new CancellationTokenSource()
            lifetime.Cancel()
            do! runUsing rig (settings rig) rig.Effects pairArguments lifetime.Token |> exit 130
            Assert.Equal<string>([| "create" |], rig.State.Trace.ToArray())
            Assert.Empty(rig.State.Approvals)
        }
