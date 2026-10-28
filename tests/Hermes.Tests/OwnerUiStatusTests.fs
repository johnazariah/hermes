namespace Hermes.Tests

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core.LocalAccessWire
open Hermes.Service

module OwnerUiStatusTests =
    open OwnerUiFixtures
    module Input = Hermes.Service.OwnerUiInput

    [<Theory>]
    [<InlineData("processId", "4124")>]
    [<InlineData("processStartTimeUtc", "\"2026-10-19T23:58:59Z\"")>]
    [<InlineData("processStartTimeUtc", "\"2026-10-20T09:59:00+10:00\"")>]
    [<InlineData("instanceId", "\"AQEBAQEBAQEBAQEBAQEBAQ\"")>]
    [<InlineData("origin", "\"http://localhost:31991\"")>]
    [<InlineData("origin", "\"http://127.0.0.1:31992\"")>]
    [<InlineData("configKey", "\"not-an-HTTP-identity-field\"")>]
    [<InlineData("version", "2")>]
    [<InlineData("version", "1.0")>]
    [<InlineData("version", "1e0")>]
    [<InlineData("processId", "0")>]
    [<InlineData("processId", "2147483648")>]
    [<InlineData("state", "\"authenticated\"")>]
    [<InlineData("capabilities", "[\"archive.read\",\"mutate\"]")>]
    [<InlineData("capabilities", "[\"unknown\"]")>]
    [<InlineData("capabilities", "[\"archive.read\",\"archive.read\"]")>]
    [<InlineData("expiresAt", "\"2026-10-20T00:01:00Z\"")>]
    [<InlineData("pairingMethod", "\"httpToken\"")>]
    let ``HTTP identity or schema spoofing stops open-ui before browser or approval``
        (field: string) (replacement: string) =
        task {
            let rig = make ()
            let bytes = publicStatus rig.Identity |> OwnerWireSamples.change None field replacement
            let effects =
                { rig.Effects with
                    Status = fun _ _ ->
                        rig.State.Trace.Enqueue("http")
                        Task.FromResult(Ok bytes) }
            do! runUsing rig (settings rig) effects [| "open-ui" |] CancellationToken.None |> exit 1
            Assert.DoesNotContain("browser", rig.State.Trace)
            Assert.DoesNotContain("read", rig.State.Trace)
            Assert.Empty(rig.State.Approvals)
        }

    [<Fact>]
    let ``strict public status rejects BOM duplicates trailing JSON UTF8 and body overflow`` () =
        let rig = make ()
        let source = publicStatus rig.Identity |> Encoding.UTF8.GetString
        let malformed =
            [ Encoding.UTF8.GetBytes("\uFEFF" + source)
              Encoding.UTF8.GetBytes(source + "{}")
              Encoding.UTF8.GetBytes(source.Replace("\"version\":1", "\"version\":1,\"version\":1"))
              Encoding.UTF8.GetBytes(source.PadRight(4097))
              [| 0xC3uy; 0x28uy |] ]
        for bytes in malformed do
            Assert.Equal<Result<unit, Input.Failure>>(
                Error Input.Failure.Rejected, OwnerUiStatus.matches rig.Identity bytes)

    [<Fact>]
    let ``public archive capability is independent of grantable owner capabilities`` () =
        let rig = make ()
        Assert.Equal<Result<unit, Input.Failure>>(
            Ok (), OwnerUiStatus.matches rig.Identity (publicStatus rig.Identity))
        Assert.Equal<string list>([ "config.read"; "model.invoke"; "mutate" ], rig.Pairing.RequestedCapabilities)

    [<Fact>]
    let ``missing HTTP status is truthful failure rather than service fallback`` () =
        task {
            let rig = make ()
            let effects: Input.Effects =
                { rig.Effects with
                    Status = fun _ _ -> Task.FromResult(Error Input.Failure.Rejected) }
            do! runUsing rig (settings rig) effects pairArguments CancellationToken.None |> exit 1
            Assert.DoesNotContain("ordinary", rig.State.Trace)
            Assert.DoesNotContain("read", rig.State.Trace)
            Assert.Empty(rig.State.Approvals)
        }

    [<Fact>]
    let ``different lexical target cannot borrow the fixture identity`` () =
        task {
            let rig = make ()
            let arguments =
                [| "pair-ui"; userCode; "--config-dir"; rig.Target.CanonicalDirectory + "-alias" |]
            do! run rig arguments |> exit 1
            Assert.DoesNotContain("http", rig.State.Trace)
            Assert.Empty(rig.State.Approvals)
        }
