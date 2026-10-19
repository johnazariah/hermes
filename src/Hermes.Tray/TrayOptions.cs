using System.Collections.Immutable;
using System.Globalization;

namespace Hermes.Tray;

internal static class ServiceWire
{
    public const string ConfigEnvironment = "HERMES_CONFIG_DIR";
    public const string PortEnvironment = "HERMES_PORT";
    public const string Executable = "Hermes.Service.exe";
    public const string ControlArgument = "--control-stdin";
    public const string ShutdownCommand = "shutdown";
}

internal sealed record TrayOptions(
    string ConfigDirectory,
    int Port,
    string ServicePath,
    bool Synthetic = false)
{
    public Uri BaseAddress => new($"http://127.0.0.1:{Port}/");
    public string LogDirectory => Path.Combine(ConfigDirectory, "logs");
    public string WebViewDirectory => Path.Combine(ConfigDirectory, "webview2");

    public static TrayOptions Parse(
        string[] arguments,
        Func<string, string?> environment,
        string baseDirectory)
    {
        var flags = arguments.Chunk(2).Select(ParsePair)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var config = flags.GetValueOrDefault("--config-dir")
            ?? environment(ServiceWire.ConfigEnvironment)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hermes");
        var port = flags.GetValueOrDefault("--port") ?? environment(ServiceWire.PortEnvironment) ?? "21741";
        var service = ResolveService(flags.GetValueOrDefault("--service-path"), baseDirectory);
        return new(Absolute(config, "Configuration directory"), ParsePort(port), service);
    }

    private static KeyValuePair<string, string> ParsePair(string[] pair) => pair switch
    {
        [("--config-dir" or "--port" or "--service-path") and var key, var value]
            when !string.IsNullOrWhiteSpace(value) => new(key, value),
        _ => throw new ArgumentException(
            "Expected pairs: --service-path ABSOLUTE_EXE --port NUMBER --config-dir ABSOLUTE_DIRECTORY.")
    };

    internal static int ParsePort(string text)
    {
        try
        {
            return int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture) switch
            {
                > 0 and <= 65535 and var port => port,
                _ => throw new ArgumentException("Port must be between 1 and 65535.")
            };
        }
        catch (Exception error) when (error is FormatException or OverflowException)
        {
            throw new ArgumentException("Port must be between 1 and 65535.", error);
        }
    }

    internal static string Absolute(string path, string description) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
            : throw new ArgumentException($"{description} must be an absolute path.");

    private static string ResolveService(string? explicitPath, string baseDirectory)
    {
        if (explicitPath is { } supplied)
            return RequireService(Absolute(supplied, "Service executable"));
        var candidates = PublishedCandidates(baseDirectory).Concat(SourceCandidates(baseDirectory)).ToImmutableArray();
        return candidates.FirstOrDefault(File.Exists) is { } found
            ? RequireService(found)
            : throw new ArgumentException(
                $"Service apphost not found. Checked: {string.Join("; ", candidates)}. " +
                "Publish the sibling service directory, build the matching source configuration, or use --service-path.");
    }

    private static IEnumerable<string> PublishedCandidates(string baseDirectory) =>
        new[]
        {
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "service", ServiceWire.Executable)),
            Path.GetFullPath(Path.Combine(baseDirectory, "service", ServiceWire.Executable))
        };

    private static IEnumerable<string> SourceCandidates(string baseDirectory)
    {
        var build = Ancestors(new DirectoryInfo(baseDirectory))
            .FirstOrDefault(directory => directory.Parent?.Name == "bin"
                && directory.Name is "Debug" or "Release");
        if (build?.Parent?.Parent is not { } tray ||
            !File.Exists(Path.Combine(tray.FullName, "Hermes.Tray.csproj")) ||
            tray.Parent is not { } source)
            return [];
        return [Path.Combine(source.FullName, "Hermes.Service", "bin", build.Name, "net9.0", ServiceWire.Executable)];
    }

    private static IEnumerable<DirectoryInfo> Ancestors(DirectoryInfo directory)
    {
        yield return directory;
        if (directory.Parent is { } parent)
            foreach (var ancestor in Ancestors(parent))
                yield return ancestor;
    }

    private static string RequireService(string path) =>
        File.Exists(path) && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
            ? path
            : throw new ArgumentException($"Service executable does not exist or is not an apphost executable: {path}");
}
