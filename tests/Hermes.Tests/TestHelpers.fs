/// Shared test infrastructure — mock algebras, DB factories, fixtures.
/// Every test file should use these instead of creating its own mocks.
module Hermes.Tests.TestHelpers

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Hermes.Core

// ─── In-memory file system ───────────────────────────────────────────

type MemFs =
    { Fs: Algebra.FileSystem
      Files: ConcurrentDictionary<string, string>
      Bytes: ConcurrentDictionary<string, byte array>
      Dirs: ConcurrentDictionary<string, bool>
      /// Normalize a path to forward slashes (matches internal storage).
      Norm: string -> string
      /// Store a file using normalized path (forward slashes).
      Put: string -> string -> unit
      /// Read a file using normalized path lookup.
      Get: string -> string option }

let memFs () : MemFs =
    let files = ConcurrentDictionary<string, string>()
    let bytes = ConcurrentDictionary<string, byte array>()
    let dirs = ConcurrentDictionary<string, bool>()
    let norm (path: string) = path.Replace('\\', '/')

    let fs : Algebra.FileSystem =
        { readAllText = fun path ->
            task {
                match files.TryGetValue(norm path) with
                | true, c -> return c
                | _ -> return failwith $"File not found: {path}"
            }
          writeAllText = fun path content -> task { files.[norm path] <- content }
          writeAllBytes = fun path b ->
            task {
                let key = norm path
                bytes.[key] <- b
                files.[key] <- Text.Encoding.UTF8.GetString(b)
            }
          readAllBytes = fun path ->
            task {
                let key = norm path
                match bytes.TryGetValue(key) with
                | true, b -> return b
                | _ ->
                    match files.TryGetValue(key) with
                    | true, c -> return Text.Encoding.UTF8.GetBytes(c)
                    | _ -> return failwith $"File not found: {path}"
            }
          fileExists = fun path -> let k = norm path in files.ContainsKey(k) || bytes.ContainsKey(k)
          directoryExists = fun path -> dirs.ContainsKey(norm path)
          createDirectory = fun path -> dirs.[norm path] <- true
          deleteFile = fun path ->
            let k = norm path
            files.TryRemove(k) |> ignore
            bytes.TryRemove(k) |> ignore
          moveFile = fun src dst ->
            let ns, nd = norm src, norm dst
            match files.TryRemove(ns) with
            | true, c -> files.[nd] <- c
            | _ -> ()
            match bytes.TryRemove(ns) with
            | true, b -> bytes.[nd] <- b
            | _ -> ()
          getFiles = fun dir _pattern ->
            let pfx = let d = norm dir in if d.EndsWith("/") then d else d + "/"
            files.Keys
            |> Seq.append bytes.Keys
            |> Seq.distinct
            |> Seq.filter (fun k ->
                k.StartsWith(pfx, StringComparison.OrdinalIgnoreCase)
                && not (k.Substring(pfx.Length).Contains("/")))
            |> Seq.toArray
          getDirectories = fun dir ->
            let pfx = let d = norm dir in if d.EndsWith("/") then d else d + "/"
            dirs.Keys
            |> Seq.filter (fun k ->
                k.StartsWith(pfx, StringComparison.OrdinalIgnoreCase)
                && k.Length > pfx.Length
                && not (k.Substring(pfx.Length).Contains("/")))
            |> Seq.toArray
          getFileSize = fun path ->
            let k = norm path
            match bytes.TryGetValue(k) with
            | true, b -> int64 b.Length
            | _ ->
                match files.TryGetValue(k) with
                | true, c -> int64 (Text.Encoding.UTF8.GetByteCount(c))
                | _ -> 0L }

    { Fs = fs; Files = files; Bytes = bytes; Dirs = dirs
      Norm = norm
      Put = fun path content -> files.[norm path] <- content
      Get = fun path -> match files.TryGetValue(norm path) with true, v -> Some v | _ -> None }

// ─── Silent logger ───────────────────────────────────────────────────

let silentLogger : Algebra.Logger =
    { info = ignore; warn = ignore; error = ignore; debug = ignore }

// ─── Fixed clock ─────────────────────────────────────────────────────

let fixedClock (dt: DateTimeOffset) : Algebra.Clock =
    { utcNow = fun () -> dt }

