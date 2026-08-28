namespace Hermes.Core

open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite

/// SQLite database initialisation, schema creation, and queries.
/// Returns an Algebra.Database record — callers never touch SqliteConnection directly.
[<RequireQualifiedAccess>]
module Database =

    let [<Literal>] CurrentSchemaVersion = 12

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
           """
        UPDATE dead_letters
        SET dismissed = 1
        WHERE dismissed = 0
          AND EXISTS (
              SELECT 1
              FROM dead_letters newer
              WHERE newer.doc_id = dead_letters.doc_id
                AND newer.stage = dead_letters.stage
                AND newer.dismissed = 0
                AND newer.id > dead_letters.id
          );
        """
           "CREATE UNIQUE INDEX IF NOT EXISTS idx_dead_letters_active ON dead_letters(doc_id, stage) WHERE dismissed = 0;"

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

           // ── Durable learned-pattern evidence (v11) ───────────────
           """
        CREATE TABLE IF NOT EXISTS learned_pattern_evidence (
            document_id     INTEGER NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
            stage_name      TEXT NOT NULL,
            generation      INTEGER NOT NULL,
            sender_domain   TEXT NOT NULL,
            document_type   TEXT NOT NULL,
            confidence      REAL NOT NULL,
            observed_at     TEXT NOT NULL DEFAULT (datetime('now')),
            PRIMARY KEY (document_id, stage_name, generation)
        );
        """
           "CREATE INDEX IF NOT EXISTS idx_learned_evidence_pattern ON learned_pattern_evidence(sender_domain, document_type);"

           // ── Durable canonical stage responses (v12) ─────────────
           """
        CREATE TABLE IF NOT EXISTS stage_publications (
            document_id       INTEGER NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
            stage_name        TEXT NOT NULL,
            generation        INTEGER NOT NULL,
            response_json     TEXT NOT NULL,
            current_category  TEXT,
            published_at      TEXT NOT NULL DEFAULT (datetime('now')),
            PRIMARY KEY (document_id, stage_name, generation)
        );
        """
           "CREATE INDEX IF NOT EXISTS idx_stage_publications_stage ON stage_publications(stage_name, generation);"

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

           // ── Reflow operations (v9) ─────────────────────────────────
           """
        CREATE TABLE IF NOT EXISTS reflow_operations (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            document_id     INTEGER NOT NULL REFERENCES documents(id),
            operation_kind  TEXT NOT NULL,
            requested_mode  TEXT NOT NULL CHECK (requested_mode IN ('dry_run', 'apply')),
            lifecycle       TEXT NOT NULL DEFAULT 'pending' CHECK (lifecycle IN ('pending', 'running', 'completed', 'failed')),
            dag_signature   TEXT,
            created_at      TEXT NOT NULL DEFAULT (datetime('now')),
            completed_at    TEXT,
            error           TEXT
        );
        """
           "CREATE UNIQUE INDEX IF NOT EXISTS idx_reflow_ops_active_apply ON reflow_operations(document_id, operation_kind) WHERE requested_mode = 'apply' AND lifecycle IN ('pending', 'running');"
           "CREATE INDEX IF NOT EXISTS idx_reflow_ops_doc ON reflow_operations(document_id);"
           "CREATE INDEX IF NOT EXISTS idx_reflow_ops_lifecycle ON reflow_operations(lifecycle);"

           """
        CREATE TABLE IF NOT EXISTS reflow_operation_stages (
            operation_id    INTEGER NOT NULL REFERENCES reflow_operations(id),
            stage_name      TEXT NOT NULL,
            outcome         TEXT NOT NULL CHECK (outcome IN ('current', 'pending', 'reran', 'failed', 'skipped')),
            started_at      TEXT,
            completed_at    TEXT,
            error           TEXT,
            PRIMARY KEY (operation_id, stage_name)
        );
        """
           "CREATE INDEX IF NOT EXISTS idx_reflow_stages_op ON reflow_operation_stages(operation_id);"

           // ── Per-document reflow generation (v10) ─────────────────
           """
        CREATE TABLE IF NOT EXISTS document_generations (
            document_id INTEGER PRIMARY KEY REFERENCES documents(id) ON DELETE CASCADE,
            generation  INTEGER NOT NULL DEFAULT 0,
            updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
        );
        """

           // ── Shared artifact folder revisions ─────────────────────
           """
        CREATE TABLE IF NOT EXISTS artifact_folder_revisions (
            folder_identity TEXT PRIMARY KEY,
            revision        INTEGER NOT NULL DEFAULT 0,
            updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
        );
        """
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

    let private readRows (reader: SqliteDataReader) =
        task {
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

    let private execReader (conn: SqliteConnection) (sql: string) (ps: (string * obj) list) =
        task {
            use cmd = conn.CreateCommand()
            cmd.CommandText <- sql
            addParams cmd ps
            use! reader = cmd.ExecuteReaderAsync()
            return! readRows reader
        }

    // ─── Transaction-bound query helpers ─────────────────────────────

    let private execNonQueryTx (conn: SqliteConnection) (tx: SqliteTransaction) (sql: string) (ps: (string * obj) list) =
        task {
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <- sql
            addParams cmd ps
            return! cmd.ExecuteNonQueryAsync()
        }

    let private execScalarTx (conn: SqliteConnection) (tx: SqliteTransaction) (sql: string) (ps: (string * obj) list) =
        task {
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <- sql
            addParams cmd ps
            return! cmd.ExecuteScalarAsync()
        }

    let private execReaderTx (conn: SqliteConnection) (tx: SqliteTransaction) (sql: string) (ps: (string * obj) list) =
        task {
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <- sql
            addParams cmd ps
            use! reader = cmd.ExecuteReaderAsync()
            return! readRows reader
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

    /// Versioned schema additions use idempotent DDL in coreSchemaSql.
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

    // ─── Transactions ──────────────────────────────────────────────────

    let private scopeFor (conn: SqliteConnection) (tx: SqliteTransaction) : Algebra.TransactionScope =
        { execNonQuery = execNonQueryTx conn tx
          execScalar = execScalarTx conn tx
          execReader = execReaderTx conn tx }

    let private settleTransaction (tx: SqliteTransaction) (result: Result<unit, string>) =
        match result with
        | Ok () ->
            tx.Commit()
            Ok()
        | Error e ->
            tx.Rollback()
            Error e

    /// BEGIN IMMEDIATE. Taking the write lock at BEGIN keeps a read-then-write
    /// transaction from failing with SQLITE_BUSY_SNAPSHOT when the second write
    /// connection commits between our first read and our first write.
    let private beginImmediate (conn: SqliteConnection) : SqliteTransaction =
        conn.BeginTransaction(deferred = false)

    let private inTransactionImpl
        (conn: SqliteConnection)
        (gate: SemaphoreSlim)
        (callback: Algebra.TransactionScope -> Task<Result<unit, string>>)
        =
        task {
            do! gate.WaitAsync()

            try
                use tx = beginImmediate conn

                try
                    let! result = callback (scopeFor conn tx)
                    return settleTransaction tx result
                with ex ->
                    tx.Rollback()
                    return Error $"Transaction failed: {ex.Message}"
            finally
                gate.Release() |> ignore
        }

    let private withConnection
        (gate: SemaphoreSlim)
        (operation: unit -> Task<'T>)
        : Task<'T> =
        task {
            do! gate.WaitAsync()
            try
                return! operation ()
            finally
                gate.Release() |> ignore
        }

    // ─── Connection management ───────────────────────────────────────

    /// WAL for reader/writer concurrency, foreign keys for referential
    /// integrity, and a busy timeout so a competing writer waits for the write
    /// lock instead of failing immediately.
    let private applyConnectionPragmas (conn: SqliteConnection) : unit =
        use pragma = conn.CreateCommand()
        pragma.CommandText <-
            "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 30000;"
        pragma.ExecuteNonQuery() |> ignore

    /// Open a connection with WAL mode, foreign keys, and a busy timeout.
    let openConnection (dbPath: string) =
        let conn = new SqliteConnection($"Data Source={dbPath}")
        conn.Open()
        applyConnectionPragmas conn
        conn

    // ─── Build the Database algebra from a connection ────────────────

    /// Create a Database algebra record backed by the given SqliteConnection.
    let fromConnection (conn: SqliteConnection) : Algebra.Database =
        applyConnectionPragmas conn
        let txGate = new SemaphoreSlim(1, 1)

        { execNonQuery =
            fun sql ps ->
                withConnection txGate (fun () -> execNonQuery conn sql ps)
          execScalar =
            fun sql ps ->
                withConnection txGate (fun () -> execScalar conn sql ps)
          execReader =
            fun sql ps ->
                withConnection txGate (fun () -> execReader conn sql ps)
          initSchema =
            fun () ->
                withConnection txGate (fun () -> initSchemaImpl conn)
          tableExists =
            fun name ->
                withConnection txGate (fun () -> tableExistsImpl conn name)
          schemaVersion =
            fun () ->
                withConnection txGate (fun () -> schemaVersionImpl conn)
          inTransaction = fun callback -> inTransactionImpl conn txGate callback
          dispose =
            fun () ->
                txGate.Dispose()
                conn.Dispose() }

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
