module Hermes.Tests.ConfigTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open FsCheck
open FsCheck.Xunit
open Hermes.Core

// ─── In-memory FileSystem interpreter for testing ────────────────────

let inMemoryFileSystem () =
    let files = System.Collections.Concurrent.ConcurrentDictionary<string, string>()
    let fileBytes = System.Collections.Concurrent.ConcurrentDictionary<string, byte array>()
    let dirs = System.Collections.Concurrent.ConcurrentDictionary<string, bool>()

    let fs: Algebra.FileSystem =
        { readAllText = fun path ->
            task {
                match files.TryGetValue(path) with
                | true, content -> return content
                | _ -> return failwith $"File not found: {path}"
            }
          writeAllText = fun path content ->
            task { files.[path] <- content }
          writeAllBytes = fun path bytes ->
            task {
                fileBytes.[path] <- bytes
                files.[path] <- System.Text.Encoding.UTF8.GetString(bytes)
            }
          readAllBytes = fun path ->
            task {
                match fileBytes.TryGetValue(path) with
                | true, bytes -> return bytes
                | _ ->
                    match files.TryGetValue(path) with
                    | true, content -> return System.Text.Encoding.UTF8.GetBytes(content)
                    | _ -> return failwith $"File not found: {path}"
            }
          fileExists = fun path -> files.ContainsKey(path) || fileBytes.ContainsKey(path)
          directoryExists = fun path -> dirs.ContainsKey(path)
          createDirectory = fun path -> dirs.[path] <- true
          deleteFile = fun path ->
            files.TryRemove(path) |> ignore
            fileBytes.TryRemove(path) |> ignore
          moveFile = fun src dst ->
            match files.TryRemove(src) with
            | true, content -> files.[dst] <- content
            | _ -> ()
            match fileBytes.TryRemove(src) with
            | true, bytes -> fileBytes.[dst] <- bytes
            | _ -> ()
          getFiles = fun dir pattern ->
            let prefix = if dir.EndsWith("/") || dir.EndsWith("\\") then dir else dir + "/"
            files.Keys
            |> Seq.append fileBytes.Keys
            |> Seq.distinct
            |> Seq.filter (fun k ->
                k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && not (k.Substring(prefix.Length).Contains("/"))
                && not (k.Substring(prefix.Length).Contains("\\")))
            |> Seq.toArray
          getFileSize = fun path ->
            match fileBytes.TryGetValue(path) with
            | true, bytes -> int64 bytes.Length
            | _ ->
                match files.TryGetValue(path) with
                | true, content -> int64 (System.Text.Encoding.UTF8.GetByteCount(content))
                | _ -> 0L }

    fs, files, dirs

// ─── Config parsing tests ────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ParseYaml_ValidYaml_ReturnsConfig`` () =
    let yaml = """
archive_dir: ~/Documents/TestHermes
sync_interval_minutes: 30
min_attachment_size: 10240
ollama:
  enabled: true
  base_url: http://localhost:11434
  embedding_model: nomic-embed-text
  vision_model: llava
  instruct_model: llama3.2
"""

    match Config.parseYaml yaml with
    | Ok config ->
        Assert.Equal(30, config.SyncIntervalMinutes)
        Assert.Equal(10240, config.MinAttachmentSize)
        Assert.True(config.Ollama.Enabled)
        Assert.Equal("nomic-embed-text", config.Ollama.EmbeddingModel)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ParseYaml_EmptyYaml_ReturnsDefaults`` () =
    match Config.parseYaml "" with
    | Ok config ->
        let def = Config.defaultConfig ()
        Assert.Equal(def.SyncIntervalMinutes, config.SyncIntervalMinutes)
        Assert.Equal(def.MinAttachmentSize, config.MinAttachmentSize)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ParseYaml_WithAccounts_ParsesAccountList`` () =
    let yaml = """
accounts:
  - label: john-personal
    provider: gmail
  - label: john-work
    provider: gmail
"""

    match Config.parseYaml yaml with
    | Ok config ->
        Assert.Equal(2, config.Accounts.Length)
        Assert.Equal("john-personal", config.Accounts.[0].Label)
        Assert.Equal("gmail", config.Accounts.[1].Provider)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ParseYaml_WithWatchFolders_ParsesPatterns`` () =
    let yaml = """
watch_folders:
  - path: /tmp/downloads
    patterns: ["*.pdf", "*invoice*"]
"""

    match Config.parseYaml yaml with
    | Ok config ->
        Assert.Equal(1, config.WatchFolders.Length)
        Assert.Equal(2, config.WatchFolders.[0].Patterns.Length)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_Load_MissingFile_ReturnsError`` () =
    let fs, _, _ = inMemoryFileSystem ()
    let result = Config.load fs "/nonexistent/config.yaml" |> Async.AwaitTask |> Async.RunSynchronously

    match result with
    | Error msg -> Assert.Contains("not found", msg)
    | Ok _ -> failwith "Expected Error for missing file"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_Load_ValidFile_ReturnsConfig`` () =
    let fs, files, _ = inMemoryFileSystem ()
    files.["/test/config.yaml"] <- "sync_interval_minutes: 42"
    let result = Config.load fs "/test/config.yaml" |> Async.AwaitTask |> Async.RunSynchronously

    match result with
    | Ok config -> Assert.Equal(42, config.SyncIntervalMinutes)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_Init_CreatesConfigAndRules`` () =
    let fs, files, dirs = inMemoryFileSystem ()
    let result = Config.init fs |> Async.AwaitTask |> Async.RunSynchronously

    match result with
    | Ok created ->
        Assert.Equal(2, created.Length)
        Assert.True(files.Count >= 2)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_Init_SkipsExistingFiles`` () =
    let fs, files, _ = inMemoryFileSystem ()
    let configPath = Path.Combine(Config.configDir (), "config.yaml")
    files.[configPath] <- "existing content"
    let result = Config.init fs |> Async.AwaitTask |> Async.RunSynchronously

    match result with
    | Ok created ->
        // Only rules.yaml should be created, config.yaml already existed
        Assert.Equal(1, created.Length)
        Assert.Equal("existing content", files.[configPath])
    | Error e -> failwith $"Expected Ok, got Error: {e}"

// ─── Path expansion tests ────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ExpandHome_TildePath_ExpandsToUserHome`` () =
    let result = Config.expandHome "~/Documents/test"
    let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
    Assert.StartsWith(home, result)
    Assert.EndsWith("test", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ExpandHome_AbsolutePath_ReturnsUnchanged`` () =
    let path = "/usr/local/bin"
    Assert.Equal(path, Config.expandHome path)

// ─── Property-based tests ────────────────────────────────────────────

[<Property>]
[<Trait("Category", "Property")>]
let ``Config_ParseYaml_NeverThrows`` (yaml: string | null) =
    // parseYaml should always return Ok or Error, never throw
    let result = Config.parseYaml (match yaml with null -> "" | s -> s)
    match result with
    | Ok _ -> true
    | Error _ -> true