let defaultClock : Algebra.Clock =
    fixedClock (DateTimeOffset(2025, 3, 15, 10, 30, 0, TimeSpan.Zero))

// ─── Fake environment ────────────────────────────────────────────────

let fakeEnvironment (home: string) (config: string) (docs: string) : Algebra.Environment =
    { homeDirectory = fun () -> home
      configDirectory = fun () -> config
      documentsDirectory = fun () -> docs }

// ─── In-memory SQLite database ───────────────────────────────────────

let private openMemoryConnection () =
    let conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:")
    conn.Open()
    use pragma = conn.CreateCommand()
    pragma.CommandText <- "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;"
    pragma.ExecuteNonQuery() |> ignore
    conn

/// Create a fresh in-memory DB with schema initialised.
let createDb () : Algebra.Database =
    let db = Database.fromConnection (openMemoryConnection ())
    db.initSchema () |> Async.AwaitTask |> Async.RunSynchronously |> ignore
    db

/// Create a raw in-memory DB WITHOUT schema (for testing schema init itself).
let createRawDb () : Algebra.Database =
    Database.fromConnection (openMemoryConnection ())

// ─── Populated v8 database fixture ───────────────────────────────────

/// Row counts the v8 to current upgrade must land on. Every row is carried
/// across; only the state of duplicate active dead letters changes.
type V8Counts =
    { Documents: int64
      StageCompletions: int64
      /// Total rows - the collapse dismisses losers, it never deletes them.
      DeadLetters: int64
      /// One survivor per (doc_id, stage) group that was active at v8.
      ActiveDeadLetters: int64 }

/// A populated v8 database plus the facts its migration assertions rely on.
/// `initSchema` has NOT been run: the upgrade under test starts here.
type V8Fixture =
    { Db: Algebra.Database
      Counts: V8Counts
      /// Active dead letters seeded at v8, before the collapse runs.
      ActiveDeadLettersAtV8: int64
      /// `category|sha256|saved_path|extracted_vendor|starred` of document 1.
      DocumentFingerprint: string
      /// Newest error of the (doc 3, 'extract') group - the row dedup keeps.
      SurvivingExtractError: string }

