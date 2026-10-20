using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hermes.Tray.Smoke;

internal static class InstalledSmokeHarness
{
    public static async Task<int> RunAsync(
        InstalledSmokeSettings settings, CancellationToken cancellationToken)
    {
        // Environment admission is followed by owner-state admission in Prepare,
        // before any installed runtime is inspected or launched.
        CiAdmission.Read(cleanOwner: true).Require();
        TemporaryPaths.RequireNewFile(settings.ReportPath);
        RequireSignals(settings);
        using (new FileStream(settings.ReportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        var report = Empty(settings);
        ProofFiles.Write(settings.ReportPath, report);
        return await ExecuteAsync(settings, report, cancellationToken);
    }

    private static void RequireSignals(InstalledSmokeSettings settings)
    {
        if (settings.ReadyFile is { } ready && settings.ContinueFile is { } continuation)
        {
            TemporaryPaths.RequireNewFile(ready);
            TemporaryPaths.RequireNewFile(continuation);
            if (InstalledSource.SamePath(ready, continuation) ||
                InstalledSource.SamePath(ready, settings.ReportPath) ||
                InstalledSource.SamePath(continuation, settings.ReportPath))
                throw new ArgumentException("Report, ready and continue paths must be distinct.");
        }
    }

    private static JsonObject Empty(InstalledSmokeSettings settings) => new()
    {
        ["schema"] = 1, ["passed"] = false, ["cleaned"] = false,
        ["installedTrayLaunched"] = false, ["trayExecutablePath"] = settings.TrayPath,
        ["failure"] = "Installed diagnostic did not complete."
    };

    private static async Task<int> ExecuteAsync(
        InstalledSmokeSettings settings, JsonObject report, CancellationToken cancellationToken)
    {
        string? root = null;
        var safeToClean = true;
        try
        {
            var (request, owner) = Prepare(settings, report);
            root = CreateWorkspace(request);
            safeToClean = false;
            report = await LaunchAsync(settings, root, request, cancellationToken);
            safeToClean = SafeToClean(report);
            VerifyOwner(settings, report, owner);
        }
        catch (Exception error)
        {
            report["passed"] = false;
            report["failure"] = ProofFiles.Failure(error);
        }
        return await FinishAsync(settings, root, report, safeToClean, cancellationToken);
    }

    private static (InstalledSmokeRequest Request, OwnerFixtureReceipt? Owner) Prepare(
        InstalledSmokeSettings settings, JsonObject report)
    {
        var owner = OwnerFixture.RequireAdmission(settings.OwnerFixtureReceipt);
        OwnerFixture.Attach(report, owner, unchanged: false);
        _ = InstalledSource.RequireRegistered(settings.TrayPath);
        InstalledSource.RefuseRunning(settings.TrayPath,
            new WindowsProcessCatalog(), new WindowsProcessIdentitySource());
        return (Request(settings), owner);
    }

    private static void VerifyOwner(
        InstalledSmokeSettings settings, JsonObject report, OwnerFixtureReceipt? owner)
    {
        OwnerFixture.Attach(report, owner, unchanged: false);
        OwnerFixture.RequireExpected(settings.OwnerFixtureReceipt, owner);
        OwnerFixture.Attach(report, owner, unchanged: true);
    }

    private static InstalledSmokeRequest Request(InstalledSmokeSettings settings) => new(
        settings.TrayPath, settings.Port, settings.TimeoutSeconds,
        InstalledSource.Capture(settings.TrayPath),
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
        settings.ReadyFile, settings.ContinueFile);

    private static string CreateWorkspace(InstalledSmokeRequest request)
    {
        var root = Path.Combine(TemporaryPaths.RunnerTemporary,
            TemporaryPaths.WorkspacePrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        TemporaryPaths.Require(root, directory: true);
        Directory.CreateDirectory(Path.Combine(root, "profile"));
        ProofFiles.Write(Path.Combine(root, "request.json"), request);
        return root;
    }

    private static ProcessStartInfo StartInfo(string root, InstalledSmokeRequest request)
    {
        var start = new ProcessStartInfo(request.TrayPath)
        {
            UseShellExecute = false, RedirectStandardInput = true,
            WorkingDirectory = Path.GetDirectoryName(request.TrayPath) ?? throw new InvalidOperationException()
        };
        start.ArgumentList.Add("--installed-smoke");
        start.ArgumentList.Add(root);
        Isolate(start, root);
        return start;
    }

    private static void Isolate(ProcessStartInfo start, string root)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        start.Environment.Clear();
        start.Environment["SystemRoot"] = windows;
        start.Environment["WINDIR"] = windows;
        start.Environment["PATH"] = Path.Combine(windows, "System32");
        start.Environment["GITHUB_ACTIONS"] = "true";
        start.Environment["RUNNER_ENVIRONMENT"] = "github-hosted";
        start.Environment["GITHUB_SERVER_URL"] = "https://github.com";
        start.Environment["GITHUB_REPOSITORY"] = "johnazariah/hermes";
        start.Environment["RUNNER_TEMP"] = TemporaryPaths.RunnerTemporary;
        foreach (var key in new[] { "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP" })
            start.Environment[key] = Path.Combine(root, "profile");
    }

    private static async Task<JsonObject> LaunchAsync(
        InstalledSmokeSettings settings, string root, InstalledSmokeRequest request,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds + 30));
        using var process = new Process { StartInfo = StartInfo(root, request) };
        if (!process.Start())
            throw new InvalidOperationException("Installed Tray did not start.");
        var started = process.StartTime.ToUniversalTime().Ticks;
        await process.StandardInput.WriteAsync(request.Challenge.AsMemory(), deadline.Token);
        await process.StandardInput.FlushAsync(deadline.Token);
        process.StandardInput.Close();
        await process.WaitForExitAsync(deadline.Token);
        var report = ProofFiles.ReadObject(Path.Combine(root, "proof.json"));
        Validate(report, request, process.Id, started, process.ExitCode);
        return report;
    }

