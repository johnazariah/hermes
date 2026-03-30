using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Util.Store;
using Hermes.Core;
using Microsoft.FSharp.Core;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Hermes.App.Views;

public partial class ShellWindow : Window
{
    private readonly HermesServiceBridge _bridge;
    private readonly DispatcherTimer _refreshTimer;

    public ShellWindow(HermesServiceBridge bridge)
    {
        _bridge = bridge;
        InitializeComponent();
        LoadCurrentValues();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _refreshTimer.Start();

        _ = RefreshStatusAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void LoadCurrentValues()
    {
        var config = _bridge.Config;
        var archiveBox = this.FindControl<TextBox>("ArchiveLocationBox");
        var syncBox = this.FindControl<NumericUpDown>("SyncIntervalBox");
        var sizeBox = this.FindControl<NumericUpDown>("MinSizeBox");
        var ollamaBox = this.FindControl<TextBox>("OllamaUrlBox");
        var archivePath = this.FindControl<TextBlock>("ArchivePath");

        if (archivePath is not null)
            archivePath.Text = _bridge.ArchiveDir;

        if (config is not null)
        {
            if (archiveBox is not null) archiveBox.Text = config.ArchiveDir;
            if (syncBox is not null) syncBox.Value = config.SyncIntervalMinutes;
            if (sizeBox is not null) sizeBox.Value = config.MinAttachmentSize / 1024;
            if (ollamaBox is not null) ollamaBox.Text = config.Ollama.BaseUrl;
        }

        // Set category summary immediately (synchronous) so it's never "Loading..."
        var categorySummary = this.FindControl<TextBlock>("CategorySummary");
        if (categorySummary is not null)
            categorySummary.Text = BuildCategorySummary();

        var saveBtn = this.FindControl<Button>("SaveSettingsButton");
        if (saveBtn is not null)
            saveBtn.Click += async (_, _) => await SaveSettingsAsync();

        var addAccountBtn = this.FindControl<Button>("AddAccountButton");
        if (addAccountBtn is not null)
            addAccountBtn.Click += async (_, _) => await AddGmailAccountAsync();

        var addWatchBtn = this.FindControl<Button>("AddWatchFolderButton");
        if (addWatchBtn is not null)
            addWatchBtn.Click += async (_, _) => await AddWatchFolderAsync();

        var syncNowBtn = this.FindControl<Button>("SyncNowButton");
        if (syncNowBtn is not null)
            syncNowBtn.Click += (_, _) =>
            {
                _bridge.RequestSync();
                var statusText = this.FindControl<TextBlock>("StatusText");
                if (statusText is not null) statusText.Text = "Sync requested — processing in background…";
            };
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            await _bridge.RefreshStatusAsync();
        }
        catch
        {
            // Service not running yet or heartbeat missing — that's fine
        }

        var status = _bridge.LastStatus;

        var statusText = this.FindControl<TextBlock>("StatusText");
        var docCount = this.FindControl<TextBlock>("DocumentCount");
        var unclassified = this.FindControl<TextBlock>("UnclassifiedCount");
        var lastSync = this.FindControl<TextBlock>("LastSyncTime");
        var startedAt = this.FindControl<TextBlock>("StartedAt");
        var ollamaStatus = this.FindControl<TextBlock>("OllamaStatus");
        var categorySummary = this.FindControl<TextBlock>("CategorySummary");

        if (statusText is not null) statusText.Text = _bridge.StatusText;

        if (status is { } s)
        {
            if (docCount is not null) docCount.Text = $"{s.DocumentCount:N0}";
            if (unclassified is not null) unclassified.Text = $"{s.UnclassifiedCount}";

            if (lastSync is not null)
            {
                var syncAt = s.LastSyncAt;
                lastSync.Text = (syncAt is not null && FSharpOption<DateTimeOffset>.get_IsSome(syncAt))
                    ? syncAt.Value.ToString("yyyy-MM-dd HH:mm:ss")
                    : "Never";
            }

            if (startedAt is not null)
            {
                var started = s.StartedAt;
                startedAt.Text = (started is not null && FSharpOption<DateTimeOffset>.get_IsSome(started))
                    ? started.Value.ToString("yyyy-MM-dd HH:mm:ss")
                    : "—";
            }
        }

        // Category summary — always update even if service isn't running
        if (categorySummary is not null)
            categorySummary.Text = BuildCategorySummary();

        // Check Ollama
        if (ollamaStatus is not null)
        {
            var ollamaUrl = _bridge.Config?.Ollama.BaseUrl ?? "http://localhost:11434";
            ollamaStatus.Text = await CheckOllamaAsync(ollamaUrl) ? "✅ Available" : "❌ Unavailable";
        }
    }

    private string BuildCategorySummary()
    {
        try
        {
            var archiveDir = _bridge.ArchiveDir;
            if (!System.IO.Directory.Exists(archiveDir)) return "Archive not found.";

            var dirs = System.IO.Directory.GetDirectories(archiveDir);
            if (dirs.Length == 0) return "No categories yet.";

            var lines = new System.Collections.Generic.List<string>();
            lines.Add($"{"Category",-20} {"Files",6}");
            lines.Add(new string('─', 28));

            foreach (var dir in dirs)
            {
                var name = System.IO.Path.GetFileName(dir);
                var files = System.IO.Directory.GetFiles(dir, "*", System.IO.SearchOption.AllDirectories);
                var count = files.Length;
                if (name != "db.sqlite")
                    lines.Add($"{name,-20} {count,6}");
            }

            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static async Task<bool> CheckOllamaAsync(string baseUrl)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await client.GetAsync($"{baseUrl.TrimEnd('/')}/api/tags");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task SaveSettingsAsync()
    {
        var syncBox = this.FindControl<NumericUpDown>("SyncIntervalBox");
        var sizeBox = this.FindControl<NumericUpDown>("MinSizeBox");
        var ollamaBox = this.FindControl<TextBox>("OllamaUrlBox");

        var syncInterval = (int)(syncBox?.Value ?? 15);
        var minSizeKb = (int)(sizeBox?.Value ?? 20);
        var ollamaUrl = ollamaBox?.Text ?? "http://localhost:11434";

        try
        {
            await _bridge.UpdateConfigAsync(syncInterval, minSizeKb, ollamaUrl);
            var statusText = this.FindControl<TextBlock>("StatusText");
            if (statusText is not null) statusText.Text = "Settings saved.";
        }
        catch (Exception ex)
        {
            var statusText = this.FindControl<TextBlock>("StatusText");
            if (statusText is not null) statusText.Text = $"Error saving settings: {ex.Message}";
        }
    }

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

                var accountsList = this.FindControl<TextBlock>("AccountsList");
                if (accountsList is not null)
                {
                    var existing = accountsList.Text == "No accounts configured." ? "" : accountsList.Text + "\n";
                    accountsList.Text = existing + label;
                }

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

            // Pattern dialog
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

                    var watchList = this.FindControl<TextBlock>("WatchFoldersList");
                    if (watchList is not null)
                    {
                        var current = watchList.Text == "None configured." ? "" : watchList.Text + "\n";
                        watchList.Text = current + $"{folder}  [{patterns}]";
                    }
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

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        base.OnClosed(e);
    }
}
