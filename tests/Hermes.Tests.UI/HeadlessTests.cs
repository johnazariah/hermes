using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Hermes.App;
using Hermes.App.Views;
using Hermes.Core;
using Microsoft.FSharp.Collections;
using Xunit;

using Path = System.IO.Path;

namespace Hermes.Tests.UI;

[Trait("Category", "UI")]
public sealed class HeadlessTests
{
    private static HermesServiceBridge CreateFakeBridge()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"hermes-hltest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "unclassified"));

        var bridge = new HermesServiceBridge();

        var ollama = new Domain.OllamaConfig(
            true, "http://localhost:11434", "nomic-embed-text", "llava", "llama3.2");
        var fallback = new Domain.FallbackConfig("onnx", "none");
        var azure = new Domain.AzureConfig("", "");
        var azureOai = new Domain.AzureOpenAIConfig("", "", "gpt-4o", 4096, 300);
        var chat = new Domain.ChatConfig(Domain.ChatProviderKind.Ollama, azureOai);

        var config = new Domain.HermesConfig(
            dir, "",
            FSharpList<Domain.AccountConfig>.Empty,
            15, 20480,
            FSharpList<Domain.WatchFolderConfig>.Empty,
            ollama, fallback, azure, chat);

        typeof(HermesServiceBridge)
            .GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(bridge, config);

