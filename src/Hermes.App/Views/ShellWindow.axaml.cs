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
using Microsoft.FSharp.Core;
using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Path = System.IO.Path;
using File = System.IO.File;

namespace Hermes.App.Views;

public partial class ShellWindow : Window
{
    private readonly ShellViewModel _vm;
    private readonly DispatcherTimer _refreshTimer;

    // Resolved controls — Pipeline funnel
    private Ellipse _ollamaDot = null!;
    private TextBlock _ollamaStatusText = null!;
    private TextBlock _ollamaModelsText = null!;
    private ProgressBar _extractedBar = null!;
    private TextBlock _extractedCountText = null!;
    private ProgressBar _embeddedBar = null!;
    private TextBlock _embeddedCountText = null!;
    private TextBlock _dbSizeText = null!;
    private TextBlock _accountsText = null!;
    private TextBlock _watchFoldersText = null!;
    private TextBlock _lastSyncText = null!;
    private Button _syncNowButton = null!;
    private StackPanel _chatPanel = null!;
    private ScrollViewer _chatScroller = null!;
    private TextBox _chatInput = null!;
    private ToggleButton _aiToggle = null!;
    private WrapPanel _suggestedQueries = null!;
    private Ellipse _statusDot = null!;
    private TextBlock _statusBarText = null!;
    private Button _navSettingsBtn = null!;
    // Pipeline sections
    private Expander _intakeExpander = null!;
    private TextBlock _intakeCount = null!;
    private Expander _extractingExpander = null!;
    private TextBlock _extractingCount = null!;
    private Expander _classifyingExpander = null!;
    private TextBlock _classifyingCount = null!;
    private StackPanel _libraryPanel = null!;
    private TextBlock _libraryCount = null!;
    private TextBlock _actionItemBadge = null!;
    private StackPanel _actionItemsPanel = null!;
    private TextBlock _dbStatusText = null!;
    private TextBlock _pipelineStatusText = null!;
    // Content pane
    private Button _backButton = null!;
    private TextBlock _breadcrumbText = null!;
    private ScrollViewer _contentScroller = null!;
    private StackPanel _contentPanel = null!;
    // Chat pane
    private Grid _chatPaneGrid = null!;
    private Button _toggleChatButton = null!;

