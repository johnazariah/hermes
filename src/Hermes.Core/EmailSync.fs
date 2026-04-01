namespace Hermes.Core

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
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
                    """INSERT INTO documents (source_type, gmail_id, account, sender, subject, email_date, original_name, saved_path, category, mime_type, size_bytes, sha256)
                       VALUES (@src, @gid, @acc, @sender, @subject, @date, @orig, @path, @cat, @mime, @size, @sha)"""
                    [ ("@src", boxVal "email_attachment")
                      ("@gid", boxVal msg.ProviderId)
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

    /// Sync one account: list new messages, download attachments, dedup, record.
    let syncAccount
        (fs: Algebra.FileSystem)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (clock: Algebra.Clock)
        (provider: Algebra.EmailProvider)
        (config: Domain.HermesConfig)
        (account: string)
        : Task<SyncResult> =
        task {
            let now = clock.utcNow ()
            let unclassifiedDir = Path.Combine(config.ArchiveDir, "unclassified")

            let mutable downloaded = 0
            let mutable duplicates = 0
            let mutable processed = 0
            let errors = ResizeArray<string>()

            try
                let! lastSync = loadSyncState db account
                let sinceStr = lastSync |> Option.map (fun d -> d.ToString("o")) |> Option.defaultValue "beginning"
                logger.info $"[{account}] Syncing since {sinceStr}"

                let! messages = provider.listNewMessages lastSync
                logger.info $"[{account}] Found {messages.Length} messages"

                for msg in messages do
                    try
                        let! alreadyProcessed = messageExists db account msg.ProviderId

                        if not alreadyProcessed then
                            // Record message first (documents table has FK to messages)
                            do! recordMessage db account msg now
                            processed <- processed + 1

                            // Fetch and store body text if not already present
                            if msg.BodyText.IsNone then
                                try
                                    let! body = provider.getMessageBody msg.ProviderId
                                    match body with
                                    | Some rawBody ->
                                        let cleanBody = stripHtml rawBody
                                        if not (String.IsNullOrWhiteSpace(cleanBody)) then
                                            let! _ =
                                                db.execNonQuery
                                                    "UPDATE messages SET body_text = @body WHERE account = @acc AND gmail_id = @gid"
                                                    [ ("@body", boxVal cleanBody)
                                                      ("@acc", boxVal account)
                                                      ("@gid", boxVal msg.ProviderId) ]
                                            logger.debug $"[{account}] Stored body text for {msg.ProviderId}"
                                    | None -> ()
                                with ex ->
                                    logger.debug $"[{account}] Could not fetch body for {msg.ProviderId}: {ex.Message}"

                            let! attachments = provider.getAttachments msg.ProviderId

                            let validAttachments =
                                attachments
                                |> List.filter (fun a -> a.SizeBytes >= int64 config.MinAttachmentSize)

                            if validAttachments.Length > 0 then
                                for att in validAttachments do
                                    let sha = computeSha256 att.Content
                                    let! isDuplicate = hashExists db sha

                                    if isDuplicate then
                                        let shaPrefix = sha.[..7]
                                        logger.debug $"[{account}] Skipping duplicate: {att.FileName} (SHA256: {shaPrefix}...)"
                                        duplicates <- duplicates + 1
                                    else
                                        let standardName = buildStandardName msg.Date msg.Sender att.FileName
                                        let savePath = Path.Combine(unclassifiedDir, standardName)

                                        fs.createDirectory unclassifiedDir
                                        do! fs.writeAllBytes savePath att.Content

                                        let sidecar = buildSidecar account msg att standardName sha now
                                        let sidecarJson = serialiseSidecar sidecar
                                        let sidecarPath = savePath + ".meta.json"
                                        do! fs.writeAllText sidecarPath sidecarJson

                                        do! recordDocument db account msg att standardName sha now
                                        downloaded <- downloaded + 1
                                        logger.info $"[{account}] Downloaded: {standardName}"

                    with ex ->
                        let errMsg = $"[{account}] Error processing message {msg.ProviderId}: {ex.Message}"
                        logger.error errMsg
                        errors.Add(errMsg)

                do! saveSyncState db account processed now

            with ex ->
                let errMsg = $"[{account}] Sync failed: {ex.Message}"
                logger.error errMsg
                errors.Add(errMsg)

            return
                { Account = account
                  MessagesProcessed = processed
                  AttachmentsDownloaded = downloaded
                  DuplicatesSkipped = duplicates
                  Errors = errors |> Seq.toList }
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

    /// Run one batch of backfill for an account. Returns (newMessages, completed).
    let backfillAccount
        (fs: Algebra.FileSystem)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (clock: Algebra.Clock)
        (provider: Algebra.EmailProvider)
        (config: Domain.HermesConfig)
        (account: Domain.AccountConfig)
        : Task<int * bool> =
        task {
            let bf = account.Backfill
            if not bf.Enabled then return (0, true)
            else

            let! state = loadBackfillState db account.Label
            if state.Completed then return (0, true)
            else

            // Ensure sync_state row exists
            let! _ =
                db.execNonQuery
                    "INSERT OR IGNORE INTO sync_state (account) VALUES (@acc)"
                    [ ("@acc", boxVal account.Label) ]

            let now = clock.utcNow ()
            let startedAt = state.StartedAt |> Option.defaultValue now

            // Build query
            let query =
                let parts = ResizeArray<string>()
                if bf.AttachmentsOnly then parts.Add("has:attachment")
                match bf.Since with
                | Some since -> parts.Add($"after:{since.ToUnixTimeSeconds()}")
                | None -> ()
                if parts.Count = 0 then None else Some (parts |> String.concat " ")

            let tokenLabel = state.PageToken |> Option.defaultValue "start"
            logger.info $"[{account.Label}] Backfill page (scanned: {state.Scanned}, token: {tokenLabel})"

            let! page = provider.listMessagePage state.PageToken query bf.BatchSize

            let mutable newCount = 0
            for msg in page.Messages do
                let! exists = messageExists db account.Label msg.ProviderId
                if not exists then
                    do! recordMessage db account.Label msg (clock.utcNow ())
                    let! atts = provider.getAttachments msg.ProviderId
                    for att in atts do
                        try
                            if int64 att.SizeBytes >= int64 config.MinAttachmentSize then
                                let sha = computeSha256 att.Content
                                let! isDuplicate = hashExists db sha
                                if isDuplicate then
                                    logger.debug $"[{account.Label}] Backfill skipping duplicate: {att.FileName}"
                                else
                                    let name = buildStandardName msg.Date msg.Sender att.FileName
                                    let destPath = IO.Path.Combine(config.ArchiveDir, "unclassified", name)
                                    fs.createDirectory (IO.Path.Combine(config.ArchiveDir, "unclassified"))
                                    do! fs.writeAllBytes destPath att.Content
                                    let sidecar = buildSidecar account.Label msg att name sha (clock.utcNow ())
                                    do! fs.writeAllText (destPath + ".meta.json") (serialiseSidecar sidecar)
                                    do! recordDocument db account.Label msg att name sha (clock.utcNow ())
                                    newCount <- newCount + 1
                        with ex ->
                            logger.warn $"[{account.Label}] Backfill attachment error ({att.FileName}): {ex.Message}"

            let completed = page.NextPageToken.IsNone
            let newState =
                { PageToken = page.NextPageToken
                  Scanned = state.Scanned + page.Messages.Length
                  TotalEstimate = if page.ResultSizeEstimate > 0L then page.ResultSizeEstimate else state.TotalEstimate
                  Completed = completed
                  StartedAt = Some startedAt }

            do! saveBackfillState db account.Label newState
            logger.info $"[{account.Label}] Backfill batch: {newCount} new, {page.Messages.Length} scanned, completed={completed}"

            return (newCount, completed)
        }
