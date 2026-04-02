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
    private ToggleButton _chatTabButton = null!;
    private ToggleButton _todoTabButton = null!;
    private ScrollViewer _todoScroller = null!;
    private StackPanel _todoPanel = null!;
    // Activity bar + navigator
    private Button _navActionItems = null!;
    private Button _navDocuments = null!;
    private Button _navThreads = null!;
    private Button _navTimeline = null!;
    private Button _navActivity = null!;
    private Button _navSettingsBtn = null!;
    private TextBlock _navigatorTitle = null!;

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
        _chatTabButton = this.FindControl<ToggleButton>("ChatTabButton")!;
        _todoTabButton = this.FindControl<ToggleButton>("TodoTabButton")!;
        _todoScroller = this.FindControl<ScrollViewer>("TodoScroller")!;
        _todoPanel = this.FindControl<StackPanel>("TodoPanel")!;
        // Activity bar + navigator
        _navActionItems = this.FindControl<Button>("NavActionItems")!;
        _navDocuments = this.FindControl<Button>("NavDocuments")!;
        _navThreads = this.FindControl<Button>("NavThreads")!;
        _navTimeline = this.FindControl<Button>("NavTimeline")!;
        _navActivity = this.FindControl<Button>("NavActivity")!;
        _navSettingsBtn = this.FindControl<Button>("NavSettingsBtn")!;
        _navigatorTitle = this.FindControl<TextBlock>("NavigatorTitle")!;
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

        // Tab toggle: Chat ↔ TODO
        _chatTabButton.Click += (_, _) =>
        {
            _chatTabButton.IsChecked = true;
            _todoTabButton.IsChecked = false;
            _chatScroller.IsVisible = true;
            _todoScroller.IsVisible = false;
        };
        _todoTabButton.Click += (_, _) =>
        {
            _todoTabButton.IsChecked = true;
            _chatTabButton.IsChecked = false;
            _chatScroller.IsVisible = false;
            _todoScroller.IsVisible = true;
            RebuildTodoPanel();
        };

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

        // Activity bar navigation
        _navActionItems.Click += (_, _) => SetActiveMode(NavigatorMode.ActionItems);
        _navDocuments.Click += (_, _) => SetActiveMode(NavigatorMode.Documents);
        _navThreads.Click += (_, _) => SetActiveMode(NavigatorMode.Threads);
        _navTimeline.Click += (_, _) => SetActiveMode(NavigatorMode.Timeline);
        _navActivity.Click += (_, _) => SetActiveMode(NavigatorMode.Activity);
        _navSettingsBtn.Click += async (_, _) => await ShowSettingsDialogAsync();
    }

    private void SetActiveMode(NavigatorMode mode)
    {
        _vm.ActiveMode = mode;
        _navigatorTitle.Text = mode switch
        {
            NavigatorMode.ActionItems => "ACTION ITEMS",
            NavigatorMode.Documents => "DOCUMENTS",
            NavigatorMode.Threads => "EMAIL THREADS",
            NavigatorMode.Timeline => "TIMELINE",
            NavigatorMode.Activity => "ACTIVITY",
            _ => "HERMES",
        };
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
                break;

            case nameof(ShellViewModel.ActionItemCount):
                _todoTabButton.Content = _vm.ActionItemCount > 0
                    ? $"📋 Action Items ({_vm.ActionItemCount})"
                    : "📋 Action Items";
                if (_todoScroller.IsVisible) RebuildTodoPanel();
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

    // ── TODO / Action Items panel ────────────────────────────────

    private void RebuildTodoPanel()
    {
        _todoPanel.Children.Clear();

        if (_vm.OverdueReminders.Count == 0 && _vm.UpcomingReminders.Count == 0)
        {
            _todoPanel.Children.Add(new TextBlock
            {
                Text = "✅ All clear — no bills or reminders",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.Parse("#888")),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0)
            });
            return;
        }

        if (_vm.OverdueReminders.Count > 0)
        {
            _todoPanel.Children.Add(new TextBlock
            {
                Text = "⚠ OVERDUE",
                FontSize = 12,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.Parse("#D32F2F")),
                Margin = new Thickness(0, 0, 0, 4)
            });

            foreach (var r in _vm.OverdueReminders)
                _todoPanel.Children.Add(CreateReminderCard(r, "#D32F2F"));
        }

        if (_vm.UpcomingReminders.Count > 0)
        {
            _todoPanel.Children.Add(new TextBlock
            {
                Text = "📋 UPCOMING",
                FontSize = 12,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.Parse("#F9A825")),
                Margin = new Thickness(0, 12, 0, 4)
            });

            foreach (var r in _vm.UpcomingReminders)
                _todoPanel.Children.Add(CreateReminderCard(r, "#F9A825"));
        }
    }

    private Control CreateReminderCard(ReminderItem item, string accentColor)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderBrush = new SolidColorBrush(Color.Parse(accentColor)),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(12, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Background = new SolidColorBrush(Color.Parse("#06000000"))
        };

        var stack = new StackPanel { Spacing = 4 };

        // Vendor + Amount header
        var header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new TextBlock
        {
            Text = item.Vendor ?? "Unknown",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold
        });
        if (item.Amount is not null)
        {
            header.Children.Add(new TextBlock
            {
                Text = item.Amount,
                FontSize = 13,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.Parse(accentColor))
            });
        }
        stack.Children.Add(header);

        // Due date + relative label
        if (item.DueDate is not null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"Due: {item.DueDate} ({item.DueLabel})",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#888"))
            });
        }

        // Document link
        if (item.FileName is not null)
        {
            var docLink = new TextBlock
            {
                Text = $"📄 {item.FileName}",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#1565C0")),
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            if (item.DocumentPath is not null)
            {
                docLink.PointerPressed += (_, _) =>
                {
                    if (File.Exists(item.DocumentPath))
                    {
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                            Process.Start(new ProcessStartInfo(item.DocumentPath) { UseShellExecute = true });
                        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                            Process.Start("open", item.DocumentPath);
                    }
                };
            }
            stack.Children.Add(docLink);
        }

        // Action buttons
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };

        var paidBtn = new Button { Content = "Mark Paid ✓", FontSize = 11, Padding = new Thickness(8, 3) };
        paidBtn.Click += async (_, _) => { paidBtn.IsEnabled = false; await _vm.MarkPaidAsync(item.Id); };
        buttons.Children.Add(paidBtn);

        var snoozeBtn = new Button { Content = "Snooze 7d ⏰", FontSize = 11, Padding = new Thickness(8, 3) };
        snoozeBtn.Click += async (_, _) => { snoozeBtn.IsEnabled = false; await _vm.SnoozeAsync(item.Id); };
        buttons.Children.Add(snoozeBtn);

        var dismissBtn = new Button { Content = "Dismiss ×", FontSize = 11, Padding = new Thickness(8, 3) };
        dismissBtn.Click += async (_, _) => { dismissBtn.IsEnabled = false; await _vm.DismissAsync(item.Id); };
        buttons.Children.Add(dismissBtn);

        stack.Children.Add(buttons);

        card.Child = stack;
        return card;
    }

    // ── Settings dialog ────────────────────────────────────────────

    private async Task ShowSettingsDialogAsync()
    {
        var config = _vm.Bridge.Config;
        if (config is null) return;

        var dialog = new Window
        {
            Title = "Hermes Settings",
            Width = 520,
            Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true
        };

        var statusLbl = new TextBlock { Text = "", Margin = new Thickness(0, 4, 0, 0), FontSize = 12 };
        var root = new StackPanel { Spacing = 6, Margin = new Thickness(20) };

        // ── Title ──
        root.Children.Add(new TextBlock
        {
            Text = "⚙ Settings",
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 8)
        });

        // ── Section: General ──
        root.Children.Add(SettingsSectionHeader("General"));

        root.Children.Add(new TextBlock { Text = "Sync interval (minutes):", FontSize = 12 });
        var syncBox = new NumericUpDown
        {
            Value = config.SyncIntervalMinutes,
            Minimum = 1,
            Maximum = 1440,
            Margin = new Thickness(0, 2, 0, 6)
        };
        root.Children.Add(syncBox);

        root.Children.Add(new TextBlock { Text = "Min attachment size (KB):", FontSize = 12 });
        var sizeBox = new NumericUpDown
        {
            Value = config.MinAttachmentSize / 1024,
            Minimum = 0,
            Maximum = 10240,
            Margin = new Thickness(0, 2, 0, 6)
        };
        root.Children.Add(sizeBox);

        // ── Section: AI / Chat ──
        root.Children.Add(SettingsSectionHeader("AI / Chat"));

        var isOllama = config.Chat.Provider.IsOllama;
        var ollamaRadio = new RadioButton
        {
            Content = "Ollama",
            GroupName = "ChatProvider",
            IsChecked = isOllama
        };
        var azureRadio = new RadioButton
        {
            Content = "Azure OpenAI",
            GroupName = "ChatProvider",
            IsChecked = !isOllama
        };
        var radioRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 16,
            Margin = new Thickness(0, 2, 0, 6)
        };
        radioRow.Children.Add(ollamaRadio);
        radioRow.Children.Add(azureRadio);
        root.Children.Add(radioRow);

        // Ollama fields
        var ollamaPanel = new StackPanel { Spacing = 4, IsVisible = isOllama };
        ollamaPanel.Children.Add(new TextBlock { Text = "Ollama URL:", FontSize = 12 });
        var ollamaUrlBox = new TextBox { Text = config.Ollama.BaseUrl };
        ollamaPanel.Children.Add(ollamaUrlBox);
        ollamaPanel.Children.Add(new TextBlock { Text = "Ollama Model:", FontSize = 12 });
        var ollamaModelBox = new TextBox { Text = config.Ollama.InstructModel };
        ollamaPanel.Children.Add(ollamaModelBox);
        root.Children.Add(ollamaPanel);

        // Azure fields
        var azurePanel = new StackPanel { Spacing = 4, IsVisible = !isOllama };
        azurePanel.Children.Add(new TextBlock { Text = "Endpoint:", FontSize = 12 });
        var azureEndpointBox = new TextBox { Text = config.Chat.AzureOpenAI.Endpoint };
        azurePanel.Children.Add(azureEndpointBox);
        azurePanel.Children.Add(new TextBlock { Text = "API Key:", FontSize = 12 });
        var azureApiKeyBox = new TextBox
        {
            Text = config.Chat.AzureOpenAI.ApiKey,
            PasswordChar = '●'
        };
        azurePanel.Children.Add(azureApiKeyBox);
        azurePanel.Children.Add(new TextBlock { Text = "Deployment:", FontSize = 12 });
        var azureDeploymentBox = new TextBox { Text = config.Chat.AzureOpenAI.DeploymentName };
        azurePanel.Children.Add(azureDeploymentBox);
        root.Children.Add(azurePanel);

        // Toggle visibility on radio change
        ollamaRadio.IsCheckedChanged += (_, _) =>
        {
            ollamaPanel.IsVisible = ollamaRadio.IsChecked == true;
            azurePanel.IsVisible = ollamaRadio.IsChecked != true;
        };

        // ── Section: Accounts ──
        root.Children.Add(SettingsSectionHeader("Accounts"));

        var accountBackfill = new System.Collections.Generic.List<(string Label, CheckBox Enabled, NumericUpDown BatchSize)>();
        var accountsPanel = new StackPanel { Spacing = 8 };

        foreach (var acct in config.Accounts)
        {
            var card = new Border
            {
                BorderBrush = new SolidColorBrush(Color.Parse("#20000000")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8),
                Margin = new Thickness(0, 2)
            };

            var cardContent = new StackPanel { Spacing = 6 };

            // Row 1: label + buttons
            var headerRow = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 8
            };
            headerRow.Children.Add(new TextBlock
            {
                Text = $"● {acct.Label} ({acct.Provider})",
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                FontSize = 13
            });

            var acctLabel = acct.Label;

            var reauthBtn = new Button { Content = "Re-auth", FontSize = 11, Padding = new Thickness(6, 2) };
            reauthBtn.Click += async (_, _) =>
            {
                reauthBtn.IsEnabled = false;
                statusLbl.Text = "Opening browser for authentication…";
                var credPath = Path.Combine(_vm.Bridge.ConfigDir, "gmail_credentials.json");
                if (!File.Exists(credPath))
                {
                    statusLbl.Text = $"❌ Missing: {credPath}";
                    reauthBtn.IsEnabled = true;
                    return;
                }
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
                        acctLabel,
                        CancellationToken.None,
                        new FileDataStore(tokenDir, true));
                    statusLbl.Text = $"✅ Re-authenticated '{acctLabel}'";
                }
                catch (Exception ex)
                {
                    statusLbl.Text = $"❌ Auth failed: {ex.Message}";
                }
                reauthBtn.IsEnabled = true;
            };
            headerRow.Children.Add(reauthBtn);

            var removeBtn = new Button
            {
                Content = "Remove ×",
                FontSize = 11,
                Padding = new Thickness(6, 2),
                Foreground = new SolidColorBrush(Color.Parse("#F44336"))
            };
            removeBtn.Click += async (_, _) =>
            {
                var confirmed = await ShowConfirmDialogAsync(dialog, "Remove Account",
                    $"Remove account '{acctLabel}'?\nThis will delete saved tokens.");
                if (!confirmed) return;
                await _vm.Bridge.RemoveAccountFromConfigAsync(acctLabel, true);
                dialog.Close();
                await ShowSettingsDialogAsync();
            };
            headerRow.Children.Add(removeBtn);
            cardContent.Children.Add(headerRow);

            // Row 2: backfill controls
            var backfillRow = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 8
            };
            var bfToggle = new CheckBox
            {
                Content = "Backfill",
                IsChecked = acct.Backfill.Enabled,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                FontSize = 12
            };
            backfillRow.Children.Add(bfToggle);
            backfillRow.Children.Add(new TextBlock
            {
                Text = "Batch:",
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                FontSize = 12
            });
            var bfBatch = new NumericUpDown
            {
                Value = acct.Backfill.BatchSize,
                Minimum = 1,
                Maximum = 500,
                Width = 100,
                FontSize = 12
            };
            backfillRow.Children.Add(bfBatch);
            cardContent.Children.Add(backfillRow);

            accountBackfill.Add((acct.Label, bfToggle, bfBatch));
            card.Child = cardContent;
            accountsPanel.Children.Add(card);
        }

        if (accountsPanel.Children.Count == 0)
        {
            accountsPanel.Children.Add(new TextBlock
            {
                Text = "No accounts configured.",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#888"))
            });
        }

        root.Children.Add(accountsPanel);

        var addAccountBtn = new Button
        {
            Content = "+ Add Gmail Account",
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 12
        };
        addAccountBtn.Click += async (_, _) =>
        {
            dialog.Close();
            await AddGmailAccountAsync();
            await ShowSettingsDialogAsync();
        };
        root.Children.Add(addAccountBtn);

        // ── Section: Watch Folders ──
        root.Children.Add(SettingsSectionHeader("Watch Folders"));

        var foldersPanel = new StackPanel { Spacing = 4 };
        foreach (var folder in config.WatchFolders)
        {
            var folderRow = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 8
            };
            var patterns = string.Join(", ", folder.Patterns);
            folderRow.Children.Add(new TextBlock
            {
                Text = $"{folder.Path} [{patterns}]",
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var folderPath = folder.Path;
            var removeFolderBtn = new Button
            {
                Content = "Remove ×",
                FontSize = 11,
                Padding = new Thickness(6, 2),
                Foreground = new SolidColorBrush(Color.Parse("#F44336"))
            };
            removeFolderBtn.Click += async (_, _) =>
            {
                await _vm.Bridge.RemoveWatchFolderFromConfigAsync(folderPath);
                dialog.Close();
                await ShowSettingsDialogAsync();
            };
            folderRow.Children.Add(removeFolderBtn);
            foldersPanel.Children.Add(folderRow);
        }

        if (foldersPanel.Children.Count == 0)
        {
            foldersPanel.Children.Add(new TextBlock
            {
                Text = "No watch folders configured.",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#888"))
            });
        }

        root.Children.Add(foldersPanel);

        var addFolderBtn = new Button
        {
            Content = "+ Add Folder",
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 12
        };
        addFolderBtn.Click += async (_, _) =>
        {
            dialog.Close();
            await AddWatchFolderAsync();
            await ShowSettingsDialogAsync();
        };
        root.Children.Add(addFolderBtn);

        // ── Save ──
        root.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.Parse("#20000000")),
            Margin = new Thickness(0, 12, 0, 4)
        });

        var saveBtn = new Button
        {
            Content = "Save",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            FontWeight = FontWeight.Bold,
            Padding = new Thickness(24, 8),
            FontSize = 14
        };
        saveBtn.Click += async (_, _) =>
        {
            try
            {
                saveBtn.IsEnabled = false;
                var provider = ollamaRadio.IsChecked == true ? "ollama" : "azure-openai";
                await _vm.Bridge.UpdateFullConfigAsync(
                    (int)(syncBox.Value ?? 15),
                    (int)(sizeBox.Value ?? 20),
                    provider,
                    ollamaUrlBox.Text ?? "http://localhost:11434",
                    ollamaModelBox.Text ?? "llama3.2",
                    azureEndpointBox.Text,
                    azureApiKeyBox.Text,
                    azureDeploymentBox.Text);

                foreach (var (label, toggle, batch) in accountBackfill)
                {
                    await _vm.Bridge.UpdateAccountBackfillAsync(
                        label,
                        toggle.IsChecked == true,
                        (int)(batch.Value ?? 50));
                }

                statusLbl.Text = "Saved ✓";
                statusLbl.Foreground = new SolidColorBrush(Color.Parse("#4CAF50"));
            }
            catch (Exception ex)
            {
                statusLbl.Text = $"❌ Error: {ex.Message}";
                statusLbl.Foreground = new SolidColorBrush(Color.Parse("#F44336"));
            }
            finally
            {
                saveBtn.IsEnabled = true;
            }
        };

        root.Children.Add(saveBtn);
        root.Children.Add(statusLbl);

        dialog.Content = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        await dialog.ShowDialog(this);
    }

    private static TextBlock SettingsSectionHeader(string text) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeight.Bold,
        Margin = new Thickness(0, 12, 0, 4)
    };

    private static async Task<bool> ShowConfirmDialogAsync(Window owner, string title, string message)
    {
        var result = false;
        var confirmDialog = new Window
        {
            Title = title,
            Width = 360,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13 });

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };
        var yesBtn = new Button { Content = "Remove", Foreground = new SolidColorBrush(Color.Parse("#F44336")) };
        var noBtn = new Button { Content = "Cancel" };

        yesBtn.Click += (_, _) => { result = true; confirmDialog.Close(); };
        noBtn.Click += (_, _) => { result = false; confirmDialog.Close(); };

        buttons.Children.Add(yesBtn);
        buttons.Children.Add(noBtn);
        panel.Children.Add(buttons);
        confirmDialog.Content = panel;

        await confirmDialog.ShowDialog(owner);
        return result;
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
