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
                    """INSERT OR IGNORE INTO messages (gmail_id, account, sender, subject, date, label_ids, has_attachments, processed_at)
                       VALUES (@gid, @acc, @sender, @subject, @date, @labels, @hasAtt, @processed)"""
                    [ ("@gid", boxVal msg.ProviderId)
                      ("@acc", boxVal account)
                      ("@sender", msg.Sender |> Option.map boxVal |> Option.defaultValue (boxVal DBNull.Value))
                      ("@subject", msg.Subject |> Option.map boxVal |> Option.defaultValue (boxVal DBNull.Value))
                      ("@date",
                       msg.Date
                       |> Option.map (fun d -> boxVal (d.ToString("o")))
                       |> Option.defaultValue (boxVal DBNull.Value))
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
