namespace Hermes.Core

open System
open System.IO
open System.Runtime.InteropServices
open YamlDotNet.Serialization
open YamlDotNet.Serialization.NamingConventions

/// Configuration loading, defaults, and initialisation.
/// All I/O goes through the FileSystem algebra.
[<RequireQualifiedAccess>]
module Config =

    // ─── YAML DTO types (mutable, required by YamlDotNet) ────────────

    [<CLIMutable>]
    type BackfillDto =
        { [<YamlMember(Alias = "enabled")>]
          Enabled: bool
          [<YamlMember(Alias = "since")>]
          Since: string
          [<YamlMember(Alias = "batch_size")>]
          BatchSize: int
          [<YamlMember(Alias = "attachments_only")>]
          AttachmentsOnly: bool
          [<YamlMember(Alias = "include_bodies")>]
          IncludeBodies: bool }

    [<CLIMutable>]
    type AccountDto =
        { [<YamlMember(Alias = "label")>]
          Label: string
          [<YamlMember(Alias = "provider")>]
          Provider: string
          [<YamlMember(Alias = "backfill")>]
          Backfill: BackfillDto }

    [<CLIMutable>]
    type WatchFolderDto =
        { [<YamlMember(Alias = "path")>]
          Path: string
          [<YamlMember(Alias = "patterns")>]
          Patterns: string array }

    [<CLIMutable>]
    type OllamaDto =
        { [<YamlMember(Alias = "enabled")>]
          Enabled: bool
          [<YamlMember(Alias = "base_url")>]
          BaseUrl: string
          [<YamlMember(Alias = "embedding_model")>]
          EmbeddingModel: string
          [<YamlMember(Alias = "vision_model")>]
          VisionModel: string
          [<YamlMember(Alias = "instruct_model")>]
          InstructModel: string }

    [<CLIMutable>]
    type FallbackDto =
        { [<YamlMember(Alias = "embedding")>]
          Embedding: string
          [<YamlMember(Alias = "ocr")>]
          Ocr: string }

    [<CLIMutable>]
    type AzureDto =
        { [<YamlMember(Alias = "document_intelligence_endpoint")>]
          DocumentIntelligenceEndpoint: string
          [<YamlMember(Alias = "document_intelligence_key")>]
          DocumentIntelligenceKey: string }

    [<CLIMutable>]
    type AzureOpenAIDto =
        { [<YamlMember(Alias = "endpoint")>]
          Endpoint: string
          [<YamlMember(Alias = "api_key")>]
          ApiKey: string
          [<YamlMember(Alias = "deployment")>]
          Deployment: string
          [<YamlMember(Alias = "max_tokens")>]
          MaxTokens: int
          [<YamlMember(Alias = "timeout_seconds")>]
          TimeoutSeconds: int }

    [<CLIMutable>]
    type ChatDto =
        { [<YamlMember(Alias = "provider")>]
          Provider: string
          [<YamlMember(Alias = "azure_openai")>]
          AzureOpenAI: AzureOpenAIDto }

    [<CLIMutable>]
    type HermesConfigDto =
        { [<YamlMember(Alias = "archive_dir")>]
          ArchiveDir: string
          [<YamlMember(Alias = "credentials")>]
          Credentials: string
          [<YamlMember(Alias = "accounts")>]
          Accounts: AccountDto array
          [<YamlMember(Alias = "sync_interval_minutes")>]
          SyncIntervalMinutes: int
          [<YamlMember(Alias = "min_attachment_size")>]
          MinAttachmentSize: int
          [<YamlMember(Alias = "watch_folders")>]
          WatchFolders: WatchFolderDto array
          [<YamlMember(Alias = "ollama")>]
          Ollama: OllamaDto
          [<YamlMember(Alias = "fallback")>]
          Fallback: FallbackDto
          [<YamlMember(Alias = "azure")>]
          Azure: AzureDto
          [<YamlMember(Alias = "chat")>]
          Chat: ChatDto }

    // ─── Path helpers ────────────────────────────────────────────────

    /// Expand ~ to the user home directory.
    let expandHome (env: Algebra.Environment) (path: string) =
        if path.StartsWith("~/") || path.StartsWith("~\\") then
            Path.Combine(env.homeDirectory (), path.Substring(2))
        elif path = "~" then
            env.homeDirectory ()
        else
            path

    /// Get the platform-specific config directory.
    let configDir (env: Algebra.Environment) =
        env.configDirectory ()

    /// Default archive directory.
    let defaultArchiveDir (env: Algebra.Environment) =
        Path.Combine(env.documentsDirectory (), "hermes")

    // ─── Defaults ────────────────────────────────────────────────────

    let defaultConfig (env: Algebra.Environment) : Domain.HermesConfig =
        { ArchiveDir = defaultArchiveDir env
          Credentials = Path.Combine(configDir env, "gmail_credentials.json")
          Accounts = []
          SyncIntervalMinutes = 15
          MinAttachmentSize = 20480
          WatchFolders = []
          Ollama =
            { Domain.OllamaConfig.Enabled = true
              BaseUrl = "http://localhost:11434"
              EmbeddingModel = "nomic-embed-text"
              VisionModel = "llava"
              InstructModel = "llama3.2" }
          Fallback = { Domain.FallbackConfig.Embedding = "onnx"; Ocr = "azure-document-intelligence" }
          Azure =
            { Domain.AzureConfig.DocumentIntelligenceEndpoint = ""
              DocumentIntelligenceKey = "" }
          Chat =
            { Domain.ChatConfig.Provider = Domain.ChatProviderKind.Ollama
              AzureOpenAI =
                { Domain.AzureOpenAIConfig.Endpoint = ""
                  ApiKey = ""
                  DeploymentName = "gpt-4o"
                  MaxTokens = 4096
                  TimeoutSeconds = 300 } } }

    // ─── DTO → Domain mapping ────────────────────────────────────────

    let private orDefault (fallback: string) (value: string | null) =
        match value with
        | null -> fallback
        | v -> v

    let private toConfig (env: Algebra.Environment) (dto: HermesConfigDto) : Domain.HermesConfig =
        let def = defaultConfig env

        let defaultBackfill : Domain.BackfillConfig =
            { Enabled = true; Since = None; BatchSize = 50; AttachmentsOnly = true; IncludeBodies = false }

        let accounts : Domain.AccountConfig list =
            if isNull (box dto.Accounts) || dto.Accounts.Length = 0 then
                def.Accounts
            else
                dto.Accounts
                |> Array.map (fun a ->
                    let bf =
                        if isNull (box a.Backfill) then defaultBackfill
                        else
                            { Domain.BackfillConfig.Enabled = a.Backfill.Enabled
                              Since =
                                let raw = a.Backfill.Since
                                if System.Object.ReferenceEquals(raw, null) || System.String.IsNullOrWhiteSpace(raw) then None
                                else match System.DateTimeOffset.TryParse(raw) with true, d -> Some d | _ -> None
                              BatchSize = if a.Backfill.BatchSize = 0 then 50 else a.Backfill.BatchSize
                              AttachmentsOnly = a.Backfill.AttachmentsOnly
                              IncludeBodies = a.Backfill.IncludeBodies }
                    ({ Label = a.Label |> orDefault ""
                       Provider = a.Provider |> orDefault "gmail"
                       Backfill = bf } : Domain.AccountConfig))
                |> Array.toList

        let watchFolders : Domain.WatchFolderConfig list =
            if isNull (box dto.WatchFolders) || dto.WatchFolders.Length = 0 then
                def.WatchFolders
            else
                dto.WatchFolders
                |> Array.map (fun w ->
                    ({ Path = w.Path |> orDefault "" |> expandHome env
                       Patterns =
                        if isNull (box w.Patterns) then
                            []
                        else
                            w.Patterns |> Array.toList } : Domain.WatchFolderConfig))
                |> Array.toList

        let ollama =
            if isNull (box dto.Ollama) then
                def.Ollama
            else
                { Enabled = dto.Ollama.Enabled
                  BaseUrl = dto.Ollama.BaseUrl |> orDefault def.Ollama.BaseUrl
                  EmbeddingModel = dto.Ollama.EmbeddingModel |> orDefault def.Ollama.EmbeddingModel
                  VisionModel = dto.Ollama.VisionModel |> orDefault def.Ollama.VisionModel
                  InstructModel = dto.Ollama.InstructModel |> orDefault def.Ollama.InstructModel }

        let fallback =
            if isNull (box dto.Fallback) then
                def.Fallback
            else
                { Embedding = dto.Fallback.Embedding |> orDefault def.Fallback.Embedding
                  Ocr = dto.Fallback.Ocr |> orDefault def.Fallback.Ocr }

        let azure =
            if isNull (box dto.Azure) then
                def.Azure
            else
                { DocumentIntelligenceEndpoint = dto.Azure.DocumentIntelligenceEndpoint |> orDefault ""
                  DocumentIntelligenceKey = dto.Azure.DocumentIntelligenceKey |> orDefault "" }

        let chat =
            if isNull (box dto.Chat) then
                def.Chat
            else
                let providerKind =
                    dto.Chat.Provider
                    |> orDefault "ollama"
                    |> Domain.ChatProviderKind.fromString
                    |> function Ok k -> k | Error _ -> Domain.ChatProviderKind.Ollama

                let azureOpenAI =
                    if isNull (box dto.Chat.AzureOpenAI) then
                        def.Chat.AzureOpenAI
                    else
                        let a = dto.Chat.AzureOpenAI
                        { Domain.AzureOpenAIConfig.Endpoint = a.Endpoint |> orDefault def.Chat.AzureOpenAI.Endpoint
                          ApiKey = a.ApiKey |> orDefault def.Chat.AzureOpenAI.ApiKey
                          DeploymentName = a.Deployment |> orDefault def.Chat.AzureOpenAI.DeploymentName
                          MaxTokens = if a.MaxTokens = 0 then def.Chat.AzureOpenAI.MaxTokens else a.MaxTokens
                          TimeoutSeconds = if a.TimeoutSeconds = 0 then def.Chat.AzureOpenAI.TimeoutSeconds else a.TimeoutSeconds }

                { Domain.ChatConfig.Provider = providerKind
                  AzureOpenAI = azureOpenAI }

        { ArchiveDir = dto.ArchiveDir |> orDefault def.ArchiveDir |> expandHome env
          Credentials = dto.Credentials |> orDefault def.Credentials |> expandHome env
          Accounts = accounts
          SyncIntervalMinutes =
            if dto.SyncIntervalMinutes = 0 then
                def.SyncIntervalMinutes
            else
                dto.SyncIntervalMinutes
          MinAttachmentSize =
            if dto.MinAttachmentSize = 0 then
                def.MinAttachmentSize
            else
                dto.MinAttachmentSize
          WatchFolders = watchFolders
          Ollama = ollama
          Fallback = fallback
          Azure = azure
          Chat = chat }

    // ─── YAML deserializer ───────────────────────────────────────────

    let private deserializer =
        DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()

    // ─── Public API (parameterised over FileSystem algebra) ──────────

    /// Parse a HermesConfig from a YAML string. Pure — no I/O.
    let parseYaml (env: Algebra.Environment) (yaml: string) : Result<Domain.HermesConfig, string> =
        try
            if System.String.IsNullOrWhiteSpace(yaml) then
                Ok(defaultConfig env)
            else
                let dto = deserializer.Deserialize<HermesConfigDto>(yaml)

                if isNull (box dto) then
                    Ok(defaultConfig env)
                else
                    Ok(toConfig env dto)
        with ex ->
            Error $"Failed to parse config: {ex.Message}"

    /// Load config from a file, using the FileSystem algebra.
    let load (fs: Algebra.FileSystem) (env: Algebra.Environment) (path: string) =
        task {
            if not (fs.fileExists path) then
                return Error $"Configuration file not found: {path}"
            else
                try
                    let! yaml = fs.readAllText path
                    return parseYaml env yaml
                with ex ->
                    return Error $"Failed to load config: {ex.Message}"
        }

    // ─── Default YAML templates ──────────────────────────────────────

    let defaultConfigYaml (env: Algebra.Environment) =
        $"""archive_dir: ~/Documents/hermes

# Gmail OAuth client credential (shared across accounts)
credentials: {configDir env}/gmail_credentials.json

accounts: []

# Sync settings
sync_interval_minutes: 15
min_attachment_size: 20480

# Watched folders
watch_folders: []

# Ollama settings
ollama:
  enabled: true
  base_url: http://localhost:11434
  embedding_model: nomic-embed-text
  vision_model: llava
  instruct_model: llama3.2

# Fallbacks when Ollama unavailable
fallback:
  embedding: onnx
  ocr: azure-document-intelligence

# Azure Document Intelligence (optional)
azure:
  document_intelligence_endpoint: ""
  document_intelligence_key: ""
"""

    let defaultRulesYaml () =
        """# Hermes classification rules
# Rules are evaluated in order: sender_domain -> filename -> subject -> unsorted/

rules:
  - name: invoices-by-filename
    match:
      filename: "(?i)invoice"
    category: invoices

  - name: receipts-by-filename
    match:
      filename: "(?i)receipt"
    category: receipts

  - name: statements-by-filename
    match:
      filename: "(?i)statement"
    category: bank-statements

  - name: payslips-by-filename
    match:
      filename: "(?i)payslip|pay.?slip"
    category: payslips

  - name: tax-by-subject
    match:
      subject: "(?i)tax|ato|mygov"
    category: tax

# Default: unmatched files go to unsorted/
default_category: unsorted

# ─── Tier 2: Content-based classification ─────────────────────────
# Applied when Tier 1 rules produce 'unsorted'. Matches on extracted markdown.
# All conditions in a rule must match (AND). First highest-confidence rule wins.

content_rules:
  - name: payslip-by-content
    match:
      content_any: ["gross pay", "tax withheld", "net pay", "pay period", "pay date"]
    category: payslips
    confidence: 0.85

  - name: bank-statement-by-content
    match:
      content_any: ["opening balance", "closing balance", "narrative", "transaction details"]
    category: bank-statements
    confidence: 0.80

  - name: invoice-by-content
    match:
      content_any: ["amount due", "invoice number", "invoice date", "payment due", "tax invoice"]
    category: invoices
    confidence: 0.80

  - name: rental-statement-by-content
    match:
      content_any: ["folio", "owner statement", "rental income", "management fee", "disbursement"]
    category: rental-statements
    confidence: 0.80

  - name: tax-by-content
    match:
      content_any: ["tax return", "assessment notice", "income statement", "PAYG summary", "taxable income"]
    category: tax
    confidence: 0.85

  - name: insurance-by-content
    match:
      content_any: ["policy number", "premium", "sum insured", "certificate of insurance", "renewal notice"]
    category: insurance
    confidence: 0.75

  - name: receipt-by-content
    match:
      content_any: ["receipt", "payment received", "thank you for your payment", "transaction approved"]
    category: receipts
    confidence: 0.70

  - name: utilities-by-content
    match:
      content_any: ["electricity", "gas usage", "water usage", "meter reading", "usage summary"]
    category: utilities
    confidence: 0.75
"""

    /// Write default config & rules files via the FileSystem algebra.
    /// Returns the list of files that were created (skips existing).
    let init (fs: Algebra.FileSystem) (env: Algebra.Environment) =
        task {
            try
                let dir = configDir env
                fs.createDirectory dir
                fs.createDirectory (Path.Combine(dir, "tokens"))

                let created = ResizeArray<string>()

                let configPath = Path.Combine(dir, "config.yaml")

                if not (fs.fileExists configPath) then
                    do! fs.writeAllText configPath (defaultConfigYaml env)
                    created.Add(configPath)

                let rulesPath = Path.Combine(dir, "rules.yaml")

                if not (fs.fileExists rulesPath) then
                    do! fs.writeAllText rulesPath (defaultRulesYaml ())
                    created.Add(rulesPath)

                return Ok(created |> Seq.toList)
            with ex ->
                return Error $"Failed to initialize config: {ex.Message}"
        }
