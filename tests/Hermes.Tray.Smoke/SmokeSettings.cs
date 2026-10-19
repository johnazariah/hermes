using System.Runtime.InteropServices;
using System.Text.Json;
using Hermes.Tray;

namespace Hermes.Tray.Smoke;

internal sealed record SmokeSettings(string ServicePath, int Port, string ReportPath, TimeSpan Timeout)
{
    public static SmokeSettings Parse(string[] arguments)
    {
        var flags = arguments.Chunk(2).Select(Pair).ToDictionary(pair => pair.Key, pair => pair.Value);
        var service = Required(flags, "--service-path");
        var report = Required(flags, "--report");
        var port = TrayOptions.ParsePort(Required(flags, "--port"));
        var seconds = TrayOptions.ParsePort(flags.GetValueOrDefault("--timeout-seconds") ?? "150");
        if (seconds is < 10 or > 300)
            throw new ArgumentException("Smoke timeout must be between 10 and 300 seconds.");
        return new(TrayOptions.Absolute(service, "Service executable"), port,
            TrayOptions.Absolute(report, "Report path"), TimeSpan.FromSeconds(seconds));
    }

    private static KeyValuePair<string, string> Pair(string[] pair) => pair switch
    {
        [("--service-path" or "--port" or "--report" or "--timeout-seconds") and var key, var value] => new(key, value),
        _ => throw new ArgumentException(
            "Smoke requires --service-path ABSOLUTE_EXE --port NUMBER --report ABSOLUTE_JSON " +
            "[--timeout-seconds 10..300].")
    };

    private static string Required(Dictionary<string, string> flags, string key) =>
        flags.GetValueOrDefault(key) ?? throw new ArgumentException($"Missing {key}.");
}

internal static class SyntheticWorkspace
{
    public static string Create()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hermes-native-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var archive = JsonSerializer.Serialize(Path.Combine(root, "archive"));
        var credentials = JsonSerializer.Serialize(Path.Combine(root, "absent-synthetic-credentials.json"));
        File.WriteAllText(Path.Combine(root, "config.yaml"),
            $"archive_dir: {archive}\ncredentials: {credentials}\naccounts: []\nwatch_folders: []\n" +
            "preferences: Synthetic native smoke preferences\n" +
            "sync_interval_minutes: 15\nmin_attachment_size: 20480\n" +
            "ollama:\n  enabled: false\n  base_url: http://127.0.0.1:1\n" +
            "fallback:\n  embedding: none\n  ocr: none\n");
        return root;
    }

    public static bool Remove(string root)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { Directory.Delete(root, recursive: true); return true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
        return false;
    }
}

internal static class DesktopPrerequisite
{
    public static void Require()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
            throw new InvalidOperationException("Native smoke requires an interactive Windows desktop.");
        var desktop = OpenInputDesktop(0, false, 0x0100);
        if (desktop == IntPtr.Zero)
            throw new InvalidOperationException("No accessible Windows input desktop; native smoke did not run.");
        if (!CloseDesktop(desktop))
            throw new InvalidOperationException("Could not release the Windows input desktop handle.");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
}
