using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Util.Store;
using Hermes.App.ViewModels;
using Hermes.Core;
using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Path = System.IO.Path;

namespace Hermes.App.Views;

public partial class ShellWindow : Window
{
    private readonly ShellViewModel _vm;
    private readonly DispatcherTimer _refreshTimer;

    // Resolved controls
    private Ellipse _ollamaDot = null!;
    private TextBlock _ollamaStatusText = null!;
    private TextBlock _ollamaModelsText = null!;
    private TextBlock _indexDocCount = null!;
    private ProgressBar _extractedBar = null!;
    private TextBlock _extractedCount = null!;
    private ProgressBar _embeddedBar = null!;
    private TextBlock _embeddedCount = null!;
    private TextBlock _categoryText = null!;
    private TextBlock _accountsText = null!;
    private TextBlock _watchFoldersText = null!;
    private TextBlock _lastSyncText = null!;
    private Button _syncNowButton = null!;
    private Button _pauseButton = null!;
    private StackPanel _chatPanel = null!;
    private ScrollViewer _chatScroller = null!;
    private TextBox _chatInput = null!;
    private ToggleButton _aiToggle = null!;
    private WrapPanel _suggestedQueries = null!;
    private Ellipse _statusDot = null!;
    private TextBlock _statusBarText = null!;

    public ShellWindow(HermesServiceBridge bridge)
    {
        _vm = new ShellViewModel(bridge);
        InitializeComponent();
        ResolveControls();
        WireUpEvents();

        _vm.AddWelcomeMessage();
        _vm.Messages.CollectionChanged += OnMessagesChanged;
        RenderAllMessages();

        _vm.PropertyChanged += (_, e) => Dispatcher.UIThread.Post(() => OnViewModelChanged(e.PropertyName));

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += async (_, _) => await _vm.RefreshAsync();
        _refreshTimer.Start();

        _ = _vm.RefreshAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void ResolveControls()
    {
        _ollamaDot = this.FindControl<Ellipse>("OllamaDot")!;
        _ollamaStatusText = this.FindControl<TextBlock>("OllamaStatusText")!;
        _ollamaModelsText = this.FindControl<TextBlock>("OllamaModelsText")!;
        _indexDocCount = this.FindControl<TextBlock>("IndexDocCount")!;
        _extractedBar = this.FindControl<ProgressBar>("ExtractedBar")!;
        _extractedCount = this.FindControl<TextBlock>("ExtractedCount")!;
        _embeddedBar = this.FindControl<ProgressBar>("EmbeddedBar")!;
        _embeddedCount = this.FindControl<TextBlock>("EmbeddedCount")!;
        _categoryText = this.FindControl<TextBlock>("CategoryText")!;
        _accountsText = this.FindControl<TextBlock>("AccountsText")!;
        _watchFoldersText = this.FindControl<TextBlock>("WatchFoldersText")!;
        _lastSyncText = this.FindControl<TextBlock>("LastSyncText")!;
        _syncNowButton = this.FindControl<Button>("SyncNowButton")!;
        _pauseButton = this.FindControl<Button>("PauseButton")!;
        _chatPanel = this.FindControl<StackPanel>("ChatPanel")!;
        _chatScroller = this.FindControl<ScrollViewer>("ChatScroller")!;
        _chatInput = this.FindControl<TextBox>("ChatInput")!;
        _aiToggle = this.FindControl<ToggleButton>("AiToggle")!;
        _suggestedQueries = this.FindControl<WrapPanel>("SuggestedQueries")!;
        _statusDot = this.FindControl<Ellipse>("StatusDot")!;
        _statusBarText = this.FindControl<TextBlock>("StatusBarText")!;
    }

    private void WireUpEvents()
    {
        _syncNowButton.Click += async (_, _) =>
        {
            _syncNowButton.IsEnabled = false;
            _syncNowButton.Content = "⏳ Syncing...";
            await _vm.SyncNowAsync();
            _syncNowButton.Content = "⟳ Sync Now";
            _syncNowButton.IsEnabled = true;
        };

        _pauseButton.Click += (_, _) =>
        {
            _vm.TogglePause();
            _pauseButton.Content = _vm.IsPaused ? "▶ Resume" : "⏸ Pause";
        };

        this.FindControl<Button>("SettingsButton")!.Click += async (_, _) => await ShowSettingsDialogAsync();
        this.FindControl<Button>("AddAccountButton")!.Click += async (_, _) => await AddGmailAccountAsync();
        this.FindControl<Button>("AddWatchFolderButton")!.Click += async (_, _) => await AddWatchFolderAsync();
        this.FindControl<Button>("SendButton")!.Click += async (_, _) => await HandleSendAsync();

        _chatInput.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) await HandleSendAsync();
        };