        return bridge;
    }

    // ── HL-1: All funnel sections exist ────────────────────────────

    [AvaloniaFact]
    public void ShellWindow_HasAllFunnelSections()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        // Named pipeline expanders
        Assert.NotNull(window.FindControl<Expander>("IntakeExpander"));
        Assert.NotNull(window.FindControl<Expander>("ExtractingExpander"));
        Assert.NotNull(window.FindControl<Expander>("ClassifyingExpander"));
        Assert.NotNull(window.FindControl<Expander>("ActionItemsExpander"));
        // Funnel container
        Assert.NotNull(window.FindControl<StackPanel>("FunnelPanel"));
    }

    // ── HL-2: Three-column layout exists ───────────────────────────

    [AvaloniaFact]
    public void ShellWindow_HasThreeColumnLayout()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        // Left: funnel panel
        Assert.NotNull(window.FindControl<StackPanel>("FunnelPanel"));
        // Centre: content scroller
        Assert.NotNull(window.FindControl<ScrollViewer>("ContentScroller"));
        // Right: chat pane
        Assert.NotNull(window.FindControl<Grid>("ChatPaneGrid"));
    }

    // ── HL-3: Chat input and send button exist ─────────────────────

    [AvaloniaFact]
    public void ShellWindow_HasChatControls()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        var input = window.FindControl<TextBox>("ChatInput");
        Assert.NotNull(input);

        // SendButton is resolved inline, verify it exists
        var chatScroller = window.FindControl<ScrollViewer>("ChatScroller");
        Assert.NotNull(chatScroller);
    }

    // ── HL-4: AI toggle exists ─────────────────────────────────────

    [AvaloniaFact]
    public void ShellWindow_HasAiToggle()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        var toggle = window.FindControl<ToggleButton>("AiToggle");
        Assert.NotNull(toggle);
        Assert.False(toggle!.IsChecked ?? true);
    }

    // ── HL-5: Settings button exists ───────────────────────────────

    [AvaloniaFact]
    public void ShellWindow_HasSettingsButton()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        Assert.NotNull(window.FindControl<Button>("NavSettingsBtn"));
    }

    // ── HL-6: Suggested query chips exist ──────────────────────────

    [AvaloniaFact]
    public void ShellWindow_HasSuggestedQueryChips()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        var chips = window.FindControl<WrapPanel>("SuggestedQueries");
        Assert.NotNull(chips);
        Assert.True(chips!.Children.Count >= 3,
            $"Expected >= 3 query chips, got {chips.Children.Count}");
    }

    // ── HL-7: Status bar controls exist ────────────────────────────

    [AvaloniaFact]
    public void ShellWindow_HasStatusBar()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        Assert.NotNull(window.FindControl<TextBlock>("StatusBarText"));
        Assert.NotNull(window.FindControl<Button>("ToggleChatButton"));
    }

    // ── HL-8: Navigation controls exist ────────────────────────────

    [AvaloniaFact]
    public void ShellWindow_HasNavigationControls()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        Assert.NotNull(window.FindControl<Button>("BackButton"));
        Assert.NotNull(window.FindControl<TextBlock>("BreadcrumbText"));
        Assert.NotNull(window.FindControl<Button>("SyncNowButton"));
    }

    // ── HL-9: Source section controls ──────────────────────────────

    [AvaloniaFact]
    public void ShellWindow_HasSourceControls()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        Assert.NotNull(window.FindControl<Button>("AddAccountButton"));
        Assert.NotNull(window.FindControl<Button>("AddWatchFolderButton"));
        Assert.NotNull(window.FindControl<TextBlock>("AccountsText"));
        Assert.NotNull(window.FindControl<TextBlock>("WatchFoldersText"));
    }

    // ── HL-10: Index section controls ──────────────────────────────

    [AvaloniaFact]
    public void ShellWindow_HasIndexControls()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        Assert.NotNull(window.FindControl<ProgressBar>("ExtractedBar"));
        Assert.NotNull(window.FindControl<TextBlock>("ExtractedCountText"));
        Assert.NotNull(window.FindControl<ProgressBar>("EmbeddedBar"));
        Assert.NotNull(window.FindControl<TextBlock>("EmbeddedCountText"));
        Assert.NotNull(window.FindControl<TextBlock>("DbSizeText"));
    }

    // ── HL-11: Service indicator controls ──────────────────────────

    [AvaloniaFact]
    public void ShellWindow_HasServiceIndicators()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        Assert.NotNull(window.FindControl<Ellipse>("OllamaDot"));
        Assert.NotNull(window.FindControl<TextBlock>("OllamaStatusText"));
        Assert.NotNull(window.FindControl<TextBlock>("OllamaModelsText"));
        Assert.NotNull(window.FindControl<Ellipse>("DbDot"));
        Assert.NotNull(window.FindControl<TextBlock>("DbStatusText"));
        Assert.NotNull(window.FindControl<Ellipse>("PipelineDot"));
        Assert.NotNull(window.FindControl<TextBlock>("PipelineStatusText"));
        Assert.NotNull(window.FindControl<TextBlock>("LastSyncText"));
    }

    // ── HL-12: Pipeline count labels ───────────────────────────────

    [AvaloniaFact]
    public void ShellWindow_HasPipelineCounts()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        Assert.NotNull(window.FindControl<TextBlock>("IntakeCount"));
        Assert.NotNull(window.FindControl<TextBlock>("ExtractingCount"));
        Assert.NotNull(window.FindControl<TextBlock>("ClassifyingCount"));
    }

    // ── HL-13: Pipeline detail panels ──────────────────────────────

    [AvaloniaFact]
    public void ShellWindow_HasPipelinePanels()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        Assert.NotNull(window.FindControl<StackPanel>("IntakePanel"));
        Assert.NotNull(window.FindControl<StackPanel>("ExtractingPanel"));
        Assert.NotNull(window.FindControl<StackPanel>("ClassifyingPanel"));
    }

    // ── HL-14: Library section controls ────────────────────────────

    [AvaloniaFact]
    public void ShellWindow_HasLibraryControls()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        Assert.NotNull(window.FindControl<StackPanel>("LibraryPanel"));
        Assert.NotNull(window.FindControl<TextBlock>("LibraryCount"));
    }

    // ── HL-15: Action items section ────────────────────────────────

    [AvaloniaFact]
    public void ShellWindow_HasActionItemControls()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        Assert.NotNull(window.FindControl<TextBlock>("ActionItemBadge"));
        Assert.NotNull(window.FindControl<StackPanel>("ActionItemsPanel"));
    }

    // ── HL-16: Content panel ───────────────────────────────────────

    [AvaloniaFact]
    public void ShellWindow_HasContentPanel()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        Assert.NotNull(window.FindControl<StackPanel>("ContentPanel"));
    }

    // ── HL-17: Chat panel and send button ──────────────────────────

    [AvaloniaFact]
    public void ShellWindow_HasChatPanelAndSend()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        Assert.NotNull(window.FindControl<StackPanel>("ChatPanel"));
        Assert.NotNull(window.FindControl<Button>("SendButton"));
    }

    // ── HL-18: Status dot in status bar ────────────────────────────

    [AvaloniaFact]
    public void ShellWindow_HasStatusDot()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        Assert.NotNull(window.FindControl<Ellipse>("StatusDot"));
    }

    // ═══════════════════════════════════════════════════════════════
    //  SetupWizard tests
    // ═══════════════════════════════════════════════════════════════

    // ── SW-1: Starts on welcome page ───────────────────────────────

    [AvaloniaFact]
    public void SetupWizard_StartsOnWelcomePage()
    {
        var wizard = new SetupWizard(CreateFakeBridge());
        wizard.Show();

        var welcome = wizard.FindControl<StackPanel>("PageWelcome");
        Assert.NotNull(welcome);
        Assert.True(welcome!.IsVisible);

        Assert.False(wizard.FindControl<StackPanel>("PageArchive")!.IsVisible);
        Assert.False(wizard.FindControl<StackPanel>("PageAccounts")!.IsVisible);
        Assert.False(wizard.FindControl<StackPanel>("PageWatch")!.IsVisible);
        Assert.False(wizard.FindControl<StackPanel>("PageOllama")!.IsVisible);
        Assert.False(wizard.FindControl<StackPanel>("PageDone")!.IsVisible);
    }

    // ── SW-2: Welcome page controls ────────────────────────────────

    [AvaloniaFact]
    public void SetupWizard_WelcomePage_HasControls()
    {
        var wizard = new SetupWizard(CreateFakeBridge());
        wizard.Show();

        Assert.NotNull(wizard.FindControl<Button>("WelcomeNext"));
    }

    // ── SW-3: Archive page controls ────────────────────────────────

    [AvaloniaFact]
    public void SetupWizard_ArchivePage_HasControls()
    {
        var wizard = new SetupWizard(CreateFakeBridge());
        wizard.Show();

        Assert.NotNull(wizard.FindControl<TextBox>("ArchivePathBox"));
        Assert.NotNull(wizard.FindControl<Button>("BrowseArchive"));
        Assert.NotNull(wizard.FindControl<Button>("ArchiveBack"));
        Assert.NotNull(wizard.FindControl<Button>("ArchiveNext"));
    }

    // ── SW-4: Accounts page controls ───────────────────────────────

    [AvaloniaFact]
    public void SetupWizard_AccountsPage_HasControls()
    {
        var wizard = new SetupWizard(CreateFakeBridge());
        wizard.Show();

        Assert.NotNull(wizard.FindControl<Button>("AddGmailButton"));
        Assert.NotNull(wizard.FindControl<TextBlock>("AccountStatus"));
        Assert.NotNull(wizard.FindControl<Button>("AccountsBack"));
        Assert.NotNull(wizard.FindControl<Button>("AccountsNext"));
    }

    // ── SW-5: Watch folders page controls ──────────────────────────

    [AvaloniaFact]
    public void SetupWizard_WatchPage_HasControls()
    {
        var wizard = new SetupWizard(CreateFakeBridge());
        wizard.Show();

        Assert.NotNull(wizard.FindControl<CheckBox>("WatchDownloads"));
        Assert.NotNull(wizard.FindControl<CheckBox>("WatchDesktop"));
        Assert.NotNull(wizard.FindControl<Button>("WatchBack"));
        Assert.NotNull(wizard.FindControl<Button>("WatchNext"));
    }

    // ── SW-6: Ollama page controls ─────────────────────────────────

    [AvaloniaFact]
    public void SetupWizard_OllamaPage_HasControls()
    {
        var wizard = new SetupWizard(CreateFakeBridge());
        wizard.Show();

        Assert.NotNull(wizard.FindControl<TextBlock>("OllamaDetectText"));
        Assert.NotNull(wizard.FindControl<CheckBox>("InstallOllama"));
        Assert.NotNull(wizard.FindControl<TextBlock>("OllamaProgress"));
        Assert.NotNull(wizard.FindControl<Button>("OllamaBack"));
        Assert.NotNull(wizard.FindControl<Button>("OllamaNext"));
    }

    // ── SW-7: Done page controls ───────────────────────────────────

    [AvaloniaFact]
    public void SetupWizard_DonePage_HasControls()
    {
        var wizard = new SetupWizard(CreateFakeBridge());
        wizard.Show();

        Assert.NotNull(wizard.FindControl<TextBlock>("DoneSummary"));
        Assert.NotNull(wizard.FindControl<Button>("DoneButton"));
    }

    // ── SW-8: Archive path shows default ───────────────────────────

    [AvaloniaFact]
    public void SetupWizard_ArchivePathBox_ShowsDefaultPath()
    {
        var bridge = CreateFakeBridge();
        var wizard = new SetupWizard(bridge);
        wizard.Show();

        var box = wizard.FindControl<TextBox>("ArchivePathBox");
        Assert.NotNull(box);
        Assert.Equal(bridge.ArchiveDir, box!.Text);
    }

    // ── SW-9: WatchDownloads defaults checked ──────────────────────

    [AvaloniaFact]
    public void SetupWizard_WatchDownloads_DefaultChecked()
    {
        var wizard = new SetupWizard(CreateFakeBridge());
        wizard.Show();

        var cb = wizard.FindControl<CheckBox>("WatchDownloads");
        Assert.NotNull(cb);
        Assert.True(cb!.IsChecked ?? false);
    }

    // ── SW-10: WatchDesktop defaults unchecked ─────────────────────

    [AvaloniaFact]
    public void SetupWizard_WatchDesktop_DefaultUnchecked()
    {
        var wizard = new SetupWizard(CreateFakeBridge());
        wizard.Show();

        var cb = wizard.FindControl<CheckBox>("WatchDesktop");
        Assert.NotNull(cb);
        Assert.False(cb!.IsChecked ?? true);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Visual tree helpers for dynamic dialogs
    // ═══════════════════════════════════════════════════════════════

    private static IEnumerable<T> FindAll<T>(Avalonia.Visual parent) where T : class
    {
        foreach (var child in parent.GetVisualChildren())
        {
            if (child is T match) yield return match;
            if (child is Avalonia.Visual v)
                foreach (var descendant in FindAll<T>(v))
                    yield return descendant;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Settings dialog tests
    // ═══════════════════════════════════════════════════════════════

    // ── DLG-1: Settings dialog has all sections ────────────────────

    [AvaloniaFact]
    public void SettingsDialog_HasAllSections()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        var dialog = window.BuildSettingsDialog();
        Assert.NotNull(dialog);
        dialog!.Show();

        var textBlocks = FindAll<TextBlock>(dialog).ToList();
        var sectionHeaders = textBlocks.Where(t => t.FontWeight == Avalonia.Media.FontWeight.Bold).Select(t => t.Text).ToList();

        Assert.Contains("⚙ Settings", sectionHeaders);
        Assert.Contains("General", sectionHeaders);
        Assert.Contains("AI / Chat", sectionHeaders);
        Assert.Contains("Accounts", sectionHeaders);
        Assert.Contains("Watch Folders", sectionHeaders);
    }

    // ── DLG-2: Settings dialog has sync interval ───────────────────

    [AvaloniaFact]
    public void SettingsDialog_HasSyncIntervalControl()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        var dialog = window.BuildSettingsDialog()!;
        dialog.Show();

        var numericUpDowns = FindAll<NumericUpDown>(dialog).ToList();
        Assert.True(numericUpDowns.Count >= 2, $"Expected >= 2 NumericUpDown controls, got {numericUpDowns.Count}");

        // First is sync interval (value should match config = 15)
        Assert.Equal(15m, numericUpDowns[0].Value);
    }

    // ── DLG-3: Settings dialog has attachment size ──────────────────

    [AvaloniaFact]
    public void SettingsDialog_HasMinAttachmentSizeControl()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        var dialog = window.BuildSettingsDialog()!;
        dialog.Show();

        var numericUpDowns = FindAll<NumericUpDown>(dialog).ToList();
        // Second is attachment size (20480 / 1024 = 20)
        Assert.Equal(20m, numericUpDowns[1].Value);
    }

    // ── DLG-4: Settings dialog has chat provider radios ────────────

    [AvaloniaFact]
    public void SettingsDialog_HasChatProviderRadios()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        var dialog = window.BuildSettingsDialog()!;
        dialog.Show();

        var radios = FindAll<RadioButton>(dialog).ToList();
        Assert.Equal(2, radios.Count);
        Assert.Equal("Ollama", radios[0].Content?.ToString());
        Assert.Equal("Azure OpenAI", radios[1].Content?.ToString());
        // Config uses Ollama, so Ollama should be checked
        Assert.True(radios[0].IsChecked ?? false);
    }

    // ── DLG-5: Settings dialog has Ollama fields ───────────────────

    [AvaloniaFact]
    public void SettingsDialog_HasOllamaFields()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        var dialog = window.BuildSettingsDialog()!;
        dialog.Show();

        var textBoxes = FindAll<TextBox>(dialog).ToList();
        // Ollama URL and Model should be present
        Assert.Contains(textBoxes, tb => tb.Text == "http://localhost:11434");
        Assert.Contains(textBoxes, tb => tb.Text == "llama3.2");
    }

    // ── DLG-6: Settings dialog has Save button ─────────────────────

    [AvaloniaFact]
    public void SettingsDialog_HasSaveButton()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        var dialog = window.BuildSettingsDialog()!;
        dialog.Show();

        var buttons = FindAll<Button>(dialog).ToList();
        Assert.Contains(buttons, b => b.Content?.ToString() == "Save");
    }

    // ── DLG-7: Settings dialog has no-accounts message ─────────────

    [AvaloniaFact]
    public void SettingsDialog_ShowsNoAccountsMessage()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        var dialog = window.BuildSettingsDialog()!;
        dialog.Show();

        var texts = FindAll<TextBlock>(dialog).ToList();
        Assert.Contains(texts, t => t.Text == "No accounts configured.");
    }

    // ── DLG-8: Settings dialog has add-account button ──────────────

    [AvaloniaFact]
    public void SettingsDialog_HasAddAccountButton()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        var dialog = window.BuildSettingsDialog()!;
        dialog.Show();

        var buttons = FindAll<Button>(dialog).ToList();
        Assert.Contains(buttons, b => b.Content?.ToString() == "+ Add Gmail Account");
    }

    // ── DLG-9: Settings dialog has no-folders message ──────────────

    [AvaloniaFact]
    public void SettingsDialog_ShowsNoWatchFoldersMessage()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        var dialog = window.BuildSettingsDialog()!;
        dialog.Show();

        var texts = FindAll<TextBlock>(dialog).ToList();
        Assert.Contains(texts, t => t.Text == "No watch folders configured.");
    }

    // ── DLG-10: Settings dialog has add-folder button ──────────────

    [AvaloniaFact]
    public void SettingsDialog_HasAddFolderButton()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        var dialog = window.BuildSettingsDialog()!;
        dialog.Show();

        var buttons = FindAll<Button>(dialog).ToList();
        Assert.Contains(buttons, b => b.Content?.ToString() == "+ Add Folder");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Confirm dialog tests
    // ═══════════════════════════════════════════════════════════════

    // ── DLG-11: Confirm dialog has message and buttons ─────────────

    [AvaloniaFact]
    public void ConfirmDialog_HasMessageAndButtons()
    {
        var (dialog, _) = ShellWindow.BuildConfirmDialog("Remove Account", "Are you sure?");
        dialog.Show();

        var texts = FindAll<TextBlock>(dialog).ToList();
        Assert.Contains(texts, t => t.Text == "Are you sure?");

        var buttons = FindAll<Button>(dialog).ToList();
        Assert.Contains(buttons, b => b.Content?.ToString() == "Remove");
        Assert.Contains(buttons, b => b.Content?.ToString() == "Cancel");
    }

    // ── DLG-12: Confirm dialog uses provided title ─────────────────

    [AvaloniaFact]
    public void ConfirmDialog_UsesProvidedTitle()
    {
        var (dialog, _) = ShellWindow.BuildConfirmDialog("Delete Everything", "Really?");
        dialog.Show();

        Assert.Equal("Delete Everything", dialog.Title);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Add Account dialog tests
    // ═══════════════════════════════════════════════════════════════

    // ── DLG-13: Add Account dialog has label box and auth button ───

    [AvaloniaFact]
    public void AddAccountDialog_HasLabelBoxAndAuthButton()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        var dialog = window.BuildAddAccountDialog();
        dialog.Show();

        var textBoxes = FindAll<TextBox>(dialog).ToList();
        Assert.Contains(textBoxes, tb => tb.Watermark == "Account label (e.g. john-personal)");

        var buttons = FindAll<Button>(dialog).ToList();
        Assert.Contains(buttons, b => b.Content?.ToString() == "Authenticate with Google");
    }

    // ── DLG-14: Add Account dialog has title and instructions ──────

    [AvaloniaFact]
    public void AddAccountDialog_HasTitleAndInstructions()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        var dialog = window.BuildAddAccountDialog();
        dialog.Show();

        var texts = FindAll<TextBlock>(dialog).ToList();
        Assert.Contains(texts, t => t.Text == "Add Gmail Account");
        Assert.Contains(texts, t => t.Text == "Enter a friendly label for this account:");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Watch Folder Pattern dialog tests
    // ═══════════════════════════════════════════════════════════════

    // ── DLG-15: Watch Folder dialog has pattern box and OK button ──

    [AvaloniaFact]
    public void WatchFolderDialog_HasPatternBoxAndAddButton()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        var dialog = window.BuildWatchFolderPatternDialog(@"C:\Users\test\Downloads");
        dialog.Show();

        var textBoxes = FindAll<TextBox>(dialog).ToList();
        Assert.Contains(textBoxes, tb => tb.Text == "*.pdf");

        var buttons = FindAll<Button>(dialog).ToList();
        Assert.Contains(buttons, b => b.Content?.ToString() == "Add Watch Folder");
    }

    // ── DLG-16: Watch Folder dialog shows folder path ──────────────

    [AvaloniaFact]
    public void WatchFolderDialog_ShowsFolderPath()
    {
        var window = new ShellWindow(CreateFakeBridge());
        window.Show();

        var dialog = window.BuildWatchFolderPatternDialog(@"C:\Users\test\Downloads");
        dialog.Show();

        var texts = FindAll<TextBlock>(dialog).ToList();
        Assert.Contains(texts, t => t.Text!.Contains(@"C:\Users\test\Downloads"));
    }
}
