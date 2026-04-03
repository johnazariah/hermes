using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Hermes.Core;
using Microsoft.FSharp.Core;
using Serilog.Events;

namespace Hermes.App;

/// <summary>
/// Bridges the Avalonia UI to the F# service host.
/// Manages config loading, service lifecycle, and status reading.
/// </summary>
public sealed class HermesServiceBridge
{
    private Domain.HermesConfig? _config;
    private ServiceHost.ServiceStatus? _lastStatus;
    private bool _paused;

    public bool IsRunning => _lastStatus?.Running ?? false;
    public bool IsPaused => _paused;
    public ServiceHost.ServiceStatus? LastStatus => _lastStatus;
    public Domain.HermesConfig? Config => _config;

    public string ArchiveDir =>
        _config?.ArchiveDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "hermes");

    public string ConfigDir => Core.Config.configDir(Interpreters.systemEnvironment);

    public bool IsFirstRun =>
        !File.Exists(Path.Combine(ConfigDir, "config.yaml"));

    public async Task StartAsync(CancellationToken ct)
    {
        // Load or create config
        var configPath = Path.Combine(ConfigDir, "config.yaml");
        var fs = Interpreters.realFileSystem;
        var configResult = await Core.Config.load(fs, Interpreters.systemEnvironment, configPath);

        if (configResult.IsOk)
        {
            _config = configResult.ResultValue;
        }
        else
        {
            // First run — use defaults
            _config = Core.Config.defaultConfig(Interpreters.systemEnvironment);
        }

        // Ensure archive directories exist before any I/O
        Directory.CreateDirectory(_config.ArchiveDir);
        Directory.CreateDirectory(Path.Combine(_config.ArchiveDir, "unclassified"));

        var logger = Logging.configure(ConfigDir, LogEventLevel.Information);
        var clock = Interpreters.systemClock;
        var db = Database.fromPath(Path.Combine(_config.ArchiveDir, "db.sqlite"));
        var initResult = await db.initSchema.Invoke(null!);

        var rulesPath = Path.Combine(ConfigDir, "rules.yaml");
        var rules = await Rules.fromFile(fs, logger, rulesPath);
        var serviceConfig = ServiceHost.defaultServiceConfig(_config);

        // Build production SyncDeps at the composition root
        var extractor = new Algebra.TextExtractor(
            extractPdf: FuncConvert.FromFunc<byte[], Task<FSharpResult<string, string>>>(
                bytes => Task.FromResult(Extraction.extractPdfText(bytes))),
            extractImage: FuncConvert.FromFunc<byte[], Task<FSharpResult<string, string>>>(
                _ => Task.FromResult(FSharpResult<string, string>.NewError("Ollama vision not configured"))));

        Algebra.EmbeddingClient? embedder = null;
        if (_config.Ollama.Enabled)
            embedder = Embeddings.ollamaClient(new HttpClient(), _config.Ollama.BaseUrl, _config.Ollama.EmbeddingModel, 768);

        Algebra.ChatProvider? chatProvider = null;
        try { chatProvider = Chat.providerFromConfig(new HttpClient(), _config.Chat, _config.Ollama.BaseUrl, _config.Ollama.InstructModel); }
        catch { /* chat not available */ }

        var contentRules = Microsoft.FSharp.Collections.ListModule.Empty<Domain.ContentRule>();
        var contentRulesPath = Path.Combine(ConfigDir, "rules.yaml");
        if (fs.fileExists.Invoke(contentRulesPath))
        {
            var yaml = await fs.readAllText.Invoke(contentRulesPath);
            contentRules = Rules.parseContentRules(yaml);
        }

        var deps = new ServiceHost.SyncDeps(
            extractor,
            embedder is not null ? FSharpOption<Algebra.EmbeddingClient>.Some(embedder) : FSharpOption<Algebra.EmbeddingClient>.None,
            chatProvider is not null ? FSharpOption<Algebra.ChatProvider>.Some(chatProvider) : FSharpOption<Algebra.ChatProvider>.None,
            contentRules,
            FuncConvert.FromFunc<string, string, Task<Algebra.EmailProvider>>(
                (cfgDir, label) => GmailProvider.create(cfgDir, label, logger)));

        // Run the service loop — pass configPath so it reloads config before each sync
        var env = Interpreters.systemEnvironment;
        await ServiceHost.createServiceHost(fs, db, logger, clock, env, rules, deps, serviceConfig, configPath, ct);
    }

    public async Task RefreshStatusAsync()
    {
        if (_config is null) return;
        var fs = Interpreters.realFileSystem;
        var result = await ServiceHost.readHeartbeat(fs, _config.ArchiveDir);

        if (result is not null && FSharpOption<ServiceHost.ServiceStatus>.get_IsSome(result))
        {
            _lastStatus = result.Value;
        }

        // Reload config from disk so external edits (e.g. config.yaml) take effect without restart
        var configPath = Path.Combine(ConfigDir, "config.yaml");
        if (File.Exists(configPath))
        {
            var configResult = await Core.Config.load(fs, Interpreters.systemEnvironment, configPath);
            if (configResult.IsOk) _config = configResult.ResultValue;
        }
    }

    public Task RequestSyncAsync()
    {
        return ServiceHost.requestSync(Interpreters.realFileSystem, Interpreters.systemClock, ArchiveDir);
    }

    public void TogglePause()
    {
        _paused = !_paused;
    }

    public async Task AddGmailAccountToConfigAsync(string label)
    {
        var configPath = Path.Combine(ConfigDir, "config.yaml");
        if (!File.Exists(configPath)) return;

        var yaml = await File.ReadAllTextAsync(configPath);
        var accountEntry = $"  - label: {label}\n    provider: gmail\n";

        if (yaml.Contains("accounts: []"))
            yaml = yaml.Replace("accounts: []", $"accounts:\n{accountEntry}");
        else if (Regex.IsMatch(yaml, @"\naccounts:\n"))
            yaml = Regex.Replace(yaml, @"(\naccounts:\n)", $"$1{accountEntry}");

        await File.WriteAllTextAsync(configPath, yaml);

        // Reload config so in-memory state reflects the new account
        var fs = Interpreters.realFileSystem;
        var result = await Core.Config.load(fs, Interpreters.systemEnvironment, configPath);
        if (result.IsOk) _config = result.ResultValue;
    }

    public async Task UpdateConfigAsync(int syncIntervalMinutes, int minAttachmentSizeKb, string ollamaUrl)
    {
        var configPath = Path.Combine(ConfigDir, "config.yaml");
        if (!File.Exists(configPath)) return;

        var yaml = await File.ReadAllTextAsync(configPath);

        yaml = Regex.Replace(yaml, @"sync_interval_minutes:\s*\d+",
            $"sync_interval_minutes: {syncIntervalMinutes}");
        yaml = Regex.Replace(yaml, @"min_attachment_size:\s*\d+",
            $"min_attachment_size: {minAttachmentSizeKb * 1024}");
        yaml = Regex.Replace(yaml, @"(base_url:\s*).*",
            $"${{1}}{ollamaUrl}");

        await File.WriteAllTextAsync(configPath, yaml);

        var fs = Interpreters.realFileSystem;
        var result = await Core.Config.load(fs, Interpreters.systemEnvironment, configPath);
        if (result.IsOk) _config = result.ResultValue;
    }

    public async Task AddWatchFolderToConfigAsync(string folderPath, string patterns)
    {
        var configPath = Path.Combine(ConfigDir, "config.yaml");
        if (!File.Exists(configPath)) return;

        var yaml = await File.ReadAllTextAsync(configPath);
        var patternList = string.Join(", ", patterns.Split(',').Select(p => $"\"{p.Trim()}\""));
        var entry = $"  - path: {folderPath}\n    patterns: [{patternList}]\n";

        if (yaml.Contains("watch_folders: []"))
            yaml = yaml.Replace("watch_folders: []", $"watch_folders:\n{entry}");
        else if (Regex.IsMatch(yaml, @"\nwatch_folders:\n"))
            yaml = Regex.Replace(yaml, @"(\nwatch_folders:\n)", $"$1{entry}");

        await File.WriteAllTextAsync(configPath, yaml);

        var fs = Interpreters.realFileSystem;
        var result = await Core.Config.load(fs, Interpreters.systemEnvironment, configPath);
        if (result.IsOk) _config = result.ResultValue;

        // Trigger an immediate sync so existing files in the new folder are ingested now
        await RequestSyncAsync();
    }

    /// <summary>
    /// Writes complete config to config.yaml, replacing general and chat settings.
    /// </summary>
    public async Task UpdateFullConfigAsync(
        int syncIntervalMinutes, int minAttachmentSizeKb,
        string chatProvider, string ollamaUrl, string ollamaModel,
        string? azureEndpoint, string? azureApiKey, string? azureDeployment)
    {
        var configPath = Path.Combine(ConfigDir, "config.yaml");
        if (!File.Exists(configPath)) return;

        var yaml = await File.ReadAllTextAsync(configPath);

        // Simple field replacements
        yaml = Regex.Replace(yaml, @"sync_interval_minutes:\s*\d+",
            $"sync_interval_minutes: {syncIntervalMinutes}");
        yaml = Regex.Replace(yaml, @"min_attachment_size:\s*\d+",
            $"min_attachment_size: {minAttachmentSizeKb * 1024}");
        yaml = Regex.Replace(yaml, @"(base_url:\s*).*",
            $"${{1}}{ollamaUrl}");
        yaml = Regex.Replace(yaml, @"(instruct_model:\s*).*",
            $"${{1}}{ollamaModel}");

        // Remove existing chat section and append updated one
        yaml = Regex.Replace(yaml, @"\r?\nchat:(?:\r?\n  .+)*", "");
        var maxTokens = _config?.Chat.AzureOpenAI.MaxTokens ?? 4096;
        var timeout = _config?.Chat.AzureOpenAI.TimeoutSeconds ?? 300;
        yaml = yaml.TrimEnd() + "\n\nchat:\n"
            + $"  provider: {chatProvider}\n"
            + "  azure_openai:\n"
            + $"    endpoint: \"{azureEndpoint ?? ""}\"\n"
            + $"    api_key: \"{azureApiKey ?? ""}\"\n"
            + $"    deployment: \"{azureDeployment ?? "gpt-4o"}\"\n"
            + $"    max_tokens: {maxTokens}\n"
            + $"    timeout_seconds: {timeout}\n";

        await File.WriteAllTextAsync(configPath, yaml);
        await ReloadConfigAsync();
    }

    /// <summary>
    /// Removes an account from config.yaml and optionally deletes its token.
    /// </summary>
    public async Task RemoveAccountFromConfigAsync(string label, bool deleteToken)
    {
        var configPath = Path.Combine(ConfigDir, "config.yaml");
        if (!File.Exists(configPath)) return;

        var yaml = await File.ReadAllTextAsync(configPath);
        var escaped = Regex.Escape(label);
        yaml = Regex.Replace(yaml, $@"  - label: {escaped}\r?\n(?:    .*\r?\n)*", "");

        // If no accounts remain, normalize to empty list
        if (!yaml.Contains("  - label:"))
            yaml = Regex.Replace(yaml, @"accounts:\s*\n", "accounts: []\n");

        await File.WriteAllTextAsync(configPath, yaml);

        if (deleteToken)
        {
            var tokenDir = Path.Combine(ConfigDir, "tokens");
            if (Directory.Exists(tokenDir))
            {
                foreach (var file in Directory.GetFiles(tokenDir).Where(f => f.Contains(label)))
                {
                    try { File.Delete(file); } catch { /* best effort */ }
                }
            }
        }

        await ReloadConfigAsync();
    }

    /// <summary>
    /// Removes a watch folder entry from config.yaml.
    /// </summary>
    public async Task RemoveWatchFolderFromConfigAsync(string folderPath)
    {
        var configPath = Path.Combine(ConfigDir, "config.yaml");
        if (!File.Exists(configPath)) return;

        var yaml = await File.ReadAllTextAsync(configPath);
        var escaped = Regex.Escape(folderPath);
        yaml = Regex.Replace(yaml, $@"  - path: {escaped}\r?\n(?:    .*\r?\n)*", "");

        if (!yaml.Contains("  - path:"))
            yaml = Regex.Replace(yaml, @"watch_folders:\s*\n", "watch_folders: []\n");

        await File.WriteAllTextAsync(configPath, yaml);
        await ReloadConfigAsync();
        await RequestSyncAsync();
    }

    /// <summary>
    /// Updates per-account backfill settings in config.yaml.
    /// </summary>
    public async Task UpdateAccountBackfillAsync(string label, bool enabled, int batchSize)
    {
        var configPath = Path.Combine(ConfigDir, "config.yaml");
        if (!File.Exists(configPath)) return;

        var yaml = await File.ReadAllTextAsync(configPath);
        var escaped = Regex.Escape(label);
        var backfillBlock = "    backfill:\n"
            + $"      enabled: {enabled.ToString().ToLowerInvariant()}\n"
            + $"      batch_size: {batchSize}\n";

        // Replace existing backfill section or add one after the provider line
        var withBackfill = $@"(  - label: {escaped}\r?\n    provider: \w+\r?\n)    backfill:\r?\n(?:      .*\r?\n)*";
        if (Regex.IsMatch(yaml, withBackfill))
        {
            yaml = Regex.Replace(yaml, withBackfill, $"$1{backfillBlock}");
        }
        else
        {
            var noBackfill = $@"(  - label: {escaped}\r?\n    provider: \w+\r?\n)";
            yaml = Regex.Replace(yaml, noBackfill, $"$1{backfillBlock}");
        }

        await File.WriteAllTextAsync(configPath, yaml);
        await ReloadConfigAsync();
    }

    private async Task ReloadConfigAsync()
    {
        var configPath = Path.Combine(ConfigDir, "config.yaml");
        var fs = Interpreters.realFileSystem;
        var result = await Core.Config.load(fs, Interpreters.systemEnvironment, configPath);
        if (result.IsOk) _config = result.ResultValue;
    }

    public string StatusText
    {
        get
        {
            if (_lastStatus is not { } s) return "Starting...";
            if (!s.Running) return "Stopped";
            if (_paused) return "Paused";
            if (s.ErrorMessage is not null) return $"Error: {s.ErrorMessage}";
            return $"Idle — {s.DocumentCount:N0} documents indexed";
        }
    }
}
