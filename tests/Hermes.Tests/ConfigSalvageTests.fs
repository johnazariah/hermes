module Hermes.Tests.ConfigSalvageTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open Hermes.Core

let private environment =
    TestHelpers.fakeEnvironment "/home/test" "/home/test/.config/hermes" "/home/test/Documents"

let private parse (yaml: string) : Domain.HermesConfig =
    Config.parseYaml environment yaml |> Result.defaultWith failwith

let private defaultAccount (label: string) : Domain.AccountConfig =
    { Label = label
      Provider = "gmail"
      ClientId = ""
      TenantId = "common"
      RedirectPort = 53682
      Backfill =
        { Enabled = true
          Since = None
          BatchSize = 50
          AttachmentsOnly = true
          IncludeBodies = false } }

let private validAccount: Domain.AccountConfig =
    { defaultAccount "valid-since" with
        Provider = "outlook"
        ClientId = "app-id"
        TenantId = "organizations"
        RedirectPort = 4242
        Backfill =
            { Enabled = false
              Since = Some (DateTimeOffset(2024, 3, 2, 1, 2, 3, TimeSpan.Zero))
              BatchSize = 25
              AttachmentsOnly = false
              IncludeBodies = true } }

let private accountsYaml =
    """
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

let private nullServicesYaml =
    """
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

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ParseYaml_AccountDefaultsAndBackfillValues_MapsDeterministically`` () =
    let config = parse accountsYaml
    let expected = [ defaultAccount "defaults"; validAccount; defaultAccount "invalid-since" ]
    Assert.Equal<Domain.AccountConfig list>(expected, config.Accounts)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ParseYaml_WatchFolderWithoutPatterns_DefaultsToEmptyPatterns`` () =
    let config = parse "watch_folders:\n  - path: /tmp/downloads"
    let folder = config.WatchFolders |> List.exactlyOne
    Assert.Equal("/tmp/downloads", folder.Path)
    Assert.Empty(folder.Patterns)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_Load_ReadFailure_ReturnsDescriptiveError`` () =
    task {
        let memory = TestHelpers.memFs ()
        let path = "/test/config.yaml"
        memory.Put path "sync_interval_minutes: 42"
        let fs =
            { memory.Fs with
                readAllText = fun _ -> Task.FromException<string>(IOException("read denied")) }
        let! result = Config.load fs environment path
        Assert.Equal<Result<Domain.HermesConfig, string>>(
            Error "Failed to load config: read denied", result)
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_Init_WriteFailure_ReturnsDescriptiveError`` () =
    task {
        let memory = TestHelpers.memFs ()
        let fs =
            { memory.Fs with
                writeAllText = fun _ _ -> Task.FromException<unit>(IOException("write denied")) }
        let! result = Config.init fs environment
        Assert.Equal<Result<string list, string>>(
            Error "Failed to initialize config: write denied", result)
        Assert.Empty(memory.Files)
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ExpandHome_TildeOnly_ReturnsUserHome`` () =
    Assert.Equal("/home/test", Config.expandHome environment "~")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ExpandHome_WindowsMixedSeparators_NormalizesSeparators`` () =
    let windows =
        TestHelpers.fakeEnvironment
            @"C:\Users\Test" @"C:\Users\Test\AppData\Hermes" @"C:\Users\Test\Documents"
    let actual = Config.expandHome windows @"~/Documents/mixed\report.pdf"
    Assert.Equal(@"C:\Users\Test\Documents\mixed\report.pdf", actual)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ParseYaml_NullServiceValues_DefaultsValuesAndInvalidProvider`` () =
    let config = parse nullServicesYaml
    let defaults = Config.defaultConfig environment
    Assert.Equal<Domain.OllamaConfig>({ defaults.Ollama with Enabled = false }, config.Ollama)
    Assert.Equal<Domain.FallbackConfig>(defaults.Fallback, config.Fallback)
    Assert.Equal<Domain.AzureConfig>(defaults.Azure, config.Azure)
    Assert.Equal<Domain.ChatConfig>(defaults.Chat, config.Chat)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ParseYaml_NullChatProvider_DefaultsProviderAndAzureSettings`` () =
    let config = parse "chat:\n  provider: null"
    let defaults = Config.defaultConfig environment
    Assert.Equal<Domain.ChatConfig>(defaults.Chat, config.Chat)
