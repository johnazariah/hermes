module Hermes.Tests.ConfigTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open FsCheck
open FsCheck.Xunit
open Hermes.Core

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
    let m = TestHelpers.memFs ()
    let result = Config.load m.Fs "/nonexistent/config.yaml" |> Async.AwaitTask |> Async.RunSynchronously

    match result with
    | Error msg -> Assert.Contains("not found", msg)
    | Ok _ -> failwith "Expected Error for missing file"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_Load_ValidFile_ReturnsConfig`` () =
    let m = TestHelpers.memFs ()
    m.Files.["/test/config.yaml"] <- "sync_interval_minutes: 42"
    let result = Config.load m.Fs "/test/config.yaml" |> Async.AwaitTask |> Async.RunSynchronously

    match result with
    | Ok config -> Assert.Equal(42, config.SyncIntervalMinutes)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_Init_CreatesConfigAndRules`` () =
    let m = TestHelpers.memFs ()
    let result = Config.init m.Fs |> Async.AwaitTask |> Async.RunSynchronously

    match result with
    | Ok created ->
        Assert.Equal(2, created.Length)
        Assert.True(m.Files.Count >= 2)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_Init_SkipsExistingFiles`` () =
    let m = TestHelpers.memFs ()
    let configPath = Path.Combine(Config.configDir (), "config.yaml")
    m.Files.[configPath] <- "existing content"
    let result = Config.init m.Fs |> Async.AwaitTask |> Async.RunSynchronously

    match result with
    | Ok created ->
        // Only rules.yaml should be created, config.yaml already existed
        Assert.Equal(1, created.Length)
        Assert.Equal("existing content", m.Files.[configPath])
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
