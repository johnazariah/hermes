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
    type AccountDto =
        { [<YamlMember(Alias = "label")>]
          Label: string
          [<YamlMember(Alias = "provider")>]
          Provider: string }

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
          Azure: AzureDto }

    // ─── Path helpers ────────────────────────────────────────────────

    /// Expand ~ to the user home directory.
    let expandHome (path: string) =
        if path.StartsWith("~/") || path.StartsWith("~\\") then
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Substring(2))
        elif path = "~" then
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        else
            path

    /// Get the platform-specific config directory.
    let configDir () =
        if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hermes")
        else
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "hermes")

    /// Default archive directory.
    let defaultArchiveDir () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "hermes")

    // ─── Defaults ────────────────────────────────────────────────────

    let defaultConfig () : Domain.HermesConfig =
        { ArchiveDir = defaultArchiveDir ()
          Credentials = Path.Combine(configDir (), "gmail_credentials.json")
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
              DocumentIntelligenceKey = "" } }

    // ─── DTO → Domain mapping ────────────────────────────────────────

    let private orDefault (fallback: string) (value: string | null) =
        match value with
        | null -> fallback
        | v -> v

    let private toConfig (dto: HermesConfigDto) : Domain.HermesConfig =
        let def = defaultConfig ()

        let accounts : Domain.AccountConfig list =
            if isNull (box dto.Accounts) || dto.Accounts.Length = 0 then
                def.Accounts
            else
                dto.Accounts
                |> Array.map (fun a ->
                    ({ Label = a.Label |> orDefault ""
                       Provider = a.Provider |> orDefault "gmail" } : Domain.AccountConfig))
                |> Array.toList

        let watchFolders : Domain.WatchFolderConfig list =
            if isNull (box dto.WatchFolders) || dto.WatchFolders.Length = 0 then
                def.WatchFolders
            else
                dto.WatchFolders
                |> Array.map (fun w ->
                    ({ Path = w.Path |> orDefault "" |> expandHome
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

        { ArchiveDir = dto.ArchiveDir |> orDefault def.ArchiveDir |> expandHome
          Credentials = dto.Credentials |> orDefault def.Credentials |> expandHome
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
          Azure = azure }

    // ─── YAML deserializer ───────────────────────────────────────────

    let private deserializer =
        DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()

    // ─── Public API (parameterised over FileSystem algebra) ──────────

    /// Parse a HermesConfig from a YAML string. Pure — no I/O.
    let parseYaml (yaml: string) : Result<Domain.HermesConfig, string> =
        try
            if System.String.IsNullOrWhiteSpace(yaml) then
                Ok(defaultConfig ())
            else
                let dto = deserializer.Deserialize<HermesConfigDto>(yaml)

                if isNull (box dto) then
                    Ok(defaultConfig ())
                else
                    Ok(toConfig dto)
        with ex ->
            Error $"Failed to parse config: {ex.Message}"

    /// Load config from a file, using the FileSystem algebra.
    let load (fs: Algebra.FileSystem) (path: string) =
        task {
            if not (fs.fileExists path) then
                return Error $"Configuration file not found: {path}"
            else
                try
                    let! yaml = fs.readAllText path
                    return parseYaml yaml
                with ex ->
                    return Error $"Failed to load config: {ex.Message}"
        }

    // ─── Default YAML templates ──────────────────────────────────────

    let defaultConfigYaml () =
        $"""archive_dir: ~/Documents/hermes

# Gmail OAuth client credential (shared across accounts)
credentials: {configDir ()}/gmail_credentials.json

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
"""

    /// Write default config & rules files via the FileSystem algebra.
    /// Returns the list of files that were created (skips existing).
    let init (fs: Algebra.FileSystem) =
        task {
            try
                let dir = configDir ()
                fs.createDirectory dir
                fs.createDirectory (Path.Combine(dir, "tokens"))

                let created = ResizeArray<string>()

                let configPath = Path.Combine(dir, "config.yaml")

                if not (fs.fileExists configPath) then
                    do! fs.writeAllText configPath (defaultConfigYaml ())
                    created.Add(configPath)

                let rulesPath = Path.Combine(dir, "rules.yaml")

                if not (fs.fileExists rulesPath) then
                    do! fs.writeAllText rulesPath (defaultRulesYaml ())
                    created.Add(rulesPath)

                return Ok(created |> Seq.toList)
            with ex ->
                return Error $"Failed to initialize config: {ex.Message}"
        }
