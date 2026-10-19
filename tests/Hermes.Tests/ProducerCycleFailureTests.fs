module Hermes.Tests.ProducerCycleFailureTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport
open Hermes.Tests.ProducerCycleTestSupport

let private verifyFailure scenario failedFactory expectedLog =
    task {
        let previous = scenario.Context.Clock.utcNow().AddDays(-3.0)
        do! seedWatermark scenario.Fixture previous
        let incoming = message "healthy-message" "healthy-thread" (Some "healthy body")
        let create (account: Domain.AccountConfig) =
            if account.Label = scenario.Fixture.Account then failedFactory ()
            else Task.FromResult (single incoming [])
        let context = withAccounts scenario [ scenario.Fixture.Account; "healthy" ] create
        do! run context
        do! assertWatermark scenario scenario.Fixture.Account previous 0L
        do! assertWatermark scenario "healthy" (context.Clock.utcNow ()) 1L
        let! rows = documents scenario
        let document = rows |> List.exactlyOne
        Assert.Equal("healthy", required "account" document)
        do! assertRegistered scenario.Fixture document
        assertLog scenario expectedLog
    }

[<Fact>]
let ``Provider creation failure is logged preserves its watermark and does not prevent the next account`` () =
    withScenario (fun scenario ->
        verifyFailure scenario
            (fun () -> Task.FromException<Algebra.EmailProvider>(IOException("synthetic provider creation failure")))
            $"Email sync failed for {scenario.Fixture.Account}: synthetic provider creation failure")

[<Fact>]
let ``Enumeration errors are reported by the cycle without advancing the failed account`` () =
    withScenario (fun scenario ->
        let source =
            { TestHelpers.emptyProvider with
                listStubPage = fun _ _ _ ->
                    Task.FromException<Algebra.StubPage>(IOException("synthetic enumeration failure")) }
        verifyFailure scenario (fun () -> Task.FromResult source)
            $"Email sync [{scenario.Fixture.Account}]: Channel enumeration failed: synthetic enumeration failure")

[<Fact>]
let ``Consumer fetch errors are reported without acknowledging the message or advancing its account`` () =
    withScenario (fun scenario ->
        let incoming = message "unfetchable" "unfetchable-thread" (Some "unavailable body")
        let source =
            { single incoming [] with
                getFullMessage = fun _ ->
                    Task.FromException<Domain.EmailMessage>(IOException("synthetic message fetch failure")) }
        verifyFailure scenario (fun () -> Task.FromResult source)
            "synthetic message fetch failure")

[<Fact>]
let ``Unrequested provider cancellation is a logged account failure rather than owner cancellation`` () =
    withScenario (fun scenario ->
        verifyFailure scenario
            (fun () ->
                Task.FromException<Algebra.EmailProvider>(
                    OperationCanceledException("synthetic unrequested provider cancellation")))
            $"Email sync failed for {scenario.Fixture.Account}: synthetic unrequested provider cancellation")
