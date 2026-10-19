module Hermes.Tests.EmailChannelAtomicTests

open System
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport
open Hermes.Tests.EmailHistoryFixtures

type private Scenario =
    { Previous: History
      Incoming: Domain.EmailMessage
      Feed: Algebra.EmailProvider
      Initial: DateTimeOffset }

let private rejectAttachments (fixture: Fixture) : Task<unit> =
    task {
        let! _ =
            fixture.Database.execNonQuery
                """CREATE TRIGGER reject_atomic_attachment
                   BEFORE INSERT ON documents WHEN new.source_type = 'email_attachment'
                   BEGIN SELECT RAISE(ABORT, 'synthetic late attachment failure'); END"""
                []
        return ()
    }

let private allowAttachments (fixture: Fixture) : Task<unit> =
    task {
        let! _ = fixture.Database.execNonQuery "DROP TRIGGER reject_atomic_attachment" []
        return ()
    }

let private scenario (fixture: Fixture) : Task<Scenario> =
    task {
        let pair = prefixPair ()
        let! previous = seedProcessedBody fixture pair.Previous
        let initial = DateTimeOffset(2025, 2, 28, 12, 0, 0, TimeSpan.Zero)
        do! seedWatermark fixture initial
        let feed = single pair.Replacement [ attachment "later.pdf" "later attachment bytes" ] |> since initial
        return { Previous = previous; Incoming = pair.Replacement; Feed = feed; Initial = initial }
    }

let private assertRolledBack
    (fixture: Fixture) (attempt: Scenario) (result: RunResult) : Task<unit> =
    task {
        assertStorageFailure result
        do! assertAcknowledged fixture attempt.Incoming.ProviderId 0L
        do! assertPreserved fixture attempt.Previous
        let! count = documentCount fixture
        Assert.Equal(1L, count)
        let! watermark = EmailSync.loadSyncState fixture.Database fixture.Account
        Assert.Equal(Some attempt.Initial, watermark)
    }

let private assertRetried
    (fixture: Fixture) (attempt: Scenario) (result: RunResult) : Task<unit> =
    task {
        assertSuccess 1 2 result
        do! assertAcknowledged fixture attempt.Incoming.ProviderId 1L
        for document in result.Documents do do! assertRegistered fixture document
        do! assertPreserved fixture attempt.Previous
        let! count = documentCount fixture
        Assert.Equal(3L, count)
        let! foreignKeys = fixture.Database.execReader "PRAGMA foreign_key_check" []
        Assert.Empty(foreignKeys)
        let! watermark = EmailSync.loadSyncState fixture.Database fixture.Account
        Assert.Equal(Some (fixture.Clock.utcNow ()), watermark)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Late attachment failure preserves processed prefix-sharing history and retries exactly once`` () =
    withFixture (fun fixture ->
        task {
            let! attempt = scenario fixture
            do! rejectAttachments fixture
            let! failed = run fixture attempt.Feed 1
            do! assertRolledBack fixture attempt failed
            do! allowAttachments fixture
            let! retried = run fixture attempt.Feed 1
            do! assertRetried fixture attempt retried
            let before = snapshot fixture
            let! repeated = run fixture (single attempt.Incoming []) 1
            assertSuccess 0 0 repeated
            Assert.Equal(1, repeated.Summary.DuplicatesSkipped)
            Assert.Equal<Map<string, string>>(before, snapshot fixture)
            do! assertPreserved fixture attempt.Previous
        })

let private mixedFeed () : Algebra.EmailProvider =
    let good = message "atomic-good" "good-thread" (Some "successful body")
    let bad = message "atomic-bad" "bad-thread" None
    let messages = [ good; bad ]
    provider (pages messages) messages
        (Map.ofList [ bad.ProviderId, [ attachment "bad.pdf" "retryable attachment" ] ])

let private assertMixedFailure (fixture: Fixture) (result: RunResult) initial =
    task {
        Assert.Equal(1, result.Summary.MessagesProcessed)
        Assert.Equal(1, result.Summary.AttachmentsDownloaded)
        Assert.Equal(0, result.Summary.DuplicatesSkipped)
        Assert.Single(result.Summary.Errors) |> ignore
        do! assertAcknowledged fixture "atomic-good" 1L
        do! assertAcknowledged fixture "atomic-bad" 0L
        do! assertRegistered fixture (source "email_body" result)
        let! watermark = EmailSync.loadSyncState fixture.Database fixture.Account
        Assert.Equal(Some initial, watermark)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Other committed emails remain valid and idempotent while a failed email retries`` () =
    withFixture (fun fixture ->
        task {
            let initial = DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero)
            do! seedWatermark fixture initial
            do! rejectAttachments fixture
            let feed = mixedFeed () |> since initial
            let! first = run fixture feed 1
            do! assertMixedFailure fixture first initial
            do! allowAttachments fixture
            let! second = run fixture feed 1
            assertSuccess 1 1 second
            Assert.Equal(1, second.Summary.DuplicatesSkipped)
            do! assertRegistered fixture (source "email_body" first)
            do! assertRegistered fixture (source "email_attachment" second)
        })
