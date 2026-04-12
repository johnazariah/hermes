module Hermes.Tests.EmailSyncTests

open System
open System.Threading.Tasks
open Xunit
open Hermes.Core

// ─── Sample data ─────────────────────────────────────────────────────

let private emailTestConfig archiveDir : Domain.HermesConfig =
    { TestHelpers.testConfig archiveDir with
        Accounts =
            [ { Label = "test-account"; Provider = "gmail"
                Backfill = { Domain.BackfillConfig.Enabled = false; Since = None; BatchSize = 50; AttachmentsOnly = true; IncludeBodies = false } } ]
        MinAttachmentSize = 100 }

let sampleMessage : Domain.EmailMessage =
    { ProviderId = "msg-001"
      ThreadId = "thread-001"
      Sender = Some "alice@example.com"
      Subject = Some "Invoice #42"
      Date = Some (DateTimeOffset(2024, 6, 15, 10, 30, 0, TimeSpan.Zero))
      Labels = [ "INBOX"; "IMPORTANT" ]
      HasAttachments = true
      BodyText = Some "Please find the invoice attached." }

let sampleAttachment : Domain.EmailAttachment =
    { FileName = "invoice.pdf"
      MimeType = "application/pdf"
      SizeBytes = 5000L
      Content = Array.init 5000 (fun i -> byte (i % 256)) }

let smallAttachment : Domain.EmailAttachment =
    { FileName = "tiny.txt"
      MimeType = "text/plain"
      SizeBytes = 50L
      Content = Array.init 50 (fun i -> byte (i % 256)) }

