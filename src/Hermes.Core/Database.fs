namespace Hermes.Core

open System
open System.IO
open System.Text
open System.Threading.Tasks
open Microsoft.Data.Sqlite

/// SQLite database initialisation, schema creation, and queries.
/// Returns an Algebra.Database record — callers never touch SqliteConnection directly.
[<RequireQualifiedAccess>]
module Database =

    let [<Literal>] CurrentSchemaVersion = 11

    // ─── Schema DDL ──────────────────────────────────────────────────

    let private coreSchemaSql =
        [| """
        CREATE TABLE IF NOT EXISTS schema_version (
            version     INTEGER PRIMARY KEY,
            applied_at  TEXT NOT NULL DEFAULT (datetime('now'))
        );
        """

           """
        CREATE TABLE IF NOT EXISTS messages (
            gmail_id        TEXT NOT NULL,
            account         TEXT NOT NULL,
            sender          TEXT,
            subject         TEXT,
            date            TEXT,
            thread_id       TEXT,
            folder_path     TEXT,
            label_ids       TEXT,
            has_attachments INTEGER NOT NULL DEFAULT 0,
            processed_at    TEXT NOT NULL DEFAULT (datetime('now')),
            PRIMARY KEY (account, gmail_id)
        );
        """

           "CREATE INDEX IF NOT EXISTS idx_msg_date    ON messages(date);"
           "CREATE INDEX IF NOT EXISTS idx_msg_sender  ON messages(sender);"
           "CREATE INDEX IF NOT EXISTS idx_msg_account ON messages(account);"

           """
        CREATE TABLE IF NOT EXISTS documents (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            stage           TEXT NOT NULL DEFAULT 'received',
            source_type     TEXT NOT NULL,
            gmail_id        TEXT,
            thread_id       TEXT,
            account         TEXT,
            sender          TEXT,
            subject         TEXT,
            email_date      TEXT,
            original_name   TEXT,
            saved_path      TEXT NOT NULL,
            folder_path     TEXT,
            category        TEXT NOT NULL,
            mime_type       TEXT,
            size_bytes      INTEGER,
            sha256          TEXT NOT NULL,
            source_path     TEXT,
            extracted_date  TEXT,
            extracted_amount REAL,
            extracted_vendor TEXT,
            extracted_abn   TEXT,
            ocr_confidence  REAL,
            extraction_method TEXT,
            extraction_confidence REAL,
            classification_tier TEXT,
            classification_confidence REAL,
            extracted_at    TEXT,
            embedded_at     TEXT,
            chunk_count     INTEGER,
            starred         INTEGER NOT NULL DEFAULT 0,
            ingested_at     TEXT NOT NULL DEFAULT (datetime('now')),
            FOREIGN KEY (account, gmail_id) REFERENCES messages(account, gmail_id)
        );
        """

           "CREATE INDEX IF NOT EXISTS idx_doc_category   ON documents(category);"
           "CREATE INDEX IF NOT EXISTS idx_doc_date       ON documents(email_date);"
           "CREATE INDEX IF NOT EXISTS idx_doc_sender     ON documents(sender);"
           "CREATE INDEX IF NOT EXISTS idx_doc_sha256     ON documents(sha256);"
           "CREATE INDEX IF NOT EXISTS idx_doc_account    ON documents(account);"
           "CREATE INDEX IF NOT EXISTS idx_doc_thread     ON documents(thread_id, account);"
           "CREATE INDEX IF NOT EXISTS idx_doc_source     ON documents(source_type);"
           "CREATE INDEX IF NOT EXISTS idx_doc_extracted  ON documents(extracted_at);"
           "CREATE INDEX IF NOT EXISTS idx_doc_embedded   ON documents(embedded_at);"
           "CREATE INDEX IF NOT EXISTS idx_doc_stage     ON documents(stage);"

           """
        CREATE TABLE IF NOT EXISTS documents_change_epoch (
            singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
            epoch     INTEGER NOT NULL CHECK (epoch >= 0)
        );
        """

           """
        INSERT OR IGNORE INTO documents_change_epoch (singleton, epoch)
        VALUES (1, 0);
        """

           """
        CREATE TRIGGER IF NOT EXISTS documents_epoch_insert
        AFTER INSERT ON documents
        BEGIN
            UPDATE documents_change_epoch
            SET epoch = epoch + 1
            WHERE singleton = 1;
        END;
        """

           """
        CREATE TRIGGER IF NOT EXISTS documents_epoch_update
        AFTER UPDATE ON documents
        BEGIN
            UPDATE documents_change_epoch
            SET epoch = epoch + 1
            WHERE singleton = 1;
        END;
        """

           """
        CREATE TRIGGER IF NOT EXISTS documents_epoch_delete
        AFTER DELETE ON documents
        BEGIN
            UPDATE documents_change_epoch
            SET epoch = epoch + 1
            WHERE singleton = 1;
        END;
        """

           """
        CREATE TABLE IF NOT EXISTS sync_state (
            account             TEXT PRIMARY KEY,
            last_history_id     TEXT,
            last_sync_at        TEXT,
            message_count       INTEGER NOT NULL DEFAULT 0,
            backfill_page_token TEXT,
            backfill_total_estimate INTEGER,
            backfill_scanned    INTEGER NOT NULL DEFAULT 0,
            backfill_completed  INTEGER NOT NULL DEFAULT 0,
            backfill_started_at TEXT
        );
        """

           """
        CREATE TABLE IF NOT EXISTS reminders (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            document_id     INTEGER REFERENCES documents(id),
            vendor          TEXT,
            amount          REAL,
            due_date        TEXT,
            category        TEXT NOT NULL,
            status          TEXT NOT NULL DEFAULT 'active',
            snoozed_until   TEXT,
            created_at      TEXT NOT NULL DEFAULT (datetime('now')),
            completed_at    TEXT,
            dismissed_at    TEXT,
            trigger_name    TEXT,
            notes           TEXT
        );
        """

           "CREATE INDEX IF NOT EXISTS idx_reminder_status ON reminders(status);"
           "CREATE INDEX IF NOT EXISTS idx_reminder_due ON reminders(due_date);"
           "CREATE INDEX IF NOT EXISTS idx_reminder_doc ON reminders(document_id);"

           // ── Activity log ─────────────────────────────────────────────────
           """
        CREATE TABLE IF NOT EXISTS activity_log (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp   TEXT NOT NULL DEFAULT (datetime('now')),
            level       TEXT NOT NULL DEFAULT 'info',
            category    TEXT NOT NULL,
            message     TEXT NOT NULL,
            document_id INTEGER,
            details     TEXT
        );
        """
           "CREATE INDEX IF NOT EXISTS idx_activity_log_ts ON activity_log(timestamp DESC);"

           // ── Dead letters ─────────────────────────────────────────────
           """
        CREATE TABLE IF NOT EXISTS dead_letters (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            doc_id        INTEGER NOT NULL,
            stage         TEXT NOT NULL,
            error         TEXT NOT NULL,
            retryable     INTEGER NOT NULL DEFAULT 0,
            failed_at     TEXT NOT NULL,
            retry_count   INTEGER NOT NULL DEFAULT 0,
            original_name TEXT,
            dismissed     INTEGER NOT NULL DEFAULT 0
        );
        """

           // ── Tags ─────────────────────────────────────────────────────
           """
        CREATE TABLE IF NOT EXISTS tags (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            document_id INTEGER NOT NULL REFERENCES documents(id),
            tag         TEXT NOT NULL,
            source      TEXT NOT NULL DEFAULT 'user',
            confidence  REAL,
            created_at  TEXT NOT NULL DEFAULT (datetime('now')),
            created_by  TEXT
        );
        """
           "CREATE INDEX IF NOT EXISTS idx_tags_doc ON tags(document_id);"
           "CREATE INDEX IF NOT EXISTS idx_tags_tag ON tags(tag);"
           "DROP INDEX IF EXISTS idx_tags_unique;"
           "CREATE UNIQUE INDEX idx_tags_unique ON tags(document_id, tag, COALESCE(created_by, ''));"

           // Keep the compatibility category and its provenance tag in the
           // same SQLite statement as a documents metadata update.
           "DROP TRIGGER IF EXISTS doc_reclassification_tag;"
           """
        CREATE TRIGGER doc_reclassification_tag
        AFTER UPDATE OF category, classification_tier, classification_confidence ON documents
        WHEN new.classification_tier IN ('manual', 'content')
        BEGIN
            DELETE FROM tags
            WHERE document_id = new.id
              AND created_by = 'reclassification'
              AND tag <> new.category;

            INSERT INTO tags (document_id, tag, source, confidence, created_by)
            VALUES (
                new.id,
                new.category,
                CASE new.classification_tier WHEN 'manual' THEN 'user' ELSE 'classifier' END,
                new.classification_confidence,
                'reclassification')
            ON CONFLICT(document_id, tag, COALESCE(created_by, '')) DO UPDATE SET
                source = excluded.source,
                confidence = excluded.confidence,
                created_by = excluded.created_by;
        END;
        """

           // ── Contacts (address book) ──────────────────────────────────
           """
        CREATE TABLE IF NOT EXISTS contacts (
            id              TEXT PRIMARY KEY,
            name            TEXT NOT NULL,
            canonical_name  TEXT NOT NULL,
            email           TEXT,
            abn             TEXT,
            phone           TEXT,
            address         TEXT,
            contact_type    TEXT NOT NULL DEFAULT 'unknown',
            tax_relevant    INTEGER,
            source_sender   TEXT,
            first_seen_at   TEXT NOT NULL DEFAULT (datetime('now')),
            last_seen_at    TEXT NOT NULL DEFAULT (datetime('now')),
            metadata        TEXT
        );
        """
           "CREATE UNIQUE INDEX IF NOT EXISTS idx_contacts_abn ON contacts(abn) WHERE abn IS NOT NULL;"
           "CREATE INDEX IF NOT EXISTS idx_contacts_type ON contacts(contact_type);"
           "CREATE INDEX IF NOT EXISTS idx_contacts_canonical ON contacts(canonical_name);"

           """
        CREATE TABLE IF NOT EXISTS document_contacts (
            document_id INTEGER NOT NULL REFERENCES documents(id),
            contact_id  TEXT NOT NULL REFERENCES contacts(id),
            role        TEXT NOT NULL DEFAULT 'issuer',
            confidence  REAL DEFAULT 1.0,
            PRIMARY KEY (document_id, contact_id, role)
        );
        """
           "CREATE INDEX IF NOT EXISTS idx_doc_contacts_contact ON document_contacts(contact_id);"

           // ── Corrections (user feedback on comprehension) ──────────────
           """
        CREATE TABLE IF NOT EXISTS corrections (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            document_id     INTEGER NOT NULL REFERENCES documents(id),
            field           TEXT NOT NULL,
            original_value  TEXT,
            corrected_value TEXT NOT NULL,
            note            TEXT,
            created_at      TEXT NOT NULL DEFAULT (datetime('now'))
        );
        """
           "CREATE INDEX IF NOT EXISTS idx_corrections_doc ON corrections(document_id);"

           // ── Learned patterns (RAC knowledge accumulation) ────────────
           """
        CREATE TABLE IF NOT EXISTS learned_patterns (
            sender_domain   TEXT NOT NULL,
            document_type   TEXT NOT NULL,
            count           INTEGER NOT NULL DEFAULT 1,
            avg_confidence  REAL NOT NULL DEFAULT 0.0,
            last_seen       TEXT NOT NULL DEFAULT (datetime('now')),
            PRIMARY KEY (sender_domain, document_type)
        );
        """

           // ── Suggestions (low-confidence review queue) ────────────────
           """
        CREATE TABLE IF NOT EXISTS suggestions (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            document_id     INTEGER NOT NULL REFERENCES documents(id),
            proposed_category TEXT NOT NULL,
            current_category  TEXT,
            confidence      REAL NOT NULL,
            status          TEXT NOT NULL DEFAULT 'pending',
            created_at      TEXT NOT NULL DEFAULT (datetime('now')),
            resolved_at     TEXT
        );
        """
           "CREATE INDEX IF NOT EXISTS idx_suggestions_status ON suggestions(status);"
           "CREATE INDEX IF NOT EXISTS idx_suggestions_doc ON suggestions(document_id);"
        |]

    let private ftsSql =
        [| """
        CREATE VIRTUAL TABLE IF NOT EXISTS documents_fts USING fts5(
            sender,
            subject,
            original_name,
            category,
            extracted_vendor,
            content='documents',
            content_rowid='id'
        );
        """

           """
        CREATE TRIGGER IF NOT EXISTS doc_fts_insert AFTER INSERT ON documents BEGIN
            INSERT INTO documents_fts(rowid, sender, subject, original_name, category, extracted_vendor)
            VALUES (new.id, new.sender, new.subject, new.original_name, new.category, new.extracted_vendor);
        END;
        """

           """
        CREATE TRIGGER IF NOT EXISTS doc_fts_update AFTER UPDATE ON documents BEGIN
            INSERT INTO documents_fts(documents_fts, rowid, sender, subject, original_name, category, extracted_vendor)
            VALUES ('delete', old.id, old.sender, old.subject, old.original_name, old.category, old.extracted_vendor);
            INSERT INTO documents_fts(rowid, sender, subject, original_name, category, extracted_vendor)
            VALUES (new.id, new.sender, new.subject, new.original_name, new.category, new.extracted_vendor);
        END;
        """

           // ── Messages FTS (email search) ──────────────────────────────
           """
        CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
            sender,
            subject,
            content='messages',
            content_rowid='rowid'
        );
        """

           """
        CREATE TRIGGER IF NOT EXISTS msg_fts_insert AFTER INSERT ON messages BEGIN
            INSERT INTO messages_fts(rowid, sender, subject)
            VALUES (new.rowid, new.sender, new.subject);
        END;
        """

           """
        CREATE TRIGGER IF NOT EXISTS msg_fts_update AFTER UPDATE ON messages BEGIN
            INSERT INTO messages_fts(messages_fts, rowid, sender, subject)
            VALUES ('delete', old.rowid, old.sender, old.subject);
            INSERT INTO messages_fts(rowid, sender, subject)
            VALUES (new.rowid, new.sender, new.subject);
        END;
        """ |]

    // ─── Low-level helpers ───────────────────────────────────────────

    type CanonicalArchivePath =
        { FullPath: string
          OwnershipKey: string }

    let private canonicalize (path: string) : string =
        path
        |> Path.GetFullPath
        |> Path.TrimEndingDirectorySeparator
        |> fun value -> value.Normalize(NormalizationForm.FormC)

    let private resolveUnresolved
        (archiveDirectory: string)
        (savedPath: string)
        : string =
        let separator = Path.DirectorySeparatorChar
        let normalizedSeparators =
            savedPath.Replace('\\', separator).Replace('/', separator)

        if Path.IsPathRooted normalizedSeparators then
            normalizedSeparators
        else
            Path.Combine(archiveDirectory, normalizedSeparators)

    /// True when `candidateKey` (already canonicalized: full path, trailing
    /// separator trimmed, FormC-normalized, upper-invariant) is the archive
    /// root itself, or a descendant of it.
    let private isWithinArchive (rootKey: string) (candidateKey: string) : bool =
        let separator = string Path.DirectorySeparatorChar

        candidateKey = rootKey
        || candidateKey.StartsWith(rootKey + separator, StringComparison.Ordinal)

    /// Resolve an archive path using the conservative ownership contract used
    /// by both filesystem validation and transactional database ownership.
    /// Invariant case folding intentionally rejects case-distinct paths rather
    /// than risking aliases on supported Windows/macOS filesystems. Rooted
    /// paths and `..` segments are both resolved and then required to stay
    /// inside the archive directory — callers (repair candidates, replayed
    /// cursors) must never be able to address a path outside it.
    let canonicalArchivePath
        (archiveDirectory: string)
        (savedPath: string)
        : Result<CanonicalArchivePath, string> =
        if String.IsNullOrWhiteSpace archiveDirectory then
            Error "Archive directory must not be empty"
        elif String.IsNullOrWhiteSpace savedPath then
            Error "Saved path must not be empty"
        else
            try
                let rootKey =
                    archiveDirectory |> canonicalize |> fun value -> value.ToUpperInvariant()

                let fullPath = resolveUnresolved archiveDirectory savedPath |> canonicalize
                let ownershipKey = fullPath.ToUpperInvariant()

                if isWithinArchive rootKey ownershipKey then
                    Ok { FullPath = fullPath; OwnershipKey = ownershipKey }
                else
                    Error "Saved path escapes the archive directory"
            with
            | :? ArgumentException as ex -> Error ex.Message
            | :? NotSupportedException as ex -> Error ex.Message
            | :? PathTooLongException as ex -> Error ex.Message

    let boxVal (x: 'a) : obj = x :> obj

    let private addParams (cmd: SqliteCommand) (ps: (string * obj) list) =
        for (name, value) in ps do
            let p = cmd.CreateParameter()
            p.ParameterName <- name
            p.Value <- value
            cmd.Parameters.Add(p) |> ignore

    let private execNonQuery (conn: SqliteConnection) (sql: string) (ps: (string * obj) list) =
        task {
            use cmd = conn.CreateCommand()
            cmd.CommandText <- sql
            addParams cmd ps
            return! cmd.ExecuteNonQueryAsync()
        }

    let private execScalar (conn: SqliteConnection) (sql: string) (ps: (string * obj) list) =
        task {
            use cmd = conn.CreateCommand()
            cmd.CommandText <- sql
            addParams cmd ps
            let! result = cmd.ExecuteScalarAsync()
            return result
        }

    let private execReader (conn: SqliteConnection) (sql: string) (ps: (string * obj) list) =
        task {
            use cmd = conn.CreateCommand()
            cmd.CommandText <- sql
            addParams cmd ps
            use! reader = cmd.ExecuteReaderAsync()
            let results = ResizeArray<Map<string, obj>>()
            let! firstRow = reader.ReadAsync()
            let mutable hasMore = firstRow

            while hasMore do
                let mutable row = Map.empty<string, obj>

                for i in 0 .. reader.FieldCount - 1 do
                    let name = reader.GetName(i)
                    let rawValue : obj | null = reader.GetValue(i)

                    let value =
                        match rawValue with
                        | null -> boxVal System.DBNull.Value
                        | v -> v

                    row <- row |> Map.add name value

                results.Add(row)
                let! nextRow = reader.ReadAsync()
                hasMore <- nextRow

            return results |> Seq.toList
        }

    type private PathOwner =
        { DocumentId: int64
          SavedPath: string }

    let private canonicalOwner
        archiveDirectory
        candidateKey
        documentId
        savedPath =
        canonicalArchivePath archiveDirectory savedPath
        |> Result.mapError (fun error ->
            $"Document {documentId} has an invalid saved_path: {error}")
        |> Result.map (fun canonical ->
            if canonical.OwnershipKey = candidateKey then
                Some
                    { DocumentId = documentId
                      SavedPath = savedPath }
            else
                None)

    let private readCanonicalOwners
        (conn: SqliteConnection)
        (transaction: SqliteTransaction)
        archiveDirectory
        candidateKey =
        task {
            use command = conn.CreateCommand()
            command.Transaction <- transaction
            command.CommandText <- "SELECT id, saved_path FROM documents ORDER BY id"
            use! reader = command.ExecuteReaderAsync()

            let rec loop owners =
                task {
                    let! hasRow = reader.ReadAsync()

                    if not hasRow then
                        return Ok(List.rev owners)
                    else
                        let documentId = reader.GetInt64(0)
                        let savedPath = reader.GetString(1)

                        match
                            canonicalOwner
                                archiveDirectory
                                candidateKey
                                documentId
                                savedPath
                        with
                        | Error error -> return Error error
                        | Ok None -> return! loop owners
                        | Ok(Some owner) -> return! loop (owner :: owners)
                }

            return! loop []
        }

    let private ownershipDecision documentId owners =
        let otherOwnerIds =
            owners
            |> List.choose (fun owner ->
                if owner.DocumentId = documentId then None
                else Some owner.DocumentId)
            |> List.distinct
            |> List.sort

        if not otherOwnerIds.IsEmpty then
            Some(Algebra.SavedPathOwnedByOtherDocuments otherOwnerIds)
        else
            owners
            |> List.tryFind (fun owner -> owner.DocumentId = documentId)
            |> Option.map (fun owner ->
                Algebra.SavedPathAlreadyOwnedByDocument owner.SavedPath)

    let private updateUnownedSavedPath
        (conn: SqliteConnection)
        (transaction: SqliteTransaction)
        (request: Algebra.SavedPathRepairRequest) =
        task {
            use command = conn.CreateCommand()
            command.Transaction <- transaction
            command.CommandText <-
                """UPDATE documents
                   SET saved_path = @candidate
                   WHERE id = @id
                     AND saved_path = @current
                     AND lower(sha256) = lower(@sha256)"""

            addParams command
                [ "@candidate", boxVal request.CandidateSavedPath
                  "@id", boxVal request.DocumentId
                  "@current", boxVal request.CurrentSavedPath
                  "@sha256", boxVal request.ExpectedSha256 ]

            let! affected = command.ExecuteNonQueryAsync()

            return
                if affected = 1 then Algebra.SavedPathUpdated
                else Algebra.SavedPathDocumentChanged
        }

    let private executeSavedPathRepair
        (conn: SqliteConnection)
        (request: Algebra.SavedPathRepairRequest) =
        task {
            use transaction =
                conn.BeginTransaction(deferred = false)

            match
                canonicalArchivePath
                    request.ArchiveDirectory
                    request.CandidateSavedPath
            with
            | Error error ->
                return Error $"Invalid candidate saved_path: {error}"
            | Ok candidate ->
                let! owners =
                    readCanonicalOwners
                        conn
                        transaction
                        request.ArchiveDirectory
                        candidate.OwnershipKey

                match owners with
                | Error error ->
                    return Error error
                | Ok values ->
                    let! decision =
                        match ownershipDecision request.DocumentId values with
                        | Some value -> Task.FromResult value
                        | None ->
                            updateUnownedSavedPath conn transaction request

                    transaction.Commit()
                    return Ok decision
        }

    let private tryRepairSavedPath conn request =
        task {
            try
                return! executeSavedPathRepair conn request
            with
            | :? SqliteException as ex -> return Error ex.Message
        }

    let private toInt64 (value: obj | null) : int64 =
        match value with
        | null -> 0L
        | v -> v :?> int64

    let private tableExistsImpl (conn: SqliteConnection) (name: string) =
        task {
            let! result =
                execScalar conn "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n" [ ("@n", boxVal name) ]

            return (toInt64 result) > 0L
        }

    let private schemaVersionImpl (conn: SqliteConnection) =
        task {
            let! exists = tableExistsImpl conn "schema_version"

            if not exists then
                return 0
            else
                let! result = execScalar conn "SELECT COALESCE(MAX(version), 0) FROM schema_version" []
                return (toInt64 result) |> int
        }

    // ─── Migrations ─────────────────────────────────────────────────

    /// No-op: all tables and columns are in coreSchemaSql.
    /// Schema version 5 = clean slate, no migration path needed.
    let private runMigrations (_conn: SqliteConnection) =
        task { () }

    let private initSchemaImpl (conn: SqliteConnection) =
        task {
            try
                for sql in coreSchemaSql do
                    let! _ = execNonQuery conn sql []
                    ()

                for sql in ftsSql do
                    let! _ = execNonQuery conn sql []
                    ()

                // Run migrations for existing databases
                do! runMigrations conn

                // Record current schema version if not already present
                let! count =
                    execScalar
                        conn
                        "SELECT COUNT(*) FROM schema_version WHERE version = @v"
                        [ ("@v", boxVal CurrentSchemaVersion) ]

                if (toInt64 count) = 0L then
                    let! _ =
                        execNonQuery
                            conn
                            "INSERT INTO schema_version (version) VALUES (@v)"
                            [ ("@v", boxVal CurrentSchemaVersion) ]

                    ()

                return Ok()
            with ex ->
                return Error $"Schema init failed: {ex.Message}"
        }

    // ─── Connection management ───────────────────────────────────────

    /// Open a connection with WAL mode and foreign keys enabled.
    let openConnection (dbPath: string) =
        let conn = new SqliteConnection($"Data Source={dbPath}")
        conn.Open()

        use pragma = conn.CreateCommand()
        pragma.CommandText <- "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;"
        pragma.ExecuteNonQuery() |> ignore

        conn

    // ─── Build the Database algebra from a connection ────────────────

    /// Create a Database algebra record backed by the given SqliteConnection.
    let fromConnection (conn: SqliteConnection) : Algebra.Database =
        { execNonQuery = fun sql ps -> execNonQuery conn sql ps
          execScalar = fun sql ps -> execScalar conn sql ps
          execReader = fun sql ps -> execReader conn sql ps
          initSchema = fun () -> initSchemaImpl conn
          tableExists = fun name -> tableExistsImpl conn name
          tryRepairSavedPath = tryRepairSavedPath conn
          schemaVersion = fun () -> schemaVersionImpl conn
          dispose = fun () -> conn.Dispose() }

    /// Create a Database algebra from a file path. Opens connection + enables WAL.
    let fromPath (dbPath: string) : Algebra.Database =
        let dir = Path.GetDirectoryName(dbPath) |> Option.ofObj

        match dir with
        | Some d when not (System.String.IsNullOrEmpty(d)) ->
            Directory.CreateDirectory(d) |> ignore
        | _ -> ()

        let conn = openConnection dbPath
        fromConnection conn

    // ─── Archive initialisation ──────────────────────────────────────

    /// Standard category directories created at init.
    let archiveCategories =
        [ "unclassified"
          "bank-statements"
          "insurance"
          "invoices"
          "legal"
          "medical"
          "donations"
          "payslips"
          "property"
          "rates-and-tax"
          "receipts"
          "subscriptions"
          "tax"
          "utilities"
          "unsorted" ]

    /// Initialise the archive directory structure + database, using the FileSystem algebra.
    let initArchive (fs: Algebra.FileSystem) (archiveDir: string) : Task<Result<Algebra.Database, string>> =
        task {
            try
                fs.createDirectory archiveDir

                for cat in archiveCategories do
                    fs.createDirectory (Path.Combine(archiveDir, cat))

                let dbPath = Path.Combine(archiveDir, "db.sqlite")
                let db = fromPath dbPath
                let! schemaResult = db.initSchema ()

                match schemaResult with
                | Ok() -> return Ok db
                | Error e -> return Error e
            with ex ->
                return Error $"Failed to initialize archive: {ex.Message}"
        }