/// Schema of an archive whose last upgrade was v8 - the only version ever
/// deployed, since 9, 10 and 11 were never merged. Only the tables whose
/// contents the upgrade must preserve are recreated here; every other v8
/// object is created by `initSchema`, exactly as on a live upgrade. v8
/// already has `dead_letters.dismissed` and no unique index over active
/// dead letters, so duplicate active rows are legal at this point.
let private v8Schema =
    [| "CREATE TABLE schema_version (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL DEFAULT (datetime('now')));"

       """
       CREATE TABLE messages (
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

       """
       CREATE TABLE documents (
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

       """
       CREATE TABLE dead_letters (
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
       CREATE TABLE stage_completions (
           document_id     INTEGER NOT NULL REFERENCES documents(id),
           stage_name      TEXT NOT NULL,
           completed_at    TEXT NOT NULL DEFAULT (datetime('now')),
           PRIMARY KEY (document_id, stage_name)
       );
       """

       """
       CREATE VIRTUAL TABLE documents_fts USING fts5(
           sender, subject, original_name, category, extracted_vendor,
           content='documents', content_rowid='id'
       );
       """

       """
       CREATE TRIGGER doc_fts_insert AFTER INSERT ON documents BEGIN
           INSERT INTO documents_fts(rowid, sender, subject, original_name, category, extracted_vendor)
           VALUES (new.id, new.sender, new.subject, new.original_name, new.category, new.extracted_vendor);
       END;
       """

       """
       CREATE TRIGGER doc_fts_update AFTER UPDATE ON documents BEGIN
           INSERT INTO documents_fts(documents_fts, rowid, sender, subject, original_name, category, extracted_vendor)
           VALUES ('delete', old.id, old.sender, old.subject, old.original_name, old.category, old.extracted_vendor);
           INSERT INTO documents_fts(rowid, sender, subject, original_name, category, extracted_vendor)
           VALUES (new.id, new.sender, new.subject, new.original_name, new.category, new.extracted_vendor);
       END;
       """ |]

/// Rows a live v8 archive carries into the upgrade: one fully processed
/// document, one part-processed, one still unsorted, the completion ledger,
/// and the duplicate active dead letters v8 allowed. Dead-letter ids are
/// explicit because the collapse must deterministically keep the highest id
/// of each (doc_id, stage) group.
let private v8Rows =
    [| """
       INSERT INTO messages (gmail_id, account, sender, subject, date, thread_id, folder_path, has_attachments)
       VALUES ('gmail-1', 'primary', 'billing@vendor.example', 'March invoice', '2025-03-01T09:00:00Z', 'thread-1', 'INBOX', 1),
              ('gmail-2', 'primary', 'statements@bank.example', 'February statement', '2025-02-01T09:00:00Z', 'thread-2', 'INBOX', 1);
       """

       """
       INSERT INTO documents
           (id, stage, source_type, gmail_id, account, sender, subject, email_date, original_name,
            saved_path, category, sha256, extracted_vendor, extracted_amount, extraction_method,
            extracted_at, embedded_at, chunk_count, starred)
       VALUES
           (1, 'embedded', 'email_attachment', 'gmail-1', 'primary', 'billing@vendor.example',
            'March invoice', '2025-03-01T09:00:00Z', 'invoice-8842.pdf',
            'invoices/2025/invoice-8842.pdf', 'invoices', 'sha-invoice-8842', 'Vendor Pty Ltd',
            412.5, 'pdf_text', '2025-03-01T09:05:00Z', '2025-03-01T09:06:00Z', 7, 1),
           (2, 'extracted', 'email_attachment', 'gmail-2', 'primary', 'statements@bank.example',
            'February statement', '2025-02-01T09:00:00Z', 'statement-feb.pdf',
            'bank-statements/2025/statement-feb.pdf', 'bank-statements', 'sha-statement-feb',
            'Bank Example', NULL, 'pdf_text', '2025-02-01T09:05:00Z', NULL, NULL, 0),
           (3, 'received', 'manual_drop', NULL, NULL, NULL, NULL, NULL, 'scan-0001.pdf',
            'unsorted/scan-0001.pdf', 'unsorted', 'sha-scan-0001', NULL, NULL, NULL, NULL,
            NULL, NULL, 0);
       """

       """
       INSERT INTO stage_completions (document_id, stage_name, completed_at)
       VALUES (1, 'extract', '2025-03-01T09:05:00Z'),
              (1, 'triage',  '2025-03-01T09:05:30Z'),
              (1, 'embed',   '2025-03-01T09:06:00Z'),
              (2, 'extract', '2025-02-01T09:05:00Z');
       """

       """
       INSERT INTO dead_letters
           (id, doc_id, stage, error, retryable, failed_at, retry_count, original_name, dismissed)
       VALUES (1, 2, 'embed',   'ollama timeout',               1, '2025-02-01T10:00:00Z', 1, 'statement-feb.pdf', 0),
              (2, 2, 'embed',   'ollama connection refused',    1, '2025-02-02T10:00:00Z', 2, 'statement-feb.pdf', 0),
              (3, 3, 'extract', 'ocr failed (attempt 1)',       1, '2025-02-03T10:00:00Z', 1, 'scan-0001.pdf',     0),
              (4, 3, 'extract', 'ocr failed (attempt 2)',       1, '2025-02-04T10:00:00Z', 2, 'scan-0001.pdf',     0),
              (5, 3, 'extract', 'ocr failed (attempt 3)',       0, '2025-02-05T10:00:00Z', 3, 'scan-0001.pdf',     0),
              (6, 3, 'embed',   'dismissed before the upgrade', 0, '2025-01-05T10:00:00Z', 1, 'scan-0001.pdf',     1),
              (7, 1, 'triage',  'transient llm error',          1, '2025-03-01T10:00:00Z', 1, 'invoice-8842.pdf',  0);
       """

       "INSERT INTO schema_version (version, applied_at) VALUES (8, '2025-01-01T00:00:00Z');" |]

let private execAll
    (conn: Microsoft.Data.Sqlite.SqliteConnection)
    (statements: string array)
    =
    for sql in statements do
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        cmd.ExecuteNonQuery() |> ignore

/// Create an in-memory database populated exactly as a v8 archive, ready for
/// the v8 to current upgrade to run against it.
let createV8Db () : V8Fixture =
    let conn = openMemoryConnection ()
    execAll conn v8Schema
    execAll conn v8Rows
    { Db = Database.fromConnection conn
      Counts =
        { Documents = 3L
          StageCompletions = 4L
          DeadLetters = 7L
          ActiveDeadLetters = 3L }
      ActiveDeadLettersAtV8 = 6L
      DocumentFingerprint =
        "invoices|sha-invoice-8842|invoices/2025/invoice-8842.pdf|Vendor Pty Ltd|1"
      SurvivingExtractError = "ocr failed (attempt 3)" }

let private v5ExtractionSchema = """
    CREATE TABLE IF NOT EXISTS extraction (
        document_id INTEGER PRIMARY KEY REFERENCES documents(id), extracted_date TEXT,
        extracted_amount REAL, extracted_vendor TEXT, extracted_abn TEXT, method TEXT,
        confidence REAL, extracted_at TEXT NOT NULL DEFAULT (datetime('now'))
    )"""

let private v5TriageSchema = """
    CREATE TABLE IF NOT EXISTS triage (
        document_id INTEGER PRIMARY KEY REFERENCES documents(id), document_type TEXT NOT NULL,
        category TEXT NOT NULL, confidence REAL NOT NULL,
        triaged_at TEXT NOT NULL DEFAULT (datetime('now'))
    )"""

let private v5ComprehensionSchema = """
    CREATE TABLE IF NOT EXISTS comprehension (
        document_id INTEGER PRIMARY KEY REFERENCES documents(id), document_type TEXT,
        category TEXT, confidence REAL, schema_version TEXT DEFAULT 'v2',
        comprehended_at TEXT NOT NULL DEFAULT (datetime('now'))
    )"""

let private v5EmbeddingSchema = """
    CREATE TABLE IF NOT EXISTS embedding (
        document_id INTEGER PRIMARY KEY REFERENCES documents(id), chunk_count INTEGER NOT NULL DEFAULT 0,
        embedded_at TEXT NOT NULL DEFAULT (datetime('now'))
    )"""

let private v5NoopProcess
    (_db: Algebra.Database)
    (_logger: Algebra.Logger)
    (_execution: PipelineV5.StageExecution)
    : Task<PipelineV5.StageOutcome> =
    task { return PipelineV5.Completed }

let standardV5Stages : PipelineV5.StageDefinition list =
    [ { Name = "extract"; DependsOn = []; OutputTable = "extraction"
        Schema = v5ExtractionSchema; Process = v5NoopProcess
        Gate = None; GpuModel = None; Mode = PipelineV5.Channel; Concurrency = 1 }
      { Name = "triage"; DependsOn = [ "extract" ]; OutputTable = "triage"
        Schema = v5TriageSchema; Process = v5NoopProcess
        Gate = None; GpuModel = None; Mode = PipelineV5.Channel; Concurrency = 1 }
      { Name = "deep-comprehend"; DependsOn = [ "extract"; "triage" ]; OutputTable = "comprehension"
        Schema = v5ComprehensionSchema; Process = v5NoopProcess
        Gate = None; GpuModel = None; Mode = PipelineV5.Channel; Concurrency = 1 }
      { Name = "embed"; DependsOn = [ "extract" ]; OutputTable = "embedding"
        Schema = v5EmbeddingSchema; Process = v5NoopProcess
        Gate = None; GpuModel = None; Mode = PipelineV5.Channel; Concurrency = 1 } ]

let standardV5Dag () : PipelineV5.Dag =
    match PipelineV5.buildDag standardV5Stages with
    | Ok dag -> dag
    | Error msg -> failwith $"Failed to build standard v5 DAG: {msg}"

let initV5 (db: Algebra.Database) : Task<unit> =
    task {
        do! Embeddings.initSchema db
        do! PipelineV5.initSchema db standardV5Stages
    }

// ─── Mock email provider ─────────────────────────────────────────────

let private emptyPage : Algebra.MessagePage =
    { Messages = []; NextPageToken = None; ResultSizeEstimate = 0L }

let private emptyStubPage : Algebra.StubPage =
    { Ids = []; NextPageToken = None; ResultSizeEstimate = 0L }

let emptyProvider : Algebra.EmailProvider =
    { listNewMessages = fun _ -> task { return [] }
      getAttachments = fun _ -> task { return [] }
      getMessageBody = fun _ -> task { return None }
      getFullMessage = fun _ -> task { return failwith "no messages" }
      listStubPage = fun _ _ _ -> Task.FromResult emptyStubPage
      listMessagePage = fun _ _ _ -> task { return emptyPage } }

let mockProvider
    (messages: Domain.EmailMessage list)
    (attachments: Map<string, Domain.EmailAttachment list>)
    : Algebra.EmailProvider =
    { listNewMessages = fun _ -> task { return messages }
      getAttachments = fun id -> task { return attachments |> Map.tryFind id |> Option.defaultValue [] }
      getMessageBody = fun _ -> task { return None }
      getFullMessage = fun id -> task { return messages |> List.find (fun m -> m.ProviderId = id) }
      listStubPage = fun _ _ _ -> Task.FromResult emptyStubPage
      listMessagePage = fun _ _ _ -> task { return emptyPage } }

// ─── Mock embedding client ───────────────────────────────────────────

let fakeEmbedder (dims: int) : Algebra.EmbeddingClient =
    { embed = fun text ->
        task {
            let hash = abs (text.GetHashCode())
            let vec = Array.init dims (fun i -> float32 (hash + i) / 1000.0f)
            return Ok vec
        }
      dimensions = dims
      isAvailable = fun () -> task { return true } }

let failingEmbedder : Algebra.EmbeddingClient =
    { embed = fun _ -> task { return Error "unavailable" }
      dimensions = 768
      isAvailable = fun () -> task { return false } }

// ─── Test chat providers ─────────────────────────────────────────────

/// A fake chat provider that returns a canned response.
let fakeChatProvider (response: string) : Algebra.ChatProvider =
    { complete = fun _sys _user -> task { return Ok response } }

/// A fake chat provider that always fails.
let failingChatProvider : Algebra.ChatProvider =
    { complete = fun _sys _user -> task { return Error "chat unavailable" } }

// ─── Test config ─────────────────────────────────────────────────────

let testConfig (archiveDir: string) : Domain.HermesConfig =
    { ArchiveDir = archiveDir
      Credentials = "/test/creds.json"
      Accounts =
        [ { Label = "test"; Provider = "gmail"
            Backfill = { Domain.BackfillConfig.Enabled = true; Since = None; BatchSize = 50; AttachmentsOnly = true; IncludeBodies = false }
            ClientId = ""; TenantId = "common"; RedirectPort = 53682 } ]
      SyncIntervalMinutes = 15
      MinAttachmentSize = 20480
      WatchFolders = []
      Ollama =
        { Domain.OllamaConfig.Enabled = false; BaseUrl = "http://localhost:11434"
          EmbeddingModel = "nomic-embed-text"; VisionModel = "llava"; InstructModel = "llama3.2"
          TriageModel = ""; SharedGpu = true; MaxHoldSeconds = 180 }
      Fallback = { Domain.FallbackConfig.Embedding = "onnx"; Ocr = "none" }
      Azure = { Domain.AzureConfig.DocumentIntelligenceEndpoint = ""; DocumentIntelligenceKey = "" }
      Chat =
        { Domain.ChatConfig.Provider = Domain.ChatProviderKind.Ollama
          AzureOpenAI =
            { Domain.AzureOpenAIConfig.Endpoint = ""; ApiKey = ""; DeploymentName = "gpt-4o"
              MaxTokens = 4096; TimeoutSeconds = 300 } }
      Pipeline = { Domain.PipelineConfig.ExtractConcurrency = 1; LlmConcurrency = 1; EmailConcurrency = 5 }
      DeepExtraction = { Domain.DeepExtractionConfig.Provider = Domain.ChatProviderKind.Ollama; Model = "llama3:8b" }
      Preferences = "" }

// ─── Default rules YAML ──────────────────────────────────────────────

let testRulesYaml = """
rules:
  - name: invoices-by-filename
    match:
      filename: "(?i)invoice"
    category: invoices
  - name: receipts-by-filename
    match:
      filename: "(?i)receipt"
    category: receipts
default_category: unsorted
"""