// ─── Filename sanitisation tests ─────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_SanitiseFileName_RemovesInvalidChars`` () =
    let result = EmailSync.sanitiseFileName "file<>name|test?.pdf"
    Assert.DoesNotContain("<", result)
    Assert.DoesNotContain(">", result)
    Assert.DoesNotContain("|", result)
    Assert.DoesNotContain("?", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_SanitiseFileName_CollapsesUnderscores`` () =
    let result = EmailSync.sanitiseFileName "file___name"
    Assert.DoesNotContain("___", result)
    Assert.Contains("_", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_SanitiseFileName_EmptyReturnsAttachment`` () =
    let result = EmailSync.sanitiseFileName ""
    Assert.Equal("attachment", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_SanitiseFileName_WhitespaceOnlyReturnsAttachment`` () =
    let result = EmailSync.sanitiseFileName "   "
    Assert.Equal("attachment", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_SanitiseFileName_NormalNameUnchanged`` () =
    let result = EmailSync.sanitiseFileName "invoice.pdf"
    Assert.Equal("invoice.pdf", result)

// ─── Standard name building tests ────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_BuildStandardName_IncludesDateSenderName`` () =
    let date = Some (DateTimeOffset(2024, 3, 15, 0, 0, 0, TimeSpan.Zero))
    let sender = Some "bob@example.com"
    let result = EmailSync.buildStandardName date sender "invoice.pdf"
    Assert.StartsWith("2024-03-15", result)
    Assert.Contains("bob", result)
    Assert.Contains("invoice.pdf", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_BuildStandardName_NoDate_UsesUndated`` () =
    let result = EmailSync.buildStandardName None (Some "x@y.com") "file.pdf"
    Assert.StartsWith("undated", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_BuildStandardName_NoSender_UsesUnknown`` () =
    let result = EmailSync.buildStandardName (Some DateTimeOffset.UtcNow) None "file.pdf"
    Assert.Contains("unknown", result)

// ─── SHA256 tests ────────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_ComputeSha256_DeterministicHash`` () =
    let data = [| 1uy; 2uy; 3uy; 4uy; 5uy |]
    let h1 = EmailSync.computeSha256 data
    let h2 = EmailSync.computeSha256 data
    Assert.Equal(h1, h2)
    Assert.Equal(64, h1.Length)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_ComputeSha256_DifferentDataDifferentHash`` () =
    let h1 = EmailSync.computeSha256 [| 1uy; 2uy; 3uy |]
    let h2 = EmailSync.computeSha256 [| 4uy; 5uy; 6uy |]
    Assert.True(h1 <> h2)

// ─── Sidecar metadata tests ─────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_BuildSidecar_ContainsAllFields`` () =
    let now = DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero)
    let sidecar = EmailSync.buildSidecar "my-account" sampleMessage sampleAttachment "saved.pdf" "abc123" now
    Assert.Equal("email_attachment", sidecar.SourceType)
    Assert.Equal("my-account", sidecar.Account)
    Assert.Equal("msg-001", sidecar.GmailId)
    Assert.Equal("thread-001", sidecar.ThreadId)
    Assert.Equal(Some "alice@example.com", sidecar.Sender)
    Assert.Equal(Some "Invoice #42", sidecar.Subject)
    Assert.Equal("invoice.pdf", sidecar.OriginalName)
    Assert.Equal("saved.pdf", sidecar.SavedAs)
    Assert.Equal("abc123", sidecar.Sha256)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_SerialiseSidecar_ProducesValidJson`` () =
    let now = DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero)
    let sidecar = EmailSync.buildSidecar "acct" sampleMessage sampleAttachment "saved.pdf" "abc" now
    let json = EmailSync.serialiseSidecar sidecar
    Assert.Contains("source_type", json)
    Assert.Contains("email_attachment", json)
    Assert.Contains("gmail_id", json)
    Assert.Contains("msg-001", json)

// ─── Sync state tests ────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_LoadSyncState_NoState_ReturnsNone`` () =
    task {
        let db = TestHelpers.createRawDb ()

        try
            let! _ = db.initSchema ()
            let! state = EmailSync.loadSyncState db "nonexistent"
            Assert.True(state.IsNone)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_LoadSyncState_AfterSync_ReturnsSome`` () =
    task {
        let db = TestHelpers.createRawDb ()

        try
            let! _ = db.initSchema ()

            let! _ =
                db.execNonQuery
                    "INSERT INTO sync_state (account, last_sync_at, message_count) VALUES (@acc, @ts, @cnt)"
                    [ ("@acc", Database.boxVal "test-acct")
                      ("@ts", Database.boxVal "2024-06-15T12:00:00+00:00")
                      ("@cnt", Database.boxVal 5L) ]

            let! state = EmailSync.loadSyncState db "test-acct"
            Assert.True(state.IsSome)
            Assert.Equal(2024, state.Value.Year)
        finally
            db.dispose ()
    }

// ─── Sync account tests ─────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_SyncAccount_NoMessages_ReturnsZeroCounts`` () =
    task {
        let m = TestHelpers.memFs ()
        let db = TestHelpers.createRawDb ()
        let logger = Logging.silent
        let clock = TestHelpers.fixedClock (DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero))
        let config = emailTestConfig "/archive"

        try
            let! _ = db.initSchema ()
            let! result = EmailSync.syncAccount m.Fs db logger clock TestHelpers.emptyProvider config "test-account"

            Assert.Equal("test-account", result.Account)
            Assert.Equal(0, result.MessagesProcessed)
            Assert.Equal(0, result.AttachmentsDownloaded)
            Assert.Equal(0, result.DuplicatesSkipped)
            Assert.Empty(result.Errors)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_SyncAccount_WithAttachments_DownloadsAndRecords`` () =
    task {
        let m = TestHelpers.memFs ()
        let db = TestHelpers.createRawDb ()
        let logger = Logging.silent
        let clock = TestHelpers.fixedClock (DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero))
        let config = emailTestConfig "/archive"

        let provider =
            TestHelpers.mockProvider
                [ sampleMessage ]
                (Map.ofList [ ("msg-001", [ sampleAttachment ]) ])

        try
            let! _ = db.initSchema ()
            let! result = EmailSync.syncAccount m.Fs db logger clock provider config "test-account"

            Assert.Equal(1, result.MessagesProcessed)
            Assert.Equal(1, result.AttachmentsDownloaded)
            Assert.Equal(0, result.DuplicatesSkipped)
            Assert.Empty(result.Errors)

            // Verify binary file was written
            Assert.True(m.Bytes.Count > 0, "Should have written at least one binary file")

            // Verify sidecar was written
            Assert.True(m.Files.Count > 0, "Should have written at least one sidecar file")
            let sidecarKey = m.Files.Keys |> Seq.find (fun k -> k.EndsWith(".meta.json"))
            let sidecarJson = (m.Get(sidecarKey)).Value
            Assert.Contains("email_attachment", sidecarJson)
            Assert.Contains("msg-001", sidecarJson)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_SyncAccount_DuplicateHash_SkipsDownload`` () =
    task {
        let m = TestHelpers.memFs ()
        let db = TestHelpers.createRawDb ()
        let logger = Logging.silent
        let clock = TestHelpers.fixedClock (DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero))
        let config = emailTestConfig "/archive"

        let provider =
            TestHelpers.mockProvider
                [ sampleMessage ]
                (Map.ofList [ ("msg-001", [ sampleAttachment ]) ])

        try
            let! _ = db.initSchema ()

            // Pre-insert a document with the same SHA256
            let sha = EmailSync.computeSha256 sampleAttachment.Content

            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (source_type, saved_path, category, sha256)
                       VALUES ('manual_drop', 'existing.pdf', 'invoices', @sha)"""
                    [ ("@sha", Database.boxVal sha) ]

            let! result = EmailSync.syncAccount m.Fs db logger clock provider config "test-account"

            Assert.Equal(1, result.MessagesProcessed)
            Assert.Equal(0, result.AttachmentsDownloaded)
            Assert.Equal(1, result.DuplicatesSkipped)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_SyncAccount_SmallAttachment_FilteredByMinSize`` () =
    task {
        let m = TestHelpers.memFs ()
        let db = TestHelpers.createRawDb ()
        let logger = Logging.silent
        let clock = TestHelpers.fixedClock (DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero))
        let config = emailTestConfig "/archive"

        let provider =
            TestHelpers.mockProvider
                [ sampleMessage ]
                (Map.ofList [ ("msg-001", [ smallAttachment ]) ])

        try
            let! _ = db.initSchema ()
            let! result = EmailSync.syncAccount m.Fs db logger clock provider config "test-account"

            Assert.Equal(1, result.MessagesProcessed)
            Assert.Equal(0, result.AttachmentsDownloaded)
            Assert.True(m.Bytes.IsEmpty)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_SyncAccount_AlreadyProcessedMessage_Skipped`` () =
    task {
        let m = TestHelpers.memFs ()
        let db = TestHelpers.createRawDb ()
        let logger = Logging.silent
        let clock = TestHelpers.fixedClock (DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero))
        let config = emailTestConfig "/archive"

        let provider =
            TestHelpers.mockProvider
                [ sampleMessage ]
                (Map.ofList [ ("msg-001", [ sampleAttachment ]) ])

        try
            let! _ = db.initSchema ()

            let! _ =
                db.execNonQuery
                    "INSERT INTO messages (gmail_id, account, has_attachments, processed_at) VALUES (@gid, @acc, 1, '2024-01-01')"
                    [ ("@gid", Database.boxVal "msg-001")
                      ("@acc", Database.boxVal "test-account") ]

            let! result = EmailSync.syncAccount m.Fs db logger clock provider config "test-account"

            Assert.Equal(0, result.MessagesProcessed)
            Assert.Equal(0, result.AttachmentsDownloaded)
        finally
            db.dispose ()
    }

// ─── Dry run tests ───────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_DryRun_ListsMessagesWithAttachments`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let logger = Logging.silent

        let provider =
            TestHelpers.mockProvider
                [ sampleMessage ]
                (Map.ofList [ ("msg-001", [ sampleAttachment ]) ])

        try
            let! _ = db.initSchema ()
            let! items = EmailSync.dryRun db logger provider "test-account"

            Assert.Equal(1, items.Length)
            Assert.Equal("test-account", items.[0].Account)
            Assert.Equal("msg-001", items.[0].GmailId)
            Assert.Equal(Some "Invoice #42", items.[0].Subject)
            Assert.Equal(1, items.[0].AttachmentCount)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_DryRun_NoMessages_ReturnsEmpty`` () =
    task {
        let db = TestHelpers.createRawDb ()
        let logger = Logging.silent

        try
            let! _ = db.initSchema ()
            let! items = EmailSync.dryRun db logger TestHelpers.emptyProvider "test-account"
            Assert.Empty(items)
        finally
            db.dispose ()
    }

// ─── Sync state after sync ──────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_SyncAccount_UpdatesSyncState`` () =
    task {
        let m = TestHelpers.memFs ()
        let db = TestHelpers.createRawDb ()
        let logger = Logging.silent
        let syncTime = DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero)
        let clock = TestHelpers.fixedClock syncTime
        let config = emailTestConfig "/archive"

        let provider =
            TestHelpers.mockProvider
                [ sampleMessage ]
                (Map.ofList [ ("msg-001", [ sampleAttachment ]) ])

        try
            let! _ = db.initSchema ()
            let! _ = EmailSync.syncAccount m.Fs db logger clock provider config "test-account"

            let! state = EmailSync.loadSyncState db "test-account"
            Assert.True(state.IsSome, "Sync state should be set after sync")
            Assert.Equal(syncTime.Year, state.Value.Year)
        finally
            db.dispose ()
    }

// ─── Backfill tests ──────────────────────────────────────────────────

let private backfillConfig : Domain.BackfillConfig =
    { Enabled = true; Since = None; BatchSize = 10; AttachmentsOnly = true; IncludeBodies = false }

let private backfillAccount (label: string) : Domain.AccountConfig =
    { Label = label; Provider = "gmail"; Backfill = backfillConfig }

let private backfillTestConfig archiveDir =
    { TestHelpers.testConfig archiveDir with Accounts = [ backfillAccount "test-backfill" ] }

let private fakePageProvider (messages: Domain.EmailMessage list) (nextToken: string option) : Algebra.EmailProvider =
    { TestHelpers.emptyProvider with
        listMessagePage = fun _ _ _ ->
            task {
                return
                    ({ Messages = messages; NextPageToken = nextToken; ResultSizeEstimate = int64 messages.Length } : Algebra.MessagePage)
            }
        getFullMessage = fun id -> task { return messages |> List.find (fun m -> m.ProviderId = id) } }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Backfill_DisabledConfig_Skips`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        let disabled : Domain.AccountConfig = { Label = "test"; Provider = "gmail"; Backfill = { backfillConfig with Enabled = false } }
        try
            let! (n, c) = EmailSync.backfillAccount m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock TestHelpers.emptyProvider (TestHelpers.testConfig "/archive") disabled
            Assert.Equal(0, n)
            Assert.True(c)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Backfill_EmptyPage_CompletesImmediately`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        let provider = fakePageProvider [] None
        try
            let! (n, c) = EmailSync.backfillAccount m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock provider (backfillTestConfig "/archive") (backfillAccount "test-bf")
            Assert.Equal(0, n)
            Assert.True(c)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Backfill_LoadBackfillState_EmptyDb_ReturnsDefaults`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! state = EmailSync.loadBackfillState db "nonexistent"
            Assert.False(state.Completed)
            Assert.Equal(0, state.Scanned)
            Assert.True(state.PageToken.IsNone)
        finally db.dispose ()
    }

// ─── syncAll and dryRunAll ───────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_SyncAll_EmptyMessages_ReturnsResultPerAccount`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Fs.createDirectory "/archive"
        m.Fs.createDirectory "/archive/unclassified"
        let config = emailTestConfig "/archive"
        try
            let makeProvider _ = TestHelpers.emptyProvider
            let! results = EmailSync.syncAll m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock makeProvider config
            Assert.Equal(config.Accounts.Length, results.Length)
            Assert.Equal(0, results.[0].AttachmentsDownloaded)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_SyncAll_MultipleAccounts_SyncsEach`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Fs.createDirectory "/archive"
        m.Fs.createDirectory "/archive/unclassified"
        let config =
            { emailTestConfig "/archive" with
                Accounts =
                    [ { Label = "acct1"; Provider = "gmail"; Backfill = { Domain.BackfillConfig.Enabled = false; Since = None; BatchSize = 50; AttachmentsOnly = true; IncludeBodies = false } }
                      { Label = "acct2"; Provider = "gmail"; Backfill = { Domain.BackfillConfig.Enabled = false; Since = None; BatchSize = 50; AttachmentsOnly = true; IncludeBodies = false } } ] }
        try
            let makeProvider _ = TestHelpers.emptyProvider
            let! results = EmailSync.syncAll m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock makeProvider config
            Assert.Equal(2, results.Length)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_DryRunAll_EmptyMessages_ReturnsEmpty`` () =
    task {
        let db = TestHelpers.createDb ()
        let config = emailTestConfig "/archive"
        try
            let makeProvider _ = TestHelpers.emptyProvider
            let! items = EmailSync.dryRunAll db TestHelpers.silentLogger makeProvider config
            Assert.Empty(items)
        finally db.dispose ()
    }
