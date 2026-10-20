using System.Runtime.Versioning;
using Hermes.Servicing;

namespace Hermes.Installer.Control;

internal interface ILocalAppDataKnownFolder
{
    string? Read();
}

internal sealed class FixedInstallRoot
{
    private FixedInstallRoot(string appDirectory) => AppDirectory = appDirectory;

    internal string AppDirectory { get; }

    [SupportedOSPlatform("windows")]
    internal static FixedInstallRoot ForCurrentUser() =>
        FromKnownFolder(new WindowsLocalAppData());

    // Capability seam for bounded tests. Production uses ForCurrentUser.
    internal static FixedInstallRoot FromKnownFolder(ILocalAppDataKnownFolder knownFolder)
    {
        var local = knownFolder.Read();
        if (string.IsNullOrWhiteSpace(local) || !Path.IsPathFullyQualified(local) ||
            local.Any(character => character == '"' || char.IsControl(character)))
            throw new InvalidOperationException("The OS local application-data folder is unavailable.");
        return new(Path.Combine(Path.TrimEndingDirectorySeparator(local), "Hermes", "App"));
    }

    internal bool IsAppDirectory(string? candidate)
    {
        if (candidate is null)
            return false;
        var expected = WindowsSpelling(AppDirectory);
        var supplied = WindowsSpelling(candidate);
        return string.Equals(supplied, expected, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(supplied, expected + "\\", StringComparison.OrdinalIgnoreCase);
    }

    internal string Require(string? candidate) =>
        IsAppDirectory(candidate)
            ? AppDirectory
            : throw new InvalidDataException(
                "Hermes supports only the current user's fixed application directory.");

    internal ControlPlan Bind(ControlPlan plan) =>
        plan with { AppDirectory = Require(plan.AppDirectory) };

    internal GuardRequest Bind(GuardRequest request) =>
        request with { AppDirectory = Require(request.AppDirectory) };

    // Do not normalize untrusted dot segments, device paths or Win32 aliases.
    private static string WindowsSpelling(string path) => path.Replace('/', '\\');

    [SupportedOSPlatform("windows")]
    private sealed class WindowsLocalAppData : ILocalAppDataKnownFolder
    {
        public string Read()
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Windows known-folder admission is required.");
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
    }
}
