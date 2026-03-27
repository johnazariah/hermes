module Hermes.Tests.EmailSyncTests

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Xunit
open Hermes.Core

// ─── In-memory FileSystem algebra for testing ────────────────────────

let inMemoryFileSystem () =
    let files = ConcurrentDictionary<string, string>()
    let binaryFiles = ConcurrentDictionary<string, byte array>()
    let dirs = ConcurrentDictionary<string, bool>()

    let fs: Algebra.FileSystem =
        { readAllText = fun path ->
            task {
                match files.TryGetValue(path) with
                | true, content -> return content
                | _ -> return failwith $"File not found: {path}"
            }
          writeAllText = fun path content ->
            task { files.[path] <- content }
          writeAllBytes = fun path bytes ->
            task { binaryFiles.[path] <- bytes }
          readAllBytes = fun path ->
            task {
                match binaryFiles.TryGetValue(path) with
                | true, bytes -> return bytes
                | _ ->
                    match files.TryGetValue(path) with
                    | true, content -> return System.Text.Encoding.UTF8.GetBytes(content)
                    | _ -> return failwith $"File not found: {path}"
            }
          fileExists = fun path -> files.ContainsKey(path) || binaryFiles.ContainsKey(path)
          directoryExists = fun path -> dirs.ContainsKey(path)
          createDirectory = fun path -> dirs.[path] <- true
          deleteFile = fun path ->
            files.TryRemove(path) |> ignore
            binaryFiles.TryRemove(path) |> ignore
          moveFile = fun src dst ->
            match files.TryRemove(src) with
            | true, content -> files.[dst] <- content
            | _ -> ()
            match binaryFiles.TryRemove(src) with
            | true, bytes -> binaryFiles.[dst] <- bytes
            | _ -> ()
          getFiles = fun dir _pattern ->
            let prefix = if dir.EndsWith("/") || dir.EndsWith("\\") then dir else dir + "/"
            files.Keys
            |> Seq.append binaryFiles.Keys
            |> Seq.distinct
            |> Seq.filter (fun k ->
                k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && not (k.Substring(prefix.Length).Contains("/"))
                && not (k.Substring(prefix.Length).Contains("\\")))
            |> Seq.toArray
          getFileSize = fun path ->
            match binaryFiles.TryGetValue(path) with
            | true, bytes -> int64 bytes.Length
            | _ ->
                match files.TryGetValue(path) with
                | true, content -> int64 (System.Text.Encoding.UTF8.GetByteCount(content))
                | _ -> 0L }

    fs, files, binaryFiles, dirs

// ─── Fixed clock ─────────────────────────────────────────────────────

let fixedClock (dt: DateTimeOffset) : Algebra.Clock =
    { utcNow = fun () -> dt }

// ─── Mock email provider ─────────────────────────────────────────────

let mockProvider
    (messages: Domain.EmailMessage list)
    (attachmentMap: Map<string, Domain.EmailAttachment list>)
    : Algebra.EmailProvider =
    { listNewMessages = fun _ -> task { return messages }
      getAttachments = fun msgId ->
        task {
            return
                match attachmentMap |> Map.tryFind msgId with
                | Some atts -> atts
                | None -> []
        }
      getMessageBody = fun _ -> task { return None } }

let emptyProvider : Algebra.EmailProvider =
    { listNewMessages = fun _ -> task { return [] }
      getAttachments = fun _ -> task { return [] }
      getMessageBody = fun _ -> task { return None } }

// ─── Test config ─────────────────────────────────────────────────────

let testConfig archiveDir : Domain.HermesConfig =
    { ArchiveDir = archiveDir
      Credentials = "/test/creds.json"
      Accounts = [ { Label = "test-account"; Provider = "gmail" } ]
      SyncIntervalMinutes = 15
      MinAttachmentSize = 100
      WatchFolders = []
      Ollama =
        { Enabled = false
          BaseUrl = ""
          EmbeddingModel = ""
          VisionModel = ""
          InstructModel = "" }
      Fallback = { Embedding = ""; Ocr = "" }
      Azure = { DocumentIntelligenceEndpoint = ""; DocumentIntelligenceKey = "" } }

// ─── Sample data ─────────────────────────────────────────────────────

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

// ─── Helper ──────────────────────────────────────────────────────────

