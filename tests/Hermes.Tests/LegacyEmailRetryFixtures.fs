module Hermes.Tests.LegacyEmailRetryFixtures

open System
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport

[<Literal>]
let private triggerName = "reject_legacy_second_attachment"

[<Literal>]
let private rejectedFile = "second.pdf"

type Attempt =
    { Incoming: Domain.EmailMessage
      Provider: Algebra.EmailProvider
      Account: Domain.AccountConfig
      NextToken: string }

let private account (fixture: Fixture) : Domain.AccountConfig =
    let original = fixture.Config.Accounts.Head
    { original with
        Label = fixture.Account
        Backfill = { original.Backfill with Enabled = true; AttachmentsOnly = true } }

let private pageProvider (source: Algebra.EmailProvider)
    (incoming: Domain.EmailMessage) (next: string) : Algebra.EmailProvider =
    let page: Algebra.MessagePage =
        { Messages = [ incoming ]; NextPageToken = Some next; ResultSizeEstimate = 2L }
    { source with
        listMessagePage = fun token _query _limit ->
            Assert.Equal(None, token)
            Task.FromResult(page) }

let attempt (fixture: Fixture) : Attempt =
    let incoming = message "legacy-atomic" "legacy-thread" None
    let files =
        [ attachment "first.pdf" "first legacy attachment bytes"
          attachment rejectedFile "second legacy attachment bytes" ]
    let next = "synthetic-next-page"
    { Incoming = incoming
      Provider = pageProvider (single incoming files) incoming next
      Account = account fixture
      NextToken = next }

let rejectSecond (fixture: Fixture) : Task<unit> =
    task {
        let! _ =
            fixture.Database.execNonQuery
                $"""CREATE TRIGGER {triggerName}
                    BEFORE INSERT ON documents WHEN new.original_name = '{rejectedFile}'
                    BEGIN SELECT RAISE(ABORT, 'synthetic late legacy attachment failure'); END"""
                []
        return ()
    }

let allowSecond (fixture: Fixture) : Task<unit> =
    task {
        let! _ = fixture.Database.execNonQuery $"DROP TRIGGER {triggerName}" []
        return ()
    }

let runLegacy (fixture: Fixture) (input: Attempt) : Task<EmailSync.SyncResult> =
    EmailSync.syncAccount fixture.Files.Fs fixture.Database fixture.Logger fixture.Clock
        input.Provider fixture.Config fixture.Account

let runBackfill (fixture: Fixture) (input: Attempt) : Task<int * bool> =
    EmailSync.backfillAccount fixture.Files.Fs fixture.Database fixture.Logger fixture.Clock
        input.Provider fixture.Config input.Account

let assertRolledBack (fixture: Fixture) (input: Attempt) : Task<unit> =
    task {
        do! assertAcknowledged fixture input.Incoming.ProviderId 0L
        let! documents = documentCount fixture
        Assert.Equal(0L, documents)
        let! claims = fixture.Database.execScalar "SELECT COUNT(*) FROM archive_source_claims" []
        Assert.Equal(0L, Assert.IsType<int64>(claims))
        let! foreignKeys = fixture.Database.execReader "PRAGMA foreign_key_check" []
        Assert.Empty(foreignKeys)
    }

let assertRegisteredEmail (fixture: Fixture) (input: Attempt) : Task<unit> =
    task {
        do! assertAcknowledged fixture input.Incoming.ProviderId 1L
        let! rows = fixture.Database.execReader "SELECT * FROM documents ORDER BY id" []
        Assert.Equal(2, rows.Length)
        for row in rows do do! assertRegistered fixture (Document.fromRow row)
        let! foreignKeys = fixture.Database.execReader "PRAGMA foreign_key_check" []
        Assert.Empty(foreignKeys)
    }

let assertLegacyFailure (fixture: Fixture) (input: Attempt)
    (initial: DateTimeOffset) (result: EmailSync.SyncResult) : Task<unit> =
    task {
        Assert.Equal(0, result.MessagesProcessed)
        Assert.Equal(0, result.AttachmentsDownloaded)
        Assert.Equal(0, result.DuplicatesSkipped)
        let error = Assert.Single<string>(result.Errors)
        Assert.Contains("synthetic late legacy attachment failure", error)
        do! assertRolledBack fixture input
        let! watermark = EmailSync.loadSyncState fixture.Database fixture.Account
        Assert.Equal(Some initial, watermark)
    }
