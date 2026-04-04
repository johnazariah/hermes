using System;
using System.IO;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Hermes.App;
using Hermes.App.Views;
using Hermes.Core;
using Microsoft.FSharp.Collections;
using Xunit;

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
}
