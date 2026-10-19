module Hermes.Tests.ProducerCycleCancellationTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Service
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport
open Hermes.Tests.ProducerCycleTestSupport

[<Fact>]
let ``Precancelled cycles do not begin folder or provider work`` () =
    withScenario (fun scenario ->
        task {
            use cancellation = new CancellationTokenSource()
            let path = drop scenario "not-started.pdf" "retain source"
            cancellation.Cancel()
            do! assertCancelled (ProducerCycle.run scenario.Context cancellation.Token)
            do! assertCount scenario 0
            Assert.Equal(Some "retain source", scenario.Fixture.Files.Get path)
            Assert.Empty(scenario.Messages)
        })

[<Fact>]
let ``Cancellation during provider creation prevents sync and later accounts`` () =
    withScenario (fun scenario ->
        task {
            use cancellation = new CancellationTokenSource()
            let previous = scenario.Context.Clock.utcNow().AddDays(-2.0)
            do! seedWatermark scenario.Fixture previous
            let create (account: Domain.AccountConfig) =
                Assert.Equal(scenario.Fixture.Account, account.Label)
                cancellation.Cancel()
                Task.FromResult TestHelpers.emptyProvider
            let context = withAccounts scenario [ scenario.Fixture.Account; "must-not-start" ] create
            do! assertCancelled (ProducerCycle.run context cancellation.Token)
            do! assertCount scenario 0
            do! assertWatermark scenario scenario.Fixture.Account previous 0L
            do! assertNoWatermark scenario "must-not-start"
        })

[<Fact>]
let ``Cancellation during actual channel sync preserves the watermark and prevents later accounts`` () =
    withScenario (fun scenario ->
        task {
            use cancellation = new CancellationTokenSource()
            let previous = scenario.Context.Clock.utcNow().AddDays(-2.0)
            do! seedWatermark scenario.Fixture previous
            let source =
                { TestHelpers.emptyProvider with
                    listStubPage = fun _ _ _ ->
                        cancellation.Cancel()
                        Task.FromResult<Algebra.StubPage>
                            { Ids = []; NextPageToken = None; ResultSizeEstimate = 0L } }
            let context =
                withAccounts scenario [ scenario.Fixture.Account; "must-not-start" ]
                    (fun _ -> Task.FromResult source)
            do! assertCancelled (ProducerCycle.run context cancellation.Token)
            do! assertWatermark scenario scenario.Fixture.Account previous 0L
            do! assertNoWatermark scenario "must-not-start"
        })
