using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Hermes.Core;
using Microsoft.FSharp.Core;
using System;
using System.Net.Http;
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

        var saveBtn = this.FindControl<Button>("SaveSettingsButton");
        if (saveBtn is not null)
            saveBtn.Click += (_, _) => SaveSettings();
    }

    private async Task RefreshStatusAsync()
    {
        await _bridge.RefreshStatusAsync();
        var status = _bridge.LastStatus;

        var statusText = this.FindControl<TextBlock>("StatusText");
        var docCount = this.FindControl<TextBlock>("DocumentCount");
        var unclassified = this.FindControl<TextBlock>("UnclassifiedCount");
        var lastSync = this.FindControl<TextBlock>("LastSyncTime");
        var startedAt = this.FindControl<TextBlock>("StartedAt");
        var ollamaStatus = this.FindControl<TextBlock>("OllamaStatus");

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

        // Check Ollama
        if (ollamaStatus is not null)
        {
            var ollamaUrl = _bridge.Config?.Ollama.BaseUrl ?? "http://localhost:11434";
            ollamaStatus.Text = await CheckOllamaAsync(ollamaUrl) ? "Available" : "Unavailable";
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

    private void SaveSettings()
    {
        // TODO: read values from controls, update config, write to disk
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        base.OnClosed(e);
    }
}
