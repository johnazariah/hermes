namespace Hermes.Tests

open System
open System.IO
open System.Threading
open Xunit
open Hermes.Service

module OwnerUiCommandTests =
    open OwnerUiFixtures
    module Input = Hermes.Service.OwnerUiInput

    let private invalid (arguments: string array) =
        task {
            let rig = make ()
            do! run rig arguments |> exit 2
            Assert.Empty(rig.State.Trace)
            Assert.Empty(rig.State.Approvals)
            Assert.Equal<string>([| Input.Usage |], rig.State.Output.ToArray())
        }

    [<Theory>]
    [<InlineData("pair-ui")>]
    [<InlineData("pair-ui invalid")>]
    [<InlineData("pair-ui 0123-4567-89ab")>]
    [<InlineData("open-ui --yes")>]
    [<InlineData("open-ui --port")>]
    [<InlineData("open-ui --config-dir")>]
    [<InlineData("open-ui --config-dir relative")>]
    [<InlineData("open-ui --port 0")>]
    [<InlineData("open-ui --port 65536")>]
    [<InlineData("open-ui --port 2147483648")>]
    [<InlineData("open-ui --port +123")>]
    [<InlineData("open-ui --port 123 --port 123")>]
    [<InlineData("OPEN-UI")>]
    [<InlineData("pair-ui-extra")>]
    [<InlineData("service pair-ui")>]
    let ``invalid owner invocations construct no effect root or ordinary context`` (arguments: string) =
        invalid (arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))

    [<Fact>]
    let ``duplicate directory flags and contaminated ports fail before effect construction`` () =
        task {
            let rig = make ()
            do! invalid [| "open-ui"; "--config-dir"; rig.Target.CanonicalDirectory
                           "--config-dir"; rig.Target.CanonicalDirectory |]
            do! invalid [| "open-ui"; "--port"; " 31991" |]
        }

    [<Theory>]
    [<InlineData("wrong")>]
    [<InlineData("65536")>]
    [<InlineData("-1")>]
    [<InlineData("0")>]
    [<InlineData("2147483648")>]
    [<InlineData(" ")>]
    let ``malformed selected environment ports have no effects`` (value: string) =
        task {
            let rig = make ()
            let environment: Input.Settings = { settings rig with Port = fun () -> value }
            let service =
                ServicePolicy.load (function "HERMES_PORT" -> value | _ -> null)
            Assert.True(Result.isError service)
            do! runUsing rig environment rig.Effects pairArguments CancellationToken.None |> exit 2
            Assert.Empty(rig.State.Trace)
        }

    [<Theory>]
    [<InlineData("31991")>]
    [<InlineData(" 31991 ")>]
    [<InlineData("+31991")>]
    let ``default owner target equals actual ServicePolicy environment selection`` (value: string) =
        task {
            let rig = make ()
            let environment: Input.Settings = { settings rig with Port = fun () -> value }
            let service =
                ServicePolicy.load (function "HERMES_PORT" -> value | _ -> null) |> ok
            let options: Input.Options =
                { Mode = Input.Mode.Pair userCode; ConfigDirectory = None; Port = None }
            let selected = Input.select environment options |> ok
            Assert.Equal(service.Port, selected.Target.Port)
            Assert.Equal(rig.Target, selected.Target)
            do! runUsing rig environment rig.Effects pairArguments CancellationToken.None |> exit 0
        }

    [<Theory>]
    [<InlineData("")>]
    [<InlineData(null)>]
    let ``absent and empty environment ports use the service default`` (value: string | null) =
        let rig = make ()
        let environment: Input.Settings = { settings rig with Port = fun () -> value }
        let service =
            ServicePolicy.load (function "HERMES_PORT" -> value | _ -> null) |> ok
        let options: Input.Options =
            { Mode = Input.Mode.Open; ConfigDirectory = None; Port = None }
        let selected = Input.select environment options |> ok
        Assert.Equal(service.Port, selected.Target.Port)
        Assert.Equal(21741, selected.Target.Port)
        Assert.Empty(rig.State.Trace)

    [<Fact>]
    let ``relative environment directory fails before constructing a client`` () =
        task {
            let rig = make ()
            let environment: Input.Settings =
                { settings rig with ConfigDirectory = fun () -> "relative" }
            do! runUsing rig environment rig.Effects pairArguments CancellationToken.None |> exit 2
            Assert.Empty(rig.State.Trace)
        }

    [<Theory>]
    [<InlineData("--initial-sync-days", "7")>]
    [<InlineData("reindex-files", "--dry-run")>]
    let ``unrelated service and reindex routing retains its lazy ordinary continuation``
        (head: string) (tail: string) =
        task {
            let rig = make ()
            do! run rig [| head; tail |] |> exit 73
            Assert.Equal<string>([| "ordinary" |], rig.State.Trace.ToArray())
            Assert.Empty(rig.State.Output)
        }

    [<Fact>]
    let ``explicit absolute target and port override unused malformed environment defaults`` () =
        task {
            let rig = make ()
            let environment: Input.Settings =
                { ConfigDirectory = fun () -> failwith "Unexpected default directory lookup."
                  Port = fun () -> failwith "Unexpected default port lookup." }
            let arguments =
                [| "pair-ui"; userCode; "--config-dir"; rig.Target.CanonicalDirectory; "--port"; "31991" |]
            do! runUsing rig environment rig.Effects arguments CancellationToken.None |> exit 0
        }

    [<Theory>]
    [<InlineData("yes\n", true)>]
    [<InlineData("YES\r\n", true)>]
    [<InlineData("yes", false)>]
    [<InlineData("", false)>]
    let ``bounded console line reader requires a completed line rather than EOF``
        (text: string) (accepted: bool) =
        task {
            use reader = new StringReader(text)
            let! line = OwnerUiCommand.readLineFrom reader CancellationToken.None
            Assert.Equal(accepted, Option.isSome line)
        }
