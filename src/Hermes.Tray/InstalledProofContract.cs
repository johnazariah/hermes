using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Hermes.Tray.Smoke;

internal sealed record InstalledSmokeSettings(
    string TrayPath, int Port, string ReportPath, int TimeoutSeconds,
    string? ReadyFile, string? ContinueFile, string? OwnerFixtureReceipt = null)
{
    public static InstalledSmokeSettings Parse(string[] arguments)
    {
        var flags = arguments.Chunk(2).Select(Pair)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var ready = Optional(flags, "--ready-file");
        var continuation = Optional(flags, "--continue-file");
        if ((ready is null) != (continuation is null))
            throw new ArgumentException("Ready and continue files must be supplied together.");
        var seconds = Number(flags.GetValueOrDefault("--timeout-seconds") ?? "150");
        if (seconds is < 10 or > 300)
            throw new ArgumentException("Timeout must be between 10 and 300 seconds.");
        var port = Number(Required(flags, "--port"));
        if (port is < 1 or > 65535)
            throw new ArgumentException("Port must be between 1 and 65535.");
        return new(Absolute(Required(flags, "--installed-tray-path")), port,
            Absolute(Required(flags, "--report")), seconds, ready, continuation,
            Optional(flags, "--owner-fixture-receipt"));
    }

    private static KeyValuePair<string, string> Pair(string[] pair) => pair switch
    {
        [("--installed-tray-path" or "--port" or "--report" or "--timeout-seconds"
            or "--ready-file" or "--continue-file" or "--owner-fixture-receipt") and var key, var value]
            when !string.IsNullOrWhiteSpace(value) => new(key, value),
        _ => throw new ArgumentException("Invalid installed-smoke argument pair.")
    };

    private static int Number(string value) =>
        int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);

    private static string Required(Dictionary<string, string> flags, string key) =>
        flags.GetValueOrDefault(key) ?? throw new ArgumentException("Missing installed-smoke argument.");

    private static string? Optional(Dictionary<string, string> flags, string key) =>
        flags.GetValueOrDefault(key) is { } value ? Absolute(value) : null;

    internal static string Absolute(string path) =>
        Path.IsPathFullyQualified(path)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
            : throw new ArgumentException("An absolute path is required.");
}

internal sealed record CiAdmission(
    bool Windows, bool Interactive, bool Actions, bool Hosted,
    bool PublicRepository, bool CleanOwner)
{
    public void Require()
    {
        if (this is not { Windows: true, Interactive: true, Actions: true,
            Hosted: true, PublicRepository: true, CleanOwner: true })
            throw new InvalidOperationException("Disposable public GitHub Windows diagnostic admission failed.");
    }

    public static CiAdmission Read(bool cleanOwner) => new(
        OperatingSystem.IsWindows(), Environment.UserInteractive,
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true",
        Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted",
        Environment.GetEnvironmentVariable("GITHUB_SERVER_URL") == "https://github.com" &&
            Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") == "johnazariah/hermes",
        cleanOwner);
}

internal sealed record InstalledFileProof(string RelativePath, string Sha256);
internal sealed record ProcessStamp(int ProcessId, long StartUtcTicks, string ImagePath);
internal sealed record InstalledRegistration(string Directory, string ProductCode, string Version);
internal sealed record InstalledSmokeRequest(
    string TrayPath, int Port, int TimeoutSeconds,
    ImmutableArray<InstalledFileProof> Files, string Challenge,
    string? ReadyFile, string? ContinueFile);

internal interface IProcessIdentitySource
{
    ProcessStamp Read(int processId);
}

internal interface IProcessCatalog
{
    IReadOnlyList<int> Candidates(string executableName);
}

internal sealed class WindowsProcessIdentitySource : IProcessIdentitySource
{
    public ProcessStamp Read(int processId)
    {
        using var process = Process.GetProcessById(processId);
        var image = process.MainModule?.FileName
            ?? throw new InvalidOperationException("Process image identity is unavailable.");
        return new(process.Id, process.StartTime.ToUniversalTime().Ticks,
            InstalledSmokeSettings.Absolute(image));
    }
}

internal sealed class WindowsProcessCatalog : IProcessCatalog
{
    public IReadOnlyList<int> Candidates(string executableName) =>
        Process.GetProcessesByName(executableName).Select(ReadAndDispose).ToArray();

    private static int ReadAndDispose(Process process)
    {
        using (process)
            return process.Id;
    }
}

internal static class InstalledSource
{
    private static readonly ImmutableArray<string> Files =
    [
        "tray/Hermes.Tray.exe", "tray/Hermes.Tray.dll",
        "service/Hermes.Service.exe", "service/Hermes.Service.dll"
    ];