    public ShellWindow(HermesServiceBridge bridge)
    {
        _vm = new ShellViewModel(bridge);
        InitializeComponent();
        ResolveControls();
        WireUpEvents();

        _vm.AddWelcomeMessage();
        _vm.Messages.CollectionChanged += OnMessagesChanged;
        RenderAllMessages();
        ShowWelcome();

        _vm.PropertyChanged += (_, e) => Dispatcher.UIThread.Post(() => OnViewModelChanged(e.PropertyName));

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += async (_, _) => await _vm.RefreshAsync();
        _refreshTimer.Start();

        _ = _vm.RefreshAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void ResolveControls()
    {
        // Services
        _ollamaDot = this.FindControl<Ellipse>("OllamaDot")!;
        _ollamaStatusText = this.FindControl<TextBlock>("OllamaStatusText")!;
        _ollamaModelsText = this.FindControl<TextBlock>("OllamaModelsText")!;
        _dbStatusText = this.FindControl<TextBlock>("DbStatusText")!;
        _pipelineStatusText = this.FindControl<TextBlock>("PipelineStatusText")!;
        // Index
        _extractedBar = this.FindControl<ProgressBar>("ExtractedBar")!;
        _extractedCountText = this.FindControl<TextBlock>("ExtractedCountText")!;
        _embeddedBar = this.FindControl<ProgressBar>("EmbeddedBar")!;
        _embeddedCountText = this.FindControl<TextBlock>("EmbeddedCountText")!;
        _dbSizeText = this.FindControl<TextBlock>("DbSizeText")!;
        // Sources
        _accountsText = this.FindControl<TextBlock>("AccountsText")!;
        _watchFoldersText = this.FindControl<TextBlock>("WatchFoldersText")!;
        _lastSyncText = this.FindControl<TextBlock>("LastSyncText")!;
        // Pipeline stages
        _intakeExpander = this.FindControl<Expander>("IntakeExpander")!;
        _intakeCount = this.FindControl<TextBlock>("IntakeCount")!;
        _extractingExpander = this.FindControl<Expander>("ExtractingExpander")!;
        _extractingCount = this.FindControl<TextBlock>("ExtractingCount")!;
        _classifyingExpander = this.FindControl<Expander>("ClassifyingExpander")!;
        _classifyingCount = this.FindControl<TextBlock>("ClassifyingCount")!;
        // Library
        _libraryPanel = this.FindControl<StackPanel>("LibraryPanel")!;
        _libraryCount = this.FindControl<TextBlock>("LibraryCount")!;
        // Action Items
        _actionItemBadge = this.FindControl<TextBlock>("ActionItemBadge")!;
        _actionItemsPanel = this.FindControl<StackPanel>("ActionItemsPanel")!;
        // Header buttons
        _syncNowButton = this.FindControl<Button>("SyncNowButton")!;
        _navSettingsBtn = this.FindControl<Button>("NavSettingsBtn")!;
        _toggleChatButton = this.FindControl<Button>("ToggleChatButton")!;
        // Content pane
        _backButton = this.FindControl<Button>("BackButton")!;
        _breadcrumbText = this.FindControl<TextBlock>("BreadcrumbText")!;
        _contentScroller = this.FindControl<ScrollViewer>("ContentScroller")!;
        _contentPanel = this.FindControl<StackPanel>("ContentPanel")!;
        // Chat pane
        _chatPaneGrid = this.FindControl<Grid>("ChatPaneGrid")!;
        _chatPanel = this.FindControl<StackPanel>("ChatPanel")!;
        _chatScroller = this.FindControl<ScrollViewer>("ChatScroller")!;
        _chatInput = this.FindControl<TextBox>("ChatInput")!;
        _aiToggle = this.FindControl<ToggleButton>("AiToggle")!;
        _suggestedQueries = this.FindControl<WrapPanel>("SuggestedQueries")!;
        // Status bar
        _statusDot = this.FindControl<Ellipse>("StatusDot")!;
        _statusBarText = this.FindControl<TextBlock>("StatusBarText")!;
    }

    private void WireUpEvents()
    {
        _syncNowButton.Click += async (_, _) =>
        {
            _syncNowButton.IsEnabled = false;
            _syncNowButton.Content = "⏳";
            await _vm.SyncNowAsync();
            _syncNowButton.Content = "⟳";
            _syncNowButton.IsEnabled = true;
        };

        _navSettingsBtn.Click += async (_, _) => await ShowSettingsDialogAsync();
        this.FindControl<Button>("AddAccountButton")!.Click += async (_, _) => await AddGmailAccountAsync();
        this.FindControl<Button>("AddWatchFolderButton")!.Click += async (_, _) => await AddWatchFolderAsync();
        this.FindControl<Button>("SendButton")!.Click += async (_, _) => await HandleSendAsync();

        _chatInput.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) await HandleSendAsync();
        };

        _aiToggle.IsCheckedChanged += (_, _) => _vm.AiEnabled = _aiToggle.IsChecked == true;

        _toggleChatButton.Click += (_, _) =>
        {
            _vm.ToggleChatPane();
            _chatPaneGrid.IsVisible = _vm.IsChatPaneVisible;
        };

        _backButton.Click += (_, _) => ShowWelcome();

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

    private void ShowWelcome()
    {
        _contentPanel.Children.Clear();
        _breadcrumbText.Text = "Welcome";
        _backButton.IsVisible = false;
        _contentPanel.Children.Add(new TextBlock
        {
            Text = "Welcome to Hermes! Select a category from the Library, or ask a question in the chat pane.",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#888")),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 20, 0, 0)
        });
    }

    // ── Content pane navigation ──────────────────────────────────

