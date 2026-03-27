using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.IO;

namespace Hermes.App.Views;

public partial class SetupWizard : Window
{
    private readonly HermesServiceBridge _bridge;
    private readonly List<StackPanel> _pages = [];
    private int _currentPage;
    private string _archivePath;
    private bool _watchDownloads = true;
    private bool _watchDesktop;

    public bool Completed { get; private set; }

    public SetupWizard(HermesServiceBridge bridge)
    {
        _bridge = bridge;
        _archivePath = bridge.ArchiveDir;
        InitializeComponent();
        WireUpPages();
        WireUpNavigation();
        ShowPage(0);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void WireUpPages()
    {
        _pages.Add(this.FindControl<StackPanel>("PageWelcome")!);
        _pages.Add(this.FindControl<StackPanel>("PageArchive")!);
        _pages.Add(this.FindControl<StackPanel>("PageAccounts")!);
        _pages.Add(this.FindControl<StackPanel>("PageWatch")!);
        _pages.Add(this.FindControl<StackPanel>("PageOllama")!);
        _pages.Add(this.FindControl<StackPanel>("PageDone")!);

        // Set initial values
        this.FindControl<TextBox>("ArchivePathBox")!.Text = _archivePath;
    }

    private void WireUpNavigation()
    {
        // Welcome → Archive
        this.FindControl<Button>("WelcomeNext")!.Click += (_, _) => ShowPage(1);

        // Archive
        this.FindControl<Button>("BrowseArchive")!.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose Archive Location",
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                _archivePath = folders[0].Path.LocalPath;
                this.FindControl<TextBox>("ArchivePathBox")!.Text = _archivePath;
            }
        };
        this.FindControl<Button>("ArchiveBack")!.Click += (_, _) => ShowPage(0);
        this.FindControl<Button>("ArchiveNext")!.Click += (_, _) => ShowPage(2);

        // Accounts
        this.FindControl<Button>("AddGmailButton")!.Click += (_, _) =>
        {
            // TODO: launch OAuth flow in browser
            this.FindControl<TextBlock>("AccountStatus")!.Text =
                "OAuth flow will open in your browser. (Not yet implemented — add accounts in Settings after setup.)";
        };
        this.FindControl<Button>("AccountsBack")!.Click += (_, _) => ShowPage(1);
        this.FindControl<Button>("AccountsNext")!.Click += (_, _) => ShowPage(3);

        // Watch Folders
        this.FindControl<Button>("WatchBack")!.Click += (_, _) => ShowPage(2);
        this.FindControl<Button>("WatchNext")!.Click += (_, _) =>
        {
            _watchDownloads = this.FindControl<CheckBox>("WatchDownloads")!.IsChecked ?? false;
            _watchDesktop = this.FindControl<CheckBox>("WatchDesktop")!.IsChecked ?? false;
            DetectOllama();
            ShowPage(4);
        };

        // Ollama
        this.FindControl<Button>("OllamaBack")!.Click += (_, _) => ShowPage(3);
        this.FindControl<Button>("OllamaNext")!.Click += async (_, _) =>
        {
            if (this.FindControl<CheckBox>("InstallOllama")!.IsChecked ?? false)
            {
                var progress = this.FindControl<TextBlock>("OllamaProgress")!;
                progress.Text = "Installing Ollama...";
                var result = await OllamaInstaller.InstallAsync(s => progress.Text = s);
                if (!result)
                    progress.Text = "Ollama install failed — you can set it up later in Settings.";
            }
            PrepareSummary();
            ShowPage(5);
        };

        // Done
        this.FindControl<Button>("DoneButton")!.Click += (_, _) =>
        {
            WriteConfig();
            Completed = true;
            Close();
        };
    }

    private void ShowPage(int index)
    {
        for (var i = 0; i < _pages.Count; i++)
            _pages[i].IsVisible = i == index;
        _currentPage = index;
    }

    private void DetectOllama()
    {
        var detectText = this.FindControl<TextBlock>("OllamaDetectText")!;
        var installCheck = this.FindControl<CheckBox>("InstallOllama")!;

        var (hasGpu, hasOllama) = OllamaInstaller.Detect();

        if (hasOllama)
        {
            detectText.Text = "Ollama is already installed. AI-powered search is available.";
            installCheck.IsChecked = false;
            installCheck.IsEnabled = false;
        }
        else if (hasGpu)
        {
            detectText.Text = "GPU detected. Installing Ollama will enable AI-powered semantic search — find documents by meaning, not just keywords.";
            installCheck.IsChecked = true;
        }
        else
        {
            detectText.Text = "No GPU detected. Ollama requires a GPU for good performance. You can add an Azure Document Intelligence key later for cloud-based OCR.";
            installCheck.IsChecked = false;
        }
    }

    private void PrepareSummary()
    {
        var summary = this.FindControl<TextBlock>("DoneSummary")!;
        var lines = new List<string>
        {
            $"Archive: {_archivePath}"
        };
        if (_watchDownloads) lines.Add("Watching: ~/Downloads");
        if (_watchDesktop) lines.Add("Watching: ~/Desktop");
        lines.Add("Sync interval: every 15 minutes");
        summary.Text = string.Join("\n", lines);
    }

    private void WriteConfig()
    {
        var configDir = _bridge.ConfigDir;
        Directory.CreateDirectory(configDir);

        var watchFolders = new List<string>();
        var downloadsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        var watchYaml = "";
        if (_watchDownloads || _watchDesktop)
        {
            watchYaml = "watch_folders:\n";
            if (_watchDownloads)
                watchYaml += $"  - path: {downloadsPath}\n    patterns: [\"*.pdf\", \"*statement*\", \"*invoice*\", \"*receipt*\", \"*payslip*\"]\n";
            if (_watchDesktop)
                watchYaml += $"  - path: {desktopPath}\n    patterns: [\"*.pdf\"]\n";
        }

        var configYaml = $"""
            archive_dir: {_archivePath}

            credentials: {Path.Combine(configDir, "gmail_credentials.json")}

            accounts: []

            sync_interval_minutes: 15
            min_attachment_size: 20480

            {watchYaml}
            ollama:
              enabled: true
              base_url: http://localhost:11434
              embedding_model: nomic-embed-text
              vision_model: llava
              instruct_model: llama3.2

            fallback:
              embedding: onnx
              ocr: azure-document-intelligence
            """;

        File.WriteAllText(Path.Combine(configDir, "config.yaml"), configYaml);

        // Create archive directory structure
        Directory.CreateDirectory(_archivePath);
        Directory.CreateDirectory(Path.Combine(_archivePath, "unclassified"));
    }
}
