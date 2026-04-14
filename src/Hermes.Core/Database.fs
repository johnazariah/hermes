namespace Hermes.Core

open System.IO
open System.Threading.Tasks
open Microsoft.Data.Sqlite

/// SQLite database initialisation, schema creation, and queries.
/// Returns an Algebra.Database record — callers never touch SqliteConnection directly.
[<RequireQualifiedAccess>]
module Database =

    let [<Literal>] CurrentSchemaVersion = 6

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
            body_text       TEXT,
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
            category        TEXT NOT NULL,
            mime_type       TEXT,
            size_bytes      INTEGER,
            sha256          TEXT NOT NULL,
            source_path     TEXT,
            extracted_text  TEXT,
            extracted_markdown TEXT,
            extracted_date  TEXT,
            extracted_amount REAL,
            extracted_vendor TEXT,
            extracted_abn   TEXT,
            ocr_confidence  REAL,
            extraction_method TEXT,
            extraction_confidence REAL,
            classification_tier TEXT,
            classification_confidence REAL,
            comprehension   TEXT,
            comprehension_schema TEXT,
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
           "CREATE UNIQUE INDEX IF NOT EXISTS idx_tags_unique ON tags(document_id, tag);"
        |]

    let private ftsSql =
        [| """
        CREATE VIRTUAL TABLE IF NOT EXISTS documents_fts USING fts5(
            sender,
            subject,
            original_name,
            category,
            extracted_text,
            extracted_vendor,
            content='documents',
            content_rowid='id'
        );
        """

           """
        CREATE TRIGGER IF NOT EXISTS doc_fts_insert AFTER INSERT ON documents BEGIN
            INSERT INTO documents_fts(rowid, sender, subject, original_name, category, extracted_text, extracted_vendor)
            VALUES (new.id, new.sender, new.subject, new.original_name, new.category, new.extracted_text, new.extracted_vendor);
        END;
        """

           """
        CREATE TRIGGER IF NOT EXISTS doc_fts_update AFTER UPDATE ON documents BEGIN
            INSERT INTO documents_fts(documents_fts, rowid, sender, subject, original_name, category, extracted_text, extracted_vendor)
            VALUES ('delete', old.id, old.sender, old.subject, old.original_name, old.category, old.extracted_text, old.extracted_vendor);
            INSERT INTO documents_fts(rowid, sender, subject, original_name, category, extracted_text, extracted_vendor)
            VALUES (new.id, new.sender, new.subject, new.original_name, new.category, new.extracted_text, new.extracted_vendor);
        END;
        """

           // ── Messages FTS (email body search) ──────────────────────────
           """
        CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
            sender,
            subject,
            body_text,
            content='messages',
            content_rowid='rowid'
        );
        """

           """
        CREATE TRIGGER IF NOT EXISTS msg_fts_insert AFTER INSERT ON messages BEGIN
            INSERT INTO messages_fts(rowid, sender, subject, body_text)
            VALUES (new.rowid, new.sender, new.subject, new.body_text);
        END;
        """

           """
        CREATE TRIGGER IF NOT EXISTS msg_fts_update AFTER UPDATE ON messages BEGIN
            INSERT INTO messages_fts(messages_fts, rowid, sender, subject, body_text)
            VALUES ('delete', old.rowid, old.sender, old.subject, old.body_text);
            INSERT INTO messages_fts(rowid, sender, subject, body_text)
            VALUES (new.rowid, new.sender, new.subject, new.body_text);
        END;
        """ |]

    // ─── Low-level helpers ───────────────────────────────────────────

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
