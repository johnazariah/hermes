using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Hermes.App;

/// <summary>
/// Checks GitHub Releases for newer versions of Hermes.
/// </summary>
public static class UpdateChecker
{
    private const string ReleasesUrl = "https://api.github.com/repos/johnazariah/hermes/releases/latest";

    public record UpdateInfo(string CurrentVersion, string LatestVersion, string DownloadUrl, bool IsUpdateAvailable);

    private sealed record GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = "";

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; init; } = "";

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }
    }

    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Hermes-UpdateChecker/1.0");

            var release = await client.GetFromJsonAsync<GitHubRelease>(ReleasesUrl);
            if (release is null) return null;

            var currentVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
            var latestVersion = release.TagName.TrimStart('v', 'V');

            var isNewer = IsNewerVersion(currentVersion, latestVersion);

            return new UpdateInfo(
                CurrentVersion: currentVersion,
                LatestVersion: latestVersion,
                DownloadUrl: release.HtmlUrl,
                IsUpdateAvailable: isNewer && !release.Prerelease
            );
        }
        catch
        {
            return null;
        }
    }

    private static bool IsNewerVersion(string current, string latest)
    {
        if (Version.TryParse(current, out var currentVer) && Version.TryParse(latest, out var latestVer))
            return latestVer > currentVer;
        return false;
    }
}
