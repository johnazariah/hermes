module Hermes.Tests.EmailChannelFailureTests

open System
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport

let private rejectWatermarkUpdate (fixture: Fixture) : Task<unit> =
    task {
        let! _ =
            fixture.Database.execNonQuery
                """CREATE TRIGGER reject_channel_watermark
                   BEFORE UPDATE OF last_sync_at ON sync_state
                   BEGIN SELECT RAISE(ABORT, 'synthetic watermark failure'); END"""
                []
        return ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Watermark persistence failure reports an error without losing registered channel sources`` () =
    withFixture (fun fixture ->
        task {
            let initial = DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero)
            do! seedWatermark fixture initial
            do! rejectWatermarkUpdate fixture
            let incoming = message "watermark-one" "watermark-thread" (Some "watermark body")
            let feed = single incoming [ attachment "source.pdf" "watermark attachment" ] |> since initial
            let! result = run fixture feed 1
            Assert.Equal(2, result.Documents.Length)
            Assert.Contains<string>(result.Summary.Errors, Predicate<string>(fun text -> text.Contains("synthetic watermark failure")))
            for document in result.Documents do do! assertRegistered fixture document
            let! watermark = EmailSync.loadSyncState fixture.Database fixture.Account
            Assert.Equal(Some initial, watermark)
        })

let private rejectBodyRegistration (fixture: Fixture) : Task<unit> =
    task {
        let! _ =
            fixture.Database.execNonQuery
                """CREATE TRIGGER reject_channel_body_registration
                   BEFORE INSERT ON documents WHEN new.source_type = 'email_body'
                   BEGIN SELECT RAISE(ABORT, 'synthetic body registration failure'); END"""
                []
        return ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Failed body registration reports failure and retains history and watermark`` () =
    withFixture (fun fixture ->
        task {
            let pair = bodyPair ()
            let! previous = seedBody fixture pair.Previous
            let initial = DateTimeOffset(2025, 2, 28, 12, 0, 0, TimeSpan.Zero)
            do! seedWatermark fixture initial
            do! rejectBodyRegistration fixture
            let! result = run fixture (single pair.Replacement [] |> since initial) 1
            do! assertRegistered fixture previous
            assertStorageFailure result
            do! assertAcknowledged fixture pair.Replacement.ProviderId 0L
            let! watermark = EmailSync.loadSyncState fixture.Database fixture.Account
            Assert.Equal(Some initial, watermark)
            Assert.NotEmpty(result.Summary.Errors)
        })