    private async Task ShowCategoryDocumentsAsync(string category)
    {
        _contentPanel.Children.Clear();
        _breadcrumbText.Text = $"Library / {category}";
        _backButton.IsVisible = true;

        _contentPanel.Children.Add(new TextBlock
        {
            Text = $"📁 {category}",
            FontSize = 16, FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        });

        var loading = new TextBlock { Text = "Loading…", FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#888")) };
        _contentPanel.Children.Add(loading);

        var dbPath = Path.Combine(_vm.Bridge.ArchiveDir, "db.sqlite");
        if (!System.IO.File.Exists(dbPath)) { loading.Text = "No database."; return; }

        try
        {
            var db = Database.fromPath(dbPath);
            try
            {
                var docs = await DocumentBrowser.listDocuments(db, category, 0, 100);
                _contentPanel.Children.Remove(loading);

                if (!docs.Any())
                {
                    _contentPanel.Children.Add(new TextBlock
                    {
                        Text = "No documents in this category.",
                        FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#888"))
                    });
                    return;
                }

                foreach (var doc in docs)
                {
                    var row = CreateDocumentListRow(doc);
                    _contentPanel.Children.Add(row);
                }
            }
            finally { db.dispose.Invoke(null!); }
        }
        catch { loading.Text = "Could not load documents."; }
    }

    private Border CreateDocumentListRow(DocumentBrowser.DocumentSummary doc)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameStack = new StackPanel { Spacing = 1 };
        nameStack.Children.Add(new TextBlock
        {
            Text = doc.OriginalName, FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1
        });

        var meta = FSharpOption<string>.get_IsSome(doc.ExtractedDate)
            ? doc.ExtractedDate!.Value : "";
        if (FSharpOption<string>.get_IsSome(doc.Sender))
            meta = string.IsNullOrEmpty(meta) ? doc.Sender!.Value : $"{doc.Sender!.Value} · {meta}";
        if (FSharpOption<double>.get_IsSome(doc.ExtractedAmount))
            meta = string.IsNullOrEmpty(meta) ? $"${doc.ExtractedAmount!.Value:F2}" : $"{meta} · ${doc.ExtractedAmount!.Value:F2}";

        if (!string.IsNullOrEmpty(meta))
        {
            nameStack.Children.Add(new TextBlock
            {
                Text = meta, FontSize = 10,
                Foreground = new SolidColorBrush(Color.Parse("#888")),
                TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1
            });
        }
        Grid.SetColumn(nameStack, 0);
        grid.Children.Add(nameStack);

        // Classification badge
        if (FSharpOption<string>.get_IsSome(doc.ClassificationTier))
        {
            var tier = doc.ClassificationTier!.Value;
            var badge = new TextBlock
            {
                Text = tier, FontSize = 9, Padding = new Thickness(4, 1),
                Foreground = new SolidColorBrush(Color.Parse("#888")),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            Grid.SetColumn(badge, 1);
            grid.Children.Add(badge);
        }

        var border = new Border
        {
            Child = grid,
            Padding = new Thickness(0, 6, 0, 6),
            BorderBrush = new SolidColorBrush(Color.Parse("#10000000")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };

        border.PointerPressed += (_, _) => _ = ShowDocumentDetailAsync(doc.Id);
        return border;
    }

    private async Task ShowDocumentDetailAsync(long documentId)
    {
        _contentPanel.Children.Clear();
        _backButton.IsVisible = true;

        _contentPanel.Children.Add(new TextBlock
        {
            Text = "Loading document…",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#888"))
        });

        var dbPath = Path.Combine(_vm.Bridge.ArchiveDir, "db.sqlite");
        if (!System.IO.File.Exists(dbPath))
        {
            _contentPanel.Children.Clear();
            _contentPanel.Children.Add(new TextBlock { Text = "No database found.", FontSize = 12 });
            return;
        }

        try
        {
            var db = Database.fromPath(dbPath);
            try
            {
                var detail = await DocumentBrowser.getDocumentDetail(db, documentId);
                if (!FSharpOption<DocumentBrowser.DocumentDetail>.get_IsSome(detail))
                {
                    _contentPanel.Children.Clear();
                    _contentPanel.Children.Add(new TextBlock { Text = "Document not found.", FontSize = 12 });
                    return;
                }

                var doc = detail!.Value;

                // Load markdown content
                var fs = Interpreters.realFileSystem;
                var archiveDir = _vm.Bridge.ArchiveDir;
                var contentResult = await DocumentFeed.getDocumentContent(
                    db, fs, archiveDir, documentId, DocumentFeed.ContentFormat.Markdown);

                _contentPanel.Children.Clear();

                _breadcrumbText.Text = $"Library / {doc.Summary.Category} / {doc.Summary.OriginalName}";

                // Header: filename
                _contentPanel.Children.Add(new TextBlock
                {
                    Text = doc.Summary.OriginalName,
                    FontSize = 16,
                    FontWeight = FontWeight.Bold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 4)
                });

                // Metadata
                var metaLines = new System.Text.StringBuilder();
                metaLines.AppendLine($"Category: {doc.Summary.Category}");
                if (FSharpOption<string>.get_IsSome(doc.Summary.Sender))
                    metaLines.AppendLine($"Sender: {doc.Summary.Sender!.Value}");
                if (FSharpOption<string>.get_IsSome(doc.Vendor))
                    metaLines.AppendLine($"Vendor: {doc.Vendor!.Value}");
                if (FSharpOption<string>.get_IsSome(doc.Summary.ExtractedDate))
                    metaLines.AppendLine($"Date: {doc.Summary.ExtractedDate!.Value}");
                if (FSharpOption<double>.get_IsSome(doc.Summary.ExtractedAmount))
                    metaLines.AppendLine($"Amount: ${doc.Summary.ExtractedAmount!.Value:F2}");
                metaLines.AppendLine($"Ingested: {doc.IngestedAt}");
                if (FSharpOption<string>.get_IsSome(doc.Summary.ClassificationTier))
                {
                    var tierLabel = doc.Summary.ClassificationTier!.Value;
                    var confText = FSharpOption<double>.get_IsSome(doc.Summary.ClassificationConfidence)
                        ? $" ({doc.Summary.ClassificationConfidence!.Value:P0})"
                        : "";
                    metaLines.AppendLine($"Classification: {tierLabel}{confText}");
                }

                _contentPanel.Children.Add(new TextBlock
                {
                    Text = metaLines.ToString().TrimEnd(),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#888")),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 12)
                });

                // Open file button
                if (!string.IsNullOrEmpty(doc.FilePath))
                {
                    var fullPath = System.IO.Path.IsPathRooted(doc.FilePath)
                        ? doc.FilePath
                        : System.IO.Path.Combine(archiveDir, doc.FilePath);

                    if (System.IO.File.Exists(fullPath))
                    {
                        var openBtn = new Button
                        {
                            Content = "📂 Open Original File",
                            FontSize = 11,
                            Padding = new Thickness(10, 4),
                            Margin = new Thickness(0, 0, 0, 12)
                        };
                        openBtn.Click += (_, _) =>
                            Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
                        _contentPanel.Children.Add(openBtn);
                    }
                }

                // Separator
                _contentPanel.Children.Add(new Rectangle
                {
                    Height = 1,
                    Fill = new SolidColorBrush(Color.Parse("#20000000")),
                    Margin = new Thickness(0, 0, 0, 12)
                });

                // Markdown content
                if (contentResult.IsOk)
                {
                    var markdown = contentResult.ResultValue;
                    _contentPanel.Children.Add(new TextBlock
                    {
                        Text = markdown,
                        FontSize = 12,
                        FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 18
                    });
                }
                else
                {
                    _contentPanel.Children.Add(new TextBlock
                    {
                        Text = "No extracted content available.",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.Parse("#888")),
                        FontStyle = FontStyle.Italic
                    });
                }
            }
            finally
            {
                db.dispose.Invoke(null!);
            }
        }
        catch (Exception ex)
        {
            _contentPanel.Children.Clear();
            _contentPanel.Children.Add(new TextBlock
            {
                Text = $"Error loading document: {ex.Message}",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#CC0000"))
            });
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
                var total = Math.Max(docs, 1);
                _extractedBar.Maximum = total;
                _extractedBar.Value = stats.ExtractedCount;
                _extractedCountText.Text = $"{stats.ExtractedCount:N0} / {docs:N0}";
                _embeddedBar.Maximum = total;
                _embeddedBar.Value = stats.EmbeddedCount;
                _embeddedCountText.Text = $"{stats.EmbeddedCount:N0} / {docs:N0}";
                _dbSizeText.Text = $"DB: {stats.DatabaseSizeMb:F1} MB";
                _dbStatusText.Text = $"Database · {docs:N0} docs";
                break;

            case nameof(ShellViewModel.PipelineCounts):
                var counts = _vm.PipelineCounts;
                if (counts is null) break;
                _intakeCount.Text = counts.IntakeCount > 0 ? $"({counts.IntakeCount})" : "";
                _intakeExpander.IsVisible = counts.IntakeCount > 0;
                _extractingCount.Text = counts.ExtractingCount > 0 ? $"({counts.ExtractingCount})" : "";
                _extractingExpander.IsVisible = counts.ExtractingCount > 0;
                _classifyingCount.Text = counts.ClassifyingCount > 0 ? $"({counts.ClassifyingCount})" : "";
                _classifyingExpander.IsVisible = counts.ClassifyingCount > 0;
                _pipelineStatusText.Text = (counts.IntakeCount + counts.ExtractingCount + counts.ClassifyingCount) > 0
                    ? "Pipeline processing…" : "Pipeline idle";
                break;

            case nameof(ShellViewModel.Categories):
                RebuildLibraryPanel();
                break;

            case nameof(ShellViewModel.Accounts):
                var accts = _vm.Accounts;
                _accountsText.Text = accts.Count == 0
                    ? "No accounts configured."
                    : string.Join("\n", accts.Select(a => $"📧 {a.Label}  {a.MessageCount} emails"));
                break;

            case nameof(ShellViewModel.LastSyncText):
                _lastSyncText.Text = _vm.LastSyncText;
                break;

            case nameof(ShellViewModel.StatusBarText):
                _statusBarText.Text = _vm.StatusBarText;
                // Also update watch folders
                var wf = _vm.WatchFolders;
                _watchFoldersText.Text = wf.Count == 0
                    ? ""
                    : string.Join("\n", wf.Select(f => $"📁 {f.Path}  [{string.Join(", ", f.Patterns)}]"));
                break;

            case nameof(ShellViewModel.IsSyncing):
                _statusDot.Fill = new SolidColorBrush(_vm.IsSyncing ? Color.Parse("#2196F3") : Color.Parse("#4CAF50"));
                break;

            case nameof(ShellViewModel.ActionItemCount):
                RebuildActionItemsPanel();
                break;
        }
    }

