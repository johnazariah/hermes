using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hermes.UI.Models;

namespace Hermes.UI.Services;

/// <summary>
/// IHermesClient implementation that calls the Hermes HTTP API.
/// Used in MAUI Hybrid mode and as the default for Blazor Server during migration.
/// </summary>
public sealed class HttpHermesClient : IHermesClient
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public HttpHermesClient(HttpClient http) => _http = http;

    public async Task<IndexStats> GetStatsAsync()
        => await _http.GetFromJsonAsync<IndexStats>("/api/stats", JsonOptions)
           ?? new IndexStats(0, 0, 0, 0, 0, 0, 0, 0);

    public async Task<PipelineState> GetPipelineStateAsync()
        => await _http.GetFromJsonAsync<PipelineState>("/api/pipeline", JsonOptions)
           ?? new PipelineState(0, 0, 0, 0, 0, 0, null, "");

    public async Task<List<ActivityEntry>> GetActivityAsync(int limit = 30)
        => await _http.GetFromJsonAsync<List<ActivityEntry>>($"/api/activity?limit={limit}", JsonOptions)
           ?? [];

    public async Task<List<CategoryCount>> GetCategoriesAsync()
        => await _http.GetFromJsonAsync<List<CategoryCount>>("/api/categories", JsonOptions)
           ?? [];

    public async Task<List<DocumentSummary>> GetDocumentsAsync(string? category = null, int offset = 0, int limit = 50)
    {
        var url = $"/api/documents?offset={offset}&limit={limit}";
        if (!string.IsNullOrEmpty(category))
            url += $"&category={Uri.EscapeDataString(category)}";
        return await _http.GetFromJsonAsync<List<DocumentSummary>>(url, JsonOptions) ?? [];
    }

    public async Task<DocumentDetail?> GetDocumentAsync(long id)
        => await _http.GetFromJsonAsync<DocumentDetail>($"/api/documents/{id}", JsonOptions);

    public async Task<string?> GetDocumentContentAsync(long id)
    {
        var result = await _http.GetFromJsonAsync<JsonElement>($"/api/documents/{id}/content", JsonOptions);
        return result.TryGetProperty("markdown", out var md) ? md.GetString() : null;
    }

    public Task<string?> GetDocumentFileUrlAsync(long id)
        => Task.FromResult<string?>($"/api/documents/{id}/file");

    public async Task<List<SearchResult>> SearchAsync(string query, int limit = 50)
        => await _http.GetFromJsonAsync<List<SearchResult>>(
               $"/api/search?q={Uri.EscapeDataString(query)}&limit={limit}", JsonOptions)
           ?? [];

    public async Task<ChatMessage> ChatAsync(string query, bool useAi = true)
    {
        var payload = new { query, useAi };
        var response = await _http.PostAsJsonAsync("/api/chat", payload, JsonOptions);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ChatMessage>(JsonOptions)
               ?? new ChatMessage("assistant", "No response.");
    }

    public async Task<string> GetSettingsYamlAsync()
        => await _http.GetStringAsync("/api/settings");

    public async Task SaveSettingsYamlAsync(string yaml)
    {
        var content = new StringContent(yaml, Encoding.UTF8, "text/yaml");
        var response = await _http.PutAsync("/api/settings", content);
        response.EnsureSuccessStatusCode();
    }

    public async Task<List<SyncAccount>> GetSyncAccountsAsync()
        => await _http.GetFromJsonAsync<List<SyncAccount>>("/api/sync/accounts", JsonOptions)
           ?? [];

    public async Task TriggerSyncAsync()
        => await _http.PostAsync("/api/sync", null);
}
