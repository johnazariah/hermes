using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Hermes.App;

/// <summary>
/// Detects GPU availability and installs Ollama via platform package managers.
/// </summary>
public static class OllamaInstaller
{
    private static readonly string[] DefaultModels = ["nomic-embed-text", "llava", "llama3.2:3b"];

    /// <summary>
    /// Detect GPU presence and whether Ollama is already installed.
    /// </summary>
    public static (bool HasGpu, bool HasOllama) Detect()
    {
        var hasOllama = CanRunCommand("ollama", "--version");
        var hasGpu = DetectGpu();
        return (hasGpu, hasOllama);
    }

    /// <summary>
    /// Install Ollama and pull default models. Reports progress via callback.
    /// </summary>
    public static async Task<bool> InstallAsync(Action<string> onProgress)
    {
        try
        {
            // Step 1: Install Ollama
            onProgress("Installing Ollama...");
            bool installed;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                installed = await InstallWindowsAsync(onProgress);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                installed = await InstallMacAsync(onProgress);
            else
            {
                onProgress("Unsupported platform for auto-install.");
                return false;
            }

            if (!installed) return false;

            // Step 2: Wait for Ollama to be ready
            onProgress("Waiting for Ollama to start...");
            await Task.Delay(3000);

            // Step 3: Pull models
            foreach (var model in DefaultModels)
            {
                onProgress($"Downloading model: {model} (this may take a few minutes)...");
                var pulled = await RunCommandAsync("ollama", $"pull {model}", timeoutSeconds: 600);
                if (!pulled)
                    onProgress($"Warning: failed to pull {model}, skipping.");
            }

            onProgress("Ollama setup complete!");
            return true;
        }
        catch (Exception ex)
        {
            onProgress($"Error: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> InstallWindowsAsync(Action<string> onProgress)
    {
        // Check for winget
        if (!CanRunCommand("winget", "--version"))
        {
            onProgress("winget not found. Please install App Installer from the Microsoft Store, then retry.");
            return false;
        }

        onProgress("Installing via winget...");
        return await RunCommandAsync("winget", "install Ollama.Ollama --accept-package-agreements --accept-source-agreements", timeoutSeconds: 300);
    }

    private static async Task<bool> InstallMacAsync(Action<string> onProgress)
    {
        // Check for brew
        if (!CanRunCommand("brew", "--version"))
        {
            onProgress("Homebrew not found. Install from https://brew.sh then retry.");
            return false;
        }

        onProgress("Installing via Homebrew...");
        return await RunCommandAsync("brew", "install ollama", timeoutSeconds: 300);
    }

    private static bool DetectGpu()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Check for NVIDIA GPU via nvidia-smi
            if (CanRunCommand("nvidia-smi", "")) return true;
            // Check for any GPU via WMIC (basic)
            return CanRunCommand("wmic", "path win32_videocontroller get name");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // All modern Macs have Metal-capable GPUs
            return true;
        }

        return false;
    }

    private static bool CanRunCommand(string command, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(command, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> RunCommandAsync(string command, string args, int timeoutSeconds = 60)
    {
        try
        {
            var psi = new ProcessStartInfo(command, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return false;

            var completed = await Task.Run(() => proc.WaitForExit(timeoutSeconds * 1000));
            if (!completed)
            {
                proc.Kill();
                return false;
            }

            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
