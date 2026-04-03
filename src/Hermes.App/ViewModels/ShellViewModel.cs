using Hermes.Core;
using Microsoft.FSharp.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace Hermes.App.ViewModels;

/// <summary>
/// Represents a single chat message (user or Hermes response).
/// </summary>
public sealed record ChatMessage(
    string Speaker,
    string Text,
    bool IsUser,
    IReadOnlyList<DocumentCard> Documents);

/// <summary>
/// A structured document search result for display as a card.
/// </summary>
public sealed record DocumentCard(
    string FileName,
    string Category,
    string? Date,
    string? Amount,
    string? Snippet,
    string SavedPath);

/// <summary>
/// A reminder/action item for display in the TODO panel.
/// </summary>
public sealed record ReminderItem(
    long Id,
    string? Vendor,
    string? Amount,
    string? DueDate,
    string DueLabel,
    bool IsOverdue,
    string? DocumentPath,
    string? FileName);

/// <summary>
/// Ollama connection status.
/// </summary>
public sealed record OllamaStatus(bool IsAvailable, IReadOnlyList<string> Models);

/// <summary>
/// ViewModel for the shell window. Owns all status and chat state.
/// UI binds to properties and calls commands — no DB or HTTP in code-behind.
/// </summary>
public sealed class ShellViewModel : INotifyPropertyChanged
{
    private readonly HermesServiceBridge _bridge;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public ShellViewModel(HermesServiceBridge bridge)
    {
        _bridge = bridge;
    }

    // ── INotifyPropertyChanged ─────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    // ── Observable state ───────────────────────────────────────────

    private OllamaStatus _ollamaStatus = new(false, []);
    public OllamaStatus OllamaStatus
    {
        get => _ollamaStatus;
        private set => Set(ref _ollamaStatus, value);
    }

    private Stats.IndexStats? _indexStats;
    public Stats.IndexStats? IndexStats
    {
        get => _indexStats;
        private set => Set(ref _indexStats, value);
    }

    private IReadOnlyList<Stats.CategoryCount> _categories = [];
    public IReadOnlyList<Stats.CategoryCount> Categories
    {
        get => _categories;
        private set => Set(ref _categories, value);
    }

    private IReadOnlyList<Stats.AccountStats> _accounts = [];
    public IReadOnlyList<Stats.AccountStats> Accounts
    {
        get => _accounts;
        private set => Set(ref _accounts, value);
    }

    private string _lastSyncText = "Last sync: Never";
    public string LastSyncText
    {
        get => _lastSyncText;
        private set => Set(ref _lastSyncText, value);
    }

    private string _statusBarText = "Starting...";
    public string StatusBarText
    {
        get => _statusBarText;
        private set => Set(ref _statusBarText, value);
    }

    private bool _isSyncing;
    public bool IsSyncing
    {
        get => _isSyncing;
        private set => Set(ref _isSyncing, value);
    }