    private void RebuildLibraryPanel()
    {
        _libraryPanel.Children.Clear();
        var cats = _vm.Categories;
        long totalDocs = _vm.IndexStats?.DocumentCount ?? 0;
        _libraryCount.Text = totalDocs > 0 ? $"({totalDocs:N0})" : "";

        if (cats.Count == 0)
        {
            _libraryPanel.Children.Add(new TextBlock
            {
                Text = "No documents yet.", FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#888"))
            });
            return;
        }

        foreach (var cat in cats)
        {
            var row = new Border
            {
                Padding = new Thickness(0, 3),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameText = new TextBlock { Text = cat.Category, FontSize = 11 };
            Grid.SetColumn(nameText, 0);
            var countText = new TextBlock
            {
                Text = $"({cat.Count})", FontSize = 10,
                Foreground = new SolidColorBrush(Color.Parse("#888")),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            Grid.SetColumn(countText, 1);
            grid.Children.Add(nameText);
            grid.Children.Add(countText);
            row.Child = grid;

            var category = cat.Category;
            row.PointerPressed += (_, _) => _ = ShowCategoryDocumentsAsync(category);
            _libraryPanel.Children.Add(row);
        }
    }

    private void RebuildActionItemsPanel()
    {
        _actionItemBadge.Text = _vm.ActionItemCount > 0 ? $"({_vm.ActionItemCount})" : "";
        _actionItemsPanel.Children.Clear();

        if (_vm.ActionItemCount == 0)
        {
            _actionItemsPanel.Children.Add(new TextBlock
            {
                Text = "No action items.", FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#888"))
            });
            return;
        }

        foreach (var r in _vm.OverdueReminders)
        {
            var row = new TextBlock
            {
                Text = $"🔴 {r.Vendor ?? r.FileName ?? "Unknown"} — {r.DueLabel}",
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };
            var item = r;
            row.PointerPressed += (_, _) => ShowReminderDetail(item);
            _actionItemsPanel.Children.Add(row);
        }

        foreach (var r in _vm.UpcomingReminders)
        {
            var row = new TextBlock
            {
                Text = $"🟡 {r.Vendor ?? r.FileName ?? "Unknown"} — {r.DueLabel}",
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };
            var item = r;
            row.PointerPressed += (_, _) => ShowReminderDetail(item);
            _actionItemsPanel.Children.Add(row);
        }
    }

    private void ShowReminderDetail(ReminderItem item)
    {
        _contentPanel.Children.Clear();
        _breadcrumbText.Text = "Action Items";
        _backButton.IsVisible = true;

        _contentPanel.Children.Add(new TextBlock
        {
            Text = item.Vendor ?? item.FileName ?? "Reminder",
            FontSize = 16, FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 8)
        });

        if (item.Amount is not null)
            _contentPanel.Children.Add(new TextBlock { Text = $"Amount: {item.Amount}", FontSize = 13 });
        if (item.DueDate is not null)
            _contentPanel.Children.Add(new TextBlock { Text = $"Due: {item.DueDate} ({item.DueLabel})", FontSize = 13 });

        var btnPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        var paidBtn = new Button { Content = "✓ Mark Paid", Padding = new Thickness(10, 4) };
        paidBtn.Click += async (_, _) => { await _vm.MarkPaidAsync(item.Id); ShowWelcome(); };
        var snoozeBtn = new Button { Content = "⏰ Snooze 7d", Padding = new Thickness(10, 4) };
        snoozeBtn.Click += async (_, _) => { await _vm.SnoozeAsync(item.Id); ShowWelcome(); };
        var dismissBtn = new Button { Content = "✕ Dismiss", Padding = new Thickness(10, 4) };
        dismissBtn.Click += async (_, _) => { await _vm.DismissAsync(item.Id); ShowWelcome(); };
        btnPanel.Children.Add(paidBtn);
        btnPanel.Children.Add(snoozeBtn);
        btnPanel.Children.Add(dismissBtn);
        _contentPanel.Children.Add(btnPanel);
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

    // RebuildTodoPanel removed — action items now in funnel sidebar

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
        var dialog = BuildSettingsDialog();
        if (dialog is null) return;
        await dialog.ShowDialog(this);
    }

    internal Window? BuildSettingsDialog()
    {
        var config = _vm.Bridge.Config;
        if (config is null) return null;

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

        return dialog;
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
        var (dialog, resultHolder) = BuildConfirmDialog(title, message);
        await dialog.ShowDialog(owner);
        return resultHolder.Value;
    }

    internal static (Window Dialog, StrongBox<bool> Result) BuildConfirmDialog(string title, string message)
    {
        var resultHolder = new StrongBox<bool>(false);
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

        yesBtn.Click += (_, _) => { resultHolder.Value = true; confirmDialog.Close(); };
        noBtn.Click += (_, _) => { resultHolder.Value = false; confirmDialog.Close(); };

        buttons.Children.Add(yesBtn);
        buttons.Children.Add(noBtn);
        panel.Children.Add(buttons);
        confirmDialog.Content = panel;

        return (confirmDialog, resultHolder);
    }

    // ── Add Gmail account ──────────────────────────────────────────

    private async Task AddGmailAccountAsync()
    {
        var dialog = BuildAddAccountDialog();
        await dialog.ShowDialog(this);
    }

    internal Window BuildAddAccountDialog()
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

        return dialog;
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
            var patternWindow = BuildWatchFolderPatternDialog(folder);

            await patternWindow.ShowDialog(this);
        }
        catch (Exception ex)
        {
            var errWin = new Window { Title = "Error", Width = 350, Height = 120, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            errWin.Content = new TextBlock { Text = $"Could not open folder picker:\n{ex.Message}", Margin = new Thickness(16) };
            await errWin.ShowDialog(this);
        }
    }

    internal Window BuildWatchFolderPatternDialog(string folder)
    {
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

        return patternWindow;
    }

    // ── Lifecycle ──────────────────────────────────────────────────

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        _vm.Dispose();
        base.OnClosed(e);
    }
}
