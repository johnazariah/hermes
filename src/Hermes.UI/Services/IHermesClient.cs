using Hermes.UI.Models;

namespace Hermes.UI.Services;

/// <summary>
/// Abstraction over Hermes data access. Two implementations:
/// <list type="bullet">
///   <item><description>DirectHermesClient — in-process, calls F# Core via DI (Blazor Server)</description></item>
///   <item><description>HttpHermesClient — calls localhost HTTP API (MAUI Hybrid)</description></item>
/// </list>
/// </summary>
public interface IHermesClient
{
    // ── Stats / Dashboard ───────────────────────────────────────────
    Task<IndexStats> GetStatsAsync();
    Task<PipelineState> GetPipelineStateAsync();
    Task<List<ActivityEntry>> GetActivityAsync(int limit = 30);

    // ── Categories ──────────────────────────────────────────────────
    Task<List<CategoryCount>> GetCategoriesAsync();

    // ── Documents ───────────────────────────────────────────────────
    Task<List<DocumentSummary>> GetDocumentsAsync(string? category = null, string? stage = null, int offset = 0, int limit = 50);
    Task<DocumentDetail?> GetDocumentAsync(long id);
    Task<string?> GetDocumentContentAsync(long id);
    Task<string?> GetDocumentFileUrlAsync(long id);

    // ── Search ──────────────────────────────────────────────────────
    Task<List<SearchResult>> SearchAsync(string query, int limit = 50);

    // ── Chat ────────────────────────────────────────────────────────
    Task<ChatMessage> ChatAsync(string query, bool useAi = true);

    // ── Corrections ─────────────────────────────────────────────────
    Task CorrectDocumentAsync(long id, List<FieldCorrection> corrections, string? note = null);
    Task RecomprehendDocumentAsync(long id);

    // ── Settings ────────────────────────────────────────────────────
    Task<string> GetSettingsYamlAsync();
    Task SaveSettingsYamlAsync(string yaml);
    Task<List<SyncAccount>> GetSyncAccountsAsync();
    Task TriggerSyncAsync();
}