    public static string Root(string trayPath)
    {
        var full = InstalledSmokeSettings.Absolute(trayPath);
        var tray = Path.GetDirectoryName(full)
            ?? throw new ArgumentException("Tray directory is missing.");
        if (!string.Equals(Path.GetFileName(full), "Hermes.Tray.exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(tray), "tray", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The installed tray/Hermes.Tray.exe layout is required.");
        return Path.GetDirectoryName(tray)
            ?? throw new ArgumentException("Installed application directory is missing.");
    }

    public static bool SamePath(string left, string right) =>
        string.Equals(InstalledSmokeSettings.Absolute(left),
            InstalledSmokeSettings.Absolute(right), StringComparison.OrdinalIgnoreCase);

    public static ImmutableArray<InstalledFileProof> Capture(string trayPath)
    {
        var root = Root(trayPath);
        return Files.Select(relative => Fingerprint(root, relative)).ToImmutableArray();
    }

    private static InstalledFileProof Fingerprint(string root, string relative)
    {
        using var stream = File.OpenRead(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return new(relative, Convert.ToHexString(SHA256.HashData(stream)));
    }

    public static InstalledRegistration RequireRegistered(string trayPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Hermes\Installer");
        var directory = key?.GetValue("InstallDirectory") is string value
            ? value : throw new InvalidOperationException("Per-user MSI registration is unavailable.");
        var product = key?.GetValue("ProductCode") is string code
            ? Guid.Parse(code).ToString("B") : throw new InvalidOperationException("Product identity is unavailable.");
        var version = key?.GetValue("Version") is string text && text.Length is > 0 and <= 64
            ? text : throw new InvalidOperationException("Installed version is unavailable.");
        if (!SamePath(directory, Root(trayPath)))
            throw new InvalidOperationException("Requested Tray is not the registered per-user installation.");
        return new(InstalledSmokeSettings.Absolute(directory), product, version);
    }

    public static void RefuseRunning(
        string trayPath, IProcessCatalog catalog, IProcessIdentitySource identities)
    {
        var root = Root(trayPath);
        var targets = Files.Where(relative => relative.EndsWith(".exe", StringComparison.Ordinal))
            .Select(relative => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (targets.Any(target => catalog.Candidates(Path.GetFileNameWithoutExtension(target))
            .Any(pid => SamePath(identities.Read(pid).ImagePath, target))))
            throw new InvalidOperationException("An installed Hermes runtime is already running.");
    }
}

internal static class ProofFiles
{
    public const int MaximumBytes = 262144;
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    public static string Failure(Exception error) =>
        $"{error.GetType().FullName}:0x{error.HResult:X8}";

    public static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public static byte[] Read(string path, int maximum = MaximumBytes)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is < 1 || stream.Length > maximum)
            throw new InvalidDataException("Diagnostic JSON size is invalid.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    public static void Write<T>(string path, T report)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, Json);
        if (bytes.Length > MaximumBytes)
            throw new InvalidDataException("Diagnostic JSON exceeded its bound.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static JsonObject ReadObject(string path) =>
        JsonNode.Parse(Read(path)) is JsonObject value
            ? value : throw new InvalidDataException("A diagnostic JSON object is required.");

    public static T Required<T>(JsonObject value, string name) =>
        value[name] is { } node
            ? node.GetValue<T>() : throw new InvalidDataException("Required diagnostic evidence is missing.");
}

internal static class TemporaryPaths
{
    public const string WorkspacePrefix = "hermes-installed-proof-";

    public static string RunnerTemporary =>
        InstalledSmokeSettings.Absolute(Environment.GetEnvironmentVariable("RUNNER_TEMP")
            ?? throw new InvalidOperationException("Runner temporary directory is unavailable."));

    public static void Require(string path, bool directory = false)
    {
        var full = InstalledSmokeSettings.Absolute(path);
        var runner = RunnerTemporary + Path.DirectorySeparatorChar;
        if (!full.StartsWith(runner, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Diagnostic paths must be below RUNNER_TEMP.");
        var parent = directory ? full : Path.GetDirectoryName(full)
            ?? throw new InvalidOperationException("Diagnostic parent is missing.");
        RequirePlainAncestors(new DirectoryInfo(parent));
    }

    internal static void RequirePlainDirectory(string path) =>
        RequirePlainAncestors(new DirectoryInfo(path));

    private static void RequirePlainAncestors(DirectoryInfo directory)
    {
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Diagnostic directory is absent or redirected.");
        if (directory.Parent is { } parent)
            RequirePlainAncestors(parent);
    }

    public static void RequireNewFile(string path)
    {
        Require(path);
        if (ProofFiles.Exists(path))
            throw new InvalidOperationException("Diagnostic output or continuation already exists.");
    }

    public static bool OwnerStateAbsent()
    {
        var owner = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hermes");
        return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HERMES_CONFIG_DIR")) &&
            !ProofFiles.Exists(owner);
    }
}
