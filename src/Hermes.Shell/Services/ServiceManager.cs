using System.Diagnostics;

namespace Hermes.Shell.Services;

/// <summary>
/// Manages the Hermes.Service as a child process with health checking and auto-restart.
/// </summary>
public sealed class ServiceManager : IDisposable
{
    private Process? _serviceProcess;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private Timer? _healthTimer;
    private bool _disposed;
    private bool _stopping;

    private static readonly int[] RestartDelaysMs = [2_000, 5_000, 10_000];

    public const int MaxRestarts = 3;
    public string ServiceUrl { get; } = "http://localhost:21741";
    public bool IsHealthy { get; private set; }
    public int RestartCount { get; private set; }
    public bool RestartFailed { get; private set; }
    public event Action<bool>? HealthChanged;

    public Task StartAsync()
    {
        _stopping = false;
        RestartCount = 0;
        RestartFailed = false;

        EnsureDirectories();
        StartServiceProcess();

        _healthTimer = new Timer(OnHealthCheck, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));

        return Task.CompletedTask;
    }

    public void Stop()
    {
        _stopping = true;
        _healthTimer?.Dispose();
        _healthTimer = null;
        StopServiceProcess();
    }

    private void StartServiceProcess()
    {
        var info = FindServiceExecutable();
        if (info is null)
        {
            Debug.WriteLine("Hermes.Service executable not found");
            return;
        }

        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "hermes");

        var psi = new ProcessStartInfo
        {
            FileName = info.FileName,
            Arguments = info.Arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        psi.Environment["HERMES_CONFIG_DIR"] = configDir;

        try
        {
            _serviceProcess = Process.Start(psi);
            if (_serviceProcess is not null)
            {
                _serviceProcess.EnableRaisingEvents = true;
                _serviceProcess.Exited += OnServiceExited;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start Hermes service: {ex.Message}");
        }
    }

    private void StopServiceProcess()
    {
        if (_serviceProcess is null || _serviceProcess.HasExited)
            return;

        try
        {
            _serviceProcess.Kill(entireProcessTree: true);
            _serviceProcess.WaitForExit(5_000);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private async void OnHealthCheck(object? state)
    {
        bool healthy;
        try
        {
            using var response = await _httpClient.GetAsync($"{ServiceUrl}/health");
            healthy = response.IsSuccessStatusCode;
        }
        catch
        {
            healthy = false;
        }

        if (healthy != IsHealthy)
        {
            IsHealthy = healthy;
            HealthChanged?.Invoke(healthy);
        }
    }

    private void OnServiceExited(object? sender, EventArgs e)
    {
        IsHealthy = false;
        HealthChanged?.Invoke(false);

        if (_stopping || _disposed)
            return;

        if (RestartCount >= MaxRestarts)
        {
            RestartFailed = true;
            return;
        }

        var delayMs = RestartDelaysMs[Math.Min(RestartCount, RestartDelaysMs.Length - 1)];
        RestartCount++;

        Task.Delay(delayMs).ContinueWith(_ =>
        {
            if (!_disposed && !_stopping)
                StartServiceProcess();
        });
    }

    private static ExecutableInfo? FindServiceExecutable()
    {
        // 1. macOS .app bundle: Contents/MacOS/ → ../Resources/service/Hermes.Service
        var bundledService = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "Resources", "service", "Hermes.Service"));
        if (File.Exists(bundledService))
            return new ExecutableInfo(bundledService, "");

        // 2. Alongside exe in service/ subdirectory (Windows / packaged)
        var exeDir = AppContext.BaseDirectory;
        var serviceExe = Path.Combine(exeDir, "service", "Hermes.Service");
        if (OperatingSystem.IsWindows())
            serviceExe += ".exe";

        if (File.Exists(serviceExe))
            return new ExecutableInfo(serviceExe, "");

        // 3. Development mode: walk up to find the service project
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "src", "Hermes.Service", "Hermes.Service.fsproj");
            if (File.Exists(candidate))
                return new ExecutableInfo("dotnet", $"run --project \"{candidate}\" --no-launch-profile --");
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }

        return null;
    }

    private static void EnsureDirectories()
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "hermes");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(Path.Combine(configDir, "logs"));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _healthTimer?.Dispose();
            StopServiceProcess();
            _serviceProcess?.Dispose();
            _httpClient.Dispose();
        }
    }

    private sealed record ExecutableInfo(string FileName, string Arguments);
}
