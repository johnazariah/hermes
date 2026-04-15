namespace Hermes.UI.Models;

/// <summary>Pipeline processing state snapshot.</summary>
public sealed record PipelineState(
    int IngestQueueDepth,
    int ExtractQueueDepth,
    int DeadLetterCount,
    int TotalDocuments,
    int TotalExtracted,
    int TotalEmbedded,
    string? CurrentDoc,
    string LastUpdated);

/// <summary>Category with document count.</summary>
public sealed record CategoryCount(string Category, int Count);

/// <summary>Lightweight document summary for list views.</summary>
public sealed record DocumentSummary(
    long Id,
    string OriginalName,
    string Category,
    string? ExtractedDate,
    decimal? ExtractedAmount,
    string? Sender,
    string? Vendor,
    string? SourceType,
    string? Account,
    string? SourcePath);

/// <summary>Pipeline processing status flags.</summary>
public sealed record PipelineStatus(
    bool Understood,
    bool Extracted,
    bool Embedded);

/// <summary>Full document detail for detail views.</summary>
public sealed record DocumentDetail(
    DocumentSummary Summary,
    string? ExtractedText,
    string? Comprehension,
    string FilePath,
    string? Vendor,
    string IngestedAt,
    string? ExtractedAt,
    string? EmbeddedAt,
    PipelineStatus PipelineStatus);

/// <summary>Index statistics for the dashboard.</summary>
public sealed record IndexStats(
    int DocumentCount,
    int ExtractedCount,
    int UnderstoodCount,
    int EmbeddedCount,
    int AwaitingExtract,
    int AwaitingUnderstand,
    int AwaitingEmbed,
    double DatabaseSizeMb);

/// <summary>Activity log entry.</summary>
public sealed record ActivityEntry(
    string Timestamp,
    string Stage,
    string FileName,
    string? Status);

/// <summary>Search result item.</summary>
public sealed record SearchResult(
    long Id,
    string OriginalName,
    string? Category,
    string? Sender,
    string Snippet,
    double Score);

/// <summary>Chat message in the conversation.</summary>
public sealed record ChatMessage(
    string Role,
    string Content,
    List<SearchResult>? Sources = null);

/// <summary>Email sync account info.</summary>
public sealed record SyncAccount(
    string Email,
    string Status,
    int MessageCount);