    // Chat state
    public ObservableCollection<ChatMessage> Messages { get; } = [];

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        private set => Set(ref _isSearching, value);
    }

    private bool _aiEnabled;
    public bool AiEnabled
    {
        get => _aiEnabled;
        set => Set(ref _aiEnabled, value);
    }

    /// <summary>
    /// Returns the name of the active chat provider for display in the UI.
    /// </summary>
    public string ChatProviderName
    {
        get
        {
            var chat = _bridge.Config?.Chat;
            if (chat is null) return "Ollama (default)";
            return chat.Provider.IsAzureOpenAI
                ? $"Azure OpenAI ({chat.AzureOpenAI.DeploymentName})"
                : $"Ollama ({_bridge.Config?.Ollama.InstructModel ?? "llama3.2"})";
        }
    }

    // Bridge accessors for the view
    public HermesServiceBridge Bridge => _bridge;
    public IReadOnlyList<Domain.WatchFolderConfig> WatchFolders
        => _bridge.Config?.WatchFolders.ToList() ?? [];

    // Reminder state
    public ObservableCollection<ReminderItem> OverdueReminders { get; } = [];
    public ObservableCollection<ReminderItem> UpcomingReminders { get; } = [];

    private int _actionItemCount;
    public int ActionItemCount
    {
        get => _actionItemCount;
        private set => Set(ref _actionItemCount, value);
    }

    private NavigatorMode _activeMode = NavigatorMode.ActionItems;
    public NavigatorMode ActiveMode
    {
        get => _activeMode;
        set => Set(ref _activeMode, value);
    }

    // ── Refresh all status ─────────────────────────────────────────

    public async Task RefreshAsync()
    {
        try { await _bridge.RefreshStatusAsync(); } catch { /* service may not be running */ }

        try
        {
            await RefreshOllamaAsync();
            await RefreshIndexStatsAsync();
            RefreshCategories();
            await RefreshAccountsAsync();
            await RefreshRemindersAsync();
            RefreshLastSync();
            RefreshStatusBar();
        }
        catch { /* never crash the timer */ }
    }

    private async Task RefreshOllamaAsync()
    {
        var baseUrl = _bridge.Config?.Ollama.BaseUrl ?? "http://localhost:11434";
        try
        {
            var response = await _http.GetAsync($"{baseUrl.TrimEnd('/')}/api/tags");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);
                var names = doc.RootElement.GetProperty("models").EnumerateArray()
                    .Select(m => m.GetProperty("name").GetString())
                    .Where(n => n is not null)
                    .Select(n => n!)
                    .ToList();
                OllamaStatus = new OllamaStatus(true, names);
            }
            else
            {
                OllamaStatus = new OllamaStatus(false, []);
            }
        }
        catch
        {
            OllamaStatus = new OllamaStatus(false, []);
        }
    }

    private async Task RefreshIndexStatsAsync()
    {
        var dbPath = Path.Combine(_bridge.ArchiveDir, "db.sqlite");
        if (!File.Exists(dbPath)) return;

        try
        {
            var db = Database.fromPath(dbPath);
            try
            {
                IndexStats = await Stats.getIndexStats(db, Interpreters.realFileSystem, dbPath);
            }
            finally
            {
                db.dispose.Invoke(null!);
            }
        }
        catch { /* DB may not exist yet */ }
    }

    private void RefreshCategories()
    {
        var counts = Stats.getCategoryCounts(Interpreters.realFileSystem, _bridge.ArchiveDir);
        Categories = counts.ToList();
    }

    private async Task RefreshAccountsAsync()
    {
        var dbPath = Path.Combine(_bridge.ArchiveDir, "db.sqlite");
        if (!File.Exists(dbPath)) return;

        try
        {
            var db = Database.fromPath(dbPath);
            try
            {
                var stats = await Stats.getAccountStats(db);
                Accounts = stats.ToList();
            }
            finally
            {
                db.dispose.Invoke(null!);
            }
        }
        catch { /* DB may not exist */ }
    }

    private void RefreshLastSync()
    {
        var status = _bridge.LastStatus;
        if (status is null)
        {
            LastSyncText = "Last sync: Never";
            return;
        }

        var syncAt = status.LastSyncAt;
        if (syncAt is null || !FSharpOption<DateTimeOffset>.get_IsSome(syncAt))
        {
            LastSyncText = "Last sync: Never";
            return;
        }

        var ago = DateTimeOffset.UtcNow - syncAt.Value;
        var agoText = ago.TotalMinutes < 1 ? "just now"
            : ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes}m ago"
            : ago.TotalHours < 24 ? $"{(int)ago.TotalHours}h ago"
            : $"{(int)ago.TotalDays}d ago";
        LastSyncText = $"Last sync: {agoText}";
    }

    private void RefreshStatusBar()
    {
        var stats = IndexStats;
        var docCount = stats?.DocumentCount ?? 0;
        var extracted = stats?.ExtractedCount ?? 0;
        var embedded = stats?.EmbeddedCount ?? 0;
        var sizeMb = stats?.DatabaseSizeMb ?? 0.0;

        var stateText = IsSyncing ? "Syncing" : "Ready";
        StatusBarText = $"{stateText} · {docCount:N0} docs · {extracted:N0} extracted · {embedded:N0} embedded · DB {sizeMb:F1} MB";
    }

    // ── Chat commands ──────────────────────────────────────────────

    public void AddWelcomeMessage()
    {
        Messages.Add(new ChatMessage(
            "Hermes",
            "Welcome! Ask me anything about your documents.\nToggle AI for enhanced answers.",
            false,
            []));
    }

    public async Task SendMessageAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;

        // Add user message
        Messages.Add(new ChatMessage("You", query, true, []));

        var dbPath = Path.Combine(_bridge.ArchiveDir, "db.sqlite");
        if (!File.Exists(dbPath))
        {
            Messages.Add(new ChatMessage("Hermes", "No database found. Run a sync first.", false, []));
            return;
        }

        IsSearching = true;
        try
        {
            var db = Database.fromPath(dbPath);
            try
            {
                var ollamaUrl = _bridge.Config?.Ollama.BaseUrl ?? "http://localhost:11434";
                var model = _bridge.Config?.Ollama.InstructModel ?? "llama3.2";
                var chatConfig = _bridge.Config?.Chat;
                using var httpClient = new System.Net.Http.HttpClient();
                var chatProvider = chatConfig is not null
                    ? Chat.providerFromConfig(httpClient, chatConfig, ollamaUrl, model)
                    : Chat.ollamaProvider(httpClient, ollamaUrl, model);
                var response = await Chat.query(db, chatProvider, AiEnabled, query);

                var documents = response.Results
                    .Select(r => new DocumentCard(
                        FileName: FSharpOption<string>.get_IsSome(r.OriginalName)
                            ? r.OriginalName!.Value
                            : Path.GetFileName(r.SavedPath),
                        Category: r.Category,
                        Date: FSharpOption<string>.get_IsSome(r.EmailDate) ? r.EmailDate!.Value : null,
                        Amount: FSharpOption<double>.get_IsSome(r.ExtractedAmount)
                            ? $"${r.ExtractedAmount!.Value:F2}"
                            : null,
                        Snippet: FSharpOption<string>.get_IsSome(r.Snippet) ? r.Snippet!.Value : null,
                        SavedPath: r.SavedPath))
                    .ToList();

                var aiText = AiEnabled
                    && response.AiSummary is not null
                    && FSharpOption<string>.get_IsSome(response.AiSummary)
                        ? response.AiSummary.Value
                        : null;

                var text = documents.Count == 0
                    ? "No results found."
                    : aiText ?? $"Found {documents.Count} document(s):";

                Messages.Add(new ChatMessage("Hermes", text, false, documents));
            }
            finally
            {
                db.dispose.Invoke(null!);
            }
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessage("Hermes", $"Search error: {ex.Message}", false, []));
        }
        finally
        {
            IsSearching = false;
        }
    }

    // ── Actions ────────────────────────────────────────────────────

    public async Task SyncNowAsync()
    {
        IsSyncing = true;
        await _bridge.RequestSyncAsync();

        var startTime = DateTimeOffset.UtcNow;
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(1000);
            await RefreshAsync();

            if (_bridge.LastStatus is { } s && s.LastSyncAt is not null
                && FSharpOption<DateTimeOffset>.get_IsSome(s.LastSyncAt)
                && s.LastSyncAt.Value > startTime)
                break;
        }

        await RefreshAsync();
        IsSyncing = false;
    }

    public void TogglePause() => _bridge.TogglePause();
    public bool IsPaused => _bridge.IsPaused;

    // ── Navigation state ──────────────────────────────────────────

    private bool _isChatPaneVisible = true;
    public bool IsChatPaneVisible
    {
        get => _isChatPaneVisible;
        set => Set(ref _isChatPaneVisible, value);
    }

    public void ToggleChatPane() => IsChatPaneVisible = !IsChatPaneVisible;

    // ── Navigation stack ──────────────────────────────────────────

    public sealed record NavigationItem(string Kind, long Id, string Label);

    private readonly Stack<NavigationItem> _navigationStack = new();

    public NavigationItem? CurrentItem => _navigationStack.Count > 0 ? _navigationStack.Peek() : null;

    public bool CanNavigateBack => _navigationStack.Count > 1;

    public IReadOnlyList<NavigationItem> BreadcrumbItems => _navigationStack.Reverse().ToList();

    public void NavigateTo(NavigationItem item)
    {
        _navigationStack.Push(item);
        OnPropertyChanged(nameof(CurrentItem));
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(BreadcrumbItems));
    }

    public void NavigateBack()
    {
        if (_navigationStack.Count <= 1) return;
        _navigationStack.Pop();
        OnPropertyChanged(nameof(CurrentItem));
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(BreadcrumbItems));
    }

    // ── Reminder actions ───────────────────────────────────────────

    public async Task MarkPaidAsync(long reminderId)
    {
        var dbPath = Path.Combine(_bridge.ArchiveDir, "db.sqlite");
        if (!File.Exists(dbPath)) return;
        var db = Database.fromPath(dbPath);
        try { await Reminders.markCompleted(db, reminderId, DateTimeOffset.UtcNow); }
        finally { db.dispose.Invoke(null!); }
        await RefreshRemindersAsync();
    }

    public async Task SnoozeAsync(long reminderId, int days = 7)
    {
        var dbPath = Path.Combine(_bridge.ArchiveDir, "db.sqlite");
        if (!File.Exists(dbPath)) return;
        var db = Database.fromPath(dbPath);
        try { await Reminders.snooze(db, reminderId, days, DateTimeOffset.UtcNow); }
        finally { db.dispose.Invoke(null!); }
        await RefreshRemindersAsync();
    }

    public async Task DismissAsync(long reminderId)
    {
        var dbPath = Path.Combine(_bridge.ArchiveDir, "db.sqlite");
        if (!File.Exists(dbPath)) return;
        var db = Database.fromPath(dbPath);
        try { await Reminders.dismiss(db, reminderId, DateTimeOffset.UtcNow); }
        finally { db.dispose.Invoke(null!); }
        await RefreshRemindersAsync();
    }

    private async Task RefreshRemindersAsync()
    {
        var dbPath = Path.Combine(_bridge.ArchiveDir, "db.sqlite");
        if (!File.Exists(dbPath)) return;

        try
        {
            var db = Database.fromPath(dbPath);
            try
            {
                var now = DateTimeOffset.UtcNow;
                var active = await Reminders.getActive(db, now);

                OverdueReminders.Clear();
                UpcomingReminders.Clear();

                foreach (var (reminder, savedPath, originalName) in active)
                {
                    var dueOpt = reminder.DueDate;
                    var hasDue = FSharpOption<DateTimeOffset>.get_IsSome(dueOpt);
                    var dueVal = hasDue ? dueOpt!.Value : DateTimeOffset.MinValue;
                    var isOverdue = hasDue && dueVal < now;
                    var dueLabel = hasDue
                        ? (isOverdue
                            ? $"{(int)(now - dueVal).TotalDays} days overdue"
                            : $"in {(int)(dueVal - now).TotalDays} days")
                        : "no due date";

                    var vendorStr = FSharpOption<string>.get_IsSome(reminder.Vendor) ? reminder.Vendor!.Value : null;
                    var amountStr = FSharpOption<decimal>.get_IsSome(reminder.Amount) ? $"${reminder.Amount!.Value:F2}" : null;
                    var pathStr = FSharpOption<string>.get_IsSome(savedPath) ? savedPath!.Value : null;
                    var nameStr = FSharpOption<string>.get_IsSome(originalName) ? originalName!.Value : null;

                    var item = new ReminderItem(
                        Id: reminder.Id,
                        Vendor: vendorStr,
                        Amount: amountStr,
                        DueDate: hasDue ? dueVal.ToString("dd MMM yyyy") : null,
                        DueLabel: dueLabel,
                        IsOverdue: isOverdue,
                        DocumentPath: pathStr,
                        FileName: nameStr);

                    if (isOverdue)
                        OverdueReminders.Add(item);
                    else
                        UpcomingReminders.Add(item);
                }

                ActionItemCount = OverdueReminders.Count + UpcomingReminders.Count;
            }
            finally
            {
                db.dispose.Invoke(null!);
            }
        }
        catch { /* DB may not exist */ }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
