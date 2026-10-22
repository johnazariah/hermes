module Hermes.Tests.CanonicalUiHostTests

open System.Threading.Tasks
open Xunit
open Hermes.Service
open Hermes.Tests.ServiceAccessHostTestSupport

module Host = Hermes.Tests.ServiceAccessHostFixture
module Ui = Hermes.Tests.CanonicalUiTestSupport

let private verifyRoute (path: string) (server: Server) : Task<unit> =
    task {
        do! wait server.Probe.Listening.Task
        let! starting = get server path
        assertUnavailable starting
        do! Host.becomeReady server
        let! ready = get server path
        Assert.Equal(200, ready.Status)
        Assert.NotEmpty(ready.Body)
    }

[<Theory>]
[<InlineData("/")>]
[<InlineData("/documents")>]
[<InlineData("/settings")>]
[<InlineData("/assets/main-synthetic.js")>]
let ``Production host admits canonical resources only after readiness`` (path: string) =
    Ui.withFixture (fun fixture ->
        task {
            let bundle =
                CanonicalUi.preflight fixture.BaseDirectory
                |> Result.defaultWith invalidOp
            do! Host.withServer (fun app -> CanonicalUi.attach app bundle)
                    (verifyRoute path)
        })
