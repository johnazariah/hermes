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
      Dirs: ConcurrentDictionary<string, bool> }

let memFs () : MemFs =
    let files = ConcurrentDictionary<string, string>()
    let bytes = ConcurrentDictionary<string, byte array>()
    let dirs = ConcurrentDictionary<string, bool>()

    let fs : Algebra.FileSystem =
        { readAllText = fun path ->
            task {
                match files.TryGetValue(path) with
                | true, c -> return c
                | _ -> return failwith $"File not found: {path}"
            }
          writeAllText = fun path content -> task { files.[path] <- content }
          writeAllBytes = fun path b ->
            task {
                bytes.[path] <- b
                files.[path] <- Text.Encoding.UTF8.GetString(b)
            }
          readAllBytes = fun path ->
            task {
                match bytes.TryGetValue(path) with
                | true, b -> return b
                | _ ->
                    match files.TryGetValue(path) with
                    | true, c -> return Text.Encoding.UTF8.GetBytes(c)
                    | _ -> return failwith $"File not found: {path}"
            }
          fileExists = fun path -> files.ContainsKey(path) || bytes.ContainsKey(path)
          directoryExists = fun path -> dirs.ContainsKey(path)
          createDirectory = fun path -> dirs.[path] <- true
          deleteFile = fun path ->
            files.TryRemove(path) |> ignore
            bytes.TryRemove(path) |> ignore
          moveFile = fun src dst ->
            match files.TryRemove(src) with
            | true, c -> files.[dst] <- c
            | _ -> ()
            match bytes.TryRemove(src) with
            | true, b -> bytes.[dst] <- b
            | _ -> ()
          getFiles = fun dir _pattern ->
            let pfx = if dir.EndsWith("/") || dir.EndsWith("\\") then dir else dir + "/"
            files.Keys
            |> Seq.append bytes.Keys
            |> Seq.distinct
            |> Seq.filter (fun k ->
                k.StartsWith(pfx, StringComparison.OrdinalIgnoreCase)
                && not (k.Substring(pfx.Length).Contains("/"))
                && not (k.Substring(pfx.Length).Contains("\\")))
            |> Seq.toArray
          getFileSize = fun path ->
            match bytes.TryGetValue(path) with
            | true, b -> int64 b.Length
            | _ ->
                match files.TryGetValue(path) with
                | true, c -> int64 (Text.Encoding.UTF8.GetByteCount(c))
                | _ -> 0L }

    { Fs = fs; Files = files; Bytes = bytes; Dirs = dirs }

// ─── Silent logger ───────────────────────────────────────────────────

let silentLogger : Algebra.Logger =
    { info = ignore; warn = ignore; error = ignore; debug = ignore }

// ─── Fixed clock ─────────────────────────────────────────────────────

let fixedClock (dt: DateTimeOffset) : Algebra.Clock =
    { utcNow = fun () -> dt }

let defaultClock : Algebra.Clock =
    fixedClock (DateTimeOffset(2025, 3, 15, 10, 30, 0, TimeSpan.Zero))

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

let emptyProvider : Algebra.EmailProvider =
    { listNewMessages = fun _ -> task { return [] }
      getAttachments = fun _ -> task { return [] }
      getMessageBody = fun _ -> task { return None } }

let mockProvider
    (messages: Domain.EmailMessage list)
    (attachments: Map<string, Domain.EmailAttachment list>)
    : Algebra.EmailProvider =
    { listNewMessages = fun _ -> task { return messages }
      getAttachments = fun id -> task { return attachments |> Map.tryFind id |> Option.defaultValue [] }
      getMessageBody = fun _ -> task { return None } }

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

// ─── Test config ─────────────────────────────────────────────────────

let testConfig (archiveDir: string) : Domain.HermesConfig =
    { ArchiveDir = archiveDir
      Credentials = "/test/creds.json"
      Accounts = [ { Label = "test"; Provider = "gmail" } ]
      SyncIntervalMinutes = 15
      MinAttachmentSize = 20480
      WatchFolders = []
      Ollama =
        { Domain.OllamaConfig.Enabled = false; BaseUrl = "http://localhost:11434"
          EmbeddingModel = "nomic-embed-text"; VisionModel = "llava"; InstructModel = "llama3.2" }
      Fallback = { Domain.FallbackConfig.Embedding = "onnx"; Ocr = "none" }
      Azure = { Domain.AzureConfig.DocumentIntelligenceEndpoint = ""; DocumentIntelligenceKey = "" } }

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
