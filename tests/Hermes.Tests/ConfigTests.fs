module Hermes.Tests.ConfigTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open FsCheck
open FsCheck.Xunit
open Hermes.Core

let private testEnv = TestHelpers.fakeEnvironment "/home/test" "/home/test/.config/hermes" "/home/test/Documents"

let private parseConfig yaml =
    match Config.parseYaml testEnv yaml with
    | Ok config -> config
    | Error error -> failwith $"Expected Ok, got Error: {error}"

let private withReadFailure message (fs: Algebra.FileSystem) =
    { fs with
        readAllText = fun _ -> Task.FromException<string>(IOException(message)) }

let private withWriteFailure message (fs: Algebra.FileSystem) =
    { fs with
        writeAllText = fun _ _ -> Task.FromException<unit>(IOException(message)) }

let private defaultBackfill: Domain.BackfillConfig =
    { Enabled = true
      Since = None
      BatchSize = 50
      AttachmentsOnly = true
      IncludeBodies = false }

let private accountByLabel label (config: Domain.HermesConfig) =
    config.Accounts |> List.find (fun account -> account.Label = label)

let private assertDefaultAccount (account: Domain.AccountConfig) =
    Assert.Equal("gmail", account.Provider)
    Assert.Equal("", account.ClientId)
    Assert.Equal("common", account.TenantId)
    Assert.Equal(53682, account.RedirectPort)
    Assert.Equal(defaultBackfill, account.Backfill)

let private assertValidAccount (account: Domain.AccountConfig) =
    let expectedSince = DateTimeOffset(2024, 3, 2, 1, 2, 3, TimeSpan.Zero)
    Assert.Equal("outlook", account.Provider)
    Assert.Equal("app-id", account.ClientId)
    Assert.Equal("organizations", account.TenantId)
    Assert.Equal(4242, account.RedirectPort)
    Assert.False(account.Backfill.Enabled)
    Assert.Equal(Some expectedSince, account.Backfill.Since)
    Assert.Equal(25, account.Backfill.BatchSize)
    Assert.False(account.Backfill.AttachmentsOnly)
    Assert.True(account.Backfill.IncludeBodies)

let private assertInvalidBackfill (account: Domain.AccountConfig) =
    Assert.True(account.Backfill.Since.IsNone)
    Assert.Equal(50, account.Backfill.BatchSize)
    Assert.Equal(53682, account.RedirectPort)

let private assertDefaultedOllama (ollama: Domain.OllamaConfig) =
    Assert.False(ollama.Enabled)
    Assert.Equal("http://localhost:11434", ollama.BaseUrl)
    Assert.Equal("nomic-embed-text", ollama.EmbeddingModel)
    Assert.Equal("llava", ollama.VisionModel)
    Assert.Equal("llama3.2", ollama.InstructModel)
    Assert.Equal("", ollama.TriageModel)
    Assert.True(ollama.SharedGpu)
    Assert.Equal(180, ollama.MaxHoldSeconds)

let private assertDefaultedFallback (fallback: Domain.FallbackConfig) =
    Assert.Equal("onnx", fallback.Embedding)
    Assert.Equal("azure-document-intelligence", fallback.Ocr)

let private assertDefaultedAzure (azure: Domain.AzureConfig) =
    Assert.Equal("", azure.DocumentIntelligenceEndpoint)
    Assert.Equal("", azure.DocumentIntelligenceKey)

let private assertDefaultedChat (chat: Domain.ChatConfig) =
    Assert.Equal(Domain.ChatProviderKind.Ollama, chat.Provider)
    Assert.Equal("", chat.AzureOpenAI.Endpoint)
    Assert.Equal("", chat.AzureOpenAI.ApiKey)
    Assert.Equal("gpt-4o", chat.AzureOpenAI.DeploymentName)
    Assert.Equal(4096, chat.AzureOpenAI.MaxTokens)
    Assert.Equal(300, chat.AzureOpenAI.TimeoutSeconds)

