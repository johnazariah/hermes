using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Util.Store;
using Hermes.Core;
using Microsoft.Data.Sqlite;
using Microsoft.FSharp.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hermes.App.Views;

public partial class ShellWindow : Window
{
    private readonly HermesServiceBridge _bridge;
    private readonly DispatcherTimer _refreshTimer;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    // Named controls (resolved once after InitializeComponent)
    private TextBlock _ollamaStatus = null!;
    private TextBlock _ollamaModels = null!;
    private TextBlock _indexStats = null!;
    private TextBlock _categorySummary = null!;
    private TextBlock _accountsList = null!;
    private TextBlock _watchFoldersList = null!;
    private TextBlock _lastSyncText = null!;
    private Button _syncNowButton = null!;
    private Button _pauseButton = null!;
    private StackPanel _chatPanel = null!;
    private ScrollViewer _chatScroller = null!;
    private TextBox _chatInput = null!;
    private ToggleButton _aiToggle = null!;

    public ShellWindow(HermesServiceBridge bridge)
    {
        _bridge = bridge;
        InitializeComponent();
        ResolveControls();
        WireUpButtons();
        AddChatBubble("Hermes", "Welcome! Ask me anything about your documents.\nToggle 🧠 for AI-enhanced answers.");

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _refreshTimer.Start();

        _ = RefreshStatusAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void ResolveControls()
    {
        _ollamaStatus = this.FindControl<TextBlock>("OllamaStatus")!;
        _ollamaModels = this.FindControl<TextBlock>("OllamaModels")!;
        _indexStats = this.FindControl<TextBlock>("IndexStats")!;
        _categorySummary = this.FindControl<TextBlock>("CategorySummary")!;
        _accountsList = this.FindControl<TextBlock>("AccountsList")!;
        _watchFoldersList = this.FindControl<TextBlock>("WatchFoldersList")!;
        _lastSyncText = this.FindControl<TextBlock>("LastSyncText")!;
        _syncNowButton = this.FindControl<Button>("SyncNowButton")!;
        _pauseButton = this.FindControl<Button>("PauseButton")!;
        _chatPanel = this.FindControl<StackPanel>("ChatPanel")!;
        _chatScroller = this.FindControl<ScrollViewer>("ChatScroller")!;
        _chatInput = this.FindControl<TextBox>("ChatInput")!;
        _aiToggle = this.FindControl<ToggleButton>("AiToggle")!;
    }

    private void WireUpButtons()
    {
        _syncNowButton.Click += async (_, _) => await HandleSyncNowAsync();
        _pauseButton.Click += (_, _) => HandlePauseToggle();
        this.FindControl<Button>("SettingsButton")!.Click += async (_, _) => await ShowSettingsDialogAsync();
        this.FindControl<Button>("AddAccountButton")!.Click += async (_, _) => await AddGmailAccountAsync();
        this.FindControl<Button>("AddWatchFolderButton")!.Click += async (_, _) => await AddWatchFolderAsync();
        this.FindControl<Button>("SendButton")!.Click += async (_, _) => await HandleSendAsync();
        _chatInput.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) await HandleSendAsync();
        };
    }

    // ── Status refresh (every 5s) ──────────────────────────────────────

    private async Task RefreshStatusAsync()
    {
        try
        {
            await _bridge.RefreshStatusAsync();
        }
        catch
        {
            // Service not running yet — that's fine
        }

        try
        {
            await RefreshOllamaStatusAsync();
            RefreshIndexStats();
            RefreshCategorySummary();
            RefreshAccountsList();
            RefreshWatchFoldersList();
            RefreshLastSync();
        }
        catch
        {
            // Never crash the timer
        }
    }

    private async Task RefreshOllamaStatusAsync()
    {
        var baseUrl = _bridge.Config?.Ollama.BaseUrl ?? "http://localhost:11434";
        try
        {
            var response = await _http.GetAsync($"{baseUrl.TrimEnd('/')}/api/tags");
            if (response.IsSuccessStatusCode)
            {
                _ollamaStatus.Text = "✅ Available";
                var json = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);
                var names = doc.RootElement.GetProperty("models").EnumerateArray()
                    .Select(m => m.GetProperty("name").GetString())
                    .Where(n => n is not null);
                _ollamaModels.Text = string.Join(", ", names);
            }
            else
            {
                _ollamaStatus.Text = "❌ Unavailable";
                _ollamaModels.Text = "";
            }
        }
        catch
        {
            _ollamaStatus.Text = "❌ Unavailable";
            _ollamaModels.Text = "";
        }
    }

    private void RefreshIndexStats()
    {
        var status = _bridge.LastStatus;
        long docCount = status?.DocumentCount ?? 0;

        long extracted = 0, embedded = 0;
        double sizeMb = 0;

        var dbPath = Path.Combine(_bridge.ArchiveDir, "db.sqlite");
        if (File.Exists(dbPath))
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                conn.Open();
                extracted = ScalarInt64(conn, "SELECT COUNT(*) FROM documents WHERE extracted_text IS NOT NULL");
                embedded = ScalarInt64(conn, "SELECT COUNT(*) FROM documents WHERE embedded_at IS NOT NULL");
                sizeMb = new FileInfo(dbPath).Length / (1024.0 * 1024.0);
            }
            catch
            {
                // DB may not exist yet
            }
        }

        _indexStats.Text = $"{docCount} docs · {extracted} extracted · {embedded} embedded\nDB: {sizeMb:F1} MB";
    }

    private void RefreshCategorySummary()
    {
        var archiveDir = _bridge.ArchiveDir;
        if (!Directory.Exists(archiveDir)) { _categorySummary.Text = ""; return; }

        var dirs = Directory.GetDirectories(archiveDir);
        var lines = new List<string>();
        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (name is "unclassified" or ".hermes") continue;
            var count = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
            if (count > 0)
                lines.Add($"  {name,-14} {count,4}");
        }

        _categorySummary.Text = lines.Count > 0 ? string.Join("\n", lines) : "";
    }

    private void RefreshAccountsList()
    {
        var config = _bridge.Config;
        if (config is null || config.Accounts.IsEmpty)
        {
            _accountsList.Text = "No accounts configured.";
            return;
        }

        var dbPath = Path.Combine(_bridge.ArchiveDir, "db.sqlite");
        var msgCounts = new Dictionary<string, int>();
        var syncTimes = new Dictionary<string, string>();

        if (File.Exists(dbPath))
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                conn.Open();
                ReadAccountMsgCounts(conn, msgCounts);
                ReadAccountSyncTimes(conn, syncTimes);
            }
            catch { /* DB may be locked or missing tables */ }
        }

        var lines = new List<string>();
        foreach (var acct in config.Accounts)
        {
            var label = acct.Label;
            var emails = msgCounts.GetValueOrDefault(label, 0);
            var lastSync = syncTimes.GetValueOrDefault(label, "Never");
            lines.Add($"  {label}  ✅ {emails} emails  last: {lastSync}");
        }

        _accountsList.Text = string.Join("\n", lines);
    }

    private void RefreshWatchFoldersList()
    {
        var config = _bridge.Config;
        if (config is null || config.WatchFolders.IsEmpty)
        {
            _watchFoldersList.Text = "None configured.";
            return;
        }

        var lines = config.WatchFolders
            .Select(wf => $"  {wf.Path}  [{string.Join(", ", wf.Patterns)}]");
        _watchFoldersList.Text = string.Join("\n", lines);
    }

    private void RefreshLastSync()
    {
        var status = _bridge.LastStatus;
        if (status is null)
        {
            _lastSyncText.Text = "Last sync: Never";
            return;
        }

        var syncAt = status.LastSyncAt;
        if (syncAt is null || !FSharpOption<DateTimeOffset>.get_IsSome(syncAt))
        {
            _lastSyncText.Text = "Last sync: Never";
            return;
        }

        var ago = DateTimeOffset.UtcNow - syncAt.Value;
        var agoText = ago.TotalMinutes < 1 ? "just now"
            : ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes}m ago"
            : ago.TotalHours < 24 ? $"{(int)ago.TotalHours}h ago"
            : $"{(int)ago.TotalDays}d ago";
        _lastSyncText.Text = $"Last sync: {agoText}";
    }

    // ── Chat ───────────────────────────────────────────────────────────

    private async Task HandleSendAsync()
    {
        var query = _chatInput.Text?.Trim();
        if (string.IsNullOrEmpty(query)) return;

        _chatInput.Text = "";
        AddChatBubble("You", query);

        var useAi = _aiToggle.IsChecked == true;
        var dbPath = Path.Combine(_bridge.ArchiveDir, "db.sqlite");

        if (!File.Exists(dbPath))
        {
            AddChatBubble("Hermes", "No database found. Run a sync first.");
            return;
        }

        try
        {
            var db = Database.fromPath(dbPath);
            var ollamaUrl = _bridge.Config?.Ollama.BaseUrl ?? "http://localhost:11434";
            var response = await Chat.query(db, ollamaUrl, "llama3:8b", useAi, query);

            var text = FormatChatResponse(response, useAi);
            AddChatBubble("Hermes", text);
        }
        catch (Exception ex)
        {
            AddChatBubble("Hermes", $"Search error: {ex.Message}");
        }
    }

    private static string FormatChatResponse(Chat.ChatResponse response, bool useAi)
    {
        var results = response.Results;
        if (results.IsEmpty)
            return "No results found.";

        var lines = new List<string>();

        // AI summary first
        var aiSummary = response.AiSummary;
        if (useAi && aiSummary is not null && FSharpOption<string>.get_IsSome(aiSummary))
        {
            lines.Add(aiSummary.Value);
            lines.Add("");
            lines.Add("Sources:");
        }

        foreach (var r in results)
        {
            var name = FSharpOption<string>.get_IsSome(r.OriginalName) ? r.OriginalName!.Value : Path.GetFileName(r.SavedPath);
            var date = FSharpOption<string>.get_IsSome(r.EmailDate) ? r.EmailDate!.Value : "";
            var amount = FSharpOption<double>.get_IsSome(r.ExtractedAmount) ? $" ${r.ExtractedAmount!.Value:F2}" : "";
            var snippet = FSharpOption<string>.get_IsSome(r.Snippet) ? r.Snippet!.Value : "";
            lines.Add($"📄 {name}  [{r.Category}]  {date}{amount}");
            if (!string.IsNullOrEmpty(snippet))
                lines.Add($"   {snippet}");
        }

        return string.Join("\n", lines);
    }

    private void AddChatBubble(string speaker, string text)
    {
        var bubble = new StackPanel { Spacing = 2, Margin = new Avalonia.Thickness(0, 0, 0, 4) };

        bubble.Children.Add(new TextBlock
        {
            Text = speaker,
            FontWeight = Avalonia.Media.FontWeight.Bold,
            FontSize = 12
        });

        bubble.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 13
        });

        _chatPanel.Children.Add(bubble);
        _chatScroller.ScrollToEnd();
    }

    // ── Sync Now ───────────────────────────────────────────────────────

    private async Task HandleSyncNowAsync()
    {
        _syncNowButton.IsEnabled = false;
        _syncNowButton.Content = "⏳ Syncing...";

        _bridge.RequestSync();

        var startTime = DateTime.UtcNow;
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(1000);
            await RefreshStatusAsync();

            if (_bridge.LastStatus is { } s && s.LastSyncAt is not null
                && FSharpOption<DateTimeOffset>.get_IsSome(s.LastSyncAt)
                && s.LastSyncAt.Value > startTime.ToUniversalTime())
            {
                break;
            }
        }

        await RefreshStatusAsync();
        _syncNowButton.Content = "⟳ Sync Now";
        _syncNowButton.IsEnabled = true;
    }

    // ── Pause ──────────────────────────────────────────────────────────

    private void HandlePauseToggle()
    {
        _bridge.TogglePause();
        _pauseButton.Content = _bridge.IsPaused ? "▶ Resume" : "⏸ Pause";
    }

    // ── Settings dialog ────────────────────────────────────────────────

    private async Task ShowSettingsDialogAsync()
    {
        var config = _bridge.Config;
        var dialog = new Window
        {
            Title = "Hermes Settings",
            Width = 480, Height = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var archiveBox = new TextBox { Text = config?.ArchiveDir ?? _bridge.ArchiveDir, Margin = new Avalonia.Thickness(16, 4, 16, 8) };
        var syncBox = new NumericUpDown { Value = config?.SyncIntervalMinutes ?? 15, Minimum = 1, Maximum = 1440, Margin = new Avalonia.Thickness(16, 4, 16, 8) };
        var sizeBox = new NumericUpDown { Value = (config?.MinAttachmentSize ?? 20480) / 1024, Minimum = 1, Maximum = 10240, Margin = new Avalonia.Thickness(16, 4, 16, 8) };
        var ollamaBox = new TextBox { Text = config?.Ollama.BaseUrl ?? "http://localhost:11434", Margin = new Avalonia.Thickness(16, 4, 16, 8) };
        var statusLbl = new TextBlock { Text = "", Margin = new Avalonia.Thickness(16, 4) };
        var saveBtn = new Button { Content = "Save", Margin = new Avalonia.Thickness(16, 8), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };

        saveBtn.Click += async (_, _) =>
        {
            try
            {
                await _bridge.UpdateConfigAsync(
                    (int)(syncBox.Value ?? 15),
                    (int)(sizeBox.Value ?? 20),
                    ollamaBox.Text ?? "http://localhost:11434");
                statusLbl.Text = "✅ Settings saved.";
            }
            catch (Exception ex)
            {
                statusLbl.Text = $"❌ Error: {ex.Message}";
            }
        };

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Settings", FontSize = 16, FontWeight = Avalonia.Media.FontWeight.Bold, Margin = new Avalonia.Thickness(16, 16, 16, 4) });
        panel.Children.Add(new TextBlock { Text = "Archive path:", Margin = new Avalonia.Thickness(16, 4, 16, 0) });
        panel.Children.Add(archiveBox);
        panel.Children.Add(new TextBlock { Text = "Sync interval (minutes):", Margin = new Avalonia.Thickness(16, 4, 16, 0) });
        panel.Children.Add(syncBox);
        panel.Children.Add(new TextBlock { Text = "Min attachment size (KB):", Margin = new Avalonia.Thickness(16, 4, 16, 0) });
        panel.Children.Add(sizeBox);
        panel.Children.Add(new TextBlock { Text = "Ollama URL:", Margin = new Avalonia.Thickness(16, 4, 16, 0) });
        panel.Children.Add(ollamaBox);
        panel.Children.Add(saveBtn);
        panel.Children.Add(statusLbl);
        dialog.Content = panel;

        await dialog.ShowDialog(this);
    }

    // ── Add Gmail account ──────────────────────────────────────────────

    private async Task AddGmailAccountAsync()
    {
        var dialog = new Window
        {
            Title = "Add Gmail Account",
            Width = 440, Height = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var labelBox = new TextBox { Watermark = "Account label (e.g. john-personal)", Margin = new Avalonia.Thickness(16, 16, 16, 8) };
        var addBtn = new Button { Content = "Authenticate with Google", Margin = new Avalonia.Thickness(16, 8), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        var statusLabel = new TextBlock { Text = "", Margin = new Avalonia.Thickness(16, 4), TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        addBtn.Click += async (_, _) =>
        {
            var label = labelBox.Text?.Trim();
            if (string.IsNullOrEmpty(label))
            {
                statusLabel.Text = "Please enter an account label.";
                return;
            }

            var credPath = Path.Combine(_bridge.ConfigDir, "gmail_credentials.json");
            if (!File.Exists(credPath))
            {
                statusLabel.Text = $"Missing credentials file:\n{credPath}\n\nDownload from Google Cloud Console → OAuth 2.0 Client IDs and save there.";
                return;
            }

            statusLabel.Text = "Opening browser for Google authentication…";
            addBtn.IsEnabled = false;

            try
            {
                ClientSecrets clientSecrets;
                await using (var stream = File.OpenRead(credPath))
                    clientSecrets = (await GoogleClientSecrets.FromStreamAsync(stream)).Secrets;

                var tokenDir = Path.Combine(_bridge.ConfigDir, "tokens");
                Directory.CreateDirectory(tokenDir);

                await GoogleWebAuthorizationBroker.AuthorizeAsync(
                    clientSecrets,
                    new[] { GmailService.Scope.GmailReadonly, GmailService.Scope.GmailModify },
                    label,
                    CancellationToken.None,
                    new FileDataStore(tokenDir, true));

                await _bridge.AddGmailAccountToConfigAsync(label);
                statusLabel.Text = $"✅ Account '{label}' authenticated successfully!";
            }
            catch (Exception ex)
            {
                statusLabel.Text = $"❌ Authentication failed:\n{ex.Message}";
            }
            finally
            {
                addBtn.IsEnabled = true;
            }
        };

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Add Gmail Account", FontSize = 16, FontWeight = Avalonia.Media.FontWeight.Bold, Margin = new Avalonia.Thickness(16, 16, 16, 4) });
        panel.Children.Add(new TextBlock { Text = "Enter a friendly label for this account:", Margin = new Avalonia.Thickness(16, 0, 16, 0) });
        panel.Children.Add(labelBox);
        panel.Children.Add(addBtn);
        panel.Children.Add(statusLabel);
        dialog.Content = panel;

        await dialog.ShowDialog(this);
    }

    // ── Add watch folder ───────────────────────────────────────────────

    private async Task AddWatchFolderAsync()
    {
        try
        {
            var folderDialog = await StorageProvider.OpenFolderPickerAsync(
                new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "Select folder to watch",
                    AllowMultiple = false
                });

            if (folderDialog.Count == 0) return;

            var folder = folderDialog[0].Path.LocalPath;
            var patternWindow = new Window
            {
                Title = "Watch Folder Patterns",
                Width = 420, Height = 220,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };

            var patternBox = new TextBox { Text = "*.pdf", Watermark = "Patterns (comma-separated)", Margin = new Avalonia.Thickness(16, 16, 16, 8) };
            var okBtn = new Button { Content = "Add Watch Folder", Margin = new Avalonia.Thickness(16, 8), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            var statusLbl = new TextBlock { Text = "", Margin = new Avalonia.Thickness(16, 4) };

            okBtn.Click += async (_, _) =>
            {
                var patterns = patternBox.Text ?? "*.pdf";
                okBtn.IsEnabled = false;
                statusLbl.Text = "Saving…";

                try
                {
                    await _bridge.AddWatchFolderToConfigAsync(folder, patterns);
                    patternWindow.Close();
                }
                catch (Exception ex)
                {
                    statusLbl.Text = $"Error: {ex.Message}";
                    okBtn.IsEnabled = true;
                }
            };

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = $"Watching: {folder}", FontSize = 14, FontWeight = Avalonia.Media.FontWeight.Bold, Margin = new Avalonia.Thickness(16, 16, 16, 4) });
            panel.Children.Add(new TextBlock { Text = "Enter file patterns to watch for:", Margin = new Avalonia.Thickness(16, 0) });
            panel.Children.Add(patternBox);
            panel.Children.Add(okBtn);
            panel.Children.Add(statusLbl);
            patternWindow.Content = panel;

            await patternWindow.ShowDialog(this);
        }
        catch (Exception ex)
        {
            var errWin = new Window { Title = "Error", Width = 350, Height = 120, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            errWin.Content = new TextBlock { Text = $"Could not open folder picker:\n{ex.Message}", Margin = new Avalonia.Thickness(16) };
            await errWin.ShowDialog(this);
        }
    }

    // ── DB helpers ─────────────────────────────────────────────────────

    private static long ScalarInt64(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result is long l ? l : 0;
    }

    private static void ReadAccountMsgCounts(SqliteConnection conn, Dictionary<string, int> counts)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT account, COUNT(*) FROM messages GROUP BY account";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            counts[reader.GetString(0)] = reader.GetInt32(1);
    }

    private static void ReadAccountSyncTimes(SqliteConnection conn, Dictionary<string, string> times)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT account, last_sync_at FROM sync_state";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            times[reader.GetString(0)] = reader.IsDBNull(1) ? "Never" : reader.GetString(1);
    }

    // ── Lifecycle ──────────────────────────────────────────────────────

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        _http.Dispose();
        base.OnClosed(e);
    }
}