    private static void Validate(
        JsonObject report, InstalledSmokeRequest request, int processId, long started, int exitCode)
    {
        var files = report["installedFiles"] is { } node
            ? JsonSerializer.Deserialize<ImmutableArray<InstalledFileProof>>(node.ToJsonString(), ProofFiles.Json)
            : [];
        if (ProofFiles.Required<int>(report, "trayProcessId") != processId ||
            ProofFiles.Required<long>(report, "trayStartUtcTicks") != started ||
            !InstalledSource.SamePath(ProofFiles.Required<string>(report, "trayExecutablePath"), request.TrayPath) ||
            !ProofFiles.Required<bool>(report, "installedTrayLaunched") ||
            !files.SequenceEqual(request.Files) ||
            !InstalledSource.Capture(request.TrayPath).SequenceEqual(request.Files))
            throw new InvalidDataException("Installed process/source proof did not match the launched apphost.");
        var passed = exitCode == 0 && ProofFiles.Required<bool>(report, "passed") &&
            ProofFiles.Required<bool>(report, "installedSourceUnchanged") && ValidStop(report) &&
            (request.ReadyFile is null || ProofFiles.Required<bool>(report, "continued"));
        report["passed"] = passed;
        if (!passed && report["failure"] is null)
            report["failure"] = "Installed native proof or graceful same-instance cleanup failed.";
    }

    private static bool ValidStop(JsonObject report) =>
        report["stop"] is JsonObject stop &&
        ProofFiles.Required<bool>(stop, "graceful") &&
        !ProofFiles.Required<bool>(stop, "forced") &&
        ProofFiles.Required<bool>(stop, "exited") &&
        ProofFiles.Required<int>(stop, "exitCode") == 0;

    private static bool SafeToClean(JsonObject report) =>
        report["processId"] is null ||
        (report["stop"] is JsonObject stop && ProofFiles.Required<bool>(stop, "exited"));

    private static async Task<int> FinishAsync(
        InstalledSmokeSettings settings, string? root, JsonObject report,
        bool safeToClean, CancellationToken cancellationToken)
    {
        var nativeCleaned = report["cleaned"]?.GetValue<bool>() ?? false;
        var passed = report["passed"]?.GetValue<bool>() ?? false;
        report["cleaned"] = false;
        ProofFiles.Write(settings.ReportPath, report);
        var cleaned = root is null ||
            (safeToClean && await RemoveAsync(root, cancellationToken));
        report["cleaned"] = cleaned && (root is null || nativeCleaned);
        report["passed"] = passed && ProofFiles.Required<bool>(report, "cleaned");
        if (!ProofFiles.Required<bool>(report, "cleaned") && report["failure"] is null)
            report["failure"] = "Diagnostic workspace retained because safe cleanup was not established.";
        ProofFiles.Write(settings.ReportPath, report);
        Console.WriteLine(ProofFiles.Required<bool>(report, "passed") ? "Installed native smoke passed." : "Installed native smoke failed.");
        return ProofFiles.Required<bool>(report, "passed") ? 0 : 1;
    }

    private static async Task<bool> RemoveAsync(string root, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { Directory.Delete(root, recursive: true); return true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            }
        }
        return false;
    }
}
