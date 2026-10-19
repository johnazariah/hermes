module Hermes.Tests.LegacyEmailRetryTests

open System
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport
open Hermes.Tests.LegacyEmailRetryFixtures

let private assertLegacySuccess (fixture: Fixture) (input: Attempt)
    (result: EmailSync.SyncResult) : Task<unit> =
    task {
        Assert.Equal(1, result.MessagesProcessed)
        Assert.Equal(2, result.AttachmentsDownloaded)
        Assert.Empty(result.Errors)
        do! assertRegisteredEmail fixture input
        let! watermark = EmailSync.loadSyncState fixture.Database fixture.Account
        Assert.Equal(input.Incoming.Date, watermark)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Legacy late attachment failure rolls back acknowledgement and processing count before retry`` () =
    withFixture (fun fixture ->
        task {
            let input = attempt fixture
            let initial = DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero)
            do! seedWatermark fixture initial
            do! rejectSecond fixture
            let! failed = runLegacy fixture input
            do! assertLegacyFailure fixture input initial failed
            do! allowSecond fixture
            let! retried = runLegacy fixture input
            do! assertLegacySuccess fixture input retried
        })

let private assertBackfillHeld (fixture: Fixture) (input: Attempt) : Task<unit> =
    task {
        do! assertRolledBack fixture input
        let! state = EmailSync.loadBackfillState fixture.Database fixture.Account
        Assert.Equal(0, state.Scanned)
        Assert.Equal(None, state.PageToken)
        Assert.False(state.Completed)
    }

let private assertBackfillAdvanced (fixture: Fixture) (input: Attempt) : Task<unit> =
    task {
        do! assertRegisteredEmail fixture input
        let! state = EmailSync.loadBackfillState fixture.Database fixture.Account
        Assert.Equal(1, state.Scanned)
        Assert.Equal(Some input.NextToken, state.PageToken)
        Assert.False(state.Completed)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Backfill late attachment failure propagates and retains its page for whole-email retry`` () =
    withFixture (fun fixture ->
        task {
            let input = attempt fixture
            do! rejectSecond fixture
            let! error =
                Assert.ThrowsAsync<SqliteException>(
                    Func<Task>(fun () -> runBackfill fixture input :> Task))
            Assert.Contains("synthetic late legacy attachment failure", error.Message)
            do! assertBackfillHeld fixture input
            do! allowSecond fixture
            let! added, completed = runBackfill fixture input
            Assert.Equal(2, added)
            Assert.False(completed)
            do! assertBackfillAdvanced fixture input
        })