let private accountsYaml = """
accounts:
  - label: defaults
    provider: null
    client_id: ~
    tenant_id:
    redirect_port: 0
  - label: valid-since
    provider: outlook
    client_id: app-id
    tenant_id: organizations
    redirect_port: 4242
    backfill:
      enabled: false
      since: "2024-03-02T01:02:03+00:00"
      batch_size: 25
      attachments_only: false
      include_bodies: true
  - label: invalid-since
    backfill:
      enabled: true
      since: definitely-not-a-date
      batch_size: 0
      attachments_only: true
      include_bodies: false
"""

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

    match Config.parseYaml testEnv yaml with
    | Ok config ->
        Assert.Equal(30, config.SyncIntervalMinutes)
        Assert.Equal(10240, config.MinAttachmentSize)
        Assert.True(config.Ollama.Enabled)
        Assert.Equal("nomic-embed-text", config.Ollama.EmbeddingModel)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ParseYaml_EmptyYaml_ReturnsDefaults`` () =
    match Config.parseYaml testEnv "" with
    | Ok config ->
        let def = Config.defaultConfig testEnv
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

    match Config.parseYaml testEnv yaml with
    | Ok config ->
        Assert.Equal(2, config.Accounts.Length)
        Assert.Equal("john-personal", config.Accounts.[0].Label)
        Assert.Equal("gmail", config.Accounts.[1].Provider)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ParseYaml_AccountDefaultsAndBackfillValues_MapsDeterministically`` () =
    let config = parseConfig accountsYaml
    Assert.Equal(3, config.Accounts.Length)
    config |> accountByLabel "defaults" |> assertDefaultAccount
    config |> accountByLabel "valid-since" |> assertValidAccount
    config |> accountByLabel "invalid-since" |> assertInvalidBackfill

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ParseYaml_WatchFolderWithoutPatterns_DefaultsToEmptyPatterns`` () =
    let config = parseConfig "watch_folders:\n  - path: /tmp/downloads"
    let folder = config.WatchFolders |> List.exactlyOne
    Assert.Equal("/tmp/downloads", folder.Path)
    Assert.Empty(folder.Patterns)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ParseYaml_WithWatchFolders_ParsesPatterns`` () =
    let yaml = """
watch_folders:
  - path: /tmp/downloads
    patterns: ["*.pdf", "*invoice*"]
"""

    match Config.parseYaml testEnv yaml with
    | Ok config ->
        Assert.Equal(1, config.WatchFolders.Length)
        Assert.Equal(2, config.WatchFolders.[0].Patterns.Length)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_Load_MissingFile_ReturnsError`` () =
    let m = TestHelpers.memFs ()
    let result = Config.load m.Fs testEnv "/nonexistent/config.yaml" |> Async.AwaitTask |> Async.RunSynchronously

    match result with
    | Error msg -> Assert.Contains("not found", msg)
    | Ok _ -> failwith "Expected Error for missing file"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_Load_ValidFile_ReturnsConfig`` () =
    let m = TestHelpers.memFs ()
    m.Files.["/test/config.yaml"] <- "sync_interval_minutes: 42"
    let result = Config.load m.Fs testEnv "/test/config.yaml" |> Async.AwaitTask |> Async.RunSynchronously

    match result with
    | Ok config -> Assert.Equal(42, config.SyncIntervalMinutes)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_Load_ReadFailure_ReturnsDescriptiveError`` () =
    task {
        let m = TestHelpers.memFs ()
        let path = "/test/config.yaml"
        m.Put path "sync_interval_minutes: 42"

        let! result = Config.load (withReadFailure "read denied" m.Fs) testEnv path

        match result with
        | Error message -> Assert.Equal("Failed to load config: read denied", message)
        | Ok _ -> failwith "Expected read failure"
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_Init_CreatesConfigAndRules`` () =
    let m = TestHelpers.memFs ()
    let result = Config.init m.Fs testEnv |> Async.AwaitTask |> Async.RunSynchronously

    match result with
    | Ok created ->
        Assert.Equal(2, created.Length)
        Assert.True(m.Files.Count >= 2)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_Init_SkipsExistingFiles`` () =
    let m = TestHelpers.memFs ()
    let configPath = Path.Combine(Config.configDir testEnv, "config.yaml") |> m.Norm
    m.Put configPath "existing content"
    let result = Config.init m.Fs testEnv |> Async.AwaitTask |> Async.RunSynchronously

    match result with
    | Ok created ->
        Assert.Equal(1, created.Length)
        Assert.Equal(Some "existing content", m.Get configPath)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_Init_WriteFailure_ReturnsDescriptiveError`` () =
    task {
        let m = TestHelpers.memFs ()
        let failingFs = withWriteFailure "write denied" m.Fs
        let! result = Config.init failingFs testEnv

        match result with
        | Error message -> Assert.Equal("Failed to initialize config: write denied", message)
        | Ok _ -> failwith "Expected write failure"
    }

// ─── Path expansion tests ────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ExpandHome_TildePath_ExpandsToUserHome`` () =
    let result = Config.expandHome testEnv "~/Documents/test"
    Assert.StartsWith("/home/test", result)
    Assert.EndsWith("test", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ExpandHome_TildeOnly_ReturnsUserHome`` () =
    Assert.Equal("/home/test", Config.expandHome testEnv "~")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ExpandHome_WindowsMixedSeparators_NormalizesSeparators`` () =
    let windowsEnv =
        TestHelpers.fakeEnvironment
            @"C:\Users\Test"
            @"C:\Users\Test\AppData\Hermes"
            @"C:\Users\Test\Documents"

    let actual = Config.expandHome windowsEnv @"~/Documents/mixed\report.pdf"
    Assert.Equal(@"C:\Users\Test\Documents\mixed\report.pdf", actual)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ExpandHome_AbsolutePath_ReturnsUnchanged`` () =
    let path = "/usr/local/bin"
    Assert.Equal(path, Config.expandHome testEnv path)

// ─── Chat provider config tests ──────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ParseYaml_ChatAzureOpenAI_ParsesProvider`` () =
    let yaml = """
chat:
  provider: azure-openai
  azure_openai:
    endpoint: https://test.openai.azure.com/
    api_key: test-key-123
    deployment: gpt-4o-mini
    max_tokens: 2048
    timeout_seconds: 120
"""

    match Config.parseYaml testEnv yaml with
    | Ok config ->
        Assert.Equal(Domain.ChatProviderKind.AzureOpenAI, config.Chat.Provider)
        Assert.Equal("https://test.openai.azure.com/", config.Chat.AzureOpenAI.Endpoint)
        Assert.Equal("test-key-123", config.Chat.AzureOpenAI.ApiKey)
        Assert.Equal("gpt-4o-mini", config.Chat.AzureOpenAI.DeploymentName)
        Assert.Equal(2048, config.Chat.AzureOpenAI.MaxTokens)
        Assert.Equal(120, config.Chat.AzureOpenAI.TimeoutSeconds)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ParseYaml_ChatOllama_ParsesProvider`` () =
    let yaml = """
chat:
  provider: ollama
"""

    match Config.parseYaml testEnv yaml with
    | Ok config ->
        Assert.Equal(Domain.ChatProviderKind.Ollama, config.Chat.Provider)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ParseYaml_NoChatSection_DefaultsToOllama`` () =
    let yaml = """
sync_interval_minutes: 15
"""

    match Config.parseYaml testEnv yaml with
    | Ok config ->
        Assert.Equal(Domain.ChatProviderKind.Ollama, config.Chat.Provider)
        Assert.Equal("gpt-4o", config.Chat.AzureOpenAI.DeploymentName)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ParseYaml_NullServiceValues_DefaultsValuesAndInvalidProvider`` () =
    let yaml = """
ollama:
  enabled: false
  base_url: null
  embedding_model: null
  vision_model: null
  instruct_model: null
  triage_model: null
  shared_gpu: false
  max_hold_seconds: 0
fallback:
  embedding: null
  ocr: null
azure:
  document_intelligence_endpoint: null
  document_intelligence_key: null
chat:
  provider: unsupported-provider
  azure_openai:
    endpoint: null
    api_key: null
    deployment: null
    max_tokens: 0
    timeout_seconds: 0
"""

    let config = parseConfig yaml
    assertDefaultedOllama config.Ollama
    assertDefaultedFallback config.Fallback
    assertDefaultedAzure config.Azure
    assertDefaultedChat config.Chat

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ParseYaml_NullChatProvider_DefaultsProviderAndAzureSettings`` () =
    let config = parseConfig "chat:\n  provider: null"
    let defaults = (Config.defaultConfig testEnv).Chat
    Assert.Equal(Domain.ChatProviderKind.Ollama, config.Chat.Provider)
    Assert.Equal(defaults.AzureOpenAI, config.Chat.AzureOpenAI)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ChatProviderKind_FromString_ParsesVariants`` () =
    Assert.Equal(Ok Domain.ChatProviderKind.Ollama, Domain.ChatProviderKind.fromString "ollama")
    Assert.Equal(Ok Domain.ChatProviderKind.AzureOpenAI, Domain.ChatProviderKind.fromString "azure-openai")
    Assert.Equal(Ok Domain.ChatProviderKind.AzureOpenAI, Domain.ChatProviderKind.fromString "azure_openai")
    Assert.True((Domain.ChatProviderKind.fromString "unknown").IsError)

// ─── Property-based tests ────────────────────────────────────────────

[<Property>]
[<Trait("Category", "Property")>]
let ``Config_ParseYaml_NeverThrows`` (yaml: string | null) =
    // parseYaml should always return Ok or Error, never throw
    let result = Config.parseYaml testEnv (match yaml with null -> "" | s -> s)
    match result with
    | Ok _ -> true
    | Error _ -> true
