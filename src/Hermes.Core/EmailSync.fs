namespace Hermes.Core

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

/// Email sync logic parameterised over algebras.
/// Downloads Gmail attachments → unclassified/, creates sidecar .meta.json files,
/// records messages in DB, and tracks incremental sync state.
[<RequireQualifiedAccess>]
module EmailSync =

    let private boxVal (x: 'a) : obj = x :> obj

    // ─── Filename sanitisation ───────────────────────────────────────

    let private invalidChars =
        Path.GetInvalidFileNameChars()
        |> Set.ofArray

    /// Remove invalid filename characters and collapse whitespace.
    let sanitiseFileName (name: string) =
        name.ToCharArray()
        |> Array.map (fun c -> if invalidChars.Contains c then '_' else c)
        |> String
        |> fun s -> System.Text.RegularExpressions.Regex.Replace(s, @"_+", "_")
        |> fun s -> s.Trim('_', ' ')
        |> fun s -> if String.IsNullOrWhiteSpace(s) then "attachment" else s

    /// Build a standardised file name: {date}_{sender}_{original}.
    let buildStandardName (date: DateTimeOffset option) (sender: string option) (originalName: string) =
        let datePart =
            match date with
            | Some d -> d.ToString("yyyy-MM-dd")
            | None -> "undated"

        let senderPart =
            match sender with
            | Some s ->
                let atIdx = s.IndexOf('@')
                let local = if atIdx > 0 then s.Substring(0, atIdx) else s
                sanitiseFileName local
            | None -> "unknown"

        let namePart = sanitiseFileName originalName
        $"{datePart}_{senderPart}_{namePart}"

    // ─── SHA256 hashing ──────────────────────────────────────────────

    let computeSha256 (data: byte array) : string =
        let hash = SHA256.HashData(data)
        Convert.ToHexStringLower(hash)

    // ─── Sidecar metadata ────────────────────────────────────────────

    let private jsonOptions =
        let opts = JsonSerializerOptions(WriteIndented = true)
        opts.PropertyNamingPolicy <- JsonNamingPolicy.SnakeCaseLower
        opts

    let buildSidecar
        (account: string)
        (msg: Domain.EmailMessage)
        (att: Domain.EmailAttachment)
        (savedAs: string)
        (sha256: string)
        (now: DateTimeOffset)
        : Domain.SidecarMetadata =
        { SourceType = "email_attachment"
          Account = account
          GmailId = msg.ProviderId
          ThreadId = msg.ThreadId
          Sender = msg.Sender
          Subject = msg.Subject
          EmailDate = msg.Date |> Option.map (fun d -> d.ToString("o"))
          OriginalName = att.FileName
          SavedAs = savedAs
          Sha256 = sha256
          DownloadedAt = now.ToString("o") }

    let serialiseSidecar (sidecar: Domain.SidecarMetadata) : string =
        JsonSerializer.Serialize(sidecar, jsonOptions)

    // ─── HTML-to-text stripping ─────────────────────────────────────

    let private htmlTagRegex = System.Text.RegularExpressions.Regex(@"<[^>]+>", System.Text.RegularExpressions.RegexOptions.Compiled)
    let private whitespaceCollapseRegex = System.Text.RegularExpressions.Regex(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled)

    /// Strip HTML tags, decode common entities, and collapse whitespace.
    let stripHtml (html: string) : string =
        if System.String.IsNullOrWhiteSpace(html) then ""
        else
            html
            |> fun s -> htmlTagRegex.Replace(s, " ")
            |> fun s -> s.Replace("&amp;", "&")
            |> fun s -> s.Replace("&lt;", "<")
            |> fun s -> s.Replace("&gt;", ">")
            |> fun s -> s.Replace("&quot;", "\"")
            |> fun s -> s.Replace("&#39;", "'")
            |> fun s -> s.Replace("&apos;", "'")
            |> fun s -> s.Replace("&nbsp;", " ")
            |> fun s -> whitespaceCollapseRegex.Replace(s, " ")
            |> fun s -> s.Trim()

    // ─── Dedup check ─────────────────────────────────────────────────

    /// Check if a SHA256 hash already exists in the documents table.
    let private hashExists (db: Algebra.Database) (sha256: string) =
        task {
            let! result =
                db.execScalar
                    "SELECT COUNT(*) FROM documents WHERE sha256 = @sha"
                    [ ("@sha", boxVal sha256) ]

            return
                match result with
                | null -> false
                | v -> (v :?> int64) > 0L
        }

    /// Check if a message has already been processed.
    let private messageExists (db: Algebra.Database) (account: string) (gmailId: string) =
        task {
            let! result =
                db.execScalar
                    "SELECT COUNT(*) FROM messages WHERE account = @acc AND gmail_id = @gid"
                    [ ("@acc", boxVal account); ("@gid", boxVal gmailId) ]

            return
                match result with
                | null -> false
                | v -> (v :?> int64) > 0L
        }

    // ─── Record to DB ────────────────────────────────────────────────

    let private recordMessage
        (db: Algebra.Database)
        (account: string)
        (msg: Domain.EmailMessage)
        (now: DateTimeOffset)
        =
        task {
            let! _ =
                db.execNonQuery
                    """INSERT OR IGNORE INTO messages (gmail_id, account, sender, subject, date, thread_id, body_text, label_ids, has_attachments, processed_at)
                       VALUES (@gid, @acc, @sender, @subject, @date, @tid, @body, @labels, @hasAtt, @processed)"""
                    [ ("@gid", boxVal msg.ProviderId)
                      ("@acc", boxVal account)
                      ("@sender", msg.Sender |> Option.map boxVal |> Option.defaultValue (boxVal DBNull.Value))
                      ("@subject", msg.Subject |> Option.map boxVal |> Option.defaultValue (boxVal DBNull.Value))
                      ("@date",
                       msg.Date
                       |> Option.map (fun d -> boxVal (d.ToString("o")))
                       |> Option.defaultValue (boxVal DBNull.Value))
                      ("@tid", boxVal msg.ThreadId)
                      ("@body", msg.BodyText |> Option.map boxVal |> Option.defaultValue (boxVal DBNull.Value))
                      ("@labels", boxVal (String.Join(",", msg.Labels)))
                      ("@hasAtt", boxVal (if msg.HasAttachments then 1L else 0L))
                      ("@processed", boxVal (now.ToString("o"))) ]

            return ()
        }

    let private recordDocument
        (db: Algebra.Database)
        (sourceType: string)
        (account: string)
        (msg: Domain.EmailMessage)
        (att: Domain.EmailAttachment)
        (savedPath: string)
        (sha256: string)
        (now: DateTimeOffset)
        =
        task {
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (source_type, gmail_id, thread_id, account, sender, subject, email_date, original_name, saved_path, category, mime_type, size_bytes, sha256)
                       VALUES (@src, @gid, @tid, @acc, @sender, @subject, @date, @orig, @path, @cat, @mime, @size, @sha)"""
                    [ ("@src", boxVal sourceType)
                      ("@gid", boxVal msg.ProviderId)
                      ("@tid", boxVal msg.ThreadId)
                      ("@acc", boxVal account)
                      ("@sender", msg.Sender |> Option.map boxVal |> Option.defaultValue (boxVal DBNull.Value))
                      ("@subject", msg.Subject |> Option.map boxVal |> Option.defaultValue (boxVal DBNull.Value))
                      ("@date",
                       msg.Date
                       |> Option.map (fun d -> boxVal (d.ToString("o")))
                       |> Option.defaultValue (boxVal DBNull.Value))
                      ("@orig", boxVal att.FileName)
                      ("@path", boxVal savedPath)
                      ("@cat", boxVal "unclassified")
                      ("@mime", boxVal att.MimeType)
                      ("@size", boxVal att.SizeBytes)
                      ("@sha", boxVal sha256) ]

            return ()
        }

    // ─── Sync state ──────────────────────────────────────────────────

    /// Load the last sync timestamp for an account.
    let loadSyncState (db: Algebra.Database) (account: string) =
        task {
            let! result =
                db.execScalar
                    "SELECT last_sync_at FROM sync_state WHERE account = @acc"
                    [ ("@acc", boxVal account) ]

            return
                match result with
                | null -> None
                | v ->
                    v.ToString()
                    |> Option.ofObj
                    |> Option.bind (fun s ->
                        match DateTimeOffset.TryParse(s) with
                        | true, dto -> Some dto
                        | _ -> None)
        }

    /// Save sync state after a successful sync.
    let private saveSyncState (db: Algebra.Database) (account: string) (messageCount: int) (now: DateTimeOffset) =
        task {
            let! _ =
                db.execNonQuery
                    """INSERT INTO sync_state (account, last_sync_at, message_count)
                       VALUES (@acc, @ts, @cnt)
                       ON CONFLICT(account) DO UPDATE SET last_sync_at = @ts, message_count = message_count + @cnt"""
                    [ ("@acc", boxVal account)
                      ("@ts", boxVal (now.ToString("o")))
                      ("@cnt", boxVal (int64 messageCount)) ]

            return ()
        }

    // ─── Sync result ─────────────────────────────────────────────────

    type SyncResult =
        { Account: string
          MessagesProcessed: int
          AttachmentsDownloaded: int
          DuplicatesSkipped: int
          Errors: string list }

    type DryRunItem =
        { Account: string
          GmailId: string
          Subject: string option
          AttachmentCount: int }

    // ─── Core sync logic ─────────────────────────────────────────────

    // ─── Sync: per-attachment processing ─────────────────────────────

    type private SyncAccum =
        { Downloaded: int; Duplicates: int; Processed: int; Errors: string list }

    let private syncAccumZero =
        { Downloaded = 0; Duplicates = 0; Processed = 0; Errors = [] }

    let private tryFetchBody (db: Algebra.Database) (provider: Algebra.EmailProvider) (logger: Algebra.Logger) (account: string) (msg: Domain.EmailMessage) =
        task {
            if msg.BodyText.IsSome then ()
            else
                try
                    let! body = provider.getMessageBody msg.ProviderId
                    match body with
                    | Some raw ->
                        let clean = stripHtml raw
                        if not (String.IsNullOrWhiteSpace(clean)) then
                            let! _ =
                                db.execNonQuery
                                    "UPDATE messages SET body_text = @body WHERE account = @acc AND gmail_id = @gid"
                                    [ ("@body", boxVal clean); ("@acc", boxVal account); ("@gid", boxVal msg.ProviderId) ]
                            ()
                    | None -> ()
                with ex ->
                    logger.debug $"[{account}] Could not fetch body for {msg.ProviderId}: {ex.Message}"
        }

    let private processAttachment
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (account: string) (msg: Domain.EmailMessage) (unclassifiedDir: string) (now: DateTimeOffset)
        (accum: SyncAccum) (att: Domain.EmailAttachment)
        : Task<SyncAccum> =
        task {
            let sha = computeSha256 att.Content
            let! isDup = hashExists db sha
            if isDup then
                // Self-healing: check if file exists on disk, re-save if missing
                let! rows = db.execReader "SELECT saved_path FROM documents WHERE sha256 = @sha" [ ("@sha", boxVal sha) ]
                match rows |> List.tryHead |> Option.bind (fun r -> Prelude.RowReader(r).OptString "saved_path") with
                | Some savedPath ->
                    let archiveDir = Path.GetDirectoryName(unclassifiedDir) |> Option.ofObj |> Option.defaultValue ""
                    let fullPath = Path.Combine(archiveDir, savedPath)
                    if not (fs.fileExists fullPath) then
                        fs.createDirectory (Path.GetDirectoryName(fullPath) |> Option.ofObj |> Option.defaultValue unclassifiedDir)
                        do! fs.writeAllBytes fullPath att.Content
                        logger.info $"[{account}] Re-downloaded missing file: {savedPath}"
                | None -> ()
                return { accum with Duplicates = accum.Duplicates + 1 }
            else
                let name = buildStandardName msg.Date msg.Sender att.FileName
                let savePath = Path.Combine(unclassifiedDir, name)
                fs.createDirectory unclassifiedDir
                do! fs.writeAllBytes savePath att.Content
                let sidecar = buildSidecar account msg att name sha now
                do! fs.writeAllText (savePath + ".meta.json") (serialiseSidecar sidecar)
                do! recordDocument db "email_attachment" account msg att name sha now
                logger.info $"[{account}] Downloaded: {name}"
                return { accum with Downloaded = accum.Downloaded + 1 }
        }

    let private processMessage
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (provider: Algebra.EmailProvider) (clock: Algebra.Clock) (config: Domain.HermesConfig)
        (account: string) (unclassifiedDir: string)
        (accum: SyncAccum) (msg: Domain.EmailMessage)
        : Task<SyncAccum> =
        task {
            try
                let! exists = messageExists db account msg.ProviderId
                if exists then return accum
                else
                let now = clock.utcNow ()
                do! recordMessage db account msg now
                do! tryFetchBody db provider logger account msg
                let! atts = provider.getAttachments msg.ProviderId
                let valid = atts |> List.filter (fun a -> a.SizeBytes >= int64 config.MinAttachmentSize)
                let! attAccum = Prelude.foldTask (processAttachment fs db logger account msg unclassifiedDir now) accum valid
                return { attAccum with Processed = attAccum.Processed + 1 }
            with ex ->
                let err = $"[{account}] Error processing {msg.ProviderId}: {ex.Message}"
                logger.error err
                return { accum with Errors = err :: accum.Errors }
        }

    /// Sync one account: list new messages, download attachments, dedup, record.
    /// Default high-water mark for first sync: 2 fiscal years + 1 month ago.
    /// Australian FY runs July–June, so this covers current + previous tax years.
    let defaultHighWaterMark (clock: Algebra.Clock) : DateTimeOffset =
        let now = clock.utcNow ()
        // Current FY starts July of this year or last year
        let fyStartYear = if now.Month >= 7 then now.Year else now.Year - 1
        // 2 FY ago + 1 month buffer = June 1 two years before current FY start
        DateTimeOffset(fyStartYear - 2, 6, 1, 0, 0, 0, TimeSpan.Zero)

    let syncAccount
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (provider: Algebra.EmailProvider)
        (config: Domain.HermesConfig) (account: string)
        : Task<SyncResult> =
        task {
            try
                let! lastSync = loadSyncState db account
                // First sync uses default highWaterMark (2 FY + 1 month ago), not "all time"
                let syncSince = lastSync |> Option.defaultWith (fun () -> defaultHighWaterMark clock)
                let sinceStr = syncSince.ToString("yyyy-MM-dd")
                logger.info $"[{account}] Syncing since {sinceStr}"
                let! messages = provider.listNewMessages (Some syncSince)
                logger.info $"[{account}] Found {messages.Length} messages"

                let unclassifiedDir = Path.Combine(config.ArchiveDir, "unclassified")
                let! accum =
                    Prelude.foldTask
                        (processMessage fs db logger provider clock config account unclassifiedDir)
                        syncAccumZero
                        messages

                // Only advance highWaterMark if we actually processed messages
                // Set it to the date of the latest message we saw, not "now"
                if messages.Length > 0 then
                    let latestMessageDate =
                        messages
                        |> List.choose (fun m -> m.Date)
                        |> List.sortDescending
                        |> List.tryHead
                        |> Option.defaultValue (clock.utcNow ())
                    let hwmStr = latestMessageDate.ToString("yyyy-MM-dd")
                    do! saveSyncState db account accum.Processed latestMessageDate
                    logger.info $"[{account}] Processed {accum.Processed} messages, {accum.Downloaded} attachments, highWaterMark={hwmStr}"
                return
                    { Account = account
                      MessagesProcessed = accum.Processed
                      AttachmentsDownloaded = accum.Downloaded
                      DuplicatesSkipped = accum.Duplicates
                      Errors = accum.Errors |> List.rev }
            with ex ->
                logger.error $"[{account}] Sync failed: {ex.Message}"
                return
                    { Account = account; MessagesProcessed = 0; AttachmentsDownloaded = 0
                      DuplicatesSkipped = 0; Errors = [ ex.Message ] }
        }

    /// Dry run: list what would be downloaded without actually downloading.
    let dryRun
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (provider: Algebra.EmailProvider)
        (account: string)
        : Task<DryRunItem list> =
        task {
            let! lastSync = loadSyncState db account
            let! messages = provider.listNewMessages lastSync
            let items = ResizeArray<DryRunItem>()

            for msg in messages do
                let! alreadyProcessed = messageExists db account msg.ProviderId

                if not alreadyProcessed then
                    let! attachments = provider.getAttachments msg.ProviderId

                    if attachments.Length > 0 then
                        items.Add(
                            { Account = account
                              GmailId = msg.ProviderId
                              Subject = msg.Subject
                              AttachmentCount = attachments.Length }
                        )

                        let subjectStr = msg.Subject |> Option.defaultValue "(no subject)"
                        logger.info $"[{account}] Would download {attachments.Length} attachment(s) from: {subjectStr}"

            return items |> Seq.toList
        }

    /// Sync all configured accounts.
    let syncAll
        (fs: Algebra.FileSystem)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (clock: Algebra.Clock)
        (makeProvider: string -> Algebra.EmailProvider)
        (config: Domain.HermesConfig)
        : Task<SyncResult list> =
        task {
            let results = ResizeArray<SyncResult>()

            for acct in config.Accounts do
                let provider = makeProvider acct.Label
                let! result = syncAccount fs db logger clock provider config acct.Label
                results.Add(result)

            return results |> Seq.toList
        }

    /// Dry-run all configured accounts.
    let dryRunAll
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (makeProvider: string -> Algebra.EmailProvider)
        (config: Domain.HermesConfig)
        : Task<DryRunItem list> =
        task {
            let items = ResizeArray<DryRunItem>()

            for acct in config.Accounts do
                let provider = makeProvider acct.Label
                let! acctItems = dryRun db logger provider acct.Label
                items.AddRange(acctItems)

            return items |> Seq.toList
        }

    // ─── Backfill ────────────────────────────────────────────────────

    type BackfillState =
        { PageToken: string option
          Scanned: int
          TotalEstimate: int64
          Completed: bool
          StartedAt: DateTimeOffset option }

    let loadBackfillState (db: Algebra.Database) (account: string) : Task<BackfillState> =
        task {
            let! rows =
                db.execReader
                    "SELECT backfill_page_token, backfill_scanned, backfill_total_estimate, backfill_completed, backfill_started_at FROM sync_state WHERE account = @acc"
                    [ ("@acc", boxVal account) ]
            match rows with
            | [] ->
                return { PageToken = None; Scanned = 0; TotalEstimate = 0L; Completed = false; StartedAt = None }
            | row :: _ ->
                let r = Prelude.RowReader(row)
                return
                    { PageToken = r.OptString "backfill_page_token"
                      Scanned = r.Int64 "backfill_scanned" 0L |> int
                      TotalEstimate = r.Int64 "backfill_total_estimate" 0L
                      Completed = r.Int64 "backfill_completed" 0L > 0L
                      StartedAt = r.OptDateTimeOffset "backfill_started_at" }
        }

    let private saveBackfillState (db: Algebra.Database) (account: string) (state: BackfillState) =
        task {
            let! _ =
                db.execNonQuery
                    """UPDATE sync_state
                       SET backfill_page_token = @tok, backfill_scanned = @scanned,
                           backfill_total_estimate = @est, backfill_completed = @done,
                           backfill_started_at = @started
                       WHERE account = @acc"""
                    [ ("@tok", state.PageToken |> Option.map boxVal |> Option.defaultValue (boxVal System.DBNull.Value))
                      ("@scanned", boxVal (int64 state.Scanned))
                      ("@est", boxVal state.TotalEstimate)
                      ("@done", boxVal (if state.Completed then 1L else 0L))
                      ("@started", state.StartedAt |> Option.map (fun d -> boxVal (d.ToString("o"))) |> Option.defaultValue (boxVal System.DBNull.Value))
                      ("@acc", boxVal account) ]
            ()
        }

    /// Build the Gmail query string for backfill.
    let private buildBackfillQuery (bf: Domain.BackfillConfig) : string option =
        let parts = ResizeArray<string>()
        if bf.AttachmentsOnly then parts.Add("has:attachment")
        bf.Since |> Option.iter (fun since -> parts.Add($"after:{since.ToUnixTimeSeconds()}"))
        if parts.Count = 0 then None else Some (parts |> String.concat " ")

    /// Process a single backfill attachment — returns 1 if new, 0 if skipped/error.
    let private processBackfillAttachment
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger) (clock: Algebra.Clock)
        (config: Domain.HermesConfig) (account: string) (msg: Domain.EmailMessage)
        (count: int) (att: Domain.EmailAttachment)
        : Task<int> =
        task {
            try
                if int64 att.SizeBytes < int64 config.MinAttachmentSize then return count
                else
                let sha = computeSha256 att.Content
                let! isDup = hashExists db sha
                if isDup then return count
                else
                let name = buildStandardName msg.Date msg.Sender att.FileName
                let unclDir = IO.Path.Combine(config.ArchiveDir, "unclassified")
                let destPath = IO.Path.Combine(unclDir, name)
                fs.createDirectory unclDir
                do! fs.writeAllBytes destPath att.Content
                let sidecar = buildSidecar account msg att name sha (clock.utcNow ())
                do! fs.writeAllText (destPath + ".meta.json") (serialiseSidecar sidecar)
                do! recordDocument db "email_attachment" account msg att name sha (clock.utcNow ())
                return count + 1
            with ex ->
                logger.warn $"[{account}] Backfill attachment error ({att.FileName}): {ex.Message}"
                return count
        }

    /// Process a single backfill message — returns count of new attachments.
    let private processBackfillMessage
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger) (clock: Algebra.Clock)
        (provider: Algebra.EmailProvider) (config: Domain.HermesConfig) (account: string)
        (count: int) (msg: Domain.EmailMessage)
        : Task<int> =
        task {
            let! exists = messageExists db account msg.ProviderId
            if exists then return count
            else
            do! recordMessage db account msg (clock.utcNow ())
            let! atts = provider.getAttachments msg.ProviderId
            return! Prelude.foldTask (processBackfillAttachment fs db logger clock config account msg) count atts
        }

    /// Run one batch of backfill for an account. Returns (newMessages, completed).
    let backfillAccount
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (provider: Algebra.EmailProvider)
        (config: Domain.HermesConfig) (account: Domain.AccountConfig)
        : Task<int * bool> =
        task {
            if not account.Backfill.Enabled then return (0, true)
            else
            let! state = loadBackfillState db account.Label
            if state.Completed then return (0, true)
            else

            let! _ = db.execNonQuery "INSERT OR IGNORE INTO sync_state (account) VALUES (@acc)" [ ("@acc", boxVal account.Label) ]
            let startedAt = state.StartedAt |> Option.defaultValue (clock.utcNow ())
            let query = buildBackfillQuery account.Backfill
            let tokenLabel = state.PageToken |> Option.defaultValue "start"
            logger.info $"[{account.Label}] Backfill page (scanned: {state.Scanned}, token: {tokenLabel})"

            let! page = provider.listMessagePage state.PageToken query account.Backfill.BatchSize
            let! newCount =
                Prelude.foldTask
                    (processBackfillMessage fs db logger clock provider config account.Label)
                    0
                    page.Messages

            let completed = page.NextPageToken.IsNone
            let newState =
                { PageToken = page.NextPageToken
                  Scanned = state.Scanned + page.Messages.Length
                  TotalEstimate = if page.ResultSizeEstimate > 0L then page.ResultSizeEstimate else state.TotalEstimate
                  Completed = completed
                  StartedAt = Some startedAt }

            do! saveBackfillState db account.Label newState
            logger.info $"[{account.Label}] Backfill: {newCount} new, {page.Messages.Length} scanned, completed={completed}"
            return (newCount, completed)
        }

    // ═══════════════════════════════════════════════════════════════════
    // Channel-based sync: enumerate IDs → channel → N concurrent consumers
    // ═══════════════════════════════════════════════════════════════════

    /// Producer: page through all Gmail stubs for an account, push IDs to a channel.
    /// Completes the channel writer when all pages are exhausted.
    let enumerateIds
        (provider: Algebra.EmailProvider)
        (logger: Algebra.Logger)
        (account: string)
        (query: string)
        (output: ChannelWriter<string>)
        (enumeratedCount: int ref)
        (ct: CancellationToken)
        : Task<int> =
        task {
            let mutable pageToken : string option = None
            let mutable total = 0
            let mutable hasMore = true

            while hasMore && not ct.IsCancellationRequested do
                try
                    let! page = provider.listStubPage pageToken (Some query) 500
                    for id in page.Ids do
                        do! output.WriteAsync(id, ct)
                    total <- total + page.Ids.Length
                    enumeratedCount.Value <- total

                    match page.NextPageToken with
                    | Some t -> pageToken <- Some t
                    | None -> hasMore <- false

                    if page.Ids.Length > 0 then
                        logger.debug $"[{account}] Enumerated {total} message IDs so far"
                with
                | :? OperationCanceledException -> hasMore <- false
                | ex ->
                    logger.warn $"[{account}] Enumerate page failed: {ex.Message}, retrying in 30s"
                    try do! Task.Delay(TimeSpan.FromSeconds(30.0), ct) with :? OperationCanceledException -> hasMore <- false

            output.Complete()
            logger.info $"[{account}] Enumeration complete: {total} message IDs"
            return total
        }

    /// Consumer: pull message IDs from channel, check DB, fetch + save if new.
    /// Pushes saved file paths to the ingest channel for downstream pipeline.
    let processMessageConsumer
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (provider: Algebra.EmailProvider)
        (config: Domain.HermesConfig) (account: string)
        (input: ChannelReader<string>)
        (ingestOutput: ChannelWriter<string> option)
        (processedCount: int ref)
        (consumerId: int)
        (ct: CancellationToken)
        : Task<int * int> =
        task {
            let mutable processed = 0
            let mutable downloaded = 0
            let unclassifiedDir = Path.Combine(config.ArchiveDir, "unclassified")

            try
                while not ct.IsCancellationRequested do
                    let! messageId = input.ReadAsync(ct)
                    try
                        let! exists = messageExists db account messageId
                        if not exists then
                            let! msg = provider.getFullMessage messageId
                            let now = clock.utcNow ()
                            do! recordMessage db account msg now

                            // Fetch and store body text, save as document
                            if msg.BodyText.IsSome then
                                let clean = stripHtml msg.BodyText.Value
                                if not (System.String.IsNullOrWhiteSpace(clean)) then
                                    let! _ =
                                        db.execNonQuery
                                            "UPDATE messages SET body_text = @body WHERE account = @acc AND gmail_id = @gid"
                                            [ ("@body", boxVal clean); ("@acc", boxVal account); ("@gid", boxVal messageId) ]

                                    // Save email body as a document — one per thread (latest wins)
                                    let subject = msg.Subject |> Option.defaultValue "(no subject)"
                                    let sender = msg.Sender |> Option.defaultValue "unknown"
                                    let dateStr = msg.Date |> Option.map (fun d -> d.ToString("yyyy-MM-dd")) |> Option.defaultValue "undated"
                                    let bodyMd = $"# {subject}\n\n**From:** {sender}  \n**Date:** {dateStr}\n\n---\n\n{clean}"
                                    let bodyBytes = System.Text.Encoding.UTF8.GetBytes(bodyMd)
                                    let bodySha = computeSha256 bodyBytes
                                    let! bodyDup = hashExists db bodySha
                                    if not bodyDup then
                                        // Check if thread already has a body document
                                        let! existingRows =
                                            db.execReader
                                                "SELECT id, saved_path, email_date FROM documents WHERE thread_id = @tid AND account = @acc AND source_type = 'email_body' LIMIT 1"
                                                [ ("@tid", boxVal msg.ThreadId); ("@acc", boxVal account) ]
                                        let shouldSave =
                                            match existingRows |> List.tryHead with
                                            | None -> true
                                            | Some row ->
                                                let r = Prelude.RowReader(row)
                                                let existingDate = r.OptString "email_date" |> Option.bind (fun s -> match DateTimeOffset.TryParse(s) with true, d -> Some d | _ -> None)
                                                let msgDate = msg.Date
                                                // Save if we're newer or existing has no date
                                                match msgDate, existingDate with
                                                | Some md, Some ed -> md > ed
                                                | Some _, None -> true
                                                | _ -> false

                                        if shouldSave then
                                            // Delete old body document for this thread if it exists
                                            for row in existingRows do
                                                let r = Prelude.RowReader(row)
                                                match r.OptInt64 "id" with
                                                | Some oldId ->
                                                    let! _ = db.execNonQuery "DELETE FROM documents WHERE id = @id" [ ("@id", boxVal oldId) ]
                                                    match r.OptString "saved_path" with
                                                    | Some oldPath ->
                                                        let fullOld = Path.Combine(config.ArchiveDir, oldPath)
                                                        if fs.fileExists fullOld then try fs.deleteFile fullOld with _ -> ()
                                                        let metaOld = fullOld + ".meta.json"
                                                        if fs.fileExists metaOld then try fs.deleteFile metaOld with _ -> ()
                                                    | None -> ()
                                                | None -> ()

                                            let bodyName = buildStandardName msg.Date msg.Sender $"{subject}.md" |> fun n -> if n.Length > 200 then n.Substring(0, 200) else n
                                            let bodyPath = Path.Combine(unclassifiedDir, bodyName)
                                            fs.createDirectory unclassifiedDir
                                            do! fs.writeAllBytes bodyPath bodyBytes
                                            let bodyAtt : Domain.EmailAttachment = { FileName = bodyName; MimeType = "text/markdown"; SizeBytes = int64 bodyBytes.Length; Content = bodyBytes }
                                            let bodySidecar = buildSidecar account msg bodyAtt bodyName bodySha now
                                            do! fs.writeAllText (bodyPath + ".meta.json") (serialiseSidecar bodySidecar)
                                            do! recordDocument db "email_body" account msg bodyAtt bodyName bodySha now
                                            downloaded <- downloaded + 1
                                            logger.info $"[{account}/{consumerId}] Saved email body: {bodyName}"

                                            match ingestOutput with
                                            | Some writer ->
                                                try do! writer.WriteAsync(bodyPath, ct)
                                                with :? OperationCanceledException -> ()
                                            | None -> ()

                            // Fetch attachments
                            let! atts = provider.getAttachments messageId
                            let valid = atts |> List.filter (fun a -> a.SizeBytes >= int64 config.MinAttachmentSize)

                            for att in valid do
                                let sha = computeSha256 att.Content
                                let! isDup = hashExists db sha
                                if not isDup then
                                    let name = buildStandardName msg.Date msg.Sender att.FileName
                                    let savePath = Path.Combine(unclassifiedDir, name)
                                    fs.createDirectory unclassifiedDir
                                    do! fs.writeAllBytes savePath att.Content
                                    let sidecar = buildSidecar account msg att name sha now
                                    do! fs.writeAllText (savePath + ".meta.json") (serialiseSidecar sidecar)
                                    do! recordDocument db "email_attachment" account msg att name sha now
                                    downloaded <- downloaded + 1
                                    logger.info $"[{account}/{consumerId}] Downloaded: {name}"

                                    // Push to downstream pipeline
                                    match ingestOutput with
                                    | Some writer ->
                                        try do! writer.WriteAsync(savePath, ct)
                                        with :? OperationCanceledException -> ()
                                    | None -> ()

                            processed <- processed + 1
                            System.Threading.Interlocked.Increment(processedCount) |> ignore
                    with
                    | :? Google.GoogleApiException as ex when ex.HttpStatusCode = System.Net.HttpStatusCode.TooManyRequests ->
                        logger.warn $"[{account}/{consumerId}] Rate limited, backing off 60s"
                        try do! Task.Delay(TimeSpan.FromSeconds(60.0), ct) with :? OperationCanceledException -> ()
                    | ex ->
                        logger.warn $"[{account}/{consumerId}] Error processing {messageId}: {ex.Message}"
            with
            | :? OperationCanceledException -> ()
            | :? ChannelClosedException -> ()

            logger.debug $"[{account}/{consumerId}] Consumer done: {processed} processed, {downloaded} downloaded"
            return (processed, downloaded)
        }

    /// Run a full channel-based sync for one account.
    /// Enumerates all message IDs, processes concurrently, advances watermark when done.
    let syncAccountChanneled
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (provider: Algebra.EmailProvider)
        (config: Domain.HermesConfig) (account: string)
        (ingestOutput: ChannelWriter<string> option)
        (concurrency: int)
        (enumeratedCounter: int ref) (processedCounter: int ref)
        (ct: CancellationToken)
        : Task<SyncResult> =
        task {
            try
                let! lastSync = loadSyncState db account
                let syncSince = lastSync |> Option.defaultWith (fun () -> defaultHighWaterMark clock)
                let sinceStr = syncSince.ToString("yyyy-MM-dd")
                let queryTimestamp = clock.utcNow ()
                let epoch = syncSince.ToUnixTimeSeconds()
                let query = $"has:attachment after:{epoch}"
                logger.info $"[{account}] Channel sync since {sinceStr} with {concurrency} consumers"

                // Create the message ID channel
                let idChannel = Channel.CreateBounded<string>(BoundedChannelOptions(10000, FullMode = BoundedChannelFullMode.Wait))

                // Start enumeration producer
                let enumTask = enumerateIds provider logger account query idChannel.Writer enumeratedCounter ct

                // Start N consumers
                let consumerTasks =
                    [| for i in 1..concurrency ->
                        processMessageConsumer fs db logger clock provider config account idChannel.Reader ingestOutput processedCounter i ct |]

                // Wait for enumeration to finish (completes the channel)
                let! totalEnumerated = enumTask

                // Wait for all consumers to drain the channel
                let! results = Task.WhenAll(consumerTasks)

                let totalProcessed = results |> Array.sumBy fst
                let totalDownloaded = results |> Array.sumBy snd

                // Advance watermark: enumeration is complete and channel is drained
                if totalEnumerated > 0 then
                    do! saveSyncState db account totalProcessed queryTimestamp
                    logger.info $"[{account}] Sync complete: {totalEnumerated} enumerated, {totalProcessed} new, {totalDownloaded} attachments"

                return
                    { Account = account
                      MessagesProcessed = totalProcessed
                      AttachmentsDownloaded = totalDownloaded
                      DuplicatesSkipped = totalEnumerated - totalProcessed
                      Errors = [] }
            with ex ->
                logger.error $"[{account}] Channel sync failed: {ex.Message}"
                return
                    { Account = account; MessagesProcessed = 0; AttachmentsDownloaded = 0
                      DuplicatesSkipped = 0; Errors = [ ex.Message ] }
        }
