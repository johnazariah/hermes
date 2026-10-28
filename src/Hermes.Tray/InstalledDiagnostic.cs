using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hermes.Tray.Smoke;

internal static class InstalledDiagnostic
{
    public static int Run(string workspace, NativeStartupContext startup)
    {
        try
        {
            _ = startup.RequireService(Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "..", "service", ServiceWire.Executable)));
            CiAdmission.Read(cleanOwner: true).Require();
            var request = Accept(workspace);
            var registration = RequireInstalledEntry(request);
            DesktopPrerequisite.Require();
            return Execute(workspace, request, registration, startup);
        }
        catch
        {
            // Never display a modal dialog or expose request/exception content.
            // The external harness records missing or rejected diagnostic evidence.
            return 1;
        }
    }

    private static InstalledSmokeRequest Accept(string workspace)
    {
        TemporaryPaths.Require(workspace, directory: true);
        var name = Path.GetFileName(workspace);
        if (!name.StartsWith(TemporaryPaths.WorkspacePrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Diagnostic workspace marker is invalid.");
        _ = Guid.ParseExact(name[TemporaryPaths.WorkspacePrefix.Length..], "N");
        if (!InstalledSource.SamePath(Path.GetDirectoryName(workspace) ?? "", TemporaryPaths.RunnerTemporary))
            throw new InvalidOperationException("Diagnostic workspace must be a direct temporary child.");
        var accepted = Path.Combine(workspace, "accepted.json");
        File.Move(Path.Combine(workspace, "request.json"), accepted, overwrite: false);
        var request = JsonSerializer.Deserialize<InstalledSmokeRequest>(ProofFiles.Read(accepted, 8192), ProofFiles.Json)
            ?? throw new InvalidDataException("Diagnostic request is missing.");
        RequireChallenge(request.Challenge);
        RequireSignals(request);
        return request;
    }

    private static void RequireChallenge(string expected)
    {
        if (expected.Length != 64)
            throw new InvalidDataException("Diagnostic challenge size is invalid.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var supplied = new char[65];
        var count = Console.In.ReadBlockAsync(supplied.AsMemory(), deadline.Token)
            .AsTask().GetAwaiter().GetResult();
        if (count != 64 || !CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(supplied.AsSpan(0, count).ToString())))
            throw new InvalidOperationException("Diagnostic challenge was not admitted.");
    }

    private static void RequireSignals(InstalledSmokeRequest request)
    {
        if (request.Port is < 1 or > 65535 || request.TimeoutSeconds is < 10 or > 300 ||
            (request.ReadyFile is null) != (request.ContinueFile is null))
            throw new InvalidDataException("Diagnostic request limits are invalid.");
        if (request.ReadyFile is { } ready && request.ContinueFile is { } continuation)
        {
            TemporaryPaths.RequireNewFile(ready);
            TemporaryPaths.RequireNewFile(continuation);
        }
    }

    private static InstalledRegistration RequireInstalledEntry(InstalledSmokeRequest request)
    {
        var registration = InstalledSource.RequireRegistered(request.TrayPath);
        var assembly = typeof(InstalledDiagnostic).Assembly;
        var expectedAssembly = Path.Combine(InstalledSource.Root(request.TrayPath), "tray", "Hermes.Tray.dll");
        if (Environment.ProcessPath is not { } image ||
            !InstalledSource.SamePath(image, request.TrayPath) ||
            Assembly.GetEntryAssembly() != assembly ||
            !InstalledSource.SamePath(assembly.Location, expectedAssembly) ||
            !InstalledSource.Capture(request.TrayPath).SequenceEqual(request.Files))
            throw new InvalidOperationException("Installed entry/source identity differs from the request.");
        return registration;
    }

    private static int Execute(
        string root, InstalledSmokeRequest request, InstalledRegistration registration,
        NativeStartupContext startup)
    {
        string? workspace = null;
        var report = SmokeReport.Empty;
        var joined = false;
        try
        {
            workspace = SyntheticWorkspace.Create();
            report = RunDesktop(root, workspace, request, registration, startup);
            joined = true;
            report = report with
            {
                InstalledSourceUnchanged = InstalledSource.Capture(request.TrayPath).SequenceEqual(request.Files)
            };
        }
        catch (Exception error) { report = report with { Passed = false, Failure = ProofFiles.Failure(error) }; }
        return Finish(root, workspace, request, registration, report, joined);
    }

    private static SmokeReport RunDesktop(
        string root, string workspace, InstalledSmokeRequest request, InstalledRegistration registration,
        NativeStartupContext startup)
    {
        var service = Path.Combine(InstalledSource.Root(request.TrayPath), "service", ServiceWire.Executable);
        var options = TrayOptions.Parse(
            ["--service-path", service, "--port", request.Port.ToString(CultureInfo.InvariantCulture),
             "--config-dir", workspace], _ => null, AppContext.BaseDirectory)
            .ForStartup(startup) with { Synthetic = true };
        using var lease = InstanceLease.Acquire(options);
        ApplicationConfiguration.Initialize();
        using var context = new SmokeContext(options, TimeSpan.FromSeconds(request.TimeoutSeconds),
            request.ReadyFile is null ? null : (report, token) => HoldAsync(root, request, registration, report, token),
            report => Write(root, request, registration, report));
        Application.Run(context);
        if (!context.Completion.IsCompletedSuccessfully)
            throw new InvalidOperationException("Installed diagnostic worker was not joined.");
        return context.Report;
    }

    private static async Task HoldAsync(
        string root, InstalledSmokeRequest request, InstalledRegistration registration,
        SmokeReport report, CancellationToken cancellationToken)
    {
        if (request.ReadyFile is not { } ready || request.ContinueFile is not { } continuation ||
            report.ProcessId is not > 0 || report.ServiceStartUtcTicks is not > 0)
            throw new InvalidOperationException("Installed hold identity is unavailable.");
        TemporaryPaths.RequireNewFile(continuation);
        Write(root, request, registration, report with { ReadyReached = true });
        ProofFiles.Write(ready, new
        {
            Schema = 1, TrayProcessId = Environment.ProcessId,
            ServiceProcessId = report.ProcessId, report.ServiceStartUtcTicks,
            Restart = 0
        });
        while (!ProofFiles.Exists(continuation))
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static int Finish(
        string root, string? workspace, InstalledSmokeRequest request,
        InstalledRegistration registration, SmokeReport report, bool joined)
    {
        report = report with { Passed = report.Passed && report.InstalledSourceUnchanged };
        Write(root, request, registration, report);
        var safe = joined && (report.ProcessId is null || report.Stop is { Exited: true });
        var cleaned = workspace is null || (safe && SyntheticWorkspace.Remove(workspace));
        report = report with
        {
            Passed = report.Passed && cleaned, Cleaned = cleaned,
            RetainedSyntheticDirectory = null,
            Failure = cleaned ? report.Failure : report.Failure ?? "Synthetic cleanup was not safe or failed."
        };
        Write(root, request, registration, report);
        return report.Passed ? 0 : 1;
    }

    private static void Write(
        string root, InstalledSmokeRequest request, InstalledRegistration registration, SmokeReport report)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        ProofFiles.Write(Path.Combine(root, "proof.json"), report with
        {
            InstalledTrayLaunched = true, TrayExecutablePath = request.TrayPath,
            TrayProcessId = process.Id, TrayStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
            InstalledProductCode = registration.ProductCode, InstalledVersion = registration.Version,
            TrayModuleVersionId = typeof(InstalledDiagnostic).Assembly.ManifestModule.ModuleVersionId,
            InstalledFiles = request.Files,
            ReadyReached = report.ReadyReached ||
                (request.ReadyFile is { } ready && ProofFiles.Exists(ready))
        });
    }
}
