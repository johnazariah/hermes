module Hermes.Tests.EmailSyncTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open Hermes.Core

// ─── Sample data ─────────────────────────────────────────────────────

let private emailTestConfig archiveDir : Domain.HermesConfig =
    { TestHelpers.testConfig archiveDir with
        Accounts =
            [ { Label = "test-account"; Provider = "gmail"
                Backfill = { Domain.BackfillConfig.Enabled = false; Since = None; BatchSize = 50; AttachmentsOnly = true; IncludeBodies = false }
                ClientId = ""; TenantId = "common"; RedirectPort = 53682 } ]
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
    Assert.Equal("msg-001", sidecar.ProviderId)
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
    Assert.Contains("provider_id", json)
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
            let sidecarKey = m.Files.Keys |> Seq.find (fun k -> k.EndsWith(".hermes.json"))
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
    { Label = label; Provider = "gmail"; Backfill = backfillConfig; ClientId = ""; TenantId = "common"; RedirectPort = 53682 }

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
        let disabled : Domain.AccountConfig = { Label = "test"; Provider = "gmail"; Backfill = { backfillConfig with Enabled = false }; ClientId = ""; TenantId = "common"; RedirectPort = 53682 }
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
                    [ { Label = "acct1"; Provider = "gmail"; Backfill = { Domain.BackfillConfig.Enabled = false; Since = None; BatchSize = 50; AttachmentsOnly = true; IncludeBodies = false }; ClientId = ""; TenantId = "common"; RedirectPort = 53682 }
                      { Label = "acct2"; Provider = "gmail"; Backfill = { Domain.BackfillConfig.Enabled = false; Since = None; BatchSize = 50; AttachmentsOnly = true; IncludeBodies = false }; ClientId = ""; TenantId = "common"; RedirectPort = 53682 } ] }
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

// ─── Channel-based sync tests ─────────────────────────────────────────

let private stubPage ids nextToken : Algebra.StubPage =
    { Ids = ids
      NextPageToken = nextToken
      ResultSizeEstimate = int64 ids.Length }

let private channelProvider
    (pages: Map<string option, Algebra.StubPage>)
    (messages: Map<string, Domain.EmailMessage>)
    (attachments: Map<string, Domain.EmailAttachment list>)
    : Algebra.EmailProvider =
    { TestHelpers.emptyProvider with
        listStubPage = fun token _ _ -> Task.FromResult(pages.[token])
        getFullMessage = fun id -> Task.FromResult(messages.[id])
        getAttachments =
            fun id ->
                attachments
                |> Map.tryFind id
                |> Option.defaultValue []
                |> Task.FromResult }

