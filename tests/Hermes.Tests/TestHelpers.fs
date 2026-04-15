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

/// Create a fresh in-memory DB with schema initialised.
let createDb () : Algebra.Database =
    let conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:")
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;"
    cmd.ExecuteNonQuery() |> ignore
    let db = Database.fromConnection conn
    db.initSchema () |> Async.AwaitTask |> Async.RunSynchronously |> ignore
    db

/// Create a raw in-memory DB WITHOUT schema (for testing schema init itself).
let createRawDb () : Algebra.Database =
    let conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:")
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;"
    cmd.ExecuteNonQuery() |> ignore
    Database.fromConnection conn

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
            Backfill = { Domain.BackfillConfig.Enabled = true; Since = None; BatchSize = 50; AttachmentsOnly = true; IncludeBodies = false } } ]
      SyncIntervalMinutes = 15
      MinAttachmentSize = 20480
      WatchFolders = []
      Ollama =
        { Domain.OllamaConfig.Enabled = false; BaseUrl = "http://localhost:11434"
          EmbeddingModel = "nomic-embed-text"; VisionModel = "llava"; InstructModel = "llama3.2"
          SharedGpu = true; MaxHoldSeconds = 180 }
      Fallback = { Domain.FallbackConfig.Embedding = "onnx"; Ocr = "none" }
      Azure = { Domain.AzureConfig.DocumentIntelligenceEndpoint = ""; DocumentIntelligenceKey = "" }
      Chat =
        { Domain.ChatConfig.Provider = Domain.ChatProviderKind.Ollama
          AzureOpenAI =
            { Domain.AzureOpenAIConfig.Endpoint = ""; ApiKey = ""; DeploymentName = "gpt-4o"
              MaxTokens = 4096; TimeoutSeconds = 300 } }
      Pipeline = { Domain.PipelineConfig.ExtractConcurrency = 1; LlmConcurrency = 1; EmailConcurrency = 5 }
      DeepExtraction = { Domain.DeepExtractionConfig.Provider = Domain.ChatProviderKind.Ollama; Model = "llama3:8b" } }

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
