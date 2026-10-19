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
        // Use a cross-platform superset so files created on macOS are safe on Windows too
        let platform = Path.GetInvalidFileNameChars() |> Set.ofArray
        let windows = set [| '<'; '>'; ':'; '"'; '/'; '\\'; '|'; '?'; '*' |]
        Set.union platform windows

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
          ProviderId = msg.ProviderId
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
                    """INSERT OR IGNORE INTO messages (gmail_id, account, sender, subject, date, thread_id, folder_path, label_ids, has_attachments, processed_at)
                       VALUES (@gid, @acc, @sender, @subject, @date, @tid, @folder, @labels, @hasAtt, @processed)"""
                    [ ("@gid", boxVal msg.ProviderId)
                      ("@acc", boxVal account)
                      ("@sender", msg.Sender |> Option.map boxVal |> Option.defaultValue (boxVal DBNull.Value))
                      ("@subject", msg.Subject |> Option.map boxVal |> Option.defaultValue (boxVal DBNull.Value))
                      ("@date",
                       msg.Date
                       |> Option.map (fun d -> boxVal (d.ToString("o")))
                       |> Option.defaultValue (boxVal DBNull.Value))
                      ("@tid", boxVal msg.ThreadId)
                      ("@folder", boxVal "")
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
        : Task<int64> =
        task {
            let! idObj =
                db.execScalar
                    """INSERT INTO documents (source_type, gmail_id, thread_id, account, sender, subject, email_date, original_name, saved_path, category, mime_type, size_bytes, sha256)
                       VALUES (@src, @gid, @tid, @acc, @sender, @subject, @date, @orig, @path, @cat, @mime, @size, @sha)
                       RETURNING id"""
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

            let docId = match idObj with :? int64 as i -> i | _ -> 0L
            return docId
        }

    let private recordBody account message now db (source: ThreadBodyIngestion.Source) =
        let attachment: Domain.EmailAttachment =
            { FileName = source.FileName
              MimeType = "text/markdown"
              SizeBytes = int64 source.Bytes.Length
              Content = source.Bytes }
        recordDocument db ThreadBodySource.sourceType account message attachment source.SavedPath source.Sha256 now

    let private recordChannelAttachment account message now db attachment path sha256 =
        recordDocument db EmailAttachmentIngestion.sourceType account message attachment path sha256 now

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

    exception ChannelConsumerFailed of errors: string list

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

    // Storage failures must escape; processMessage catches only after rollback.
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
                let! rows = db.execReader "SELECT id, saved_path FROM documents WHERE sha256 = @sha ORDER BY id" [ ("@sha", boxVal sha) ]
                match rows |> List.tryHead with
                | Some row ->
                    let identity = Prelude.RowReader(row)
                    let savedPath = identity.String "saved_path" ""
                    let archiveDir = Path.GetDirectoryName(unclassifiedDir) |> Option.ofObj |> Option.defaultValue ""
                    let fullPath = Path.Combine(archiveDir, savedPath)
                    do! SourceFiles.restore db fs (identity.Int64 "id" 0L) fullPath sha att.Content
                | None -> ()
                return { accum with Duplicates = accum.Duplicates + 1 }
            else
                let senderDomain = ArchiveWriter.extractSenderDomain (msg.Sender |> Option.defaultValue "")
                let subject = msg.Subject |> Option.defaultValue "(no subject)"
                let relFolder = ArchiveWriter.threadFolderPath account senderDomain subject msg.ThreadId
                let archiveDir = Path.GetDirectoryName(unclassifiedDir) |> Option.ofObj |> Option.defaultValue ""
                let absFolder = ArchiveWriter.ensureFolder fs archiveDir relFolder
                let! attFileName =
                    ArchiveWriter.writeAttachment (SourceFiles.newFileSystem db fs)
                        absFolder (msg.Date |> Option.defaultValue now) att.FileName sha att.Content
                let relPath = Path.Combine(relFolder, attFileName)

                let sidecarFile : ArchiveWriter.SidecarFile =
                    { Name = attFileName; MimeType = att.MimeType; SizeBytes = att.SizeBytes; Sha256 = sha }
                let sidecarData : ArchiveWriter.SidecarData =
                    { Version = 2; SourceType = "email_attachment"; Account = account
                      ProviderId = msg.ProviderId; ThreadId = msg.ThreadId
                      Sender = msg.Sender; Subject = msg.Subject
                      ReceivedAt = now.ToString("o"); Files = [ sidecarFile ] }
                do! ArchiveWriter.writeSidecar (SourceFiles.metadataFileSystem db fs) absFolder sidecarData

                let! _ = recordDocument db "email_attachment" account msg att relPath sha now
                logger.info $"[{account}] Downloaded: {attFileName}"
                return { accum with Downloaded = accum.Downloaded + 1 }
        }

    let private persistLegacyMessage (fs: Algebra.FileSystem) (db: Algebra.Database)
        (logger: Algebra.Logger) (account: string) (unclassifiedDir: string)
        (now: DateTimeOffset) (accum: SyncAccum)
        (msg: Domain.EmailMessage) (valid: Domain.EmailAttachment list) : Task<SyncAccum> =
        task {
            let! exists = messageExists db account msg.ProviderId
            if exists then return accum
            else
                do! recordMessage db account msg now
                let! updated =
                    Prelude.foldTask
                        (processAttachment fs db logger account msg unclassifiedDir now) accum valid
                return { updated with Processed = updated.Processed + 1 }
        }

    let private processLegacyNew (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (provider: Algebra.EmailProvider) (clock: Algebra.Clock) (config: Domain.HermesConfig)
        (account: string) (unclassifiedDir: string) (accum: SyncAccum)
        (msg: Domain.EmailMessage) : Task<SyncAccum> =
        task {
            let! attachments = provider.getAttachments msg.ProviderId
            let valid = attachments |> List.filter (fun file -> file.SizeBytes >= int64 config.MinAttachmentSize)
            let now = clock.utcNow ()
            return! db.transaction.Run(fun transaction ->
                persistLegacyMessage fs transaction logger account unclassifiedDir now accum msg valid)
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
                    return! processLegacyNew fs db logger provider clock config account unclassifiedDir accum msg
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
                if messages.Length > 0 && accum.Errors.IsEmpty then
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

    let private writeBackfillSource (fs: Algebra.FileSystem) (db: Algebra.Database)
        (clock: Algebra.Clock) (config: Domain.HermesConfig) (account: string)
        (msg: Domain.EmailMessage) (att: Domain.EmailAttachment) (sha: string) : Task<unit> =
        let name = buildStandardName msg.Date msg.Sender att.FileName
        let directory = Path.Combine(config.ArchiveDir, "unclassified")
        let destination = Path.Combine(directory, name)
        task {
            fs.createDirectory directory
            do! SourceFiles.writeNew db fs destination att.Content
            let sidecar = buildSidecar account msg att name sha (clock.utcNow ())
            do! (SourceFiles.metadataFileSystem db fs).writeAllText
                    (destination + ".meta.json") (serialiseSidecar sidecar)
            let! _ = recordDocument db "email_attachment" account msg att
                        (Path.Combine("unclassified", name)) sha (clock.utcNow ())
            return ()
        }

    /// Attachment failures propagate to the enclosing message transaction.
    let private processBackfillAttachment
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger) (clock: Algebra.Clock)
        (config: Domain.HermesConfig) (account: string) (msg: Domain.EmailMessage)
        (count: int) (att: Domain.EmailAttachment)
        : Task<int> =
        task {
            if int64 att.SizeBytes < int64 config.MinAttachmentSize then return count
            else
                let sha = computeSha256 att.Content
                let! isDup = hashExists db sha
                if isDup then return count
                else
                    do! writeBackfillSource fs db clock config account msg att sha
                    return count + 1
        }

    let private persistBackfillMessage (fs: Algebra.FileSystem) (db: Algebra.Database)
        (logger: Algebra.Logger) (clock: Algebra.Clock) (config: Domain.HermesConfig)
        (account: string) (count: int) (msg: Domain.EmailMessage)
        (attachments: Domain.EmailAttachment list) : Task<int> =
        task {
            let! exists = messageExists db account msg.ProviderId
            if exists then return count
            else
                do! recordMessage db account msg (clock.utcNow ())
                return! Prelude.foldTask
                    (processBackfillAttachment fs db logger clock config account msg) count attachments
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
                let! atts = provider.getAttachments msg.ProviderId
                return! db.transaction.Run(fun transaction ->
                    persistBackfillMessage fs transaction logger clock config account count msg atts)
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

    /// Producer compatibility API. Failures fault the task and always close input.
    let enumerateIds
        (provider: Algebra.EmailProvider)
        (logger: Algebra.Logger)
        (account: string)
        (query: string)
        (output: ChannelWriter<string>)
        (enumeratedCount: int ref)
        (ct: CancellationToken)
        : Task<int> =
        EmailChannelEnumeration.runWithDelay EmailRateLimit.systemDelay
            provider logger account query output enumeratedCount ct

    let internal channelContext (fs: Algebra.FileSystem) (db: Algebra.Database)
        (logger: Algebra.Logger) (clock: Algebra.Clock) (config: Domain.HermesConfig)
        (account: string) : EmailChannelPersistence.Context =
        { Database = db
          Files = fs
          Logger = logger
          Clock = clock
          Config = config
          Account = account
          CleanBody = stripHtml
          Store =
              { Exists = messageExists
                Acknowledge = recordMessage
                Body = recordBody
                Attachment = recordChannelAttachment } }

    /// Compatibility adapter: callbacks run after commit; failures are explicit.
    let processMessageConsumer
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (provider: Algebra.EmailProvider)
        (config: Domain.HermesConfig) (account: string)
        (input: ChannelReader<string>)
        (onIngest: int64 -> string -> Task<unit>)
        (processedCount: int ref)
        (consumerId: int)
        (ct: CancellationToken)
        : Task<int * int> =
        let context = channelContext fs db logger clock config account
        task {
            let! result = EmailChannelConsumer.run context provider input onIngest processedCount ct
            logger.debug $"[{account}/{consumerId}] Consumer committed {result.Processed} messages"
            if not result.Errors.IsEmpty then
                return raise (ChannelConsumerFailed result.Errors)
            else
                return result.Processed, result.Downloaded
        }

    type private ChannelRun =
        { Enumerated: int
          Summary: EmailChannelConsumer.Summary }

    let private notifyChannel (db: Algebra.Database) (writer: ChannelWriter<Document.T>)
        (id: int64) (_path: string) : Task<unit> =
        task {
            let! rows = db.execReader "SELECT * FROM documents WHERE id = @id" [ "@id", boxVal id ]
            let row =
                rows |> List.tryHead
                |> Option.defaultWith (fun () -> invalidOp $"Committed document {id} not found")
            do! writer.WriteAsync(Document.fromRow row).AsTask()
        }

    let private observeEnumeration (enumeration: Task<int>) : Task<Result<int, string>> =
        task {
            try
                let! count = enumeration
                return Ok count
            with error ->
                return Error $"Channel enumeration failed: {error.Message}"
        }

    let private channelRun count outcome results : ChannelRun =
        let summary =
            results
            |> Array.fold EmailChannelConsumer.combine EmailChannelConsumer.empty
        let errors =
            outcome
            |> Result.map (fun _ -> summary.Errors)
            |> Result.defaultWith (fun error -> summary.Errors @ [ error ])
        { Enumerated = count
          Summary = { summary with Errors = errors } }

    let private collectChannel (delay: EmailRateLimit.Delay)
        (context: EmailChannelPersistence.Context)
        (provider: Algebra.EmailProvider) (writer: ChannelWriter<Document.T>)
        (concurrency: int) (ct: CancellationToken) (query: string) : Task<ChannelRun> =
        let options = BoundedChannelOptions(10000, FullMode = BoundedChannelFullMode.Wait)
        let input = Channel.CreateBounded<string>(options)
        let enumerated = ref 0
        let processed = ref 0
        let enumeration =
            EmailChannelEnumeration.runWithDelay delay provider context.Logger
                context.Account query input.Writer enumerated ct
            |> observeEnumeration
        let workers =
            Array.init (max 1 concurrency) (fun _ ->
                EmailChannelConsumer.runWithDelay delay context provider input.Reader
                    (notifyChannel context.Database writer) processed ct)
        task {
            let! outcome = enumeration
            let! results = Task.WhenAll(workers)
            return channelRun enumerated.Value outcome results
        }

    let private channelResponse (account: string) (ct: CancellationToken) (run: ChannelRun) : SyncResult =
        let summary = run.Summary
        let errors =
            if ct.IsCancellationRequested then summary.Errors @ [ "Channel sync cancelled before completion" ]
            elif run.Enumerated <> summary.Processed + summary.Duplicates + summary.Failed then
                summary.Errors @ [ "Channel enumeration was not fully accounted for" ]
            else summary.Errors
        { Account = account
          MessagesProcessed = summary.Processed
          AttachmentsDownloaded = summary.Downloaded
          DuplicatesSkipped = summary.Duplicates
          Errors = errors }

    let private finishChannel (db: Algebra.Database) (logger: Algebra.Logger)
        (account: string) (timestamp: DateTimeOffset) (ct: CancellationToken)
        (run: ChannelRun) : Task<SyncResult> =
        task {
            let response = channelResponse account ct run
            try
                if run.Enumerated > 0 && response.Errors.IsEmpty then
                    do! saveSyncState db account response.MessagesProcessed timestamp
                return response
            with error ->
                logger.error $"[{account}] Watermark was not advanced: {error.Message}"
                return { response with Errors = response.Errors @ [ error.Message ] }
        }

    /// Run a full channel-based sync for one account.
    /// Enumerates all message IDs, processes concurrently, advances watermark when done.
    /// Writes new documents to the pipeline's extract channel via onIngest callback.
    let internal syncAccountWithChannelAndDelay
        (delay: EmailRateLimit.Delay)
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (provider: Algebra.EmailProvider)
        (config: Domain.HermesConfig) (account: string)
        (extractWriter: ChannelWriter<Document.T>)
        (concurrency: int)
        (ct: CancellationToken)
        : Task<SyncResult> =
        task {
            try
                let! lastSync = loadSyncState db account
                let syncSince = lastSync |> Option.defaultWith (fun () -> defaultHighWaterMark clock)
                let queryTimestamp = clock.utcNow ()
                let query = $"after:{syncSince.ToUnixTimeSeconds()}"
                let context = channelContext fs db logger clock config account
                let! run = collectChannel delay context provider extractWriter concurrency ct query
                return! finishChannel db logger account queryTimestamp ct run
            with ex ->
                logger.error $"[{account}] Channel sync failed: {ex.Message}"
                return
                    { Account = account; MessagesProcessed = 0; AttachmentsDownloaded = 0
                      DuplicatesSkipped = 0; Errors = [ ex.Message ] }
        }

    /// Production adapter retains the existing public channel-sync API.
    let syncAccountWithChannel fs db logger clock provider config account
        extractWriter concurrency ct : Task<SyncResult> =
        syncAccountWithChannelAndDelay EmailRateLimit.systemDelay
            fs db logger clock provider config account extractWriter concurrency ct