let createTestDb () =
    let conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:")
    conn.Open()

    use pragma = conn.CreateCommand()
    pragma.CommandText <- "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;"
    pragma.ExecuteNonQuery() |> ignore

    Database.fromConnection conn

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
[<Trait("Category", "Unit")>]
let ``EmailSync_LoadSyncState_NoState_ReturnsNone`` () =
    task {
        let db = createTestDb ()

        try
            let! _ = db.initSchema ()
            let! state = EmailSync.loadSyncState db "nonexistent"
            Assert.True(state.IsNone)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_LoadSyncState_AfterSync_ReturnsSome`` () =
    task {
        let db = createTestDb ()

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
[<Trait("Category", "Unit")>]
let ``EmailSync_SyncAccount_NoMessages_ReturnsZeroCounts`` () =
    task {
        let fs, _, _, _ = inMemoryFileSystem ()
        let db = createTestDb ()
        let logger = Logging.silent
        let clock = fixedClock (DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero))
        let config = testConfig "/archive"

        try
            let! _ = db.initSchema ()
            let! result = EmailSync.syncAccount fs db logger clock emptyProvider config "test-account"

            Assert.Equal("test-account", result.Account)
            Assert.Equal(0, result.MessagesProcessed)
            Assert.Equal(0, result.AttachmentsDownloaded)
            Assert.Equal(0, result.DuplicatesSkipped)
            Assert.Empty(result.Errors)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_SyncAccount_WithAttachments_DownloadsAndRecords`` () =
    task {
        let fs, textFiles, binaryFiles, dirs = inMemoryFileSystem ()
        let db = createTestDb ()
        let logger = Logging.silent
        let clock = fixedClock (DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero))
        let config = testConfig "/archive"

        let provider =
            mockProvider
                [ sampleMessage ]
                (Map.ofList [ ("msg-001", [ sampleAttachment ]) ])

        try
            let! _ = db.initSchema ()
            let! result = EmailSync.syncAccount fs db logger clock provider config "test-account"

            Assert.Equal(1, result.MessagesProcessed)
            Assert.Equal(1, result.AttachmentsDownloaded)
            Assert.Equal(0, result.DuplicatesSkipped)
            Assert.Empty(result.Errors)

            // Verify binary file was written
            Assert.True(binaryFiles.Count > 0, "Should have written at least one binary file")

            // Verify sidecar was written
            Assert.True(textFiles.Count > 0, "Should have written at least one sidecar file")
            let sidecarKey = textFiles.Keys |> Seq.find (fun k -> k.EndsWith(".meta.json"))
            let sidecarJson = textFiles.[sidecarKey]
            Assert.Contains("email_attachment", sidecarJson)
            Assert.Contains("msg-001", sidecarJson)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_SyncAccount_DuplicateHash_SkipsDownload`` () =
    task {
        let fs, _, _, _ = inMemoryFileSystem ()
        let db = createTestDb ()
        let logger = Logging.silent
        let clock = fixedClock (DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero))
        let config = testConfig "/archive"

        let provider =
            mockProvider
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

            let! result = EmailSync.syncAccount fs db logger clock provider config "test-account"

            Assert.Equal(1, result.MessagesProcessed)
            Assert.Equal(0, result.AttachmentsDownloaded)
            Assert.Equal(1, result.DuplicatesSkipped)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_SyncAccount_SmallAttachment_FilteredByMinSize`` () =
    task {
        let fs, _, binaryFiles, _ = inMemoryFileSystem ()
        let db = createTestDb ()
        let logger = Logging.silent
        let clock = fixedClock (DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero))
        let config = testConfig "/archive"

        let provider =
            mockProvider
                [ sampleMessage ]
                (Map.ofList [ ("msg-001", [ smallAttachment ]) ])

        try
            let! _ = db.initSchema ()
            let! result = EmailSync.syncAccount fs db logger clock provider config "test-account"

            Assert.Equal(1, result.MessagesProcessed)
            Assert.Equal(0, result.AttachmentsDownloaded)
            Assert.True(binaryFiles.IsEmpty)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_SyncAccount_AlreadyProcessedMessage_Skipped`` () =
    task {
        let fs, _, _, _ = inMemoryFileSystem ()
        let db = createTestDb ()
        let logger = Logging.silent
        let clock = fixedClock (DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero))
        let config = testConfig "/archive"

        let provider =
            mockProvider
                [ sampleMessage ]
                (Map.ofList [ ("msg-001", [ sampleAttachment ]) ])

        try
            let! _ = db.initSchema ()

            let! _ =
                db.execNonQuery
                    "INSERT INTO messages (gmail_id, account, has_attachments, processed_at) VALUES (@gid, @acc, 1, '2024-01-01')"
                    [ ("@gid", Database.boxVal "msg-001")
                      ("@acc", Database.boxVal "test-account") ]

            let! result = EmailSync.syncAccount fs db logger clock provider config "test-account"

            Assert.Equal(0, result.MessagesProcessed)
            Assert.Equal(0, result.AttachmentsDownloaded)
        finally
            db.dispose ()
    }

// ─── Dry run tests ───────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_DryRun_ListsMessagesWithAttachments`` () =
    task {
        let db = createTestDb ()
        let logger = Logging.silent

        let provider =
            mockProvider
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
[<Trait("Category", "Unit")>]
let ``EmailSync_DryRun_NoMessages_ReturnsEmpty`` () =
    task {
        let db = createTestDb ()
        let logger = Logging.silent

        try
            let! _ = db.initSchema ()
            let! items = EmailSync.dryRun db logger emptyProvider "test-account"
            Assert.Empty(items)
        finally
            db.dispose ()
    }

// ─── Sync state after sync ──────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_SyncAccount_UpdatesSyncState`` () =
    task {
        let fs, _, _, _ = inMemoryFileSystem ()
        let db = createTestDb ()
        let logger = Logging.silent
        let syncTime = DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero)
        let clock = fixedClock syncTime
        let config = testConfig "/archive"

        let provider =
            mockProvider
                [ sampleMessage ]
                (Map.ofList [ ("msg-001", [ sampleAttachment ]) ])

        try
            let! _ = db.initSchema ()
            let! _ = EmailSync.syncAccount fs db logger clock provider config "test-account"

            let! state = EmailSync.loadSyncState db "test-account"
            Assert.True(state.IsSome, "Sync state should be set after sync")
            Assert.Equal(syncTime.Year, state.Value.Year)
        finally
            db.dispose ()
    }