        _aiToggle.IsCheckedChanged += (_, _) => _vm.AiEnabled = _aiToggle.IsChecked == true;

        // Wire up suggested query chips
        foreach (var child in _suggestedQueries.Children.OfType<Button>())
        {
            var chipText = child.Content?.ToString() ?? "";
            child.Click += async (_, _) =>
            {
                _chatInput.Text = chipText;
                await HandleSendAsync();
            };
        }
    }

    // ── ViewModel → View sync ──────────────────────────────────────

    private void OnViewModelChanged(string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(ShellViewModel.OllamaStatus):
                var ollama = _vm.OllamaStatus;
                _ollamaDot.Fill = new SolidColorBrush(ollama.IsAvailable ? Color.Parse("#4CAF50") : Color.Parse("#F44336"));
                _ollamaStatusText.Text = ollama.IsAvailable ? "Available" : "Unavailable";
                _ollamaModelsText.Text = ollama.Models.Count > 0 ? string.Join(", ", ollama.Models) : "";
                break;

            case nameof(ShellViewModel.IndexStats):
                var stats = _vm.IndexStats;
                if (stats is null) break;
                var docs = stats.DocumentCount;
                _indexDocCount.Text = $"{docs:N0} documents";
                _extractedBar.Maximum = Math.Max(docs, 1);
                _extractedBar.Value = stats.ExtractedCount;
                _extractedCount.Text = $"{stats.ExtractedCount:N0}/{docs:N0}";
                _embeddedBar.Maximum = Math.Max(docs, 1);
                _embeddedBar.Value = stats.EmbeddedCount;
                _embeddedCount.Text = $"{stats.EmbeddedCount:N0}/{docs:N0}";
                break;

            case nameof(ShellViewModel.Categories):
                var cats = _vm.Categories;
                _categoryText.Text = cats.Count == 0 ? "" : string.Join("\n", cats.Select(c => $"{c.Category,-14} {c.Count,4}"));
                break;

            case nameof(ShellViewModel.Accounts):
                var accts = _vm.Accounts;
                _accountsText.Text = accts.Count == 0
                    ? "No accounts configured."
                    : string.Join("\n", accts.Select(a => $"● {a.Label}  {a.MessageCount} emails"));
                break;

            case nameof(ShellViewModel.LastSyncText):
                _lastSyncText.Text = _vm.LastSyncText;
                break;

            case nameof(ShellViewModel.StatusBarText):
                _statusBarText.Text = _vm.StatusBarText;
                break;

            case nameof(ShellViewModel.IsSyncing):
                _statusDot.Fill = new SolidColorBrush(_vm.IsSyncing ? Color.Parse("#2196F3") : Color.Parse("#4CAF50"));
                break;

            case nameof(ShellViewModel.IsSearching):
                // Could animate the send button or show a spinner
                break;
        }

        // Update watch folders display
        if (propertyName == nameof(ShellViewModel.StatusBarText))
        {
            var wf = _vm.WatchFolders;
            _watchFoldersText.Text = wf.Count == 0
                ? "None configured."
                : string.Join("\n", wf.Select(f => $"{f.Path}  [{string.Join(", ", f.Patterns)}]"));
        }
    }

    // ── Chat rendering ─────────────────────────────────────────────

    private async Task HandleSendAsync()
    {
        var query = _chatInput.Text?.Trim();
        if (string.IsNullOrEmpty(query)) return;

        _chatInput.Text = "";
        _suggestedQueries.IsVisible = false;

        await _vm.SendMessageAsync(query);
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (ChatMessage msg in e.NewItems)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _chatPanel.Children.Add(CreateMessageBubble(msg));
                    _chatScroller.ScrollToEnd();
                });
            }
        }
        else if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            Dispatcher.UIThread.Post(() => _chatPanel.Children.Clear());
        }
    }

    private void RenderAllMessages()
    {
        _chatPanel.Children.Clear();
        foreach (var msg in _vm.Messages)
            _chatPanel.Children.Add(CreateMessageBubble(msg));
    }

    private static Control CreateMessageBubble(ChatMessage msg)
    {
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8),
            Margin = msg.IsUser
                ? new Thickness(60, 0, 0, 0)
                : new Thickness(0, 0, 60, 0),
            Background = new SolidColorBrush(msg.IsUser
                ? Color.Parse("#1A4FC3F7")
                : Color.Parse("#0A000000")),
            HorizontalAlignment = msg.IsUser
                ? Avalonia.Layout.HorizontalAlignment.Right
                : Avalonia.Layout.HorizontalAlignment.Left,
        };

        var content = new StackPanel { Spacing = 4 };

        // Speaker label
        content.Children.Add(new TextBlock
        {
            Text = msg.Speaker,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#888"))
        });

        // Message text
        content.Children.Add(new TextBlock
        {
            Text = msg.Text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        });

        // Document cards
        foreach (var doc in msg.Documents)
        {
            content.Children.Add(CreateDocumentCard(doc));
        }

        bubble.Child = content;
        return bubble;
    }

    private static Control CreateDocumentCard(DocumentCard doc)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderBrush = new SolidColorBrush(Color.Parse("#20000000")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 4, 0, 0),
            Background = new SolidColorBrush(Color.Parse("#06000000")),
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        var stack = new StackPanel { Spacing = 2 };

        // File name + category
        var header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new TextBlock
        {
            Text = $"📄 {doc.FileName}",
            FontSize = 12,
            FontWeight = FontWeight.Medium
        });

        var categoryBadge = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#E3F2FD")),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        categoryBadge.Child = new TextBlock
        {
            Text = doc.Category,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse("#1565C0"))
        };
        header.Children.Add(categoryBadge);
        stack.Children.Add(header);

        // Date + amount
        var meta = new System.Collections.Generic.List<string>();
        if (doc.Date is not null) meta.Add(doc.Date);
        if (doc.Amount is not null) meta.Add(doc.Amount);
        if (meta.Count > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", meta),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#888"))
            });
        }

        // Snippet
        if (!string.IsNullOrWhiteSpace(doc.Snippet))
        {
            stack.Children.Add(new TextBlock
            {
                Text = doc.Snippet,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#666")),
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2
            });
        }

        card.Child = stack;

        // Click to open file
        card.PointerPressed += (_, _) =>
        {
            var fullPath = doc.SavedPath;
            if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath))
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    Process.Start("open", fullPath);
            }
        };

        return card;
    }

    // ── Settings dialog ────────────────────────────────────────────

    private async Task ShowSettingsDialogAsync()
    {
        var config = _vm.Bridge.Config;
        var dialog = new Window
        {
            Title = "Hermes Settings",
            Width = 480,
            Height = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var syncBox = new NumericUpDown { Value = config?.SyncIntervalMinutes ?? 15, Minimum = 1, Maximum = 1440, Margin = new Thickness(16, 4, 16, 8) };
        var sizeBox = new NumericUpDown { Value = (config?.MinAttachmentSize ?? 20480) / 1024, Minimum = 1, Maximum = 10240, Margin = new Thickness(16, 4, 16, 8) };
        var ollamaBox = new TextBox { Text = config?.Ollama.BaseUrl ?? "http://localhost:11434", Margin = new Thickness(16, 4, 16, 8) };
        var statusLbl = new TextBlock { Text = "", Margin = new Thickness(16, 4) };
        var saveBtn = new Button { Content = "Save", Margin = new Thickness(16, 8), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };

        saveBtn.Click += async (_, _) =>
        {
            try
            {
                await _vm.Bridge.UpdateConfigAsync(
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
        panel.Children.Add(new TextBlock { Text = "Settings", FontSize = 16, FontWeight = FontWeight.Bold, Margin = new Thickness(16, 16, 16, 4) });
        panel.Children.Add(new TextBlock { Text = "Sync interval (minutes):", Margin = new Thickness(16, 4, 16, 0) });
        panel.Children.Add(syncBox);
        panel.Children.Add(new TextBlock { Text = "Min attachment size (KB):", Margin = new Thickness(16, 4, 16, 0) });
        panel.Children.Add(sizeBox);
        panel.Children.Add(new TextBlock { Text = "Ollama URL:", Margin = new Thickness(16, 4, 16, 0) });
        panel.Children.Add(ollamaBox);
        panel.Children.Add(saveBtn);
        panel.Children.Add(statusLbl);
        dialog.Content = panel;

        await dialog.ShowDialog(this);
    }

    // ── Add Gmail account ──────────────────────────────────────────

    private async Task AddGmailAccountAsync()
    {
        var dialog = new Window
        {
            Title = "Add Gmail Account",
            Width = 440,
            Height = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var labelBox = new TextBox { Watermark = "Account label (e.g. john-personal)", Margin = new Thickness(16, 16, 16, 8) };
        var addBtn = new Button { Content = "Authenticate with Google", Margin = new Thickness(16, 8), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        var statusLabel = new TextBlock { Text = "", Margin = new Thickness(16, 4), TextWrapping = TextWrapping.Wrap };

        addBtn.Click += async (_, _) =>
        {
            var label = labelBox.Text?.Trim();
            if (string.IsNullOrEmpty(label))
            {
                statusLabel.Text = "Please enter an account label.";
                return;
            }

            var credPath = Path.Combine(_vm.Bridge.ConfigDir, "gmail_credentials.json");
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

                var tokenDir = Path.Combine(_vm.Bridge.ConfigDir, "tokens");
                Directory.CreateDirectory(tokenDir);

                await GoogleWebAuthorizationBroker.AuthorizeAsync(
                    clientSecrets,
                    [GmailService.Scope.GmailReadonly, GmailService.Scope.GmailModify],
                    label,
                    CancellationToken.None,
                    new FileDataStore(tokenDir, true));

                await _vm.Bridge.AddGmailAccountToConfigAsync(label);
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
        panel.Children.Add(new TextBlock { Text = "Add Gmail Account", FontSize = 16, FontWeight = FontWeight.Bold, Margin = new Thickness(16, 16, 16, 4) });
        panel.Children.Add(new TextBlock { Text = "Enter a friendly label for this account:", Margin = new Thickness(16, 0, 16, 0) });
        panel.Children.Add(labelBox);
        panel.Children.Add(addBtn);
        panel.Children.Add(statusLabel);
        dialog.Content = panel;

        await dialog.ShowDialog(this);
    }

    // ── Add watch folder ───────────────────────────────────────────

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
                Width = 420,
                Height = 220,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };

            var patternBox = new TextBox { Text = "*.pdf", Watermark = "Patterns (comma-separated)", Margin = new Thickness(16, 16, 16, 8) };
            var okBtn = new Button { Content = "Add Watch Folder", Margin = new Thickness(16, 8), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            var statusLbl = new TextBlock { Text = "", Margin = new Thickness(16, 4) };

            okBtn.Click += async (_, _) =>
            {
                var patterns = patternBox.Text ?? "*.pdf";
                okBtn.IsEnabled = false;
                statusLbl.Text = "Saving…";

                try
                {
                    await _vm.Bridge.AddWatchFolderToConfigAsync(folder, patterns);
                    patternWindow.Close();
                }
                catch (Exception ex)
                {
                    statusLbl.Text = $"Error: {ex.Message}";
                    okBtn.IsEnabled = true;
                }
            };

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = $"Watching: {folder}", FontSize = 14, FontWeight = FontWeight.Bold, Margin = new Thickness(16, 16, 16, 4) });
            panel.Children.Add(new TextBlock { Text = "Enter file patterns to watch for:", Margin = new Thickness(16, 0) });
            panel.Children.Add(patternBox);
            panel.Children.Add(okBtn);
            panel.Children.Add(statusLbl);
            patternWindow.Content = panel;

            await patternWindow.ShowDialog(this);
        }
        catch (Exception ex)
        {
            var errWin = new Window { Title = "Error", Width = 350, Height = 120, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            errWin.Content = new TextBlock { Text = $"Could not open folder picker:\n{ex.Message}", Margin = new Thickness(16) };
            await errWin.ShowDialog(this);
        }
    }

    // ── Lifecycle ──────────────────────────────────────────────────

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        _vm.Dispose();
        base.OnClosed(e);
    }
}
