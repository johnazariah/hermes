using System;
using System.IO;
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
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Hermes");

    public string ConfigDir => Core.Config.configDir();

    public bool IsFirstRun =>
        !File.Exists(Path.Combine(ConfigDir, "config.yaml"));

    public async Task StartAsync(CancellationToken ct)
    {
        // Load or create config
        var configPath = Path.Combine(ConfigDir, "config.yaml");
        var fs = Interpreters.realFileSystem;
        var configResult = await Core.Config.load(fs, configPath);

        if (configResult.IsOk)
        {
            _config = configResult.ResultValue;
        }
        else
        {
            // First run — use defaults
            _config = Core.Config.defaultConfig();
        }

        var logger = Logging.configure(ConfigDir, LogEventLevel.Information);
        var clock = Interpreters.systemClock;
        var db = Database.fromPath(Path.Combine(_config.ArchiveDir, "db.sqlite"));
        var initResult = await db.initSchema.Invoke(null!);

        var rulesPath = Path.Combine(ConfigDir, "rules.yaml");
        var rules = Rules.fromFile(fs, logger, rulesPath);
        var serviceConfig = ServiceHost.defaultServiceConfig(_config);

        // Run the service loop
        await ServiceHost.createServiceHost(fs, db, logger, clock, rules, serviceConfig, ct);
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
    }

    public void TogglePause()
    {
        _paused = !_paused;
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