let private completedReader (items: 'a list) : ChannelReader<'a> =
    let channel = Channel.CreateUnbounded<'a>()
    items |> List.iter (fun item -> channel.Writer.TryWrite(item) |> ignore)
    channel.Writer.Complete()
    channel.Reader

let private tryRead (reader: ChannelReader<'a>) : 'a option =
    let mutable item = Unchecked.defaultof<'a>
    if reader.TryRead(&item) then Some item else None

let private countRows (db: Algebra.Database) table =
    task {
        let sql = $"SELECT COUNT(*) FROM {table}"
        let! value = db.execScalar sql []
        return value :?> int64
    }

let private insertExistingMessage (db: Algebra.Database) account messageId =
    task {
        let! _ =
            db.execNonQuery
                "INSERT INTO messages (gmail_id, account, processed_at) VALUES (@id, @account, @at)"
                [ ("@id", Database.boxVal messageId)
                  ("@account", Database.boxVal account)
                  ("@at", Database.boxVal "2024-01-01T00:00:00+00:00") ]
        return ()
    }

let private withDatabase (action: Algebra.Database -> Task<unit>) =
    task {
        let db = TestHelpers.createDb ()
        try
            return! action db
        finally
            db.dispose ()
    }

let private verifyCallback
    (mem: TestHelpers.MemFs)
    (archiveDir: string)
    (db: Algebra.Database)
    (documentId: int64, savedPath: string)
    =
    task {
        let! rows =
            db.execReader
                "SELECT * FROM documents WHERE id = @id"
                [ ("@id", Database.boxVal documentId) ]
        let row = rows |> List.exactlyOne
        Assert.True(documentId > 0L)
        Assert.Equal(Some savedPath, row |> Document.decode<string> "saved_path")
        Assert.True(mem.Fs.fileExists(Path.Combine(archiveDir, savedPath)))
        return row
    }

let private callbackOfDocument (document: Document.T) =
    Document.id document,
    document |> Document.decode<string> "saved_path" |> Option.defaultValue ""

let private requireStatementLocalDocumentIdentity (db: Algebra.Database) =
    let returningDocumentInserts = ref 0
    let isDocumentInsert (sql: string) =
        sql.TrimStart().StartsWith("INSERT INTO documents", StringComparison.Ordinal)
    let hasReturningId (sql: string) =
        sql.Contains("RETURNING id", StringComparison.OrdinalIgnoreCase)
    let hasLastInsertRowId (sql: string) =
        sql.Contains("last_insert_rowid", StringComparison.OrdinalIgnoreCase)

    let wrapped =
        { db with
            execNonQuery =
                fun sql parameters ->
                    if isDocumentInsert sql then
                        failwith "Document inserts must use execScalar with RETURNING id"
                    else
                        db.execNonQuery sql parameters
            execScalar =
                fun sql parameters ->
                    if hasLastInsertRowId sql then
                        failwith "Document identity must not use last_insert_rowid"
                    elif isDocumentInsert sql && hasReturningId sql then
                        Interlocked.Increment(returningDocumentInserts) |> ignore
                        db.execScalar sql parameters
                    else
                        db.execScalar sql parameters }

    wrapped, returningDocumentInserts

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_EnumerateIds_TwoPages_ForwardsAllIdsAndCompletesWriter`` () =
    task {
        let output = Channel.CreateUnbounded<string>()
        let requests = ConcurrentQueue<string option * string option * int>()
        let counter = ref 0
        let provider =
            { TestHelpers.emptyProvider with
                listStubPage =
                    fun token query pageSize ->
                        requests.Enqueue((token, query, pageSize))
                        match token with
                        | None -> Task.FromResult(stubPage [ "id-1"; "id-2" ] (Some "next"))
                        | Some "next" -> Task.FromResult(stubPage [ "id-3" ] None)
                        | unexpected -> Task.FromException<_>(InvalidOperationException($"Unexpected token: {unexpected}")) }

        let! total =
            EmailSync.enumerateIds
                provider TestHelpers.silentLogger "account" "after:1"
                output.Writer counter CancellationToken.None

        let! first = output.Reader.ReadAsync()
        let! second = output.Reader.ReadAsync()
        let! third = output.Reader.ReadAsync()
        let ids = [| first; second; third |]
        use completionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1.0))
        let! hasMore = output.Reader.WaitToReadAsync(completionTimeout.Token)
        let expectedRequests =
            [| (None, Some "after:1", 500)
               (Some "next", Some "after:1", 500) |]

        Assert.Equal(3, total)
        Assert.Equal(3, counter.Value)
        Assert.Equal<string array>([| "id-1"; "id-2"; "id-3" |], ids)
        Assert.False(hasMore)
        Assert.Equal<(string option * string option * int) array>(
            expectedRequests,
            requests.ToArray())
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_EnumerateIds_ProviderFailure_CancelsRetryAndCompletesWriter`` () =
    task {
        use cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1.0))
        let output = Channel.CreateUnbounded<string>()
        let warnings = ConcurrentQueue<string>()
        let logger = { TestHelpers.silentLogger with warn = warnings.Enqueue }
        let counter = ref 0
        let provider =
            { TestHelpers.emptyProvider with
                listStubPage =
                    fun _ _ _ ->
                        cancellation.Cancel()
                        Task.FromException<_>(InvalidOperationException("enumeration failed")) }

        let! total =
            EmailSync.enumerateIds
                provider logger "account" "after:1"
                output.Writer counter cancellation.Token

        use completionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1.0))
        let! hasMore = output.Reader.WaitToReadAsync(completionTimeout.Token)
        Assert.Equal(0, total)
        Assert.Equal(0, counter.Value)
        Assert.True(cancellation.IsCancellationRequested)
        Assert.False(hasMore)
        Assert.Contains("enumeration failed", warnings.ToArray() |> Array.exactlyOne)
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_EnumerateIds_NonCooperativeOperationCanceledException_RetriesInsteadOfCompleting`` () =
    task {
        use cancellation = new CancellationTokenSource()
        let output = Channel.CreateUnbounded<string>()
        let warnings = ConcurrentQueue<string>()
        let logger =
            { TestHelpers.silentLogger with
                warn =
                    fun message ->
                        warnings.Enqueue message
                        cancellation.Cancel() }
        let callCount = ref 0
        let counter = ref 0
        let provider =
            { TestHelpers.emptyProvider with
                listStubPage =
                    fun _ _ _ ->
                        Interlocked.Increment(callCount) |> ignore
                        Task.FromException<_>(TaskCanceledException("request timed out")) }

        let! total =
            EmailSync.enumerateIds
                provider logger "account" "after:1"
                output.Writer counter cancellation.Token
        use completionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1.0))
        let! hasMore = output.Reader.WaitToReadAsync(completionTimeout.Token)
        Assert.Equal(1, callCount.Value)
        Assert.Equal(0, total)
        Assert.Equal(0, counter.Value)
        Assert.False(hasMore)
        Assert.Contains("request timed out", warnings.ToArray() |> Array.exactlyOne)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_ProcessMessageConsumer_NewBodyAndAttachment_PersistsAndInvokesIngest`` () =
    let mem = TestHelpers.memFs ()
    let config = emailTestConfig "/archive"
    let ingested = ConcurrentQueue<int64 * string>()
    let provider =
        channelProvider
            Map.empty
            (Map.ofList [ (sampleMessage.ProviderId, sampleMessage) ])
            (Map.ofList [ (sampleMessage.ProviderId, [ sampleAttachment ]) ])

    withDatabase (fun db ->
        task {
            let counter = ref 0
            let onIngest id path = task { ingested.Enqueue((id, path)) }
            let! processed, downloaded =
                EmailSync.processMessageConsumer
                    mem.Fs db TestHelpers.silentLogger TestHelpers.defaultClock provider config
                    "test-account" (completedReader [ sampleMessage.ProviderId ])
                    onIngest counter 1 CancellationToken.None
            let! documents = db.execReader "SELECT * FROM documents ORDER BY id" []
            let! messageCount = countRows db "messages"
            let sources = documents |> List.choose (Document.decode<string> "source_type") |> Set.ofList
            let savedPaths = documents |> List.choose (Document.decode<string> "saved_path") |> Set.ofList
            let callbacks = ingested.ToArray()
            let callbackPaths = callbacks |> Array.map snd |> Set.ofArray
            let! _ =
                callbacks
                |> Array.map (verifyCallback mem config.ArchiveDir db)
                |> Task.WhenAll
            Assert.Equal(1, processed)
            Assert.Equal(2, downloaded)
            Assert.Equal(1, counter.Value)
            Assert.Equal(1L, messageCount)
            Assert.Equal(2, documents.Length)
            Assert.Equal<Set<string>>(
                set [ "email_body"; "email_attachment" ],
                sources)
            Assert.True(savedPaths |> Set.exists (fun path -> path.EndsWith(".md")))
            Assert.True(savedPaths |> Set.exists (fun path -> path.EndsWith(".pdf")))
            Assert.Equal<Set<string>>(savedPaths, callbackPaths)
            Assert.Equal(2, ingested.Count)
            Assert.True(mem.Files.Values |> Seq.exists (fun text -> text.Contains("Please find the invoice")))
            Assert.True(mem.Bytes.Values |> Seq.exists (fun bytes -> bytes = sampleAttachment.Content))
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_ProcessMessageConsumer_RecordedMessage_ReportsZeroNewWork`` () =
    let requested = ConcurrentQueue<string>()
    let provider =
        { TestHelpers.emptyProvider with
            getFullMessage =
                fun id ->
                    requested.Enqueue(id)
                    Task.FromException<_>(InvalidOperationException("should not fetch")) }

    withDatabase (fun db ->
        task {
            do! insertExistingMessage db "test-account" sampleMessage.ProviderId
            let counter = ref 0
            let! processed, downloaded =
                EmailSync.processMessageConsumer
                    (TestHelpers.memFs ()).Fs db TestHelpers.silentLogger TestHelpers.defaultClock
                    provider (emailTestConfig "/archive") "test-account"
                    (completedReader [ sampleMessage.ProviderId ])
                    (fun _ _ -> task { return () }) counter 1 CancellationToken.None
            let! documentCount = countRows db "documents"
            Assert.Equal(0, processed)
            Assert.Equal(0, downloaded)
            Assert.Equal(0, counter.Value)
            Assert.Equal(0L, documentCount)
            Assert.Empty(requested)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_ProcessMessageConsumer_ProviderFailures_ContainsErrorsAndDrainsChannel`` () =
    let requested = ConcurrentQueue<string>()
    let warnings = ConcurrentQueue<string>()
    let logger = { TestHelpers.silentLogger with warn = warnings.Enqueue }
    let provider =
        { TestHelpers.emptyProvider with
            getFullMessage =
                fun id ->
                    requested.Enqueue(id)
                    Task.FromException<_>(InvalidOperationException($"failed {id}")) }

    withDatabase (fun db ->
        task {
            let counter = ref 0
            let! processed, downloaded =
                EmailSync.processMessageConsumer
                    (TestHelpers.memFs ()).Fs db logger TestHelpers.defaultClock provider
                    (emailTestConfig "/archive") "test-account"
                    (completedReader [ "bad-1"; "bad-2" ])
                    (fun _ _ -> task { return () }) counter 1 CancellationToken.None
            let! messageCount = countRows db "messages"
            Assert.Equal(0, processed)
            Assert.Equal(0, downloaded)
            Assert.Equal(0, counter.Value)
            Assert.Equal(0L, messageCount)
            Assert.True(requested.ToArray() = [| "bad-1"; "bad-2" |])
            Assert.Equal(2, warnings.Count)
            warnings |> Seq.iter (fun warning -> Assert.Contains("Error processing", warning))
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_SyncAccountWithChannel_EmptyEnumeration_ReturnsZeroWithoutAdvancingState`` () =
    let output = Channel.CreateUnbounded<Document.T>()

    withDatabase (fun db ->
        task {
            let! result =
                EmailSync.syncAccountWithChannel
                    (TestHelpers.memFs ()).Fs db TestHelpers.silentLogger TestHelpers.defaultClock
                    TestHelpers.emptyProvider (emailTestConfig "/archive") "test-account"
                    output.Writer 1 CancellationToken.None
            let! state = EmailSync.loadSyncState db "test-account"
            Assert.Equal(0, result.MessagesProcessed)
            Assert.Equal(0, result.AttachmentsDownloaded)
            Assert.Equal(0, result.DuplicatesSkipped)
            Assert.Empty(result.Errors)
            Assert.True(state.IsNone)
            Assert.True(tryRead output.Reader |> Option.isNone)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_SyncAccountWithChannel_MessageFailure_ReturnsErrorWithoutAdvancingState`` () =
    let output = Channel.CreateUnbounded<Document.T>()
    let provider =
        { TestHelpers.emptyProvider with
            listStubPage =
                fun _ _ _ -> Task.FromResult(stubPage [ "failed-message" ] None)
            getFullMessage =
                fun id -> Task.FromException<_>(InvalidOperationException($"failed {id}")) }

    withDatabase (fun db ->
        task {
            let! result =
                EmailSync.syncAccountWithChannel
                    (TestHelpers.memFs ()).Fs db TestHelpers.silentLogger TestHelpers.defaultClock
                    provider (emailTestConfig "/archive") "test-account"
                    output.Writer 1 CancellationToken.None
            let! state = EmailSync.loadSyncState db "test-account"
            Assert.Equal(0, result.MessagesProcessed)
            Assert.Equal(0, result.AttachmentsDownloaded)
            Assert.Equal(0, result.DuplicatesSkipped)
            Assert.Single(result.Errors) |> ignore
            Assert.Contains("failed-message", result.Errors.Head)
            Assert.True(state.IsNone)
            Assert.True(tryRead output.Reader |> Option.isNone)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_SyncAccountWithChannel_SyntheticMessage_ForwardsDocumentsAndAdvancesState`` () =
    let output = Channel.CreateUnbounded<Document.T>()
    let syncTime = DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero)
    let provider =
        channelProvider
            (Map.ofList [ (None, stubPage [ sampleMessage.ProviderId ] None) ])
            (Map.ofList [ (sampleMessage.ProviderId, sampleMessage) ])
            (Map.ofList [ (sampleMessage.ProviderId, [ sampleAttachment ]) ])

    withDatabase (fun db ->
        task {
            let! result =
                EmailSync.syncAccountWithChannel
                    (TestHelpers.memFs ()).Fs db TestHelpers.silentLogger
                    (TestHelpers.fixedClock syncTime) provider (emailTestConfig "/archive")
                    "test-account" output.Writer 1 CancellationToken.None
            let documents = [ tryRead output.Reader; tryRead output.Reader ] |> List.choose id
            let sources = documents |> List.choose (Document.decode<string> "source_type") |> Set.ofList
            let! state = EmailSync.loadSyncState db "test-account"
            Assert.Equal(1, result.MessagesProcessed)
            Assert.Equal(2, result.AttachmentsDownloaded)
            Assert.Equal(0, result.DuplicatesSkipped)
            Assert.Empty(result.Errors)
            Assert.Equal(2, documents.Length)
            Assert.Equal<Set<string>>(
                set [ "email_body"; "email_attachment" ],
                sources)
            Assert.Equal(Some syncTime, state)
            Assert.True(tryRead output.Reader |> Option.isNone)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_SyncAccountWithChannel_ConcurrentMessages_ReturnsMatchingDocumentIds`` () =
    let mem = TestHelpers.memFs ()
    let config = emailTestConfig "/archive"
    let output = Channel.CreateUnbounded<Document.T>()
    let syncTime = DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero)
    let firstMessage =
        { sampleMessage with
            ProviderId = "concurrent-1"
            ThreadId = "thread-concurrent-1"
            Subject = Some "First"
            BodyText = None }
    let secondMessage =
        { sampleMessage with
            ProviderId = "concurrent-2"
            ThreadId = "thread-concurrent-2"
            Subject = Some "Second"
            BodyText = None }
    let firstAttachment =
        { sampleAttachment with
            FileName = "first.bin"
            Content = Array.create 5000 0x11uy }
    let secondAttachment =
        { sampleAttachment with
            FileName = "second.bin"
            Content = Array.create 5000 0x22uy }
    let messages =
        [ firstMessage; secondMessage ]
        |> List.map (fun message -> message.ProviderId, message)
        |> Map.ofList
    let attachments =
        Map.ofList
            [ firstMessage.ProviderId, [ firstAttachment ]
              secondMessage.ProviderId, [ secondAttachment ] ]
    let timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5.0))
    let arrivals = ref 0
    let messageBarrier =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let baseProvider =
        channelProvider
            (Map.ofList [ None, stubPage [ firstMessage.ProviderId; secondMessage.ProviderId ] None ])
            messages
            attachments
    let provider =
        { baseProvider with
            getFullMessage =
                fun id ->
                    task {
                        if Interlocked.Increment(arrivals) = 2 then
                            messageBarrier.TrySetResult(()) |> ignore
                        do! messageBarrier.Task.WaitAsync(timeout.Token)
                        return messages.[id]
                    } }
    let verifyConcurrentCallback db callback =
        task {
            let _, savedPath = callback
            let! row = verifyCallback mem config.ArchiveDir db callback
            let gmailId =
                row
                |> Document.decode<string> "gmail_id"
                |> Option.defaultValue ""
            let expected = attachments.[gmailId] |> List.exactlyOne
            let! actual =
                mem.Fs.readAllBytes(Path.Combine(config.ArchiveDir, savedPath))
            Assert.Equal<byte array>(expected.Content, actual)
        }

    withDatabase (fun db ->
        task {
            use _timeout = timeout
            let concurrentDb, returningDocumentInserts =
                requireStatementLocalDocumentIdentity db
            let! result =
                EmailSync.syncAccountWithChannel
                    mem.Fs concurrentDb TestHelpers.silentLogger
                    (TestHelpers.fixedClock syncTime) provider config
                    "test-account" output.Writer 2 timeout.Token
            let forwarded =
                [ tryRead output.Reader; tryRead output.Reader ]
                |> List.choose id
            let callbacks = forwarded |> List.map callbackOfDocument
            let callbackIds = callbacks |> List.map fst
            let! state = EmailSync.loadSyncState db "test-account"
            Assert.Equal(2, result.MessagesProcessed)
            Assert.Equal(2, result.AttachmentsDownloaded)
            Assert.Equal(0, result.DuplicatesSkipped)
            Assert.Empty(result.Errors)
            Assert.Equal(2, returningDocumentInserts.Value)
            Assert.Equal(2, callbacks.Length)
            Assert.Equal(2, callbackIds |> Set.ofList |> Set.count)
            Assert.DoesNotContain(0L, callbackIds)
            Assert.Equal(Some syncTime, state)
            Assert.True(tryRead output.Reader |> Option.isNone)
            do!
                callbacks
                |> List.map (verifyConcurrentCallback db)
                |> Task.WhenAll
                :> Task
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_SyncAccountWithChannel_DuplicateMessage_ReportsDuplicateWithoutForwarding`` () =
    let output = Channel.CreateUnbounded<Document.T>()
    let provider =
        channelProvider
            (Map.ofList [ (None, stubPage [ sampleMessage.ProviderId ] None) ])
            (Map.ofList [ (sampleMessage.ProviderId, sampleMessage) ])
            Map.empty

    withDatabase (fun db ->
        task {
            do! insertExistingMessage db "test-account" sampleMessage.ProviderId
            let! result =
                EmailSync.syncAccountWithChannel
                    (TestHelpers.memFs ()).Fs db TestHelpers.silentLogger TestHelpers.defaultClock
                    provider (emailTestConfig "/archive") "test-account"
                    output.Writer 1 CancellationToken.None
            let! documentCount = countRows db "documents"
            let! state = EmailSync.loadSyncState db "test-account"
            Assert.Equal(0, result.MessagesProcessed)
            Assert.Equal(0, result.AttachmentsDownloaded)
            Assert.Equal(1, result.DuplicatesSkipped)
            Assert.Empty(result.Errors)
            Assert.Equal(0L, documentCount)
            Assert.True(state.IsSome)
            Assert.True(tryRead output.Reader |> Option.isNone)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_SyncAccountWithChannel_CancelledDuringConsumption_DoesNotAdvanceStateOrReportDuplicates`` () =
    task {
        use cts = new CancellationTokenSource(TimeSpan.FromSeconds(5.0))
        let output = Channel.CreateUnbounded<Document.T>()
        let enumeratedIds = [ "msg-a"; "msg-b"; "msg-c" ]
        let provider =
            { TestHelpers.emptyProvider with
                listStubPage =
                    fun _ _ _ -> Task.FromResult(stubPage enumeratedIds None)
                getFullMessage =
                    fun id ->
                        cts.Cancel()
                        Task.FromException<_>(
                            OperationCanceledException($"aborted while fetching {id}")) }

        do!
            withDatabase (fun db ->
                task {
                    let! result =
                        EmailSync.syncAccountWithChannel
                            (TestHelpers.memFs ()).Fs db TestHelpers.silentLogger
                            TestHelpers.defaultClock provider (emailTestConfig "/archive")
                            "test-account" output.Writer 1 cts.Token
                    let! state = EmailSync.loadSyncState db "test-account"
                    Assert.Equal(0, result.MessagesProcessed)
                    Assert.Equal(0, result.AttachmentsDownloaded)
                    Assert.Equal(0, result.DuplicatesSkipped)
                    Assert.Contains(
                        result.Errors,
                        fun error ->
                            error.Contains("Sync cancelled after enumerating", StringComparison.Ordinal))
                    Assert.True(state.IsNone)
                    Assert.True(tryRead output.Reader |> Option.isNone)
                })
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_SyncAccountWithChannel_DatabaseFailure_ReturnsTopLevelError`` () =
    let output = Channel.CreateUnbounded<Document.T>()
    let errors = ConcurrentQueue<string>()
    let logger = { TestHelpers.silentLogger with error = errors.Enqueue }

    withDatabase (fun db ->
        task {
            let failingDb =
                { db with
                    execScalar =
                        fun sql parameters ->
                            if sql.Contains("SELECT last_sync_at") then
                                Task.FromException<obj | null>(InvalidOperationException("sync state unavailable"))
                            else
                                db.execScalar sql parameters }
            let! result =
                EmailSync.syncAccountWithChannel
                    (TestHelpers.memFs ()).Fs failingDb logger TestHelpers.defaultClock
                    TestHelpers.emptyProvider (emailTestConfig "/archive") "test-account"
                    output.Writer 1 CancellationToken.None
            Assert.Equal(0, result.MessagesProcessed)
            Assert.Equal(0, result.AttachmentsDownloaded)
            Assert.Equal(0, result.DuplicatesSkipped)
            Assert.Single(result.Errors) |> ignore
            Assert.Contains("sync state unavailable", result.Errors.Head)
            Assert.Contains("sync state unavailable", errors.ToArray() |> Array.exactlyOne)
            Assert.True(tryRead output.Reader |> Option.isNone)
        })
