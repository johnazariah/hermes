using System.Text.Json;
using Hermes.Tray;

namespace Hermes.Tray.Smoke;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [STAThread]
    private static int Main(string[] arguments)
    {
        try { return Execute(SmokeSettings.Parse(arguments)); }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Native smoke failed: {SafeFailure.Describe(error)}");
            return 1;
        }
    }

    private static int Execute(SmokeSettings settings)
    {
        string? workspace = null;
        var report = SmokeReport.Empty;
        try
        {
            DesktopPrerequisite.Require();
            workspace = SyntheticWorkspace.Create();
            report = RunDesktop(settings, workspace);
        }
        catch (Exception error) { report = report with { Failure = SafeFailure.Describe(error), Passed = false }; }
        report = Clean(workspace, report);
        Directory.CreateDirectory(Path.GetDirectoryName(settings.ReportPath) ?? throw new ArgumentException("Report directory missing."));
        File.WriteAllText(settings.ReportPath, JsonSerializer.Serialize(report, Json));
        Console.WriteLine(JsonSerializer.Serialize(report, Json));
        return report.Passed ? 0 : 1;
    }

    private static SmokeReport RunDesktop(SmokeSettings settings, string workspace)
    {
        var options = TrayOptions.Parse(
            ["--service-path", settings.ServicePath, "--port", settings.Port.ToString(),
             "--config-dir", workspace], _ => null, AppContext.BaseDirectory) with { Synthetic = true };
        using var lease = InstanceLease.Acquire(options);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        using var context = new SmokeContext(options, settings.Timeout);
        Application.Run(context);
        if (!context.Completion.IsCompletedSuccessfully)
            throw new InvalidOperationException("Native smoke worker did not finish before its message loop exited.");
        return context.Report;
    }

    private static SmokeReport Clean(string? workspace, SmokeReport report)
    {
        var cleaned = workspace is null || SyntheticWorkspace.Remove(workspace);
        return report with
        {
            Passed = report.Passed && cleaned,
            Cleaned = cleaned,
            RetainedSyntheticDirectory = cleaned ? null : workspace,
            Failure = cleaned ? report.Failure : report.Failure ?? "Synthetic workspace cleanup failed."
        };
    }
}
