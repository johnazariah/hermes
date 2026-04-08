namespace Hermes.Core

open System

/// Core domain types for Hermes document intelligence.
[<RequireQualifiedAccess>]
module Domain =

    /// How a document entered the system.
    type SourceType =
        | EmailAttachment
        | WatchedFolder
        | ManualDrop

    module SourceType =
        let toString =
            function
            | EmailAttachment -> "email_attachment"
            | WatchedFolder -> "watched_folder"
            | ManualDrop -> "manual_drop"

        let fromString =
            function
            | "email_attachment" -> Ok EmailAttachment
            | "watched_folder" -> Ok WatchedFolder
            | "manual_drop" -> Ok ManualDrop
            | other -> Error $"Unknown source type: {other}"

    /// A processed email message (dedup boundary).
    type Message =
        { GmailId: string
          Account: string
          Sender: string option
          Subject: string option
          Date: DateTimeOffset option
          LabelIds: string list
          HasAttachments: bool
          ProcessedAt: DateTimeOffset }

    /// A file stored in the archive.
    type Document =
        { Id: int64
          SourceType: SourceType
          GmailId: string option
          Account: string option
          Sender: string option
          Subject: string option
          EmailDate: DateTimeOffset option
          OriginalName: string option
          SavedPath: string
          Category: string
          MimeType: string option
          SizeBytes: int64 option
          Sha256: string
          SourcePath: string option
          ExtractedText: string option
          ExtractedDate: string option
          ExtractedAmount: decimal option
          ExtractedVendor: string option
          ExtractedAbn: string option
          OcrConfidence: float option
          ExtractionMethod: string option
          ExtractedAt: DateTimeOffset option
          EmbeddedAt: DateTimeOffset option
          ChunkCount: int option
          IngestedAt: DateTimeOffset }

    /// Per-account sync state for incremental Gmail sync.
    type SyncState =
        { Account: string
          LastHistoryId: string option
          LastSyncAt: DateTimeOffset option
          MessageCount: int }

    /// Schema version for database migrations.
    type SchemaVersion = { Version: int; AppliedAt: DateTimeOffset }

    /// Gmail account configuration.
    type BackfillConfig =
        { Enabled: bool
          Since: DateTimeOffset option
          BatchSize: int
          AttachmentsOnly: bool
          IncludeBodies: bool }

    type AccountConfig =
        { Label: string
          Provider: string
          Backfill: BackfillConfig }

    /// Watched folder configuration.
    type WatchFolderConfig =
        { Path: string
          Patterns: string list }

    /// Ollama AI configuration.
    type OllamaConfig =
        { Enabled: bool
          BaseUrl: string
          EmbeddingModel: string
          VisionModel: string
          InstructModel: string }

    /// Fallback configuration when Ollama is unavailable.
    type FallbackConfig =
        { Embedding: string
          Ocr: string }

    /// Azure Document Intelligence configuration.
    type AzureConfig =
        { DocumentIntelligenceEndpoint: string
          DocumentIntelligenceKey: string }

    /// Azure OpenAI configuration for cloud-based chat.
    type AzureOpenAIConfig =
        { Endpoint: string
          ApiKey: string
          DeploymentName: string
          MaxTokens: int
          TimeoutSeconds: int }

    /// Which chat provider to use.
    type ChatProviderKind =
        | Ollama
        | AzureOpenAI

    module ChatProviderKind =
        let fromString =
            function
            | "ollama" -> Ok Ollama
            | "azure-openai" | "azure_openai" | "azureopenai" -> Ok AzureOpenAI
            | other -> Error $"Unknown chat provider: {other}"

        let toString =
            function
            | Ollama -> "ollama"
            | AzureOpenAI -> "azure-openai"

    /// Chat configuration: which provider + provider-specific settings.
    type ChatConfig =
        { Provider: ChatProviderKind
          AzureOpenAI: AzureOpenAIConfig }

    // ─── Reminders ───────────────────────────────────────────────────

    type ReminderStatus =
        | Active
        | Snoozed
        | Completed
        | Dismissed

    module ReminderStatus =
        let toString = function
            | Active -> "active"
            | Snoozed -> "snoozed"
            | Completed -> "completed"
            | Dismissed -> "dismissed"

        let fromString = function
            | "active" -> Active
            | "snoozed" -> Snoozed
            | "completed" -> Completed
            | "dismissed" -> Dismissed
            | _ -> Active

    type Reminder =
        { Id: int64
          DocumentId: int64 option
          Vendor: string option
          Amount: decimal option
          DueDate: DateTimeOffset option
          Category: string
          Status: ReminderStatus
          SnoozedUntil: DateTimeOffset option
          CreatedAt: DateTimeOffset
          CompletedAt: DateTimeOffset option }

    type ReminderSummary =
        { OverdueCount: int
          UpcomingCount: int
          TotalActiveAmount: decimal }

    /// Root configuration record.
    type HermesConfig =
        { ArchiveDir: string
          Credentials: string
          Accounts: AccountConfig list
          SyncIntervalMinutes: int
          MinAttachmentSize: int
          WatchFolders: WatchFolderConfig list
          Ollama: OllamaConfig
          Fallback: FallbackConfig
          Azure: AzureConfig
          Chat: ChatConfig }

    // ─── Email sync domain types ─────────────────────────────────────

    /// A Gmail message returned by the provider.
    type EmailMessage =
        { ProviderId: string
          ThreadId: string
          Sender: string option
          Subject: string option
          Date: DateTimeOffset option
          Labels: string list
          HasAttachments: bool
          BodyText: string option }

    /// An attachment downloaded from an email.
    type EmailAttachment =
        { FileName: string
          MimeType: string
          SizeBytes: int64
          Content: byte array }

    /// Sidecar metadata written alongside each downloaded attachment.
    type SidecarMetadata =
        { SourceType: string
          Account: string
          GmailId: string
          ThreadId: string
          Sender: string option
          Subject: string option
          EmailDate: string option
          OriginalName: string
          SavedAs: string
          Sha256: string
          DownloadedAt: string }

    // ─── Classification domain types ─────────────────────────────────

    /// Which rule type matched during classification.
    type ClassificationRule =
        | DomainRule of ruleName: string * domain: string
        | FilenameRule of ruleName: string * pattern: string
        | SubjectRule of ruleName: string * pattern: string
        | DefaultRule

    /// Result of classifying a document.
    type ClassificationResult =
        { Category: string
          MatchedRule: ClassificationRule }

    module ClassificationRule =
        let describe =
            function
            | DomainRule(name, domain) -> $"domain rule '{name}' (domain={domain})"
            | FilenameRule(name, pattern) -> $"filename rule '{name}' (pattern={pattern})"
            | SubjectRule(name, pattern) -> $"subject rule '{name}' (pattern={pattern})"
            | DefaultRule -> "default (unsorted)"

    // ─── Content classification (Tier 2) ─────────────────────────────

    type ContentMatch =
        | ContentAny of keywords: string list
        | ContentAll of keywords: string list
        | HasTable
        | TableHeadersAny of headers: string list
        | TableHeadersAll of headers: string list
        | HasAmount
        | HasDate

    type ContentRule =
        { Name: string
          Conditions: ContentMatch list
          Category: string
          Confidence: float }

    // ─── Pipeline types ──────────────────────────────────────────────

    /// A document that permanently failed a pipeline stage.
    type DeadLetter =
        { DocId: int64
          Stage: string
          Error: string
          Retryable: bool
          FailedAt: DateTimeOffset
          RetryCount: int
          OriginalName: string }

    /// Result of extracting text and metadata from a document.
    type ExtractionResult =
        { Text: string
          Markdown: string option
          Date: string option
          Amount: decimal option
          Vendor: string option
          Abn: string option
          Method: string
          OcrConfidence: float option }
