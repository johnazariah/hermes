module Hermes.Tests.ProducerCycleEmailTests

open System
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport
open Hermes.Tests.EmailHistoryFixtures
open Hermes.Tests.ProducerCycleTestSupport

let private assertEmailSources scenario (file: Domain.EmailAttachment) =
    task {
        let! rows = documents scenario
        Assert.Equal(2, rows.Length)
        for row in rows do
            do! assertRegistered scenario.Fixture row
        let attachment = rows |> List.find (fun row -> required "source_type" row = "email_attachment")
        let! bytes =
            scenario.Context.Fs.readAllBytes
                (fullPath scenario.Fixture (required "saved_path" attachment))
        Assert.True(file.Content = bytes)
        let body = rows |> List.find (fun row -> required "source_type" row = "email_body")
        let text = scenario.Fixture.Files.Get (fullPath scenario.Fixture (required "saved_path" body))
        Assert.Contains("cycle body", text |> Option.defaultValue "")
    }

[<Fact>]
let ``Actual cycle ingests email sources advances its watermark and preserves them on replay`` () =
    withScenario (fun scenario ->
        task {
            let incoming = message "cycle-message" "cycle-thread" (Some "cycle body")
            let file = attachment "cycle.pdf" "synthetic attachment"
            let previous = scenario.Context.Clock.utcNow().AddDays(-2.0)
            do! seedWatermark scenario.Fixture previous
            let provider = single incoming [ file ]
            let context =
                withAccounts scenario [ scenario.Fixture.Account ]
                    (fun _ -> Task.FromResult (since previous provider))
            do! run context
            do! assertEmailSources scenario file
            do! assertWatermark scenario scenario.Fixture.Account (context.Clock.utcNow ()) 1L
            assertLog scenario "1 message(s), 2 attachment(s), 0 duplicate(s), 0 error(s)"
        })

let private replay scenario =
    task {
        let incoming = message "replayed-message" "replayed-thread" (Some "cycle body")
        let file = attachment "replayed.pdf" "synthetic attachment"
        let context =
            withAccounts scenario [ scenario.Fixture.Account ]
                (fun _ -> Task.FromResult (single incoming [ file ]))
        do! run context
        let! before = documents scenario
        let files = snapshot scenario.Fixture
        do! run context
        let! after = documents scenario
        Assert.Equal<Map<string, obj> list>(before, after)
        Assert.Equal<Map<string, string>>(files, snapshot scenario.Fixture)
        do! assertWatermark scenario scenario.Fixture.Account (context.Clock.utcNow ()) 1L
        assertLog scenario "0 message(s), 0 attachment(s), 1 duplicate(s), 0 error(s)"
    }

[<Fact>]
let ``Actual cycle replay preserves document identity bytes claims and message count`` () =
    withScenario replay

[<Fact>]
let ``Cycle preserves processed history user annotations and source identity when a thread receives new mail`` () =
    withScenario (fun scenario ->
        task {
            let pair = prefixPair ()
            let! history = seedProcessedBody scenario.Fixture pair.Previous
            let incoming = [ pair.Previous; pair.Replacement ]
            let source = provider (pages incoming) incoming Map.empty
            let context =
                withAccounts scenario [ scenario.Fixture.Account ] (fun _ -> Task.FromResult source)
            do! run context
            do! assertPreserved scenario.Fixture history
            let! rows = documents scenario
            let recent = rows |> List.find (fun row -> Document.id row <> Document.id history.Document)
            do! assertRegistered scenario.Fixture recent
            Assert.NotEqual<string>(required "saved_path" history.Document, required "saved_path" recent)
            Assert.Equal(2, rows.Length)
            assertLog scenario "1 message(s), 1 attachment(s), 1 duplicate(s), 0 error(s)"
        })

[<Fact>]
let ``Empty accounts do not manufacture watermark progress`` () =
    withScenario (fun scenario ->
        task {
            let context =
                withAccounts scenario [ "empty-first"; "empty-second" ]
                    (fun _ -> Task.FromResult TestHelpers.emptyProvider)
            do! run context
            do! assertCount scenario 0
            do! assertNoWatermark scenario "empty-first"
            do! assertNoWatermark scenario "empty-second"
            assertLog scenario "Email sync [empty-first]: 0 message(s)"
            assertLog scenario "Email sync [empty-second]: 0 message(s)"
        })
